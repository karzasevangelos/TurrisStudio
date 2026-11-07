using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TurrisStudio.Core.Overlay;

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum LabelSource { Text, Sensor, Time, Date, Unit }
public enum LabelBackground { None, Color, GradientVertical, GradientHorizontal }
public enum WidgetAlign { Left, Center, Right }

/// <summary>Fonts the Turris firmware ships (from the official app).</summary>
public static class DeviceFonts
{
    public static readonly (string Id, string Display)[] All =
    [
        ("roboto-regular", "Roboto"),
        ("roboto-thin", "Roboto Thin"),
        ("Orbitron-Regular", "Orbitron"),
        ("BitcountSingle-Regular", "Bitcount Single"),
        ("RubikGlitch-Regular", "Rubik Glitch"),
    ];
}

/// <summary>One piece of text inside a widget (maps to a firmware OverlayLabel).</summary>
public sealed class OverlayLabelModel : ObservableBase
{
    private string _name = "Text";
    private LabelSource _source = LabelSource.Text;
    private string _text = "Text";
    private string? _sensorKey;
    private string _sensorDisplay = "";
    private int _decimals;
    private double _multiplier = 1;
    private string _prefix = "";
    private string _suffix = "";
    private bool _appendUnit;
    private string _timeFormat = "HH:mm";
    private int _line;
    private int _gapLeft;
    private string _font = "roboto-regular";
    private int _size = 40;
    private string _color = "#FFFFFF";
    private LabelBackground _background = LabelBackground.None;
    private string _backgroundColor = "#A92F2C";
    private string _gradientColor = "#CB6236";

    public string Name { get => _name; set => Set(ref _name, value); }
    public LabelSource Source { get => _source; set { if (Set(ref _source, value)) OnPropertyChanged(nameof(IsDynamic)); } }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string? SensorKey { get => _sensorKey; set => Set(ref _sensorKey, value); }
    /// <summary>Human-readable sensor name, kept so the UI can show it while the sensor is offline.</summary>
    public string SensorDisplay { get => _sensorDisplay; set => Set(ref _sensorDisplay, value); }
    public int Decimals { get => _decimals; set => Set(ref _decimals, Math.Clamp(value, 0, 4)); }
    public double Multiplier { get => _multiplier; set => Set(ref _multiplier, value); }
    public string Prefix { get => _prefix; set => Set(ref _prefix, value); }
    public string Suffix { get => _suffix; set => Set(ref _suffix, value); }
    public bool AppendUnit { get => _appendUnit; set => Set(ref _appendUnit, value); }
    public string TimeFormat { get => _timeFormat; set => Set(ref _timeFormat, value); }
    /// <summary>Row inside the widget (0 = top).</summary>
    public int Line { get => _line; set => Set(ref _line, Math.Clamp(value, 0, 9)); }
    /// <summary>Horizontal spacing before this label on its row, in pixels.</summary>
    public int GapLeft { get => _gapLeft; set => Set(ref _gapLeft, value); }
    public string Font { get => _font; set => Set(ref _font, value); }
    public int Size { get => _size; set => Set(ref _size, Math.Clamp(value, 6, 400)); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public LabelBackground Background { get => _background; set => Set(ref _background, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string GradientColor { get => _gradientColor; set => Set(ref _gradientColor, value); }

    [JsonIgnore] public bool IsDynamic => Source != LabelSource.Text;

    public OverlayLabelModel Clone() => (OverlayLabelModel)MemberwiseClone();
}

/// <summary>A positioned box of labels (maps to a firmware OverlayGroup).</summary>
public sealed class OverlayWidgetModel : ObservableBase
{
    private string _name = "Widget";
    private int _x = 40, _y = 40, _width = 400, _height = 160;
    private WidgetAlign _align = WidgetAlign.Left;
    private int _lineGap = -10;
    private bool _visible = true;

    public string Name { get => _name; set => Set(ref _name, value); }
    public int X { get => _x; set => Set(ref _x, Math.Clamp(value, 0, 2239)); }
    public int Y { get => _y; set => Set(ref _y, Math.Clamp(value, 0, 1079)); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 10, 2240)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 10, 1080)); }
    public WidgetAlign Align { get => _align; set => Set(ref _align, value); }
    /// <summary>Extra vertical spacing between rows (negative = tighter).</summary>
    public int LineGap { get => _lineGap; set => Set(ref _lineGap, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public ObservableCollection<OverlayLabelModel> Labels { get; set; } = [];

    public OverlayWidgetModel Clone()
    {
        var copy = (OverlayWidgetModel)MemberwiseClone();
        copy.Labels = new ObservableCollection<OverlayLabelModel>(Labels.Select(l => l.Clone()));
        return copy;
    }
}

public sealed class OverlayDesign : ObservableBase
{
    private string _name = "My layout";
    private bool _fahrenheit;

    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Fahrenheit { get => _fahrenheit; set => Set(ref _fahrenheit, value); }
    public ObservableCollection<OverlayWidgetModel> Widgets { get; set; } = [];
    /// <summary>Screen size the widget positions were laid out for.</summary>
    public int CanvasWidth { get; set; } = 1280;
    public int CanvasHeight { get; set; } = 720;

    /// <summary>Rescales widget positions when the design is used on a screen of another size.</summary>
    public void FitTo(int width, int height)
    {
        if (CanvasWidth <= 0 || CanvasHeight <= 0) { CanvasWidth = width; CanvasHeight = height; }
        if (CanvasWidth == width && CanvasHeight == height) return;
        double sx = (double)width / CanvasWidth, sy = (double)height / CanvasHeight;
        foreach (var w in Widgets)
        {
            w.Width = Math.Clamp((int)Math.Round(w.Width * sx), 10, width);
            w.Height = Math.Clamp((int)Math.Round(w.Height * sy), 10, height);
            w.X = Math.Clamp((int)Math.Round(w.X * sx), 0, Math.Max(0, width - w.Width));
            w.Y = Math.Clamp((int)Math.Round(w.Y * sy), 0, Math.Max(0, height - w.Height));
        }
        CanvasWidth = width;
        CanvasHeight = height;
    }
}
