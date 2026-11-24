using NAudio.Wave;

namespace Walkman.L2.Codec.Pipeline.Atrac3Plus;

internal readonly record struct Atrac3PlusEncodeOutput(
    byte[] RawFrames,
    uint FrameCount,
    uint CodecParams24,
    uint DurationMs,
    int BitrateKbps);

/// <summary>
/// Clean-room native C# ATRAC3plus stereo encoder (44.1 kHz, all 9 Walkman bitrates: 48..352 kbps).
/// </summary>
internal static class Atrac3PlusEncoder
{
    private const int SampleRateHz = 44100;
    private const int FrameSamplePairs = 2048;

    public static Atrac3PlusEncodeOutput EncodeWavFile(
        string wav44k16StereoPath,
        int targetBitrateKbps,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wav44k16StereoPath);
        using var reader = new WaveFileReader(wav44k16StereoPath);
        if (reader.WaveFormat.SampleRate != SampleRateHz ||
            reader.WaveFormat.Channels != 2 ||
            reader.WaveFormat.BitsPerSample != 16)
        {
            throw new InvalidDataException(
                $"ATRAC3plus encoder requires 44100 Hz / 16-bit / 2-ch PCM WAV (got {reader.WaveFormat}).");
        }

        var preset = Atrac3PlusTables.ResolvePreset(targetBitrateKbps);
        int inputPairs = (int)(reader.Length / 4);
        if (inputPairs <= 0)
        {
            throw new InvalidDataException("ATRAC3plus encoder requires non-empty 44100 Hz / 16-bit / 2-ch PCM WAV.");
        }

        uint durationMs = (uint)Math.Round(inputPairs * 1000.0 / SampleRateHz, MidpointRounding.AwayFromZero);
        int totalFrames = ((inputPairs + FrameSamplePairs - 1) / FrameSamplePairs) + 1;
        byte[] encodedFrames = new byte[totalFrames * preset.FrameBytes];

        var dspL = new Atrac3PlusChannelDsp();
        var dspR = new Atrac3PlusChannelDsp();
        var quantizer = new Atrac3PlusQuantizer();

        double[] bufL = new double[FrameSamplePairs];
        double[] bufR = new double[FrameSamplePairs];
        double[] specL = new double[FrameSamplePairs];
        double[] specR = new double[FrameSamplePairs];
        bool[] wndL = new bool[16];
        bool[] wndR = new bool[16];
        int[,] gainNumPoints = new int[2, 16];
        int[,,] gainLevCode = new int[2, 16, 7];
        int[,,] gainLocCode = new int[2, 16, 7];

        const double normScale = 1.0 / 32768.0;
        byte[] framePcmBytes = new byte[FrameSamplePairs * 4];

        for (int f = 0; f < totalFrames; f++)
        {
            ct.ThrowIfCancellationRequested();

            int bytesRead = 0;
            while (bytesRead < framePcmBytes.Length)
            {
                int n = reader.Read(framePcmBytes, bytesRead, framePcmBytes.Length - bytesRead);
                if (n <= 0) break;
                bytesRead += n;
            }

            int pairsRead = bytesRead / 4;
            for (int i = 0; i < FrameSamplePairs; i++)
            {
                if (i < pairsRead)
                {
                    short l = (short)(framePcmBytes[i * 4] | (framePcmBytes[i * 4 + 1] << 8));
                    short r = (short)(framePcmBytes[i * 4 + 2] | (framePcmBytes[i * 4 + 3] << 8));
                    bufL[i] = l * normScale;
                    bufR[i] = r * normScale;
                }
                else
                {
                    bufL[i] = 0.0;
                    bufR[i] = 0.0;
                }
            }

            dspL.AnalyzeFrame(bufL, specL, wndL, gainNumPoints, gainLevCode, gainLocCode, ch: 0);
            dspR.AnalyzeFrame(bufR, specR, wndR, gainNumPoints, gainLevCode, gainLocCode, ch: 1);

            var plan = quantizer.AllocateAndQuantize(preset, specL, specR, wndL, wndR, gainNumPoints, gainLevCode, gainLocCode);
            byte[] packet = Atrac3PlusBitstreamWriter.EncodeStereoFrame(plan, preset.FrameBytes);
            packet.CopyTo(encodedFrames.AsSpan(f * preset.FrameBytes, preset.FrameBytes));
        }

