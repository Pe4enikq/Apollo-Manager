using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Walkman.L1.DevicePoller.Probing;

/// <summary>
/// Outcome of read-only hardware DvID key extraction across Tiers 1-3.
/// </summary>
public sealed record DvIdResolutionResult(
    uint? DeviceKey,
    ushort? ModelSegment,
    byte[]? RawDvIdBytes,
    string SourceTier);

/// <summary>
/// Read-only 3-tier hardware DvID key extractor:
/// Tier 1: DvID.DAT on volume (MP3FM/DvID.DAT, OMGAUDIO/DvID.DAT, or root DvID.DAT).
/// Tier 2: Zero-elevation known-plaintext recovery from 0xFFFE MP3 .OMA headers.
/// Tier 3: Optional read-only IOCTL_SCSI_PASS_THROUGH (0xA4) ASIC inquiry when elevated.
/// </summary>
public sealed class DvIdHardwareKeyResolver
{
    private const uint KeyMultiplier = 0x5296E435u;
    private const uint KeyAdditive = 0x2465u;
    private const uint GenericReadWrite = 0x80000000u | 0x40000000u;
    private const byte ScsiDataIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ScsiPassThroughWithBuffer
    {
        public Windows.Win32.Storage.IscsiDisc.SCSI_PASS_THROUGH Spt;
        public uint Filler;
        public fixed byte DataBuffer[64];
    }

    /// <summary>
    /// Attempts Tier 1 -> Tier 2 -> Tier 3 read-only hardware key extraction for the specified volume root.
    /// </summary>
    public DvIdResolutionResult ResolveForVolume(string volumeRootPath, bool allowScsiPassThrough = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRootPath);

        var tier1 = TryReadDvIdFile(volumeRootPath);
        if (tier1 is not null)
        {
            return tier1;
        }

        var tier2 = TryRecoverFromExistingOmaHeaders(volumeRootPath);
        if (tier2 is not null)
        {
            return tier2;
        }

        if (allowScsiPassThrough && OperatingSystem.IsWindows())
        {
            var tier3 = TryQueryScsiReportKey(volumeRootPath);
            if (tier3 is not null)
            {
                return tier3;
            }
        }

