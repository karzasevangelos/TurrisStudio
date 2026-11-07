namespace TurrisStudio.Core.Protocol;

/// <summary>Which TRYX protocol dialect a display speaks over the USB printer-class pipe.</summary>
public enum ProtocolFamily
{
    /// <summary>Turris 620: fixed file-transfer track id, MXHD media, negotiated keepalive.</summary>
    Turris,
    /// <summary>Panorama/Stage "SCREEN" family: track-0 bootstrap with authentication, Ping keepalive, overlay lease.</summary>
    Pase,
}

/// <summary>
/// A supported TRYX display. Geometry and dialect come from the official KANALI app's device table
/// (pid → manager → screen profile) and the MIT Tryx-Linux-GUI product profiles.
/// </summary>
public sealed record DeviceModel(
    ushort ProductId,
    string Code,
    string Name,
    int Width,
    int Height,
    ProtocolFamily Family,
    bool SplitScreen,
    string DefaultPowerOnMedia,
    string DefaultStandbyMedia)
{
    public static readonly DeviceModel Turris620 = new(0x2011, "TURR", "Turris 620", 1280, 720, ProtocolFamily.Turris,
        SplitScreen: false, "default_poweron_1280x720.mp4.h264", "default_standby_1280x720.mp4.h264");

    /// <summary>Stage 360: two 4" 720×720 IPS screens driven as one 1440×720 surface (KANALI "HICU", profile stage1440).</summary>
    public static readonly DeviceModel Stage360 = new(0x1041, "HICU", "Stage 360", 1440, 720, ProtocolFamily.Pase,
        SplitScreen: true, "default_poweron_1440x720.mp4.h264", "default_standby_1440x720.mp4.h264");

    public static IReadOnlyList<DeviceModel> All { get; } = [Turris620, Stage360];

    public static DeviceModel? ForProductId(ushort productId) => All.FirstOrDefault(m => m.ProductId == productId);

    public static DeviceModel ForCode(string? code) =>
        All.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Turris620;

    /// <summary>Size of one half of a split-screen display.</summary>
    public int HalfWidth => SplitScreen ? Width / 2 : Width;

    public string SizeKey(bool half) => half && SplitScreen ? $"{HalfWidth}x{Height}" : $"{Width}x{Height}";
}
