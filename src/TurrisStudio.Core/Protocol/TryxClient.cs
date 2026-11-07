using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Panorama.Wire.V1;
using TurrisStudio.Core.Usb;

namespace TurrisStudio.Core.Protocol;

public sealed class TurrisProtocolException(string message) : Exception(message);

public enum PlayMode { Single, LoopAll, Shuffle }

/// <summary>Display state that the app owns and writes as one complete UserConfiguration.</summary>
public sealed record DisplayState
{
    /// <summary>Full-screen media (or the left screen in split mode).</summary>
    public string MediaFile { get; init; } = "";
    /// <summary>Right screen media when <see cref="SplitScreen"/> is on (Stage only).</summary>
    public string RightMediaFile { get; init; } = "";
    /// <summary>Separate media per screen on dual-screen coolers.</summary>
    public bool SplitScreen { get; init; }
    public PlayMode PlayMode { get; init; } = PlayMode.Single;
    public uint Brightness { get; init; } = 100;
    public bool BacklightOn { get; init; } = true;
    public bool Mirror { get; init; }
}

public sealed record DeviceMediaFile(string Name, uint Size, bool ReadOnly, bool IsPreset);

/// <summary>
/// TRYX printer-class protocol for TRYX displays (Turris 620, Stage 360): "TRYX" + u32le length +
/// protobuf Request/Response. Wire behaviour follows the MIT-licensed Tryx-Linux-GUI project.
/// </summary>
public sealed class TryxClient : IDisposable
{
    private const ulong TurrisFileTransferTrackId = 981521;
    private const int FileChunkSize = 0x40000;
    private const int MaxPayload = 1024 * 1024;
    private static readonly byte[] Magic = "TRYX"u8.ToArray();

    private readonly UsbPrinterTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ulong _nextTrackId;
    private UserConfiguration? _lastUserConfig;
    private DateTime _lastPing = DateTime.UtcNow;

    public DeviceModel Model { get; }
    public DeviceInformation? DeviceInfo { get; private set; }
    public SystemConfiguration? SystemConfig { get; private set; }
    public bool KeepaliveRequired { get; private set; } = true;
    public bool OverlaySupported { get; private set; } = true;
    public bool IsFaulted => _transport.IsFaulted;
    public string DevicePath => _transport.DevicePath;
    public Action<string>? Log { get; set; }

    private bool IsPase => Model.Family == ProtocolFamily.Pase;

