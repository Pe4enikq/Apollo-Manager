namespace Walkman.L2.Codec.Pipeline.Atrac3Plus;

internal sealed class BitWriter
{
    private readonly byte[] _buffer;
    private int _bitPos;

    public BitWriter(int maxBytes)
    {
        _buffer = new byte[maxBytes];
    }

    public int BitsWritten => _bitPos;
    public int CapacityBits => _buffer.Length * 8;

    public void WriteBits(uint value, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (_bitPos + count > CapacityBits)
        {
            throw new InvalidOperationException(
                $"ATRAC3plus frame bit overflow: attempted {_bitPos + count} bits > {CapacityBits} capacity.");
        }

        for (int i = count - 1; i >= 0; i--)
        {
            if (((value >> i) & 1u) != 0)
            {
                _buffer[_bitPos >> 3] |= (byte)(1 << (7 - (_bitPos & 7)));
            }

            _bitPos++;
        }
    }

    public void WriteBit(bool bit) => WriteBits(bit ? 1u : 0u, 1);

    public byte[] FinalizeFrame(byte padByte = 0x01)
    {
        int bytePos = (_bitPos + 7) >> 3;
        while (bytePos < _buffer.Length)
        {
            _buffer[bytePos++] = padByte;
        }

        return _buffer;
    }
}

internal sealed class Atrac3PlusFramePlan
{
    public int NumQu { get; set; } = 32;
    public int UsedQu { get; set; }
    public int[,] Wl { get; } = new int[2, 32];
    public int[,] Sf { get; } = new int[2, 32];
    public int[] TableType { get; } = new int[2];
    public int[,] CodeTab { get; } = new int[2, 32];
    public int[,,] QuantCoeffs { get; } = new int[2, 32, 128];
    public bool[,] WndSteep { get; } = new bool[2, 16];
    public int[,] GainNumPoints { get; } = new int[2, 16];
    public int[,,] GainLevCode { get; } = new int[2, 16, 7];
    public int[,,] GainLocCode { get; } = new int[2, 16, 7];
}

internal static class Atrac3PlusBitstreamWriter
{
    /// <summary>
    /// Computes the exact bitstream size (in bits) of <paramref name="plan"/> using optimal
    /// compact header encoding modes without allocating output buffers.
    /// </summary>
    public static int MeasureFrameBits(Atrac3PlusFramePlan plan) =>
        EmitOrMeasure(plan, writer: null);

    /// <summary>
    /// Serializes a quantized stereo frame into a Sony-compliant <paramref name="frameBytes"/> ATRAC3plus packet.
    /// </summary>
    public static byte[] EncodeStereoFrame(Atrac3PlusFramePlan plan, int frameBytes)
    {
        var writer = new BitWriter(frameBytes);
        int written = EmitOrMeasure(plan, writer);
        if (written > frameBytes * 8)
        {
            throw new InvalidOperationException($"Frame exceeded budget ({written} > {frameBytes * 8} bits).");
        }

        return writer.FinalizeFrame(padByte: 0x01);
    }

