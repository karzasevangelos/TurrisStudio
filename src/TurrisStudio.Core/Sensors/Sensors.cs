using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace TurrisStudio.Core.Sensors;

/// <summary>One readable value. Key is stable across restarts, e.g. "hwinfo:CPU [#0]: AMD Ryzen...|CPU (Tctl/Tdie)".</summary>
public sealed record SensorReading(string Key, string Source, string Group, string Name, string Unit, double Value);

public interface ISensorSource
{
    string Name { get; }
    bool IsAvailable { get; }
    void Poll(Dictionary<string, SensorReading> into);
}

/// <summary>Reads every HWiNFO sensor through its shared memory (enable "Shared Memory Support" in HWiNFO).</summary>
public sealed class HwInfoSource : ISensorSource
{
    private const string MapName = @"Global\HWiNFO_SENS_SM2";
    private const uint Signature = 0x53695748; // "HWiS"
    private const int NameLen = 128;
    private const int UnitLen = 16;

    public string Name => "HWiNFO";
    public bool IsAvailable { get; private set; }
    public string? Status { get; private set; }

    public void Poll(Dictionary<string, SensorReading> into)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var signature = view.ReadUInt32(0);
            if (signature != Signature)
            {
                IsAvailable = false;
                Status = signature == 0x44414544 ? "HWiNFO shared memory has expired (restart HWiNFO or re-enable it)." : "HWiNFO shared memory is not ready.";
                return;
            }
            // Header (packed): sig u32, ver u32, rev u32, poll_time i64, then offsets at 20.
            var sensorOffset = view.ReadUInt32(20);
            var sensorSize = view.ReadUInt32(24);
            var sensorCount = view.ReadUInt32(28);
            var readingOffset = view.ReadUInt32(32);
            var readingSize = view.ReadUInt32(36);
            var readingCount = view.ReadUInt32(40);

            var sensorNames = new string[sensorCount];
            var nameBuf = new byte[NameLen];
            for (var i = 0; i < sensorCount; i++)
            {
                long b = sensorOffset + (long)i * sensorSize;
                // id u32, instance u32, nameOrig[128], nameUser[128]
                view.ReadArray(b + 8 + NameLen, nameBuf, 0, NameLen);
                var user = ReadString(nameBuf);
                if (user.Length == 0)
                {
                    view.ReadArray(b + 8, nameBuf, 0, NameLen);
                    user = ReadString(nameBuf);
                }
                sensorNames[i] = user;
            }

            var unitBuf = new byte[UnitLen];
            for (var i = 0; i < readingCount; i++)
            {
                long b = readingOffset + (long)i * readingSize;
                var type = view.ReadUInt32(b);
                if (type == 0) continue; // SENSOR_TYPE_NONE
                var sensorIndex = view.ReadUInt32(b + 4);
                // labelOrig at +12, labelUser at +140, unit at +268, value at +284
                view.ReadArray(b + 12, nameBuf, 0, NameLen);
                var original = ReadString(nameBuf);
                view.ReadArray(b + 12 + NameLen, nameBuf, 0, NameLen);
                var label = ReadString(nameBuf);
                if (label.Length == 0) label = original;
                view.ReadArray(b + 12 + 2 * NameLen, unitBuf, 0, UnitLen);
                var unit = ReadString(unitBuf);
                var value = view.ReadDouble(b + 12 + 2 * NameLen + UnitLen);
                var group = sensorIndex < sensorNames.Length ? sensorNames[sensorIndex] : "Sensor";
                var key = $"hwinfo:{group}|{original}";
                into[key] = new SensorReading(key, Name, group, label, unit, value);
            }
            IsAvailable = true;
            Status = $"{readingCount} readings";
        }
        catch (FileNotFoundException)
        {
            IsAvailable = false;
            Status = "HWiNFO is not running or Shared Memory Support is off.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            IsAvailable = false;
            Status = ex.Message;
        }
    }

    private static string ReadString(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.Latin1.GetString(buffer, 0, end).Trim();
    }
}

/// <summary>Values Windows provides without extra software.</summary>
public sealed class SystemSource : ISensorSource
{
    private ulong _lastIdle, _lastTotal;
    private readonly DateTime _started = DateTime.Now;
    private long _lastNetBytesIn, _lastNetBytesOut;
    private DateTime _lastNetTime;

