using System.Buffers;

namespace Walkman.L2.Codec.Pipeline.Atrac3Plus;

/// <summary>
/// Clean-room ATRAC3plus DSP stage:
///   1. 16-band PQMF analysis filterbank (384-tap polyphase FIR + 16-pt scaled DCT-IV)
///   2. Per-subband transient detector driving Sine/Steep MDCT window switching
///   3. 256 -> 128 MDCT (64-pt radix-2 complex FFT) with odd-subband spectral reversal
/// </summary>
internal sealed class Atrac3PlusChannelDsp
{
    public const int FrameSamples = 2048;
    public const int NumSubbands = 16;
    public const int SubbandSamples = 128;
    private const int PqmfTaps = 384;
    private const int PqmfOverlap = PqmfTaps - NumSubbands; // 368
    private const double SubbandNormFactor = 32768.0 / 1.1220184543019633; // 32768 / 10^0.05 = 29204.58875

    private static readonly double[] Fir = BuildPqmfAnalysisFir();
    private static readonly double[,] Dct4 = BuildDct4Matrix();
    private static readonly double[] SineWin128 = BuildSineWindow(128);
    private static readonly double[] SineWin64 = BuildSineWindow(64);
    private static readonly double[] MdctCos = BuildMdctTwiddleCos();
    private static readonly double[] MdctSin = BuildMdctTwiddleSin();

    private readonly double[] _pcmHistory = new double[PqmfOverlap + FrameSamples];
    private readonly double[,] _mdctPrevLeft = new double[NumSubbands, SubbandSamples];
    private readonly double[] _prevBlockDb = new double[NumSubbands];
    private readonly int[] _prevGainNumPoints = new int[NumSubbands];
    private readonly int[,] _prevGainLevCode = new int[NumSubbands, 7];
    private readonly int[,] _prevGainLocCode = new int[NumSubbands, 7];

    // Preallocated scratch buffers (per-channel single-threaded encode)
    private readonly double[] _subbands = new double[NumSubbands * SubbandSamples];
    private readonly double[] _mdctWork = new double[256];
    private readonly double[] _fftRe = new double[64];
    private readonly double[] _fftIm = new double[64];
    private readonly double[] _yAcc = new double[32];
    private readonly double[] _yy = new double[16];

    public Atrac3PlusChannelDsp()
    {
        Array.Fill(_prevBlockDb, -120.0);
    }

    /// <summary>
    /// Transforms 2048 normalized PCM samples [-1, +1] into 2048 MDCT spectral coefficients (16 x 128)
    /// and computes per-subband Sine (false) vs Steep (true) transient window flags.
    /// </summary>
    public void AnalyzeFrame(
        ReadOnlySpan<double> pcm2048,
        Span<double> mdctSpectrum2048,
        Span<bool> wndSteep16,
        int[,]? gainNumPoints = null,
        int[,,]? gainLevCode = null,
        int[,,]? gainLocCode = null,
        int ch = 0)
    {
        if (pcm2048.Length != FrameSamples)
        {
            throw new ArgumentException($"Expected {FrameSamples} PCM samples.", nameof(pcm2048));
        }

        // 1. Copy input into overlap history and run 16-band PQMF analysis (128 time slots)
        pcm2048.CopyTo(_pcmHistory.AsSpan(PqmfOverlap));

        for (int s = 0; s < SubbandSamples; s++)
        {
            int xOff = s * NumSubbands;
            for (int i = 0; i < 32; i++)
            {
                double sum = 0.0;
                int firOff = i * 12;
                for (int j = 0; j < 12; j++)
                {
                    sum += Fir[firOff + j] * _pcmHistory[xOff + j * 32 + i];
                }

                _yAcc[i] = sum;
            }

            for (int i = 0; i < 8; i++)
            {
                _yy[i] = _yAcc[i + 8] + _yAcc[7 - i];
                _yy[i + 8] = _yAcc[i + 16] + _yAcc[31 - i];
            }

            for (int b = 0; b < NumSubbands; b++)
            {
                double acc = 0.0;
                for (int c = 0; c < NumSubbands; c++)
                {
                    acc += Dct4[b, c] * _yy[c];
                }

                _subbands[b * SubbandSamples + s] = 65536.0 * acc;
            }
        }

        // Slide PQMF history
        Array.Copy(_pcmHistory, FrameSamples, _pcmHistory, 0, PqmfOverlap);

        // 2. Per-subband transient detection, Tier-2 gain pre-scaling & 256->128 windowed MDCT
        for (int b = 0; b < NumSubbands; b++)
        {
            int sbBase = b * SubbandSamples;
            Span<double> sbSpan = _subbands.AsSpan(sbBase, SubbandSamples);
            bool isTransient = DetectTransient(b, sbSpan);
            wndSteep16[b] = isTransient;

            // Emit previous frame's gain points (aligned to _mdctPrevLeft[b, 0..127])
            if (gainNumPoints is not null && gainLevCode is not null && gainLocCode is not null)
            {
                int prevN = _prevGainNumPoints[b];
                gainNumPoints[ch, b] = prevN;
                for (int p = 0; p < prevN; p++)
                {
                    gainLevCode[ch, b, p] = _prevGainLevCode[b, p];
                    gainLocCode[ch, b, p] = _prevGainLocCode[b, p];
                }
            }

            // Detect and apply Tier-2 gain pre-scaling on current subband block for next frame's left overlap
            DetectAndPreScaleGain(b, sbSpan);

            // Load left overlap half saved from previous frame
            for (int i = 0; i < SubbandSamples; i++)
            {
                _mdctWork[i] = _mdctPrevLeft[b, i];
            }

            if (!isTransient)
            {
                for (int i = 0; i < SubbandSamples; i++)
                {
                    double r = _subbands[sbBase + i] / SubbandNormFactor;
                    _mdctWork[128 + i] = SineWin128[127 - i] * r;
                    _mdctPrevLeft[b, i] = SineWin128[i] * r;
                }
            }
            else
            {
                // Steep window right overlap [128..255]: [0..31]*2.0, [32..95]*W64_rev, [96..127]=0.0
                for (int i = 0; i < 32; i++)
                {
                    double r = _subbands[sbBase + i] / SubbandNormFactor;
                    _mdctWork[128 + i] = 2.0 * r;
                }
                for (int i = 0; i < 64; i++)
                {
                    double r = _subbands[sbBase + 32 + i] / SubbandNormFactor;
                    _mdctWork[160 + i] = SineWin64[63 - i] * r;
                }
                Array.Clear(_mdctWork, 224, 32);

                // Steep window next-frame left half [0..127]: [0..31]=0.0, [32..95]*W64, [96..127]*2.0
                for (int i = 0; i < 32; i++)
                {
                    _mdctPrevLeft[b, i] = 0.0;
                }
                for (int i = 0; i < 64; i++)
                {
                    double r = _subbands[sbBase + 32 + i] / SubbandNormFactor;
                    _mdctPrevLeft[b, 32 + i] = SineWin64[i] * r;
                }
                for (int i = 0; i < 32; i++)
                {
                    double r = _subbands[sbBase + 96 + i] / SubbandNormFactor;
                    _mdctPrevLeft[b, 96 + i] = 2.0 * r;
                }
            }

            Span<double> sbSpec = mdctSpectrum2048.Slice(sbBase, SubbandSamples);
            RunMdct256(_mdctWork, sbSpec);

            // Mandatory odd-subband spectral reversal compensating PQMF folding
            if ((b & 1) != 0)
            {
                sbSpec.Reverse();
            }
        }
    }

