using System.IO;
using TurrisStudio.Controls;
using TurrisStudio.Core.Config;
using TurrisStudio.Core.Media;
using TurrisStudio.Core.Protocol;
using Wpf.Ui.Controls;

namespace TurrisStudio;

public static class UiHelpers
{
    /// <summary>The connected (or last seen) cooler model.</summary>
    public static DeviceModel Model => AppServices.Device.Model;

    /// <summary>True when a dual-screen cooler shows separate media on each screen.</summary>
    public static bool SplitActive => Model.SplitScreen && AppServices.Settings.Display.SplitScreen;

    /// <summary>Points library items' <see cref="MediaLibraryItem.RemoteName"/> at the size the cooler currently uses.</summary>
    public static void UpdateMediaTarget() => MediaLibraryItem.ActiveSizeKey = Model.SizeKey(SplitActive);

    /// <summary>The library item for a file on the device (adopting KANALI uploads when possible).</summary>
    public static MediaLibraryItem? ItemFor(string? remoteName)
    {
        if (string.IsNullOrEmpty(remoteName)) return null;
        return AppServices.Settings.Library.FirstOrDefault(i => i.IsUploaded(remoteName)) ?? AdoptExternalUpload(remoteName);
    }

    /// <summary>The library item currently shown on the cooler (the left screen in split mode; null for presets).</summary>
    public static MediaLibraryItem? CurrentItem() => ItemFor(AppServices.Settings.Display.MediaFile);

    /// <summary>The library item on the right screen in split mode.</summary>
    public static MediaLibraryItem? CurrentRightItem() => SplitActive ? ItemFor(AppServices.Settings.Display.RightMediaFile) : null;

    /// <summary>
    /// Files uploaded by KANALI aren't in our library; their originals usually still sit in KANALI's
    /// media folders. Find the original and add it to the library so it can be previewed.
    /// </summary>
    public static MediaLibraryItem? AdoptExternalUpload(string remoteName)
    {
        var cut = remoteName.IndexOf(".h264", StringComparison.OrdinalIgnoreCase);
        if (cut <= 0) return null;
        var original = remoteName[..cut]; // e.g. 2026-11-06_23-14-02-144.mp4
        var folders = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kanali", "media"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KANALI", "media"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KANALI", "media", "temp"),
        };
        var source = folders.Select(f => Path.Combine(f, original)).FirstOrDefault(File.Exists);
        if (source == null) return null;

