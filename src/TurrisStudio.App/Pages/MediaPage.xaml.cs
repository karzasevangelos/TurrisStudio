using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TurrisStudio.Core.Config;
using TurrisStudio.Core.Media;
using TurrisStudio.Core.Overlay;
using TurrisStudio.Core.Protocol;
using Wpf.Ui.Controls;

namespace TurrisStudio.Pages;

public sealed class MediaItemVm : ObservableBase
{
    public MediaLibraryItem Item { get; }
    private ImageSource? _thumbnail;
    public MediaItemVm(MediaLibraryItem item) { Item = item; LoadThumbnail(); }

    public string Name => Item.DisplayName;
    public string Source => Item.SourcePath;
    public string KindText => Item.Kind switch { MediaKind.Gif => "GIF", MediaKind.Image => "IMAGE", _ => "VIDEO" };
    public bool IsShowing
    {
        get
        {
            var d = AppServices.Settings.Display;
            return Item.IsUploaded(d.MediaFile) || (UiHelpers.SplitActive && Item.IsUploaded(d.RightMediaFile));
        }
    }
    public string StatusText => Item.RemoteName == null
        ? "Not uploaded yet" + (Item.Rotation != 0 ? $"  ·  rotated {Item.Rotation}°" : "")
        : "On the cooler" + (Item.Rotation != 0 ? $"  ·  rotated {Item.Rotation}°" : "");
    public Visibility SingleVisibility => UiHelpers.SplitActive ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SplitVisibility => UiHelpers.SplitActive ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Thumbnail => _thumbnail;

    public int FitIndex
    {
        get => (int)Item.Fit;
        set
        {
            if ((int)Item.Fit == value) return;
            Item.Fit = (FitMode)value;
            Item.Uploads.Clear(); // needs re-conversion
            Item.PreviewPath = null;
            AppServices.SaveSoon();
            Refresh();
        }
    }

    public void LoadThumbnail()
    {
        _thumbnail = null;
        if (Item.ThumbnailPath != null && File.Exists(Item.ThumbnailPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(Item.ThumbnailPath);
                bmp.DecodePixelWidth = 540;
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
            }
            catch (Exception) { /* missing thumbnail is fine */ }
        }
        OnPropertyChanged(nameof(Thumbnail));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsShowing));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(SingleVisibility));
        OnPropertyChanged(nameof(SplitVisibility));
    }
}

public sealed record DeviceFileVm(DeviceMediaFile File, string Title, string Detail, bool CanDelete, SymbolRegular Icon)
{
    public Visibility SingleVisibility => UiHelpers.SplitActive ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SplitVisibility => UiHelpers.SplitActive ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MediaPage : UserControl, IDisposable
{
    private readonly ObservableCollection<MediaItemVm> _library = [];
    private CancellationTokenSource? _uploadCts;
    private bool _loading;

    public MediaPage()
    {
        InitializeComponent();
        foreach (var item in AppServices.Settings.Library.OrderByDescending(i => i.Added)) _library.Add(new MediaItemVm(item));
        LibraryList.ItemsSource = _library;
        _loading = true;
        PlayModeBox.SelectedIndex = (int)AppServices.Settings.Display.PlayMode;
        _loading = false;
        UpdateEmpty();
        AppServices.Device.MediaChanged += OnDeviceMediaChanged;
        AppServices.Device.StateChanged += OnDeviceMediaChanged;
        AppServices.Device.ModelChanged += OnDeviceMediaChanged;
        OnDeviceMediaChanged();
    }

    private void UpdateLayoutControls()
    {
        var model = UiHelpers.Model;
        _loading = true;
        LayoutCard.Visibility = model.SplitScreen ? Visibility.Visible : Visibility.Collapsed;
        FullModeRadio.IsChecked = !UiHelpers.SplitActive;
        SplitModeRadio.IsChecked = UiHelpers.SplitActive;
        PlayModeBox.IsEnabled = !UiHelpers.SplitActive;
        _loading = false;
        SubtitleText.Text = model.SplitScreen
            ? $"Drop GIFs, videos or images here. They're converted for your {model.Name} ({model.Width}×{model.Height} across both screens, or {model.HalfWidth}×{model.Height} per screen), uploaded, and kept in your library."
            : $"Drop GIFs, videos or images here. They're converted to fit the {model.Width}×{model.Height} screen of your {model.Name}, uploaded, and kept in your library.";
    }

    private void ScreenMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var split = SplitModeRadio.IsChecked == true;
        if (split == AppServices.Settings.Display.SplitScreen) return;
        AppServices.Settings.Display = AppServices.Settings.Display with { SplitScreen = split };
        AppServices.Store.Save();
        UiHelpers.UpdateMediaTarget();
        AppServices.Device.InvalidateDisplay(restartPlayback: true);
        OnDeviceMediaChanged();
        MainWindow.Notify(split ? "Two pictures" : "One picture",
            split ? "Use the Left and Right buttons to choose what each screen shows." : "Click Show on anything to stretch it across both screens.");
    }