    public string Name => "Windows";
    public bool IsAvailable => true;

    public void Poll(Dictionary<string, SensorReading> into)
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong idleT = idle, total = kernel + user;
            if (_lastTotal != 0 && total > _lastTotal)
            {
                var usage = 100.0 * (1.0 - (double)(idleT - _lastIdle) / (total - _lastTotal));
                Add(into, "cpu_usage", "CPU", "CPU Usage", "%", Math.Clamp(usage, 0, 100));
            }
            _lastIdle = idleT;
            _lastTotal = total;
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            Add(into, "ram_usage", "Memory", "Memory Usage", "%", mem.dwMemoryLoad);
            Add(into, "ram_used", "Memory", "Memory Used", "GB", (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0);
            Add(into, "ram_total", "Memory", "Memory Total", "GB", mem.ullTotalPhys / 1073741824.0);
        }

        Add(into, "uptime_h", "System", "Uptime", "h", Environment.TickCount64 / 3600000.0);

        try
        {
            long inBytes = 0, outBytes = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var s = nic.GetIPv4Statistics();
                inBytes += s.BytesReceived;
                outBytes += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (_lastNetTime != default)
            {
                var secs = (now - _lastNetTime).TotalSeconds;
                if (secs > 0)
                {
                    Add(into, "net_down", "Network", "Download", "MB/s", Math.Max(0, inBytes - _lastNetBytesIn) / secs / 1048576.0);
                    Add(into, "net_up", "Network", "Upload", "MB/s", Math.Max(0, outBytes - _lastNetBytesOut) / secs / 1048576.0);
                }
            }
            _lastNetBytesIn = inBytes;
            _lastNetBytesOut = outBytes;
            _lastNetTime = now;
        }
        catch (Exception) { /* network stats are optional */ }
    }

    private void Add(Dictionary<string, SensorReading> into, string id, string group, string name, string unit, double value)
    {
        var key = "sys:" + id;
        into[key] = new SensorReading(key, Name, group, name, unit, value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>Polls all sources and keeps the latest snapshot.</summary>
public sealed class SensorHub : IDisposable
{
    private readonly object _lock = new();
    private Dictionary<string, SensorReading> _snapshot = new();

    public HwInfoSource HwInfo { get; } = new();
    public SystemSource System { get; } = new();
    public BuiltInSource BuiltIn { get; } = new();

    /// <summary>Use the built-in reader for "Automatic" sensors even when HWiNFO is running.</summary>
    public bool PreferBuiltIn { get; set; }

    public event Action? Updated;

    public IReadOnlyDictionary<string, SensorReading> Snapshot
    {
        get { lock (_lock) return _snapshot; }
    }

    public void Poll()
    {
        var next = new Dictionary<string, SensorReading>();
        System.Poll(next);
        HwInfo.Poll(next);
        BuiltIn.Poll(next);
        AddAutomatic(next, PreferBuiltIn);
        lock (_lock) _snapshot = next;
        Updated?.Invoke();
    }

    public void Dispose() => BuiltIn.Dispose();

    /// <summary>Virtual "auto:" sensors that pick the best matching reading (HWiNFO first, then built-in).</summary>
    public static readonly (string Key, string Name, string Unit)[] AutomaticSensors =
    [
        ("auto:cpu_temp", "CPU Temperature", "°C"),
        ("auto:cpu_usage", "CPU Usage", "%"),
        ("auto:cpu_clock", "CPU Clock", "MHz"),
        ("auto:cpu_power", "CPU Power", "W"),
        ("auto:gpu_temp", "GPU Temperature", "°C"),
        ("auto:gpu_usage", "GPU Usage", "%"),
        ("auto:gpu_clock", "GPU Clock", "MHz"),
        ("auto:gpu_power", "GPU Power", "W"),
        ("auto:ram_usage", "Memory Usage", "%"),
    ];

    private static void AddAutomatic(Dictionary<string, SensorReading> all, bool preferBuiltIn)
    {
        // Dedicated GPUs ("dGPU [#0]: AMD Radeon...", "GPU: NVIDIA ...") before integrated ones.
        static List<SensorReading> From(Dictionary<string, SensorReading> all, string source) =>
            all.Values.Where(r => r.Source == source)
                .OrderBy(r => r.Group.StartsWith("iGPU", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        var hwinfo = From(all, "HWiNFO");
        var builtIn = From(all, "Built-in");
        var sources = preferBuiltIn ? new[] { builtIn, hwinfo } : new[] { hwinfo, builtIn };

        static SensorReading? FindIn(List<SensorReading> list, Func<SensorReading, bool> filter, string unit, string[] names)
        {
            // Without administrator rights the built-in reader reports 0 for CPU temperature/clock/power: skip those.
            bool groupFilter(SensorReading r) => filter(r) && !(r.Source == "Built-in" && r.Value <= 0 && unit is "°C" or "MHz" or "W");
            foreach (var n in names)
            {
                var match = list.FirstOrDefault(r => groupFilter(r) && r.Unit == unit &&
                                                     r.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            foreach (var n in names)
            {
                var match = list.FirstOrDefault(r => groupFilter(r) && r.Unit == unit &&
                                                     r.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return null;
        }

        SensorReading? Find(Func<SensorReading, bool> groupFilter, string unit, params string[] names)
        {
            foreach (var list in sources)
                if (FindIn(list, groupFilter, unit, names) is { } r) return r;
            return null;
        }

        static bool IsCpu(SensorReading r) => r.Group.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);
        static bool IsGpu(SensorReading r) =>
            r.Group.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ||
            r.Group.StartsWith("dGPU", StringComparison.OrdinalIgnoreCase) ||
            r.Group.StartsWith("iGPU", StringComparison.OrdinalIgnoreCase);
        static bool IsMemory(SensorReading r) =>
            (r.Group.StartsWith("System", StringComparison.OrdinalIgnoreCase) || r.Group.StartsWith("Memory", StringComparison.OrdinalIgnoreCase)) &&
            !r.Group.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        var picks = new Dictionary<string, SensorReading?>
        {
            // HWiNFO names first, then LibreHardwareMonitor names (AMD "Core (Tctl/Tdie)", Intel "CPU Package").
            ["auto:cpu_temp"] = Find(IsCpu, "°C", "CPU (Tctl/Tdie)", "Core (Tctl/Tdie)", "CPU Package", "CPU Die (average)",
                "Core Max", "Core Average", "CCD1 (Tdie)", "CPU"),
            ["auto:cpu_usage"] = Find(IsCpu, "%", "Total CPU Usage", "CPU Total", "Max CPU/Thread Usage") ?? all.GetValueOrDefault("sys:cpu_usage"),
            ["auto:cpu_clock"] = Find(IsCpu, "MHz", "Core Clocks (avg)", "Average Effective Clock", "Cores (Average)", "Core 0 Clock", "Core #1"),
            ["auto:cpu_power"] = Find(IsCpu, "W", "CPU Package Power", "CPU PPT", "CPU Package", "Package", "CPU Power"),
            ["auto:gpu_temp"] = Find(IsGpu, "°C", "GPU Temperature", "GPU Core", "GPU Hot Spot"),
            ["auto:gpu_usage"] = Find(IsGpu, "%", "GPU Core Load", "GPU Utilization", "GPU Core", "GPU D3D Usage", "D3D 3D"),
            ["auto:gpu_clock"] = Find(IsGpu, "MHz", "GPU Clock", "GPU Core", "GPU Effective Clock"),
            ["auto:gpu_power"] = Find(IsGpu, "W", "Total Board Power", "GPU Power", "GPU Package", "GPU Core Power", "GPU Core"),
            ["auto:ram_usage"] = Find(IsMemory, "%", "Physical Memory Load", "Memory") ?? all.GetValueOrDefault("sys:ram_usage"),
        };
        foreach (var (key, name, unit) in AutomaticSensors)
        {
            if (picks.GetValueOrDefault(key) is { } r)
                all[key] = new SensorReading(key, "Automatic", "Automatic", name, r.Unit.Length > 0 ? r.Unit : unit, r.Value);
        }
    }

    public SensorReading? Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_lock) return _snapshot.GetValueOrDefault(key);
    }
}
