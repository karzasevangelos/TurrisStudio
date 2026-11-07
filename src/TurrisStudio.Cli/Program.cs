using TurrisStudio.Core.Media;
using TurrisStudio.Core.Overlay;
using TurrisStudio.Core.Protocol;
using TurrisStudio.Core.Sensors;
using TurrisStudio.Core.Usb;

// Small diagnostic tool:  turris probe | sensors | list | upload <file> | show <name> | overlay [seconds]
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";

if (command == "sensors")
{
    using var hub = new SensorHub { PreferBuiltIn = args.Contains("--builtin") };
    for (var i = 0; i < 20 && !hub.BuiltIn.IsAvailable; i++) { hub.Poll(); Thread.Sleep(500); }
    hub.Poll();
    Console.WriteLine($"HWiNFO: {hub.HwInfo.Status}");
    Console.WriteLine($"Built-in: {hub.BuiltIn.Status} (elevated: {BuiltInSource.IsElevated})");
    foreach (var r in hub.Snapshot.Values.Where(r => r.Source is not ("HWiNFO" or "Built-in") || args.Contains("--all")).OrderBy(r => r.Key))
        Console.WriteLine($"{r.Key,-45} {r.Group,-34} {r.Name,-26} {r.Value,10:F1} {r.Unit}");
    return 0;
}

var devices = UsbPrinterTransport.Discover();
Console.WriteLine($"Found {devices.Count} TRYX printer-class device(s):");
foreach (var d in devices) Console.WriteLine($"  {d.VendorId:x4}:{d.ProductId:x4}  {d.DevicePath}");
var device = devices.FirstOrDefault(d => d.IsSupported);
if (device == null) return 1;

using var client = await TryxClient.ConnectAsync(device, Console.WriteLine);
Console.WriteLine($"Keepalive required: {client.KeepaliveRequired}, overlay supported: {client.OverlaySupported}");
var sys = client.SystemConfig;
if (sys != null)
    Console.WriteLine($"SysConfig: product={sys.ReportedProduct} poweron={sys.PoweronMediaFile} standby={sys.StandbyMediaFile} " +
                      $"decoder={sys.VideoDecoder?.Width}x{sys.VideoDecoder?.Height} output={sys.VideoOutput?.Width}x{sys.VideoOutput?.Height}");

switch (command)
{
    case "probe":
    case "list":
        foreach (var f in await client.GetMediaCatalogAsync())
            Console.WriteLine($"  {(f.IsPreset ? "preset" : "user  ")} {f.Size,10} {f.Name}");
        var cfg = await client.TryReadUserConfigurationAsync();
        Console.WriteLine("UserConfig: " + (cfg?.ToString() ?? "(not answered)"));
        break;

    case "upload":
    {
        var prepared = await MediaConverter.PrepareAsync(args[1], new ConversionOptions { Width = client.Model.Width, Height = client.Model.Height }, Path.Combine(Path.GetTempPath(), "turris"),
            new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Prepared {prepared.RemoteName}: {prepared.Blob.Length} bytes, {prepared.FrameCount} frames");
        await client.UploadAsync(prepared.RemoteName, prepared.Blob, new Progress<double>(p => Console.Write($"\r{p:P0}   ")));
        Console.WriteLine("\nUploaded. Showing it...");
        await client.ApplyAsync(new DisplayState { MediaFile = prepared.RemoteName, Brightness = 80 }, null, restartPlayback: true);
        break;
    }

    case "show":
        await client.ApplyAsync(new DisplayState { MediaFile = args[1], Brightness = 80 }, null, restartPlayback: true);
        break;

    case "overlay":
    {
        var seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
        var hub = new SensorHub();
        var design = WidgetPresets.Default();
        design.Widgets.Add(WidgetPresets.Clock(800, 40));
        hub.Poll();
        await client.SetOverlayLayoutAsync(OverlayCompiler.BuildLayout(design, hub, DateTime.Now));
        for (var i = 0; i < seconds; i++)
        {
            await Task.Delay(1000);
            hub.Poll();
            await client.SendMetricsAsync(OverlayCompiler.BuildMetrics(design, hub, DateTime.Now));
            if (client.KeepaliveRequired && i % 2 == 0) await client.PingAsync();
            Console.Write($"\rtick {i + 1}/{seconds}  CPU {hub.Get("auto:cpu_temp")?.Value:F0}°C   ");
        }
        Console.WriteLine();
        break;
    }
}
return 0;