    private static int EmitOrMeasure(Atrac3PlusFramePlan plan, BitWriter? writer)
    {
        int bits = 0;

        // 1. Fixed stereo unit header: first_bit=0 (1), ch_unit_id=1 (2), num_qu-1 (5), mute=0 (1)
        int numQu = plan.NumQu;
        int usedQu = plan.UsedQu;
        int numSubbands = Atrac3PlusTables.QuantUnitToSubband[numQu - 1] + 1;
        int numCodedSubbands = usedQu > 0 ? Atrac3PlusTables.QuantUnitToSubband[usedQu - 1] + 1 : 0;

        writer?.WriteBits(0, 1);
        writer?.WriteBits(1, 2); // CH_UNIT_STEREO
        writer?.WriteBits((uint)(numQu - 1), 5);
        writer?.WriteBit(false); // mute_flag = 0
        bits += 9;

        // 2. Word lengths wl[0..1][0..numQu-1]
        for (int ch = 0; ch < 2; ch++)
        {
            bits += EmitWordLengths(ch, numQu, plan.Wl, writer);
        }

        if (usedQu > 0)
        {
            // 3. Scale factors sf[0..1][0..usedQu-1]
            for (int ch = 0; ch < 2; ch++)
            {
                bits += EmitScaleFactors(ch, usedQu, plan.Sf, writer);
            }

            // 4. Restricted code tables (use_full_table = 0)
            writer?.WriteBit(false);
            bits += 1;

            for (int ch = 0; ch < 2; ch++)
            {
                bits += EmitCodeTableIndexes(ch, usedQu, plan.TableType[ch], plan.Wl, plan.CodeTab, writer);
            }

            // 5. Quantized Spectral Coefficients + Power Compensation (disabled = 15)
            for (int ch = 0; ch < 2; ch++)
            {
                int tt = plan.TableType[ch];
                for (int qu = 0; qu < usedQu; qu++)
                {
                    int w = plan.Wl[ch, qu];
                    if (w == 0)
                    {
                        continue;
                    }

                    int nspecs = Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - Atrac3PlusTables.QuantUnitToSpecPos[qu];
                    int fullCt = Atrac3PlusTables.RestrictedToFullCodeTable[tt][w - 1][plan.CodeTab[ch, qu]];
                    int ti = (tt * 8 + fullCt) * 7 + (w - 1);
                    var desc = Atrac3PlusTables.SpectralTableDescriptors[ti];
                    var vtab = Atrac3PlusTables.SpectralVlc[ti];

                    bits += EmitQuantizedQuSpectrum(plan, ch, qu, nspecs, desc, vtab, writer);
                }

                if (usedQu > 2)
                {
                    int npg = Atrac3PlusTables.SubbandToNumPowerGroups[numCodedSubbands - 1];
                    for (int g = 0; g < npg; g++)
                    {
                        writer?.WriteBits(15u, 4);
                    }

                    bits += npg * 4;
                }
            }
        }

        // 6. Stereo / Window / Gain / Tones / Noise / Terminator
        // swap_ch = all 0 -> "0" (1 bit)
        writer?.WriteBit(false);
        bits += 1;

        // neg_coef = all 0 -> "0" (1 bit)
        writer?.WriteBit(false);
        bits += 1;

        // wnd_shape[0..1]
        for (int ch = 0; ch < 2; ch++)
        {
            bits += EmitSubbandFlags(plan.WndSteep, ch, numSubbands, writer);
        }

        // gain_present[0..1]
        for (int ch = 0; ch < 2; ch++)
        {
            bits += EmitGainControl(plan, ch, numSubbands, writer);
        }

        // tones_present = 0, noise_present = 0, CH_UNIT_TERMINATOR = 3 (2 bits)
        writer?.WriteBit(false);
        writer?.WriteBit(false);
        writer?.WriteBits(3u, 2);
        bits += 4;

        return bits;
    }

    private static int EmitGainControl(Atrac3PlusFramePlan plan, int ch, int numSubbands, BitWriter? writer)
    {
        int csb = 0;
        for (int sb = numSubbands - 1; sb >= 0; sb--)
        {
            if (plan.GainNumPoints[ch, sb] > 0)
            {
                csb = sb + 1;
                break;
            }
        }

        if (csb == 0)
        {
            writer?.WriteBit(false);
            return 1;
        }

        int bits = 0;
        writer?.WriteBit(true);
        writer?.WriteBits((uint)(csb - 1), 4);
        writer?.WriteBit(false); // highband_repl = 0 (ngsb = csb)
        bits += 6;

        // num_points: Mode 0 (3-bit raw per sb)
        writer?.WriteBits(0u, 2);
        bits += 2;
        for (int sb = 0; sb < csb; sb++)
        {
            writer?.WriteBits((uint)plan.GainNumPoints[ch, sb], 3);
            bits += 3;
        }

        // lev_code: Mode 0 (4-bit raw per point)
        writer?.WriteBits(0u, 2);
        bits += 2;
        for (int sb = 0; sb < csb; sb++)
        {
            int npts = plan.GainNumPoints[ch, sb];
            for (int p = 0; p < npts; p++)
            {
                int lev = Math.Clamp(plan.GainLevCode[ch, sb, p], 0, 15);
                writer?.WriteBits((uint)lev, 4);
                bits += 4;
            }
        }

        // loc_code: Mode 0 (loc_m0 variable-width delta)
        writer?.WriteBits(0u, 2);
        bits += 2;
        for (int sb = 0; sb < csb; sb++)
        {
            int npts = plan.GainNumPoints[ch, sb];
            int prev = 0;
            for (int p = 0; p < npts; p++)
            {
                int loc = Math.Clamp(plan.GainLocCode[ch, sb, p], p == 0 ? 1 : prev + 1, 30);
                if (p == 0 || prev < 15)
                {
                    writer?.WriteBits((uint)loc, 5);
                    bits += 5;
                }
                else if (prev < 30)
                {
                    int bw = System.Numerics.BitOperations.Log2((uint)(30 - prev)) + 1;
                    writer?.WriteBits((uint)(loc - prev - 1), bw);
                    bits += bw;
                }
                prev = loc;
            }
        }

        return bits;
    }

