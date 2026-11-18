using Walkman.Contracts.Devices;

namespace Walkman.Contracts.Engines;

/// <summary>
/// Mounted storage capacity and FAT serial metadata returned by a transport driver.
/// </summary>
public sealed record VolumeSpaceInfo(
    long TotalBytes,
    long FreeBytes,
    uint VolumeSerialNumber);

/// <summary>
/// Canonical track metadata record stored in 04CNTINF.DAT and .oma headers.
/// </summary>
public sealed record AudioTrackDto(
    int TrackId,
    string Title,
    string Artist,
    string Album,
    string Genre,
    string? SortTitle,
    int TrackNumber,
    int Year,
    uint DurationMs,
    uint FrameCount,
    AudioCodecId Codec,
    int BitrateKbps,
    OmaEncryptionMode EncryptionMode,
    uint FilePropertiesBigEndian,
    byte[]? LsiKeyring48);

/// <summary>
/// Album or folder group entry stored in 01TREE*.DAT and 03GINF*.DAT.
/// </summary>
public sealed record TrackGroupDto(
    int GroupId,
    string Title,
    string Artist,
    string Genre,
    uint TotalDurationMs,
    IReadOnlyList<int> TrackIds);

/// <summary>
/// Read-only snapshot of an OMGAUDIO database parsed from a player volume.
/// </summary>
public sealed record OmgDatabaseSnapshot(
    IReadOnlyList<AudioTrackDto> Tracks,
    IReadOnlyList<TrackGroupDto> Groups,
    uint TotalDurationMs);

/// <summary>
/// Atomic batch update committed to OMGAUDIO tables after audio staging completes.
/// </summary>
public sealed record OmgDatabaseTransaction(
    IReadOnlyList<AudioTrackDto> UpsertedTracks,
    IReadOnlyList<int> DeletedTrackIds,
    IReadOnlyList<TrackGroupDto> UpdatedGroups);

/// <summary>
/// UTF-16BE tag payload encoded into an .oma ea3 block.
/// </summary>
public sealed record Ea3TagMetadata(
    string Title,
    string Artist,
    string Album,
    string Genre,
    int? Year,
    int? TrackNumber,
    uint DurationMs);

/// <summary>
/// Parsed dual ea3 + 96-byte EA3 header block of an .oma container.
/// </summary>
public sealed record OmaHeaderInfo(
    Ea3TagMetadata Tags,
    byte Ea3Version,
    OmaEncryptionMode EncryptionId,
    AudioCodecId CodecId,
    uint CodecParams24,
    uint DurationMs,
    uint FrameCount,
    int AudioPayloadOffset);

/// <summary>
/// Bounded pipeline request to wrap or transcode a source audio file into staged .oma output.
/// </summary>
public sealed record TranscodeRequest(
    string SourceFilePath,
    string StagingOutputPath,
    AudioCodecId TargetCodec,
    int TargetBitrateKbps,
    OmaEncryptionMode EncryptionMode,
    int TrackId,
    uint? HardwareDeviceKey,
    Ea3TagMetadata Tags);

/// <summary>
/// Output summary produced by L2.Codec.Pipeline for a single track job.
/// </summary>
public sealed record TranscodeResult(
    bool Success,
    string OutputOmaPath,
    uint DurationMs,
    uint FrameCount,
    uint CodecParams24,
    string? ErrorMessage);

/// <summary>
/// Single timeline frame entry inside a Sony .nwm screensaver (2 bytes: bitmapIndex + flags).
/// </summary>
public sealed record NwmFrameEntry(
    byte BitmapIndex,
    byte Flags = 0x10);

/// <summary>
/// Complete logical screensaver project packed or unpacked by L2.Custom.NwmEngine.
/// </summary>
public sealed record NwmProjectDto(
    ScreensaverMagic Magic,
    string Name,
    string Author,
    ushort TimingFlag1,
    ushort TimingFlag2,
    int Width,
    int Height,
    int BitsPerPixel,
    IReadOnlyList<NwmFrameEntry> Timeline,
    IReadOnlyList<byte[]> UniqueBitmaps);

