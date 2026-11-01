using System.Diagnostics;
using SamaLcd.Core;

namespace SamaLcd;

internal static class WindowsCrashReport
{
    // Fixed read-only query, independent of user input. Include native crashes that bypass .NET handlers.
    public static string ReadRecent()
    {
        try
        {
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new(); Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1001,1002,1026; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'SamaLcd.exe' } | Select-Object -First 12 TimeCreated,Id,ProviderName,Message | Format-List | Out-String -Width 200" }) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000)) { process.Kill(true); return "Windows event query timed out."; }
            var result = output.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(result) ? "No matching Windows crash events found, or the event log was unavailable.\n" + DiagnosticLog.Clip(error.GetAwaiter().GetResult()) : DiagnosticLog.Clip(result, 65536);
        }
        catch (Exception e) { AppLog.Error("diagnostics.windows_events", e); return "Windows crash events unavailable: " + e.Message; }
    }
}
