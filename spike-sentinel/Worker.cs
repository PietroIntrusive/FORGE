namespace ForgeSentinel;

sealed class Worker(ILogger<Worker> log) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Forge Sentinel iniciado — {Time}", DateTime.Now);

        var baseline = InitBaseline();
        var lastWu = WuDetector.GetLastInstallTime();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Interval, ct).ConfigureAwait(false);

            var currentWu = WuDetector.GetLastInstallTime();
            if (currentWu != lastWu)
            {
                log.LogWarning("Windows Update detectado em {Time}", currentWu?.ToLocalTime());
                lastWu = currentWu;
            }

            Check(baseline);
        }
    }

    Snapshot InitBaseline()
    {
        var baseline = SnapshotEngine.LoadBaseline();
        if (baseline is null)
        {
            baseline = SnapshotEngine.Take();
            SnapshotEngine.SaveBaseline(baseline);
            log.LogInformation("Baseline criado — plano: {Plan}, DiagTrack: {DT}, WSearch: {WS}",
                baseline.PowerPlanName, baseline.DiagTrackStartType, baseline.WSearchStartType);
            ToastHelper.Show("Forge Sentinel ativo",
                $"Monitorando configurações. Baseline: {baseline.PowerPlanName}.");
        }
        else
        {
            log.LogInformation("Baseline carregado de {At}", baseline.CapturedAt);
        }
        return baseline;
    }

    void Check(Snapshot baseline)
    {
        var current = SnapshotEngine.Take();
        var diffs = SnapshotEngine.Compare(baseline, current);

        if (diffs.Count == 0)
        {
            log.LogInformation("[{Time}] Sem regressões.", DateTime.Now.ToString("HH:mm"));
            return;
        }

        foreach (var d in diffs)
            log.LogWarning("  REGRESSÃO — {Setting}: {B} → {C} | {Desc}",
                d.Setting, d.Baseline, d.Current, d.Description);

        var summary = diffs.Count == 1
            ? diffs[0].Description
            : $"{diffs.Count} configurações revertidas — {string.Join(", ", diffs.Select(d => d.Setting))}";

        ToastHelper.Show(
            $"Forge — {diffs.Count} regressão{(diffs.Count > 1 ? "ões" : "")} detectada{(diffs.Count > 1 ? "s" : "")}",
            summary + ". Abra o Forge para corrigir.");
    }
}
