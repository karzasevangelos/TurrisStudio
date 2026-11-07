using System.Text.Json;
using System.Text.Json.Serialization;
using TurrisStudio.Core.Media;
using TurrisStudio.Core.Overlay;
using TurrisStudio.Core.Protocol;

namespace TurrisStudio.Core.Config;

public sealed class MediaLibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string? ThumbnailPath { get; set; }
    public MediaKind Kind { get; set; }
    /// <summary>Files on the device per converted size ("1280x720", "1440x720", "720x720" ...).</summary>
    public Dictionary<string, string> Uploads { get; set; } = new();

    /// <summary>The size the app currently converts for (connected model + screen mode).</summary>
    public static string ActiveSizeKey { get; set; } = "1280x720";

    /// <summary>File name on the device for the active size, once uploaded.</summary>
    [JsonIgnore]
    public string? RemoteName
    {
        get => Uploads.GetValueOrDefault(ActiveSizeKey);
        set
        {
            if (value == null) Uploads.Remove(ActiveSizeKey);
            else Uploads[SizeKeyOf(value) ?? ActiveSizeKey] = value;
        }
    }

    /// <summary>Settings files from before multi-model support stored a single Turris name.</summary>
    [JsonPropertyName("RemoteName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRemoteName
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value)) Uploads[SizeKeyOf(value) ?? "1280x720"] = value; }
    }

    public bool IsUploaded(string remoteName) => Uploads.ContainsValue(remoteName);

    /// <summary>"2026-11-06_23-14-02-144.mp4.h264_1440x720" → "1440x720".</summary>
    public static string? SizeKeyOf(string remoteName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(remoteName, @"(\d{3,4})x(\d{3,4})");
        return m.Success ? $"{m.Groups[1].Value}x{m.Groups[2].Value}" : null;
    }
    public DateTime Added { get; set; } = DateTime.Now;
    public FitMode Fit { get; set; } = FitMode.Fill;
    public int Rotation { get; set; }
    /// <summary>Small looping MP4 used to preview the media inside the app.</summary>
    public string? PreviewPath { get; set; }
}

/// <summary>Everything the app remembers between runs.</summary>
public sealed class AppSettings
{
    public DisplayState Display { get; set; } = new();
    public List<MediaLibraryItem> Library { get; set; } = [];
    public List<OverlayDesign> Designs { get; set; } = [];
    public string? ActiveDesign { get; set; }
    public bool OverlayEnabled { get; set; } = true;
    public int UpdateIntervalMs { get; set; } = 1000;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string? FfmpegFolder { get; set; }
    public string Theme { get; set; } = "Dark";
    /// <summary>Last connected cooler model (KANALI code: TURR, HICU ...); drives the preview size.</summary>
    public string ModelCode { get; set; } = "TURR";
    /// <summary>Prefer the built-in sensor reader even when HWiNFO shared memory is available.</summary>
    public bool PreferBuiltInSensors { get; set; }

    [JsonIgnore]
    public OverlayDesign Design
    {
        get
        {
            var design = Designs.FirstOrDefault(d => d.Name == ActiveDesign) ?? Designs.FirstOrDefault();
            if (design == null)
            {
                var model = DeviceModel.ForCode(ModelCode);
                design = WidgetPresets.Default(model.Width, model.Height);
                Designs.Add(design);
            }
            ActiveDesign = design.Name;
            return design;
        }
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurrisStudio");

    public static string MediaFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurrisStudio", "media");

    public string FilePath { get; } = Path.Combine(AppDataFolder, "settings.json");
    public AppSettings Settings { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Keep a copy of a broken file instead of losing it.
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { /* ignored */ }
            Settings = new AppSettings();
        }
        _ = Settings.Design; // ensure one design exists
        foreach (var d in Settings.Designs) Migrate(d);
        return Settings;
    }

    /// <summary>When set (developer screenshot mode) nothing is written to disk.</summary>
    public bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly) return;
        lock (_lock)
        {
            Directory.CreateDirectory(AppDataFolder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Options));
            File.Move(tmp, FilePath, true);
        }
    }

    /// <summary>Upgrades older designs: unit labels follow their widget's sensor automatically.</summary>
    public static void Migrate(OverlayDesign design)
    {
        foreach (var w in design.Widgets)
        {
            var value = w.Labels.FirstOrDefault(l => l.Source == LabelSource.Sensor);
            if (value == null) continue;
            var oldStyle = false;
            foreach (var l in w.Labels.Where(l => l.Source == LabelSource.Text && l.Name == "Unit"))
            {
                l.Source = LabelSource.Unit;
                oldStyle = true;
            }
            // Old designs kept the title separately from the widget name the user typed; the name wins.
            if (oldStyle && w.Labels.FirstOrDefault(l => l.Source == LabelSource.Text && l.Name == "Title") is { } title &&
                !string.IsNullOrWhiteSpace(w.Name) && !string.Equals(title.Text, w.Name, StringComparison.OrdinalIgnoreCase))
                title.Text = w.Name.ToUpperInvariant();
            foreach (var l in w.Labels.Where(l => l.Source == LabelSource.Unit)) l.SensorKey = value.SensorKey;
        }
    }

    public static string ExportDesign(OverlayDesign design) => JsonSerializer.Serialize(design, Options);
    public static OverlayDesign? ImportDesign(string json) => JsonSerializer.Deserialize<OverlayDesign>(json, Options);
}