    private void UpdateEmpty() => EmptyHint.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnDeviceMediaChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var device = AppServices.Device;
            UiHelpers.UpdateMediaTarget();
            UpdateLayoutControls();
            var names = new Dictionary<string, string>();
            foreach (var i in AppServices.Settings.Library)
                foreach (var remote in i.Uploads.Values) names[remote] = i.DisplayName;
            var display = AppServices.Settings.Display;
            DeviceList.ItemsSource = device.IsConnected
                ? device.DeviceMedia.Select(f => new DeviceFileVm(f,
                    names.TryGetValue(f.Name, out var friendly) ? friendly : PrettyName(f.Name),
                    (f.IsPreset ? "Preset" : "Uploaded") + $"  ·  {f.Size / 1048576.0:F1} MB" +
                    (UiHelpers.SplitActive
                        ? (f.Name == display.MediaFile ? "  ·  left screen" : "") + (f.Name == display.RightMediaFile ? "  ·  right screen" : "")
                        : f.Name == display.MediaFile ? "  ·  showing now" : ""),
                    !f.IsPreset && !f.ReadOnly,
                    f.IsPreset ? SymbolRegular.Star24 : SymbolRegular.Video24)).ToList()
                : null;
            DeviceHint.Visibility = device.IsConnected && device.DeviceMedia.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            DeviceHint.Text = device.IsConnected ? "No files reported by the cooler." : "Connect the cooler to see its files.";
            foreach (var vm in _library) vm.Refresh();
            UpdateNowShowing();
        });
    }

    private void UpdateNowShowing()
    {
        var item = UiHelpers.CurrentItem();
        NowName.Text = UiHelpers.CurrentMediaName();
        var file = AppServices.Settings.Display.MediaFile;
        NowDetail.Text = UiHelpers.SplitActive
            ? "A different picture on each screen  ·  use Left / Right below to change them"
            : string.IsNullOrEmpty(file)
            ? "Pick something below and click Show."
            : (item != null ? $"{(item.Kind == MediaKind.Gif ? "GIF" : item.Kind == MediaKind.Image ? "Image" : "Video")} from your library" : "Built into the cooler")
              + $"  ·  {AppServices.Settings.Display.PlayMode switch { PlayMode.LoopAll => "looping through all files", PlayMode.Shuffle => "shuffling all files", _ => "repeating" }}";
        NowThumb.Source = null;
        if (item?.ThumbnailPath is { } thumb && File.Exists(thumb))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(thumb);
                bmp.DecodePixelWidth = 320;
                bmp.EndInit();
                bmp.Freeze();
                NowThumb.Source = bmp;
            }
            catch (Exception) { }
        }
    }

    private static string PrettyName(string remote) => UiHelpers.PrettyRemoteName(remote);

    // ------------------------------------------------------------ adding

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Add media",
            Filter = "Media|*.gif;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog() == true) await AddFilesAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFilesAsync(files);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        base.OnDragLeave(e);
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var thumbs = Path.Combine(SettingsStore.MediaFolder, "thumbs");
        Directory.CreateDirectory(thumbs);
        var added = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            if (!MediaConverter.IsSupported(path))
            {
                MainWindow.Notify("Unsupported file", Path.GetFileName(path), InfoBarSeverity.Warning);
                continue;
            }
            var item = new MediaLibraryItem
            {
                DisplayName = Path.GetFileNameWithoutExtension(path),
                SourcePath = path,
                Kind = MediaConverter.DetectKind(path),
            };
            item.ThumbnailPath = await MediaConverter.ExtractPreviewFrameAsync(path, Path.Combine(thumbs, item.Id + ".jpg"),
                new ConversionOptions { Fit = item.Fit });
            AppServices.Settings.Library.Add(item);
            _library.Insert(0, new MediaItemVm(item));
            _ = UiHelpers.EnsurePreviewClipAsync(item);
            added++;
        }
        AppServices.Store.Save();
        UpdateEmpty();
        if (added > 0)
            MainWindow.Notify("Added to library", added == 1 ? "Click Show to send it to the cooler." : $"{added} files added. Click Show on one to send it.",
                InfoBarSeverity.Success);
    }

    // ------------------------------------------------------------ actions

    private async void Show_Click(object sender, RoutedEventArgs e) =>
        await ShowAsync(sender, Core.Service.DeviceService.ScreenSide.Full);

    private async void ShowLeft_Click(object sender, RoutedEventArgs e) =>
        await ShowAsync(sender, Core.Service.DeviceService.ScreenSide.Left);

    private async void ShowRight_Click(object sender, RoutedEventArgs e) =>
        await ShowAsync(sender, Core.Service.DeviceService.ScreenSide.Right);

    private async Task ShowAsync(object sender, Core.Service.DeviceService.ScreenSide side)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaItemVm vm) return;
        var device = AppServices.Device;
        var where = side switch { Core.Service.DeviceService.ScreenSide.Left => " on the left screen", Core.Service.DeviceService.ScreenSide.Right => " on the right screen", _ => "" };
        var remote = vm.Item.RemoteName;
        var onDevice = remote != null && device.DeviceMedia.Any(f => f.Name == remote);
        if (onDevice)
        {
            await UiHelpers.RunAsync(() => device.ShowMediaAsync(remote!, side), $"Now showing {vm.Name}{where}.");
        }
        else
        {
            if (!MediaConverter.ToolsAvailable())
            {
                MainWindow.Notify("FFmpeg needed", "FFmpeg converts media for the cooler. Set its folder in Settings.", InfoBarSeverity.Error);
                return;
            }
            _uploadCts = new CancellationTokenSource();
            ProgressCard.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = $"Preparing {vm.Name}...";
            var progress = new Progress<(string stage, double p)>(s =>
            {
                ProgressText.Text = s.stage == "Uploading" ? $"Uploading {vm.Name}... {s.p:P0}" : $"{s.stage} {vm.Name}";
                ProgressBar.IsIndeterminate = s.stage != "Uploading";
                ProgressBar.Value = s.p;
            });
            await UiHelpers.RunAsync(() => device.UploadAsync(vm.Item, side, progress, _uploadCts.Token),
                $"{vm.Name} is now on your cooler{where}.");
            ProgressCard.Visibility = Visibility.Collapsed;
            _uploadCts.Dispose();
            _uploadCts = null;
            vm.LoadThumbnail();
        }
        foreach (var item in _library) item.Refresh();
        OnDeviceMediaChanged();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _uploadCts?.Cancel();

    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaItemVm vm) return;
        vm.Item.Rotation = (vm.Item.Rotation + 90) % 360;
        vm.Item.Uploads.Clear();
        vm.Item.PreviewPath = null;
        var thumbs = Path.Combine(SettingsStore.MediaFolder, "thumbs");
        vm.Item.ThumbnailPath = await MediaConverter.ExtractPreviewFrameAsync(vm.Item.SourcePath, Path.Combine(thumbs, vm.Item.Id + ".jpg"),
            new ConversionOptions { Fit = vm.Item.Fit, Rotation = vm.Item.Rotation });
        vm.LoadThumbnail();
        vm.Refresh();
        AppServices.SaveSoon();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaItemVm vm) return;
        AppServices.Settings.Library.Remove(vm.Item);
        _library.Remove(vm);
        AppServices.Store.Save();
        UpdateEmpty();
        MainWindow.Notify("Removed from library", "The file stays on the cooler until you delete it below.");
    }

    private async void ShowDevice_Click(object sender, RoutedEventArgs e) => await ShowDeviceAsync(sender, Core.Service.DeviceService.ScreenSide.Full);
    private async void ShowDeviceLeft_Click(object sender, RoutedEventArgs e) => await ShowDeviceAsync(sender, Core.Service.DeviceService.ScreenSide.Left);
    private async void ShowDeviceRight_Click(object sender, RoutedEventArgs e) => await ShowDeviceAsync(sender, Core.Service.DeviceService.ScreenSide.Right);

    private async Task ShowDeviceAsync(object sender, Core.Service.DeviceService.ScreenSide side)
    {
        if ((sender as FrameworkElement)?.Tag is not DeviceFileVm vm) return;
        await UiHelpers.RunAsync(() => AppServices.Device.ShowMediaAsync(vm.File.Name, side), $"Now showing {vm.Title}.");
        foreach (var item in _library) item.Refresh();
        OnDeviceMediaChanged();
    }

    private async void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DeviceFileVm vm) return;
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete from cooler?",
            Content = $"“{vm.Title}” will be removed from the cooler's storage.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        await UiHelpers.RunAsync(() => AppServices.Device.DeleteMediaAsync(vm.File.Name), "Deleted.");
        foreach (var item in _library) item.Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await UiHelpers.RunAsync(() => AppServices.Device.RefreshMediaAsync(), null);

    private async void PlayMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PlayModeBox.SelectedIndex < 0) return;
        AppServices.Settings.Display = AppServices.Settings.Display with { PlayMode = (PlayMode)PlayModeBox.SelectedIndex };
        AppServices.Store.Save();
        AppServices.Device.InvalidateDisplay(restartPlayback: true);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        AppServices.Device.MediaChanged -= OnDeviceMediaChanged;
        AppServices.Device.StateChanged -= OnDeviceMediaChanged;
        AppServices.Device.ModelChanged -= OnDeviceMediaChanged;
        _uploadCts?.Cancel();
    }
}
