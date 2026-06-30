namespace ForgeSentinel;

// Manual verbs for the spike. The shipped product drives these same engines from
// the UI over IPC; here they're a console surface to prove apply + revert end to end.
static class Cli
{
    public static void Help()
    {
        Console.WriteLine("""
            Forge Sentinel — uso:
              ForgeSentinel run                inicia o serviço (detecta regressões pós-Windows Update)
              ForgeSentinel status             mostra baseline vs estado atual
              ForgeSentinel score              mostra o score 0–100 e categorias
              ForgeSentinel restore            dry-run: lista o que seria restaurado (nada muda)
              ForgeSentinel restore --apply    aplica a restauração ao baseline (exige admin)
              ForgeSentinel undo               dry-run: mostra a última restauração a desfazer
              ForgeSentinel undo --apply       desfaz a última restauração (exige admin)
              ForgeSentinel baseline --reset   recaptura o baseline a partir do estado atual
              ForgeSentinel serve              API HTTP endurecida em 127.0.0.1:5172 + UI
            """);
    }

    public static void Status()
    {
        var baseline = SnapshotEngine.LoadBaseline();
        if (baseline is null)
        {
            Console.WriteLine("Sem baseline. Rode o serviço uma vez, ou: ForgeSentinel baseline --reset");
            return;
        }

        var current = SnapshotEngine.Take();
        var diffs = SnapshotEngine.Compare(baseline, current);

        Console.WriteLine($"Baseline: {baseline.CapturedAt.ToLocalTime()}");
        Console.WriteLine($"  Plano de energia : {baseline.PowerPlanName}");
        Console.WriteLine($"  Hibernação       : {(baseline.HibernationEnabled == 1 ? "ativa" : "desativada")}");
        Console.WriteLine();

        if (diffs.Count == 0)
        {
            Console.WriteLine("OK — nenhuma regressão. Sistema bate com o baseline.");
            return;
        }

        Console.WriteLine($"{diffs.Count} regressao(oes):");
        foreach (var d in diffs)
            Console.WriteLine($"  - {d.Setting}: {d.Baseline} -> {d.Current}");
        Console.WriteLine();
        Console.WriteLine("Corrigir:  ForgeSentinel restore --apply");
    }

