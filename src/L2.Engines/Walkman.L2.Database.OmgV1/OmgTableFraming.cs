using System.Buffers.Binary;
using System.Text;
using Walkman.Contracts.Devices;

namespace Walkman.L2.Database.OmgV1;

/// <summary>
/// Clean-room Big-Endian binary framing helpers for Sony OMGAUDIO .DAT tables.
/// </summary>
internal static class OmgTableFraming
{
    public const int TableHeaderSize = 16;
    public const int ClassDescriptorSize = 16;
    public const int ClassHeaderSize = 16;
    public const int SubElementSize = 0x80;
    public const int MaxSubElementChars = 59;

    private static readonly int[] Mpeg1L3BitratesKbps =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    private static readonly int[] Mpeg2L3BitratesKbps =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    public readonly record struct ClassDescriptor(string Magic, int StartAddress, int Length);

    public readonly record struct ClassHeader(string Magic, int ElementCount, int ElementSize, uint Complement1, uint Complement2);

    public static void WriteTableHeader(Span<byte> buffer, string tableMagic, byte classCount)
    {
        Encoding.ASCII.GetBytes(tableMagic.AsSpan(0, 4), buffer.Slice(0, 4));
        buffer[4] = 0x01;
        buffer[5] = 0x01;
        buffer[6] = 0x00;
        buffer[7] = 0x00;
        buffer[8] = classCount;
    }

    public static void WriteClassDescriptor(Span<byte> buffer, string classMagic, int startAddress, int length)
    {
        Encoding.ASCII.GetBytes(classMagic.AsSpan(0, 4), buffer.Slice(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(4, 4), startAddress);
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(8, 4), length);
    }

    public static void WriteClassHeader(
        Span<byte> buffer,
        string classMagic,
        int elementCount,
        int elementSize,
        uint complement1 = 0,
        uint complement2 = 0)
    {
        Encoding.ASCII.GetBytes(classMagic.AsSpan(0, 4), buffer.Slice(0, 4));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(4, 2), (ushort)elementCount);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(6, 2), (ushort)elementSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), complement1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(12, 4), complement2);
    }

    public static (string Magic, byte ClassCount) ReadTableHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < TableHeaderSize)
        {
            throw new InvalidDataException("OMGAUDIO table shorter than 16-byte header.");
        }

        return (Encoding.ASCII.GetString(data.Slice(0, 4)), data[8]);
    }

    public static ClassDescriptor ReadClassDescriptor(ReadOnlySpan<byte> data, int classIndex)
    {
        int offset = TableHeaderSize + classIndex * ClassDescriptorSize;
        if (offset + ClassDescriptorSize > data.Length)
        {
            throw new InvalidDataException($"Missing class descriptor {classIndex}.");
        }

        string magic = Encoding.ASCII.GetString(data.Slice(offset, 4));
        int start = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 4, 4));
        int length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));
        return new ClassDescriptor(magic, start, length);
    }

    public static ClassHeader ReadClassHeader(ReadOnlySpan<byte> data, int startAddress)
    {
        if (startAddress + ClassHeaderSize > data.Length)
        {
            throw new InvalidDataException($"Class header at 0x{startAddress:X} exceeds buffer length.");
        }

        string magic = Encoding.ASCII.GetString(data.Slice(startAddress, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(startAddress + 4, 2));
        int size = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(startAddress + 6, 2));
        uint comp1 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(startAddress + 8, 4));
        uint comp2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(startAddress + 12, 4));
        return new ClassHeader(magic, count, size, comp1, comp2);
    }

    public static void WriteUtf16BeSubElement(Span<byte> dest, string tag, string? value)
    {
        Encoding.ASCII.GetBytes(tag.AsSpan(0, 4), dest.Slice(0, 4));
        dest[4] = 0x00;
        dest[5] = 0x02; // UTF-16BE without BOM

        if (!string.IsNullOrEmpty(value))
        {
            string clipped = value.Length <= MaxSubElementChars ? value : value[..MaxSubElementChars];
            Encoding.BigEndianUnicode.GetBytes(clipped, dest.Slice(6, SubElementSize - 6));
        }
    }

    public static string ReadUtf16BeSubElementValue(ReadOnlySpan<byte> subElement)
    {
        if (subElement.Length < SubElementSize)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> textSpan = subElement.Slice(6, SubElementSize - 6);
        int usedBytes = textSpan.Length;
        for (int i = 0; i + 1 < textSpan.Length; i += 2)
        {
            if (textSpan[i] == 0x00 && textSpan[i + 1] == 0x00)
            {
                usedBytes = i;
                break;
            }
        }

        return usedBytes == 0
            ? string.Empty
            : Encoding.BigEndianUnicode.GetString(textSpan.Slice(0, usedBytes)).TrimEnd('\0').Trim().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeGroupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim().Normalize(NormalizationForm.FormC);
    }

    public static int Align16(int value) => (value + 15) & ~15;

    public static string FormatTrackRelativePath(int trackId)
    {
        int folderIndex = trackId / 256;
        return $"OMGAUDIO/10F{folderIndex:X2}/1{trackId:X7}.OMA";
    }

    public static (AudioCodecId Codec, int BitrateKbps) DecodeFileProperties(uint filePropertiesBigEndian)
    {
        var codec = (AudioCodecId)((filePropertiesBigEndian >> 24) & 0xFF);
        uint param24 = filePropertiesBigEndian & 0x00FF_FFFF;

        int bitrateKbps = codec switch
        {
            AudioCodecId.Mp3 => DecodeMp3BitrateKbps(param24),
            AudioCodecId.Atrac3 => DecodeAtrac3BitrateKbps(param24),
            AudioCodecId.Atrac3Plus => DecodeAtrac3PlusBitrateKbps(param24),
            AudioCodecId.Aac or AudioCodecId.Wma => (int)((param24 >> 8) & 0x0FFF),
            AudioCodecId.LinearPcm => 1411,
            _ => 0
        };

        return (codec, bitrateKbps);
    }

    private static int DecodeMp3BitrateKbps(uint param24)
    {
        byte byte34 = (byte)((param24 >> 8) & 0xFF);
        int versionCode = (byte34 >> 6) & 0x03;
        int bitrateIdx = byte34 & 0x0F;
        if (bitrateIdx is 0 or 15)
        {
            return 0;
        }

        return versionCode == 3 ? Mpeg1L3BitratesKbps[bitrateIdx] : Mpeg2L3BitratesKbps[bitrateIdx];
    }

    private static int DecodeAtrac3BitrateKbps(uint param24)
    {
        int frameBytes = (int)(param24 & 0x3FF) * 8;
        return frameBytes switch
        {
            192 => 66,
            304 => 105,
            384 => 132,
            _ => frameBytes > 0 ? (int)Math.Round(frameBytes * 8.0 * 44100.0 / 1024.0 / 1000.0) : 0
        };
    }

    private static int DecodeAtrac3PlusBitrateKbps(uint param24)
    {
        int frameBytes = ((int)(param24 & 0x3FF) * 8) + 8;
        return frameBytes > 8 ? (int)Math.Round(frameBytes * 8.0 * 44100.0 / 2048.0 / 1000.0) : 0;
    }
}
