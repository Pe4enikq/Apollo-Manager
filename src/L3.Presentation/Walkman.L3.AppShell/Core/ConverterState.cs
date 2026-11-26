namespace Walkman.L3.AppShell.Core;

public enum TargetCodec
{
    Atrac3Plus,
    Atrac3,
    Atrac1,
    Mp3,
    Aal,
    Aac = Aal,
    Flac,
    Wav
}

public enum BitrateOption
{
    K352,
    K292,
    K256,
    K192,
    K132,
    K128,
    K105,
    K66,
    Lossless
}

public enum SampleRateOption
{
    Hz44100,
    Hz48000
}

public enum ConverterControlKind
{
    TargetCodec,
    Bitrate,
    SampleRate,
    NormalizeVolume,
    PreserveMetadata,
    KeepSource,
    AddToLibrary,
    SourceFolder,
    OutputFolder,
    RememberChoice
}

public sealed record OfflineConverterConfig(
    string SourceLocationId,
    string OutputLocationId,
    TargetCodec TargetCodec,
    BitrateOption Bitrate,
    SampleRateOption SampleRate,
    bool NormalizeVolume,
    bool PreserveMetadata,
    bool KeepSourceFiles,
    bool AddToLibrary,
    bool RememberChoice)
{
    public static OfflineConverterConfig Default => new(
        SourceLocationId: "pc-library",
        OutputLocationId: "pc-converted",
        TargetCodec: TargetCodec.Atrac3Plus,
        Bitrate: BitrateOption.K256,
        SampleRate: SampleRateOption.Hz44100,
        NormalizeVolume: true,
        PreserveMetadata: true,
        KeepSourceFiles: true,
        AddToLibrary: true,
        RememberChoice: false);
}

public static class ConverterStateLogic
{
    public static bool IsLosslessCodec(TargetCodec codec) =>
        codec is TargetCodec.Flac or TargetCodec.Wav or TargetCodec.Aal;

    public static string GetCodecLabel(TargetCodec codec) => codec switch
    {
        TargetCodec.Atrac3Plus => "ATRAC3+",
        TargetCodec.Atrac3 => "ATRAC3",
        TargetCodec.Atrac1 => "ATRAC1 (SP)",
        TargetCodec.Mp3 => "MP3",
        TargetCodec.Aal => "AAL",
        TargetCodec.Flac => "FLAC",
        TargetCodec.Wav => "WAV",
        _ => "ATRAC3+"
    };

    public static string GetCodecHint(TargetCodec codec) => codec switch
    {
        TargetCodec.Atrac3Plus => "Sony Walkman native (48–352k)",
        TargetCodec.Atrac3 => "Legacy Walkman / NetMD LP2/LP4 (66/105/132k)",
        TargetCodec.Atrac1 => "Legacy MiniDisc / NetMD SP (292k .aea)",
        TargetCodec.Mp3 => "Universal playback",
        TargetCodec.Aal => "ATRAC Lossless (Import/Decode-Only)",
        TargetCodec.Flac => "Lossless archive",
        TargetCodec.Wav => "Uncompressed PCM",
        _ => string.Empty
    };

    public static string GetOutputExtension(TargetCodec codec) => codec switch
    {
        TargetCodec.Atrac3Plus => ".oma",
        TargetCodec.Atrac3 => ".oma",
        TargetCodec.Atrac1 => ".aea",
        TargetCodec.Mp3 => ".mp3",
        TargetCodec.Aal => ".oma",
        TargetCodec.Flac => ".flac",
        TargetCodec.Wav => ".wav",
        _ => ".oma"
    };

    public static string GetBitrateLabel(BitrateOption opt) => opt switch
    {
        BitrateOption.K352 => "352k",
        BitrateOption.K292 => "292k (SP)",
        BitrateOption.K256 => "256k",
        BitrateOption.K192 => "192k",
        BitrateOption.K132 => "132k (LP2)",
        BitrateOption.K128 => "128k",
        BitrateOption.K105 => "105k",
        BitrateOption.K66 => "66k (LP4)",
        BitrateOption.Lossless => "Lossless",
        _ => "256k"
    };

    public static int GetBitrateKbps(BitrateOption opt) => opt switch
    {
        BitrateOption.K352 => 352,
        BitrateOption.K292 => 292,
        BitrateOption.K256 => 256,
        BitrateOption.K192 => 192,
        BitrateOption.K132 => 132,
        BitrateOption.K128 => 128,
        BitrateOption.K105 => 105,
        BitrateOption.K66 => 66,
        BitrateOption.Lossless => 1411,
        _ => 256
    };

    public static IReadOnlyList<BitrateOption> GetSupportedBitrates(TargetCodec codec) => codec switch
    {
        TargetCodec.Atrac3 => [BitrateOption.K132, BitrateOption.K105, BitrateOption.K66],
        TargetCodec.Atrac1 => [BitrateOption.K292],
        TargetCodec.Flac or TargetCodec.Wav or TargetCodec.Aal => [BitrateOption.Lossless],
        TargetCodec.Mp3 => [BitrateOption.K256, BitrateOption.K192, BitrateOption.K128],
        _ => [BitrateOption.K352, BitrateOption.K256, BitrateOption.K192, BitrateOption.K128]
    };

    public static string GetSampleRateLabel(SampleRateOption rate) => rate switch
    {
        SampleRateOption.Hz44100 => "44.1 kHz",
        SampleRateOption.Hz48000 => "48 kHz",
        _ => "44.1 kHz"
    };

    public static bool IsConverterControlLocked(bool rememberChoice, ConverterControlKind control)
    {
        if (!rememberChoice)
        {
            return false;
        }

        return control is not (ConverterControlKind.SourceFolder or ConverterControlKind.OutputFolder or ConverterControlKind.RememberChoice);
    }

    public static UnderGlowTone GetConverterGlow(bool isActive, bool isLocked) =>
        FileManagerStateLogic.GetControlUnderGlow(isActive, isLocked);

    public static double EstimateConvertedSizeMb(double durationSec, TargetCodec targetCodec, BitrateOption bitrate)
    {
        if (targetCodec == TargetCodec.Wav)
        {
            return Math.Round((durationSec * 1411.0) / 8.0 / 1024.0, 1);
        }

        if (targetCodec == TargetCodec.Flac)
        {
            return Math.Round((durationSec * 820.0) / 8.0 / 1024.0, 1);
        }

        if (targetCodec == TargetCodec.Aal)
        {
            return Math.Round((durationSec * 860.0) / 8.0 / 1024.0, 1);
        }

        int kbps = bitrate == BitrateOption.Lossless ? 256 : GetBitrateKbps(bitrate);
        return Math.Round(Math.Max(0.8, (durationSec * kbps) / 8.0 / 1024.0), 1);
    }

    public static string DescribeConverterProfile(OfflineConverterConfig config)
    {
        string rate = GetSampleRateLabel(config.SampleRate);
        string codec = GetCodecLabel(config.TargetCodec);
        if (IsLosslessCodec(config.TargetCodec))
        {
            return $"{codec} Lossless · {rate}";
        }

        return $"{codec} {GetBitrateLabel(config.Bitrate)} · {rate}";
    }
}