    private static int EmitQuantizedQuSpectrum(
        Atrac3PlusFramePlan plan,
        int ch,
        int qu,
        int nspecs,
        SpectralTableDescriptor desc,
        CanonicalVlcTable vtab,
        BitWriter? writer)
    {
        int gs = desc.GroupSize;
        int nc = desc.NumCoeffsPerSymbol;
        int bpc = desc.BitsPerCoeff;
        bool isSigned = desc.IsSigned;
        int cMask = (1 << bpc) - 1;
        int stride = gs * nc;
        int bits = 0;

        for (int pos = 0; pos < nspecs; pos += stride)
        {
            if (gs > 1)
            {
                bool anyNonZero = false;
                for (int k = 0; k < stride; k++)
                {
                    if (plan.QuantCoeffs[ch, qu, pos + k] != 0)
                    {
                        anyNonZero = true;
                        break;
                    }
                }

                writer?.WriteBit(anyNonZero);
                bits += 1;
                if (!anyNonZero)
                {
                    continue;
                }
            }

            for (int g = 0; g < gs; g++)
            {
                int sym = 0;
                int grpBase = pos + g * nc;
                for (int c = 0; c < nc; c++)
                {
                    int q = plan.QuantCoeffs[ch, qu, grpBase + c];
                    int cf = isSigned ? (q & cMask) : Math.Abs(q);
                    sym |= (cf & cMask) << (c * bpc);
                }

                var code = vtab.GetCode(sym);
                writer?.WriteBits(code.Code, code.BitLength);
                bits += code.BitLength;

                if (!isSigned)
                {
                    for (int c = 0; c < nc; c++)
                    {
                        int q = plan.QuantCoeffs[ch, qu, grpBase + c];
                        if (q != 0)
                        {
                            writer?.WriteBit(q < 0);
                            bits += 1;
                        }
                    }
                }
            }
        }

        return bits;
    }

