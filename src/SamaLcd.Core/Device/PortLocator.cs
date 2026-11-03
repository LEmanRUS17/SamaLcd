using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// Restarts the panel's USB devices, like unplugging it: a stuck USB serial function stays listed but every
    /// open fails with "A device attached to the system is not functioning". Needs administrator rights.
    /// Returns false when there was nothing to restart or Windows refused.
    /// </summary>
    public static bool RestartUsb(Action<string>? log = null)
    {
        // the whole composite device when there is one, otherwise the port itself
        var ids = new List<string>();
        using (var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\%'"))
        {
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"] as string ?? "";
                if (AwakeIds.Concat(SleepingIds).Any(v => id.Contains(v, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
            }
        }
        // interfaces (…&MI_00) restart together with their composite device
        var targets = ids.Where(id => !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0) targets = ids;
        if (targets.Count == 0) return false;

        bool ok = false;
        foreach (var id in targets)
        {
            log?.Invoke($"Restarting USB device {id}");
            if (CM_Locate_DevNodeW(out var dev, id, 0) != 0 || CM_Get_Parent(out var hub, dev, 0) != 0)
            {
                log?.Invoke("  device not found");
                continue;
            }
            // remove the device like "safely remove hardware", then let the hub find it again (pnputil reports success even when denied)
            var veto = new StringBuilder(260);
            int cr = CM_Query_And_Remove_SubTreeW(dev, out var vetoType, veto, veto.Capacity, CM_REMOVE_UI_NOT_OK);
            int again = CM_Reenumerate_DevNode(hub, CM_REENUMERATE_SYNCHRONOUS);
            log?.Invoke($"  remove: {Describe(cr)}{(vetoType != 0 ? $" (vetoed by {veto})" : "")}, reenumerate: {Describe(again)}");
            if (cr == 0) ok = true;
        }
        return ok;
    }

    private static string Describe(int cr) => cr switch { 0 => "ok", 0x33 => "access denied", 0x17 => "vetoed", _ => $"CR 0x{cr:X}" };

    private const int CM_REMOVE_UI_NOT_OK = 0x1, CM_REENUMERATE_SYNCHRONOUS = 0x1;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Query_And_Remove_SubTreeW(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint devInst, int flags);

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
