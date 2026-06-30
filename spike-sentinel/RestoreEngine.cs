using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForgeSentinel;

// A single planned restore step. Pure data — no behavior. RawTarget is the value
// we want to write (the baseline); RawCurrent is what's there now (the undo target).
record PlannedAction(
    string Kind,
    string Setting,
    string RawCurrent,
    string RawTarget,
    string BeforeLabel,
    string AfterLabel,
    string Detail
);

// SECURITY principle 1: no arbitrary code execution. Every change maps to one of a
// fixed set of hardcoded actions below. Inputs are validated (GUID regex, service
// allowlist, bounded ints) before anything touches the system. Nothing here ever
// runs a command, path, or value supplied at runtime by config or UI.
static class RestoreEngine
{
    static readonly Regex GuidRe = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Build the restore plan: one action per setting that drifted from the baseline.
    public static List<PlannedAction> BuildPlan(Snapshot baseline, Snapshot current)
    {
        var plan = new List<PlannedAction>();

        if (baseline.PowerPlanGuid != "unknown" && baseline.PowerPlanGuid != current.PowerPlanGuid)
            plan.Add(new PlannedAction(
                "powerplan", "Plano de energia",
                current.PowerPlanGuid, baseline.PowerPlanGuid,
                current.PowerPlanName, baseline.PowerPlanName,
                $"Reativar plano '{baseline.PowerPlanName}'"));

        if (baseline.DiagTrackStartType != -1 && baseline.DiagTrackStartType != current.DiagTrackStartType)
            plan.Add(new PlannedAction(
                "service:DiagTrack", "Telemetria (DiagTrack)",
                current.DiagTrackStartType.ToString(), baseline.DiagTrackStartType.ToString(),
                StartLabel(current.DiagTrackStartType), StartLabel(baseline.DiagTrackStartType),
                "Restaurar tipo de início do serviço DiagTrack"));

        if (baseline.WSearchStartType != -1 && baseline.WSearchStartType != current.WSearchStartType)
            plan.Add(new PlannedAction(
                "service:WSearch", "Windows Search",
                current.WSearchStartType.ToString(), baseline.WSearchStartType.ToString(),
                StartLabel(current.WSearchStartType), StartLabel(baseline.WSearchStartType),
                "Restaurar tipo de início do serviço WSearch"));

        if (baseline.HibernationEnabled != -1 && baseline.HibernationEnabled != current.HibernationEnabled)
            plan.Add(new PlannedAction(
                "hibernation", "Hibernação",
                current.HibernationEnabled.ToString(), baseline.HibernationEnabled.ToString(),
                current.HibernationEnabled == 1 ? "ativa" : "desativada",
                baseline.HibernationEnabled == 1 ? "ativa" : "desativada",
                $"Definir hibernação para {(baseline.HibernationEnabled == 1 ? "on" : "off")}"));

        return plan;
    }

    // The single execution primitive. Used by both `restore` (write target) and
    // `undo` (write the logged before-value). Dispatches on a fixed kind allowlist.
    public static bool ApplyRaw(string kind, string raw)
    {
        switch (kind)
        {
            case "powerplan":          return SetPowerPlan(raw);
            case "service:DiagTrack":  return SetServiceStart("DiagTrack", raw);
            case "service:WSearch":    return SetServiceStart("WSearch", raw);
            case "hibernation":        return SetHibernation(raw);
            case "mouseaccel":         return SetMouseAccel(raw);
            case "accesskeys":         return SetAccessKeys(raw);
            default:                   return false;
        }
    }

    static bool SetPowerPlan(string guid)
    {
        if (!GuidRe.IsMatch(guid)) return false;
        return RunPowercfg("/setactive", guid);
    }

    static bool SetServiceStart(string service, string rawStart)
    {
        if (service is not ("DiagTrack" or "WSearch")) return false;
        if (!int.TryParse(rawStart, out var start) || start is < 0 or > 4) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{service}", writable: true);
            if (key is null) return false;
            key.SetValue("Start", start, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    static bool SetHibernation(string raw)
    {
        if (raw is not ("0" or "1")) return false;
        return RunPowercfg("/hibernate", raw == "1" ? "on" : "off");
    }

    // Aceleração do mouse ("melhorar precisão do ponteiro"). raw "0" = desligar (mira
    // 1:1, o que todo jogador de FPS quer), "1" = restaurar o padrão do Windows.
    // Grava os três valores REG_SZ E aplica ao vivo via SystemParametersInfo — sem isso
    // a mudança só valeria no próximo logon.
    const uint SPI_SETMOUSE        = 0x0004;
    const uint SPIF_UPDATEINIFILE  = 0x01;
    const uint SPIF_SENDCHANGE     = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint uAction, uint uParam, int[] lpvParam, uint fWinIni);

    static bool SetMouseAccel(string raw)
    {
        if (raw is not ("0" or "1")) return false;
        // {threshold1, threshold2, speed}. Tudo 0 = sem aceleração; padrão Windows = 6/10/1.
        var (t1, t2, spd) = raw == "1" ? ("6", "10", "1") : ("0", "0", "0");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true);
            if (key is null) return false;
            key.SetValue("MouseThreshold1", t1, RegistryValueKind.String);
            key.SetValue("MouseThreshold2", t2, RegistryValueKind.String);
            key.SetValue("MouseSpeed",      spd, RegistryValueKind.String);
        }
        catch { return false; }
        // Best-effort live apply; o registro acima já é a fonte da verdade pro snapshot.
        try { SystemParametersInfo(SPI_SETMOUSE, 0,
            new[] { int.Parse(t1), int.Parse(t2), int.Parse(spd) },
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE); } catch { }
        return true;
    }

    // Teclas de acessibilidade (Sticky/Filter/Toggle). raw "0" = desligar o atalho
    // (limpa o bit 4 SKF_HOTKEYACTIVE → sem popup do Shift x5 no meio da partida),
    // "1" = restaurar os defaults do Windows. Flags são REG_SZ. Sticky aplica ao vivo
    // via SPI_SETSTICKYKEYS; Filter/Toggle valem no próximo logon (popups raros).
    const uint SPI_SETSTICKYKEYS = 0x003B;

    [StructLayout(LayoutKind.Sequential)]
    struct STICKYKEYS { public uint cbSize; public uint dwFlags; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint uAction, uint uParam, ref STICKYKEYS pv, uint fWinIni);

    static bool SetAccessKeys(string raw)
    {
        if (raw is not ("0" or "1")) return false;
        // off limpa o bit 4 (hotkey) de cada: Sticky 510→506, Filter 126→122, Toggle 62→58.
        var (sticky, filter, toggle) = raw == "1" ? ("510", "126", "62") : ("506", "122", "58");
        try
        {
            SetFlags(@"Control Panel\Accessibility\StickyKeys", sticky);
            SetFlags(@"Control Panel\Accessibility\Keyboard Response", filter);
            SetFlags(@"Control Panel\Accessibility\ToggleKeys", toggle);
        }
        catch { return false; }
        // Live apply só do Sticky (o popup que de fato atrapalha jogo).
        try
        {
            var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>(), dwFlags = uint.Parse(sticky) };
            SystemParametersInfo(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch { }
        return true;
    }

    static void SetFlags(string subkey, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(subkey);
        key.SetValue("Flags", value, RegistryValueKind.String);
    }

    // Args passed as a fixed verb + a single validated token — never a shell string.
    static bool RunPowercfg(string verb, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    static string StartLabel(int v) => v switch
    {
        0 => "boot", 1 => "sistema", 2 => "automático", 3 => "manual", 4 => "desabilitado", _ => $"({v})"
    };
}
