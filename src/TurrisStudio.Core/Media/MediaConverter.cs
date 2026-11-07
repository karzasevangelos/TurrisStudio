using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TurrisStudio.Core.Media;

public enum MediaKind { Image = 2, Gif = 3, Video = 4 }

public enum FitMode { Fill, Fit, Stretch }

public sealed record ConversionOptions
{
    public FitMode Fit { get; init; } = FitMode.Fill;
    /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
    public int Rotation { get; init; }
    public string PadColor { get; init; } = "000000";
    /// <summary>Optional trim for videos.</summary>
    public TimeSpan? Start { get; init; }
    public TimeSpan? Duration { get; init; }
    /// <summary>Video quality: null = bitrate derived from the source like the official app.</summary>
    public int? VideoBitrateKbps { get; init; }
    /// <summary>Output size: the whole screen, or one half of a split-screen cooler.</summary>
    public int Width { get; init; } = MediaConverter.DefaultWidth;
    public int Height { get; init; } = MediaConverter.DefaultHeight;
    /// <summary>Frame rate for GIFs and videos (KANALI uses 30 for square split-screen halves).</summary>
    public int MotionFps { get; init; } = 60;
}

public sealed record PreparedMedia(string RemoteName, byte[] Blob, MediaKind Kind, ulong FrameCount, string? ThumbnailPath);

