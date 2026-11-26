namespace Walkman.Contracts.Devices;

public enum SupportStatus
{
    Verified,
    Experimental,
    Unsupported
}

/// <summary>
/// USB Vendor/Product identifier pair registered for a Sony Walkman series.
/// </summary>
public sealed record UsbIdentifier(
    ushort VendorId,
    ushort ProductId,
    string UsbIdsName);

/// <summary>
/// Partition size rule used to disambiguate capacity variants sharing a USB PID.
/// </summary>
public sealed record CapacityTierRule(
    string ModelCode,
    int NominalCapacityMb,
    long MinBytes,
    long MaxBytes,
    bool HasFmTuner);

/// <summary>
/// Physical casing color variant metadata used by UI theming engines.
/// </summary>
public sealed record ColorVariantInfo(
    string SuffixCode,
    string MarketingName,
    string PrimaryHex,
    string AccentHex,
    string OledGlowHex);

/// <summary>
/// Hardware OLED/LCD display and .nwm screensaver constraints for a device series.
/// </summary>
public sealed record ScreenSpecification(
    int LogicalWidth,
    int LogicalHeight,
    int PhysicalFrameWidth,
    int PhysicalFrameHeight,
    int BitsPerPixel,
    int BytesPerFrame,
    ScreensaverMagic Magic,
    bool ColumnStripeMangled,
    int MaxFileSizeBytes,
    int MaxUniqueBitmaps,
    IReadOnlyList<string> TargetFolders,
    int? MaxFrameCount = null,
    bool EnforceStrictFileSizeCap = true);

/// <summary>
/// Per-codec playback constraints and required EA3 protection mode on a device.
/// </summary>
public sealed record CodecCapability(
    AudioCodecId Codec,
    IReadOnlyList<int> SupportedBitratesKbps,
    IReadOnlyList<int> AllowedSampleRatesHz,
    bool SupportsVbr,
    OmaEncryptionMode RequiredEncryption);

/// <summary>
/// Filesystem directory and OMGAUDIO table markers used during multi-factor identification.
/// </summary>
public sealed record FilesystemMarkers(
    IReadOnlyList<string> RequiredFolders,
    IReadOnlyList<string> OptionalFolders,
    IReadOnlyList<string> RequiredTableMarkers,
    IReadOnlyList<string> ExcludedFolders);

/// <summary>
/// Declarative capability profile for a Sony Walkman series loaded from manifests/devices.json.
/// </summary>
public sealed record StrictFat16BpbRule(
    ushort RequiredReservedSectors,
    bool WarnOnWindowsOem);

public enum BpbVerificationState
{
    VerifiedOk = 0,
    InvalidWindowsFormat = 1,
    UnverifiedAccessDenied = 2
}

public sealed record VolumeBpbDiagnostics(
    string OemName,
    ushort BytesPerSector,
    byte SectorsPerCluster,
    ushort ReservedSectors,
    byte FatCount,
    string FileSystemType,
    bool HasWindowsFormatWarning,
    string? WarningMessage,
    BpbVerificationState VerificationState = BpbVerificationState.VerifiedOk);

public sealed record DeviceProfile(
    string SeriesId,
    string DisplayName,
    WalkmanGeneration Generation,
    TransportProtocol Transport,
    DatabaseFormat DatabaseFormat,
    bool RequiresHardwareDvIdKey,
    ushort? ExpectedDvIdSegment,
    IReadOnlyList<UsbIdentifier> UsbIds,
    IReadOnlyList<CapacityTierRule> CapacityTiers,
    IReadOnlyList<ColorVariantInfo> ColorVariants,
    IReadOnlyList<CodecCapability> Codecs,
    ScreenSpecification? Screen,
    FilesystemMarkers Markers,
    IReadOnlyList<string> RequiredL2Modules,
    IReadOnlyList<string> SupportedL3Modules,
    bool RequiresScsiAuthForDvIdFallback = false,
    StrictFat16BpbRule? StrictFactoryFat16Bpb = null,
    string? BootstrapSchemaProfile = null,
    SupportStatus SupportStatus = SupportStatus.Experimental);

/// <summary>
/// Raw hardware and filesystem telemetry collected by L1.DevicePoller for resolution.
/// </summary>
public sealed record DeviceDetectionProbe(
    ushort VendorId,
    ushort ProductId,
    string? UsbProductString,
    string? VolumeRootPath,
    uint? VolumeSerialNumber,
    long? TotalVolumeBytes,
    ushort? HardwareDvIdSegment,
    uint? HardwareDvIdKey,
    IReadOnlyList<string> PresentRootFolders,
    IReadOnlyList<string> PresentOmgAudioFiles,
    VolumeBpbDiagnostics? BpbDiagnostics = null);

/// <summary>
/// Resolved active device session bound to a mounted volume serial and hardware key.
/// </summary>
public sealed record ConnectedDeviceIdentity(
    string DeviceInstanceId,
    DeviceProfile Profile,
    string ResolvedModelCode,
    string? ColorSuffix,
    string? VolumeRootPath,
    uint? VolumeSerialNumber,
    uint? HardwareDvIdKey,
    byte[]? RawDvIdBytes,
    VolumeBpbDiagnostics? BpbDiagnostics = null);

/// <summary>
/// Outcome of multi-factor device manifest resolution.
/// </summary>
public sealed record DeviceResolutionResult(
    bool IsRecognized,
    ConnectedDeviceIdentity? Identity,
    string? UnrecognizedReason);
