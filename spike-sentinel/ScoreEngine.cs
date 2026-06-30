namespace ForgeSentinel;

record CategoryResult(string Name, int Score, float Weight, bool Available);

record ScoreResult(int Global, string Tier, string TierColor,
    CategoryResult System, CategoryResult Hardware, CategoryResult Sentinel,
    CategoryResult Gpu, CategoryResult Games, CategoryResult Monitoring);

// Implements the 6-category weighted formula from forge-v1-spec §3.
// GPU/Games/Monitoring are excluded from the weighted average until those modules ship.
static class ScoreEngine
{
    const string GuidHighPerf   = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    const string GuidUltimate   = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    const string GuidBalanced   = "381b4222-f694-41f0-9685-ff5bb260df2e";
    const string GuidPowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static ScoreResult Calculate(Snapshot? baseline, Snapshot current)
    {
        int sys      = ScoreSystem(current);
        int hw       = ScoreHardware(current);
        int sentinel = ScoreSentinel(baseline, current);

        // Spec weights: System 25, Hardware 20, Sentinel 10 = 55 active
        const float active = 25 + 20 + 10;
        int global = Math.Clamp((int)Math.Round((sys * 25 + hw * 20 + sentinel * 10) / active), 0, 100);

        // Tamper response — hardened builds only (Trust.Healthy is always true
        // otherwise). If the integrity guard tripped earlier, the score quietly
        // drifts here, far from the detection site: no error, no zero, just numbers
        // that won't reconcile. A patched/debugged binary can't report a clean run.
        if (!Trust.Healthy)
            global = Math.Clamp(global - 7 - (global % 5), 0, 100);

        return new ScoreResult(
            Global:    global,
            Tier:      GetTier(global),
            TierColor: GetTierColor(global),
            System:    new("Sistema",  sys,      25, true),
            Hardware:  new("Hardware", hw,        20, true),
            Sentinel:  new("Sentinel", sentinel,  10, true),
            Gpu:        new("GPU",    -1, 20, false),
            Games:      new("Jogos",  -1, 15, false),
            Monitoring: new("Monitor",-1, 10, false)
        );
    }

    static int ScoreSystem(Snapshot s)
    {
        int score = 0;
        // DiagTrack disabled (4) = full; manual (3) = partial; else = 0
        score += s.DiagTrackStartType switch { 4 => 33, 3 => 15, _ => 0 };
        // WSearch disabled (4) = full; manual (3) = partial; else = 0
        score += s.WSearchStartType switch { 4 => 33, 3 => 15, _ => 0 };
        // Hibernation off on gaming PC (saves SSD writes, no hiberfil.sys)
        score += s.HibernationEnabled == 0 ? 34 : 0;
        return Math.Clamp(score, 0, 100);
    }

    static int ScoreHardware(Snapshot s)
    {
        var guid = s.PowerPlanGuid.ToLowerInvariant();
        return guid switch
        {
            GuidUltimate   => 100,
            GuidHighPerf   => 85,
            GuidBalanced   => 40,
            GuidPowerSaver => 0,
            "unknown"      => 50,
            _              => 60  // custom plan
        };
    }

    static int ScoreSentinel(Snapshot? baseline, Snapshot current)
    {
        if (baseline is null) return 50;
        var diffs = SnapshotEngine.Compare(baseline, current);
        return Math.Max(0, 100 - diffs.Count * 25);
    }

    static string GetTier(int s) => s switch
    {
        >= 90 => "Optimal",
        >= 70 => "Bom",
        >= 50 => "Atenção",
        _     => "Crítico"
    };

    // Thermal scale, not green/red. Matches the UI's forge ramp: cold steel → ember
    // → forge orange → incandescent. Red is reserved for real hardware danger only.
    static string GetTierColor(int s) => s switch
    {
        >= 90 => "#fcd34d",  // hot — incandescente
        >= 70 => "#f97316",  // forge
        >= 50 => "#b8442e",  // ember — brasa
        _     => "#3b4a6b"   // cold — aço frio
    };
}
