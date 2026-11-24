using System.Buffers.Binary;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Clean-room Span&lt;byte&gt; implementation of Sony .OMA (ea3 + 96-byte EA3) parsing/building,
/// Gen 3 0xFFFE 4-byte XOR scrambling, and strict MP3 frame hygiene.
/// </summary>
public sealed class OpenMgCryptoService : IOpenMgCryptoService
{
    private const uint KeyMultiplier = 0x5296E435u;
    private const uint KeyAdditive = 0x2465u;
    private const int Ea3DescriptorSize = 96;
    private const int MaxTagCharacters = 59;

    private static readonly int[] Mpeg1L3BitratesKbps =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    private static readonly int[] Mpeg2L3BitratesKbps =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    private static readonly int[] Mpeg1SampleRatesHz = [44100, 48000, 32000, 0];
    private static readonly int[] Mpeg2SampleRatesHz = [22050, 24000, 16000, 0];
    private static readonly int[] Mpeg25SampleRatesHz = [11025, 12000, 8000, 0];

    /// <inheritdoc />
    public OmaHeaderInfo ParseHeader(ReadOnlySpan<byte> omaData)
    {
        if (omaData.Length < 10 + Ea3DescriptorSize)
        {
            throw new InvalidDataException("OMA buffer is too short to contain ea3 and EA3 headers.");
        }

        if (omaData[0] != (byte)'e' || omaData[1] != (byte)'a' || omaData[2] != (byte)'3')
        {
            throw new InvalidDataException("Missing lowercase 'ea3' container prefix.");
        }

        int ea3BodySize = DecodeSynchsafe(omaData.Slice(6, 4));
        int ea3TotalSize = 10 + ea3BodySize;
        if (ea3TotalSize + Ea3DescriptorSize > omaData.Length)
        {
            throw new InvalidDataException(
                $"OMA buffer length ({omaData.Length}) is smaller than declared ea3+EA3 offset ({ea3TotalSize + Ea3DescriptorSize}).");
        }

        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        string genre = string.Empty;
        int? year = null;
        int? trackNumber = null;
        uint tagDurationMs = 0;
        string? albumArtist = null;
        string? composer = null;
        string? sortTitle = null;
        int? rating = null;
        string? comment = null;
        string? artistLinks = null;

        int pos = 10;
        while (pos + 10 <= ea3TotalSize)
        {
            byte first = omaData[pos];
            if (first == 0x00)
            {
                break;
            }

            string frameId = Encoding.ASCII.GetString(omaData.Slice(pos, 4));
            int frameSize = BinaryPrimitives.ReadInt32BigEndian(omaData.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > ea3TotalSize)
            {
                break;
            }

            ReadOnlySpan<byte> payload = omaData.Slice(pos + 10, frameSize);
            if (payload.Length >= 1)
            {
                byte enc = payload[0];
                ReadOnlySpan<byte> body = payload.Slice(1);
                switch (frameId)
                {
                    case "TIT2":
                        title = DecodeText(enc, body);
                        break;
                    case "TPE1":
                        artist = DecodeText(enc, body);
                        break;
                    case "TALB":
                        album = DecodeText(enc, body);
                        break;
                    case "TCON":
                        genre = DecodeText(enc, body);
                        break;
                    case "TCOM":
                        composer = DecodeText(enc, body);
                        break;
                    case "COMM":
                        if (body.Length > 3)
                        {
                            string commText = DecodeText(enc, body.Slice(3));
                            int commNul = commText.IndexOf('\0');
                            comment = commNul >= 0 ? commText[(commNul + 1)..] : commText;
                        }
                        break;
                    case "RATE":
                        if (int.TryParse(DecodeText(enc, body), out int parsedRate))
                        {
                            rating = Math.Clamp(parsedRate, 0, 5);
                        }
                        break;
                    case "TYER":
                        if (int.TryParse(DecodeText(enc, body), out int parsedYear))
                        {
                            year = parsedYear;
                        }
                        break;
                    case "TLEN":
                        if (uint.TryParse(DecodeText(enc, body), out uint parsedLen))
                        {
                            tagDurationMs = parsedLen;
                        }
                        break;
                    case "TXXX":
                        var txxx = DecodeText(enc, body);
                        int nulIdx = txxx.IndexOf('\0');
                        if (nulIdx > 0)
                        {
                            var key = txxx[..nulIdx];
                            var val = txxx[(nulIdx + 1)..];
                            if (key.Equals("OMG_TRACK", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(val, out int parsedTrack))
                            {
                                trackNumber = parsedTrack;
                            }
                            else if (key.Equals("OMG_TIT2S", StringComparison.OrdinalIgnoreCase))
                            {
                                sortTitle = val;
                            }
                            else if (key.Equals("OMG_ATPE1", StringComparison.OrdinalIgnoreCase))
                            {
                                albumArtist = val;
                            }
                            else if (key.Equals("OMG_TCOMS", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(composer))
                            {
                                composer = val;
                            }
                            else if (key.Equals("OMG_ATLINK", StringComparison.OrdinalIgnoreCase))
                            {
                                artistLinks = val;
                            }
                        }
                        break;
                }
            }

            pos += 10 + frameSize;
        }

        ReadOnlySpan<byte> ea3 = omaData.Slice(ea3TotalSize, Ea3DescriptorSize);
        if (ea3[0] != (byte)'E' || ea3[1] != (byte)'A' || ea3[2] != (byte)'3')
        {
            throw new InvalidDataException($"Missing uppercase 'EA3' descriptor at offset 0x{ea3TotalSize:X}.");
        }

        byte ea3Version = ea3[3];
        byte descriptorSize = ea3[5];
        if (descriptorSize != Ea3DescriptorSize)
        {
            throw new InvalidDataException($"Unexpected EA3 descriptor size {descriptorSize} (expected 96).");
        }

        var encryptionId = (OmaEncryptionMode)BinaryPrimitives.ReadUInt16BigEndian(ea3.Slice(6, 2));
        byte[] contentId20 = ea3.Slice(12, 20).ToArray();
        var codecId = (AudioCodecId)ea3[32];
        uint codecParams24 = (uint)((ea3[33] << 16) | (ea3[34] << 8) | ea3[35]);
        uint durationMs = BinaryPrimitives.ReadUInt32BigEndian(ea3.Slice(36, 4));
        uint frameCount = BinaryPrimitives.ReadUInt32BigEndian(ea3.Slice(40, 4));

        uint effectiveDurationMs = durationMs > 0 ? durationMs : tagDurationMs;
        var tags = new Ea3TagMetadata(
            title,
            artist,
            album,
            genre,
            year,
            trackNumber,
            effectiveDurationMs,
            AlbumArtist: albumArtist,
            Composer: composer,
            SortTitle: sortTitle,
            Rating: rating,
            Comment: comment,
            ArtistLinks: artistLinks);
        bool isCopyProtected = EvaluateCopyProtectionLock(omaData);

        return new OmaHeaderInfo(
            Tags: tags,
            Ea3Version: ea3Version,
            EncryptionId: encryptionId,
            CodecId: codecId,
            CodecParams24: codecParams24,
            DurationMs: effectiveDurationMs,
            FrameCount: frameCount,
            AudioPayloadOffset: ea3TotalSize + Ea3DescriptorSize,
            ContentId20: contentId20,
            Ea3BlockSize: ea3TotalSize,
            IsCopyProtected: isCopyProtected);
    }

    /// <inheritdoc />
    public byte[] BuildHeader(OmaHeaderInfo headerSpec)
    {
        ArgumentNullException.ThrowIfNull(headerSpec);
        ArgumentNullException.ThrowIfNull(headerSpec.Tags);

        var frames = BuildEa3TagFrames(headerSpec.Tags);
        int minRequired = 10 + frames.Length;
        int requestedSize = headerSpec.Ea3BlockSize >= 1024 ? headerSpec.Ea3BlockSize : 1024;
        int ea3TotalSize = requestedSize;
        while (ea3TotalSize < minRequired)
        {
            ea3TotalSize += 1024;
        }

        var result = new byte[ea3TotalSize + Ea3DescriptorSize];
        var span = result.AsSpan();

        // Block 1: ea3 ID3v2.3 container header
        span[0] = (byte)'e';
        span[1] = (byte)'a';
        span[2] = (byte)'3';
        span[3] = 0x03;
        span[4] = 0x00;
        span[5] = 0x00;
        EncodeSynchsafe(ea3TotalSize - 10, span.Slice(6, 4));
        frames.CopyTo(span.Slice(10));

        // Block 2: 96-byte EA3 audio & crypto descriptor
        var ea3 = span.Slice(ea3TotalSize, Ea3DescriptorSize);
        ea3[0] = (byte)'E';
        ea3[1] = (byte)'A';
        ea3[2] = (byte)'3';
        ea3[3] = headerSpec.Ea3Version == 0 ? (byte)0x02 : headerSpec.Ea3Version;
        ea3[4] = 0x00;
        ea3[5] = Ea3DescriptorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ea3.Slice(6, 2), (ushort)headerSpec.EncryptionId);
        // Bytes 8..11 reserved zeroes

        if (headerSpec.ContentId20 is { Length: 20 })
        {
            headerSpec.ContentId20.CopyTo(ea3.Slice(12, 20));
        }

        ea3[32] = (byte)headerSpec.CodecId;
        ea3[33] = (byte)((headerSpec.CodecParams24 >> 16) & 0xFF);
        ea3[34] = (byte)((headerSpec.CodecParams24 >> 8) & 0xFF);
        ea3[35] = (byte)(headerSpec.CodecParams24 & 0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(ea3.Slice(36, 4), headerSpec.DurationMs);
        BinaryPrimitives.WriteUInt32BigEndian(ea3.Slice(40, 4), headerSpec.FrameCount);

        return result;
    }

    /// <inheritdoc />
    public uint ComputeTrackXorKey(int trackId, uint hardwareDeviceKey)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId), trackId, "TrackId must be >= 1.");
        }

        return unchecked((KeyAdditive + (uint)trackId * KeyMultiplier) ^ hardwareDeviceKey);
    }

    /// <inheritdoc />
    public void ApplyXorScrambleInPlace(Span<byte> audioPayload, uint trackXorKey)
    {
        byte k0 = (byte)((trackXorKey >> 24) & 0xFF);
        byte k1 = (byte)((trackXorKey >> 16) & 0xFF);
        byte k2 = (byte)((trackXorKey >> 8) & 0xFF);
        byte k3 = (byte)(trackXorKey & 0xFF);

        int i = 0;
        int alignedLimit = audioPayload.Length - 3;
        while (i < alignedLimit)
        {
            audioPayload[i] ^= k0;
            audioPayload[i + 1] ^= k1;
            audioPayload[i + 2] ^= k2;
            audioPayload[i + 3] ^= k3;
            i += 4;
        }

        while (i < audioPayload.Length)
        {
            byte k = (i & 3) switch
            {
                0 => k0,
                1 => k1,
                2 => k2,
                _ => k3
            };
            audioPayload[i] ^= k;
            i++;
        }
    }

    /// <inheritdoc />
    public Mp3StreamInfo InspectMp3Payload(ReadOnlySpan<byte> mp3Data)
    {
        if (mp3Data.Length < 4)
        {
            throw new InvalidDataException("MP3 input is too short.");
        }

        int start = 0;
        if (mp3Data.Length >= 10 && mp3Data[0] == (byte)'I' && mp3Data[1] == (byte)'D' && mp3Data[2] == (byte)'3')
        {
            int id3Body = DecodeSynchsafe(mp3Data.Slice(6, 4));
            bool hasFooter = (mp3Data[5] & 0x10) != 0;
            start = Math.Min(mp3Data.Length, 10 + id3Body + (hasFooter ? 10 : 0));
        }

        int endLimit = mp3Data.Length;
        if (endLimit - start >= 128 &&
            mp3Data[endLimit - 128] == (byte)'T' &&
            mp3Data[endLimit - 127] == (byte)'A' &&
            mp3Data[endLimit - 126] == (byte)'G')
        {
            endLimit -= 128;
        }

        // Locate first valid MPEG frame sync (prefer two consecutive synchronized frames).
        int firstFrameOffset = -1;
        for (int pos = start; pos <= endLimit - 4; pos++)
        {
            if (!TryParseMpegFrameHeader(mp3Data.Slice(pos, 4), out var frame))
            {
                continue;
            }

            int nextPos = pos + frame.FrameLengthBytes;
            if (nextPos == endLimit ||
                (nextPos + 4 <= endLimit && TryParseMpegFrameHeader(mp3Data.Slice(nextPos, 4), out _)))
            {
                firstFrameOffset = pos;
                break;
            }
        }

        if (firstFrameOffset < 0)
        {
            throw new InvalidDataException("No valid MPEG Audio frames found in MP3 stream.");
        }

        uint frameCount = 0;
        int cursor = firstFrameOffset;
        MpegFrameMetrics firstFrame = default;
        bool isVbr = false;
        long totalBitrateSum = 0;

        while (cursor + 4 <= endLimit && TryParseMpegFrameHeader(mp3Data.Slice(cursor, 4), out var frame))
        {
            if (cursor + frame.FrameLengthBytes > endLimit)
            {
                break;
            }

            if (frameCount == 0)
            {
                firstFrame = frame;
                isVbr = ContainsXingHeader(mp3Data.Slice(cursor, frame.FrameLengthBytes), firstFrame);
            }
            else if (frame.BitrateKbps != firstFrame.BitrateKbps)
            {
                isVbr = true;
            }

            totalBitrateSum += frame.BitrateKbps;
            frameCount++;
            cursor += frame.FrameLengthBytes;
        }

        if (frameCount == 0)
        {
            throw new InvalidDataException("Failed to decode any complete MPEG frames.");
        }

        int cleanLength = cursor - firstFrameOffset;
        double totalSamples = (double)frameCount * firstFrame.SamplesPerFrame;
        uint durationMs = (uint)Math.Round(totalSamples * 1000.0 / firstFrame.SampleRateHz, MidpointRounding.AwayFromZero);
        int avgBitrateKbps = (int)Math.Round((double)totalBitrateSum / frameCount, MidpointRounding.AwayFromZero);

        byte byte33 = isVbr ? (byte)0x90 : (byte)0x80;
        byte byte34 = (byte)((firstFrame.VersionCode << 6) | (firstFrame.LayerCode << 4) | firstFrame.BitrateIndex);
        byte byte35 = firstFrame.ChannelMode switch
        {
            2 => 0x20, // Dual channel
            3 => 0x30, // Mono
            _ => 0x10  // Stereo / Joint Stereo
        };
        uint codecParams24 = (uint)((byte33 << 16) | (byte34 << 8) | byte35);

        return new Mp3StreamInfo(
            AudioOffset: firstFrameOffset,
            CleanPayloadLength: cleanLength,
            FrameCount: frameCount,
            DurationMs: durationMs,
            CodecParams24: codecParams24,
            BitrateKbps: avgBitrateKbps,
            SampleRateHz: firstFrame.SampleRateHz,
            IsVbr: isVbr);
    }

    /// <inheritdoc />
    public byte[] WrapMp3ToOma(
        ReadOnlySpan<byte> sourceMp3,
        int trackId,
        uint? hardwareDeviceKey,
        OmaEncryptionMode encryptionMode,
        Ea3TagMetadata tags,
        int ea3BlockSize = 1024,
        byte[]? contentId20 = null)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (encryptionMode == OmaEncryptionMode.HardwareXor_0xFFFE && !hardwareDeviceKey.HasValue)
        {
            throw new InvalidOperationException("HardwareDeviceKey is required when encrypting with 0xFFFE.");
        }

        var mp3Info = InspectMp3Payload(sourceMp3);
        uint effectiveDuration = tags.DurationMs > 0 ? tags.DurationMs : mp3Info.DurationMs;
        var effectiveTags = tags with { DurationMs = effectiveDuration };

        var headerInfo = new OmaHeaderInfo(
            Tags: effectiveTags,
            Ea3Version: 0x02,
            EncryptionId: encryptionMode,
            CodecId: AudioCodecId.Mp3,
            CodecParams24: mp3Info.CodecParams24,
            DurationMs: effectiveDuration,
            FrameCount: mp3Info.FrameCount,
            AudioPayloadOffset: ea3BlockSize + Ea3DescriptorSize,
            ContentId20: contentId20,
            Ea3BlockSize: ea3BlockSize);

        byte[] headerBytes = BuildHeader(headerInfo);
        byte[] output = new byte[headerBytes.Length + mp3Info.CleanPayloadLength];
        headerBytes.CopyTo(output, 0);

        ReadOnlySpan<byte> cleanAudio = sourceMp3.Slice(mp3Info.AudioOffset, mp3Info.CleanPayloadLength);
        Span<byte> payloadDest = output.AsSpan(headerBytes.Length);
        cleanAudio.CopyTo(payloadDest);

        if (encryptionMode == OmaEncryptionMode.HardwareXor_0xFFFE)
        {
            uint trackKey = ComputeTrackXorKey(trackId, hardwareDeviceKey!.Value);
            ApplyXorScrambleInPlace(payloadDest, trackKey);
        }

        return output;
    }

    /// <inheritdoc />
    public byte[] UnwrapOmaToMp3(ReadOnlySpan<byte> omaBytes, int trackId, uint? hardwareDeviceKey)
    {
        var header = ParseHeader(omaBytes);
        if (header.CodecId != AudioCodecId.Mp3)
        {
            throw new NotSupportedException($"Codec '{header.CodecId}' cannot be unwrapped directly to MP3.");
        }

        if (header.EncryptionId == OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            throw new NotSupportedException("MagicGate LSI (0x0001) decryption is not supported for raw MP3 unwrap.");
        }

        int payloadLength = omaBytes.Length - header.AudioPayloadOffset;
        if (payloadLength <= 0)
        {
            throw new InvalidDataException("OMA file contains zero audio payload bytes.");
        }

        byte[] id3Header = BuildStandardId3v23Header(header.Tags);
        byte[] output = new byte[id3Header.Length + payloadLength];
        id3Header.CopyTo(output, 0);

        Span<byte> audioDest = output.AsSpan(id3Header.Length);
        omaBytes.Slice(header.AudioPayloadOffset).CopyTo(audioDest);

        if (header.EncryptionId == OmaEncryptionMode.HardwareXor_0xFFFE)
        {
            if (!hardwareDeviceKey.HasValue)
            {
                throw new InvalidOperationException("HardwareDeviceKey is required to descramble 0xFFFE .OMA payload.");
            }

            uint trackKey = ComputeTrackXorKey(trackId, hardwareDeviceKey.Value);
            ApplyXorScrambleInPlace(audioDest, trackKey);
        }

        return output;
    }

    /// <inheritdoc />
    public MagicGate0x0001WrapResult WrapAtracToOma(
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
        ReadOnlySpan<byte> ekb00010021Bytes = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var effectiveTags = tags with { DurationMs = durationMs > 0 ? durationMs : tags.DurationMs };
        byte[] baseTagFrames = BuildEa3TagFrames(effectiveTags);

        return MagicGate0x0001Crypto.WrapAtracToOma(
            rawAtracFrames,
            codecId,
            codecParams24,
            effectiveTags.DurationMs,
            frameCount,
            trackId,
            hardwareDeviceKey,
            encryptionMode,
            effectiveTags,
            ea3BlockSize,
            contentId20,
            baseTagFrames,
            ekb00010021Bytes);
    }

    /// <inheritdoc />
    public byte[] DecryptOmaPayload(
        ReadOnlySpan<byte> omaBytes,
        int trackId,
        uint? hardwareDeviceKey,
        ReadOnlySpan<byte> ekb00010021Bytes = default)
    {
        var header = ParseHeader(omaBytes);
        return MagicGate0x0001Crypto.DecryptOmaPayload(omaBytes, header.Ea3BlockSize, trackId, hardwareDeviceKey, ekb00010021Bytes);
    }

    /// <inheritdoc />
    public SignedMagicGateSlotTemplate? ExtractSignedSlotTemplate(
        ReadOnlySpan<byte> omaBytes,
        int trackId,
        ReadOnlySpan<byte> ekb00010021Bytes = default) =>
        MagicGate0x0001Crypto.ExtractSignedSlotTemplate(omaBytes, trackId, ekb00010021Bytes);

    /// <inheritdoc />
    public MagicGate0x0001WrapResult WrapAtracIntoSignedSlot(
        ReadOnlySpan<byte> rawAtracFrames,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        SignedMagicGateSlotTemplate slotTemplate,
        Ea3TagMetadata tags,
        ReadOnlySpan<byte> ekb00010021Bytes = default,
        int ea3BlockSize = 2048)
    {
        ArgumentNullException.ThrowIfNull(slotTemplate);
        ArgumentNullException.ThrowIfNull(tags);
        var effectiveTags = tags with { DurationMs = durationMs > 0 ? durationMs : tags.DurationMs };
        byte[] baseTagFrames = BuildEa3TagFrames(effectiveTags);

        return MagicGate0x0001Crypto.WrapAtracIntoSignedSlot(
            rawAtracFrames,
            codecId,
            codecParams24,
            effectiveTags.DurationMs,
            frameCount,
            slotTemplate,
            baseTagFrames,
            ekb00010021Bytes,
            ea3BlockSize);
    }

    /// <inheritdoc />
    public byte[] BuildSignedSlotDummyStub(SignedMagicGateSlotTemplate slotTemplate) =>
        MagicGate0x0001Crypto.BuildSignedSlotDummyStub(slotTemplate);

    /// <inheritdoc />
    public bool EvaluateCopyProtectionLock(
        ReadOnlySpan<byte> omaHeaderOrFileBytes,
        bool isCntinfDamedaFlagSet = false,
        ushort chindcFlag = 0)
    {
        if (omaHeaderOrFileBytes.Length < 10 + Ea3DescriptorSize ||
            omaHeaderOrFileBytes[0] != (byte)'e' ||
            omaHeaderOrFileBytes[1] != (byte)'a' ||
            omaHeaderOrFileBytes[2] != (byte)'3')
        {
            return isCntinfDamedaFlagSet || chindcFlag != 0;
        }

        int ea3BodySize = DecodeSynchsafe(omaHeaderOrFileBytes.Slice(6, 4));
        int ea3TotalSize = 10 + ea3BodySize;
        if (ea3TotalSize + Ea3DescriptorSize > omaHeaderOrFileBytes.Length)
        {
            return isCntinfDamedaFlagSet || chindcFlag != 0;
        }

        ReadOnlySpan<byte> ea3 = omaHeaderOrFileBytes.Slice(ea3TotalSize, Ea3DescriptorSize);
        if (ea3[0] != (byte)'E' || ea3[1] != (byte)'A' || ea3[2] != (byte)'3')
        {
            return isCntinfDamedaFlagSet || chindcFlag != 0;
        }

        var encryptionId = (OmaEncryptionMode)BinaryPrimitives.ReadUInt16BigEndian(ea3.Slice(6, 2));
        if (encryptionId != OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            return false;
        }

        // Condition 4: 04CNTINF Dameda byte or 05CHINDC.DAT check-in/out flag
        if (isCntinfDamedaFlagSet || chindcFlag != 0)
        {
            return true;
        }

        ReadOnlySpan<byte> bklsi = ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> ulinf = ReadOnlySpan<byte>.Empty;
        bool hasBklsi = false;
        bool hasUlinf = false;

        int pos = 10;
        while (pos + 10 <= ea3TotalSize)
        {
            if (omaHeaderOrFileBytes[pos] == 0x00)
            {
                break;
            }

            int frameSize = BinaryPrimitives.ReadInt32BigEndian(omaHeaderOrFileBytes.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > ea3TotalSize)
            {
                break;
            }

            if (omaHeaderOrFileBytes[pos] == (byte)'G' &&
                omaHeaderOrFileBytes[pos + 1] == (byte)'E' &&
                omaHeaderOrFileBytes[pos + 2] == (byte)'O' &&
                omaHeaderOrFileBytes[pos + 3] == (byte)'B')
            {
                ReadOnlySpan<byte> geobBody = omaHeaderOrFileBytes.Slice(pos + 10, frameSize);
                if (!hasBklsi && MagicGate0x0001Crypto.TryExtractGeobData(geobBody, "OMG_BKLSI", out var bk) && bk.Length >= 428)
                {
                    bklsi = bk;
                    hasBklsi = true;
                }
                else if (!hasUlinf && MagicGate0x0001Crypto.TryExtractGeobData(geobBody, "OMG_ULINF", out var ul) && ul.Length >= 240)
                {
                    ulinf = ul;
                    hasUlinf = true;
                }
            }

            pos += 10 + frameSize;
        }

        // Condition 2: Restricted OMG_BKLSI usage rules (when OMG_BKLSI is present)
        if (hasBklsi)
        {
            if (!bklsi.Slice(0x15C, 12).SequenceEqual("SHARE_P_SID "u8) ||
                !bklsi.Slice(0x17C, 12).SequenceEqual("!REFID      "u8))
            {
                return true;
            }

            ReadOnlySpan<byte> bkRefid = bklsi.Slice(0x18C, 7);
            if (bkRefid[1] > 0x01 ||
                bkRefid[2] is 0x01 or 0x02 ||
                bkRefid[3] is 0x01 or 0x02 or 0x04 ||
                bkRefid[5] != 0x01 ||
                bkRefid[6] != 0x02)
            {
                return true;
            }
        }

        // Condition 3: Restricted OMG_ULINF usage rules
        if (hasUlinf)
        {
            ReadOnlySpan<byte> ulRefid = ulinf.Slice(0x0C0, 7);
            if (ulRefid[1] > 0x01 ||
                ulRefid[2] is 0x01 or 0x02 ||
                ulRefid[6] != 0x00)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public byte[] UpdateOmaEa3TagsPreservingCrypto(ReadOnlySpan<byte> existingOmaBytes, Ea3TagMetadata updatedTags)
    {
        ArgumentNullException.ThrowIfNull(updatedTags);
        if (existingOmaBytes.Length < 10 + Ea3DescriptorSize ||
            existingOmaBytes[0] != (byte)'e' ||
            existingOmaBytes[1] != (byte)'a' ||
            existingOmaBytes[2] != (byte)'3')
        {
            throw new InvalidDataException("Invalid .OMA file: missing 'ea3' header.");
        }

        int ea3BodySize = DecodeSynchsafe(existingOmaBytes.Slice(6, 4));
        int ea3TotalSize = 10 + ea3BodySize;
        if (ea3TotalSize + Ea3DescriptorSize > existingOmaBytes.Length)
        {
            throw new InvalidDataException("Invalid .OMA file: ea3 length exceeds buffer.");
        }

        ReadOnlySpan<byte> ea3 = existingOmaBytes.Slice(ea3TotalSize, Ea3DescriptorSize);
        if (ea3[0] != (byte)'E' || ea3[1] != (byte)'A' || ea3[2] != (byte)'3')
        {
            throw new InvalidDataException("Invalid .OMA file: missing 'EA3' descriptor.");
        }

        using var leadingGeobs = new MemoryStream(320);
        using var trailingGeobs = new MemoryStream(640);
        using var preservedOther = new MemoryStream(256);

        uint existingTlen = 0;
        int pos = 10;
        while (pos + 10 <= ea3TotalSize)
        {
            if (existingOmaBytes[pos] == 0x00)
            {
                break;
            }

            int frameSize = BinaryPrimitives.ReadInt32BigEndian(existingOmaBytes.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > ea3TotalSize)
            {
                break;
            }

            ReadOnlySpan<byte> rawFrame = existingOmaBytes.Slice(pos, 10 + frameSize);
            string frameId = Encoding.ASCII.GetString(rawFrame.Slice(0, 4));

            if (frameId == "GEOB")
            {
                ReadOnlySpan<byte> geobBody = rawFrame.Slice(10);
                if (MagicGate0x0001Crypto.TryExtractGeobData(geobBody, "OMG_ULINF", out _))
                {
                    leadingGeobs.Write(rawFrame);
                }
                else
                {
                    trailingGeobs.Write(rawFrame);
                }
            }
            else if (frameId is "TIT2" or "TPE1" or "TALB" or "TCON" or "TYER" or "TLEN" or "TCOM" or "COMM" or "RATE")
            {
                if (frameId == "TLEN" && frameSize > 1 && uint.TryParse(DecodeText(rawFrame[10], rawFrame.Slice(11)), out uint parsedTlen))
                {
                    existingTlen = parsedTlen;
                }
            }
            else if (frameId == "TXXX" && frameSize > 1)
            {
                string txxx = DecodeText(rawFrame[10], rawFrame.Slice(11));
                int nul = txxx.IndexOf('\0');
                string key = nul > 0 ? txxx[..nul] : txxx;
                if (!key.Equals("OMG_TRACK", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OMG_TRLDA", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OMG_TIT2S", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OMG_ATPE1", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OMG_TCOMS", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OMG_ATLINK", StringComparison.OrdinalIgnoreCase))
                {
                    preservedOther.Write(rawFrame);
                }
            }
            else
            {
                preservedOther.Write(rawFrame);
            }

            pos += 10 + frameSize;
        }

        uint ea3DurationMs = BinaryPrimitives.ReadUInt32BigEndian(ea3.Slice(36, 4));
        uint effectiveDuration = updatedTags.DurationMs > 0
            ? updatedTags.DurationMs
            : (existingTlen > 0 ? existingTlen : ea3DurationMs);
        byte[] newTextFrames = BuildEa3TagFrames(updatedTags with { DurationMs = effectiveDuration });

        int combinedPayloadLen = (int)(leadingGeobs.Length + newTextFrames.Length + preservedOther.Length + trailingGeobs.Length);
        int newEa3TotalSize = Math.Max(ea3TotalSize, 1024);
        while (newEa3TotalSize < 10 + combinedPayloadLen)
        {
            newEa3TotalSize += 1024;
        }

        ReadOnlySpan<byte> ea3AndAudioTail = existingOmaBytes.Slice(ea3TotalSize);
        byte[] result = new byte[newEa3TotalSize + ea3AndAudioTail.Length];
        Span<byte> dest = result.AsSpan();

        dest[0] = (byte)'e';
        dest[1] = (byte)'a';
        dest[2] = (byte)'3';
        dest[3] = 0x03;
        dest[4] = 0x00;
        dest[5] = 0x00;
        EncodeSynchsafe(newEa3TotalSize - 10, dest.Slice(6, 4));

        int writePos = 10;
        leadingGeobs.ToArray().CopyTo(dest.Slice(writePos));
        writePos += (int)leadingGeobs.Length;
        newTextFrames.CopyTo(dest.Slice(writePos));
        writePos += newTextFrames.Length;
        preservedOther.ToArray().CopyTo(dest.Slice(writePos));
        writePos += (int)preservedOther.Length;
        trailingGeobs.ToArray().CopyTo(dest.Slice(writePos));

        ea3AndAudioTail.CopyTo(dest.Slice(newEa3TotalSize));
        return result;
    }

    private static byte[] BuildEa3TagFrames(Ea3TagMetadata tags)
    {
        using var ms = new MemoryStream(512);

        WriteEa3TextFrame(ms, "TIT2", tags.Title);
        if (!string.IsNullOrEmpty(tags.Artist))
        {
            WriteEa3TextFrame(ms, "TPE1", tags.Artist);
        }

        if (!string.IsNullOrEmpty(tags.Album))
        {
            WriteEa3TextFrame(ms, "TALB", tags.Album);
        }

        if (!string.IsNullOrEmpty(tags.Genre))
        {
            WriteEa3TextFrame(ms, "TCON", tags.Genre);
        }

        if (tags.TrackNumber is > 0)
        {
            WriteEa3TxxxFrame(ms, "OMG_TRACK", tags.TrackNumber.Value.ToString());
        }

        if (tags.Year is > 0)
        {
            WriteEa3TextFrame(ms, "TYER", tags.Year.Value.ToString("D4"));
        }

        WriteEa3TextFrame(ms, "TLEN", tags.DurationMs.ToString());
        WriteEa3TxxxFrame(ms, "OMG_TRLDA", $"{(tags.Year is > 0 ? tags.Year.Value : 2026):D4}/01/01 00:00:00");

        if (!string.IsNullOrEmpty(tags.SortTitle))
        {
            WriteEa3TxxxFrame(ms, "OMG_TIT2S", tags.SortTitle);
        }

        if (!string.IsNullOrEmpty(tags.AlbumArtist))
        {
            WriteEa3TxxxFrame(ms, "OMG_ATPE1", tags.AlbumArtist);
        }

        if (!string.IsNullOrEmpty(tags.Composer))
        {
            WriteEa3TextFrame(ms, "TCOM", tags.Composer);
            WriteEa3TxxxFrame(ms, "OMG_TCOMS", tags.Composer);
        }

        if (tags.Rating is >= 1 and <= 5)
        {
            WriteEa3TextFrame(ms, "RATE", tags.Rating.Value.ToString());
        }

        if (!string.IsNullOrEmpty(tags.Comment))
        {
            byte[] commentBytes = Encoding.BigEndianUnicode.GetBytes(ClipTag(tags.Comment));
            byte[] commPayload = new byte[5 + commentBytes.Length]; // "eng" + UTF-16BE NUL terminator
            commPayload[0] = (byte)'e';
            commPayload[1] = (byte)'n';
            commPayload[2] = (byte)'g';
            commentBytes.CopyTo(commPayload, 5);
            WriteFrameRaw(ms, "COMM", encodingByte: 0x02, commPayload);
        }

        if (!string.IsNullOrEmpty(tags.ArtistLinks))
        {
            WriteEa3TxxxFrame(ms, "OMG_ATLINK", tags.ArtistLinks);
        }

        return ms.ToArray();
    }

    private static void WriteEa3TextFrame(Stream stream, string frameId, string value)
    {
        string clipped = ClipTag(value);
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(clipped);
        WriteFrameRaw(stream, frameId, encodingByte: 0x02, utf16);
    }

    private static void WriteEa3TxxxFrame(Stream stream, string description, string value)
    {
        byte[] descBytes = Encoding.BigEndianUnicode.GetBytes(description);
        byte[] valBytes = Encoding.BigEndianUnicode.GetBytes(ClipTag(value));
        byte[] combined = new byte[descBytes.Length + 2 + valBytes.Length];
        descBytes.CopyTo(combined, 0);
        // UTF-16 null terminator [0x00, 0x00] sits between description and value.
        valBytes.CopyTo(combined, descBytes.Length + 2);
        WriteFrameRaw(stream, "TXXX", encodingByte: 0x02, combined);
    }

    private static void WriteFrameRaw(Stream stream, string frameId, byte encodingByte, byte[] payload)
    {
        Span<byte> header = stackalloc byte[11];
        Encoding.ASCII.GetBytes(frameId.AsSpan(0, 4), header.Slice(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), payload.Length + 1);
        header[8] = 0x00;
        header[9] = 0x00;
        header[10] = encodingByte;
        stream.Write(header);
        stream.Write(payload);
    }

    private static byte[] BuildStandardId3v23Header(Ea3TagMetadata tags)
    {
        using var body = new MemoryStream(256);

        if (!string.IsNullOrEmpty(tags.Title))
        {
            WriteId3Utf16LeFrame(body, "TIT2", tags.Title);
        }

        if (!string.IsNullOrEmpty(tags.Artist))
        {
            WriteId3Utf16LeFrame(body, "TPE1", tags.Artist);
        }

        if (!string.IsNullOrEmpty(tags.Album))
        {
            WriteId3Utf16LeFrame(body, "TALB", tags.Album);
        }

        if (!string.IsNullOrEmpty(tags.Genre))
        {
            WriteId3Utf16LeFrame(body, "TCON", tags.Genre);
        }

        if (tags.Year is > 0)
        {
            WriteId3Utf16LeFrame(body, "TYER", tags.Year.Value.ToString("D4"));
        }

        if (tags.TrackNumber is > 0)
        {
            WriteId3Utf16LeFrame(body, "TRCK", tags.TrackNumber.Value.ToString());
        }

        byte[] bodyBytes = body.ToArray();
        byte[] result = new byte[10 + bodyBytes.Length];
        result[0] = (byte)'I';
        result[1] = (byte)'D';
        result[2] = (byte)'3';
        result[3] = 0x03;
        result[4] = 0x00;
        result[5] = 0x00;
        EncodeSynchsafe(bodyBytes.Length, result.AsSpan(6, 4));
        bodyBytes.CopyTo(result, 10);
        return result;
    }

    private static void WriteId3Utf16LeFrame(Stream stream, string frameId, string text)
    {
        byte[] utf16Le = Encoding.Unicode.GetBytes(text);
        byte[] payload = new byte[2 + utf16Le.Length];
        payload[0] = 0xFF;
        payload[1] = 0xFE;
        utf16Le.CopyTo(payload, 2);
        WriteFrameRaw(stream, frameId, encodingByte: 0x01, payload);
    }

    private static string ClipTag(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxTagCharacters ? value : value[..MaxTagCharacters];
    }

    private static string DecodeText(byte encodingByte, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        string decoded = encodingByte switch
        {
            0x00 => Encoding.Latin1.GetString(payload),
            0x01 when payload.Length >= 2 && payload[0] == 0xFE && payload[1] == 0xFF =>
                Encoding.BigEndianUnicode.GetString(payload.Slice(2)),
            0x01 when payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE =>
                Encoding.Unicode.GetString(payload.Slice(2)),
            0x01 => Encoding.Unicode.GetString(payload),
            0x02 => Encoding.BigEndianUnicode.GetString(payload),
            0x03 => Encoding.UTF8.GetString(payload),
            _ => Encoding.BigEndianUnicode.GetString(payload)
        };

        return decoded.TrimEnd('\0');
    }

    private static int DecodeSynchsafe(ReadOnlySpan<byte> bytes) =>
        ((bytes[0] & 0x7F) << 21) |
        ((bytes[1] & 0x7F) << 14) |
        ((bytes[2] & 0x7F) << 7) |
        (bytes[3] & 0x7F);

    private static void EncodeSynchsafe(int value, Span<byte> dest)
    {
        dest[0] = (byte)((value >> 21) & 0x7F);
        dest[1] = (byte)((value >> 14) & 0x7F);
        dest[2] = (byte)((value >> 7) & 0x7F);
        dest[3] = (byte)(value & 0x7F);
    }

    private readonly record struct MpegFrameMetrics(
        byte VersionCode,
        byte LayerCode,
        byte BitrateIndex,
        int BitrateKbps,
        int SampleRateHz,
        int SamplesPerFrame,
        int FrameLengthBytes,
        byte ChannelMode);

    private static bool TryParseMpegFrameHeader(ReadOnlySpan<byte> header, out MpegFrameMetrics metrics)
    {
        metrics = default;
        if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        byte versionCode = (byte)((header[1] >> 3) & 0x03);
        byte layerCode = (byte)((header[1] >> 1) & 0x03);
        byte bitrateIndex = (byte)((header[2] >> 4) & 0x0F);
        byte srateIndex = (byte)((header[2] >> 2) & 0x03);
        int padding = (header[2] >> 1) & 0x01;
        byte channelMode = (byte)((header[3] >> 6) & 0x03);

        // Support Layer III (layerCode == 1), skip reserved version (1), free/bad bitrate (0, 15), reserved srate (3).
        if (versionCode == 1 || layerCode != 1 || bitrateIndex is 0 or 15 || srateIndex == 3)
        {
            return false;
        }

        int bitrateKbps = versionCode == 3
            ? Mpeg1L3BitratesKbps[bitrateIndex]
            : Mpeg2L3BitratesKbps[bitrateIndex];

        int sampleRateHz = versionCode switch
        {
            3 => Mpeg1SampleRatesHz[srateIndex],
            2 => Mpeg2SampleRatesHz[srateIndex],
            0 => Mpeg25SampleRatesHz[srateIndex],
            _ => 0
        };

        if (bitrateKbps <= 0 || sampleRateHz <= 0)
        {
            return false;
        }

        int samplesPerFrame = versionCode == 3 ? 1152 : 576;
        int factor = versionCode == 3 ? 144 : 72;
        int frameLength = (factor * bitrateKbps * 1000 / sampleRateHz) + padding;

        if (frameLength < 4)
        {
            return false;
        }

        metrics = new MpegFrameMetrics(
            versionCode,
            layerCode,
            bitrateIndex,
            bitrateKbps,
            sampleRateHz,
            samplesPerFrame,
            frameLength,
            channelMode);
        return true;
    }

    private static bool ContainsXingHeader(ReadOnlySpan<byte> frameBytes, MpegFrameMetrics metrics)
    {
        int offset = (metrics.VersionCode, metrics.ChannelMode) switch
        {
            (3, 3) => 21, // MPEG-1 Mono
            (3, _) => 36, // MPEG-1 Stereo
            (_, 3) => 13, // MPEG-2/2.5 Mono
            _ => 21       // MPEG-2/2.5 Stereo
        };

        if (offset + 4 > frameBytes.Length)
        {
            return false;
        }

        // LAME distinguishes VBR ("Xing") from CBR ("Info").
        return frameBytes.Slice(offset, 4).SequenceEqual("Xing"u8);
    }
}