        return new Atrac3PlusEncodeOutput(
            RawFrames: encodedFrames,
            FrameCount: (uint)totalFrames,
            CodecParams24: preset.CodecParams24,
            DurationMs: durationMs,
            BitrateKbps: preset.BitrateKbps);
    }

    public static Atrac3PlusEncodeOutput EncodePcm16Stereo(
        ReadOnlySpan<short> interleavedPcm16,
        int targetBitrateKbps,
        CancellationToken ct = default)
    {
        if (interleavedPcm16.Length == 0 || (interleavedPcm16.Length & 1) != 0)
        {
            throw new ArgumentException("Interleaved stereo PCM buffer must have positive even length.", nameof(interleavedPcm16));
        }

        var preset = Atrac3PlusTables.ResolvePreset(targetBitrateKbps);
        int inputPairs = interleavedPcm16.Length / 2;
        uint durationMs = (uint)Math.Round(inputPairs * 1000.0 / SampleRateHz, MidpointRounding.AwayFromZero);

        // Include +1 overlap flush frame so MDCT right-half tail resolves cleanly
        int totalFrames = ((inputPairs + FrameSamplePairs - 1) / FrameSamplePairs) + 1;
        byte[] encodedFrames = new byte[totalFrames * preset.FrameBytes];

        var dspL = new Atrac3PlusChannelDsp();
        var dspR = new Atrac3PlusChannelDsp();
        var quantizer = new Atrac3PlusQuantizer();

        double[] bufL = new double[FrameSamplePairs];
        double[] bufR = new double[FrameSamplePairs];
        double[] specL = new double[FrameSamplePairs];
        double[] specR = new double[FrameSamplePairs];
        bool[] wndL = new bool[16];
        bool[] wndR = new bool[16];
        int[,] gainNumPoints = new int[2, 16];
        int[,,] gainLevCode = new int[2, 16, 7];
        int[,,] gainLocCode = new int[2, 16, 7];

        const double normScale = 1.0 / 32768.0;

        for (int f = 0; f < totalFrames; f++)
        {
            ct.ThrowIfCancellationRequested();

            int pairStart = f * FrameSamplePairs;
            for (int i = 0; i < FrameSamplePairs; i++)
            {
                int idx = pairStart + i;
                if (idx < inputPairs)
                {
                    bufL[i] = interleavedPcm16[idx * 2] * normScale;
                    bufR[i] = interleavedPcm16[idx * 2 + 1] * normScale;
                }
                else
                {
                    bufL[i] = 0.0;
                    bufR[i] = 0.0;
                }
            }

            dspL.AnalyzeFrame(bufL, specL, wndL, gainNumPoints, gainLevCode, gainLocCode, ch: 0);
            dspR.AnalyzeFrame(bufR, specR, wndR, gainNumPoints, gainLevCode, gainLocCode, ch: 1);

            var plan = quantizer.AllocateAndQuantize(preset, specL, specR, wndL, wndR, gainNumPoints, gainLevCode, gainLocCode);
            byte[] packet = Atrac3PlusBitstreamWriter.EncodeStereoFrame(plan, preset.FrameBytes);
            packet.CopyTo(encodedFrames.AsSpan(f * preset.FrameBytes, preset.FrameBytes));
        }

        return new Atrac3PlusEncodeOutput(
            RawFrames: encodedFrames,
            FrameCount: (uint)totalFrames,
            CodecParams24: preset.CodecParams24,
            DurationMs: durationMs,
            BitrateKbps: preset.BitrateKbps);
    }
}