/// <summary>
/// Firmware ceiling and geometry validation result for a .nwm project.
/// </summary>
public sealed record NwmValidationResult(
    bool IsValid,
    int EstimatedByteSize,
    IReadOnlyList<string> Errors);

/// <summary>
/// Privacy-safe diagnostic collection parameters for unrecognized Sony hardware.
/// </summary>
public sealed record DiagnosticDumpRequest(
    DeviceDetectionProbe Probe,
    string OutputZipPath);

/// <summary>
/// Result of sanitized diagnostic archive creation by L2.Telemetry.Dumper.
/// </summary>
public sealed record DiagnosticDumpResult(
    bool Success,
    string OutputZipPath,
    long ZipSizeBytes,
    int CapturedFilesCount);

/// <summary>
/// L2 filesystem transport abstraction enforcing 8.3 paths and flush-to-disk guarantees.
/// </summary>
public interface ITransportDriver
{
    TransportProtocol Protocol { get; }

    Task<VolumeSpaceInfo> GetSpaceInfoAsync(ConnectedDeviceIdentity device, CancellationToken ct = default);

    Task<bool> ExistsAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListFilesAsync(ConnectedDeviceIdentity device, string relativeDirectory, CancellationToken ct = default);

    Task<byte[]> ReadFileAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default);

    Task WriteFileAtomicAsync(ConnectedDeviceIdentity device, string relativePath, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task DeleteFileAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default);
}

/// <summary>
/// L2 OMGAUDIO binary database reader, incremental writer, and scratch rebuilder.
/// </summary>
public interface IDatabaseEngine
{
    DatabaseFormat Format { get; }

    Task<OmgDatabaseSnapshot> ReadDatabaseAsync(ConnectedDeviceIdentity device, CancellationToken ct = default);

    Task CommitTransactionAsync(ConnectedDeviceIdentity device, OmgDatabaseTransaction transaction, CancellationToken ct = default);

    Task RebuildFromTracksAsync(ConnectedDeviceIdentity device, IReadOnlyList<AudioTrackDto> tracks, CancellationToken ct = default);
}

/// <summary>
/// L2 OpenMG ea3/EA3 container header builder and 0xFFFE XOR payload scrambler.
/// </summary>
public interface IOpenMgCryptoService
{
    OmaHeaderInfo ParseHeader(ReadOnlySpan<byte> omaData);

    byte[] BuildHeader(OmaHeaderInfo headerSpec);

    uint ComputeTrackXorKey(int trackId, uint hardwareDeviceKey);

    void ApplyXorScrambleInPlace(Span<byte> audioPayload, uint trackXorKey);
}

/// <summary>
/// L2 audio transcode, MP3 pass-through wrapper, and .oma extraction pipeline.
/// </summary>
public interface ICodecService
{
    Task<TranscodeResult> ProcessToOmaAsync(TranscodeRequest request, CancellationToken ct = default);

    Task<bool> ExtractFromOmaAsync(string sourceOmaPath, string outputFilePath, uint? hardwareDeviceKey, CancellationToken ct = default);
}

/// <summary>
/// L2 Sony .nwm screensaver binary packer, unpacker, and firmware constraint validator.
/// </summary>
public interface IScreensaverPacker
{
    NwmValidationResult Validate(NwmProjectDto project, ScreenSpecification targetScreen);

    byte[] Pack(NwmProjectDto project);

    NwmProjectDto Unpack(ReadOnlySpan<byte> nwmBytes);
}

/// <summary>
/// L2 read-only diagnostic collector for generating sanitized ~50 KB community dumps.
/// </summary>
public interface ITelemetryDumper
{
    Task<DiagnosticDumpResult> CreateSanitizedDumpAsync(DiagnosticDumpRequest request, CancellationToken ct = default);
}

/// <summary>
/// L3 presentation controller contract routing diagnostic dump exports through the L1 mediator.
/// </summary>
public interface IDumpWizardController
{
    Task<DiagnosticDumpResult> ExportDiagnosticArchiveAsync(
        DeviceDetectionProbe probe,
        string outputZipPath,
        CancellationToken ct = default);
}
