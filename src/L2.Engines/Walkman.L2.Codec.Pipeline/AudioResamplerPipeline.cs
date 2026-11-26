using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NVorbis;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Decodes heterogeneous audio sources (FLAC, WAV, MP3, AAC/M4A, WMA, OGG Vorbis, Opus)
/// and resamples strictly to Sony Walkman hardware PCM format: 44,100 Hz / 16-bit Stereo.
/// </summary>
internal static class AudioResamplerPipeline
{
    public const int TargetSampleRateHz = 44100;
    public const int TargetChannels = 2;

    public static void TranscodeToMp3File(string sourcePath, string destMp3Path, int bitrateKbps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var decoded = OpenDecodedSampleStream(sourcePath);
        ISampleProvider stereo44k = NormalizeTo44100Stereo(decoded.Provider);
        var pcm16 = new SampleToWaveProvider16(stereo44k);

        LameNativeEncoder.EncodeWaveProviderToMp3(pcm16, destMp3Path, bitrateKbps, ct);
    }

    public static void DecodeTo44100StereoWav(string sourcePath, string destWavPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var decoded = OpenDecodedSampleStream(sourcePath);
        ISampleProvider stereo44k = NormalizeTo44100Stereo(decoded.Provider);
        var pcm16 = new SampleToWaveProvider16(stereo44k);

        using var writer = new WaveFileWriter(destWavPath, pcm16.WaveFormat);
        PumpWaveProviderToStream(pcm16, writer, ct);
    }

    public static void ConvertWavToMp3File(string sourceWavPath, string destMp3Path, int bitrateKbps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(sourceWavPath);
        ISampleProvider stereo44k = NormalizeTo44100Stereo(reader.ToSampleProvider());
        var pcm16 = new SampleToWaveProvider16(stereo44k);

        LameNativeEncoder.EncodeWaveProviderToMp3(pcm16, destMp3Path, bitrateKbps, ct);
    }

    private static ISampleProvider NormalizeTo44100Stereo(ISampleProvider input)
    {
        ISampleProvider current = input;

        if (current.WaveFormat.Channels == 1)
        {
            current = new MonoToStereoSampleProvider(current);
        }
        else if (current.WaveFormat.Channels > 2)
        {
            var multiplexer = new MultiplexingSampleProvider([current], 2);
            multiplexer.ConnectInputToOutput(0, 0);
            multiplexer.ConnectInputToOutput(1, 1);
            current = multiplexer;
        }

        if (current.WaveFormat.SampleRate != TargetSampleRateHz)
        {
            current = new WdlResamplingSampleProvider(current, TargetSampleRateHz);
        }

        return current;
    }