    private static int EmitWordLengths(int ch, int numQu, int[,] wl, BitWriter? writer)
    {
        // Determine trailing zero length to evaluate fill_mode = 1 when beneficial
        int activeLen = 0;
        for (int i = numQu - 1; i >= 0; i--)
        {
            if (wl[ch, i] != 0)
            {
                activeLen = i + 1;
                break;
            }
        }

        bool useFillTail = activeLen > 0 && activeLen < numQu;
        int fillMode = useFillTail ? 1 : 0;
        int codedCount = useFillTail ? activeLen : numQu;

        // Candidate A: Mode 0 direct 3-bit across all numQu
        int bestMode = 0;
        int bestVlcTab = 0;
        int bestFillMode = 0;
        int bestCoded = numQu;
        int bestBits = 2 + 3 * numQu;

        // Candidate B: Mode 3 intra-channel differential VLC (widx = 0)
        for (int fillOpt = 0; fillOpt <= (useFillTail ? 1 : 0); fillOpt++)
        {
            int cCount = fillOpt == 1 ? activeLen : numQu;
            if (cCount <= 0)
            {
                continue;
            }

            int hdrBits = 2 + 2 + 2 + (fillOpt == 1 ? 5 : 0) + 2 + 3; // mode + widx + fill + [num_coded] + vlcIdx + wl[0]
            for (int tab = 0; tab < 4; tab++)
            {
                var vtab = Atrac3PlusTables.WordLenVlc[tab];
                bool valid = true;
                int sumBits = hdrBits;
                for (int i = 1; i < cCount; i++)
                {
                    int delta = (wl[ch, i] - wl[ch, i - 1]) & 7;
                    if (!vtab.TryGetCode(delta, out _, out byte len))
                    {
                        valid = false;
                        break;
                    }

                    sumBits += len;
                }

                if (valid && sumBits < bestBits)
                {
                    bestBits = sumBits;
                    bestMode = 3;
                    bestVlcTab = tab;
                    bestFillMode = fillOpt;
                    bestCoded = cCount;
                }
            }
        }

        // Candidate C (ch == 1 only): Mode 1 inter-channel delta from ch 0
        if (ch == 1)
        {
            for (int fillOpt = 0; fillOpt <= (useFillTail ? 1 : 0); fillOpt++)
            {
                int cCount = fillOpt == 1 ? activeLen : numQu;
                if (cCount <= 0)
                {
                    continue;
                }

                int hdrBits = 2 + 2 + (fillOpt == 1 ? 5 : 0) + 2; // mode + fill + [num_coded] + vlcIdx
                for (int tab = 0; tab < 4; tab++)
                {
                    var vtab = Atrac3PlusTables.WordLenVlc[tab];
                    bool valid = true;
                    int sumBits = hdrBits;
                    for (int i = 0; i < cCount; i++)
                    {
                        int delta = (wl[1, i] - wl[0, i]) & 7;
                        if (!vtab.TryGetCode(delta, out _, out byte len))
                        {
                            valid = false;
                            break;
                        }

                        sumBits += len;
                    }

                    if (valid && sumBits < bestBits)
                    {
                        bestBits = sumBits;
                        bestMode = 1;
                        bestVlcTab = tab;
                        bestFillMode = fillOpt;
                        bestCoded = cCount;
                    }
                }
            }
        }

        if (writer is not null)
        {
            writer.WriteBits((uint)bestMode, 2);
            if (bestMode == 0)
            {
                for (int i = 0; i < numQu; i++)
                {
                    writer.WriteBits((uint)wl[ch, i], 3);
                }
            }
            else if (bestMode == 3)
            {
                writer.WriteBits(0, 2); // widx = 0
                writer.WriteBits((uint)bestFillMode, 2);
                if (bestFillMode == 1)
                {
                    writer.WriteBits((uint)bestCoded, 5);
                }

                writer.WriteBits((uint)bestVlcTab, 2);
                writer.WriteBits((uint)wl[ch, 0], 3);
                var vtab = Atrac3PlusTables.WordLenVlc[bestVlcTab];
                for (int i = 1; i < bestCoded; i++)
                {
                    var code = vtab.GetCode((wl[ch, i] - wl[ch, i - 1]) & 7);
                    writer.WriteBits(code.Code, code.BitLength);
                }
            }
            else // bestMode == 1 on ch 1
            {
                writer.WriteBits((uint)bestFillMode, 2);
                if (bestFillMode == 1)
                {
                    writer.WriteBits((uint)bestCoded, 5);
                }

                writer.WriteBits((uint)bestVlcTab, 2);
                var vtab = Atrac3PlusTables.WordLenVlc[bestVlcTab];
                for (int i = 0; i < bestCoded; i++)
                {
                    var code = vtab.GetCode((wl[1, i] - wl[0, i]) & 7);
                    writer.WriteBits(code.Code, code.BitLength);
                }
            }
        }

        return bestBits;
    }

    private static int EmitScaleFactors(int ch, int usedQu, int[,] sf, BitWriter? writer)
    {
        if (ch == 1)
        {
            bool identical = true;
            for (int i = 0; i < usedQu; i++)
            {
                if (sf[1, i] != sf[0, i])
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                writer?.WriteBits(3u, 2); // Mode 3 clone ch 0
                return 2;
            }
        }

        int bestMode = 0;
        int bestVlcTab = 0;
        int bestBits = 2 + 6 * usedQu;

        if (ch == 0)
        {
            // Mode 3 with widx = 0: differential SF_VLC[0..3]
            for (int vsel = 0; vsel < 4; vsel++)
            {
                var vtab = Atrac3PlusTables.ScaleFactorVlc[vsel];
                bool valid = true;
                int sumBits = 2 + 2 + 2 + 6; // mode + widx(0) + vsel + sf[0]
                for (int i = 1; i < usedQu; i++)
                {
                    int delta = (sf[0, i] - sf[0, i - 1]) & 0x3F;
                    if (!vtab.TryGetCode(delta, out _, out byte len))
                    {
                        valid = false;
                        break;
                    }

                    sumBits += len;
                }

                if (valid && sumBits < bestBits)
                {
                    bestBits = sumBits;
                    bestMode = 3;
                    bestVlcTab = vsel;
                }
            }
        }
        else
        {
            // Mode 1 on ch 1: inter-channel delta from ch 0 via SF_VLC[0..3]
            for (int vsel = 0; vsel < 4; vsel++)
            {
                var vtab = Atrac3PlusTables.ScaleFactorVlc[vsel];
                bool valid = true;
                int sumBits = 2 + 2; // mode + vsel
                for (int i = 0; i < usedQu; i++)
                {
                    int delta = (sf[1, i] - sf[0, i]) & 0x3F;
                    if (!vtab.TryGetCode(delta, out _, out byte len))
                    {
                        valid = false;
                        break;
                    }

                    sumBits += len;
                }

                if (valid && sumBits < bestBits)
                {
                    bestBits = sumBits;
                    bestMode = 1;
                    bestVlcTab = vsel;
                }
            }
        }

        if (writer is not null)
        {
            writer.WriteBits((uint)bestMode, 2);
            if (bestMode == 0)
            {
                for (int i = 0; i < usedQu; i++)
                {
                    writer.WriteBits((uint)sf[ch, i], 6);
                }
            }
            else if (bestMode == 3)
            {
                writer.WriteBits(0u, 2); // widx = 0
                writer.WriteBits((uint)bestVlcTab, 2);
                writer.WriteBits((uint)sf[0, 0], 6);
                var vtab = Atrac3PlusTables.ScaleFactorVlc[bestVlcTab];
                for (int i = 1; i < usedQu; i++)
                {
                    var code = vtab.GetCode((sf[0, i] - sf[0, i - 1]) & 0x3F);
                    writer.WriteBits(code.Code, code.BitLength);
                }
            }
            else // Mode 1 ch 1
            {
                writer.WriteBits((uint)bestVlcTab, 2);
                var vtab = Atrac3PlusTables.ScaleFactorVlc[bestVlcTab];
                for (int i = 0; i < usedQu; i++)
                {
                    var code = vtab.GetCode((sf[1, i] - sf[0, i]) & 0x3F);
                    writer.WriteBits(code.Code, code.BitLength);
                }
            }
        }

        return bestBits;
    }