        return new DvIdResolutionResult(null, null, null, "None");
    }

    /// <summary>
    /// Tier 1: Reads a 16-byte DvID.DAT file if present on the mounted volume.
    /// </summary>
    public DvIdResolutionResult? TryReadDvIdFile(string volumeRootPath)
    {
        string[] candidateRelativePaths =
        [
            Path.Combine("MP3FM", "DvID.DAT"),
            Path.Combine("OMGAUDIO", "DvID.DAT"),
            "DvID.DAT"
        ];

        foreach (var rel in candidateRelativePaths)
        {
            var full = ResolveCaseInsensitivePath(volumeRootPath, rel);
            if (full is null)
            {
                continue;
            }

            var data = File.ReadAllBytes(full);
            if (data.Length < 16)
            {
                continue;
            }

            ushort segment = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
            uint key = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10, 4));
            return new DvIdResolutionResult(key, segment, data[..16], "Tier1_DvIdFile");
        }

        return null;
    }

    /// <summary>
    /// Tier 2: Recovers the 32-bit device key from unprivileged read-only inspection of 0xFFFE MP3 .OMA tracks.
    /// </summary>
    public DvIdResolutionResult? TryRecoverFromExistingOmaHeaders(string volumeRootPath)
    {
        var omgPath = ResolveCaseInsensitiveDirectory(volumeRootPath, "OMGAUDIO");
        if (omgPath is null)
        {
            return null;
        }

        var candidateTracks = Directory.GetDirectories(omgPath, "10F*", SearchOption.TopDirectoryOnly)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .SelectMany(dir => Directory.GetFiles(dir, "*.OMA", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            .Take(5)
            .ToArray();

        if (candidateTracks.Length == 0)
        {
            return null;
        }

        uint? firstRecoveredKey = null;
        foreach (var trackPath in candidateTracks)
        {
            if (!TryParseTrackIdFromPath(trackPath, out int trackId) || trackId <= 0)
            {
                continue;
            }

            var key = TryRecoverDeviceKeyFromSingleOma(trackPath, trackId);
            if (!key.HasValue)
            {
                continue;
            }

            if (!firstRecoveredKey.HasValue)
            {
                firstRecoveredKey = key.Value;
            }
            else if (firstRecoveredKey.Value == key.Value)
            {
                // Confirmed identically across two distinct tracks.
                return new DvIdResolutionResult(key.Value, null, null, "Tier2_OmaKnownPlaintext");
            }
        }

        return firstRecoveredKey.HasValue
            ? new DvIdResolutionResult(firstRecoveredKey.Value, null, null, "Tier2_OmaKnownPlaintext")
            : null;
    }

    /// <summary>
    /// Tier 3: Issues read-only SCSI 0xA4 CDB over \\.\X: when elevated (returns null on access denied).
    /// </summary>
    public unsafe DvIdResolutionResult? TryQueryScsiReportKey(string volumeRootPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
        {
            return null;
        }

        var driveRoot = Path.GetPathRoot(Path.GetFullPath(volumeRootPath));
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
        {
            return null;
        }

        string devicePath = $@"\\.\{driveRoot[..2]}";
        using SafeFileHandle handle = PInvoke.CreateFile(
            devicePath,
            GenericReadWrite,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);

        if (handle.IsInvalid)
        {
            return null;
        }

        ScsiPassThroughWithBuffer sptwb = default;
        sptwb.Spt.Length = (ushort)sizeof(Windows.Win32.Storage.IscsiDisc.SCSI_PASS_THROUGH);
        sptwb.Spt.CdbLength = 12;
        sptwb.Spt.DataIn = ScsiDataIn;
        sptwb.Spt.DataTransferLength = 18;
        sptwb.Spt.TimeOutValue = 5;
        sptwb.Spt.DataBufferOffset = (nuint)(sptwb.DataBuffer - (byte*)&sptwb);

        // Read-only Sony DvID inquiry CDB: A4 00 00 00 00 00 00 BC 00 12 3F 00
        sptwb.Spt.Cdb[0] = 0xA4;
        sptwb.Spt.Cdb[7] = 0xBC;
        sptwb.Spt.Cdb[9] = 0x12;
        sptwb.Spt.Cdb[10] = 0x3F;

        uint totalSize = (uint)sizeof(ScsiPassThroughWithBuffer);
        uint bytesReturned = 0;

        bool ok = PInvoke.DeviceIoControl(
            handle,
            PInvoke.IOCTL_SCSI_PASS_THROUGH,
            &sptwb,
            totalSize,
            &sptwb,
            totalSize,
            &bytesReturned,
            null);

        if (!ok || bytesReturned < 18)
        {
            return null;
        }

        ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(sptwb.DataBuffer, 64).Slice(2, 16);
        ushort segment = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2));
        uint key = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(10, 4));
        return new DvIdResolutionResult(key, segment, payload.ToArray(), "Tier3_Scsi0xA4");
    }

    private static uint? TryRecoverDeviceKeyFromSingleOma(string filePath, int trackId)
    {
        Span<byte> headerBuf = stackalloc byte[4096];
        int bytesRead;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytesRead = fs.Read(headerBuf);
        }

        if (bytesRead < 112)
        {
            return null;
        }

        // Verify lowercase "ea3" prefix (0x65 0x61 0x33)
        if (headerBuf[0] != (byte)'e' || headerBuf[1] != (byte)'a' || headerBuf[2] != (byte)'3')
        {
            return null;
        }

        int ea3BodySize =
            ((headerBuf[6] & 0x7F) << 21) |
            ((headerBuf[7] & 0x7F) << 14) |
            ((headerBuf[8] & 0x7F) << 7) |
            (headerBuf[9] & 0x7F);

        int ea3TotalSize = 10 + ea3BodySize;
        int audioOffset = ea3TotalSize + 96;

        if (audioOffset + 16 <= bytesRead)
        {
            return ExtractKeyFromEa3Span(headerBuf[..bytesRead], ea3TotalSize, audioOffset, trackId);
        }

        if (audioOffset + 16 > 65536)
        {
            return null;
        }

        var oversizedBuf = new byte[audioOffset + 16];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int totalRead = fs.Read(oversizedBuf, 0, oversizedBuf.Length);
            if (totalRead < oversizedBuf.Length)
            {
                return null;
            }
        }

        return ExtractKeyFromEa3Span(oversizedBuf, ea3TotalSize, audioOffset, trackId);
    }

    private static uint? ExtractKeyFromEa3Span(ReadOnlySpan<byte> span, int ea3TotalSize, int audioOffset, int trackId)
    {
        // Verify uppercase "EA3" descriptor magic
        if (span[ea3TotalSize] != (byte)'E' ||
            span[ea3TotalSize + 1] != (byte)'A' ||
            span[ea3TotalSize + 2] != (byte)'3')
        {
            return null;
        }

        ushort encryptionId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(ea3TotalSize + 6, 2));
        byte codecId = span[ea3TotalSize + 32];

        // Only 0xFFFE scrambled MP3 (0x03) carries XOR-masked MPEG frames directly after EA3.
        if (encryptionId != 0xFFFE || codecId != 0x03)
        {
            return null;
        }

        uint word0 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(audioOffset, 4));
        uint word1 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(audioOffset + 4, 4));
        uint word2 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(audioOffset + 8, 4));

        // Zero-padded MPEG Xing/LAME header leaves two identical 32-bit Big-Endian trackKey words.
        if (word1 != word2 || word1 == 0)
        {
            return null;
        }

        uint candidateTrackKey = word1;
        uint unscrambledHeader = word0 ^ candidateTrackKey;
        byte b0 = (byte)(unscrambledHeader >> 24);
        byte b1 = (byte)((unscrambledHeader >> 16) & 0xFF);

        bool isValidMpegSync =
            b0 == 0xFF &&
            (b1 & 0xE0) == 0xE0 &&
            (b1 & 0x18) != 0x08 &&
            (b1 & 0x06) != 0x00;

        if (!isValidMpegSync)
        {
            return null;
        }

        return unchecked(candidateTrackKey ^ (KeyAdditive + (uint)trackId * KeyMultiplier));
    }

    private static bool TryParseTrackIdFromPath(string filePath, out int trackId)
    {
        trackId = 0;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (stem.Length != 8 || stem[0] != '1')
        {
            return false;
        }

        return int.TryParse(stem.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out trackId);
    }

    private static string? ResolveCaseInsensitivePath(string rootPath, string relativePath)
    {
        var current = rootPath;
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            bool isLast = i == segments.Length - 1;
            if (!isLast)
            {
                var nextDir = Directory.GetDirectories(current)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), segments[i], StringComparison.OrdinalIgnoreCase));

                if (nextDir is null)
                {
                    return null;
                }

                current = nextDir;
            }
            else
            {
                return Directory.GetFiles(current)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), segments[i], StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    private static string? ResolveCaseInsensitiveDirectory(string rootPath, string folderName)
    {
        return Directory.GetDirectories(rootPath)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));
    }
}