    public static void Restore(bool apply)
    {
        var baseline = SnapshotEngine.LoadBaseline();
        if (baseline is null) { Console.WriteLine("Sem baseline."); return; }

        var current = SnapshotEngine.Take();
        var plan = RestoreEngine.BuildPlan(baseline, current);

        if (plan.Count == 0)
        {
            Console.WriteLine("Nada a restaurar — sistema bate com o baseline.");
            return;
        }

        Console.WriteLine($"Plano de restauracao ({plan.Count} acao(oes)):");
        foreach (var a in plan)
            Console.WriteLine($"  - {a.Setting}: {a.BeforeLabel} -> {a.AfterLabel}  ({a.Detail})");
        Console.WriteLine();

        // SECURITY principle 4: shown before applied. Dry-run is the default; the
        // explicit --apply IS the confirmation.
        if (!apply)
        {
            Console.WriteLine("DRY-RUN. Nada foi alterado.");
            Console.WriteLine("Aplicar de verdade:  ForgeSentinel restore --apply");
            return;
        }

        if (!Elevation.IsAdmin())
        {
            Console.WriteLine("ERRO: restaurar servicos e energia exige administrador.");
            Console.WriteLine("Rode num terminal elevado (Executar como administrador).");
            return;
        }

        // Safety net before touching the machine. Best-effort; doesn't block apply.
        var rp = RestorePointEngine.Create("Forge antes de restaurar ao baseline");
        Console.WriteLine(rp.Created
            ? "  Ponto de restauração criado."
            : $"  (ponto de restauração não criado — {rp.Message})");
        Console.WriteLine();

        var batchId = Guid.NewGuid().ToString("N");
        int ok = 0;
        foreach (var a in plan)
        {
            if (RestoreEngine.ApplyRaw(a.Kind, a.RawTarget))
            {
                ok++;
                FixLog.Append(new FixLogEntry(
                    DateTime.UtcNow, batchId, "restore", a.Kind, a.Setting,
                    a.RawCurrent, a.RawTarget, a.BeforeLabel, a.AfterLabel, a.Detail));
                Console.WriteLine($"  [OK]    {a.Setting}: {a.BeforeLabel} -> {a.AfterLabel}");
            }
            else
            {
                Console.WriteLine($"  [FALHA] {a.Setting} — nao aplicado");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{ok}/{plan.Count} aplicada(s). Log: {FixLog.Location}");
        ToastHelper.Show("Forge — restauracao aplicada",
            $"{ok} de {plan.Count} configuracao(oes) restaurada(s) ao baseline.");
    }

    public static void Undo(bool apply)
    {
        var log = FixLog.Load();
        // Undo the last *forward* mutation batch — a restore-to-baseline or an
        // applied optimization. Both write via ApplyRaw and log RawBefore, so the
        // same revert path covers them.
        var lastBatch = log.Where(e => e.Action is "restore" or "apply")
                           .GroupBy(e => e.BatchId)
                           .LastOrDefault();

        if (lastBatch is null)
        {
            Console.WriteLine("Nada no fix_log para desfazer.");
            return;
        }

        var entries = lastBatch.ToList();
        Console.WriteLine($"Desfazer ultima restauracao ({entries.Count} acao(oes)):");
        foreach (var e in entries)
            Console.WriteLine($"  - {e.Setting}: {e.AfterLabel} -> {e.BeforeLabel}");
        Console.WriteLine();

        if (!apply)
        {
            Console.WriteLine("DRY-RUN. Aplicar:  ForgeSentinel undo --apply");
            return;
        }

        if (!Elevation.IsAdmin())
        {
            Console.WriteLine("ERRO: exige administrador. Rode num terminal elevado.");
            return;
        }

        var batchId = Guid.NewGuid().ToString("N");
        int ok = 0;
        foreach (var e in entries)
        {
            if (RestoreEngine.ApplyRaw(e.Kind, e.RawBefore))
            {
                ok++;
                FixLog.Append(new FixLogEntry(
                    DateTime.UtcNow, batchId, "undo", e.Kind, e.Setting,
                    e.RawAfter, e.RawBefore, e.AfterLabel, e.BeforeLabel, $"undo de {e.Detail}"));
                Console.WriteLine($"  [OK]    {e.Setting}: {e.AfterLabel} -> {e.BeforeLabel}");
            }
            else
            {
                Console.WriteLine($"  [FALHA] {e.Setting} — nao revertido");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{ok}/{entries.Count} revertida(s).");
    }

    public static void Baseline(bool reset)
    {
        if (!reset)
        {
            Console.WriteLine("Use: ForgeSentinel baseline --reset  (recaptura o baseline do estado atual)");
            return;
        }
        var snap = SnapshotEngine.Take();
        SnapshotEngine.SaveBaseline(snap);
        Console.WriteLine($"Baseline recapturado: {snap.CapturedAt.ToLocalTime()}");
        Console.WriteLine($"  Plano: {snap.PowerPlanName} | DiagTrack: {snap.DiagTrackStartType} | WSearch: {snap.WSearchStartType} | Hib: {snap.HibernationEnabled}");
    }

    public static void Score()
    {
        var baseline = SnapshotEngine.LoadBaseline();
        var current  = SnapshotEngine.Take();
        var result   = ScoreEngine.Calculate(baseline, current);

        Console.WriteLine($"FORGE SCORE: {result.Global}/100  [{result.Tier}]");
        Console.WriteLine();
        Console.WriteLine("Categorias (módulos não implementados mostram N/A):");
        PrintCat(result.System);
        PrintCat(result.Hardware);
        PrintCat(result.Sentinel);
        PrintCat(result.Gpu);
        PrintCat(result.Games);
        PrintCat(result.Monitoring);
        Console.WriteLine();
        if (baseline is null)
            Console.WriteLine("Aviso: sem baseline — Sentinel retornou 50 (neutro). Rode: baseline --reset");
    }

    static void PrintCat(CategoryResult c)
    {
        var score = c.Available ? $"{c.Score,3}/100" : "  N/A  ";
        var bar   = c.Available ? new string('█', c.Score / 5) + new string('░', 20 - c.Score / 5) : new string('·', 20);
        Console.WriteLine($"  {c.Name,-10} {score}  {bar}  (peso {c.Weight}%)");
    }

    // The HTTP surface moved to ApiServer (loopback lock, CSRF token, Host/Origin
    // allowlist, same-origin UI). Kept here as a thin delegate so `serve` still works.
    public static Task Serve() => ApiServer.Run();
}