/// <summary>
/// Converts any image/GIF/video into the TRYX display format: Annex-B H.264 at the screen size (x264, main 4.1,
/// 1 s GOP, no B-frames, full-range BT.709) wrapped in the "MXHD" media header.
/// </summary>
public static class MediaConverter
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;
    private const uint MediaMagic = 0x4D584844; // "MXHD"

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"];
    private static readonly string[] GifExtensions = [".gif", ".apng"];

    public static string? FfmpegPath { get; set; }
    public static string? FfprobePath { get; set; }

    public static MediaKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (GifExtensions.Contains(ext)) return MediaKind.Gif;
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        return MediaKind.Video;
    }

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || GifExtensions.Contains(ext) ||
               ext is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".m4v" or ".flv" or ".mpg" or ".mpeg" or ".ts";
    }

    public static string ResolveTool(string name)
    {
        var configured = name == "ffmpeg" ? FfmpegPath : FfprobePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var exe = name + ".exe";
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        foreach (var guess in new[] { @"C:\ffmpeg\bin", @"C:\Program Files\ffmpeg\bin" })
        {
            var candidate = Path.Combine(guess, exe);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{exe} was not found. Install FFmpeg or set its folder in Settings.");
    }

    public static bool ToolsAvailable()
    {
        try
        {
            ResolveTool("ffmpeg");
            ResolveTool("ffprobe");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public static async Task<PreparedMedia> PrepareAsync(string sourcePath, ConversionOptions options,
        string workDirectory, IProgress<string>? status = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDirectory);
        var kind = DetectKind(sourcePath);
        var fps = kind == MediaKind.Image ? 30 : options.MotionFps;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) ext = "mp4";
        var remoteName = $"{stamp}.{ext}.h264_{options.Width}x{options.Height}";
        var rawPath = Path.Combine(workDirectory, remoteName + ".raw.h264");
        var thumbPath = Path.Combine(workDirectory, remoteName + ".jpg");

        var filter = BuildFilter(options, fps);
        var args = new List<string> { "-hide_banner", "-y" };
        if (options.Start is { } start) args.AddRange(["-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", sourcePath]);
        if (options.Duration is { } dur && kind != MediaKind.Image)
            args.AddRange(["-t", dur.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-c:v", "libx264", "-preset", "fast"]);
        if (kind == MediaKind.Gif) args.AddRange(["-tune", "animation"]);
        if (kind is MediaKind.Image or MediaKind.Gif)
        {
            args.AddRange(["-crf", "18"]);
        }
        else
        {
            var kbps = options.VideoBitrateKbps ?? await DeriveBitrateAsync(sourcePath, fps, options.Width * options.Height, ct).ConfigureAwait(false);
            args.AddRange(["-b:v", $"{kbps}k"]);
        }
        args.AddRange([
            "-vf", filter, "-an", "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr", "-profile:v", "main", "-level:v", "4.1", "-g", fps.ToString(CultureInfo.InvariantCulture),
            "-bf", "0", "-color_range", "pc", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-x264-params", kind == MediaKind.Gif
                ? "repeat-headers=1:annexb=1:aud=1:open-gop=0:scenecut=0:b-pyramid=0:weightp=0:ref=3:fullrange=on:colorprim=bt709:transfer=bt709:colormatrix=bt709"
                : "repeat-headers=1:annexb=1:aud=1:b-pyramid=0:weightp=0:ref=3:fullrange=on:colorprim=bt709:transfer=bt709:colormatrix=bt709",
        ]);
        if (kind == MediaKind.Image) args.AddRange(["-frames:v", "1"]);
        args.AddRange(["-f", "h264", rawPath]);

        status?.Report("Converting...");
        var (code, output) = await RunAsync(ResolveTool("ffmpeg"), args, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(rawPath) || new FileInfo(rawPath).Length == 0)
            throw new InvalidOperationException("FFmpeg conversion failed:\n" + Tail(output));

        status?.Report("Counting frames...");
        var frames = await CountFramesAsync(rawPath, ct).ConfigureAwait(false);
        if (frames == 0) throw new InvalidOperationException("The converted media has no frames.");
        if (kind == MediaKind.Image) frames = 1;

        status?.Report("Creating thumbnail...");
        try
        {
            await RunAsync(ResolveTool("ffmpeg"),
                ["-hide_banner", "-y", "-f", "h264", "-i", rawPath, "-frames:v", "1", "-vf", "scale=320:-2", thumbPath], ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        var raw = await File.ReadAllBytesAsync(rawPath, ct).ConfigureAwait(false);
        File.Delete(rawPath);
        var blob = WrapMxhd(raw, kind, frames, fps, options.Width, options.Height);
        return new PreparedMedia(remoteName, blob, kind, frames, File.Exists(thumbPath) ? thumbPath : null);
    }

    /// <summary>Builds the ffmpeg filter for rotation + fit mode, ending in the device colour setup.</summary>
    public static string BuildFilter(ConversionOptions options, int fps)
    {
        var sb = new StringBuilder();
        switch (((options.Rotation % 360) + 360) % 360)
        {
            case 90: sb.Append("transpose=1,"); break;
            case 180: sb.Append("hflip,vflip,"); break;
            case 270: sb.Append("transpose=2,"); break;
        }
        sb.Append("scale=trunc(iw*sar/2)*2:ih,setsar=1,");
        int Width = options.Width, Height = options.Height;
        switch (options.Fit)
        {
            case FitMode.Fit:
                sb.Append($"scale={Width}:{Height}:force_original_aspect_ratio=decrease,");
                sb.Append($"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=0x{options.PadColor},");
                break;
            case FitMode.Stretch:
                sb.Append($"scale={Width}:{Height},");
                break;
            default:
                sb.Append($"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height},");
                break;
        }
        sb.Append($"fps={fps},scale=in_range=auto:out_range=full:out_color_matrix=bt709,setsar=0");
        return sb.ToString();
    }

    public static byte[] WrapMxhd(byte[] rawH264, MediaKind kind, ulong frameCount, int fps, int width, int height)
    {
        var meta = new List<byte>();
        void Varint(ulong v)
        {
            while (v >= 0x80) { meta.Add((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            meta.Add((byte)v);
        }
        void VarintField(uint field, ulong value) { Varint((ulong)field << 3); Varint(value); }

        var description = Encoding.ASCII.GetBytes($"Tryx media header v1, fps={fps}, size={width}x{height}");
        VarintField(1, MediaMagic);
        Varint((2ul << 3) | 2); Varint((ulong)description.Length); meta.AddRange(description);
        VarintField(3, (uint)kind);
        VarintField(4, 1);
        VarintField(5, (ulong)fps);
        VarintField(6, (ulong)width);
        VarintField(7, (ulong)height);
        VarintField(8, frameCount);

        var blob = new byte[4 + meta.Count + rawH264.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)meta.Count);
        meta.CopyTo(blob, 4);
        rawH264.CopyTo(blob, 4 + meta.Count);
        return blob;
    }

    private static async Task<ulong> CountFramesAsync(string rawPath, CancellationToken ct)
    {
        var (_, output) = await RunAsync(ResolveTool("ffprobe"),
            ["-v", "error", "-f", "h264", "-select_streams", "v:0", "-count_frames", "-show_entries",
             "stream=nb_read_frames", "-of", "default=noprint_wrappers=1:nokey=1", rawPath], ct).ConfigureAwait(false);
        foreach (var line in output.Split('\n'))
            if (ulong.TryParse(line.Trim(), out var n)) return n;
        return 0;
    }

    private static async Task<int> DeriveBitrateAsync(string path, int outputFps, int outputArea, CancellationToken ct)
    {
        try
        {
            var (_, output) = await RunAsync(ResolveTool("ffprobe"),
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,bit_rate,avg_frame_rate:format=bit_rate",
                 "-of", "default=noprint_wrappers=1", path], ct).ConfigureAwait(false);
            var values = output.Split('\n').Select(l => l.Trim().Split('=', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Select(p => p[1]).ToList());
            double Get(string key) => values.TryGetValue(key, out var list)
                ? list.Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).FirstOrDefault(d => d > 0)
                : 0;
            var bitRate = Get("bit_rate");
            if (bitRate <= 0) return 4000;
            var w = Get("width"); var h = Get("height");
            double srcFps = 0;
            if (values.TryGetValue("avg_frame_rate", out var fr) && fr[0].Split('/') is [var a, var b] &&
                double.TryParse(a, CultureInfo.InvariantCulture, out var na) && double.TryParse(b, CultureInfo.InvariantCulture, out var nb) && nb > 0)
                srcFps = na / nb;
            var fpsRatio = srcFps > 0 ? Math.Clamp(outputFps / srcFps, 0.75, 2.0) : 1.0;
            var area = w > 0 && h > 0 ? outputArea / (w * h) : 1.0;
            var areaRatio = area < 1.0 ? Math.Max(area, 0.35) : 1.0;
            var kbps = bitRate / 1000.0 * fpsRatio * areaRatio * 1.15 + 0.5;
            return (int)Math.Clamp(kbps, 500, 12000);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return 4000;
        }
    }

    /// <summary>Makes a small looping MP4 (640x360, max 20 s) for previewing GIFs/videos inside the app.</summary>
    public static async Task<string?> CreatePreviewClipAsync(string sourcePath, string outputMp4, ConversionOptions options,
        CancellationToken ct = default)
    {
        if (DetectKind(sourcePath) == MediaKind.Image) return null;
        try
        {
            var filter = BuildFilter(options, 30).Replace(",scale=in_range=auto:out_range=full:out_color_matrix=bt709,setsar=0", "")
                         + $",scale={640}:{(int)Math.Round(640.0 * options.Height / options.Width / 2) * 2}";
            var (code, _) = await RunAsync(ResolveTool("ffmpeg"),
                ["-hide_banner", "-y", "-i", sourcePath, "-t", "20", "-vf", filter, "-an", "-c:v", "libx264", "-preset", "veryfast",
                 "-crf", "30", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outputMp4], ct).ConfigureAwait(false);
            return code == 0 && File.Exists(outputMp4) ? outputMp4 : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Extracts a preview frame from any source file (for the designer background).</summary>
    public static async Task<string?> ExtractPreviewFrameAsync(string sourcePath, string outputJpg, ConversionOptions options,
        CancellationToken ct = default)
    {
        try
        {
            var filter = BuildFilter(options, 30).Replace(",scale=in_range=auto:out_range=full:out_color_matrix=bt709,setsar=0", "");
            var (code, _) = await RunAsync(ResolveTool("ffmpeg"),
                ["-hide_banner", "-y", "-i", sourcePath, "-frames:v", "1", "-vf", filter, "-q:v", "3", outputJpg], ct).ConfigureAwait(false);
            return code == 0 && File.Exists(outputJpg) ? outputJpg : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<(int code, string output)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignored */ }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
