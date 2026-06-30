using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForgeSentinel;

record Snapshot(
    DateTime CapturedAt,
    string PowerPlanGuid,
    string PowerPlanName,
    int DiagTrackStartType,
    int WSearchStartType,
    int HibernationEnabled,
    // Aceleração do mouse ("melhorar precisão do ponteiro"): 1 = ligada, 0 = desligada,
    // -1 = não lido. QoL de gameplay (mira 1:1), não é drift rastreado pelo Sentinel.
    int MouseAccel = -1,
    // Atalhos de teclas de acessibilidade (Shift x5 = Sticky Keys etc): 1 = hotkey ativo
    // (o popup que rouba foco no meio da partida), 0 = desativado, -1 = não lido. QoL.
    int AccessKeyHotkeys = -1
);

record Diff(string Setting, string Baseline, string Current, string Description);

static class SnapshotEngine
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ForgeSentinel");

    // Encrypted store (AES-GCM). Legacy plaintext path kept only for one-time import.
    static readonly string SnapshotPath       = Path.Combine(Dir, "snapshot.dat");
    static readonly string LegacySnapshotPath = Path.Combine(Dir, "snapshot.json");

    public static Snapshot Take()
    {
        var (guid, name) = GetActivePowerPlan();
        return new Snapshot(
            CapturedAt:          DateTime.UtcNow,
            PowerPlanGuid:       guid,
            PowerPlanName:       name,
            DiagTrackStartType:  GetServiceStart("DiagTrack"),
            WSearchStartType:    GetServiceStart("WSearch"),
            HibernationEnabled:  GetHibernation(),
            MouseAccel:          GetMouseAccel(),
            AccessKeyHotkeys:    GetAccessKeyHotkeys()
        );
    }

    public static List<Diff> Compare(Snapshot baseline, Snapshot current)
    {
        var diffs = new List<Diff>();

        if (baseline.PowerPlanGuid != current.PowerPlanGuid && baseline.PowerPlanGuid != "unknown")
            diffs.Add(new Diff("Plano de energia",
                baseline.PowerPlanName, current.PowerPlanName,
                $"Plano revertido de '{baseline.PowerPlanName}' para '{current.PowerPlanName}'"));

        if (baseline.DiagTrackStartType != current.DiagTrackStartType && baseline.DiagTrackStartType != -1)
            diffs.Add(new Diff("Telemetria (DiagTrack)",
                StartLabel(baseline.DiagTrackStartType), StartLabel(current.DiagTrackStartType),
                "Serviço de telemetria Microsoft foi reativado"));

        if (baseline.WSearchStartType != current.WSearchStartType && baseline.WSearchStartType != -1)
            diffs.Add(new Diff("Windows Search",
                StartLabel(baseline.WSearchStartType), StartLabel(current.WSearchStartType),
                "Indexação do Windows Search foi reativada"));

        if (baseline.HibernationEnabled != current.HibernationEnabled && baseline.HibernationEnabled != -1)
            diffs.Add(new Diff("Hibernação",
                baseline.HibernationEnabled == 1 ? "ativa" : "desativada",
                current.HibernationEnabled == 1 ? "ativa" : "desativada",
                "Estado de hibernação alterado"));

        return diffs;
    }

    public static void SaveBaseline(Snapshot s) => SecureStore.WriteJson(SnapshotPath, s);

    // First-run signal for the wizard: has a baseline ever been captured on this machine?
    public static bool BaselineExists() => File.Exists(SnapshotPath) || File.Exists(LegacySnapshotPath);

    public static Snapshot? LoadBaseline()
    {
        // If the encrypted store exists we trust ONLY it. A failed read here means
        // the blob was tampered (GCM auth fail) — we must NOT fall back to a
        // cleartext legacy file, or an attacker could downgrade by dropping a
        // plaintext snapshot.json. Legacy import happens only when .dat is absent.
        if (File.Exists(SnapshotPath))
        {
            var s = SecureStore.ReadJson<Snapshot>(SnapshotPath);
            // Encrypted file present but unreadable → tampered. Quarantine the stale
            // cleartext so it can't act as a downgrade path, and report no baseline.
            if (s is null && File.Exists(LegacySnapshotPath))
                TryDelete(LegacySnapshotPath);
            return s;
        }

        // One-time import of a pre-encryption baseline, then drop the cleartext file.
        if (File.Exists(LegacySnapshotPath))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(LegacySnapshotPath));
                if (legacy is not null)
                {
                    SaveBaseline(legacy);
                    File.Delete(LegacySnapshotPath);
                    return legacy;
                }
            }
            catch { /* ignore — treat as no baseline */ }
        }
        return null;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    static (string guid, string name) GetActivePowerPlan()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            // Output varies by locale: "GUID do Esquema de Energia: {uuid}  (Nome)" or "Power Scheme GUID: {uuid}  (Name)"
            var m = Regex.Match(output, @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((.+?)\)", RegexOptions.IgnoreCase);
            if (m.Success)
                return (m.Groups[1].Value.Trim().ToLowerInvariant(), m.Groups[2].Value.Trim());
        }
        catch { }
        return ("unknown", "Desconhecido");
    }

    static int GetServiceStart(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("Start") is int v ? v : -1;
        }
        catch { return -1; }
    }

    // "Melhorar precisão do ponteiro" = aceleração do mouse. Ligada por padrão no
    // Windows (MouseSpeed="1", limiares 6/10); desligada = tudo "0". Lemos MouseSpeed
    // (REG_SZ) como sinal. Hive do usuário logado (HKCU) — o processo elevado herda o
    // perfil do Pietro via UAC, então é o hive certo.
    static int GetMouseAccel()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            var v = key?.GetValue("MouseSpeed")?.ToString();
            return v switch { "0" => 0, "1" => 1, _ => -1 };
        }
        catch { return -1; }
    }

    // Sticky Keys é a representante das 3 teclas de acessibilidade. Bit 4 (SKF_HOTKEYACTIVE)
    // no Flags = o atalho Shift x5 que abre o popup. 1 = hotkey ligado (incomoda), 0 = off.
    static int GetAccessKeyHotkeys()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\StickyKeys");
            var raw = key?.GetValue("Flags")?.ToString();
            if (raw is null || !int.TryParse(raw, out var flags)) return -1;
            return (flags & 4) != 0 ? 1 : 0;
        }
        catch { return -1; }
    }

    static int GetHibernation()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (key is null) return -1;
            var val = key.GetValue("HibernateEnabled");
            // Key absent means hibernation disabled (powercfg /h off removes it)
            return val is int v ? v : 0;
        }
        catch { return -1; }
    }

    static string StartLabel(int v) => v switch
    {
        0 => "boot",
        1 => "sistema",
        2 => "automático",
        3 => "manual",
        4 => "desabilitado",
        _ => $"({v})"
    };
}
