using TurrisStudio.Core.Config;
using TurrisStudio.Core.Media;
using TurrisStudio.Core.Overlay;
using TurrisStudio.Core.Protocol;
using TurrisStudio.Core.Sensors;
using TurrisStudio.Core.Usb;

namespace TurrisStudio.Core.Service;

public enum ConnectionState { Searching, Connecting, Connected, Busy, Error }

/// <summary>
/// Background loop: finds the display, reconnects automatically, restores the saved overlay,
/// pushes live sensor values and keeps the USB link alive.
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TryxClient? _client;
    private bool _leaseNext;
    private volatile bool _layoutDirty = true;
    private DateTime _lastPing = DateTime.MinValue;
    private DateTime _lastConnectAttempt = DateTime.MinValue;
    private readonly List<string> _log = [];

    public SensorHub Sensors { get; } = new();
    public ConnectionState State { get; private set; } = ConnectionState.Searching;
    public string StatusText { get; private set; } = "Looking for the display...";
    public DeviceInfoSummary? Device { get; private set; }
    /// <summary>The connected model, or the last one seen when disconnected.</summary>
    public DeviceModel Model => _client?.Model ?? DeviceModel.ForCode(Settings.ModelCode);
    public IReadOnlyList<DeviceMediaFile> DeviceMedia { get; private set; } = [];
    public bool IsConnected => _client != null && State is ConnectionState.Connected or ConnectionState.Busy;

    public event Action? StateChanged;
    public event Action<string>? LogAdded;
    public event Action? MediaChanged;
    /// <summary>Raised when a different cooler model than last time is connected (screen size changes).</summary>
    public event Action? ModelChanged;

    public sealed record DeviceInfoSummary(string Product, string Firmware, string App, string Serial, bool OverlaySupported);

    public DeviceService(SettingsStore store) => _store = store;

    private AppSettings Settings => _store.Settings;

    public IReadOnlyList<string> LogLines
    {
        get { lock (_log) return _log.ToList(); }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Call after the design changed; the new layout is pushed on the next tick.</summary>
    public void InvalidateLayout() => _layoutDirty = true;

    private volatile bool _displayDirty;
    private volatile bool _restartPending;

    /// <summary>
    /// Call after any display setting changed (brightness, backlight, mirror, playback). The service
    /// sends it on the next tick, and again automatically whenever the cooler reconnects.
    /// </summary>
    public void InvalidateDisplay(bool restartPlayback = false)
    {
        if (restartPlayback) _restartPending = true;
        _displayDirty = true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var tickStart = DateTime.UtcNow;
            try
            {
                Sensors.Poll();
                if (_client == null)
                {
                    if (DateTime.UtcNow - _lastConnectAttempt > TimeSpan.FromSeconds(3))
                    {
                        _lastConnectAttempt = DateTime.UtcNow;
                        await TryConnectAsync(ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await TickAsync(_client, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddLog("Connection error: " + ex.Message);
                Disconnect(ex.Message);
            }

            var interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.UpdateIntervalMs, 250, 10000));
            var wait = interval - (DateTime.UtcNow - tickStart);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken ct)
    {
        var device = UsbPrinterTransport.Discover().FirstOrDefault(d => d.IsSupported);
        if (device == null)
        {
            SetState(ConnectionState.Searching, "Display not found. Is the cooler's USB cable connected?");
            return;
        }
        if (System.Diagnostics.Process.GetProcessesByName("UDB").Length > 0 ||
            System.Diagnostics.Process.GetProcessesByName("KANALI").Length > 0)
        {
            SetState(ConnectionState.Error, "KANALI is running. Quit it (tray icon → Exit) so this app can control the screen.");
            return;
        }
        SetState(ConnectionState.Connecting, "Connecting...");
        try
        {
            var client = await TryxClient.ConnectAsync(device, AddLog, ct).ConfigureAwait(false);
            _client = client;
            var info = client.DeviceInfo;
            Device = new DeviceInfoSummary(client.Model.Name, info?.FirmwareVersion ?? "?", info?.AppVersion ?? "?",
                info?.SerialNumber ?? "", client.OverlaySupported);
            if (!string.Equals(Settings.ModelCode, client.Model.Code, StringComparison.OrdinalIgnoreCase))
            {
                Settings.ModelCode = client.Model.Code;
                if (!client.Model.SplitScreen && Settings.Display.SplitScreen)
                    Settings.Display = Settings.Display with { SplitScreen = false };
                _store.Save();
                ModelChanged?.Invoke();
            }
            _layoutDirty = true;
            // Restore the remembered settings (brightness, media, playback...) after every (re)connect.
            _displayDirty = !string.IsNullOrEmpty(Settings.Display.MediaFile);
            SetState(ConnectionState.Connected, "Connected");
            AddLog("Connected to " + Device.Product);
            await RefreshMediaAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            SetState(ConnectionState.Error, ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or TurrisProtocolException)
        {
            SetState(ConnectionState.Error, "The display did not respond: " + ex.Message);
        }
    }

    private async Task TickAsync(TryxClient client, CancellationToken ct)
    {
        if (client.IsFaulted) throw new IOException("USB connection lost.");
        if (State == ConnectionState.Busy) return; // don't interleave live updates with an upload
        if (_displayDirty)
        {
            _displayDirty = false;
            var restart = _restartPending;
            _restartPending = false;
            try
            {
                await ApplyDisplayAsync(restart, ct).ConfigureAwait(false);
                _layoutDirty = false; // the apply already sent the current layout
            }
            catch (Exception ex) when (ex is TurrisProtocolException or InvalidOperationException or TimeoutException)
            {
                AddLog("Could not sync display settings: " + ex.Message);
            }
        }
        var now = DateTime.Now;
        if (client.OverlaySupported)
        {
            var design = Settings.OverlayEnabled ? Settings.Design : new OverlayDesign();
            if (_layoutDirty)
            {
                _layoutDirty = false;
                await client.SetOverlayLayoutAsync(OverlayCompiler.BuildLayout(design, Sensors, now), ct).ConfigureAwait(false);
            }
            else
            {
                await client.SendMetricsAsync(OverlayCompiler.BuildMetrics(design, Sensors, now), ct).ConfigureAwait(false);
            }
        }
        if (client.KeepaliveRequired && DateTime.UtcNow - _lastPing > TimeSpan.FromSeconds(2))
        {
            _lastPing = DateTime.UtcNow;
            // Panorama-family firmware alternates Ping with an overlay "lease" refresh, like the official app.
            if (client.Model.Family == ProtocolFamily.Pase && client.OverlaySupported && _leaseNext && Settings.OverlayEnabled &&
                Settings.Design.Widgets.Any(w => w.Visible))
                await client.RefreshOverlayLeaseAsync(OverlayCompiler.BuildLayout(Settings.Design, Sensors, now), ct).ConfigureAwait(false);
            else
                await client.PingAsync(ct).ConfigureAwait(false);
            _leaseNext = !_leaseNext;
        }
    }

    private void Disconnect(string reason)
    {
        _client?.Dispose();
        _client = null;
        Device = null;
        SetState(ConnectionState.Error, reason);
    }

    // ---------------------------------------------------------------- user operations

    private TryxClient RequireClient() =>
        _client ?? throw new InvalidOperationException("The display is not connected.");

    public async Task RefreshMediaAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        try
        {
            DeviceMedia = await client.GetMediaCatalogAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or TurrisProtocolException)
        {
            AddLog("Could not read the media list: " + ex.Message);
        }
        MediaChanged?.Invoke();
    }

    /// <summary>Which screen of a split-screen cooler media is shown on.</summary>
    public enum ScreenSide { Full, Left, Right }

    private bool SplitActive => Settings.Display.SplitScreen && Model.SplitScreen;

    /// <summary>Conversion size for the connected cooler (whole surface, or one screen of a split cooler).</summary>
    public ConversionOptions TargetFor(MediaLibraryItem item, bool half)
    {
        var model = Model;
        half &= model.SplitScreen;
        return new ConversionOptions
        {
            Fit = item.Fit, Rotation = item.Rotation,
            Width = half ? model.HalfWidth : model.Width, Height = model.Height,
            MotionFps = half ? 30 : 60,
        };
    }

    /// <summary>Converts, uploads and (optionally) shows a library item.</summary>
    public Task UploadAsync(MediaLibraryItem item, bool applyAfter, IProgress<(string stage, double progress)>? progress,
        CancellationToken ct = default) =>
        UploadAsync(item, applyAfter ? (SplitActive ? ScreenSide.Left : ScreenSide.Full) : null, progress, ct);

    /// <summary>Converts and uploads a library item, then shows it on <paramref name="showOn"/> when set.</summary>
    public async Task UploadAsync(MediaLibraryItem item, ScreenSide? showOn, IProgress<(string stage, double progress)>? progress,
        CancellationToken ct = default)
    {
        var client = RequireClient();
        var half = showOn is ScreenSide.Left or ScreenSide.Right || (showOn == null && SplitActive);
        var target = TargetFor(item, half);
        MediaConverter.FfmpegPath = Settings.FfmpegFolder is { Length: > 0 } f ? Path.Combine(f, "ffmpeg.exe") : null;
        MediaConverter.FfprobePath = Settings.FfmpegFolder is { Length: > 0 } g ? Path.Combine(g, "ffprobe.exe") : null;

        progress?.Report(("Converting", 0));
        var prepared = await MediaConverter.PrepareAsync(item.SourcePath, target, SettingsStore.MediaFolder,
            new Progress<string>(s => progress?.Report((s, 0))), ct).ConfigureAwait(false);
        if (prepared.ThumbnailPath != null && item.ThumbnailPath == null) item.ThumbnailPath = prepared.ThumbnailPath;

        SetState(ConnectionState.Busy, "Uploading...");
        try
        {
            AddLog($"Uploading {item.DisplayName} ({prepared.Blob.Length / 1024} KB, {prepared.FrameCount} frames)");
            await client.UploadAsync(prepared.RemoteName, prepared.Blob,
                new Progress<double>(p => progress?.Report(("Uploading", p))), ct).ConfigureAwait(false);
            item.Uploads[$"{target.Width}x{target.Height}"] = prepared.RemoteName;
            _store.Save();
            await RefreshMediaAsync(ct).ConfigureAwait(false);
            if (showOn is { } side)
            {
                progress?.Report(("Applying", 1));
                await ShowMediaAsync(prepared.RemoteName, side, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_client != null) SetState(ConnectionState.Connected, "Connected");
        }
        progress?.Report(("Done", 1));
    }

    public Task ShowMediaAsync(string remoteName, CancellationToken ct = default) =>
        ShowMediaAsync(remoteName, SplitActive ? ScreenSide.Left : ScreenSide.Full, ct);

    public Task ShowMediaAsync(string remoteName, ScreenSide side, CancellationToken ct = default)
    {
        Settings.Display = side switch
        {
            ScreenSide.Right => Settings.Display with { RightMediaFile = remoteName, SplitScreen = true, BacklightOn = true },
            ScreenSide.Left => Settings.Display with { MediaFile = remoteName, SplitScreen = true, BacklightOn = true },
            _ => Settings.Display with { MediaFile = remoteName, SplitScreen = false, BacklightOn = true },
        };
        _store.Save();
        return ApplyDisplayAsync(restartPlayback: true, ct);
    }

    public async Task ApplyDisplayAsync(bool restartPlayback = false, CancellationToken ct = default)
    {
        var client = RequireClient();
        var state = Settings.Display;
        if (!client.Model.SplitScreen && state.SplitScreen) state = state with { SplitScreen = false };
        // A file remembered from another cooler (or deleted) can't be shown: fall back to what the device plays.
        if (DeviceMedia.Count > 0 && !string.IsNullOrEmpty(state.MediaFile) && DeviceMedia.All(f => f.Name != state.MediaFile))
            state = state with { MediaFile = "" };
        if (state.SplitScreen && DeviceMedia.Count > 0 && !string.IsNullOrEmpty(state.RightMediaFile) &&
            DeviceMedia.All(f => f.Name != state.RightMediaFile))
            state = state with { RightMediaFile = "" };
        if (state.SplitScreen && string.IsNullOrEmpty(state.MediaFile) != string.IsNullOrEmpty(state.RightMediaFile))
        {
            // Only one screen chosen so far: show the same media on both until the user picks the other one.
            var any = string.IsNullOrEmpty(state.MediaFile) ? state.RightMediaFile : state.MediaFile;
            state = state with
            {
                MediaFile = string.IsNullOrEmpty(state.MediaFile) ? any : state.MediaFile,
                RightMediaFile = string.IsNullOrEmpty(state.RightMediaFile) ? any : state.RightMediaFile,
            };
        }
        if (string.IsNullOrEmpty(state.MediaFile))
        {
            // Fall back to whatever the device currently plays.
            var current = await client.TryReadUserConfigurationAsync(ct).ConfigureAwait(false);
            var file = current?.WorkConfig?.SingleModeMediaFile;
            if (string.IsNullOrEmpty(file)) file = current?.WorkConfig?.DualModeLeftMediaFile;
            if (string.IsNullOrEmpty(file)) file = DeviceMedia.FirstOrDefault(f => f.IsPreset)?.Name ?? DeviceMedia.FirstOrDefault()?.Name;
            if (string.IsNullOrEmpty(file))
                throw new InvalidOperationException("Choose a media file to show first.");
            state = state with { MediaFile = file };
            if (state.SplitScreen && string.IsNullOrEmpty(state.RightMediaFile)) state = state with { RightMediaFile = file };
        }
        Settings.Display = state;
        var design = Settings.OverlayEnabled ? Settings.Design : new OverlayDesign();
        await client.ApplyAsync(state, OverlayCompiler.BuildLayout(design, Sensors, DateTime.Now), restartPlayback, ct)
            .ConfigureAwait(false);
        _store.Save();
        AddLog(state.SplitScreen
            ? $"Applied: left {state.MediaFile}, right {state.RightMediaFile}, brightness {state.Brightness}%"
            : $"Applied: {state.MediaFile}, {state.PlayMode}, brightness {state.Brightness}%");
    }

    public async Task DeleteMediaAsync(string remoteName, CancellationToken ct = default)
    {
        var client = RequireClient();
        await client.DeleteAsync(remoteName, ct).ConfigureAwait(false);
        foreach (var item in Settings.Library)
            foreach (var key in item.Uploads.Where(p => p.Value == remoteName).Select(p => p.Key).ToList())
                item.Uploads.Remove(key);
        _store.Save();
        await Task.Delay(300, ct).ConfigureAwait(false);
        await RefreshMediaAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- helpers

    private void SetState(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
        StateChanged?.Invoke();
    }

    private void AddLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_log)
        {
            _log.Add(stamped);
            if (_log.Count > 500) _log.RemoveAt(0);
        }
        LogAdded?.Invoke(stamped);
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataFolder);
            File.AppendAllText(Path.Combine(SettingsStore.AppDataFolder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd} {stamped}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignored */ }
        }
        _client?.Dispose();
        Sensors.Dispose();
        _cts.Dispose();
    }
}
