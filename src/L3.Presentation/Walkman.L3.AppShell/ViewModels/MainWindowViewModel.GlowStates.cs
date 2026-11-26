using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static bool IsGreen(bool active, bool locked) => active && !locked;
    private static bool IsBlue(bool active, bool locked) => active && locked;
    private static double DimOpacity(bool active, bool locked) => locked && !active ? 0.45 : 1.0;

    // File Manager Glow & Opacity Helpers
    public bool FmQualityGreen => IsGreen(IsUploadQuality, IsFmModeLocked);
    public bool FmQualityBlue => IsBlue(IsUploadQuality, IsFmModeLocked);
    public double FmQualityOpacity => DimOpacity(IsUploadQuality, IsFmModeLocked);

    public bool FmFasterGreen => IsGreen(IsUploadFaster, IsFmModeLocked);
    public bool FmFasterBlue => IsBlue(IsUploadFaster, IsFmModeLocked);
    public double FmFasterOpacity => DimOpacity(IsUploadFaster, IsFmModeLocked);

    public bool FmAutoGreen => IsGreen(IsUploadAuto, IsFmModeLocked);
    public bool FmAutoBlue => IsBlue(IsUploadAuto, IsFmModeLocked);
    public double FmAutoOpacity => DimOpacity(IsUploadAuto, IsFmModeLocked);

    public bool FmCustomGreen => IsGreen(IsUploadCustom, IsFmModeLocked);
    public bool FmCustomBlue => IsBlue(IsUploadCustom, IsFmModeLocked);
    public double FmCustomOpacity => DimOpacity(IsUploadCustom, IsFmModeLocked);

    public bool FmNormGreen => IsGreen(FmNormalizeVolume, IsFmNormLocked);
    public bool FmNormBlue => IsBlue(FmNormalizeVolume, IsFmNormLocked);
    public double FmNormOpacity => DimOpacity(FmNormalizeVolume, IsFmNormLocked);
    public string FmNormStateText => FmNormalizeVolume ? "ON" : "OFF";

    public bool FmTablesGreen => IsGreen(FmRewriteTables, IsFmTablesLocked);
    public bool FmTablesBlue => IsBlue(FmRewriteTables, IsFmTablesLocked);
    public double FmTablesOpacity => DimOpacity(FmRewriteTables, IsFmTablesLocked);
    public string FmTablesStateText => FmRewriteTables ? "ON" : "OFF";

    public bool FmLockGreen => IsGreen(FmRewriteLock, IsFmLockTrackLocked);
    public bool FmLockBlue => IsBlue(FmRewriteLock, IsFmLockTrackLocked);
    public double FmLockOpacity => DimOpacity(FmRewriteLock, IsFmLockTrackLocked);
    public string FmLockStateText => FmRewriteLock ? "ON" : "OFF";

    public bool FmEjectGreen => IsGreen(FmSafeEjectAfter, IsFmEjectLocked);
    public bool FmEjectBlue => IsBlue(FmSafeEjectAfter, IsFmEjectLocked);
    public double FmEjectOpacity => DimOpacity(FmSafeEjectAfter, IsFmEjectLocked);
    public string FmEjectStateText => FmSafeEjectAfter ? "ON" : "OFF";

    // Codec Converter Glow & Opacity Helpers
    public bool ConvAtracGreen => IsGreen(IsTargetAtrac3Plus, IsConvCodecLocked);
    public bool ConvAtracBlue => IsBlue(IsTargetAtrac3Plus, IsConvCodecLocked);
    public double ConvAtracOpacity => DimOpacity(IsTargetAtrac3Plus, IsConvCodecLocked);

    public bool ConvAtrac3Green => IsGreen(IsTargetAtrac3, IsConvCodecLocked);
    public bool ConvAtrac3Blue => IsBlue(IsTargetAtrac3, IsConvCodecLocked);
    public double ConvAtrac3Opacity => DimOpacity(IsTargetAtrac3, IsConvCodecLocked);

    public bool ConvAtrac1Green => IsGreen(IsTargetAtrac1, IsConvCodecLocked);
    public bool ConvAtrac1Blue => IsBlue(IsTargetAtrac1, IsConvCodecLocked);
    public double ConvAtrac1Opacity => DimOpacity(IsTargetAtrac1, IsConvCodecLocked);

    public bool ConvMp3Green => IsGreen(IsTargetMp3, IsConvCodecLocked);
    public bool ConvMp3Blue => IsBlue(IsTargetMp3, IsConvCodecLocked);
    public double ConvMp3Opacity => DimOpacity(IsTargetMp3, IsConvCodecLocked);

    public bool ConvAalGreen => IsGreen(IsTargetAal, IsConvCodecLocked);
    public bool ConvAalBlue => IsBlue(IsTargetAal, IsConvCodecLocked);
    public double ConvAalOpacity => DimOpacity(IsTargetAal, IsConvCodecLocked);

    public bool ConvAacGreen => ConvAalGreen;
    public bool ConvAacBlue => ConvAalBlue;
    public double ConvAacOpacity => ConvAalOpacity;

    public bool ConvFlacGreen => IsGreen(IsTargetFlac, IsConvCodecLocked);
    public bool ConvFlacBlue => IsBlue(IsTargetFlac, IsConvCodecLocked);
    public double ConvFlacOpacity => DimOpacity(IsTargetFlac, IsConvCodecLocked);

    public bool ConvWavGreen => IsGreen(IsTargetWav, IsConvCodecLocked);
    public bool ConvWavBlue => IsBlue(IsTargetWav, IsConvCodecLocked);
    public double ConvWavOpacity => DimOpacity(IsTargetWav, IsConvCodecLocked);

    public bool Conv352Green => IsGreen(IsBitrate352, IsConvBitrateLocked);
    public bool Conv352Blue => IsBlue(IsBitrate352, IsConvBitrateLocked);
    public double Conv352Opacity => DimOpacity(IsBitrate352, IsConvBitrateLocked);

    public bool Conv292Green => IsGreen(IsBitrate292, IsConvBitrateLocked);
    public bool Conv292Blue => IsBlue(IsBitrate292, IsConvBitrateLocked);
    public double Conv292Opacity => DimOpacity(IsBitrate292, IsConvBitrateLocked);

    public bool Conv256Green => IsGreen(IsBitrate256, IsConvBitrateLocked);
    public bool Conv256Blue => IsBlue(IsBitrate256, IsConvBitrateLocked);
    public double Conv256Opacity => DimOpacity(IsBitrate256, IsConvBitrateLocked);

    public bool Conv192Green => IsGreen(IsBitrate192, IsConvBitrateLocked);
    public bool Conv192Blue => IsBlue(IsBitrate192, IsConvBitrateLocked);
    public double Conv192Opacity => DimOpacity(IsBitrate192, IsConvBitrateLocked);

    public bool Conv132Green => IsGreen(IsBitrate132, IsConvBitrateLocked);
    public bool Conv132Blue => IsBlue(IsBitrate132, IsConvBitrateLocked);
    public double Conv132Opacity => DimOpacity(IsBitrate132, IsConvBitrateLocked);

    public bool Conv128Green => IsGreen(IsBitrate128, IsConvBitrateLocked);
    public bool Conv128Blue => IsBlue(IsBitrate128, IsConvBitrateLocked);
    public double Conv128Opacity => DimOpacity(IsBitrate128, IsConvBitrateLocked);

    public bool Conv105Green => IsGreen(IsBitrate105, IsConvBitrateLocked);
    public bool Conv105Blue => IsBlue(IsBitrate105, IsConvBitrateLocked);
    public double Conv105Opacity => DimOpacity(IsBitrate105, IsConvBitrateLocked);

    public bool Conv66Green => IsGreen(IsBitrate66, IsConvBitrateLocked);
    public bool Conv66Blue => IsBlue(IsBitrate66, IsConvBitrateLocked);
    public double Conv66Opacity => DimOpacity(IsBitrate66, IsConvBitrateLocked);

    public bool Conv441Green => IsGreen(IsRate44100, IsConvRateLocked);
    public bool Conv441Blue => IsBlue(IsRate44100, IsConvRateLocked);
    public double Conv441Opacity => DimOpacity(IsRate44100, IsConvRateLocked);

    public bool Conv480Green => IsGreen(IsRate48000, IsConvRateLocked);
    public bool Conv480Blue => IsBlue(IsRate48000, IsConvRateLocked);
    public double Conv480Opacity => DimOpacity(IsRate48000, IsConvRateLocked);

    public bool ConvNormGreen => IsGreen(ConvNormalizeVolume, IsConvNormLocked);
    public bool ConvNormBlue => IsBlue(ConvNormalizeVolume, IsConvNormLocked);
    public double ConvNormOpacity => DimOpacity(ConvNormalizeVolume, IsConvNormLocked);
    public string ConvNormStateText => ConvNormalizeVolume ? "ON" : "OFF";

    public bool ConvMetaGreen => IsGreen(ConvPreserveMetadata, IsConvMetaLocked);
    public bool ConvMetaBlue => IsBlue(ConvPreserveMetadata, IsConvMetaLocked);
    public double ConvMetaOpacity => DimOpacity(ConvPreserveMetadata, IsConvMetaLocked);
    public string ConvMetaStateText => ConvPreserveMetadata ? "ON" : "OFF";

    public bool ConvKeepGreen => IsGreen(ConvKeepSourceFiles, IsConvKeepLocked);
    public bool ConvKeepBlue => IsBlue(ConvKeepSourceFiles, IsConvKeepLocked);
    public double ConvKeepOpacity => DimOpacity(ConvKeepSourceFiles, IsConvKeepLocked);
    public string ConvKeepStateText => ConvKeepSourceFiles ? "ON" : "OFF";

    public bool ConvAddGreen => IsGreen(ConvAddToLibrary, IsConvAddLocked);
    public bool ConvAddBlue => IsBlue(ConvAddToLibrary, IsConvAddLocked);
    public double ConvAddOpacity => DimOpacity(ConvAddToLibrary, IsConvAddLocked);
    public string ConvAddStateText => ConvAddToLibrary ? "ON" : "OFF";

    // Transfer Job Dock Computed Text
    public string TransferJobProgressText => TransferJob is null ? string.Empty : $"{TransferJob.CompletedCount}/{TransferJob.TotalCount} · {TransferJob.ProgressPct}%";
    public string TransferJobEtaText => TransferJob is null
        ? string.Empty
        : (TransferJob.IsError
            ? "Failed"
            : (TransferJob.IsCompleted ? "Finished" : $"ETA {AudioTrackItem.FormatDuration(TransferJob.EtaSeconds)}"));
    public string TransferJobStageCodec => TransferJob is null
        ? string.Empty
        : $"{(TransferJob.IsError ? MainWindowViewModel.SanitizeTransferErrorForDock(TransferJob.StageLabel) : TransferJob.StageLabel)} · {TransferJob.CodecLabel}";
    public string? TransferJobErrorTooltip => string.IsNullOrWhiteSpace(TransferJob?.ErrorText) ? null : TransferJob.ErrorText;
    public int TransferJobProgressPct => TransferJob?.ProgressPct ?? 0;

    partial void OnFmNormalizeVolumeChanged(bool value) => NotifyFmStates();
    partial void OnFmRewriteTablesChanged(bool value) => NotifyFmStates();
    partial void OnFmRewriteLockChanged(bool value) => NotifyFmStates();
    partial void OnFmSafeEjectAfterChanged(bool value) => NotifyFmStates();

    partial void OnConvNormalizeVolumeChanged(bool value) => NotifyConvStates();
    partial void OnConvPreserveMetadataChanged(bool value) => NotifyConvStates();
    partial void OnConvKeepSourceFilesChanged(bool value) => NotifyConvStates();
    partial void OnConvAddToLibraryChanged(bool value) => NotifyConvStates();

    private void NotifyFmStates()
    {
        OnPropertyChanged(nameof(FmQualityGreen)); OnPropertyChanged(nameof(FmQualityBlue)); OnPropertyChanged(nameof(FmQualityOpacity));
        OnPropertyChanged(nameof(FmFasterGreen)); OnPropertyChanged(nameof(FmFasterBlue)); OnPropertyChanged(nameof(FmFasterOpacity));
        OnPropertyChanged(nameof(FmAutoGreen)); OnPropertyChanged(nameof(FmAutoBlue)); OnPropertyChanged(nameof(FmAutoOpacity));
        OnPropertyChanged(nameof(FmCustomGreen)); OnPropertyChanged(nameof(FmCustomBlue)); OnPropertyChanged(nameof(FmCustomOpacity));
        OnPropertyChanged(nameof(FmNormGreen)); OnPropertyChanged(nameof(FmNormBlue)); OnPropertyChanged(nameof(FmNormOpacity)); OnPropertyChanged(nameof(FmNormStateText));
        OnPropertyChanged(nameof(FmTablesGreen)); OnPropertyChanged(nameof(FmTablesBlue)); OnPropertyChanged(nameof(FmTablesOpacity)); OnPropertyChanged(nameof(FmTablesStateText));
        OnPropertyChanged(nameof(FmLockGreen)); OnPropertyChanged(nameof(FmLockBlue)); OnPropertyChanged(nameof(FmLockOpacity)); OnPropertyChanged(nameof(FmLockStateText));
        OnPropertyChanged(nameof(FmEjectGreen)); OnPropertyChanged(nameof(FmEjectBlue)); OnPropertyChanged(nameof(FmEjectOpacity)); OnPropertyChanged(nameof(FmEjectStateText));
    }

    private void NotifyConvStates()
    {
        OnPropertyChanged(nameof(ConvAtracGreen)); OnPropertyChanged(nameof(ConvAtracBlue)); OnPropertyChanged(nameof(ConvAtracOpacity));
        OnPropertyChanged(nameof(ConvAtrac3Green)); OnPropertyChanged(nameof(ConvAtrac3Blue)); OnPropertyChanged(nameof(ConvAtrac3Opacity));
        OnPropertyChanged(nameof(ConvAtrac1Green)); OnPropertyChanged(nameof(ConvAtrac1Blue)); OnPropertyChanged(nameof(ConvAtrac1Opacity));
        OnPropertyChanged(nameof(ConvMp3Green)); OnPropertyChanged(nameof(ConvMp3Blue)); OnPropertyChanged(nameof(ConvMp3Opacity));
        OnPropertyChanged(nameof(ConvAalGreen)); OnPropertyChanged(nameof(ConvAalBlue)); OnPropertyChanged(nameof(ConvAalOpacity));
        OnPropertyChanged(nameof(ConvAacGreen)); OnPropertyChanged(nameof(ConvAacBlue)); OnPropertyChanged(nameof(ConvAacOpacity));
        OnPropertyChanged(nameof(ConvFlacGreen)); OnPropertyChanged(nameof(ConvFlacBlue)); OnPropertyChanged(nameof(ConvFlacOpacity));
        OnPropertyChanged(nameof(ConvWavGreen)); OnPropertyChanged(nameof(ConvWavBlue)); OnPropertyChanged(nameof(ConvWavOpacity));
        OnPropertyChanged(nameof(Conv352Green)); OnPropertyChanged(nameof(Conv352Blue)); OnPropertyChanged(nameof(Conv352Opacity));
        OnPropertyChanged(nameof(Conv292Green)); OnPropertyChanged(nameof(Conv292Blue)); OnPropertyChanged(nameof(Conv292Opacity));
        OnPropertyChanged(nameof(Conv256Green)); OnPropertyChanged(nameof(Conv256Blue)); OnPropertyChanged(nameof(Conv256Opacity));
        OnPropertyChanged(nameof(Conv192Green)); OnPropertyChanged(nameof(Conv192Blue)); OnPropertyChanged(nameof(Conv192Opacity));
        OnPropertyChanged(nameof(Conv132Green)); OnPropertyChanged(nameof(Conv132Blue)); OnPropertyChanged(nameof(Conv132Opacity));
        OnPropertyChanged(nameof(Conv128Green)); OnPropertyChanged(nameof(Conv128Blue)); OnPropertyChanged(nameof(Conv128Opacity));
        OnPropertyChanged(nameof(Conv105Green)); OnPropertyChanged(nameof(Conv105Blue)); OnPropertyChanged(nameof(Conv105Opacity));
        OnPropertyChanged(nameof(Conv66Green)); OnPropertyChanged(nameof(Conv66Blue)); OnPropertyChanged(nameof(Conv66Opacity));
        OnPropertyChanged(nameof(Conv441Green)); OnPropertyChanged(nameof(Conv441Blue)); OnPropertyChanged(nameof(Conv441Opacity));
        OnPropertyChanged(nameof(Conv480Green)); OnPropertyChanged(nameof(Conv480Blue)); OnPropertyChanged(nameof(Conv480Opacity));
        OnPropertyChanged(nameof(ConvNormGreen)); OnPropertyChanged(nameof(ConvNormBlue)); OnPropertyChanged(nameof(ConvNormOpacity)); OnPropertyChanged(nameof(ConvNormStateText));
        OnPropertyChanged(nameof(ConvMetaGreen)); OnPropertyChanged(nameof(ConvMetaBlue)); OnPropertyChanged(nameof(ConvMetaOpacity)); OnPropertyChanged(nameof(ConvMetaStateText));
        OnPropertyChanged(nameof(ConvKeepGreen)); OnPropertyChanged(nameof(ConvKeepBlue)); OnPropertyChanged(nameof(ConvKeepOpacity)); OnPropertyChanged(nameof(ConvKeepStateText));
        OnPropertyChanged(nameof(ConvAddGreen)); OnPropertyChanged(nameof(ConvAddBlue)); OnPropertyChanged(nameof(ConvAddOpacity)); OnPropertyChanged(nameof(ConvAddStateText));
    }
}
