using System.Security.Principal;

namespace ForgeSentinel;

static class Elevation
{
    // Writing service Start types and power state needs admin. The shipped daemon
    // runs as LocalSystem; this CLI must be launched from an elevated terminal.
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
