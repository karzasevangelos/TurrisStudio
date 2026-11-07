using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TurrisStudio.Pages;

public partial class SensorsPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public sealed record Row(string Name, string Group, string ValueText, string Source);

    public SensorsPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var hub = AppServices.Device.Sensors;
        // Only nag about HWiNFO when the built-in reader can't provide CPU temperatures.
        HwInfoBar.IsOpen = !hub.HwInfo.IsAvailable && !Core.Sensors.BuiltInSource.IsElevated;
        var terms = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = hub.Snapshot.Values
            .Where(r => terms.All(t => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Source == "Automatic" ? 0 : r.Source == "Windows" ? 1 : 2).ThenBy(r => r.Group).ThenBy(r => r.Name)
            .Select(r => new Row(r.Name, r.Group, $"{r.Value:0.##} {r.Unit}", r.Source))
            .ToList();
        SensorList.ItemsSource = rows;
    }
}