    private TryxClient(UsbPrinterTransport transport, DeviceModel model)
    {
        _transport = transport;
        Model = model;
        _nextTrackId = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) | 1;
    }

    public static async Task<TryxClient> ConnectAsync(UsbPrinterDevice device, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var model = DeviceModel.ForProductId(device.ProductId)
                    ?? throw new TurrisProtocolException($"Unsupported TRYX display (USB 391a:{device.ProductId:x4}).");
        var transport = UsbPrinterTransport.Open(device.DevicePath);
        var client = new TryxClient(transport, model) { Log = log };
        try
        {
            if (client.IsPase) await client.BootstrapPaseAsync(ct).ConfigureAwait(false);
            else await client.BootstrapTurrisAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- session

    private void LogDeviceInfo() =>
        Log?.Invoke(DeviceInfo != null
            ? $"Device: {DeviceInfo.ProductName} fw {DeviceInfo.FirmwareVersion} app {DeviceInfo.AppVersion} SN {DeviceInfo.SerialNumber}"
            : "Device information was not answered.");

    private async Task BootstrapTurrisAsync(CancellationToken ct)
    {
        // 100 device information
        var info = await TryExecuteAsync(new Request { DeviceInformationQuery = new QueryToken { Dummy = "NA" } },
            Response.BodyOneofCase.DeviceInformation, ct: ct).ConfigureAwait(false);
        DeviceInfo = info?.DeviceInformation;
        LogDeviceInfo();

        // 102 system configuration (only after a confirmed device)
        if (DeviceInfo != null)
        {
            var sys = await TryExecuteAsync(new Request { SystemConfigurationQuery = new QueryToken { Dummy = "NA" } },
                Response.BodyOneofCase.SystemConfiguration, ct: ct).ConfigureAwait(false);
            SystemConfig = sys?.SystemConfiguration;
            if (SystemConfig?.RuntimeBehavior is { HasUsbAutoKeepalive: true } rb)
                KeepaliveRequired = !rb.UsbAutoKeepalive;
        }

        // 201 empty overlay layout: the official app sends this on every connect.
        var layoutResult = await SendTrackedOnlyAsync(new Request { OverlayLayout = new OverlayLayout() }, ct).ConfigureAwait(false);
        if (layoutResult == SendOutcome.Rejected) OverlaySupported = false;
    }

    /// <summary>
    /// Panorama/Stage handshake: DeviceInfo (retried until the firmware is ready), SystemConfiguration and
    /// DeviceAuthentication, all on track id 0, followed by the mandatory first Ping.
    /// </summary>
    private async Task BootstrapPaseAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var backoff = 500;
        Exception? last = null;
        while (DeviceInfo == null && DateTime.UtcNow < deadline)
        {
            try
            {
                var info = await ExecuteAsync(new Request { DeviceInformationQuery = new QueryToken { Dummy = "NA" } },
                    Response.BodyOneofCase.DeviceInformation, fixedTrackId: 0, timeout: TimeSpan.FromSeconds(3), ct: ct)
                    .ConfigureAwait(false);
                DeviceInfo = info.DeviceInformation;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                last = ex;
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                backoff = Math.Min(backoff * 2, 2000);
            }
        }
        if (DeviceInfo == null)
            throw new TimeoutException("The display did not answer the handshake" + (last != null ? $": {last.Message}" : "."));
        LogDeviceInfo();

        var sys = await ExecuteAsync(new Request { SystemConfigurationQuery = new QueryToken { Dummy = "NA" } },
            Response.BodyOneofCase.SystemConfiguration, fixedTrackId: 0, ct: ct).ConfigureAwait(false);
        SystemConfig = sys.SystemConfiguration;

        var auth = await TryExecuteAsync(new Request { DeviceAuthenticationQuery = new DeviceAuthQuery { Key = 1 } },
            Response.BodyOneofCase.DeviceAuthentication, fixedTrackId: 0, ct: ct).ConfigureAwait(false);
        if (auth == null) Log?.Invoke("Device authentication was not answered; continuing.");

        KeepaliveRequired = true; // PASE firmware drops the session without regular Pings.
        await PingAsync(ct).ConfigureAwait(false);
        await TryReadUserConfigurationAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WritePingAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WritePingAsync(CancellationToken ct)
    {
        var request = new Request { Header = new WireHeader(), Ping = new Ping { Payload = ByteString.CopyFromUtf8("hello?") } };
        await WriteFrameAsync(request, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        _lastPing = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- media

    public async Task<IReadOnlyList<DeviceMediaFile>> GetMediaCatalogAsync(CancellationToken ct = default)
    {
        var response = await ExecuteAsync(new Request { MediaCatalogQuery = new QueryToken() },
            Response.BodyOneofCase.MediaCatalog, ct: ct).ConfigureAwait(false);
        var list = new List<DeviceMediaFile>();
        foreach (var e in response.MediaCatalog.MediaFileList)
            if (NormalizeName(e.FilePath) is { } n) list.Add(new DeviceMediaFile(n, e.FileSize, e.ReadOnly, false));
        foreach (var e in response.MediaCatalog.PresetFileList)
            if (NormalizeName(e.FilePath) is { } n) list.Add(new DeviceMediaFile(n, e.FileSize, true, true));
        return list;
    }

    /// <summary>Uploads a prepared MXHD blob (see <see cref="Media.MediaConverter"/>).</summary>
    public async Task UploadAsync(string remoteName, byte[] blob, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(30);
        // Turris uses one fixed id; Panorama-family firmware expects a fresh id for each transfer sequence.
        var trackId = IsPase ? AllocateTrackId() : TurrisFileTransferTrackId;
        var begin = await ExecuteAsync(new Request
            {
                TransferBegin = new TransferBegin { FileName = ByteString.CopyFromUtf8(remoteName), FileSize = (uint)blob.Length }
            }, Response.BodyOneofCase.TransferBeginStatus, version: 0, fixedTrackId: trackId,
            timeout: timeout, ct: ct).ConfigureAwait(false);
        CheckTransferStatus(begin.TransferBeginStatus, "begin");

        for (var offset = 0; offset < blob.Length; offset += FileChunkSize)
        {
            var size = Math.Min(FileChunkSize, blob.Length - offset);
            var chunk = await ExecuteAsync(new Request
                {
                    TransferChunk = new TransferChunk { FileData = ByteString.CopyFrom(blob, offset, size) }
                }, Response.BodyOneofCase.TransferChunkStatus, version: 0, fixedTrackId: trackId,
                timeout: timeout, ct: ct).ConfigureAwait(false);
            CheckTransferStatus(chunk.TransferChunkStatus, "data");
            progress?.Report((double)(offset + size) / blob.Length);
            if (IsPase && DateTime.UtcNow - _lastPing > TimeSpan.FromSeconds(2))
                await PingAsync(ct).ConfigureAwait(false);
        }

        var end = await ExecuteAsync(new Request
            {
                TransferEnd = new TransferEnd { FileType = ByteString.CopyFromUtf8("media"), Checksum = 0 }
            }, Response.BodyOneofCase.TransferEndStatus, version: 0, fixedTrackId: trackId,
            timeout: timeout, ct: ct).ConfigureAwait(false);
        CheckTransferStatus(end.TransferEndStatus, "end");
    }

    public async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        // The device does not always acknowledge deletes; the caller confirms through the catalog.
        await SendTrackedOnlyAsync(new Request { FileRemoval = new FileRemoval { FileName = fileName, FileType = "media" } }, ct,
            IsPase ? TimeSpan.FromSeconds(3) : null).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- configuration

    public async Task<UserConfiguration?> TryReadUserConfigurationAsync(CancellationToken ct = default)
    {
        var response = await TryExecuteAsync(new Request { UserConfigurationQuery = new QueryToken { Dummy = "NA" } },
            Response.BodyOneofCase.UserConfiguration, ct: ct).ConfigureAwait(false);
        if (response?.UserConfiguration != null) _lastUserConfig = response.UserConfiguration;
        return response?.UserConfiguration;
    }

    /// <summary>Writes the full user configuration, activates the overlay layout and (re)starts playback.</summary>
    public async Task ApplyAsync(DisplayState state, OverlayLayout? layout, bool restartPlayback,
        CancellationToken ct = default)
    {
        var split = Model.SplitScreen && state.SplitScreen;
        if (string.IsNullOrEmpty(state.MediaFile) && (!split || string.IsNullOrEmpty(state.RightMediaFile)))
            throw new TurrisProtocolException("Select a media file before applying display settings.");

        var config = _lastUserConfig?.Clone() ?? await TryReadUserConfigurationAsync(ct).ConfigureAwait(false) ?? new UserConfiguration();
        config = config.Clone();
        var powerOn = SystemConfig is { HasPoweronMediaFile: true } ? SystemConfig.PoweronMediaFile.Trim() : "";
        var standby = SystemConfig is { HasStandbyMediaFile: true } ? SystemConfig.StandbyMediaFile.Trim() : "";
        if (config.PoweronConfig == null || string.IsNullOrEmpty(config.PoweronConfig.MediaFile))
            config.PoweronConfig = new PowerOnConfiguration { MediaFile = powerOn.Length > 0 ? powerOn : Model.DefaultPowerOnMedia };
        config.StandbyConfig ??= new StandbyConfiguration
            { Enable = true, MediaFile = standby.Length > 0 ? standby : Model.DefaultStandbyMedia };
        config.WorkConfig ??= new WorkConfiguration();
        config.DisplayConfig ??= new DisplayConfiguration();

        if (split)
        {
            config.WorkConfig.MediaMode = WorkConfiguration.Types.MediaMode.MediaDual;
            config.WorkConfig.LoopMode = WorkConfiguration.Types.LoopMode.LoopSingle;
            config.WorkConfig.DualModeLeftMediaFile = state.MediaFile;
            config.WorkConfig.DualModeRightMediaFile = state.RightMediaFile;
        }
        else
        {
            config.WorkConfig.MediaMode = WorkConfiguration.Types.MediaMode.MediaSingle;
            config.WorkConfig.LoopMode = state.PlayMode switch
            {
                PlayMode.LoopAll => WorkConfiguration.Types.LoopMode.LoopAll,
                PlayMode.Shuffle => WorkConfiguration.Types.LoopMode.LoopRandom,
                _ => WorkConfiguration.Types.LoopMode.LoopSingle,
            };
            config.WorkConfig.SingleModeMediaFile = state.MediaFile;
        }
        config.DisplayConfig.BacklightEnable = state.BacklightOn;
        config.DisplayConfig.BacklightBrightness = Math.Clamp(state.Brightness, 0u, 100u);
        if (IsPase)
        {
            // Panorama-family firmware rotates instead of using a mirror flag.
            config.DisplayConfig.Mirror = false;
            config.DisplayConfig.UiRotation = 0;
            config.DisplayConfig.MediaRotation = state.Mirror ? 180u : 0u;
        }
        else
        {
            config.DisplayConfig.Mirror = state.Mirror;
        }

        // The Turris stores the configuration but may not acknowledge it within the window; PASE answers.
        var outcome = await SendTrackedOnlyAsync(new Request { UserConfiguration = config }, ct,
            IsPase ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
        if (outcome == SendOutcome.Rejected)
            throw new TurrisProtocolException("The display rejected the configuration.");
        _lastUserConfig = config;

        await SetOverlayLayoutAsync(layout ?? new OverlayLayout(), ct).ConfigureAwait(false);

        if (restartPlayback && !IsPase)
        {
            // A bare FileTransmitEnd makes the Turris reload and restart playback.
            await TryExecuteAsync(new Request
                {
                    TransferEnd = new TransferEnd { FileType = ByteString.CopyFromUtf8("media"), Checksum = 0 }
                }, Response.BodyOneofCase.TransferEndStatus, version: 0, fixedTrackId: TurrisFileTransferTrackId,
                timeout: TimeSpan.FromSeconds(3), ct: ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- overlay

    public async Task SetOverlayLayoutAsync(OverlayLayout layout, CancellationToken ct = default)
    {
        var outcome = await SendTrackedOnlyAsync(new Request { OverlayLayout = layout }, ct).ConfigureAwait(false);
        if (outcome == SendOutcome.Rejected)
        {
            if (!IsPase) throw new TurrisProtocolException("The display rejected the overlay layout.");
            Log?.Invoke("The display rejected the overlay layout.");
        }
    }

    /// <summary>
    /// Periodic overlay "lease" refresh used by Panorama-family firmware: the layout is re-sent as an
    /// untracked setter (empty header), without waiting for an answer.
    /// </summary>
    public async Task RefreshOverlayLeaseAsync(OverlayLayout layout, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(new Request { Header = new WireHeader(), OverlayLayout = layout }, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Live label text updates; header-less and unacknowledged, like the official app.</summary>
    public async Task SendMetricsAsync(MetricBatch batch, CancellationToken ct = default)
    {
        if (batch.LabelGroups.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(new Request { MetricBatch = batch }, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- transactions

    private enum SendOutcome { Acknowledged, Unconfirmed, Rejected }

    private ulong AllocateTrackId()
    {
        var id = _nextTrackId++;
        if (id == 0) id = _nextTrackId++;
        return id;
    }

    private async Task<Response?> TryExecuteAsync(Request request, Response.BodyOneofCase expected, uint version = 1,
        ulong? fixedTrackId = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteAsync(request, expected, version, fixedTrackId, timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke($"No answer to {request.BodyCase}.");
            return null;
        }
        catch (TurrisProtocolException ex)
        {
            Log?.Invoke($"{request.BodyCase} declined: {ex.Message}");
            return null;
        }
    }

    private async Task<Response> ExecuteAsync(Request request, Response.BodyOneofCase expected, uint version = 1,
        ulong? fixedTrackId = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var trackId = fixedTrackId ?? AllocateTrackId();
            request.Header = new WireHeader { Version = version, TrackId = trackId, PayloadCrc32 = 0 };
            _transport.DiscardInput();
            await WriteFrameAsync(request, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            var response = await _transport.ReadUntilAsync(span => ParseFrame(span, trackId, expected, acceptHeaderOnly: false),
                timeout ?? TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return response!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Setter that may or may not be answered; a tracked error reply means rejection.</summary>
    private async Task<SendOutcome> SendTrackedOnlyAsync(Request request, CancellationToken ct, TimeSpan? window = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var trackId = AllocateTrackId();
            request.Header = new WireHeader { Version = 1, TrackId = trackId, PayloadCrc32 = 0 };
            _transport.DiscardInput();
            await WriteFrameAsync(request, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            try
            {
                await _transport.ReadUntilAsync(span => ParseFrame(span, trackId, Response.BodyOneofCase.None, acceptHeaderOnly: true),
                    window ?? TimeSpan.FromMilliseconds(1000), ct).ConfigureAwait(false);
                return SendOutcome.Acknowledged;
            }
            catch (TimeoutException)
            {
                return SendOutcome.Unconfirmed;
            }
            catch (TurrisProtocolException ex)
            {
                Log?.Invoke($"{request.BodyCase} rejected: {ex.Message}");
                return SendOutcome.Rejected;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteFrameAsync(Request request, TimeSpan timeout, CancellationToken ct)
    {
        var payload = request.ToByteArray();
        if (payload.Length > MaxPayload) throw new TurrisProtocolException("Request is too large.");
        var frame = new byte[8 + payload.Length];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        await _transport.WriteAsync(frame, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Consumes frames from the receive buffer until the matching response is found.</summary>
    private (int consumed, bool done, Response? value) ParseFrame(ReadOnlySpan<byte> buffer, ulong trackId,
        Response.BodyOneofCase expected, bool acceptHeaderOnly)
    {
        var start = buffer.IndexOf(Magic);
        if (start < 0)
            return (Math.Max(0, buffer.Length - 3), false, null); // keep a possible partial magic
        if (start > 0) return (start, false, null);
        if (buffer.Length < 8) return (0, false, null);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (size > MaxPayload) return (1, false, null); // bogus header, resync
        if (buffer.Length < 8 + size) return (0, false, null);

        var frameLength = 8 + (int)size;
        Response response;
        try
        {
            response = Response.Parser.ParseFrom(buffer.Slice(8, (int)size));
        }
        catch (InvalidProtocolBufferException)
        {
            return (frameLength, false, null);
        }

        var tracked = response.Header != null && (response.Header.TrackId == trackId || response.Header.TrackId == 0);
        var errorCode = response.Error?.Code ?? ProtocolError.Types.Code.Success;
        if (tracked && response.Header!.TrackId == trackId && errorCode != ProtocolError.Types.Code.Success)
            throw new TurrisProtocolException(string.IsNullOrEmpty(response.Error!.Why) ? errorCode.ToString() : response.Error.Why);

        if (tracked && expected != Response.BodyOneofCase.None && response.BodyCase == expected)
            return (frameLength, true, response);
        if (acceptHeaderOnly && response.Header?.TrackId == trackId)
            return (frameLength, true, response);
        return (frameLength, false, null); // unrelated frame (pong, async event, stale reply)
    }

    private static void CheckTransferStatus(TransferStatus status, string stage)
    {
        if (status.Status != TransferStatus.Types.Code.Ok)
            throw new TurrisProtocolException(status.Status switch
            {
                TransferStatus.Types.Code.SpaceNotEnough => "The display is out of storage space. Delete some media first.",
                TransferStatus.Types.Code.ChecksumFailure => "Upload checksum failed.",
                _ => $"Upload failed during {stage} ({status.Status}).",
            });
    }

    private static string? NormalizeName(string path)
    {
        path = path.Trim();
        if (path.Length == 0) return null;
        var slash = path.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.Length == 0 ? null : name;
    }

    public void Dispose()
    {
        _transport.Dispose();
        _gate.Dispose();
    }
}
