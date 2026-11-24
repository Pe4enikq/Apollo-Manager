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
    byte[]? LsiKeyring48,
    long FileSizeBytes = 0,
    string? GroupName = null,
    byte[]? MaclistEntry8 = null,
    int? TargetPositionIndex = null,
    bool IsCopyProtected = false);

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
    IReadOnlyList<TrackGroupDto> UpdatedGroups,
    bool PreserveExistingMaclist0 = false,
    IReadOnlyList<string>? DeletedGroupNames = null,
    IReadOnlyDictionary<string, string>? RenamedGroups = null,
    IReadOnlyList<string>? OrderedGroupNames = null);

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
    uint DurationMs,
    string? AlbumArtist = null,
    string? Composer = null,
    string? SortTitle = null,
    int? Rating = null,
    string? Comment = null,
    string? ArtistLinks = null);

/// <summary>
/// Patch payload for updating on-device 04CNTINF.DAT and .OMA ea3 metadata frames without touching MACLIST0.DAT.
/// </summary>
public sealed record TrackMetadataPatch(
    int TrackId,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? Genre = null,
    string? SortTitle = null,
    int? Year = null,
    int? TrackNumber = null,
    string? AlbumArtist = null,
    string? Composer = null,
    int? Rating = null,
    string? Comment = null,
    string? ArtistLinks = null);

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
    int AudioPayloadOffset,
    byte[]? ContentId20 = null,
    int Ea3BlockSize = 1024,
    bool IsCopyProtected = false);

/// <summary>
/// Clean MPEG audio stream metrics extracted after stripping ID3v2/ID3v1/APEv2 metadata.
/// </summary>
public sealed record Mp3StreamInfo(
    int AudioOffset,
    int CleanPayloadLength,
    uint FrameCount,
    uint DurationMs,
    uint CodecParams24,
    int BitrateKbps,
    int SampleRateHz,
    bool IsVbr);

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
    Ea3TagMetadata Tags,
    int Ea3BlockSize = 1024,
    SignedMagicGateSlotTemplate? RecycledMagicGateSlot = null,
    byte[]? DeviceEkb00010021Bytes = null);

/// <summary>
/// Output summary produced by L2.Codec.Pipeline for a single track job.
/// </summary>
public sealed record TranscodeResult(
    bool Success,
    string OutputOmaPath,
    uint DurationMs,
    uint FrameCount,
    uint CodecParams24,
    string? ErrorMessage,
    byte[]? LsiKeyring48 = null,
    byte[]? MaclistEntry8 = null);

/// <summary>
/// MagicGate 0x0001 wrapped ATRAC/OMA package with 20-byte CID, 48-byte 05CIDLST record, and 8-byte MACLIST0 slot.
/// </summary>
public sealed record MagicGate0x0001WrapResult(
    byte[] OmaBytes,
    byte[] ContentId20,
    byte[] LsiKeyring48,
    byte[]? MaclistEntry8);

/// <summary>
/// Pre-signed OpenMG 0x0001 slot template harvested from a valid hardware-signed .OMA file
/// so pure MSC transfers can replace or recycle the track payload without modifying MACLIST0.DAT.
/// </summary>
public sealed record SignedMagicGateSlotTemplate(
    int TrackId,
    byte[] Ulinf240,
    byte[]? Bklsi428,
    byte[]? Olinf64,
    byte[] ContentId20,
    byte[] Iv8,
    byte[] MaclistEntry8);

/// <summary>
/// Snapshot of the 168-byte (0xA8) non-FAT Hardware ICV register read via CM-AAD SCSI 0xA4/0x33.
/// </summary>
public sealed record HardwareIcvSnapshot(
    byte[] RawIcv168,
    uint SequenceCounter,
    uint BoundEkbRevision,
    byte[] Maclist0Umac8,
    byte[] MicvsMac8);

