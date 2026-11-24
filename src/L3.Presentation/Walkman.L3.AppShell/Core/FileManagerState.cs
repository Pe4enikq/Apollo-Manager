using Walkman.Contracts.Devices;

namespace Walkman.L3.AppShell.Core;

public enum UploadMode
{
    Quality,
    Faster,
    Auto,
    Custom
}

public enum TransferControlKind
{
    Swap,
    UploadMode,
    CustomCodec,
    NormalizeVolume,
    RewriteTables,
    RewriteLock,
    SafeEjectAfter,
    RememberChoice
}

public enum UnderGlowTone
{
    None,
    Green,
    Blue
}

public sealed record CustomCodecSpec(
    string DisplayName,
    AudioCodecId Codec,
    int BitrateKbps);

public sealed record FileManagerTransferConfig(
    string SourceLocationId,
    string DestLocationId,
    UploadMode UploadMode,
    string CustomCodec,
    bool NormalizeVolume,
    bool RewriteTables,
    bool RewriteLock,
    bool SafeEjectAfter,
    bool RememberChoice)
{
    public static FileManagerTransferConfig Default => new(
        SourceLocationId: "pc-library",
        DestLocationId: "nw-hd5",
        UploadMode: UploadMode.Auto,
        CustomCodec: "ATRAC3+ 256k",
        NormalizeVolume: true,
        RewriteTables: false,
        RewriteLock: false,
        SafeEjectAfter: false,
        RememberChoice: false);
}

public static class FileManagerStateLogic
{
    public static readonly IReadOnlyList<CustomCodecSpec> CustomCodecSpecs =
    [
        new("ATRAC3+ 352k", AudioCodecId.Atrac3Plus, 352),
        new("ATRAC3+ 256k", AudioCodecId.Atrac3Plus, 256),
        new("ATRAC3+ 192k", AudioCodecId.Atrac3Plus, 192),
        new("ATRAC3+ 128k", AudioCodecId.Atrac3Plus, 128),
        new("MP3 320k CBR", AudioCodecId.Mp3, 320),
        new("MP3 192k CBR", AudioCodecId.Mp3, 192),
        new("AAL (Import-Only)", AudioCodecId.Atrac3PlusLossless, 1411),
        new("Linear PCM", AudioCodecId.LinearPcm, 1411)
    ];

    public static FileManagerTransferConfig SwapTransferEndpoints(FileManagerTransferConfig config) =>
        config with
        {
            SourceLocationId = config.DestLocationId,
            DestLocationId = config.SourceLocationId
        };

    public static bool IsTransferControlLocked(bool rememberChoice, TransferControlKind control)
    {
        if (!rememberChoice)
        {
            return false;
        }

        return control is not (TransferControlKind.Swap or TransferControlKind.CustomCodec or TransferControlKind.RememberChoice);
    }

    public static UnderGlowTone GetControlUnderGlow(bool isActive, bool isLocked)
    {
        if (!isActive)
        {
            return UnderGlowTone.None;
        }

        return isLocked ? UnderGlowTone.Blue : UnderGlowTone.Green;
    }

    public static string DescribeEffectiveCodec(FileManagerTransferConfig config) => config.UploadMode switch
    {
        UploadMode.Quality => "Max Player Quality (ATRAC3+ 352k / PCM)",
        UploadMode.Faster => "Optimized ATRAC3+ 192k",
        UploadMode.Auto => "Auto (Native passthrough + ATRAC3+ fallback)",
        UploadMode.Custom => $"Custom ({config.CustomCodec})",
        _ => "Auto"
    };

    public static string GetModeHint(UploadMode mode) => mode switch
    {
        UploadMode.Quality => "Max fidelity for player",
        UploadMode.Faster => "Optimized ATRAC3+",
        UploadMode.Auto => "Convert only unsupported",
        UploadMode.Custom => "Manual codec & bitrate",
        _ => string.Empty
    };
}