        var item = new MediaLibraryItem
        {
            DisplayName = "KANALI " + PrettyRemoteName(remoteName),
            SourcePath = source,
            Kind = MediaConverter.DetectKind(source),
        };
        item.Uploads[MediaLibraryItem.SizeKeyOf(remoteName) ?? MediaLibraryItem.ActiveSizeKey] = remoteName;
        AppServices.Settings.Library.Add(item);
        AppServices.SaveSoon();
        var thumbs = Path.Combine(SettingsStore.MediaFolder, "thumbs");
        Directory.CreateDirectory(thumbs);
        _ = MediaConverter.ExtractPreviewFrameAsync(source, Path.Combine(thumbs, item.Id + ".jpg"), new ConversionOptions())
            .ContinueWith(t => { item.ThumbnailPath = t.Result; AppServices.SaveSoon(); }, TaskScheduler.FromCurrentSynchronizationContext());
        return item;
    }

    /// <summary>Thumbnail of the media currently shown, for the preview background.</summary>
    public static string? CurrentBackground()
    {
        var item = CurrentItem() ?? AppServices.Settings.Library.FirstOrDefault(i => i.ThumbnailPath != null && File.Exists(i.ThumbnailPath));
        return item?.ThumbnailPath;
    }

    public static string CurrentMediaName()
    {
        var display = AppServices.Settings.Display;
        string NameOf(string file) => string.IsNullOrEmpty(file) ? "nothing" : ItemFor(file)?.DisplayName ?? PrettyRemoteName(file);
        if (SplitActive)
            return $"Left: {NameOf(display.MediaFile)}   ·   Right: {NameOf(display.RightMediaFile)}";
        if (string.IsNullOrEmpty(display.MediaFile)) return "Nothing selected yet";
        return NameOf(display.MediaFile);
    }

    public static string PrettyRemoteName(string remote)
    {
        var i = remote.IndexOf(".h264", StringComparison.OrdinalIgnoreCase);
        var name = i > 0 ? remote[..i] : remote;
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_?\d{3,4}x\d{3,4}", "");
        return name.Replace(".mp4", "").Replace("default_", "").Replace('_', ' ').Trim();
    }

    /// <summary>Matches a preview canvas to the connected cooler's screen(s).</summary>
    public static void ApplyScreen(ScreenCanvas canvas) => canvas.SetScreen(Model.Width, Model.Height, Model.SplitScreen);

    /// <summary>
    /// Shows media (moving, when it's a GIF/video) behind the overlay of a preview canvas. With no item,
    /// shows what the cooler currently plays, including both screens in split mode.
    /// </summary>
    public static async Task ShowInPreviewAsync(ScreenCanvas canvas, MediaLibraryItem? item)
    {
        ApplyScreen(canvas);
        if (item == null && SplitActive)
        {
            var left = CurrentItem();
            var right = CurrentRightItem();
            canvas.SetSplitBackground(left?.ThumbnailPath, QuickClip(left), right?.ThumbnailPath, QuickClip(right));
            var leftClip = left != null ? await EnsurePreviewClipAsync(left) : null;
            var rightClip = right != null ? await EnsurePreviewClipAsync(right) : null;
            if (leftClip != null || rightClip != null)
                canvas.SetSplitBackground(left?.ThumbnailPath, leftClip ?? QuickClip(left), right?.ThumbnailPath, rightClip ?? QuickClip(right));
            return;
        }
        item ??= CurrentItem();
        if (item == null)
        {
            canvas.SetBackground(CurrentBackground(), null);
            return;
        }
        canvas.SetBackground(item.ThumbnailPath, QuickClip(item));
        if (item.Kind == MediaKind.Image) return;
        var clip = await EnsurePreviewClipAsync(item);
        if (clip != null) canvas.SetBackground(item.ThumbnailPath, clip);
    }

    /// <summary>A clip that can play right away: the preview clip, or a ready-to-play source file.</summary>
    private static string? QuickClip(MediaLibraryItem? item)
    {
        if (item == null || item.Kind == MediaKind.Image) return null;
        if (item.PreviewPath != null && File.Exists(item.PreviewPath)) return item.PreviewPath;
        var ext = Path.GetExtension(item.SourcePath).ToLowerInvariant();
        return ext is ".mp4" or ".wmv" or ".m4v" or ".mov" && item.Rotation == 0 && item.Fit == FitMode.Fill && File.Exists(item.SourcePath)
            ? item.SourcePath
            : null;
    }

    private static readonly Dictionary<string, Task<string?>> PendingClips = new();

    public static Task<string?> EnsurePreviewClipAsync(MediaLibraryItem item)
    {
        if (item.PreviewPath != null && File.Exists(item.PreviewPath)) return Task.FromResult<string?>(item.PreviewPath);
        if (item.Kind == MediaKind.Image || !File.Exists(item.SourcePath)) return Task.FromResult<string?>(null);
        if (PendingClips.TryGetValue(item.Id, out var pending)) return pending;
        var task = CreateClipAsync(item);
        PendingClips[item.Id] = task;
        return task;
    }

    private static async Task<string?> CreateClipAsync(MediaLibraryItem item)
    {
        var folder = Path.Combine(SettingsStore.MediaFolder, "previews");
        Directory.CreateDirectory(folder);
        var clip = await MediaConverter.CreatePreviewClipAsync(item.SourcePath, Path.Combine(folder, $"{item.Id}_{item.Rotation}_{item.Fit}.mp4"),
            new ConversionOptions { Fit = item.Fit, Rotation = item.Rotation });
        PendingClips.Remove(item.Id);
        if (clip != null)
        {
            item.PreviewPath = clip;
            AppServices.SaveSoon();
        }
        return clip;
    }

    /// <summary>Runs a device operation and turns failures into a friendly notification.</summary>
    public static async Task<bool> RunAsync(Func<Task> action, string? successMessage)
    {
        try
        {
            await action();
            if (successMessage != null) MainWindow.Notify("Done", successMessage, InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TurrisProtocolException or TimeoutException or IOException or FileNotFoundException)
        {
            MainWindow.Notify("Something went wrong", ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }
}
