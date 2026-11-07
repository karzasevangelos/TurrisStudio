using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TurrisStudio.Core.Protocol;

namespace TurrisStudio.Pages;

public partial class DisplayPage : UserControl
{
    private bool _loading = true;

    public DisplayPage()
    {
        InitializeComponent();
        var s = AppServices.Settings;
        BrightnessSlider.Value = s.Display.Brightness;
        BrightnessText.Text = $"{s.Display.Brightness}%";
        BacklightToggle.IsChecked = s.Display.BacklightOn;
        MirrorToggle.IsChecked = s.Display.Mirror;
        PlayModeBox.SelectedIndex = (int)s.Display.PlayMode;
        OverlayToggle.IsChecked = s.OverlayEnabled;
        IntervalBox.SelectedItem = IntervalBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => int.Parse((string)i.Tag) == s.UpdateIntervalMs)
                                   ?? IntervalBox.Items[1];
        _loading = false;
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText == null) return;
        BrightnessText.Text = $"{(int)e.NewValue}%";
        Setting_Changed(sender, e);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var playModeChanged = (int)AppServices.Settings.Display.PlayMode != PlayModeBox.SelectedIndex;
        AppServices.Settings.Display = AppServices.Settings.Display with
        {
            Brightness = (uint)BrightnessSlider.Value,
            BacklightOn = BacklightToggle.IsChecked == true,
            Mirror = MirrorToggle.IsChecked == true,
            PlayMode = (PlayMode)Math.Max(0, PlayModeBox.SelectedIndex),
        };
        AppServices.SaveSoon();
        AppServices.Device.InvalidateDisplay(restartPlayback: playModeChanged);
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.OverlayEnabled = OverlayToggle.IsChecked == true;
        AppServices.Store.Save();
        AppServices.Device.InvalidateLayout();
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IntervalBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        AppServices.Settings.UpdateIntervalMs = int.Parse(tag);
        AppServices.SaveSoon();
    }
}
