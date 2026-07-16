using System.Runtime.InteropServices;
using System.Text;

namespace ForgeSentinel;

// Telemetria de GPU in-process via NVML (nvml.dll, embarcada no driver NVIDIA —
// a mesma biblioteca que Afterburner/HWiNFO usam). Substitui o spawn de
// nvidia-smi a cada poll: sob carga de jogo o ReadToEnd síncrono segurava o
// processo e nvidia-smi.exe acumulava igual aos powershell (beta v1.1.x).
// nvidia-smi continua existindo como fallback de leitura (NVML indisponível)
// e na escrita do power limit (GpuTuner), que é rara e disparada pelo usuário.
static class NvmlGpu
{
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    static extern int GetHandle(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    static extern int GetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    static extern int GetClock(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    static extern int GetUtil(IntPtr device, out Utilization util);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    static extern int GetMemory(IntPtr device, out Memory mem);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    static extern int GetPower(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
    static extern int GetFan(IntPtr device, out uint pct);

    // nvml.h: enums usados — NVML_TEMPERATURE_GPU=0, NVML_CLOCK_GRAPHICS=0,
    // NVML_CLOCK_MEM=2. Structs v1 (EntryPoint sem sufixo _v2 onde importa).
    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Mem; }
    [StructLayout(LayoutKind.Sequential)] struct Memory { public ulong Total, Free, Used; }

    static readonly object Lock = new();
    static bool _tried;
    static IntPtr _dev;
    static string _name = "";

    // null = NVML indisponível (dll ausente / sem GPU NVIDIA / init falhou) —
    // o chamador cai pro caminho nvidia-smi. Init roda UMA vez e fica cacheado;
    // cada leitura depois disso é uma chamada de driver em microssegundos.
    public static GpuReader.GpuInfo? TryRead()
    {
        lock (Lock)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    if (Init() == 0 && GetHandle(0, out _dev) == 0)
                    {
                        var sb = new StringBuilder(96);
                        if (GetName(_dev, sb, 96) == 0) _name = sb.ToString();
                    }
                    else _dev = IntPtr.Zero;
                }
                catch { _dev = IntPtr.Zero; } // DllNotFoundException etc.
            }
            if (_dev == IntPtr.Zero) return null;

            try
            {
                int? temp = GetTemperature(_dev, 0, out var t) == 0 ? (int)t : null;
                int? core = GetClock(_dev, 0, out var c) == 0 ? (int)c : null;
                int? mem  = GetClock(_dev, 2, out var m) == 0 ? (int)m : null;
                int? util = GetUtil(_dev, out var u) == 0 ? (int)u.Gpu : null;
                int? usedMb = null, totalMb = null;
                if (GetMemory(_dev, out var mi) == 0)
                {
                    usedMb  = (int)(mi.Used / 1048576);
                    totalMb = (int)(mi.Total / 1048576);
                }
                int? power = GetPower(_dev, out var pw) == 0 ? (int)Math.Round(pw / 1000.0) : null;
                int? fan   = GetFan(_dev, out var f) == 0 ? (int)f : null;

                return new GpuReader.GpuInfo(
                    Available: true, Name: _name, TempC: temp, CoreMhz: core, MemMhz: mem,
                    UtilPct: util, MemUsedMb: usedMb, MemTotalMb: totalMb,
                    PowerW: power, FanPct: fan);
            }
            catch { return null; }
        }
    }
}
