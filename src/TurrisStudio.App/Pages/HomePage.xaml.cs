using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TurrisStudio.Core.Service;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace TurrisStudio.Pages;

public partial class HomePage : UserControl, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(string key, TextBlock value, TextBlock unit)> _tiles = [];
    private bool _loading;

    private static readonly (string key, string title, SymbolRegular icon)[] TileDefs =
    [
        ("auto:cpu_temp", "CPU temperature", SymbolRegular.Temperature24),
        ("auto:gpu_temp", "GPU temperature", SymbolRegular.Temperature24),
        ("auto:cpu_usage", "CPU usage", SymbolRegular.DeveloperBoard24),
        ("auto:ram_usage", "Memory usage", SymbolRegular.Storage24),
    ];

    public HomePage()
    {
        InitializeComponent();
        Preview.Sensors = AppServices.Device.Sensors;
        foreach (var (key, title, icon) in TileDefs) Tiles.Children.Add(CreateTile(key, title, icon));

        _loading = true;
        OverlayToggle.IsChecked = AppServices.Settings.OverlayEnabled;
        BrightnessSlider.Value = AppServices.Settings.Display.Brightness;
        _loading = false;

        LogBox.Text = string.Join(Environment.NewLine, AppServices.Device.LogLines);
        LogBox.ScrollToEnd();
        AppServices.Device.LogAdded += OnLog;
        AppServices.Device.StateChanged += OnState;
        _timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            var design = AppServices.Settings.Design;
            design.FitTo(UiHelpers.Model.Width, UiHelpers.Model.Height);
            Preview.Design = design;
            PreviewCaption.Text = UiHelpers.CurrentMediaName();
            Refresh();
            OnState();
            _timer.Start();
            await UiHelpers.ShowInPreviewAsync(Preview, null);
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private UIElement CreateTile(string key, string title, SymbolRegular icon)
    {
        var value = new TextBlock { Text = "--", FontSize = 34, FontWeight = FontWeights.SemiBold };
        var unit = new TextBlock { Text = "", FontSize = 16, Margin = new Thickness(4, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.7 };
        _tiles.Add((key, value, unit));
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new SymbolIcon { Symbol = icon, Margin = new Thickness(0, 0, 8, 0), Opacity = 0.8 });
        header.Children.Add(new TextBlock { Text = title, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        valueRow.Children.Add(value);
        valueRow.Children.Add(unit);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(valueRow);
        return new Card { Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 0), Content = stack };
    }

    private void Refresh()
    {
        var sensors = AppServices.Device.Sensors;
        foreach (var (key, value, unit) in _tiles)
        {
            var r = sensors.Get(key);
            value.Text = r == null ? "--" : r.Value.ToString(r.Unit == "%" || r.Unit == "°C" ? "F0" : "F1");
            unit.Text = r?.Unit ?? "";
        }
        Preview.RefreshValues();
        SensorsText.Text = sensors.HwInfo.IsAvailable && !sensors.PreferBuiltIn
            ? "HWiNFO + built-in"
            : sensors.BuiltIn.IsAvailable
                ? (Core.Sensors.BuiltInSource.IsElevated ? "Built-in (full access)" : "Built-in (start from the Start menu for CPU temperature)")
                : "Starting sensors...";
        MediaText.Text = UiHelpers.CurrentMediaName();
        PreviewCaption.Text = MediaText.Text;
    }

    private void OnState()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var d = AppServices.Device;
            DeviceStatus.Text = d.State == ConnectionState.Connected ? "Connected and updating" : d.StatusText;
            DeviceIcon.Symbol = d.IsConnected ? SymbolRegular.PlugConnected24 : SymbolRegular.PlugDisconnected24;
            DeviceIcon.Foreground = d.IsConnected ? Brushes.LimeGreen : Brushes.IndianRed;
            DeviceTitle.Text = (d.Device?.Product is { Length: > 0 } p ? p : UiHelpers.Model.Name).ToUpperInvariant();
            FirmwareText.Text = d.Device != null ? $"{d.Device.Firmware} (app {d.Device.App})" : "-";
            SerialText.Text = d.Device?.Serial is { Length: > 0 } s ? s : "-";
            // A cooler with another screen size was connected: refit the live preview.
            if (Preview.ScreenWidth != UiHelpers.Model.Width || Preview.DualScreen != UiHelpers.Model.SplitScreen)
            {
                AppServices.Settings.Design.FitTo(UiHelpers.Model.Width, UiHelpers.Model.Height);
                _ = UiHelpers.ShowInPreviewAsync(Preview, null);
            }
        });
    }

    private void OnLog(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
        LogBox.ScrollToEnd();
    });

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.OverlayEnabled = OverlayToggle.IsChecked == true;
        AppServices.Store.Save();
        AppServices.Device.InvalidateLayout();
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValue != null) BrightnessValue.Text = $"{(int)e.NewValue}%";
        if (_loading) return;
        var value = (uint)e.NewValue;
        if (AppServices.Settings.Display.Brightness == value) return;
        AppServices.Settings.Display = AppServices.Settings.Display with { Brightness = value };
        AppServices.SaveSoon();
        AppServices.Device.InvalidateDisplay();
    }

    private void BrightnessSlider_Commit(object sender, RoutedEventArgs e) { }

    private void GoMedia_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("media");
    private void GoDesigner_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("designer");

    public void Dispose()
    {
        _timer.Stop();
        AppServices.Device.LogAdded -= OnLog;
        AppServices.Device.StateChanged -= OnState;
    }
}
