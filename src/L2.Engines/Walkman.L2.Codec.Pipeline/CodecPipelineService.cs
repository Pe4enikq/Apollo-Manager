using System.Buffers.Binary;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.L2.Codec.Pipeline.Atrac3Plus;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Bidirectional audio conversion and .OMA container pipeline supporting MP3 pass-through,
/// LAME MP3 transcoding, external atracdenc ATRAC3 encoding, vgmstream decoding, and device-to-device re-keying.
/// </summary>
public sealed class CodecPipelineService : ICodecService, IAudioStreamDecoder
{
    private const int Ea3DescriptorSize = 96;

    private readonly Func<IOpenMgCryptoService> _cryptoResolver;

    public CodecPipelineService(Func<IOpenMgCryptoService> cryptoResolver)
    {
        _cryptoResolver = cryptoResolver ?? throw new ArgumentNullException(nameof(cryptoResolver));
    }

    /// <inheritdoc />
    public Ea3TagMetadata ExtractMetadata(string sourceFilePath) =>
        AudioMetadataHelper.ReadMetadata(sourceFilePath);

    /// <inheritdoc />
    public async Task<TranscodeResult> ProcessToOmaAsync(TranscodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(request.SourceFilePath))
        {
            return new TranscodeResult(false, request.StagingOutputPath, 0, 0, 0, $"Source file not found: '{request.SourceFilePath}'.");
        }

