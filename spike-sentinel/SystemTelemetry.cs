using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ForgeSentinel;

// Telemetria do monitor sem subprocess no caminho quente.
//
// O MonitorScript antigo spawnava um powershell.exe por poll. Com um jogo
// cravando a CPU (beta v1.1.1, feedback Torres), o spawn levava segundos ou
// estourava o timeout: número atrasado, payload incompleto, seção sumindo na
// UI. Divisão nova:
//
//   QUENTE (todo poll, in-process, ~ms):
//     - CPU total e por núcleo via PDH "% Processor Utility" — a MESMA métrica
//       do Gerenciador de Tarefas (pondera frequência/boost). O antigo
//       PercentProcessorTime é baseado em tempo e lia 60-80% com jogo a 100%.
//       PdhAddEnglishCounterW aceita o caminho em inglês em qualquer locale;
//       os nomes de contador são TRADUZIDOS no Windows pt-BR.
//     - RAM via GlobalMemoryStatusEx (mesma semântica "em uso" do Gerenciador).
//     - I/O de disco agregado via PDH PhysicalDisk(_Total).
//     - Top processos por RAM; CPU% por delta de TotalProcessorTime entre polls.
//
//   FRIO (PowerShell UMA vez no boot + refresh a cada 120s em thread própria,
//   nunca no caminho do request): cores físicos da CPU e inventário de discos
//   (modelo/mídia/barramento/tamanho/uso/saúde/temp). Refresh falhou? Serve o
//   cache anterior — a seção da UI não some.
static class SystemTelemetry
{
    // ---------- PDH (caminho quente) ----------

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr reserved, out PdhFmtValue value);

    const uint PdhFmtDouble = 0x200;

    // pdh.h: DWORD CStatus + união (usamos só o double, que alinha em offset 8).
    [StructLayout(LayoutKind.Sequential)]
    struct PdhFmtValue { public uint CStatus; public double Value; }

    static readonly object HotLock = new();
    static IntPtr _query;
    static IntPtr _cpuTotal, _ioRead, _ioWrite;
    static IntPtr[] _cpuCore = Array.Empty<IntPtr>();
    static bool _primed;

    static void EnsureQuery()
    {
        if (_query != IntPtr.Zero) return;
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }

        PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuTotal);
        PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _ioRead);
        PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _ioWrite);

        // Instâncias de "Processor Information" nomeiam "grupo,índice" ("0,0"…"0,11").
        var cores = new List<IntPtr>();
        for (int i = 0; i < Environment.ProcessorCount; i++)
            if (PdhAddEnglishCounterW(_query, $@"\Processor Information(0,{i})\% Processor Utility",
                                      IntPtr.Zero, out var h) == 0)
                cores.Add(h);
        _cpuCore = cores.ToArray();

        // Contador de taxa exige duas coletas; esta primeira só arma a base.
        PdhCollectQueryData(_query);
    }

    static double Fmt(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return -1;
        return PdhGetFormattedCounterValue(counter, PdhFmtDouble, IntPtr.Zero, out var v) == 0 && v.CStatus == 0
            ? v.Value : -1;
    }

    // "% Processor Utility" passa de 100 com boost de clock; exibição clampa.
    static int Pct(double v) => v < 0 ? 0 : (int)Math.Clamp(Math.Round(v), 0, 100);

    // ---------- RAM ----------

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length; public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile;
        public ulong TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // ---------- top processos ----------

    sealed record ProcSample(TimeSpan Cpu, DateTime At);
    static Dictionary<int, ProcSample> _procPrev = new();

    static object[] TopProcs()
    {
        var now = DateTime.UtcNow;
        var next = new Dictionary<int, ProcSample>();
        var list = new List<(string Name, long Ram, int Cpu)>();
        int lp = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; } // processo protegido
                next[p.Id] = new ProcSample(cpu, now);

                int pct = 0;
                if (cpu > TimeSpan.Zero && _procPrev.TryGetValue(p.Id, out var prev))
                {
                    var wall = (now - prev.At).TotalSeconds;
                    if (wall > 0.2)
                        pct = (int)Math.Clamp((cpu - prev.Cpu).TotalSeconds / wall / lp * 100, 0, 100);
                }
                list.Add((p.ProcessName, p.WorkingSet64, pct));
            }
            catch { /* processo morreu no meio da leitura */ }
            finally { p.Dispose(); }
        }

        _procPrev = next;
        return list.OrderByDescending(x => x.Ram).Take(6)
                   .Select(x => (object)new { name = x.Name, ram = x.Ram, cpu = x.Cpu })
                   .ToArray();
    }

    // ---------- cache frio (specs CPU + inventário de discos) ----------

    static readonly object ColdLock = new();
    static JsonElement? _coldDisks;
    static int _coldCores;
    static DateTime _coldAt = DateTime.MinValue;
    static bool _coldRunning;

    // Mesmo idioma do antigo MonitorScript: uma linha, aspas simples, sem aspas
    // duplas (passa pelo -Command com aspas duplas do RunPs).
    const string ColdScript =
        "$ErrorActionPreference='SilentlyContinue';" +
        "$cp=Get-CimInstance Win32_Processor|Select-Object -First 1;" +
        "$dk=Get-PhysicalDisk|ForEach-Object {" +
            "$rt=$null;try{$rt=($_|Get-StorageReliabilityCounter).Temperature}catch{};" +
            "$dn=[int]$_.DeviceId;$us=$null;" +
            "try{$vs=Get-Partition -DiskNumber $dn|Get-Volume;$us=([int64](($vs|Measure-Object -Property Size -Sum).Sum))-([int64](($vs|Measure-Object -Property SizeRemaining -Sum).Sum))}catch{};" +
            "[pscustomobject]@{model=$_.FriendlyName;media=[string]$_.MediaType;bus=[string]$_.BusType;size=[int64]$_.Size;used=$us;health=[string]$_.HealthStatus;temp=$rt}};" +
        "[pscustomobject]@{cores=[int]$cp.NumberOfCores;disks=@($dk)}|ConvertTo-Json -Depth 5 -Compress";

    static void RefreshColdAsync()
    {
        lock (ColdLock)
        {
            if (_coldRunning || (DateTime.UtcNow - _coldAt) < TimeSpan.FromSeconds(120)) return;
            _coldRunning = true;
        }
        Task.Run(() =>
        {
            try
            {
                var raw = ApiServer.RunPs(ColdScript).Trim();
                if (raw.Length > 0)
                {
                    using var doc = JsonDocument.Parse(raw);
                    var cores = doc.RootElement.TryGetProperty("cores", out var c) ? c.GetInt32() : 0;
                    JsonElement? disks = doc.RootElement.TryGetProperty("disks", out var d) ? d.Clone() : null;
                    lock (ColdLock)
                    {
                        _coldCores = cores;
                        if (disks is not null) _coldDisks = disks;
                        _coldAt = DateTime.UtcNow;
                    }
                }
            }
            catch { /* mantém o cache anterior — seção não some */ }
            finally { lock (ColdLock) _coldRunning = false; }
        });
    }

    static string? _cpuName;
    static string CpuName()
    {
        if (_cpuName is not null) return _cpuName;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch { _cpuName = ""; }
        return _cpuName;
    }

    // Chamado uma vez na subida do daemon: abre a query PDH e dispara a primeira
    // carga do cache frio antes de qualquer poll da UI.
    public static void PrimeAtStartup()
    {
        lock (HotLock) EnsureQuery();
        RefreshColdAsync();
    }

    // Payload completo do GET /api/monitor — mesmo contrato do MonitorScript
    // antigo: {available, sys:{cpu{name,cores,threads,load,per_core}, ram, io,
    // disks, procs}}.
    public static string MonitorJson()
    {
        RefreshColdAsync(); // agenda se envelheceu; nunca bloqueia o request

        double load, read, write;
        int[] perCore;
        lock (HotLock)
        {
            EnsureQuery();
            if (_query == IntPtr.Zero) return "{\"available\":false}";

            PdhCollectQueryData(_query);
            if (!_primed)
            {
                // Primeira leitura real: espera um tick pra taxa ter base válida.
                _primed = true;
                Thread.Sleep(300);
                PdhCollectQueryData(_query);
            }
            load = Fmt(_cpuTotal);
            read = Fmt(_ioRead);
            write = Fmt(_ioWrite);
            perCore = _cpuCore.Select(h => Pct(Fmt(h))).ToArray();
        }

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        long totalKb = 0, freeKb = 0;
        if (GlobalMemoryStatusEx(ref mem))
        {
            totalKb = (long)(mem.TotalPhys / 1024);
            freeKb  = (long)(mem.AvailPhys / 1024);
        }

        JsonElement? disks;
        int physCores;
        lock (ColdLock) { disks = _coldDisks; physCores = _coldCores; }

        var sys = new
        {
            cpu = new
            {
                name = CpuName(),
                cores = physCores,
                threads = Environment.ProcessorCount,
                load = Pct(load),
                per_core = perCore,
            },
            ram = new { total_kb = totalKb, free_kb = freeKb },
            io = new { read_bps = (long)Math.Max(read, 0), write_bps = (long)Math.Max(write, 0) },
            disks = disks is JsonElement d ? (object)d : Array.Empty<object>(),
            procs = TopProcs(),
        };
        return "{\"available\":true,\"sys\":" + JsonSerializer.Serialize(sys) + "}";
    }
}
