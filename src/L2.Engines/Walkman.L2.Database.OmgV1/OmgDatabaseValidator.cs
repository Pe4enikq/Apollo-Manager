using System.Buffers.Binary;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Database.OmgV1;

internal static class OmgDatabaseValidator
{
    public static void ValidateBeforeCommit(
        IReadOnlyDictionary<string, byte[]> stagedFiles,
        IReadOnlyList<AudioTrackDto> activeTracks,
        DeviceProfile profile)
    {
        foreach (var (path, data) in stagedFiles)
        {
            if (path.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase) && data.Length > 1)
            {
                ValidateDatFraming(path, data);
            }
        }

        if (!stagedFiles.TryGetValue("OMGAUDIO/04CNTINF.DAT", out byte[]? cntinf) ||
            !stagedFiles.TryGetValue("OMGAUDIO/05CIDLST.DAT", out byte[]? cidlst))
        {
            throw new InvalidOperationException("Pre-commit validation failed: missing 04CNTINF.DAT or 05CIDLST.DAT.");
        }

        int cntCount = BinaryPrimitives.ReadUInt16BigEndian(cntinf.AsSpan(36, 2));
        int cntElemSize = BinaryPrimitives.ReadUInt16BigEndian(cntinf.AsSpan(38, 2));
        int cidCount = BinaryPrimitives.ReadUInt16BigEndian(cidlst.AsSpan(36, 2));

        // Verify cross-table track ID bijection for 01TREE01..04 and 01TREE2D
        foreach (byte tid in new byte[] { 0x01, 0x02, 0x03, 0x04, 0x2D })
        {
            string treePath = $"OMGAUDIO/01TREE{tid:X2}.DAT";
            if (!stagedFiles.TryGetValue(treePath, out byte[]? treeBytes))
            {
                continue;
            }

            uint tplbOff = BinaryPrimitives.ReadUInt32BigEndian(treeBytes.AsSpan(36, 4));
            int tCount = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.AsSpan((int)tplbOff + 4, 2));
            if (tCount != activeTracks.Count)
            {
                throw new InvalidOperationException(
                    $"Pre-commit validation failed: {treePath} TPLB count ({tCount}) does not match active track count ({activeTracks.Count}).");
            }

