using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TurrisStudio.Core.Sensors;

namespace TurrisStudio.Controls;

/// <summary>Button showing the chosen sensor; opens a searchable list of every available reading.</summary>
public sealed class SensorPicker : Grid
{
    private readonly Button _button = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6) };
    private readonly TextBlock _buttonText = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Popup _popup;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _list = new() { Height = 380 };
    private List<SensorReading> _all = [];

    public SensorHub? Hub { get; set; }

    /// <summary>Raised with (key, display name) when the user picks a sensor.</summary>
    public event Action<string, string>? Picked;

    public SensorPicker()
    {
        _button.Content = _buttonText;
        _button.Click += (_, _) => Open();
        Children.Add(_button);

        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.MouseDoubleClick += (_, _) => Commit();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; (_list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); }
            if (e.Key == Key.Enter) { if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0; Commit(); }
        };

        var hint = new TextBlock { Text = "Type to search, double-click to choose", Opacity = 0.6, FontSize = 11, Margin = new Thickness(2, 0, 0, 6) };
        var panel = new StackPanel { Margin = new Thickness(10), Width = 460 };
        panel.Children.Add(hint);
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        _popup = new Popup
        {
            PlacementTarget = _button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 42)), CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1), Child = panel,
            },
        };
        Children.Add(_popup);
        SetDisplay("Choose a sensor...");
    }

    public void SetDisplay(string text) => _buttonText.Text = string.IsNullOrWhiteSpace(text) ? "Choose a sensor..." : text;

    private void Open()
    {
        _all = Hub?.Snapshot.Values
            .OrderBy(r => r.Source == "Automatic" ? 0 : r.Source == "Windows" ? 1 : 2)
            .ThenBy(r => r.Group).ThenBy(r => r.Name).ToList() ?? [];
        _search.Text = "";
        Filter();
        _popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => _search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Filter()
    {
        var terms = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _list.ItemsSource = _all
            .Where(r => terms.All(t => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       r.Group.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       r.Unit.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(400)
            .Select(r => new ListBoxItem
            {
                Tag = r,
                Content = new DockPanel
                {
                    Children =
                    {
                        Docked(new TextBlock { Text = $"{r.Value:0.##} {r.Unit}", Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0) }, Dock.Right),
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                                new TextBlock { Text = r.Group, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis },
                            },
                        },
                    },
                },
            }).ToList();
    }

    private static UIElement Docked(UIElement e, Dock dock)
    {
        DockPanel.SetDock(e, dock);
        return e;
    }

    private void Commit()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: SensorReading r }) return;
        _popup.IsOpen = false;
        var display = r.Source == "Automatic" ? $"{r.Name} (auto)" : $"{r.Name} · {r.Group}";
        SetDisplay(display);
        Picked?.Invoke(r.Key, display);
    }
}
