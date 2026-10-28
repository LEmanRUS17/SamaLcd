using LibreHardwareMonitor.Hardware;

namespace SamaLcd.Core.Sensors;

public sealed record SensorReading(string Id, string Name, string Hardware, string Type, float? Value);

/// <summary>A source of named values that themes can reference, e.g. "cpu.temp".</summary>
public interface ISensorSource : IDisposable
{
    void Update();
    /// <summary>Returns the value for <paramref name="key"/> or null if this source does not know it.</summary>
    float? Get(string key);
    IEnumerable<SensorReading> List();
}

/// <summary>Combines several sensor sources; the first one that knows a key wins.</summary>
public sealed class SensorHub : IDisposable
{
    private readonly List<ISensorSource> _sources = [];

    public void Add(ISensorSource source) => _sources.Add(source);

    public static SensorHub CreateDefault()
    {
        var hub = new SensorHub();
        hub.Add(new HardwareMonitorSource());
        return hub;
    }

    public void Update()
    {
        foreach (var s in _sources)
        {
            try { s.Update(); }
            catch { /* a failing source must not stop the display */ }
        }
    }

    public float? Get(string key)
    {
        foreach (var s in _sources)
        {
            var v = s.Get(key);
            if (v.HasValue) return v;
        }
        return null;
    }

    public IEnumerable<SensorReading> List() => _sources.SelectMany(s => s.List());

    /// <summary>CPU temperature cannot be read (PawnIO driver missing or not running as administrator).</summary>
    public bool CpuSensorsUnavailable => _sources.OfType<HardwareMonitorSource>().Any(s => s.CpuSensorsUnavailable);

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
    }
}

