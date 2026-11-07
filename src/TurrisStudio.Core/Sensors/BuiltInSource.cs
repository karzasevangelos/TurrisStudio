using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace TurrisStudio.Core.Sensors;

/// <summary>
/// Built-in hardware sensors (LibreHardwareMonitor): CPU/GPU temperatures, clocks, load and power,
/// memory, motherboard and drives, without HWiNFO. CPU temperatures need the app to run as
/// administrator (the PawnIO driver it uses is installed by the setup).
/// </summary>
public sealed class BuiltInSource : ISensorSource, IDisposable
{
    private readonly object _lock = new();
    private Computer? _computer;
    private bool _openFailed;
    private Task? _opening;

    public string Name => "Built-in";
    public bool IsAvailable { get; private set; }
    public string? Status { get; private set; } = "Starting...";

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Poll(Dictionary<string, SensorReading> into)
    {
        Computer? computer;
        lock (_lock)
        {
            if (_computer == null)
            {
                // Opening scans all hardware and can take a few seconds: do it off the polling thread.
                if (!_openFailed) _opening ??= Task.Run(Open);
                return;
            }
            computer = _computer;
        }

        var count = 0;
        try
        {
            foreach (var hardware in computer.Hardware)
                count += Read(hardware, GroupName(hardware), into);
        }
        catch (Exception ex)
        {
            Status = "Sensor read failed: " + ex.Message;
            return;
        }
        IsAvailable = count > 0;
        Status = IsElevated
            ? $"{count} readings"
            : $"{count} readings (CPU temperature needs administrator rights)";
    }

    private void Open()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                // The cooler itself is a USB device: never probe controllers/PSUs over HID.
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };
            computer.Open();
            lock (_lock) _computer = computer;
        }
        catch (Exception ex)
        {
            _openFailed = true;
            Status = "Built-in sensors are unavailable: " + ex.Message;
        }
    }

    private static int Read(IHardware hardware, string group, Dictionary<string, SensorReading> into)
    {
        hardware.Update();
        var count = 0;
        var coreClocks = new List<double>();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } v || float.IsNaN(v)) continue;
            var unit = UnitOf(sensor.SensorType);
            if (unit == null) continue;
            var key = $"lhm:{sensor.Identifier}";
            into[key] = new SensorReading(key, "Built-in", group, sensor.Name, unit, v);
            count++;
            if (hardware.HardwareType == HardwareType.Cpu && sensor.SensorType == SensorType.Clock &&
                sensor.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) && v > 0)
                coreClocks.Add(v);
        }
        if (coreClocks.Count > 0)
        {
            var key = $"lhm:{hardware.Identifier}/clock/average";
            into[key] = new SensorReading(key, "Built-in", group, "Cores (Average)", "MHz", coreClocks.Average());
        }
        foreach (var sub in hardware.SubHardware)
            count += Read(sub, group, into);
        return count;
    }

    private static string GroupName(IHardware hardware)
    {
        var type = hardware.HardwareType.ToString();
        var prefix = type switch
        {
            "Cpu" => "CPU",
            "GpuAmd" or "GpuNvidia" => IsIntegrated(hardware) ? "iGPU" : "GPU",
            "GpuIntel" => "iGPU",
            "Memory" => "Memory",
            "Motherboard" or "SuperIO" => "Motherboard",
            "Storage" => "Drive",
            _ => type,
        };
        return prefix == hardware.Name ? prefix : $"{prefix}: {hardware.Name}";
    }

    /// <summary>APUs report as AMD GPUs too; a small dedicated memory pool gives them away.</summary>
    private static bool IsIntegrated(IHardware hardware)
    {
        var total = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData &&
                                                         s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))?.Value;
        if (total is > 0 and < 2048) return true;
        var name = hardware.Name;
        return name.EndsWith("(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase);
    }

    private static string? UnitOf(SensorType type) => type.ToString() switch
    {
        "Temperature" => "°C",
        "Load" or "Control" or "Level" or "Humidity" => "%",
        "Clock" => "MHz",
        "Power" => "W",
        "Voltage" => "V",
        "Current" => "A",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "B/s",
        "Frequency" => "Hz",
        "Noise" => "dBA",
        _ => null,
    };

    public void Dispose()
    {
        lock (_lock)
        {
            try { _computer?.Close(); } catch (Exception) { /* closing is best effort */ }
            _computer = null;
        }
    }
}