    private static int EmitCodeTableIndexes(int ch, int usedQu, int tableType, int[,] wl, int[,] tabIdx, BitWriter? writer)
    {
        // Evaluate Mode 0 (2b direct) vs Mode 1 (CT_VLC[0]) under use_full = 0, nvals = usedQu
        int mode0Bits = 1 + 2 + 1; // table_type + mode + nvals_flag(0)
        int mode1Bits = mode0Bits;
        var ctVlc = Atrac3PlusTables.CodeTabVlc[0];
        bool mode1Valid = true;

        for (int i = 0; i < usedQu; i++)
        {
            if (wl[ch, i] > 0)
            {
                mode0Bits += 2;
                if (!ctVlc.TryGetCode(tabIdx[ch, i], out _, out byte len))
                {
                    mode1Valid = false;
                }
                else
                {
                    mode1Bits += len;
                }
            }
            else if (ch == 1 && wl[0, i] > 0)
            {
                mode0Bits += 1;
                mode1Bits += 1;
            }
        }

        bool useMode1 = mode1Valid && mode1Bits < mode0Bits;
        int bestBits = useMode1 ? mode1Bits : mode0Bits;

        if (writer is not null)
        {
            writer.WriteBits((uint)tableType, 1);
            writer.WriteBits(useMode1 ? 1u : 0u, 2);
            writer.WriteBit(false); // nvals_flag = 0 -> nvals = usedQu

            for (int i = 0; i < usedQu; i++)
            {
                if (wl[ch, i] > 0)
                {
                    if (useMode1)
                    {
                        var code = ctVlc.GetCode(tabIdx[ch, i]);
                        writer.WriteBits(code.Code, code.BitLength);
                    }
                    else
                    {
                        writer.WriteBits((uint)tabIdx[ch, i], 2);
                    }
                }
                else if (ch == 1 && wl[0, i] > 0)
                {
                    // clone_master = 1 leaves ch 1 zeroed
                    writer.WriteBit(true);
                }
            }
        }

        return bestBits;
    }

    private static int EmitSubbandFlags(bool[,] flags, int ch, int count, BitWriter? writer)
    {
        if (count == 0)
        {
            writer?.WriteBit(false);
            return 1;
        }

        bool allFalse = true;
        bool allTrue = true;
        for (int i = 0; i < count; i++)
        {
            if (flags[ch, i]) allFalse = false;
            else allTrue = false;
        }

        if (allFalse)
        {
            writer?.WriteBit(false);
            return 1;
        }

        if (allTrue)
        {
            writer?.WriteBits(2u, 2); // 10
            return 2;
        }

        writer?.WriteBits(3u, 2); // 11
        for (int i = 0; i < count; i++)
        {
            writer?.WriteBit(flags[ch, i]);
        }

        return 2 + count;
    }
}
