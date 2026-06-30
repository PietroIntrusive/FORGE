using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForgeSentinel;

// Anti-tamper for hardened (production) builds. In a competitive local scenario
// an advanced user may attach a debugger or memory editor (dnSpy, x64dbg) to the
// binary to fake a score or reverse the scoring rules.
//
// Design rule (the important one): detection NEVER reacts at the detection site.
// No exit, no message box, no exception. A visible reaction tells the attacker
// exactly which instruction to patch out. Instead we silently flip an internal
// integrity flag; a later, unrelated part of the engine reads Trust.Healthy and
// degrades subtly, so the failure surfaces far from the check and looks like a
// logic bug rather than a guard.
//
// Gated behind FORGE_HARDENED. In normal Debug/dev builds the whole subsystem
// compiles to nothing — Trust.Healthy is always true — so we never sabotage our
// own debugging. Turn it on for release:  -p:Hardened=true

// Internal trust state. Read by engine code that wants to behave differently when
// the process looks tampered. Always healthy unless a hardened build trips it.
static class Trust
{
    // volatile: written by the guard thread, read by engine threads.
    private static volatile bool _healthy = true;

    public static bool Healthy => _healthy;

    // One-way latch. Once tainted, never recovers for the life of the process.
    // Marked internal so only our own code (not reflection-friendly publics) flips it.
    internal static void Taint() => _healthy = false;
}

static class IntegrityGuard
{
    // Starts the background watcher. No-op unless FORGE_HARDENED is defined, and
    // the [Conditional] attribute means callers don't even need an #if around it.
    [Conditional("FORGE_HARDENED")]
    public static void Arm()
    {
        var t = new Thread(Watch)
        {
            IsBackground = true,
            Name = "fs-housekeeping", // innocuous name; doesn't advertise its job
        };
        t.Start();
    }

    private static void Watch()
    {
        // Stagger the first check and the interval so the trip point isn't at a
        // fixed, easily-breakpointed moment after launch.
        var rng = new Random();
        Thread.Sleep(2000 + rng.Next(1500));

        while (true)
        {
            if (DebuggerPresent())
                Trust.Taint();

            Thread.Sleep(4000 + rng.Next(3000));
        }
    }

    // Three independent signals. Any one is enough. Spread across managed + Win32
    // + NT so patching a single API doesn't fully blind the guard.
    private static bool DebuggerPresent()
    {
        if (Debugger.IsAttached) return true;
        if (IsDebuggerPresent()) return true;

        try
        {
            if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out var remote) && remote)
                return true;
        }
        catch { /* handle race on shutdown — ignore */ }

        // NtQueryInformationProcess(ProcessDebugPort): non-zero port => debugger.
        try
        {
            const int ProcessDebugPort = 7;
            if (NtQueryInformationProcess(
                    Process.GetCurrentProcess().Handle, ProcessDebugPort,
                    out var port, IntPtr.Size, out _) == 0 && port != IntPtr.Zero)
                return true;
        }
        catch { /* ntdll unavailable / blocked — fall through */ }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        out IntPtr processInformation, int processInformationLength, out int returnLength);
}