            var seenTreeTrackIds = new HashSet<ushort>(tCount);
            for (int i = 0; i < tCount; i++)
            {
                ushort trackId = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.AsSpan((int)tplbOff + 16 + i * 2, 2));
                if (trackId == 0 || trackId > cntCount || trackId > cidCount)
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: {treePath} references out-of-range TrackId={trackId} (cntinf={cntCount}, cidlst={cidCount}).");
                }

                if (!seenTreeTrackIds.Add(trackId))
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: {treePath} contains duplicate TrackId={trackId} in TPLB.");
                }

                int recOff = 48 + (trackId - 1) * cntElemSize;
                ushort encId = BinaryPrimitives.ReadUInt16BigEndian(cntinf.AsSpan(recOff + 2, 2));
                if (encId == 0)
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: {treePath} references unpopulated 04CNTINF slot TrackId={trackId}.");
                }
            }

            // Validate GPLB groupId and firstTrackPos monotonicity against matching 03GINF table
            string ginfPath = $"OMGAUDIO/03GINF{tid:X2}.DAT";
            if (stagedFiles.TryGetValue(ginfPath, out byte[]? ginfBytes) && ginfBytes.Length >= 40)
            {
                uint gplbOff = BinaryPrimitives.ReadUInt32BigEndian(treeBytes.AsSpan(20, 4));
                int gCount = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.AsSpan((int)gplbOff + 4, 2));
                int ginfCount = BinaryPrimitives.ReadUInt16BigEndian(ginfBytes.AsSpan(36, 2));
                ushort prevPos = 0;
                for (int g = 0; g < gCount; g++)
                {
                    int entryOff = (int)gplbOff + 16 + g * 8;
                    ushort groupId = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.AsSpan(entryOff, 2));
                    ushort firstPos = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.AsSpan(entryOff + 4, 2));
                    if (groupId == 0 || groupId > ginfCount)
                    {
                        throw new InvalidOperationException(
                            $"Pre-commit validation failed: {treePath} GPLB[{g}] references out-of-range GroupId={groupId} (ginf={ginfCount}).");
                    }

                    if (firstPos > 0)
                    {
                        if (firstPos > tCount || firstPos < prevPos)
                        {
                            throw new InvalidOperationException(
                                $"Pre-commit validation failed: {treePath} GPLB[{g}] has non-monotonic or out-of-range FirstTrackPos={firstPos} (prev={prevPos}, tplb={tCount}).");
                        }

                        prevPos = firstPos;
                    }
                }
            }
        }

        // Verify encryption & codec policy against DeviceProfile
        foreach (var tr in activeTracks)
        {
            var cap = profile.Codecs.FirstOrDefault(c => c.Codec == tr.Codec);
            if (cap is not null && tr.Codec == AudioCodecId.Mp3 && cap.RequiredEncryption != tr.EncryptionMode)
            {
                throw new InvalidOperationException(
                    $"Pre-commit validation failed: TrackId={tr.TrackId} uses MP3 encryption {tr.EncryptionMode}, but {profile.SeriesId} requires {cap.RequiredEncryption}.");
            }
        }

        for (int i = 0; i < cntCount; i++)
        {
            int recOff = 48 + i * cntElemSize;
            ushort encId = BinaryPrimitives.ReadUInt16BigEndian(cntinf.AsSpan(recOff + 2, 2));
            if (encId == 0) continue; // tombstone/empty slot

            byte codecByte = cntinf[recOff + 4];
            AudioCodecId? codec = codecByte switch
            {
                0x00 => AudioCodecId.Atrac3,
                0x01 => AudioCodecId.Atrac3Plus,
                0x03 => AudioCodecId.Mp3,
                _ => null
            };
            if (codec is null) continue;

            var cap = profile.Codecs.FirstOrDefault(c => c.Codec == codec);
            if (cap is not null && (ushort)cap.RequiredEncryption != encId)
            {
                if (codec == AudioCodecId.Mp3)
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: TrackId={i + 1} uses MP3 encryption 0x{encId:X4}, but {profile.SeriesId} requires 0x{(ushort)cap.RequiredEncryption:X4}.");
                }

                if (codec is AudioCodecId.Atrac3 or AudioCodecId.Atrac3Plus &&
                    cap.RequiredEncryption == OmaEncryptionMode.MagicGateLsi_0x0001 &&
                    encId == (ushort)OmaEncryptionMode.Unprotected_0xFFFF)
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: TrackId={i + 1} uses unprotected 0xFFFF ATRAC, but {profile.SeriesId} requires 0x0001 MagicGate.");
                }
            }
        }
    }

    private static void ValidateDatFraming(string path, byte[] data)
    {
        if (data.Length < 32)
        {
            throw new InvalidOperationException($"Pre-commit validation failed: {path} too short ({data.Length} B).");
        }

        for (int i = 0; i < 4; i++)
        {
            if (data[i] < 0x20 || data[i] > 0x7E)
            {
                throw new InvalidOperationException($"Pre-commit validation failed: {path} has non-ASCII magic.");
            }
        }

        ushort secCount = data[8];
        if (secCount < 1)
        {
            throw new InvalidOperationException($"Pre-commit validation failed: {path} declares 0 sections.");
        }

        ulong prevEnd = 16ul + (ulong)secCount * 16ul;
        if (prevEnd > (ulong)data.Length)
        {
            throw new InvalidOperationException($"Pre-commit validation failed: {path} section directory exceeds file length {data.Length}.");
        }

        for (int s = 0; s < secCount; s++)
        {
            int dirOff = 16 + s * 16;
            uint secOff = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dirOff + 4, 4));
            uint secLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dirOff + 8, 4));
            if (secOff < prevEnd || (ulong)secOff + (ulong)secLen > (ulong)data.Length)
            {
                throw new InvalidOperationException(
                    $"Pre-commit validation failed: {path} section[{s}] bounds (off=0x{secOff:X}, len=0x{secLen:X}) invalid for file length {data.Length}.");
            }

            if (secLen >= 16)
            {
                ushort recCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)secOff + 4, 2));
                ushort recSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)secOff + 6, 2));
                if (16ul + (ulong)recCount * (ulong)recSize > secLen)
                {
                    throw new InvalidOperationException(
                        $"Pre-commit validation failed: {path} section[{s}] block overflow ({recCount} * {recSize} + 16 > {secLen}).");
                }
            }

            prevEnd = (ulong)secOff + (ulong)secLen;
        }
    }
}
