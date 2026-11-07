using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TurrisStudio.Core.Service;
using TurrisStudio.Pages;
using Wpf.Ui.Controls;

namespace TurrisStudio;

public partial class MainWindow : FluentWindow
{
    private static MainWindow? _current;
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        _current = this;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.IsOpen = false; };
        AppServices.Device.StateChanged += OnDeviceStateChanged;
        Closed += (_, _) =>
        {
            AppServices.Device.StateChanged -= OnDeviceStateChanged;
            foreach (var page in _pages.Values) (page as IDisposable)?.Dispose();
            if (_current == this) _current = null;
        };
        OnDeviceStateChanged();
        Nav.SelectedIndex = 0;
    }

    public void Navigate(string tag)
    {
        foreach (ListBoxItem item in Nav.Items)
            if ((string)item.Tag == tag) { Nav.SelectedItem = item; return; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "media" => new MediaPage(),
                "designer" => new DesignerPage(),
                "display" => new DisplayPage(),
                "sensors" => new SensorsPage(),
                "settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[tag] = page;
        }
        PageHost.Content = page;
    }

    private void OnDeviceStateChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var device = AppServices.Device;
            (StatusTitle.Text, StatusDot.Fill) = device.State switch
            {
                ConnectionState.Connected => ("Connected", (Brush)Brushes.LimeGreen),
                ConnectionState.Busy => ("Working...", Brushes.DeepSkyBlue),
                ConnectionState.Connecting => ("Connecting", Brushes.Orange),
                ConnectionState.Error => ("Not connected", Brushes.IndianRed),
                _ => ("Searching", Brushes.Orange),
            };
            StatusDetail.Text = device.State == ConnectionState.Connected && device.Device is { } d
                ? $"{d.Product}  ·  fw {d.Firmware}"
                : device.StatusText;
        });
    }

    /// <summary>Small notification at the bottom of the window.</summary>
    public static void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        var window = _current;
        if (window == null) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            window.Toast.Title = title;
            window.Toast.Message = message;
            window.Toast.Severity = severity;
            window.Toast.IsOpen = true;
            window._toastTimer.Stop();
            window._toastTimer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 10 : 5);
            window._toastTimer.Start();
        });
    }
}
