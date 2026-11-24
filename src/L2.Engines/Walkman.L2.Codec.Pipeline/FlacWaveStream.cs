using System.Buffers.Binary;
using NAudio.Wave;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Clean-room RFC 9639 FLAC decoder yielding 16-bit little-endian PCM via NAudio <see cref="WaveStream"/>.
/// Used as a fallback when Windows Media Foundation rejects FLAC files with large PADDING/PICTURE blocks.
/// </summary>
internal sealed class FlacWaveStream : WaveStream
{
    private static readonly int[] SampleRateTable =
    [
        0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000
    ];

    private static readonly byte[] Crc8Table = BuildCrc8Table();

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _minBlockSize;
    private readonly int _maxBlockSize;
    private readonly int _streamSampleRate;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly long _totalSamples;
    private readonly long _firstFrameOffset;
    private readonly long _lengthBytes;
    private readonly WaveFormat _waveFormat;

    private readonly byte[] _readBuf = new byte[65536];
    private int _bufPos;
    private int _bufLen;
    private ulong _bitAcc;
    private int _bitCount;

    private int[][] _channelBuffers;
    private int[] _residualBuffer;
    private byte[] _pcmFrameBuffer;
    private int _pcmBufferPos;
    private int _pcmBufferLen;
    private long _currentSample;

    public static bool LooksLikeFlacFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            Span<byte> hdr = stackalloc byte[10];
            if (fs.Read(hdr) < 4) return false;
            if (hdr[0] == (byte)'f' && hdr[1] == (byte)'L' && hdr[2] == (byte)'a' && hdr[3] == (byte)'C') return true;
            if (hdr[0] == (byte)'I' && hdr[1] == (byte)'D' && hdr[2] == (byte)'3')
            {
                int bodySize = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
                int footer = (hdr[5] & 0x10) != 0 ? 10 : 0;
                fs.Seek(10L + bodySize + footer, SeekOrigin.Begin);
                Span<byte> magic = stackalloc byte[4];
                return fs.Read(magic) == 4 && magic[0] == (byte)'f' && magic[1] == (byte)'L' && magic[2] == (byte)'a' && magic[3] == (byte)'C';
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public FlacWaveStream(string filePath)
        : this(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536), ownsStream: true)
    {
    }