/// <summary>
/// Sensors from LibreHardwareMonitor. Keys: short aliases (cpu.temp, cpu.load, gpu.temp, ram.load, ...)
/// or any raw sensor identifier such as "/amdcpu/0/temperature/2" (see "samalcd-cli sensors").
/// CPU temperatures require the process to run as administrator.
/// </summary>
public sealed class HardwareMonitorSource : ISensorSource
{
    private readonly Computer _computer;
    private readonly Dictionary<string, ISensor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<float?>> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public HardwareMonitorSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,
        };
        _computer.Open();
        Update();
        BuildAliases();
    }

    public void Update()
    {
        _byId.Clear();
        foreach (var hw in _computer.Hardware) Visit(hw);
    }

    private void Visit(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors) _byId[s.Identifier.ToString()] = s;
        foreach (var sub in hw.SubHardware) Visit(sub);
    }

    /// <summary>
    /// Picks the first sensor of <paramref name="type"/> on hardware accepted by <paramref name="hw"/> whose name
    /// matches one of <paramref name="names"/> (exact match first, then substring). No blind fallback:
    /// an alias that matches nothing yields null ("--") rather than an unrelated sensor.
    /// </summary>
    private Func<float?> Pick(Func<IHardware, bool> hw, SensorType type, params string[] names) => () =>
    {
        var list = _byId.Values.Where(s => s.SensorType == type && hw(s.Hardware)).ToList();
        foreach (var n in names)
        {
            var s = list.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (s != null) return Clean(s);
        }
        return null;
    };

    /// <summary>Without a working driver LibreHardwareMonitor reports 0 for temperatures, clocks and power: show "--" instead.</summary>
    private static float? Clean(ISensor s) =>
        s.Value is 0 && s.SensorType is SensorType.Temperature or SensorType.Clock or SensorType.Power ? null : s.Value;

    /// <summary>
    /// The GPU used for the gpu.* aliases: a discrete card beats an integrated one
    /// (e.g. RTX 4070 Ti over the Ryzen's "AMD Radeon(TM) Graphics").
    /// </summary>
    private IHardware? PrimaryGpu() => _computer.Hardware
        .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        .OrderBy(h => h.HardwareType switch
        {
            HardwareType.GpuNvidia => 0,
            HardwareType.GpuAmd when !IsIntegrated(h) => 1,
            HardwareType.GpuIntel when h.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) => 2,
            _ => 3,
        })
        .FirstOrDefault();

    private static bool IsIntegrated(IHardware h) =>
        h.Name.EndsWith("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
        h.Name.EndsWith("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
        h.Name.Contains("Vega", StringComparison.OrdinalIgnoreCase) && h.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);

    private void BuildAliases()
    {
        Func<IHardware, bool> cpu = h => h.HardwareType == HardwareType.Cpu;
        Func<IHardware, bool> gpu = h => h == PrimaryGpu();
        // "Total Memory" (/ram) is physical RAM; "Virtual Memory" (/vram) includes the page file.
        Func<IHardware, bool> ram = h => h.HardwareType == HardwareType.Memory && h.Identifier.ToString() == "/ram";
        Func<IHardware, bool> board = h => h.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.Cooler or HardwareType.EmbeddedController;

        _aliases["cpu.temp"] = Pick(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tdie", "Core Average", "CCD1 (Tdie)");
        _aliases["cpu.load"] = Pick(cpu, SensorType.Load, "CPU Total");
        _aliases["cpu.clock"] = () =>
            Pick(cpu, SensorType.Clock, "Cores (Average)")() ??
            _byId.Values.Where(s => cpu(s.Hardware) && s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.Ordinal))
                .Select(Clean).Max();
        _aliases["cpu.power"] = Pick(cpu, SensorType.Power, "Package", "CPU Package");
        _aliases["gpu.temp"] = Pick(gpu, SensorType.Temperature, "GPU Core");
        _aliases["gpu.hotspot"] = Pick(gpu, SensorType.Temperature, "GPU Hot Spot");
        _aliases["gpu.load"] = Pick(gpu, SensorType.Load, "GPU Core");
        _aliases["gpu.clock"] = Pick(gpu, SensorType.Clock, "GPU Core");
        _aliases["gpu.power"] = Pick(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        _aliases["gpu.mem.load"] = Pick(gpu, SensorType.Load, "GPU Memory");
        _aliases["gpu.mem.used"] = Pick(gpu, SensorType.SmallData, "GPU Memory Used");
        _aliases["gpu.fan"] = Pick(gpu, SensorType.Fan, "GPU Fan");
        _aliases["ram.load"] = Pick(ram, SensorType.Load, "Memory");
        _aliases["ram.used"] = Pick(ram, SensorType.Data, "Memory Used");
        _aliases["ram.free"] = Pick(ram, SensorType.Data, "Memory Available");
        _aliases["board.temp"] = Pick(board, SensorType.Temperature, "System", "Motherboard");
        _aliases["pump.rpm"] = Pick(board, SensorType.Fan, "Pump", "AIO", "CPU Optional", "Fan #2");
        _aliases["fan.rpm"] = Pick(board, SensorType.Fan, "CPU Fan", "Fan #1");
    }

    /// <summary>True when CPU sensors are unreadable, i.e. the PawnIO driver is missing or the process is not elevated.</summary>
    public bool CpuSensorsUnavailable => Get("cpu.temp") is null &&
        _byId.Values.Any(s => s.Hardware.HardwareType == HardwareType.Cpu && s.SensorType == SensorType.Temperature);

    public float? Get(string key)
    {
        if (_aliases.TryGetValue(key, out var f)) return f();
        return _byId.TryGetValue(key, out var s) ? s.Value : null;
    }

    public IEnumerable<SensorReading> List()
    {
        foreach (var a in _aliases.Keys) yield return new SensorReading(a, "alias", "", "", Get(a));
        foreach (var s in _byId.Values.OrderBy(s => s.Identifier.ToString()))
            yield return new SensorReading(s.Identifier.ToString(), s.Name, s.Hardware.Name, s.SensorType.ToString(), s.Value);
    }

    public void Dispose() => _computer.Close();
}
