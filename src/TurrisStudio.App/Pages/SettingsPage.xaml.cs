using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TurrisStudio.Core.Config;
using TurrisStudio.Core.Media;

namespace TurrisStudio.Pages;

public partial class SettingsPage : UserControl
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppServices.Settings;
        StartupToggle.IsChecked = s.StartWithWindows;
        MinimizedToggle.IsChecked = s.StartMinimized;
        TrayToggle.IsChecked = s.MinimizeToTray;
        FfmpegBox.Text = s.FfmpegFolder ?? "";
        PathsText.Text = $"Settings: {AppServices.Store.FilePath}\nMedia cache: {SettingsStore.MediaFolder}";
        BuiltInToggle.IsChecked = s.PreferBuiltInSensors;
        VersionText.Text = $"Turris Studio {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        _loading = false;
        UpdateFfmpegStatus();
        UpdateSensorStatus();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => UpdateSensorStatus();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }

    private void UpdateSensorStatus()
    {
        var hub = AppServices.Device.Sensors;
        var admin = Core.Sensors.BuiltInSource.IsElevated
            ? "Running with administrator rights, so CPU temperature, clock and power are available."
            : "Not running as administrator: CPU temperature, clock and power come from HWiNFO if it's running. Start the app from its Start-menu shortcut (or with Windows) to give it full access.";
        var hwinfo = hub.HwInfo.IsAvailable ? hub.HwInfo.Status : $"not in use ({hub.HwInfo.Status})";
        SensorStatus.Text = $"Built-in: {hub.BuiltIn.Status}{Environment.NewLine}HWiNFO: {hwinfo}{Environment.NewLine}{admin}";
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppServices.Settings;
        s.StartWithWindows = StartupToggle.IsChecked == true;
        s.StartMinimized = MinimizedToggle.IsChecked == true;
        s.MinimizeToTray = TrayToggle.IsChecked == true;
        s.PreferBuiltInSensors = BuiltInToggle.IsChecked == true;
        AppServices.Device.Sensors.PreferBuiltIn = s.PreferBuiltInSensors;
        App.ApplyStartWithWindows(s.StartWithWindows);
        AppServices.Store.Save();
    }

    private void FfmpegBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.FfmpegFolder = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text.Trim();
        AppServices.SaveSoon();
        UpdateFfmpegStatus();
    }

    private void UpdateFfmpegStatus()
    {
        var folder = AppServices.Settings.FfmpegFolder;
        MediaConverter.FfmpegPath = folder != null ? Path.Combine(folder, "ffmpeg.exe") : null;
        MediaConverter.FfprobePath = folder != null ? Path.Combine(folder, "ffprobe.exe") : null;
        try
        {
            FfmpegStatus.Text = "Found: " + MediaConverter.ResolveTool("ffmpeg");
        }
        catch (FileNotFoundException ex)
        {
            FfmpegStatus.Text = ex.Message;
        }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder containing ffmpeg.exe and ffprobe.exe" };
        if (dialog.ShowDialog() == true) FfmpegBox.Text = dialog.FolderName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.AppDataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.AppDataFolder) { UseShellExecute = true });
    }
}