    private static void PumpWaveProviderToStream(IWaveProvider source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    private static DecodedSampleStream OpenDecodedSampleStream(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        switch (ext)
        {
            case ".ogg":
            {
                var vorbis = new VorbisReader(sourcePath);
                try
                {
                    return new DecodedSampleStream(new VorbisSampleProvider(vorbis), vorbis);
                }
                catch
                {
                    vorbis.Dispose();
                    throw;
                }
            }
            case ".opus":
            {
                var fs = File.OpenRead(sourcePath);
                try
                {
                    var opusProvider = new OpusOggSampleProvider(fs);
                    return new DecodedSampleStream(opusProvider, opusProvider);
                }
                catch
                {
                    fs.Dispose();
                    throw;
                }
            }
            case ".wav":
            {
                var wav = new WaveFileReader(sourcePath);
                try
                {
                    return new DecodedSampleStream(wav.ToSampleProvider(), wav);
                }
                catch
                {
                    wav.Dispose();
                    throw;
                }
            }
            case ".aiff":
            case ".aif":
            {
                var aiff = new AiffFileReader(sourcePath);
                try
                {
                    return new DecodedSampleStream(aiff.ToSampleProvider(), aiff);
                }
                catch
                {
                    aiff.Dispose();
                    throw;
                }
            }
            case ".oma":
            case ".aa3":
            case ".omg":
            case ".at3":
            case ".at9":
            case ".aea":
            case ".wv":
            case ".ape":
            {
                return OpenViaVgmstreamWav(sourcePath);
            }
            default:
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var mf = new MediaFoundationReader(sourcePath);
                        return new DecodedSampleStream(mf.ToSampleProvider(), mf);
                    }
                    catch when (ext == ".flac")
                    {
                        var flac = new FlacWaveStream(sourcePath);
                        return new DecodedSampleStream(flac.ToSampleProvider(), flac);
                    }
                }

                if (ext == ".flac")
                {
                    var flac = new FlacWaveStream(sourcePath);
                    return new DecodedSampleStream(flac.ToSampleProvider(), flac);
                }

                var afr = new AudioFileReader(sourcePath);
                return new DecodedSampleStream(afr, afr);
            }
        }
    }

    private sealed class DecodedSampleStream : IDisposable
    {
        private readonly IDisposable _resource;

        public DecodedSampleStream(ISampleProvider provider, IDisposable resource)
        {
            Provider = provider;
            _resource = resource;
        }

        public ISampleProvider Provider { get; }

        public void Dispose() => _resource.Dispose();
    }

    private static DecodedSampleStream OpenViaVgmstreamWav(string sourcePath)
    {
        string tempWav = Path.Combine(Path.GetTempPath(), $"apollo_vgm_{Guid.NewGuid():N}.wav");
        try
        {
            if (Path.GetExtension(sourcePath).Equals(".aea", StringComparison.OrdinalIgnoreCase))
            {
                string atracdencExe = ExternalCodecToolRunner.ResolveToolExecutable("atracdenc", "atracdenc.exe");
                ExternalCodecToolRunner.RunProcessAsync(atracdencExe, ["-d", "-i", sourcePath, "-o", tempWav], CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                string vgmCli = ExternalCodecToolRunner.ResolveToolExecutable("vgmstream", "vgmstream-cli.exe");
                ExternalCodecToolRunner.RunProcessAsync(vgmCli, ["-o", tempWav, sourcePath], CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            if (!File.Exists(tempWav))
            {
                throw new InvalidDataException($"External decoder failed to decode '{Path.GetFileName(sourcePath)}'.");
            }

            var reader = new WaveFileReader(tempWav);
            var cleanup = new TempFileDisposable(reader, tempWav);
            return new DecodedSampleStream(reader.ToSampleProvider(), cleanup);
        }
        catch
        {
            if (File.Exists(tempWav))
            {
                try { File.Delete(tempWav); } catch { }
            }

            // Fallback to Windows Media Foundation if a host codec is registered (e.g. WavPack/APE DirectShow/MF).
            if (OperatingSystem.IsWindows())
            {
                var mf = new MediaFoundationReader(sourcePath);
                return new DecodedSampleStream(mf.ToSampleProvider(), mf);
            }

            throw;
        }
    }

    private sealed class TempFileDisposable : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly string _tempPath;

        public TempFileDisposable(IDisposable inner, string tempPath)
        {
            _inner = inner;
            _tempPath = tempPath;
        }

        public void Dispose()
        {
            try { _inner.Dispose(); } catch { }
            if (File.Exists(_tempPath))
            {
                try { File.Delete(_tempPath); } catch { }
            }
        }
    }

    private sealed class VorbisSampleProvider : ISampleProvider
    {
        private readonly VorbisReader _reader;

        public VorbisSampleProvider(VorbisReader reader)
        {
            _reader = reader;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) =>
            _reader.ReadSamples(buffer, offset, count);
    }

    private sealed class OpusOggSampleProvider : ISampleProvider, IDisposable
    {
        private readonly Stream _stream;
        private readonly IOpusDecoder _decoder;
        private readonly OpusOggReadStream _oggStream;
        private short[] _pendingPacket = Array.Empty<short>();
        private int _pendingOffset;

        public OpusOggSampleProvider(Stream stream)
        {
            _stream = stream;
            _decoder = OpusCodecFactory.CreateDecoder(48000, 2);
            _oggStream = new OpusOggReadStream(_decoder, stream);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_pendingOffset >= _pendingPacket.Length)
                {
                    if (!_oggStream.HasNextPacket)
                    {
                        break;
                    }

                    short[]? next = _oggStream.DecodeNextPacket();
                    if (next is null || next.Length == 0)
                    {
                        continue;
                    }

                    _pendingPacket = next;
                    _pendingOffset = 0;
                }

                int available = _pendingPacket.Length - _pendingOffset;
                int toCopy = Math.Min(available, count - written);
                for (int i = 0; i < toCopy; i++)
                {
                    buffer[offset + written + i] = _pendingPacket[_pendingOffset + i] / 32768f;
                }

                _pendingOffset += toCopy;
                written += toCopy;
            }

            return written;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
