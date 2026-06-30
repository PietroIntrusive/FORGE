using System.Diagnostics;

namespace ForgeSentinel;

// Creates a Windows System Restore point before we mutate services / power state.
// This is the user-facing safety net the Gemini spec calls for: even though every
// change is individually logged and reversible via `undo`, a real restore point
// lets the user roll the whole machine back from outside the app if something goes
// sideways. Best-effort: if it fails (System Restore disabled, not admin, non-
// system drive), we report it but never hard-block the operation — the per-action
// FixLog undo is still the primary, always-available revert path.
static class RestorePointEngine
{
    // SQL/WMI restore-point type constants.
    const int BEGIN_SYSTEM_CHANGE = 100;
    const int MODIFY_SETTINGS     = 12;

    public static RestorePointResult Create(string description)
    {
        // System Restore only works from an elevated context.
        if (!Elevation.IsAdmin())
            return new RestorePointResult(false, "sem privilégio de administrador");

        try
        {
            // Drive Checkpoint-Computer through powershell with a fixed, non-
            // interpolated argument vector. `description` is the only caller input
            // and it never reaches a shell string — it's passed as a discrete
            // ArgumentList entry, and we sanitize it to a safe charset anyway.
            var safe = Sanitize(description);

            var psi = new ProcessStartInfo("powershell.exe")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                $"Checkpoint-Computer -Description '{safe}' -RestorePointType MODIFY_SETTINGS");

            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);

            if (p.ExitCode == 0)
                return new RestorePointResult(true, "ponto de restauração criado");

            // The most common non-fatal cause: Windows throttles restore points to
            // one per ~24h by default (SystemRestorePointCreationFrequency).
            var reason = stderr.Contains("frequency", StringComparison.OrdinalIgnoreCase)
                ? "já existe um ponto recente (limite de frequência do Windows)"
                : "System Restore indisponível ou desativado";
            return new RestorePointResult(false, reason);
        }
        catch (Exception ex)
        {
            return new RestorePointResult(false, ex.Message);
        }
    }

    // Restore-point descriptions are free text but we keep them to a boring,
    // quote-free charset so nothing can break out of the -Description argument.
    static string Sanitize(string s)
    {
        var clean = new string(s.Where(c =>
            char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or ':').ToArray());
        clean = clean.Trim();
        if (clean.Length == 0) clean = "Forge Sentinel";
        return clean.Length > 64 ? clean[..64] : clean;
    }
}

record RestorePointResult(bool Created, string Message);
