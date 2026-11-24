namespace Walkman.L2.Codec.Pipeline.Atrac3Plus;

/// <summary>
/// Clean-room ATRAC3plus psychoacoustic model, scale-factor selector, optimal
/// restricted-codebook searcher, and rate-control quantizer (RE_WIP_03).
/// </summary>
internal sealed class Atrac3PlusQuantizer
{
    private static readonly int[] ValidNumQuDescending = [32, 28, 27, 26, 25, 24, 22, 20, 18, 16, 14, 12, 8, 4, 1];

    private static readonly double[] QuCenterBark = BuildQuCenterBark();
    private static readonly double[] QuAthFloor = BuildQuAthFloor();
    private static readonly double[,] BarkSpread = BuildBarkSpreadMatrix();

    private readonly double[,] _quEnergy = new double[2, 32];
    private readonly double[,] _maskFloor = new double[2, 32];
    private readonly int[,] _baseWl = new int[2, 32];

    // Cache [ch, qu, tt, wl] -> best restricted ct & bit cost (-1 = infeasible)
    private readonly int[,,,] _cachedCt = new int[2, 32, 2, 8];
    private readonly int[,,,] _cachedBits = new int[2, 32, 2, 8];
    private readonly int[,,,] _cachedQ = new int[2, 8, 32, 128]; // [ch, wl, qu, k]

