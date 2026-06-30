using System.Diagnostics;

namespace ForgeSentinel;

// Live GPU telemetry. Reads real numbers from the NVIDIA driver via nvidia-smi instead
// of the dashboard's old hard-coded placeholders. nvidia-smi ships WITH the driver, so
// there's no extra dependency and nothing to install.
//
// SECURITY: this is a READ, never a mutation. The executable path is one of a fixed set
// of known locations (never taken from the request) and the arguments are a hard-coded
// vector passed via ArgumentList — no shell string, no caller input ever reaches the
// process. Same out-of-process idiom the daemon already uses for powercfg / CIM.
static class GpuReader
{
    // A single GPU's live readings. Every metric is nullable: a field is null when the
    // driver reports "[N/A]" for it (e.g. fan speed / power on some boards), so the UI
    // can show "—" rather than invent a value.
    public sealed record GpuInfo(
        bool Available,
        string Name,
        int? TempC,
        int? CoreMhz,
        int? MemMhz,
        int? UtilPct,
        int? MemUsedMb,
        int? MemTotalMb,
        int? PowerW,
        int? FanPct);

    static readonly GpuInfo None = new(false, "", null, null, null, null, null, null, null, null);

    // Standard install locations first (driver puts nvidia-smi in System32 on modern
    // Windows; older drivers used the NVSMI folder). Bare name (PATH) only as a last
    // resort — we prefer absolute paths so a hijacked PATH entry can't stand in for it.
    static readonly string[] Candidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        "nvidia-smi",
    };

    // The fixed query vector. Order here defines the CSV column order parsed below.
    static readonly string[] QueryArgs =
    {
        "--query-gpu=name,temperature.gpu,clocks.current.graphics,clocks.current.memory," +
        "utilization.gpu,memory.used,memory.total,power.draw,fan.speed",
        "--format=csv,noheader,nounits",
    };

    public static GpuInfo Read()
    {
        var exe = Array.Find(Candidates, c => c == "nvidia-smi" || File.Exists(c));
        if (exe is null) return None;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in QueryArgs) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return None;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return None; }
            if (p.ExitCode != 0) return None;

            // First non-empty line = the primary GPU. (Multi-GPU rigs report one line each;
            // the dashboard shows the primary, matching the single-card design.)
            var line = outp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is null) return None;

            var f = line.Split(',').Select(s => s.Trim()).ToArray();
            if (f.Length < 7) return None;

            return new GpuInfo(
                Available:  true,
                Name:       f[0],
                TempC:      ParseInt(f, 1),
                CoreMhz:    ParseInt(f, 2),
                MemMhz:     ParseInt(f, 3),
                UtilPct:    ParseInt(f, 4),
                MemUsedMb:  ParseInt(f, 5),
                MemTotalMb: ParseInt(f, 6),
                PowerW:     ParseRoundInt(f, 7),
                FanPct:     ParseInt(f, 8));
        }
        catch { return None; }
    }

    // Resolve nvidia-smi from the fixed candidate set (absolute paths preferred). Shared
    // with GpuTuner so the write path uses the same trusted executable resolution.
    internal static string? ResolveSmi() =>
        Array.Find(Candidates, c => c == "nvidia-smi" || File.Exists(c));

    // Run nvidia-smi with a hardcoded argument vector (never a shell string, never caller
    // input). Returns (exitCode, stdout). exit -1 means the process couldn't run.
    internal static (int exit, string outp) RunSmi(params string[] args)
    {
        var exe = ResolveSmi();
        if (exe is null) return (-1, "");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return (-1, ""); }
            return (p.ExitCode, outp);
        }
        catch { return (-1, ""); }
    }

    // nvidia-smi prints "[N/A]" (or "[Not Supported]") for metrics a board doesn't expose.
    // Anything that isn't a plain number becomes null.
    static int? ParseInt(string[] f, int i) =>
        i < f.Length && int.TryParse(f[i], out var v) ? v : null;

    // power.draw comes as a decimal ("123.45"); round to whole watts.
    static int? ParseRoundInt(string[] f, int i) =>
        i < f.Length && double.TryParse(f[i], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v) : null;
}
