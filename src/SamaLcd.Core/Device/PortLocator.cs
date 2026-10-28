using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace SamaLcd.Core.Device;

public sealed record SerialPortInfo(string Port, string PnpId, string Name)
{
    public bool Matches(string vidPid) => PnpId.Contains(vidPid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds the panel's COM port and wakes it up if it is in its boot state.</summary>
public static partial class PortLocator
{
    /// <summary>USB IDs of an awake panel (the 2.1" SAMA screen shows up as 1D6B:0121).</summary>
    public static readonly string[] AwakeIds =
    [
        "VID_1D6B&PID_0121", "VID_1D6B&PID_0106", "VID_1D6B&PID_0124", "VID_1D6B&PID_0127",
        "VID_1D6B&PID_0135", "VID_1D6B&PID_0221", "VID_1D6B&PID_0321", "VID_0525&PID_A4A7",
    ];

    /// <summary>USB ID / serial of a panel that still has to be woken up by opening its port.</summary>
    public static readonly string[] SleepingIds = ["VID_1A86&PID_CA21", "CT21INCH", "USB7INCH", "CT88INCH"];

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComName();

    public static IReadOnlyList<SerialPortInfo> ListPorts()
    {
        var result = new List<SerialPortInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (ManagementObject o in searcher.Get())
        {
            var name = o["Name"] as string ?? "";
            var m = ComName().Match(name);
            if (m.Success) result.Add(new SerialPortInfo(m.Groups[1].Value, o["PNPDeviceID"] as string ?? "", name));
        }
        return result;
    }

    public static SerialPortInfo? FindAwake() =>
        ListPorts().FirstOrDefault(p => AwakeIds.Any(p.Matches));

    /// <summary>Returns the port of an awake panel, waking a sleeping one if needed.</summary>
    public static string? Locate(Action<string>? log = null, int wakeAttempts = 15)
    {
        var awake = FindAwake();
        if (awake != null) return awake.Port;

        for (int attempt = 0; attempt < wakeAttempts; attempt++)
        {
            var sleeping = ListPorts().Where(p => SleepingIds.Any(p.Matches)).ToList();
            if (sleeping.Count == 0) return null;
            foreach (var p in sleeping)
            {
                log?.Invoke($"Waking panel on {p.Port} ({p.PnpId})");
                try
                {
                    using var sp = new SerialPort(p.Port, 115200) { ReadTimeout = 1000, WriteTimeout = 1000, RtsEnable = true, DtrEnable = true };
                    sp.Open();
                    Thread.Sleep(100);
                    sp.Close();
                }
                catch (Exception e)
                {
                    log?.Invoke($"  {e.Message}");
                }
            }
            Thread.Sleep(1000);
            awake = FindAwake();
            if (awake != null)
            {
                Thread.Sleep(1000);
                return awake.Port;
            }
        }
        return null;
    }
}