        var crypto = _cryptoResolver();
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_codec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] omaBytes;
            byte[]? lsiKeyring48 = null;
            byte[]? maclistEntry8 = null;
            uint? encodedFrameCount = null;

            if (request.TargetCodec == AudioCodecId.LinearPcm)
            {
                string outExt = Path.GetExtension(request.StagingOutputPath).ToLowerInvariant();
                string pcmWavPath = Path.Combine(tempDir, "pcm44k.wav");
                AudioResamplerPipeline.DecodeTo44100StereoWav(request.SourceFilePath, pcmWavPath, ct);

                byte[] outputBytes;
                if (outExt == ".flac")
                {
                    string flacPath = Path.Combine(tempDir, "out.flac");
                    WriteVerbatimFlacFromWav(pcmWavPath, flacPath, ct);
                    outputBytes = await File.ReadAllBytesAsync(flacPath, ct).ConfigureAwait(false);
                }
                else
                {
                    outputBytes = await File.ReadAllBytesAsync(pcmWavPath, ct).ConfigureAwait(false);
                }

                await WriteStagedFileAtomicAsync(request.StagingOutputPath, outputBytes, ct).ConfigureAwait(false);
                if (outExt == ".flac")
                {
                    AudioMetadataHelper.WriteMetadata(request.StagingOutputPath, request.Tags);
                }

                uint durMs = request.Tags.DurationMs > 0
                    ? request.Tags.DurationMs
                    : ComputeWavDurationMs(pcmWavPath);
                return new TranscodeResult(true, request.StagingOutputPath, durMs, 0, 0, null);
            }

            if (request.TargetCodec == AudioCodecId.Mp3 &&
                request.EncryptionMode == OmaEncryptionMode.Unprotected_0xFFFF &&
                request.StagingOutputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                string encodedMp3 = Path.Combine(tempDir, "offline.mp3");
                AudioResamplerPipeline.TranscodeToMp3File(request.SourceFilePath, encodedMp3, NormalizeMp3Bitrate(request.TargetBitrateKbps), ct);
                byte[] mp3Bytes = await File.ReadAllBytesAsync(encodedMp3, ct).ConfigureAwait(false);
                await WriteStagedFileAtomicAsync(request.StagingOutputPath, mp3Bytes, ct).ConfigureAwait(false);
                AudioMetadataHelper.WriteMetadata(request.StagingOutputPath, request.Tags);
                var mp3Info = crypto.InspectMp3Payload(mp3Bytes);
                return new TranscodeResult(true, request.StagingOutputPath, mp3Info.DurationMs, mp3Info.FrameCount, mp3Info.CodecParams24, null);
            }

            if (request.TargetCodec == AudioCodecId.Mp3)
            {
                if (request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
                {
                    return new TranscodeResult(
                        false,
                        request.StagingOutputPath,
                        0,
                        0,
                        0,
                        "MP3 streams on Sony Walkman require 0xFFFE or 0xFFFF framing rather than MagicGate 0x0001.");
                }

                omaBytes = await BuildMp3OmaAsync(request, crypto, tempDir, ct).ConfigureAwait(false);
            }
            else if (request.TargetCodec == AudioCodecId.Atrac1)
            {
                return await BuildAtrac1Async(request, tempDir, ct).ConfigureAwait(false);
            }
            else if (request.TargetCodec == AudioCodecId.Atrac3)
            {
                var (wrapped, frameCount) = await BuildAtrac3OmaAsync(request, crypto, tempDir, ct).ConfigureAwait(false);
                omaBytes = wrapped.OmaBytes;
                lsiKeyring48 = wrapped.LsiKeyring48;
                maclistEntry8 = wrapped.MaclistEntry8;
                encodedFrameCount = frameCount;
            }
            else if (request.TargetCodec == AudioCodecId.Atrac3Plus)
            {
                var (wrapped, frameCount) = await BuildAtrac3PlusOmaAsync(request, crypto, tempDir, ct).ConfigureAwait(false);
                omaBytes = wrapped.OmaBytes;
                lsiKeyring48 = wrapped.LsiKeyring48;
                maclistEntry8 = wrapped.MaclistEntry8;
                encodedFrameCount = frameCount;
            }
            else
            {
                return new TranscodeResult(
                    false,
                    request.StagingOutputPath,
                    0,
                    0,
                    0,
                    $"Target codec '{request.TargetCodec}' is not supported by Codec.Pipeline.");
            }

            await WriteStagedFileAtomicAsync(request.StagingOutputPath, omaBytes, ct).ConfigureAwait(false);
            var parsed = crypto.ParseHeader(omaBytes);

            return new TranscodeResult(
                Success: true,
                OutputOmaPath: request.StagingOutputPath,
                DurationMs: parsed.DurationMs,
                FrameCount: parsed.FrameCount > 0 ? parsed.FrameCount : (encodedFrameCount ?? 0u),
                CodecParams24: parsed.CodecParams24,
                ErrorMessage: null,
                LsiKeyring48: lsiKeyring48,
                MaclistEntry8: maclistEntry8);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranscodeResult(false, request.StagingOutputPath, 0, 0, 0, ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> DecodeOmaToPlayableWavStreamAsync(
        string sourceOmaPath,
        uint? hardwareDeviceKey = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceOmaPath) || !File.Exists(sourceOmaPath))
        {
            return null;
        }

        string tempWav = Path.Combine(Path.GetTempPath(), $"apollo_play_{Guid.NewGuid():N}.wav");
        try
        {
            bool ok = await ExtractFromOmaAsync(sourceOmaPath, tempWav, hardwareDeviceKey, ct).ConfigureAwait(false);
            if (!ok || !File.Exists(tempWav))
            {
                return null;
            }

            byte[] wavBytes = await File.ReadAllBytesAsync(tempWav, ct).ConfigureAwait(false);
            return new MemoryStream(wavBytes, writable: false);
        }
        finally
        {
            if (File.Exists(tempWav))
            {
                try
                {
                    File.Delete(tempWav);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    private static uint ComputeWavDurationMs(string wavPath)
    {
        using var reader = new NAudio.Wave.WaveFileReader(wavPath);
        return (uint)Math.Max(1, Math.Round(reader.TotalTime.TotalMilliseconds));
    }

    private static void WriteVerbatimFlacFromWav(string wavPath, string flacPath, CancellationToken ct)
    {
        using var reader = new NAudio.Wave.WaveFileReader(wavPath);
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        int bitsPerSample = reader.WaveFormat.BitsPerSample;
        long totalSamples = reader.SampleCount;

        const int blockSize = 4096;
        int bytesPerSample = bitsPerSample / 8;
        int framePcmBytes = blockSize * channels * bytesPerSample;
        byte[] pcmBuf = new byte[framePcmBytes];

        using var fs = new FileStream(flacPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        fs.Write("fLaC"u8);

        // STREAMINFO (34 bytes, last-metadata-block = 1)
        byte[] streamInfo = new byte[38];
        streamInfo[0] = 0x80; // Last metadata block + type 0 (STREAMINFO)
        streamInfo[1] = 0x00;
        streamInfo[2] = 0x00;
        streamInfo[3] = 0x22; // 34 bytes
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(4, 2), blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(6, 2), blockSize);
        // min/max frame size = 0 (bytes 8..13)
        ulong packed =
            ((ulong)(sampleRate & 0xFFFFF) << 44) |
            ((ulong)((channels - 1) & 0x07) << 41) |
            ((ulong)((bitsPerSample - 1) & 0x1F) << 36) |
            ((ulong)totalSamples & 0xFFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(14, 8), packed);
        fs.Write(streamInfo);

        uint frameNumber = 0;
        int bytesRead;
        while ((bytesRead = reader.Read(pcmBuf, 0, pcmBuf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int samplesInBlock = bytesRead / (channels * bytesPerSample);
            if (samplesInBlock <= 0)
            {
                break;
            }

            using var frameMs = new MemoryStream(bytesRead + 64);
            frameMs.WriteByte(0xFF);
            frameMs.WriteByte(0xF8);

            byte bsCode = samplesInBlock == 4096 ? (byte)0x0C : (byte)0x07; // 0x07 = 16-bit (blocksize - 1) at end of header
            byte srCode = sampleRate == 44100 ? (byte)0x09 : (byte)0x0A;
            frameMs.WriteByte((byte)((bsCode << 4) | srCode));

            byte chAssign = (byte)(channels - 1);
            byte bpsCode = bitsPerSample == 16 ? (byte)0x04 : (byte)0x00;
            frameMs.WriteByte((byte)((chAssign << 4) | (bpsCode << 1)));

            WriteUtf8UInt32(frameMs, frameNumber++);
            if (bsCode == 0x07)
            {
                int bsMinus1 = samplesInBlock - 1;
                frameMs.WriteByte((byte)(bsMinus1 >> 8));
                frameMs.WriteByte((byte)(bsMinus1 & 0xFF));
            }

            byte[] hdrSpan = frameMs.ToArray();
            frameMs.WriteByte(ComputeFlacCrc8(hdrSpan));

            // Subframes: SUBFRAME_VERBATIM (type 1 => 0b0_000001_0 = 0x02)
            for (int ch = 0; ch < channels; ch++)
            {
                frameMs.WriteByte(0x02);
                for (int s = 0; s < samplesInBlock; s++)
                {
                    int idx = (s * channels + ch) * bytesPerSample;
                    // WAV is little-endian signed 16-bit; FLAC verbatim is big-endian.
                    frameMs.WriteByte(pcmBuf[idx + 1]);
                    frameMs.WriteByte(pcmBuf[idx]);
                }
            }

            byte[] frameWithoutCrc16 = frameMs.ToArray();
            ushort crc16 = ComputeFlacCrc16(frameWithoutCrc16);
            frameMs.WriteByte((byte)(crc16 >> 8));
            frameMs.WriteByte((byte)(crc16 & 0xFF));

            byte[] fullFrame = frameMs.ToArray();
            fs.Write(fullFrame, 0, fullFrame.Length);
        }
    }

    private static void WriteUtf8UInt32(Stream s, uint val)
    {
        if (val < 0x80)
        {
            s.WriteByte((byte)val);
        }
        else if (val < 0x800)
        {
            s.WriteByte((byte)(0xC0 | (val >> 6)));
            s.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
        else if (val < 0x10000)
        {
            s.WriteByte((byte)(0xE0 | (val >> 12)));
            s.WriteByte((byte)(0x80 | ((val >> 6) & 0x3F)));
            s.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
        else
        {
            s.WriteByte((byte)(0xF0 | (val >> 18)));
            s.WriteByte((byte)(0x80 | ((val >> 12) & 0x3F)));
            s.WriteByte((byte)(0x80 | ((val >> 6) & 0x3F)));
            s.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
    }

    private static byte ComputeFlacCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
            }
        }
        return crc;
    }

    private static ushort ComputeFlacCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x8005) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <inheritdoc />
    public async Task<bool> ExtractFromOmaAsync(
        string sourceOmaPath,
        string outputFilePath,
        uint? hardwareDeviceKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOmaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        if (!File.Exists(sourceOmaPath))
        {
            return false;
        }

        var crypto = _cryptoResolver();
        string srcExt = Path.GetExtension(sourceOmaPath).ToLowerInvariant();
        string outExt = Path.GetExtension(outputFilePath).ToLowerInvariant();
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Non-EA3 Sony/game/niche formats (.at3, .at9, .aea, .wv, .ape): decode via atracdenc (.aea) or vgmstream.
            if (srcExt is ".at3" or ".at9" or ".aea" or ".wv" or ".ape")
            {
                string decodedWav = Path.Combine(tempDir, "ext_out.wav");
                if (srcExt == ".aea")
                {
                    string atracdencExe = ExternalCodecToolRunner.ResolveToolExecutable("atracdenc", "atracdenc.exe");
                    await ExternalCodecToolRunner.RunProcessAsync(atracdencExe, ["-d", "-i", sourceOmaPath, "-o", decodedWav], ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        string vgmCli = ExternalCodecToolRunner.ResolveToolExecutable("vgmstream", "vgmstream-cli.exe");
                        await ExternalCodecToolRunner.RunProcessAsync(vgmCli, ["-o", decodedWav, sourceOmaPath], ct)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        AudioResamplerPipeline.DecodeTo44100StereoWav(sourceOmaPath, decodedWav, ct);
                    }
                }

                if (!File.Exists(decodedWav))
                {
                    return false;
                }

                if (outExt == ".mp3")
                {
                    string encodedMp3 = Path.Combine(tempDir, "reencoded.mp3");
                    AudioResamplerPipeline.ConvertWavToMp3File(decodedWav, encodedMp3, 192, ct);
                    byte[] mp3Bytes = await File.ReadAllBytesAsync(encodedMp3, ct).ConfigureAwait(false);
                    await WriteStagedFileAtomicAsync(outputFilePath, mp3Bytes, ct).ConfigureAwait(false);
                    return true;
                }

                byte[] wavOut = await File.ReadAllBytesAsync(decodedWav, ct).ConfigureAwait(false);
                await WriteStagedFileAtomicAsync(outputFilePath, wavOut, ct).ConfigureAwait(false);
                return true;
            }

            byte[] omaBytes = await File.ReadAllBytesAsync(sourceOmaPath, ct).ConfigureAwait(false);
            var header = crypto.ParseHeader(omaBytes);
            if (header.CodecId == AudioCodecId.Mp3)
            {
                int trackId = TryParseTrackIdFromPath(sourceOmaPath) ?? 1;
                byte[] cleanMp3WithId3 = crypto.UnwrapOmaToMp3(omaBytes, trackId, hardwareDeviceKey);

                if (outExt == ".mp3")
                {
                    await WriteStagedFileAtomicAsync(outputFilePath, cleanMp3WithId3, ct).ConfigureAwait(false);
                    AudioMetadataHelper.WriteMetadata(outputFilePath, header.Tags);
                    return true;
                }

                string tempMp3 = Path.Combine(tempDir, "unwrapped.mp3");
                await File.WriteAllBytesAsync(tempMp3, cleanMp3WithId3, ct).ConfigureAwait(false);

                string tempWav = Path.Combine(tempDir, "decoded.wav");
                AudioResamplerPipeline.DecodeTo44100StereoWav(tempMp3, tempWav, ct);
                byte[] wavBytes = await File.ReadAllBytesAsync(tempWav, ct).ConfigureAwait(false);
                await WriteStagedFileAtomicAsync(outputFilePath, wavBytes, ct).ConfigureAwait(false);
                return true;
            }

            if (header.CodecId is AudioCodecId.Atrac3 or AudioCodecId.Atrac3Plus or AudioCodecId.Atrac3Lossless or AudioCodecId.Atrac3PlusLossless)
            {
                string vgmInputPath = sourceOmaPath;
                if (header.EncryptionId is OmaEncryptionMode.HardwareXor_0xFFFE or OmaEncryptionMode.MagicGateLsi_0x0001)
                {
                    if (header.EncryptionId == OmaEncryptionMode.HardwareXor_0xFFFE && !hardwareDeviceKey.HasValue)
                    {
                        return false;
                    }

                    int trackId = TryParseTrackIdFromPath(sourceOmaPath) ?? 1;
                    byte[]? ekbBytes = TryLoadAncestorEkb00010021(sourceOmaPath);
                    byte[] plainPayload = crypto.DecryptOmaPayload(omaBytes, trackId, hardwareDeviceKey, ekbBytes);
                    var unwrapped = crypto.WrapAtracToOma(
                        plainPayload,
                        header.CodecId,
                        header.CodecParams24,
                        header.DurationMs,
                        header.FrameCount,
                        trackId,
                        hardwareDeviceKey: null,
                        OmaEncryptionMode.Unprotected_0xFFFF,
                        header.Tags,
                        Math.Max(1024, header.Ea3BlockSize),
                        header.ContentId20);

                    vgmInputPath = Path.Combine(tempDir, "decrypted.oma");
                    await File.WriteAllBytesAsync(vgmInputPath, unwrapped.OmaBytes, ct).ConfigureAwait(false);
                }

                string vgmCli = ExternalCodecToolRunner.ResolveToolExecutable("vgmstream", "vgmstream-cli.exe");
                string decodedWav = Path.Combine(tempDir, "vgm_out.wav");
                await ExternalCodecToolRunner.RunProcessAsync(vgmCli, ["-o", decodedWav, vgmInputPath], ct)
                    .ConfigureAwait(false);

                if (outExt == ".mp3")
                {
                    string encodedMp3 = Path.Combine(tempDir, "reencoded.mp3");
                    AudioResamplerPipeline.ConvertWavToMp3File(decodedWav, encodedMp3, 192, ct);
                    byte[] mp3Bytes = await File.ReadAllBytesAsync(encodedMp3, ct).ConfigureAwait(false);
                    await WriteStagedFileAtomicAsync(outputFilePath, mp3Bytes, ct).ConfigureAwait(false);
                    AudioMetadataHelper.WriteMetadata(outputFilePath, header.Tags);
                    return true;
                }

                byte[] wavOut = await File.ReadAllBytesAsync(decodedWav, ct).ConfigureAwait(false);
                await WriteStagedFileAtomicAsync(outputFilePath, wavOut, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<TranscodeResult> RekeyOmaAsync(
        string sourceOmaPath,
        string stagingOutputPath,
        int targetTrackId,
        uint? sourceDeviceKey,
        uint? targetDeviceKey,
        OmaEncryptionMode targetEncryptionMode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOmaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingOutputPath);

        var crypto = _cryptoResolver();
        byte[] sourceBytes = await File.ReadAllBytesAsync(sourceOmaPath, ct).ConfigureAwait(false);
        var header = crypto.ParseHeader(sourceBytes);

        int sourceTrackId = TryParseTrackIdFromPath(sourceOmaPath) ?? 1;
        if (header.CodecId == AudioCodecId.Mp3)
        {
            if (targetEncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
            {
                return new TranscodeResult(false, stagingOutputPath, 0, 0, 0, "MP3 tracks cannot be re-keyed into MagicGate 0x0001.");
            }

            byte[] cleanMp3 = crypto.UnwrapOmaToMp3(sourceBytes, sourceTrackId, sourceDeviceKey);
            byte[] targetOma = crypto.WrapMp3ToOma(
                cleanMp3,
                targetTrackId,
                targetDeviceKey,
                targetEncryptionMode,
                header.Tags,
                header.Ea3BlockSize);

            await WriteStagedFileAtomicAsync(stagingOutputPath, targetOma, ct).ConfigureAwait(false);
            var parsedTarget = crypto.ParseHeader(targetOma);
            return new TranscodeResult(true, stagingOutputPath, parsedTarget.DurationMs, parsedTarget.FrameCount, parsedTarget.CodecParams24, null);
        }

        if (header.CodecId is AudioCodecId.Atrac3 or AudioCodecId.Atrac3Plus)
        {
            byte[]? ekbBytes = TryLoadAncestorEkb00010021(sourceOmaPath);
            byte[] plainAtrac = crypto.DecryptOmaPayload(sourceBytes, sourceTrackId, sourceDeviceKey, ekbBytes);
            var wrapped = crypto.WrapAtracToOma(
                plainAtrac,
                header.CodecId,
                header.CodecParams24,
                header.DurationMs,
                header.FrameCount,
                targetTrackId,
                targetDeviceKey,
                targetEncryptionMode,
                header.Tags,
                Math.Max(header.Ea3BlockSize, targetEncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 ? 2048 : 1024),
                header.ContentId20);

            await WriteStagedFileAtomicAsync(stagingOutputPath, wrapped.OmaBytes, ct).ConfigureAwait(false);
            var parsedTarget = crypto.ParseHeader(wrapped.OmaBytes);
            return new TranscodeResult(
                true,
                stagingOutputPath,
                parsedTarget.DurationMs,
                parsedTarget.FrameCount,
                parsedTarget.CodecParams24,
                null,
                wrapped.LsiKeyring48,
                wrapped.MaclistEntry8);
        }

        return new TranscodeResult(false, stagingOutputPath, 0, 0, 0, $"Raw re-keying is not supported for codec {header.CodecId}.");
    }

    private static async Task<byte[]> BuildMp3OmaAsync(
        TranscodeRequest request,
        IOpenMgCryptoService crypto,
        string tempDir,
        CancellationToken ct)
    {
        byte[] mp3SourceBytes;
        bool isMp3Ext = Path.GetExtension(request.SourceFilePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase);

        if (isMp3Ext)
        {
            byte[] candidate = await File.ReadAllBytesAsync(request.SourceFilePath, ct).ConfigureAwait(false);
            var info = crypto.InspectMp3Payload(candidate);
            if (info.SampleRateHz == AudioResamplerPipeline.TargetSampleRateHz)
            {
                // Fast pass-through wrapper without lossy re-encoding.
                mp3SourceBytes = candidate;
            }
            else
            {
                string resampledMp3 = Path.Combine(tempDir, "resampled.mp3");
                AudioResamplerPipeline.TranscodeToMp3File(request.SourceFilePath, resampledMp3, NormalizeMp3Bitrate(request.TargetBitrateKbps), ct);
                mp3SourceBytes = await File.ReadAllBytesAsync(resampledMp3, ct).ConfigureAwait(false);
            }
        }
        else
        {
            string encodedMp3 = Path.Combine(tempDir, "encoded.mp3");
            AudioResamplerPipeline.TranscodeToMp3File(request.SourceFilePath, encodedMp3, NormalizeMp3Bitrate(request.TargetBitrateKbps), ct);
            mp3SourceBytes = await File.ReadAllBytesAsync(encodedMp3, ct).ConfigureAwait(false);
        }

        return crypto.WrapMp3ToOma(
            mp3SourceBytes,
            request.TrackId,
            request.HardwareDeviceKey,
            request.EncryptionMode,
            request.Tags,
            request.Ea3BlockSize);
    }

    private static async Task<TranscodeResult> BuildAtrac1Async(
        TranscodeRequest request,
        string tempDir,
        CancellationToken ct)
    {
        string normalizedWav = Path.Combine(tempDir, "pcm44k_at1.wav");
        AudioResamplerPipeline.DecodeTo44100StereoWav(request.SourceFilePath, normalizedWav, ct);

        string atracdencExe = ExternalCodecToolRunner.ResolveToolExecutable("atracdenc", "atracdenc.exe");
        string aeaPath = Path.Combine(tempDir, "atrac1_sp.aea");
        await ExternalCodecToolRunner.RunProcessAsync(atracdencExe, ["-e", "atrac1", "-i", normalizedWav, "-o", aeaPath], ct)
            .ConfigureAwait(false);

        byte[] aeaBytes = await File.ReadAllBytesAsync(aeaPath, ct).ConfigureAwait(false);
        await WriteStagedFileAtomicAsync(request.StagingOutputPath, aeaBytes, ct).ConfigureAwait(false);

        uint frameCount = aeaBytes.Length > 2048 ? (uint)((aeaBytes.Length - 2048) / 424) : 0u;
        uint durationMs = request.Tags.DurationMs > 0
            ? request.Tags.DurationMs
            : (uint)Math.Round(frameCount * 512.0 * 1000.0 / 44100.0, MidpointRounding.AwayFromZero);

        return new TranscodeResult(true, request.StagingOutputPath, durationMs, frameCount, 0x00000000u, null);
    }

    private static async Task<(MagicGate0x0001WrapResult Wrapped, uint FrameCount)> BuildAtrac3OmaAsync(
        TranscodeRequest request,
        IOpenMgCryptoService crypto,
        string tempDir,
        CancellationToken ct)
    {
        string normalizedWav = Path.Combine(tempDir, "pcm44k.wav");
        AudioResamplerPipeline.DecodeTo44100StereoWav(request.SourceFilePath, normalizedWav, ct);

        string atracdencExe = ExternalCodecToolRunner.ResolveToolExecutable("atracdenc", "atracdenc.exe");
        string rawOmaPath = Path.Combine(tempDir, "atrac3_raw.oma");

        // atracdenc v0.2.3: "-e atrac3_lp4" emits exact Sony 66k LP4 (00 02 20 18, 192 B/frame);
        // "-e atrac3" (without --bitrate, which is RealMedia-only) emits exact Sony 132k LP2 (00 00 20 30, 384 B/frame).
        string encoderMode = request.TargetBitrateKbps is > 0 and <= 70 ? "atrac3_lp4" : "atrac3";
        var args = new List<string> { "-e", encoderMode };

        args.Add("-i");
        args.Add(normalizedWav);
        args.Add("-o");
        args.Add(rawOmaPath);

        await ExternalCodecToolRunner.RunProcessAsync(atracdencExe, args, ct).ConfigureAwait(false);
        byte[] rawOma = await File.ReadAllBytesAsync(rawOmaPath, ct).ConfigureAwait(false);

        if (rawOma.Length < Ea3DescriptorSize ||
            rawOma[0] != (byte)'E' || rawOma[1] != (byte)'A' || rawOma[2] != (byte)'3')
        {
            throw new InvalidDataException("atracdenc did not emit expected EA3 header at offset 0.");
        }

        uint codecParams24 = (uint)((rawOma[33] << 16) | (rawOma[34] << 8) | rawOma[35]);
        int frameBytes = (int)(codecParams24 & 0x3FF) * 8;
        int payloadLen = rawOma.Length - Ea3DescriptorSize;
        uint frameCount = frameBytes > 0 ? (uint)(payloadLen / frameBytes) : 0u;
        uint durationMs = request.Tags.DurationMs > 0
            ? request.Tags.DurationMs
            : (uint)Math.Round(frameCount * 1024.0 * 1000.0 / 44100.0, MidpointRounding.AwayFromZero);

        int ea3Size = request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001
            ? Math.Max(2048, request.Ea3BlockSize)
            : request.Ea3BlockSize;

        var tagsWithDuration = request.Tags with { DurationMs = durationMs };
        var wrapped = request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 &&
                      request.RecycledMagicGateSlot is not null
            ? crypto.WrapAtracIntoSignedSlot(
                rawOma.AsSpan(Ea3DescriptorSize),
                AudioCodecId.Atrac3,
                codecParams24,
                durationMs,
                frameCount,
                request.RecycledMagicGateSlot,
                tagsWithDuration,
                request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty,
                ea3Size)
            : crypto.WrapAtracToOma(
                rawOma.AsSpan(Ea3DescriptorSize),
                AudioCodecId.Atrac3,
                codecParams24,
                durationMs,
                frameCount,
                request.TrackId,
                request.HardwareDeviceKey,
                request.EncryptionMode,
                tagsWithDuration,
                ea3Size,
                ekb00010021Bytes: request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty);

        return (wrapped, frameCount);
    }

    private async Task<(MagicGate0x0001WrapResult Wrapped, uint FrameCount)> BuildAtrac3PlusOmaAsync(
        TranscodeRequest request,
        IOpenMgCryptoService crypto,
        string tempDir,
        CancellationToken ct)
    {
        int ea3Size = request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001
            ? Math.Max(2048, request.Ea3BlockSize)
            : request.Ea3BlockSize;

        // COD-30: Direct lossless re-wrap for pre-encoded ATRAC3plus .oma/.aa3/.omg files (e.g. 192 kbps 01 00 28 8B).
        string srcExt = Path.GetExtension(request.SourceFilePath).ToLowerInvariant();
        if (srcExt is ".oma" or ".aa3" or ".omg")
        {
            byte[] srcBytes = await File.ReadAllBytesAsync(request.SourceFilePath, ct).ConfigureAwait(false);
            var srcHdr = crypto.ParseHeader(srcBytes);
            if (srcHdr.CodecId == AudioCodecId.Atrac3Plus)
            {
                int srcFrameBytes = ((int)(srcHdr.CodecParams24 & 0x3FF) + 1) * 8;
                int srcKbps = (int)Math.Round(srcFrameBytes * 172.265625 / 1000.0);
                if (request.TargetBitrateKbps <= 0 || Math.Abs(srcKbps - request.TargetBitrateKbps) <= 12)
                {
                    int srcTrackId = TryParseTrackIdFromPath(request.SourceFilePath) ?? 1;
                    byte[]? ekb = request.DeviceEkb00010021Bytes ?? TryLoadAncestorEkb00010021(request.SourceFilePath);
                    byte[] rawFrames = srcHdr.EncryptionId == OmaEncryptionMode.Unprotected_0xFFFF
                        ? srcBytes.AsSpan(srcHdr.AudioPayloadOffset).ToArray()
                        : crypto.DecryptOmaPayload(srcBytes, srcTrackId, request.HardwareDeviceKey, ekb);

                    uint passDurMs = request.Tags.DurationMs > 0 ? request.Tags.DurationMs : srcHdr.DurationMs;
                    uint passFrames = srcHdr.FrameCount > 0
                        ? srcHdr.FrameCount
                        : (srcFrameBytes > 0 ? (uint)(rawFrames.Length / srcFrameBytes) : 0u);
                    var passTags = request.Tags with { DurationMs = passDurMs };

                    var passWrapped = request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 &&
                                      request.RecycledMagicGateSlot is not null
                        ? crypto.WrapAtracIntoSignedSlot(
                            rawFrames,
                            AudioCodecId.Atrac3Plus,
                            srcHdr.CodecParams24,
                            passDurMs,
                            passFrames,
                            request.RecycledMagicGateSlot,
                            passTags,
                            request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty,
                            ea3Size)
                        : crypto.WrapAtracToOma(
                            rawFrames,
                            AudioCodecId.Atrac3Plus,
                            srcHdr.CodecParams24,
                            passDurMs,
                            passFrames,
                            request.TrackId,
                            request.HardwareDeviceKey,
                            request.EncryptionMode,
                            passTags,
                            ea3Size,
                            ekb00010021Bytes: request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty);

                    return (passWrapped, passFrames);
                }
            }
        }

        string normalizedWav = Path.Combine(tempDir, "pcm44k_atp.wav");
        if (srcExt is ".oma" or ".aa3" or ".omg")
        {
            bool extracted = await ExtractFromOmaAsync(request.SourceFilePath, normalizedWav, request.HardwareDeviceKey, ct).ConfigureAwait(false);
            if (!extracted)
            {
                AudioResamplerPipeline.DecodeTo44100StereoWav(request.SourceFilePath, normalizedWav, ct);
            }
        }
        else
        {
            AudioResamplerPipeline.DecodeTo44100StereoWav(request.SourceFilePath, normalizedWav, ct);
        }

        var encoded = Atrac3PlusEncoder.EncodeWavFile(normalizedWav, request.TargetBitrateKbps, ct);
        uint durationMs = request.Tags.DurationMs > 0 ? request.Tags.DurationMs : encoded.DurationMs;

        var tagsWithDuration = request.Tags with { DurationMs = durationMs };
        var wrapped = request.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 &&
                      request.RecycledMagicGateSlot is not null
            ? crypto.WrapAtracIntoSignedSlot(
                encoded.RawFrames,
                AudioCodecId.Atrac3Plus,
                encoded.CodecParams24,
                durationMs,
                encoded.FrameCount,
                request.RecycledMagicGateSlot,
                tagsWithDuration,
                request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty,
                ea3Size)
            : crypto.WrapAtracToOma(
                encoded.RawFrames,
                AudioCodecId.Atrac3Plus,
                encoded.CodecParams24,
                durationMs,
                encoded.FrameCount,
                request.TrackId,
                request.HardwareDeviceKey,
                request.EncryptionMode,
                tagsWithDuration,
                ea3Size,
                ekb00010021Bytes: request.DeviceEkb00010021Bytes ?? ReadOnlySpan<byte>.Empty);

        return (wrapped, encoded.FrameCount);
    }

    private static int NormalizeMp3Bitrate(int requestedKbps) =>
        requestedKbps is >= 32 and <= 320 ? requestedKbps : 192;

    private static int? TryParseTrackIdFromPath(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (stem.Length == 8 && stem[0] == '1' &&
            int.TryParse(stem.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int id))
        {
            return id;
        }

        return null;
    }

    private static async Task WriteStagedFileAtomicAsync(string destinationPath, byte[] data, CancellationToken ct)
    {
        string fullDest = Path.GetFullPath(destinationPath);
        string? dir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempFile = fullDest + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempFile, fullDest, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            throw;
        }
    }

    private static byte[]? TryLoadAncestorEkb00010021(string sourceOmaPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceOmaPath));
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(dir); depth++)
            {
                string direct = Path.Combine(dir, "00010021.DAT");
                if (File.Exists(direct))
                {
                    return File.ReadAllBytes(direct);
                }

                string underOmg = Path.Combine(dir, "OMGAUDIO", "00010021.DAT");
                if (File.Exists(underOmg))
                {
                    return File.ReadAllBytes(underOmg);
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // Best-effort EKB lookup for legacy SonicStage-locked rescue.
        }

        return null;
    }
}
