using System.Text;
using Microsoft.Win32;

namespace ForgeSentinel;

// Legacy-Windows console guard.
//
// Forcing the console to UTF-8 (code page 65001) is fine on Windows 10 1909+ but
// is actively broken on older ConHost: it freezes stdin for non-ASCII input and
// makes bundled tools (find.exe, more) fail with "Not enough memory". So we only
// flip to UTF-8 when the OS build is recent enough; on anything older we leave the
// console at its native code page and rely on UTF-16 (WCHAR) at the API boundary.
static class PlatformConsole
{
    // Windows 10 build 1909 == 18363. Below this, do not touch the code page.
    const int MinBuildForUtf8 = 18363;

    public static void Init()
    {
        if (CurrentBuild() >= MinBuildForUtf8)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { /* redirected stdout / no console — nothing to set */ }
        }
        // else: legacy ConHost. Leave the code page as-is on purpose.
    }

    // Read the real build number from the registry. Environment.OSVersion lies on
    // unmanifested apps (caps at 6.2 / Windows 8), so it can't gate this.
    static int CurrentBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var raw = key?.GetValue("CurrentBuildNumber") as string;
            return int.TryParse(raw, out var b) ? b : 0;
        }
        catch { return 0; }
    }
}