/// <summary>
/// Availability status of the optional Windows 32-bit OpenMG COM ICV signer bridge (Option 1).
/// </summary>
public sealed record IcvBridgeCapability(
    bool IsSupportedOs,
    bool IsHostRuntimePresent,
    bool IsWorkerAvailable,
    string StatusMessage);

/// <summary>
/// Request entry passed to the optional ICV signer bridge to mint a brand-new 0x0001 slot.
/// </summary>
public sealed record IcvMintTrackRequest(
    int TrackId,
    byte[] ContentId20,
    byte[] PlaintextContentKey8);

/// <summary>
/// Outcome of an optional Hardware ICV + MACLIST0.DAT commit transaction.
/// </summary>
public sealed record IcvCommitResult(
    bool Success,
    byte[]? UpdatedMaclist0,
    byte[]? UpdatedIcv168,
    IReadOnlyDictionary<int, SignedMagicGateSlotTemplate>? MintedSlots,
    string? ErrorMessage);

/// <summary>
/// Telemetry summary of pre-signed OpenMG 0x0001 slots and Hardware ICV state on a mounted Walkman.
/// </summary>
public sealed record MagicGateSlotPoolStatus(
    bool HasMaclist0,
    int TotalSignedSlotsInMaclist0,
    int ActiveSignedTrackCount,
    int RecycledFreeSlotCount,
    IReadOnlyList<int> FreeRecycledTrackIds,
    IReadOnlyList<int> ActiveSignedTrackIds,
    HardwareIcvSnapshot? HardwareIcv,
    IcvBridgeCapability? WindowsIcvBridge,
    int BrokenUnsignedSlotCount = 0,
    IReadOnlyList<int>? BrokenUnsignedTrackIds = null,
    bool IsHardwareIcvInitialized = true,
    string? HealthWarning = null);

/// <summary>
/// Batch transfer source item queued through L1.FileManager.
/// </summary>
public sealed record SyncTrackRequest(
    string SourceFilePath,
    AudioCodecId TargetCodec = AudioCodecId.Mp3,
    int TargetBitrateKbps = 192,
    string? GroupName = null,
    Ea3TagMetadata? TagOverrides = null,
    int? ReplaceTrackId = null,
    int? TargetPositionIndex = null);

