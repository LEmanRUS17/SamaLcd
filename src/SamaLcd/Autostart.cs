using System.Diagnostics;

namespace SamaLcd;

/// <summary>
/// Starts the app at logon through Task Scheduler, which (unlike the Run registry key)
/// can launch an elevated program without a UAC prompt. Sama does the same.
/// </summary>
static class Autostart
{
    private const string TaskName = "SamaLcd";

    public static bool IsEnabled => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            var exe = Environment.ProcessPath!;
            int code = Schtasks($"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:10 /TR \"\\\"{exe}\\\" --autostart\"");
            if (code != 0) throw new InvalidOperationException($"schtasks failed with code {code}");
        }
        else if (IsEnabled)
        {
            Schtasks($"/Delete /F /TN \"{TaskName}\"");
        }
    }

    private static int Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        return p.ExitCode;
    }
}
