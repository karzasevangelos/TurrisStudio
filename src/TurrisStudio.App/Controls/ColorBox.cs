using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TurrisStudio.Controls;

/// <summary>Hex colour input with a swatch that opens a palette.</summary>
public sealed class ColorBox : DockPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(ColorBox),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorBox)d).Sync()));

    private static readonly string[] Palette =
    [
        "#FFFFFF", "#DCDCDC", "#A0A0A0", "#606060", "#303030", "#000000",
        "#FF3B30", "#FF6B35", "#FF9500", "#FFCC00", "#FFE066", "#C8FF00",
        "#34C759", "#00E676", "#00C7BE", "#00E5FF", "#30B0FF", "#007AFF",
        "#5856D6", "#7C4DFF", "#AF52DE", "#FF2D92", "#FF6FB5", "#A92F2C",
        "#CB6236", "#00629A", "#0068B5", "#76B900", "#ED1C24", "#4A4A4A",
    ];

    private readonly Border _swatch;
    private readonly TextBox _text;
    private readonly Popup _popup;
    private bool _syncing;

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ColorBox()
    {
        _swatch = new Border
        {
            Width = 34, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        _text = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        _text.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var t = _text.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && int.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out _))
                Value = t.ToUpperInvariant();
        };

        var grid = new WrapPanel { Width = 6 * 34, Margin = new Thickness(8) };
        foreach (var c in Palette)
        {
            var b = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(3), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1), ToolTip = c,
            };
            b.MouseLeftButtonUp += (_, _) => { Value = c; _popup!.IsOpen = false; };
            grid.Children.Add(b);
        }
        _popup = new Popup
        {
            PlacementTarget = _swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 46)), CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1), Child = grid,
            },
        };
        _swatch.MouseLeftButtonUp += (_, _) => _popup.IsOpen = true;

        SetDock(_swatch, Dock.Left);
        Children.Add(_swatch);
        Children.Add(_text);
        Children.Add(_popup);
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        if (!string.Equals(_text.Text, Value, StringComparison.OrdinalIgnoreCase)) _text.Text = Value;
        _swatch.Background = new SolidColorBrush(ScreenCanvas.ToColor(Value));
        _syncing = false;
    }
}