    public Atrac3PlusFramePlan AllocateAndQuantize(
        AtracBitratePreset preset,
        ReadOnlySpan<double> specL,
        ReadOnlySpan<double> specR,
        ReadOnlySpan<bool> wndSteepL,
        ReadOnlySpan<bool> wndSteepR,
        int[,]? gainNumPoints = null,
        int[,,]? gainLevCode = null,
        int[,,]? gainLocCode = null)
    {
        int maxFrameBits = preset.FrameBytes * 8;
        var plan = new Atrac3PlusFramePlan
        {
            NumQu = preset.DefaultNumQu
        };

        for (int b = 0; b < 16; b++)
        {
            plan.WndSteep[0, b] = wndSteepL[b];
            plan.WndSteep[1, b] = wndSteepR[b];
            if (gainNumPoints is not null && gainLevCode is not null && gainLocCode is not null)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int npts = gainNumPoints[ch, b];
                    plan.GainNumPoints[ch, b] = npts;
                    for (int p = 0; p < npts; p++)
                    {
                        plan.GainLevCode[ch, b, p] = gainLevCode[ch, b, p];
                        plan.GainLocCode[ch, b, p] = gainLocCode[ch, b, p];
                    }
                }
            }
        }

        double maxPeak = 0.0;
        for (int k = 0; k < 2048; k++)
        {
            double al = Math.Abs(specL[k]);
            double ar = Math.Abs(specR[k]);
            if (al > maxPeak) maxPeak = al;
            if (ar > maxPeak) maxPeak = ar;
        }

        // Canonical Sony silence frame convention (num_qu = 32, mute = 0, used_qu = 0)
        if (maxPeak < 1e-6)
        {
            plan.NumQu = 32;
            plan.UsedQu = 0;
            return plan;
        }

        int initialNumQu = preset.DefaultNumQu;

        // Clear caches
        for (int ch = 0; ch < 2; ch++)
        {
            ReadOnlySpan<double> spec = ch == 0 ? specL : specR;
            ComputeScaleFactorsAndMasking(ch, spec, plan.Sf);
            PrecomputeCodebookCosts(ch, spec, plan.Sf);
        }

        // Evaluate table_type = 0 vs 1 per channel based on average bit efficiency across mid wl=1..5
        for (int ch = 0; ch < 2; ch++)
        {
            int sumT0 = 0, sumT1 = 0;
            for (int qu = 0; qu < initialNumQu; qu++)
            {
                for (int w = 1; w <= 5; w++)
                {
                    if (_cachedBits[ch, qu, 0, w] > 0) sumT0 += _cachedBits[ch, qu, 0, w];
                    if (_cachedBits[ch, qu, 1, w] > 0) sumT1 += _cachedBits[ch, qu, 1, w];
                }
            }

            plan.TableType[ch] = sumT1 < sumT0 ? 1 : 0;
        }

        // Compute base psychoacoustic word lengths
        for (int ch = 0; ch < 2; ch++)
        {
            for (int qu = 0; qu < 32; qu++)
            {
                if (qu >= initialNumQu || _quEnergy[ch, qu] <= _maskFloor[ch, qu] * 0.25)
                {
                    _baseWl[ch, qu] = 0;
                }
                else
                {
                    double snrNeededDb = 10.0 * Math.Log10(_quEnergy[ch, qu] / Math.Max(_maskFloor[ch, qu], 1e-24));
                    _baseWl[ch, qu] = MapSnrDbToWordLen(snrNeededDb, preset.BitrateKbps, qu);
                }
            }
        }

        // Binary search global quality offset in [-14 .. +14] (in 0.5 wl-units -> integer delta [-7..+7])
        // Aim for highest quality fitting strictly inside maxFrameBits.
        int bestDelta = -7;
        for (int delta = 7; delta >= -7; delta--)
        {
            ApplyWordLengthOffset(plan, initialNumQu, delta);
            int totalBits = Atrac3PlusBitstreamWriter.MeasureFrameBits(plan);
            if (totalBits <= maxFrameBits)
            {
                bestDelta = delta;
                break;
            }
        }

        ApplyWordLengthOffset(plan, initialNumQu, bestDelta);

        // If still over budget (or room left to upgrade important QUs), refine greedily
        int curBits = Atrac3PlusBitstreamWriter.MeasureFrameBits(plan);
        if (curBits > maxFrameBits)
        {
            TrimToBudget(plan, maxFrameBits);
        }
        else
        {
            UpgradeWithRemainingBudget(plan, maxFrameBits);
        }

        // Final safety check on ValidNumQuDescending if an extreme frame still overshoots
        curBits = Atrac3PlusBitstreamWriter.MeasureFrameBits(plan);
        if (curBits > maxFrameBits)
        {
            foreach (int candNumQu in ValidNumQuDescending)
            {
                if (candNumQu > initialNumQu) continue;
                plan.NumQu = candNumQu;
                for (int qu = candNumQu; qu < 32; qu++)
                {
                    plan.Wl[0, qu] = 0;
                    plan.Wl[1, qu] = 0;
                }

                RecomputeUsedQu(plan);
                TrimToBudget(plan, maxFrameBits);
                if (Atrac3PlusBitstreamWriter.MeasureFrameBits(plan) <= maxFrameBits)
                {
                    break;
                }
            }
        }

        FinalizePlanBuffers(plan);
        return plan;
    }

    private void ComputeScaleFactorsAndMasking(int ch, ReadOnlySpan<double> spec, int[,] sfOut)
    {
        for (int qu = 0; qu < 32; qu++)
        {
            int start = Atrac3PlusTables.QuantUnitToSpecPos[qu];
            int end = Atrac3PlusTables.QuantUnitToSpecPos[qu + 1];
            int n = end - start;

            double peak = 0.0;
            double energy = 0.0;
            double logSum = 0.0;

            for (int k = start; k < end; k++)
            {
                double v = spec[k];
                double av = Math.Abs(v);
                if (av > peak) peak = av;
                double e = v * v;
                energy += e;
                logSum += Math.Log(Math.Max(e, 1e-24));
            }

            _quEnergy[ch, qu] = energy;

            double clippedPeak = Math.Min(1.0, peak);
            int sfIdx = 0;
            while (sfIdx < 63 && Atrac3PlusTables.ScaleTable[sfIdx] < clippedPeak)
            {
                sfIdx++;
            }

            sfOut[ch, qu] = sfIdx;

            double meanE = energy / n;
            double geoE = Math.Exp(logSum / n);
            double sfm = meanE > 1e-24 ? Math.Clamp(geoE / meanE, 0.0, 1.0) : 1.0;
            double tonality = Math.Clamp(-Math.Log(Math.Max(sfm, 1e-9)) / 6.0, 0.0, 1.0);

            // Asymmetric Bark masking offset: pure tone ~16 dB, noise ~5.5 dB
            double maskOffsetDb = 5.5 + tonality * 10.5;
            _maskFloor[ch, qu] = maskOffsetDb; // temporarily store offset in dB
        }

        // Apply Bark-domain energy spreading across QUs
        for (int qu = 0; qu < 32; qu++)
        {
            double spread = 0.0;
            for (int j = 0; j < 32; j++)
            {
                spread += _quEnergy[ch, j] * BarkSpread[j, qu];
            }

            double maskOffsetDb = _maskFloor[ch, qu];
            double maskedE = spread * Math.Pow(10.0, -maskOffsetDb / 10.0);
            _maskFloor[ch, qu] = Math.Max(maskedE, QuAthFloor[qu]);
        }
    }

    private void PrecomputeCodebookCosts(int ch, ReadOnlySpan<double> spec, int[,] sfOut)
    {
        for (int qu = 0; qu < 32; qu++)
        {
            int start = Atrac3PlusTables.QuantUnitToSpecPos[qu];
            int nspecs = Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - start;
            double invScale = 1.0 / Atrac3PlusTables.ScaleTable[sfOut[ch, qu]];

            for (int tt = 0; tt < 2; tt++)
            {
                _cachedBits[ch, qu, tt, 0] = 0;
                _cachedCt[ch, qu, tt, 0] = 0;
            }

            for (int w = 1; w <= 7; w++)
            {
                double invStep = 1.0 / Atrac3PlusTables.MantissaStepTable[w];
                int maxMag = Atrac3PlusTables.MaxQuantMag[w];

                for (int i = 0; i < nspecs; i++)
                {
                    double norm = Math.Clamp(spec[start + i] * invScale, -0.999999, 0.999999);
                    int q = (int)Math.Round(norm * invStep, MidpointRounding.ToEven);
                    _cachedQ[ch, w, qu, i] = Math.Clamp(q, -maxMag, maxMag);
                }

                for (int tt = 0; tt < 2; tt++)
                {
                    int bestCt = -1;
                    int bestBits = int.MaxValue;

                    for (int ct = 0; ct < 4; ct++)
                    {
                        int fullCt = Atrac3PlusTables.RestrictedToFullCodeTable[tt][w - 1][ct];
                        int ti = (tt * 8 + fullCt) * 7 + (w - 1);
                        var desc = Atrac3PlusTables.SpectralTableDescriptors[ti];
                        var vtab = Atrac3PlusTables.SpectralVlc[ti];

                        int cost = MeasureQuSpectralBits(ch, qu, w, nspecs, desc, vtab);
                        if (cost >= 0 && cost < bestBits)
                        {
                            bestBits = cost;
                            bestCt = ct;
                        }
                    }

                    _cachedCt[ch, qu, tt, w] = bestCt;
                    _cachedBits[ch, qu, tt, w] = bestCt >= 0 ? bestBits : -1;
                }
            }
        }
    }

    private int MeasureQuSpectralBits(
        int ch,
        int qu,
        int w,
        int nspecs,
        SpectralTableDescriptor desc,
        CanonicalVlcTable vtab)
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
                    if (_cachedQ[ch, w, qu, pos + k] != 0)
                    {
                        anyNonZero = true;
                        break;
                    }
                }

                bits += 1;
                if (!anyNonZero) continue;
            }

            for (int g = 0; g < gs; g++)
            {
                int sym = 0;
                int grpBase = pos + g * nc;
                for (int c = 0; c < nc; c++)
                {
                    int q = _cachedQ[ch, w, qu, grpBase + c];
                    int cf = isSigned ? (q & cMask) : Math.Abs(q);
                    if (!isSigned && q != 0)
                    {
                        bits += 1;
                    }

                    sym |= (cf & cMask) << (c * bpc);
                }

                if (!vtab.TryGetCode(sym, out _, out byte vlcLen))
                {
                    return -1;
                }

                bits += vlcLen;
            }
        }

        return bits;
    }

    private void ApplyWordLengthOffset(Atrac3PlusFramePlan plan, int numQu, int delta)
    {
        plan.NumQu = numQu;
        for (int ch = 0; ch < 2; ch++)
        {
            int tt = plan.TableType[ch];
            for (int qu = 0; qu < 32; qu++)
            {
                if (qu >= numQu || _baseWl[ch, qu] == 0)
                {
                    plan.Wl[ch, qu] = 0;
                    plan.CodeTab[ch, qu] = 0;
                    continue;
                }

                int targetW = Math.Clamp(_baseWl[ch, qu] + delta, 0, 7);
                while (targetW > 0 && _cachedCt[ch, qu, tt, targetW] < 0)
                {
                    targetW--;
                }

                plan.Wl[ch, qu] = targetW;
                plan.CodeTab[ch, qu] = targetW > 0 ? _cachedCt[ch, qu, tt, targetW] : 0;
            }
        }

        RecomputeUsedQu(plan);
        CopyQuantCoeffsToPlan(plan);
    }

    private void TrimToBudget(Atrac3PlusFramePlan plan, int maxFrameBits)
    {
        while (Atrac3PlusBitstreamWriter.MeasureFrameBits(plan) > maxFrameBits && plan.UsedQu > 0)
        {
            // Trim highest-frequency or highest-cost unit first while preserving core bands
            int bestCh = -1;
            int bestQu = -1;
            int bestDeltaWl = -1;
            double lowestValuePerBit = double.MaxValue;

            for (int qu = plan.UsedQu - 1; qu >= 0; qu--)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int w = plan.Wl[ch, qu];
                    if (w == 0) continue;

                    int nextW = w - 1;
                    int tt = plan.TableType[ch];
                    while (nextW > 0 && _cachedCt[ch, qu, tt, nextW] < 0)
                    {
                        nextW--;
                    }

                    int bitsSaved = Math.Max(1, _cachedBits[ch, qu, tt, w] - _cachedBits[ch, qu, tt, nextW]);
                    double freqWeight = 1.0 + qu * 0.25; // prefer shedding ultra-high bands slightly earlier under stress
                    double costRatio = (1.0 / freqWeight) * (w <= 2 ? 0.5 : 1.0) / bitsSaved;
                    if (costRatio < lowestValuePerBit)
                    {
                        lowestValuePerBit = costRatio;
                        bestCh = ch;
                        bestQu = qu;
                        bestDeltaWl = nextW;
                    }
                }
            }

            if (bestCh < 0) break;

            plan.Wl[bestCh, bestQu] = bestDeltaWl;
            plan.CodeTab[bestCh, bestQu] = bestDeltaWl > 0
                ? _cachedCt[bestCh, bestQu, plan.TableType[bestCh], bestDeltaWl]
                : 0;
            RecomputeUsedQu(plan);
            CopyQuantCoeffsToPlan(plan);
        }
    }

    private void UpgradeWithRemainingBudget(Atrac3PlusFramePlan plan, int maxFrameBits)
    {
        // Greedily lift active low/mid QUs up to wl=7 while spare bit budget allows
        for (int pass = 0; pass < 3; pass++)
        {
            bool changed = false;
            for (int qu = 0; qu < plan.NumQu; qu++)
            {
                // Keep Sony's 15.16 kHz high-frequency gate unless energy in QU >= 27 is significant
                if (qu >= 27 && _baseWl[0, qu] == 0 && _baseWl[1, qu] == 0)
                {
                    continue;
                }

                for (int ch = 0; ch < 2; ch++)
                {
                    int curW = plan.Wl[ch, qu];
                    if (curW >= 7 || (_baseWl[ch, qu] == 0 && curW == 0)) continue;

                    int nextW = curW + 1;
                    int tt = plan.TableType[ch];
                    while (nextW <= 7 && _cachedCt[ch, qu, tt, nextW] < 0)
                    {
                        nextW++;
                    }
                    if (nextW > 7) continue;

                    int prevCt = plan.CodeTab[ch, qu];
                    int prevUsed = plan.UsedQu;
                    plan.Wl[ch, qu] = nextW;
                    plan.CodeTab[ch, qu] = _cachedCt[ch, qu, tt, nextW];
                    if (qu + 1 > plan.UsedQu) plan.UsedQu = qu + 1;
                    CopySingleQuToPlan(plan, ch, qu, nextW);

                    if (Atrac3PlusBitstreamWriter.MeasureFrameBits(plan) <= maxFrameBits)
                    {
                        changed = true;
                    }
                    else
                    {
                        plan.Wl[ch, qu] = curW;
                        plan.CodeTab[ch, qu] = prevCt;
                        plan.UsedQu = prevUsed;
                        CopySingleQuToPlan(plan, ch, qu, curW);
                    }
                }
            }

            if (!changed) break;
        }
    }

    private void FinalizePlanBuffers(Atrac3PlusFramePlan plan)
    {
        RecomputeUsedQu(plan);
        CopyQuantCoeffsToPlan(plan);
    }

    private static void RecomputeUsedQu(Atrac3PlusFramePlan plan)
    {
        int used = 0;
        for (int i = plan.NumQu - 1; i >= 0; i--)
        {
            if (plan.Wl[0, i] > 0 || plan.Wl[1, i] > 0)
            {
                used = i + 1;
                break;
            }
        }

        plan.UsedQu = used;
    }

    private void CopyQuantCoeffsToPlan(Atrac3PlusFramePlan plan)
    {
        for (int ch = 0; ch < 2; ch++)
        {
            for (int qu = 0; qu < 32; qu++)
            {
                CopySingleQuToPlan(plan, ch, qu, plan.Wl[ch, qu]);
            }
        }
    }

    private void CopySingleQuToPlan(Atrac3PlusFramePlan plan, int ch, int qu, int wl)
    {
        int nspecs = Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - Atrac3PlusTables.QuantUnitToSpecPos[qu];
        if (wl <= 0)
        {
            for (int i = 0; i < nspecs; i++)
            {
                plan.QuantCoeffs[ch, qu, i] = 0;
            }
            return;
        }

        for (int i = 0; i < nspecs; i++)
        {
            plan.QuantCoeffs[ch, qu, i] = _cachedQ[ch, wl, qu, i];
        }
    }

    private static int MapSnrDbToWordLen(double snrDb, int bitrateKbps, int qu)
    {
        if (qu >= 27 && bitrateKbps <= 96 && snrDb < 14.0)
        {
            return 0;
        }

        if (snrDb <= 0.0) return 1;
        if (snrDb < 10.0) return 2;
        if (snrDb < 14.0) return 3;
        if (snrDb < 19.0) return 4;
        if (snrDb < 24.0) return 5;
        if (snrDb < 30.0) return 6;
        return 7;
    }

    private static double[] BuildQuCenterBark()
    {
        var barks = new double[32];
        for (int qu = 0; qu < 32; qu++)
        {
            double centerBin = 0.5 * (Atrac3PlusTables.QuantUnitToSpecPos[qu] + Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - 1);
            double hz = (centerBin + 0.5) * 22050.0 / 2048.0;
            barks[qu] = HzToBark(hz);
        }

        return barks;
    }

    private static double HzToBark(double f) =>
        13.0 * Math.Atan(0.00076 * f) + 3.5 * Math.Atan(Math.Pow(f / 7500.0, 2.0));

    private static double[] BuildQuAthFloor()
    {
        var floor = new double[32];
        for (int qu = 0; qu < 32; qu++)
        {
            double centerBin = 0.5 * (Atrac3PlusTables.QuantUnitToSpecPos[qu] + Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - 1);
            double f = Math.Max(20.0, (centerBin + 0.5) * 22050.0 / 2048.0);
            double fk = f / 1000.0;
            // Cap high-frequency Terhardt rise so >15 kHz bands remain encodable at 128-352 kbps
            double athDb = Math.Min(
                42.0,
                3.64 * Math.Pow(fk, -0.8)
                - 6.5 * Math.Exp(-0.6 * Math.Pow(fk - 3.3, 2.0))
                + 0.001 * Math.Pow(fk, 4.0));
            double relDb = athDb - 102.0;
            floor[qu] = Math.Pow(10.0, relDb / 10.0) * (Atrac3PlusTables.QuantUnitToSpecPos[qu + 1] - Atrac3PlusTables.QuantUnitToSpecPos[qu]);
        }

        return floor;
    }

    private static double[,] BuildBarkSpreadMatrix()
    {
        var m = new double[32, 32];
        for (int j = 0; j < 32; j++)
        {
            for (int i = 0; i < 32; i++)
            {
                double dz = QuCenterBark[i] - QuCenterBark[j];
                // +25 dB/Bark upward masking slope into higher frequencies, -10 dB/Bark downward
                double spreadDb = dz >= 0 ? -15.0 * dz : 25.0 * dz;
                m[j, i] = spreadDb < -60.0 ? 0.0 : Math.Pow(10.0, spreadDb / 10.0);
            }
        }

        return m;
    }
}
