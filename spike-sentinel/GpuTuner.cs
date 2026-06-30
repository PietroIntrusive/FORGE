using System.Globalization;
using System.Threading;

namespace ForgeSentinel;

// GPU tuning — Fase 1: power limit only, via nvidia-smi (-pl). Ships with the driver, no
// extra dependency, no kernel driver. Offsets / undervolt (V/F curve) / fan curve need
// NVAPI and land in Fase 2.
//
// SAFETY: every apply is provisional. The daemon arms an auto-revert timer; if the UI
// doesn't confirm within the window, the previous limit is restored automatically. This
// is the "apply → test → keep or roll back" harness — even though a power limit can't
// crash the box (worst case is lower clocks), the pattern is built here so Fase 2's
// genuinely risky knobs inherit it.
//
// SECURITY: the watts value IS taken from the request (unavoidable for a slider), so it is
// CLAMPED server-side to [min,max] read from the driver. This is the documented departure
// from the strict id-only allowlist — the daemon still never runs a caller-supplied string,
// only nvidia-smi with a bounds-checked integer.
static class GpuTuner
{
    public sealed record PowerInfo(
        bool Available, double Current, double Default, double Min, double Max, bool Pending);

    static readonly object Lock = new();
    static Timer? _revertTimer;
    static double? _revertTo;       // watts to restore if not confirmed
    static bool _pending;

    // Window to confirm an applied limit before it auto-reverts.
    const int RevertSeconds = 15;

    public static PowerInfo Read()
    {
        var (exit, outp) = GpuReader.RunSmi(
            "--query-gpu=power.limit,power.default_limit,power.min_limit,power.max_limit",
            "--format=csv,noheader,nounits");
        if (exit != 0) return new PowerInfo(false, 0, 0, 0, 0, _pending);

        var line = outp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return new PowerInfo(false, 0, 0, 0, 0, _pending);

        var f = line.Split(',').Select(s => s.Trim()).ToArray();
        if (f.Length < 4) return new PowerInfo(false, 0, 0, 0, 0, _pending);

        double cur = ParseD(f, 0), def = ParseD(f, 1), min = ParseD(f, 2), max = ParseD(f, 3);
        // Some boards report min/max as [N/A]; fall back to sane bounds around default.
        if (min <= 0) min = def * 0.5;
        if (max <= 0) max = def;
        return new PowerInfo(true, cur, def, min, max, _pending);
    }

    public sealed record ApplyResult(bool Ok, double Applied, double RevertTo, double Min, double Max, string Message);

    // Apply a provisional power limit. Clamps to driver bounds, records the value to roll
    // back to, runs nvidia-smi -pl, and arms the auto-revert timer.
    public static ApplyResult Apply(double watts)
    {
        var info = Read();
        if (!info.Available)
            return new ApplyResult(false, 0, 0, 0, 0, "GPU/driver não disponível");

        double target = Math.Clamp(Math.Round(watts), Math.Round(info.Min), Math.Round(info.Max));

        lock (Lock)
        {
            // Roll back to whatever was set BEFORE the first provisional apply, not to the
            // previous provisional value — so repeated nudges still revert to the real baseline.
            double revertTo = _pending && _revertTo is not null ? _revertTo.Value : info.Current;

            if (!SetPl(target))
                return new ApplyResult(false, 0, revertTo, info.Min, info.Max, "nvidia-smi -pl falhou (precisa admin?)");

            FixLog.Append(new FixLogEntry(DateTime.UtcNow, Guid.NewGuid().ToString("N"),
                "apply", "gpu:powerlimit", "Limite de energia da GPU",
                revertTo.ToString(CultureInfo.InvariantCulture), target.ToString(CultureInfo.InvariantCulture),
                $"{revertTo:0} W", $"{target:0} W", "Limite de energia (provisório, auto-reverte)"));

            _revertTo = revertTo;
            _pending = true;
            ArmRevert();
            return new ApplyResult(true, target, revertTo, info.Min, info.Max, $"Aplicado {target:0} W — confirme em {RevertSeconds}s ou reverte sozinho");
        }
    }

    // Keep the provisional value: cancel the auto-revert.
    public static bool Confirm()
    {
        lock (Lock)
        {
            if (!_pending) return false;
            _revertTimer?.Dispose();
            _revertTimer = null;
            _pending = false;
            _revertTo = null;
            return true;
        }
    }

    // Restore the driver default limit immediately and clear any pending state.
    public static ApplyResult Reset()
    {
        var info = Read();
        if (!info.Available)
            return new ApplyResult(false, 0, 0, 0, 0, "GPU/driver não disponível");
        lock (Lock)
        {
            bool ok = SetPl(Math.Round(info.Default));
            _revertTimer?.Dispose(); _revertTimer = null;
            _pending = false; _revertTo = null;
            return ok
                ? new ApplyResult(true, Math.Round(info.Default), info.Default, info.Min, info.Max, "Restaurado ao padrão")
                : new ApplyResult(false, 0, info.Default, info.Min, info.Max, "nvidia-smi -pl falhou");
        }
    }

    static void ArmRevert()
    {
        _revertTimer?.Dispose();
        _revertTimer = new Timer(_ =>
        {
            lock (Lock)
            {
                if (!_pending || _revertTo is null) return;
                SetPl(Math.Round(_revertTo.Value));
                ToastHelper.Show("Forge — GPU",
                    $"Limite de energia revertido a {_revertTo.Value:0} W (sem confirmação).");
                _pending = false; _revertTo = null;
                _revertTimer?.Dispose(); _revertTimer = null;
            }
        }, null, TimeSpan.FromSeconds(RevertSeconds), Timeout.InfiniteTimeSpan);
    }

    // nvidia-smi -pl <watts>. Needs admin; returns false on non-zero exit.
    static bool SetPl(double watts)
    {
        var (exit, _) = GpuReader.RunSmi("-pl", ((int)Math.Round(watts)).ToString(CultureInfo.InvariantCulture));
        return exit == 0;
    }

    static double ParseD(string[] f, int i) =>
        i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