    private bool DetectTransient(int subband, ReadOnlySpan<double> samples128)
    {
        double prevDb = _prevBlockDb[subband];
        bool transient = false;

        // 8 short blocks of 16 samples
        for (int blk = 0; blk < 8; blk++)
        {
            double energy = 0.0;
            int start = blk * 16;
            for (int i = 0; i < 16; i++)
            {
                double v = samples128[start + i];
                energy += v * v;
            }

            double rms = Math.Sqrt(energy / 16.0);
            double db = 20.0 * Math.Log10(rms + 1e-12);
            if (db - prevDb > 16.0 || prevDb - db > 20.0)
            {
                transient = true;
            }

            prevDb = db;
        }

        _prevBlockDb[subband] = prevDb;
        return transient;
    }

    private void DetectAndPreScaleGain(int subband, Span<double> samples128)
    {
        _prevGainNumPoints[subband] = 0;

        Span<double> slotRms = stackalloc double[32];
        for (int s = 0; s < 32; s++)
        {
            double sumSq = 0.0;
            int baseIdx = s * 4;
            for (int i = 0; i < 4; i++)
            {
                double v = samples128[baseIdx + i];
                sumSq += v * v;
            }
            slotRms[s] = Math.Sqrt(sumSq * 0.25);
        }

        double preMaxRms = slotRms[0];
        int attackSlot = -1;
        double attackJumpDb = 0.0;

        for (int s = 1; s <= 30; s++)
        {
            if (slotRms[s] > 1e-4)
            {
                double jumpDb = 20.0 * Math.Log10(slotRms[s] / (preMaxRms + 1e-12));
                if (jumpDb > 18.0)
                {
                    attackSlot = s;
                    attackJumpDb = jumpDb;
                    break;
                }
            }
            if (slotRms[s] > preMaxRms)
            {
                preMaxRms = slotRms[s];
            }
        }

        if (attackSlot < 1)
        {
            return;
        }

        int lev0 = Math.Clamp(6 + (int)Math.Floor((attackJumpDb - 12.0) / 6.0205999), 7, 10);
        double boost = Math.Pow(2.0, lev0 - 6);

        int rampStart = attackSlot * 4;
        for (int i = 0; i < rampStart; i++)
        {
            samples128[i] *= boost;
        }

        double stepFactor = Math.Pow(1.0 / boost, 0.25);
        double g = boost;
        for (int i = 0; i < 4; i++)
        {
            samples128[rampStart + i] *= g;
            g *= stepFactor;
        }

        _prevGainNumPoints[subband] = 1;
        _prevGainLevCode[subband, 0] = lev0;
        _prevGainLocCode[subband, 0] = attackSlot;
    }