/// <summary>
/// Summary of an atomic batch transfer executed by L1.FileManager.
/// </summary>
public sealed record SyncBatchResult(
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<AudioTrackDto> AddedTracks,
    IReadOnlyList<string> Errors);

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

    Task<byte[]> ReadFilePrefixAsync(ConnectedDeviceIdentity device, string relativePath, int maxBytes, CancellationToken ct = default);

    Task<long> GetFileLengthAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default);

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

    Task<OmgDatabaseSnapshot> ReadDatabaseFastAsync(ConnectedDeviceIdentity device, CancellationToken ct = default) =>
        ReadDatabaseAsync(device, ct);

    Task CommitTransactionAsync(ConnectedDeviceIdentity device, OmgDatabaseTransaction transaction, CancellationToken ct = default);

    Task RebuildFromTracksAsync(ConnectedDeviceIdentity device, IReadOnlyList<AudioTrackDto> tracks, CancellationToken ct = default);

    Task<int> UpdateTrackMetadataAsync(ConnectedDeviceIdentity device, IReadOnlyList<TrackMetadataPatch> patches, CancellationToken ct = default) =>
        throw new NotSupportedException($"Engine '{Format}' does not support in-place track metadata updates.");
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

    Mp3StreamInfo InspectMp3Payload(ReadOnlySpan<byte> mp3Data);

    byte[] WrapMp3ToOma(
        ReadOnlySpan<byte> sourceMp3,
        int trackId,
        uint? hardwareDeviceKey,
        OmaEncryptionMode encryptionMode,
        Ea3TagMetadata tags,
        int ea3BlockSize = 1024,
        byte[]? contentId20 = null);

    byte[] UnwrapOmaToMp3(ReadOnlySpan<byte> omaBytes, int trackId, uint? hardwareDeviceKey);

    MagicGate0x0001WrapResult WrapAtracToOma(
        ReadOnlySpan<byte> rawAtracFrames,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        int trackId,
        uint? hardwareDeviceKey,
        OmaEncryptionMode encryptionMode,
        Ea3TagMetadata tags,
        int ea3BlockSize = 2048,
        byte[]? contentId20 = null,
        ReadOnlySpan<byte> ekb00010021Bytes = default);

    byte[] DecryptOmaPayload(
        ReadOnlySpan<byte> omaBytes,
        int trackId,
        uint? hardwareDeviceKey,
        ReadOnlySpan<byte> ekb00010021Bytes = default);

    SignedMagicGateSlotTemplate? ExtractSignedSlotTemplate(
        ReadOnlySpan<byte> omaBytes,
        int trackId,
        ReadOnlySpan<byte> ekb00010021Bytes = default);

    MagicGate0x0001WrapResult WrapAtracIntoSignedSlot(
        ReadOnlySpan<byte> rawAtracFrames,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        SignedMagicGateSlotTemplate slotTemplate,
        Ea3TagMetadata tags,
        ReadOnlySpan<byte> ekb00010021Bytes = default,
        int ea3BlockSize = 2048);

    bool EvaluateCopyProtectionLock(
        ReadOnlySpan<byte> omaHeaderOrFileBytes,
        bool isCntinfDamedaFlagSet = false,
        ushort chindcFlag = 0) => false;

    byte[] UpdateOmaEa3TagsPreservingCrypto(
        ReadOnlySpan<byte> existingOmaBytes,
        Ea3TagMetadata updatedTags) =>
        throw new NotSupportedException("Updating .OMA ea3 tags is not supported by this crypto service.");

    byte[] BuildSignedSlotDummyStub(SignedMagicGateSlotTemplate slotTemplate) =>
        throw new NotSupportedException("Signed slot dummy stub generation is not supported by this crypto service.");
}

/// <summary>
/// L2 CM-AAD SCSI 0xA3/0xA4 hardware ICV reader and optional Windows 32-bit OpenMG COM bridge (Option 1).
/// </summary>
public interface IOpenMgIcvBridge
{
    IcvBridgeCapability ProbeCapability();

    HardwareIcvSnapshot? ParseHardwareIcvResponse(ReadOnlySpan<byte> response1028);

    byte[] BuildWriteHardwareIcvPayload(ReadOnlySpan<byte> icv168);

    Task<HardwareIcvSnapshot?> ReadHardwareIcvAsync(ConnectedDeviceIdentity device, CancellationToken ct = default);

    Task<IcvCommitResult> MintAndCommitSlotsAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<IcvMintTrackRequest> mintRequests,
        IReadOnlyList<int> deleteTrackIds,
        CancellationToken ct = default);
}

/// <summary>
/// L2 audio transcode, MP3 pass-through wrapper, and .oma extraction pipeline.
/// </summary>
public interface ICodecService
{
    Ea3TagMetadata ExtractMetadata(string sourceFilePath);

    Task<TranscodeResult> ProcessToOmaAsync(TranscodeRequest request, CancellationToken ct = default);

    Task<bool> ExtractFromOmaAsync(string sourceOmaPath, string outputFilePath, uint? hardwareDeviceKey, CancellationToken ct = default);

    Task<TranscodeResult> RekeyOmaAsync(
        string sourceOmaPath,
        string stagingOutputPath,
        int targetTrackId,
        uint? sourceDeviceKey,
        uint? targetDeviceKey,
        OmaEncryptionMode targetEncryptionMode,
        CancellationToken ct = default);
}

/// <summary>
/// L2 live .OMA audio stream decoder yielding seekable 44.1 kHz PCM WAV streams for direct playback.
/// </summary>
public interface IAudioStreamDecoder
{
    Task<Stream?> DecodeOmaToPlayableWavStreamAsync(
        string sourceOmaPath,
        uint? hardwareDeviceKey = null,
        CancellationToken ct = default);
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
