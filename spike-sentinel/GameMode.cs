using System.Diagnostics;
using System.Text.Json;

namespace ForgeSentinel;

// Forge.Games — automatic "Modo Jogo".
//
// Truth-mode scope: this is the HONEST first slice, not the full per-game graphics
// solver sketched in profiles/README.md (HardwareTier + solver + per-format config
// writing are still ahead). What ships here is real and reversible: detect when a
// known game is running and, for that session only, apply a safe SYSTEM optimization
// — the High-performance power plan, so CPU/GPU don't downclock mid-match — then
// restore the EXACT pre-game plan when the game exits. Same ethic as RestoreEngine:
// snapshot before, audited fixlog, fully reversible. No game config file is touched.
//
// Power-plan switching doesn't require admin, but the /api/gamemode toggle is still
// admin-gated for consistency with the other mutating endpoints and because the
// module is meant to grow into changes (process priority, services) that do.
static class GameMode
{
    // Windows "High performance" power scheme GUID — same target /api/apply uses.
    const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    static readonly object _gate = new();
    static readonly System.Threading.Timer _timer = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

    static Dictionary<string, string>? _procToGame; // process name (no .exe) -> game display name
    static bool _enabled;
    static bool _ticking;

    // Current session, when a detected game is being optimized.
    static string? _activeGame;
    static string? _prePlanGuid; // power plan to restore when the game exits

    public static bool Enabled { get { lock (_gate) return _enabled; } }
    public static string? ActiveGame { get { lock (_gate) return _activeGame; } }

    public static void Enable()
    {
        lock (_gate)
        {
            if (_enabled) return;
            _enabled = true;
            _procToGame ??= LoadProfiles();
        }
        ToastHelper.Show("Forge — Modo Jogo", "Vigia ligado. Otimizo o sistema quando um jogo abrir.");
        _timer.Change(0, 5000); // scan now, then every 5s
    }

    public static void Disable()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _enabled = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_activeGame is not null) EndSessionNoLock("Modo Jogo desligado");
        }
        ToastHelper.Show("Forge — Modo Jogo", "Vigia desligado.");
    }

    static void Tick()
    {
        lock (_gate)
        {
            if (!_enabled || _ticking) return;
            _ticking = true; // a slow process scan must not overlap the next timer fire
        }
        try
        {
            var running = RunningGame();
            lock (_gate)
            {
                if (!_enabled) return;
                if (_activeGame is null && running is not null)
                    StartSessionNoLock(running);
                else if (_activeGame is not null && running is null)
                    EndSessionNoLock("Jogo encerrado");
                else if (_activeGame is not null && running is not null && running != _activeGame)
                {
                    // Switched straight from one game to another: revert A, start B.
                    EndSessionNoLock("Troca de jogo");
                    StartSessionNoLock(running);
                }
            }
        }
        finally { lock (_gate) _ticking = false; }
    }

    // Display name of a detected, running game, or null. Best-effort: a process that
    // vanishes mid-scan (race) is simply skipped.
    static string? RunningGame()
    {
        var map = _procToGame;
        if (map is null || map.Count == 0) return null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string name;
                try { name = p.ProcessName; }
                catch { continue; }
                finally { p.Dispose(); }
                if (map.TryGetValue(name, out var game)) return game;
            }
        }
        catch { /* enumeration race — try again next tick */ }
        return null;
    }

    static void StartSessionNoLock(string game)
    {
        var snap = SnapshotEngine.Take();
        var prev = snap.PowerPlanGuid;
        _activeGame = game;
        _prePlanGuid = prev;

        // Already on High-performance: nothing to change, just track the session so we
        // don't claim a revert later that we never made.
        if (string.Equals(prev, HighPerfGuid, StringComparison.OrdinalIgnoreCase))
        {
            ToastHelper.Show("Forge — Modo Jogo", $"{game} detectado. Já no plano de máximo desempenho.");
            return;
        }

        if (RestoreEngine.ApplyRaw("powerplan", HighPerfGuid))
        {
            var batchId = Guid.NewGuid().ToString("N");
            FixLog.Append(new FixLogEntry(DateTime.UtcNow, batchId, "game-on", "powerplan",
                "Plano de energia", prev, HighPerfGuid, snap.PowerPlanName, "Alto desempenho",
                $"Modo Jogo: {game}"));
            ToastHelper.Show("Forge — Modo Jogo", $"{game}: plano Alto desempenho ativado.");
        }
    }

    static void EndSessionNoLock(string reason)
    {
        var game = _activeGame;
        var prev = _prePlanGuid;
        _activeGame = null;
        _prePlanGuid = null;
        if (game is null || prev is null) return;

        // Restore the exact pre-game plan — but only if we're still on the one we set
        // (don't stomp a plan the user changed by hand during the session).
        var snap = SnapshotEngine.Take();
        if (!string.Equals(snap.PowerPlanGuid, prev, StringComparison.OrdinalIgnoreCase)
            && RestoreEngine.ApplyRaw("powerplan", prev))
        {
            var batchId = Guid.NewGuid().ToString("N");
            FixLog.Append(new FixLogEntry(DateTime.UtcNow, batchId, "game-off", "powerplan",
                "Plano de energia", snap.PowerPlanGuid, prev, "Alto desempenho", "(plano anterior)",
                $"Modo Jogo encerrado: {reason}"));
        }
        ToastHelper.Show("Forge — Modo Jogo", $"{game}: sessão encerrada, plano revertido.");
    }

    // Build process-name -> game-name from profiles/games/*.json using JsonDocument
    // only (AOT-safe; no reflection serializer). A malformed profile is skipped, not
    // fatal. detect.process entries may carry a .exe suffix; we key on the bare name
    // to match Process.ProcessName.
    static Dictionary<string, string> LoadProfiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = LocateProfiles();
        if (dir is null) return map;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Jogo" : "Jogo";
                if (root.TryGetProperty("detect", out var det)
                    && det.TryGetProperty("process", out var procs)
                    && procs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var proc in procs.EnumerateArray())
                    {
                        var ps = proc.GetString();
                        if (string.IsNullOrWhiteSpace(ps)) continue;
                        var key = ps.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ps[..^4] : ps;
                        map[key] = name;
                    }
                }
            }
            catch { /* skip malformed profile */ }
        }
        return map;
    }

    // Resolve profiles/games relative to the exe, walking up a few levels so it works
    // both from bin/Debug/... during the spike and from the installed layout.
    static string? LocateProfiles()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "profiles", "games");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