    private void RunMdct256(ReadOnlySpan<double> u256, Span<double> out128)
    {
        const int n4 = 64;
        const int n2 = 128;
        const int n34 = 192;
        const int n54 = 320;

        for (int i = 0; i < n4; i++)
        {
            int n = i * 2;
            double r0, i0;
            if (n < n4)
            {
                r0 = u256[n34 - 1 - n] + u256[n34 + n];
                i0 = u256[n4 + n] - u256[n4 - 1 - n];
            }
            else
            {
                r0 = u256[n34 - 1 - n] - u256[n - n4];
                i0 = u256[n4 + n] + u256[n54 - 1 - n];
            }

            double c = MdctCos[i];
            double s = MdctSin[i];
            _fftRe[i] = r0 * c + i0 * s;
            _fftIm[i] = i0 * c - r0 * s;
        }

        Fft64(_fftRe, _fftIm);

        for (int i = 0; i < n4; i++)
        {
            int n = i * 2;
            double r0 = _fftRe[i];
            double i0 = _fftIm[i];
            double c = MdctCos[i];
            double s = MdctSin[i];
            out128[n] = -r0 * c - i0 * s;
            out128[n2 - 1 - n] = -r0 * s + i0 * c;
        }
    }

    private static void Fft64(Span<double> re, Span<double> im)
    {
        // Bit-reversal permutation for N = 64 (6 bits)
        for (int i = 0; i < 64; i++)
        {
            int j = Reverse6Bits(i);
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Cooley-Tukey radix-2 forward DFT: exp(-2*pi*i*k*n / N)
        for (int len = 2; len <= 64; len <<= 1)
        {
            int half = len >> 1;
            double angleStep = -2.0 * Math.PI / len;
            for (int start = 0; start < 64; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double angle = angleStep * k;
                    double wr = Math.Cos(angle);
                    double wi = Math.Sin(angle);

                    int even = start + k;
                    int odd = even + half;
                    double tr = re[odd] * wr - im[odd] * wi;
                    double ti = re[odd] * wi + im[odd] * wr;

                    re[odd] = re[even] - tr;
                    im[odd] = im[even] - ti;
                    re[even] += tr;
                    im[even] += ti;
                }
            }
        }
    }

    private static int Reverse6Bits(int x)
    {
        int r = 0;
        for (int b = 0; b < 6; b++)
        {
            r = (r << 1) | (x & 1);
            x >>= 1;
        }

        return r;
    }

    private static double[] BuildPqmfAnalysisFir()
    {
        var fir = new double[PqmfTaps];
        for (int sb = 0; sb < NumSubbands; sb++)
        {
            for (int j = 0; j < 12; j++)
            {
                if (sb < 8)
                {
                    fir[sb * 12 + j] = Atrac3PlusTables.PqmfProtoCoeffs1[j, sb];
                    fir[192 + sb * 12 + j] = Atrac3PlusTables.PqmfProtoCoeffs2[j, sb];
                }
                else
                {
                    fir[96 + (sb - 8) * 12 + j] = Atrac3PlusTables.PqmfProtoCoeffs1[j, sb];
                    fir[288 + (sb - 8) * 12 + j] = Atrac3PlusTables.PqmfProtoCoeffs2[j, sb];
                }
            }
        }

        return fir;
    }

    private static double[,] BuildDct4Matrix()
    {
        var m = new double[NumSubbands, NumSubbands];
        for (int b = 0; b < NumSubbands; b++)
        {
            for (int c = 0; c < NumSubbands; c++)
            {
                m[b, c] = Math.Cos(Math.PI / NumSubbands * (c + 0.5) * (b + 0.5));
            }
        }

        return m;
    }

    private static double[] BuildSineWindow(int halfLen)
    {
        var win = new double[halfLen];
        double denom = 2.0 * halfLen;
        for (int i = 0; i < halfLen; i++)
        {
            win[i] = 2.0 * Math.Sin(Math.PI * (i + 0.5) / denom);
        }

        return win;
    }

    private static double[] BuildMdctTwiddleCos()
    {
        const double sc = 1.0 / 16.0; // sqrt(1 / 256)
        const double alpha = 2.0 * Math.PI / 2048.0;
        const double omega = 2.0 * Math.PI / 256.0;
        var cos = new double[64];
        for (int i = 0; i < 64; i++)
        {
            cos[i] = sc * Math.Cos(omega * i + alpha);
        }

        return cos;
    }

    private static double[] BuildMdctTwiddleSin()
    {
        const double sc = 1.0 / 16.0;
        const double alpha = 2.0 * Math.PI / 2048.0;
        const double omega = 2.0 * Math.PI / 256.0;
        var sin = new double[64];
        for (int i = 0; i < 64; i++)
        {
            sin[i] = sc * Math.Sin(omega * i + alpha);
        }

        return sin;
    }
}
