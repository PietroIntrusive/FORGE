using Microsoft.Win32;

namespace ForgeSentinel;

static class WuDetector
{
    // Windows writes the last successful WU install time to one of these paths
    static readonly string[] RegistryPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WindowsUpdate\Auto Update\Results\Install",
    ];

    public static DateTime? GetLastInstallTime()
    {
        foreach (var path in RegistryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                var val = key?.GetValue("LastSuccessTime")?.ToString();
                if (val != null && DateTime.TryParse(val, out var dt))
                    return dt.ToUniversalTime();
            }
            catch { }
        }
        return null;
    }
}