    public FlacWaveStream(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        try
        {
            _stream.Position = 0;
            SkipOptionalId3v2();

        Span<byte> magic = stackalloc byte[4];
        ReadExact(_stream, magic);
        if (magic[0] != (byte)'f' || magic[1] != (byte)'L' || magic[2] != (byte)'a' || magic[3] != (byte)'C')
        {
            throw new InvalidDataException("Not a valid FLAC stream.");
        }

        bool foundStreamInfo = false;
        bool isLast;
        Span<byte> lenBytes = stackalloc byte[3];
        do
        {
            int hdr = _stream.ReadByte();
            if (hdr < 0)
            {
                throw new EndOfStreamException();
            }

            isLast = (hdr & 0x80) != 0;
            int blockType = hdr & 0x7F;
            ReadExact(_stream, lenBytes);
            int blockLen = (lenBytes[0] << 16) | (lenBytes[1] << 8) | lenBytes[2];

            if (blockType == 0 && blockLen >= 34)
            {
                byte[] info = new byte[blockLen];
                ReadExact(_stream, info);
                _minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0, 2));
                _maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(2, 2));
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(10, 8));
                _streamSampleRate = (int)((packed >> 44) & 0xFFFFFUL);
                _channels = (int)(((packed >> 41) & 0x07UL) + 1);
                _bitsPerSample = (int)(((packed >> 36) & 0x1FUL) + 1);
                _totalSamples = (long)(packed & 0xFFFFFFFFFUL);
                foundStreamInfo = true;
            }
            else
            {
                _stream.Seek(blockLen, SeekOrigin.Current);
            }
        }
        while (!isLast);

        if (!foundStreamInfo || _streamSampleRate <= 0 || _channels <= 0 || _channels > 8)
        {
            throw new InvalidDataException("Invalid FLAC STREAMINFO.");
        }

        _firstFrameOffset = _stream.Position;
        _waveFormat = new WaveFormat(_streamSampleRate, 16, _channels);
        _lengthBytes = _totalSamples > 0
            ? _totalSamples * _channels * 2L
            : Math.Max(0, (_stream.Length - _firstFrameOffset) * 2L);

        int maxBufSamples = Math.Max(4096, Math.Max(_maxBlockSize, 65535));
        _channelBuffers = new int[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            _channelBuffers[ch] = new int[maxBufSamples];
        }

            _residualBuffer = new int[maxBufSamples];
            _pcmFrameBuffer = new byte[maxBufSamples * _channels * 2];
        }
        catch
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }

            throw;
        }
    }

    public override WaveFormat WaveFormat => _waveFormat;
    public override long Length => _lengthBytes;
    public override long Position
    {
        get
        {
            int rem = (_pcmBufferLen - _pcmBufferPos) / (_channels * 2);
            return Math.Max(0, _currentSample - rem) * _channels * 2L;
        }
        set
        {
            if (value <= 0)
            {
                _stream.Position = _firstFrameOffset;
                _bufPos = 0;
                _bufLen = 0;
                _bitAcc = 0;
                _bitCount = 0;
                _pcmBufferPos = 0;
                _pcmBufferLen = 0;
                _currentSample = 0;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalWritten = 0;
        while (totalWritten < count)
        {
            if (_pcmBufferPos < _pcmBufferLen)
            {
                int available = _pcmBufferLen - _pcmBufferPos;
                int toCopy = Math.Min(available, count - totalWritten);
                Buffer.BlockCopy(_pcmFrameBuffer, _pcmBufferPos, buffer, offset + totalWritten, toCopy);
                _pcmBufferPos += toCopy;
                totalWritten += toCopy;
                continue;
            }

            if (!DecodeNextFrame())
            {
                break;
            }
        }

        return totalWritten;
    }

    private bool DecodeNextFrame()
    {
        while (true)
        {
            int b1 = ReadByteAligned();
            if (b1 < 0)
            {
                return false;
            }

            if (b1 != 0xFF)
            {
                continue;
            }

            long frameStartPos = GetAlignedStreamPosition() - 1;
            ResetBitReader(frameStartPos);
            if (!TryParseFrameHeader(out int blockSize, out int channelAssignment, out int frameBps))
            {
                ResetBitReader(frameStartPos + 1);
                continue;
            }

            EnsureBuffers(blockSize);

            try
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    int subBps = frameBps;
                    if ((channelAssignment == 8 && ch == 1) ||
                        (channelAssignment == 9 && ch == 0) ||
                        (channelAssignment == 10 && ch == 1))
                    {
                        subBps++;
                    }

                    DecodeSubframe(_channelBuffers[ch], blockSize, subBps);
                }

                AlignToByte();
                _ = ReadBits(16);
                DecorrelateChannels(blockSize, channelAssignment);
                PackPcm16(blockSize, frameBps);
                _currentSample += blockSize;
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }

    private bool TryParseFrameHeader(out int blockSize, out int channelAssignment, out int frameBps)
    {
        blockSize = 0;
        channelAssignment = 0;
        frameBps = _bitsPerSample;

        try
        {
            byte crc = 0;
            uint sync = (uint)ReadBits(15);
            if (sync != 0x7FFC)
            {
                return false;
            }

            uint blockingStrategy = (uint)ReadBits(1);
            byte b0 = (byte)((sync >> 7) & 0xFF);
            byte b1 = (byte)(((sync & 0x7F) << 1) | blockingStrategy);
            crc = Crc8Table[crc ^ b0];
            crc = Crc8Table[crc ^ b1];

            int bsCode = (int)ReadBits(4);
            int srCode = (int)ReadBits(4);
            crc = Crc8Table[crc ^ (byte)((bsCode << 4) | srCode)];

            channelAssignment = (int)ReadBits(4);
            int ssCode = (int)ReadBits(3);
            int reserved = (int)ReadBits(1);
            if (reserved != 0)
            {
                return false;
            }

            crc = Crc8Table[crc ^ (byte)((channelAssignment << 4) | (ssCode << 1))];
            int expectedChannels = channelAssignment <= 7 ? channelAssignment + 1 : 2;
            if (expectedChannels != _channels)
            {
                return false;
            }

            frameBps = ssCode switch
            {
                0 => _bitsPerSample,
                1 => 8,
                2 => 12,
                4 => 16,
                5 => 20,
                6 => 24,
                7 => 32,
                _ => -1
            };
            if (frameBps <= 0)
            {
                return false;
            }

            _ = ReadUtf8UInt64(ref crc);
            blockSize = bsCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (bsCode - 2),
                6 => (int)ReadHeaderByte(ref crc) + 1,
                7 => (int)ReadHeaderUInt16(ref crc) + 1,
                >= 8 and <= 15 => 256 << (bsCode - 8),
                _ => 0
            };
            if (blockSize <= 0)
            {
                return false;
            }

            int sr = srCode switch
            {
                0 => _streamSampleRate,
                >= 1 and <= 11 => SampleRateTable[srCode],
                12 => (int)ReadHeaderByte(ref crc) * 1000,
                13 => (int)ReadHeaderUInt16(ref crc),
                14 => (int)ReadHeaderUInt16(ref crc) * 10,
                _ => 0
            };
            if (sr != _streamSampleRate)
            {
                return false;
            }

            return (byte)ReadBits(8) == crc;
        }
        catch
        {
            return false;
        }
    }

    private void DecodeSubframe(int[] output, int blockSize, int bps)
    {
        if (ReadBits(1) != 0)
        {
            throw new InvalidDataException();
        }

        int type = (int)ReadBits(6);
        int wastedFlag = (int)ReadBits(1);
        int wastedBits = 0;
        if (wastedFlag != 0)
        {
            wastedBits = ReadUnaryZeros() + 1;
            bps -= wastedBits;
        }

        if (bps <= 0 || bps > 33)
        {
            throw new InvalidDataException($"Invalid FLAC subframe bits-per-sample: {bps}.");
        }

        if (type == 0)
        {
            Array.Fill(output, ReadSignedBits(bps), 0, blockSize);
        }
        else if (type == 1)
        {
            for (int i = 0; i < blockSize; i++) output[i] = ReadSignedBits(bps);
        }
        else if (type is >= 8 and <= 12)
        {
            int order = type - 8;
            if (order > blockSize) throw new InvalidDataException("FLAC fixed predictor order exceeds block size.");
            for (int i = 0; i < order; i++) output[i] = ReadSignedBits(bps);
            DecodeResidual(blockSize, order);
            RestoreFixed(output, blockSize, order);
        }
        else if (type >= 32)
        {
            int order = type - 31;
            if (order > blockSize) throw new InvalidDataException("FLAC LPC order exceeds block size.");
            for (int i = 0; i < order; i++) output[i] = ReadSignedBits(bps);
            int qlpPrecision = (int)ReadBits(4) + 1;
            if (qlpPrecision > 15) throw new InvalidDataException($"Invalid FLAC QLP precision: {qlpPrecision}.");
            int qlpShift = ReadSignedBits(5);
            if (qlpShift < 0 || qlpShift > 31) throw new InvalidDataException($"Invalid FLAC QLP shift: {qlpShift}.");
            Span<int> coefs = stackalloc int[order];
            for (int i = 0; i < order; i++) coefs[i] = ReadSignedBits(qlpPrecision);
            DecodeResidual(blockSize, order);
            RestoreLpc(output, blockSize, order, coefs, qlpShift);
        }
        else
        {
            throw new InvalidDataException();
        }

        if (wastedBits > 0)
        {
            for (int i = 0; i < blockSize; i++) output[i] <<= wastedBits;
        }
    }

    private void DecodeResidual(int blockSize, int predictorOrder)
    {
        int method = (int)ReadBits(2);
        if (method is not (0 or 1))
        {
            throw new InvalidDataException($"Unsupported FLAC residual coding method: {method}.");
        }

        int paramBits = method == 0 ? 4 : 5;
        int escapeCode = method == 0 ? 15 : 31;
        int partitionOrder = (int)ReadBits(4);
        int numPartitions = 1 << partitionOrder;
        if (numPartitions > blockSize || (blockSize % numPartitions) != 0)
        {
            throw new InvalidDataException("Invalid FLAC residual partition order.");
        }

        int samplesPerPartition = blockSize >> partitionOrder;
        if (samplesPerPartition < predictorOrder)
        {
            throw new InvalidDataException("FLAC residual partition smaller than predictor order.");
        }

        int resIdx = predictorOrder;
        for (int p = 0; p < numPartitions; p++)
        {
            int count = p == 0 ? samplesPerPartition - predictorOrder : samplesPerPartition;
            if (count < 0 || resIdx + count > _residualBuffer.Length)
            {
                throw new InvalidDataException("FLAC residual partition exceeds buffer bounds.");
            }
            int param = (int)ReadBits(paramBits);
            if (param == escapeCode)
            {
                int bps = (int)ReadBits(5);
                if (bps == 0) { Array.Clear(_residualBuffer, resIdx, count); resIdx += count; }
                else { for (int i = 0; i < count; i++) _residualBuffer[resIdx++] = ReadSignedBits(bps); }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    uint q = (uint)ReadUnaryZeros();
                    uint r = param > 0 ? (uint)ReadBits(param) : 0u;
                    uint folded = (q << param) | r;
                    _residualBuffer[resIdx++] = (int)(folded >> 1) ^ -(int)(folded & 1);
                }
            }
        }
    }

    private void RestoreFixed(int[] output, int blockSize, int order)
    {
        switch (order)
        {
            case 0: Array.Copy(_residualBuffer, 0, output, 0, blockSize); break;
            case 1: for (int i = 1; i < blockSize; i++) output[i] = _residualBuffer[i] + output[i - 1]; break;
            case 2: for (int i = 2; i < blockSize; i++) output[i] = (int)(_residualBuffer[i] + (2L * output[i - 1]) - output[i - 2]); break;
            case 3: for (int i = 3; i < blockSize; i++) output[i] = (int)(_residualBuffer[i] + (3L * output[i - 1]) - (3L * output[i - 2]) + output[i - 3]); break;
            case 4: for (int i = 4; i < blockSize; i++) output[i] = (int)(_residualBuffer[i] + (4L * output[i - 1]) - (6L * output[i - 2]) + (4L * output[i - 3]) - output[i - 4]); break;
        }
    }

    private void RestoreLpc(int[] output, int blockSize, int order, ReadOnlySpan<int> coefs, int qlpShift)
    {
        for (int i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)coefs[j] * output[i - j - 1];
            output[i] = (int)(_residualBuffer[i] + (sum >> qlpShift));
        }
    }

    private void DecorrelateChannels(int blockSize, int channelAssignment)
    {
        if (channelAssignment == 8)
        {
            int[] l = _channelBuffers[0], s = _channelBuffers[1];
            for (int i = 0; i < blockSize; i++) s[i] = l[i] - s[i];
        }
        else if (channelAssignment == 9)
        {
            int[] s = _channelBuffers[0], r = _channelBuffers[1];
            for (int i = 0; i < blockSize; i++) s[i] += r[i];
        }
        else if (channelAssignment == 10)
        {
            int[] m = _channelBuffers[0], s = _channelBuffers[1];
            for (int i = 0; i < blockSize; i++)
            {
                long side = s[i];
                long mid = ((long)m[i] << 1) | (side & 1L);
                m[i] = (int)((mid + side) >> 1);
                s[i] = (int)((mid - side) >> 1);
            }
        }
    }

    private void PackPcm16(int blockSize, int bps)
    {
        int dst = 0;
        int sr = bps > 16 ? bps - 16 : 0;
        int sl = bps < 16 ? 16 - bps : 0;
        for (int s = 0; s < blockSize; s++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                int v = _channelBuffers[ch][s];
                if (sr > 0) v >>= sr;
                else if (sl > 0) v <<= sl;
                short c = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                _pcmFrameBuffer[dst++] = (byte)(c & 0xFF);
                _pcmFrameBuffer[dst++] = (byte)((c >> 8) & 0xFF);
            }
        }

        _pcmBufferPos = 0;
        _pcmBufferLen = dst;
    }

    private void EnsureBuffers(int blockSize)
    {
        if (_channelBuffers[0].Length < blockSize)
        {
            for (int ch = 0; ch < _channels; ch++) _channelBuffers[ch] = new int[blockSize];
            _residualBuffer = new int[blockSize];
        }

        int needed = blockSize * _channels * 2;
        if (_pcmFrameBuffer.Length < needed) _pcmFrameBuffer = new byte[needed];
    }

    private void SkipOptionalId3v2()
    {
        Span<byte> id3 = stackalloc byte[10];
        if (_stream.Read(id3) == 10 && id3[0] == (byte)'I' && id3[1] == (byte)'D' && id3[2] == (byte)'3')
        {
            int bodySize = ((id3[6] & 0x7F) << 21) | ((id3[7] & 0x7F) << 14) | ((id3[8] & 0x7F) << 7) | (id3[9] & 0x7F);
            int footer = (id3[5] & 0x10) != 0 ? 10 : 0;
            _stream.Seek(10L + bodySize + footer, SeekOrigin.Begin);
        }
        else _stream.Position = 0;
    }

    private void ResetBitReader(long pos) { _stream.Position = pos; _bufPos = 0; _bufLen = 0; _bitAcc = 0; _bitCount = 0; }
    private long GetAlignedStreamPosition() { AlignToByte(); return _stream.Position - (_bufLen - _bufPos + (_bitCount >> 3)); }
    private int ReadByteAligned() { AlignToByte(); if (_bitCount >= 8) return (int)ReadBits(8); if (_bufPos >= _bufLen && !FillBuffer()) return -1; return _readBuf[_bufPos++]; }
    private void AlignToByte() { int d = _bitCount & 7; if (d > 0) { _bitCount -= d; _bitAcc &= (1UL << _bitCount) - 1UL; } }
    private ulong ReadBits(int count)
    {
        if (count == 0) return 0;
        while (_bitCount < count)
        {
            if (_bufPos >= _bufLen && !FillBuffer()) throw new EndOfStreamException();
            _bitAcc = (_bitAcc << 8) | _readBuf[_bufPos++];
            _bitCount += 8;
        }

        _bitCount -= count;
        ulong mask = count == 64 ? ulong.MaxValue : (1UL << count) - 1UL;
        ulong res = (_bitAcc >> _bitCount) & mask;
        _bitAcc &= (1UL << _bitCount) - 1UL;
        return res;
    }
    private int ReadSignedBits(int count)
    {
        if (count <= 0) return 0;
        ulong raw = ReadBits(count);
        if (count >= 32)
        {
            int sh64 = 64 - count;
            long signed = ((long)raw << sh64) >> sh64;
            return (int)Math.Clamp(signed, int.MinValue, int.MaxValue);
        }

        int sh = 32 - count;
        return ((int)raw << sh) >> sh;
    }

    private int ReadUnaryZeros()
    {
        const int maxUnaryZeros = 65536;
        int z = 0;
        while (ReadBits(1) == 0)
        {
            if (++z > maxUnaryZeros)
            {
                throw new InvalidDataException("FLAC unary zero run exceeded maximum bound.");
            }
        }

        return z;
    }
    private ulong ReadUtf8UInt64(ref byte crc)
    {
        byte f = ReadHeaderByte(ref crc);
        if ((f & 0x80) == 0) return f;
        int ex = (f & 0xE0) == 0xC0 ? 1 : (f & 0xF0) == 0xE0 ? 2 : (f & 0xF8) == 0xF0 ? 3 : (f & 0xFC) == 0xF8 ? 4 : (f & 0xFE) == 0xFC ? 5 : 6;
        ulong v = (ulong)(f & ((1 << (6 - ex)) - 1));
        for (int i = 0; i < ex; i++) v = (v << 6) | (uint)(ReadHeaderByte(ref crc) & 0x3F);
        return v;
    }
    private byte ReadHeaderByte(ref byte crc) { byte b = (byte)ReadBits(8); crc = Crc8Table[crc ^ b]; return b; }
    private ushort ReadHeaderUInt16(ref byte crc) => (ushort)((ReadHeaderByte(ref crc) << 8) | ReadHeaderByte(ref crc));
    private bool FillBuffer() { _bufPos = 0; _bufLen = _stream.Read(_readBuf, 0, _readBuf.Length); return _bufLen > 0; }
    private static void ReadExact(Stream s, Span<byte> d) { int t = 0; while (t < d.Length) { int n = s.Read(d.Slice(t)); if (n <= 0) throw new EndOfStreamException(); t += n; } }
    private static byte[] BuildCrc8Table()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) { int c = i; for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) : (c << 1); t[i] = (byte)(c & 0xFF); }
        return t;
    }
    protected override void Dispose(bool disposing) { if (disposing && _ownsStream) _stream.Dispose(); base.Dispose(disposing); }
}
