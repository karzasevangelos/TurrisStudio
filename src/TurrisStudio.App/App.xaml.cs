using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TurrisStudio.Core.Config;
using TurrisStudio.Core.Service;

namespace TurrisStudio;

public static class AppServices
{
    public static SettingsStore Store { get; } = new();
    public static AppSettings Settings => Store.Settings;
    public static DeviceService Device { get; set; } = null!;

    /// <summary>Debounced save so slider drags etc. don't hammer the disk.</summary>
    private static DispatcherTimer? _saveTimer;

    public static void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Store.Save();
        }, Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TurrisStudio.SingleInstance";
    private const string ShowEventName = @"Local\TurrisStudio.Show";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Created by the installer: starts the app with full rights at sign-in.</summary>
    public const string StartupTaskName = "Turris Studio";
    /// <summary>Created by the installer: opens the app with full rights on demand (no UAC prompt).</summary>
    public const string LauncherTaskName = "Turris Studio Launcher";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshotMode = e.Args.Contains("--screenshot");
        var startedByWindows = e.Args.Contains("--minimized");
        var isFirst = TryCreateInstanceMutex(out _mutex);
        if (!isFirst && !screenshotMode)
        {
            // Already running (e.g. started with Windows): bring that window forward, unless this is
            // a second autostart entry, which should stay silent.
            if (!startedByWindows)
            {
                // A normal-rights process can't signal the elevated copy (Windows integrity levels), so ask the
                // elevated launcher task to start a copy that can; it signals the running one and exits.
                if (!Core.Sensors.BuiltInSource.IsElevated && !e.Args.Contains("--via-task") && TaskExists(LauncherTaskName))
                    RunTask(LauncherTaskName);
                else
                    try { EventWaitHandleAcl.OpenExisting(ShowEventName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize).Set(); } catch { /* ignored */ }
            }
            Shutdown();
            return;
        }
        // Without administrator rights CPU temperatures are unavailable: reopen through the installer's
        // elevated task, which needs no UAC prompt. --via-task prevents loops for standard users.
        if (!screenshotMode && !e.Args.Contains("--via-task") && !Core.Sensors.BuiltInSource.IsElevated && TaskExists(LauncherTaskName))
        {
            _mutex?.Dispose();
            _mutex = null;
            if (RunTask(startedByWindows ? StartupTaskName : LauncherTaskName))
            {
                Shutdown();
                return;
            }
            isFirst = TryCreateInstanceMutex(out _mutex);
            if (!isFirst) { Shutdown(); return; }
        }
        _showEvent = CreateShowEvent(screenshotMode ? ShowEventName + ".shot" : ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.Invoke(ShowMainWindow);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "Turris Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppServices.Store.Load();
        if (screenshotMode)
        {
            // Developer aid: never write the user's settings; optionally simulate another cooler.
            AppServices.Store.ReadOnly = true;
            var mi = Array.IndexOf(e.Args, "--model");
            if (mi >= 0 && mi + 1 < e.Args.Length) AppServices.Settings.ModelCode = e.Args[mi + 1];
            if (e.Args.Contains("--split")) AppServices.Settings.Display = AppServices.Settings.Display with { SplitScreen = true };
        }
        RemoveRunKeyIfTaskInstalled();
        if (AppServices.Settings.FfmpegFolder is { Length: > 0 } ff)
        {
            TurrisStudio.Core.Media.MediaConverter.FfmpegPath = Path.Combine(ff, "ffmpeg.exe");
            TurrisStudio.Core.Media.MediaConverter.FfprobePath = Path.Combine(ff, "ffprobe.exe");
        }
        ApplyStartWithWindows(AppServices.Settings.StartWithWindows);

        AppServices.Device = new DeviceService(AppServices.Store);
        AppServices.Device.Sensors.PreferBuiltIn = AppServices.Settings.PreferBuiltInSensors;
        FitDesignToModel();
        AppServices.Device.ModelChanged += () => Dispatcher.BeginInvoke(() =>
        {
            FitDesignToModel();
            AppServices.Device.InvalidateLayout();
        });
        var shotIndex = Array.IndexOf(e.Args, "--screenshot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            _ = TakeScreenshotsAsync(e.Args[shotIndex + 1]);
            return;
        }
        AppServices.Device.Start();

        _tray = new TrayIcon(ShowMainWindow, ExitApplication);

        if (!(startedByWindows && AppServices.Settings.StartMinimized))
            ShowMainWindow();
    }

    /// <summary>Developer aid: renders every page to PNG without touching the device.</summary>
    private async Task TakeScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        AppServices.Device.Sensors.Poll();
        await Task.Delay(500);
        AppServices.Device.Sensors.Poll();
        var window = new MainWindow { Width = 1440, Height = 900, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        foreach (var tag in new[] { "home", "media", "designer", "display", "sensors", "settings" })
        {
            window.Navigate(tag);
            await Task.Delay(1200);
            if (tag == "designer" && window.FindName("PageHost") is System.Windows.Controls.ContentControl { Content: Pages.DesignerPage dp })
            {
                dp.SelectFirstWidgetForScreenshot();
                await Task.Delay(600);
            }
            var root = (FrameworkElement)window.Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)) };
            bg.Measure(new Size(root.ActualWidth, root.ActualHeight));
            bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using var file = File.Create(Path.Combine(folder, tag + ".png"));
            encoder.Save(file);
        }
        _exiting = true;
        window.Close();
        Shutdown();
    }

    public void ShowMainWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closing += (_, args) =>
            {
                if (!_exiting && AppServices.Settings.MinimizeToTray)
                {
                    args.Cancel = true;
                    _window.Hide();
                    _tray?.ShowHintOnce();
                }
            };
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!AppServices.Settings.MinimizeToTray) ExitApplication();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        AppServices.Store.Save();
        _window?.Close();
        _tray?.Dispose();
        try { await AppServices.Device.DisposeAsync(); } catch { /* ignored */ }
        Shutdown();
    }

    /// <summary>Keeps the active layout and media size in step with the connected cooler's screen.</summary>
    private static void FitDesignToModel()
    {
        UiHelpers.UpdateMediaTarget();
        var model = UiHelpers.Model;
        AppServices.Settings.Design.FitTo(model.Width, model.Height);
    }

    // ------------------------------------------------------------ single instance across rights levels

    /// <summary>The objects are shared between elevated and normal copies, so allow the signed-in user.</summary>
    private static bool TryCreateInstanceMutex(out Mutex? mutex)
    {
        try
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Allow));
            mutex = MutexAcl.Create(true, InstanceMutexName, out var createdNew, security);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            mutex = null;
            return false; // exists, owned by a copy we can't open: another instance is running
        }
    }

    private static EventWaitHandle CreateShowEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    // ------------------------------------------------------------ scheduled tasks (created by the installer)

    private static (int code, string output) Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    public static bool TaskExists(string name) => Schtasks("/query", "/tn", name).code == 0;

    private static bool RunTask(string name) => Schtasks("/run", "/tn", name).code == 0;

    /// <summary>When installed, the sign-in task replaces the old per-user Run entry.</summary>
    private static void RemoveRunKeyIfTaskInstalled()
    {
        if (!TaskExists(StartupTaskName)) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue("TurrisStudio", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>Registers the app to launch quietly (tray only) when you sign in.</summary>
    public static void ApplyStartWithWindows(bool enabled)
    {
        // Installed copy: switch the elevated sign-in task on or off (needs the app's full rights).
        if (TaskExists(StartupTaskName))
        {
            if (Core.Sensors.BuiltInSource.IsElevated)
                Schtasks("/change", "/tn", StartupTaskName, enabled ? "/enable" : "/disable");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (enabled && exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                key.SetValue("TurrisStudio", $"\"{exe}\" --minimized");
            else if (!enabled)
                key.DeleteValue("TurrisStudio", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not fatal: the user can still start the app manually.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
