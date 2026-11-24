using System.Buffers.Binary;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Custom.NwmEngine;

/// <summary>
/// Clean-room binary packer, unpacker, and hardware constraint validator for Sony Walkman
/// .NWM screensaver files: NW-E400/E500/A600 (0xEC01), NW-E002/S20xF (0xEC02), and
/// NW-A1000/A1200/A3000 (0xD301).
/// </summary>
public sealed class NwmScreensaverPacker : IScreensaverPacker
{
    public const ushort DefaultTimingFlag1 = 30720; // 0x7800
    public const ushort DefaultTimingFlag2 = 12288; // 0x3000
    public const byte DefaultFrameFlag = 0x10;

    private const int E500RawWidth = 48;
    private const int E500RawHeight = 120;
    private const int E500VisibleWidth = 120;
    private const int E500VisibleHeight = 36;
    private const int E500FrameBytes = 720;

    private const int E002RawWidth = 16;
    private const int E002RawHeight = 108;
    private const int E002VisibleWidth = 108;
    private const int E002VisibleHeight = 16;
    private const int E002FrameBytes = 216;

    private const int A1000Size = 80;
    private const int A1000FrameBytes = 1600;
    private const int A1000ThumbWidth = 18;
    private const int A1000ThumbHeight = 18;
    private const int A1000ThumbAlignedWidth = 20;
    private const int A1000ThumbBytes = 90;

    public NwmValidationResult Validate(NwmProjectDto project, ScreenSpecification targetScreen)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targetScreen);

        var errors = new List<string>();

        if (project.Magic != targetScreen.Magic)
        {
            errors.Add($"Project magic 0x{(ushort)project.Magic:X4} does not match target screen magic 0x{(ushort)targetScreen.Magic:X4}.");
        }

        if (string.IsNullOrEmpty(project.Name) || !IsPureAscii(project.Name))
        {
            errors.Add("Screensaver name must be non-empty pure ASCII.");
        }

        if (!IsPureAscii(project.Author))
        {
            errors.Add("Screensaver author must be pure ASCII.");
        }

        if (project.Timeline.Count == 0)
        {
            errors.Add("Timeline must contain at least one frame.");
        }

        if (project.UniqueBitmaps.Count == 0)
        {
            errors.Add("Project must contain at least one bitmap.");
        }

        if (project.BitsPerPixel != targetScreen.BitsPerPixel)
        {
            errors.Add($"Project bits-per-pixel ({project.BitsPerPixel}) does not match hardware ({targetScreen.BitsPerPixel} bpp).");
        }

        int expectedLogicalBytes = (project.Width * project.Height * Math.Max(1, project.BitsPerPixel) + 7) / 8;
        var canonicalHashes = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < project.UniqueBitmaps.Count; i++)
        {
            var bmp = project.UniqueBitmaps[i];
            if (bmp is null || bmp.Length == 0)
            {
                errors.Add($"Bitmap [{i}] is empty.");
                continue;
            }

            if (bmp.Length != expectedLogicalBytes && bmp.Length != targetScreen.BytesPerFrame)
            {
                errors.Add($"Bitmap [{i}] has {bmp.Length} bytes, expected {expectedLogicalBytes} bytes ({project.Width}x{project.Height}@{project.BitsPerPixel}bpp) or {targetScreen.BytesPerFrame} raw bytes.");
                continue;
            }

            try
            {
                byte[] hwChunk = ConvertBitmapToHardwareFormat(project, bmp);
                canonicalHashes.Add(Convert.ToHexString(hwChunk));
            }
            catch (Exception ex)
            {
                errors.Add($"Bitmap [{i}] conversion error: {ex.Message}");
            }
        }

        foreach (var entry in project.Timeline)
        {
            if (entry.BitmapIndex >= project.UniqueBitmaps.Count)
            {
                errors.Add($"Timeline frame references bitmap index {entry.BitmapIndex}, which exceeds UniqueBitmaps count ({project.UniqueBitmaps.Count}).");
                break;
            }
        }

        int uniqueCount = Math.Max(1, canonicalHashes.Count);
        if (uniqueCount > targetScreen.MaxUniqueBitmaps)
        {
            errors.Add($"Unique bitmap count ({uniqueCount}) exceeds hardware cap ({targetScreen.MaxUniqueBitmaps}).");
        }

        int estimatedBytes = EstimateByteSize(project, uniqueCount, targetScreen.BytesPerFrame);
        if (estimatedBytes > targetScreen.MaxFileSizeBytes)
        {
            errors.Add($"Estimated file size ({estimatedBytes:N0} B) exceeds hardware cap ({targetScreen.MaxFileSizeBytes:N0} B).");
        }

        return new NwmValidationResult(errors.Count == 0, estimatedBytes, errors);
    }

    public byte[] Pack(NwmProjectDto project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Timeline.Count == 0)
        {
            throw new ArgumentException("Screensaver timeline cannot be empty.", nameof(project));
        }

        if (project.UniqueBitmaps.Count == 0)
        {
            throw new ArgumentException("Screensaver bitmap list cannot be empty.", nameof(project));
        }

        // Convert each source bitmap into physical hardware-format chunk and deduplicate automatically.
        var hardwareChunks = new List<byte[]>(project.UniqueBitmaps.Count);
        var uniqueByKey = new Dictionary<string, byte>(StringComparer.Ordinal);
        var remap = new byte[project.UniqueBitmaps.Count];

        for (int i = 0; i < project.UniqueBitmaps.Count; i++)
        {
            byte[] chunk = ConvertBitmapToHardwareFormat(project, project.UniqueBitmaps[i]);
            string key = Convert.ToHexString(chunk);
            if (!uniqueByKey.TryGetValue(key, out byte existingIdx))
            {
                if (hardwareChunks.Count > 255)
                {
                    throw new InvalidOperationException("Unique bitmap count exceeds uint8 .nwm index capacity (255).");
                }

                existingIdx = (byte)hardwareChunks.Count;
                uniqueByKey[key] = existingIdx;
                hardwareChunks.Add(chunk);
            }

            remap[i] = existingIdx;
        }

        var remappedTimeline = new List<NwmFrameEntry>(project.Timeline.Count);
        for (int i = 0; i < project.Timeline.Count; i++)
        {
            var entry = project.Timeline[i];
            if (entry.BitmapIndex >= remap.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(project),
                    $"Timeline entry [{i}] references bitmap index {entry.BitmapIndex} >= {remap.Length}.");
            }

            remappedTimeline.Add(new NwmFrameEntry(remap[entry.BitmapIndex], entry.Flags));
        }

        return project.Magic switch
        {
            ScreensaverMagic.NwE400_0xEC01 => PackE500(project, remappedTimeline, hardwareChunks),
            ScreensaverMagic.NwE002_0xEC02 => PackE002(project, remappedTimeline, hardwareChunks),
            ScreensaverMagic.NwA1000_0xD301 => PackA1000(project, remappedTimeline, hardwareChunks),
            _ => throw new NotSupportedException($"Unsupported .nwm screensaver magic: {project.Magic}.")
        };
    }

    public NwmProjectDto Unpack(ReadOnlySpan<byte> nwmBytes)
    {
        if (nwmBytes.Length < 10)
        {
            throw new InvalidDataException($"File too small to be a valid .nwm container ({nwmBytes.Length} bytes).");
        }

        ushort magicRaw = BinaryPrimitives.ReadUInt16BigEndian(nwmBytes[..2]);
        ushort frameCount = BinaryPrimitives.ReadUInt16BigEndian(nwmBytes[2..4]);
        ushort bitmapCount = BinaryPrimitives.ReadUInt16BigEndian(nwmBytes[4..6]);

        var magic = (ScreensaverMagic)magicRaw;
        return magic switch
        {
            ScreensaverMagic.NwE400_0xEC01 => UnpackE500(nwmBytes, frameCount, bitmapCount),
            ScreensaverMagic.NwE002_0xEC02 => UnpackE002(nwmBytes, frameCount, bitmapCount),
            ScreensaverMagic.NwA1000_0xD301 => UnpackA1000(nwmBytes, frameCount, bitmapCount),
            _ => throw new InvalidDataException($"Unknown .nwm magic 0x{magicRaw:X4}.")
        };
    }

    /// <summary>
    /// Reorders a row-major 48x120 1bpp bitmap (720 bytes) into Sony 0xEC01 3-stripe format:
    /// block0(cols 32..47) || block1(cols 16..31) || block2(cols 0..15), with rows reversed (119 - row).
    /// </summary>
    public static byte[] MangleE500Bitmap(ReadOnlySpan<byte> unmangled48x120)
    {
        if (unmangled48x120.Length != E500FrameBytes)
        {
            throw new ArgumentException($"Expected 720 bytes for 48x120 bitmap, got {unmangled48x120.Length}.", nameof(unmangled48x120));
        }

        byte[] mangled = new byte[E500FrameBytes];
        Span<byte> block0 = mangled.AsSpan(0, 240);
        Span<byte> block1 = mangled.AsSpan(240, 240);
        Span<byte> block2 = mangled.AsSpan(480, 240);

        for (int row = 0; row < E500RawHeight; row++)
        {
            int srcOff = row * 6;
            int dstOff = (E500RawHeight - 1 - row) * 2;

            unmangled48x120.Slice(srcOff, 2).CopyTo(block2.Slice(dstOff, 2));
            unmangled48x120.Slice(srcOff + 2, 2).CopyTo(block1.Slice(dstOff, 2));
            unmangled48x120.Slice(srcOff + 4, 2).CopyTo(block0.Slice(dstOff, 2));
        }

        return mangled;
    }

    /// <summary>
    /// Restores a Sony 0xEC01 720-byte 3-stripe mangled frame back into row-major 48x120 1bpp order.
    /// </summary>
    public static byte[] UnmangleE500Bitmap(ReadOnlySpan<byte> mangled720)
    {
        if (mangled720.Length != E500FrameBytes)
        {
            throw new ArgumentException($"Expected 720 bytes for mangled bitmap, got {mangled720.Length}.", nameof(mangled720));
        }

        ReadOnlySpan<byte> block0 = mangled720[..240];
        ReadOnlySpan<byte> block1 = mangled720.Slice(240, 240);
        ReadOnlySpan<byte> block2 = mangled720.Slice(480, 240);

        byte[] raw = new byte[E500FrameBytes];
        for (int row = 0; row < E500RawHeight; row++)
        {
            int srcOff = row * 2;
            int dstOff = (E500RawHeight - 1 - row) * 6;

            block2.Slice(srcOff, 2).CopyTo(raw.AsSpan(dstOff, 2));
            block1.Slice(srcOff, 2).CopyTo(raw.AsSpan(dstOff + 2, 2));
            block0.Slice(srcOff, 2).CopyTo(raw.AsSpan(dstOff + 4, 2));
        }

        return raw;
    }

    /// <summary>
    /// Converts a logical upright 120x36 1bpp MSB-first bitmap (540 bytes) into a raw 48x120
    /// unmangled bitmap (720 bytes) using the hardware-verified coordinates:
    /// cols px = 0..11 blank, visible pixel (col, row) -> (px = 47 - row, py = 119 - col).
    /// </summary>
    public static byte[] LogicalLandscape120x36ToRaw48x120(ReadOnlySpan<byte> logical120x36Packed)
    {
        if (logical120x36Packed.Length != 540)
        {
            throw new ArgumentException($"Expected 540 bytes for 120x36 1bpp bitmap, got {logical120x36Packed.Length}.", nameof(logical120x36Packed));
        }

        byte[] raw = new byte[E500FrameBytes];
        for (int row = 0; row < E500VisibleHeight; row++)
        {
            int px = 47 - row;
            int pxByte = px >> 3;
            byte pxMask = (byte)(0x80 >> (px & 7));

            for (int col = 0; col < E500VisibleWidth; col++)
            {
                int srcBitIdx = row * E500VisibleWidth + col;
                bool lit = (logical120x36Packed[srcBitIdx >> 3] & (0x80 >> (srcBitIdx & 7))) != 0;
                if (lit)
                {
                    int py = 119 - col;
                    raw[py * 6 + pxByte] |= pxMask;
                }
            }
        }

        return raw;
    }

    /// <summary>
    /// Converts a raw 48x120 1bpp bitmap (720 bytes) back to an upright logical 120x36 1bpp bitmap (540 bytes).
    /// </summary>
    public static byte[] Raw48x120ToLogicalLandscape120x36(ReadOnlySpan<byte> raw48x120)
    {
        if (raw48x120.Length != E500FrameBytes)
        {
            throw new ArgumentException($"Expected 720 bytes for 48x120 bitmap, got {raw48x120.Length}.", nameof(raw48x120));
        }

        byte[] logical = new byte[540];
        for (int row = 0; row < E500VisibleHeight; row++)
        {
            int px = 47 - row;
            int pxByte = px >> 3;
            byte pxMask = (byte)(0x80 >> (px & 7));

            for (int col = 0; col < E500VisibleWidth; col++)
            {
                int py = 119 - col;
                bool lit = (raw48x120[py * 6 + pxByte] & pxMask) != 0;
                if (lit)
                {
                    int dstBitIdx = row * E500VisibleWidth + col;
                    logical[dstBitIdx >> 3] |= (byte)(0x80 >> (dstBitIdx & 7));
                }
            }
        }

        return logical;
    }

    private static byte[] ConvertBitmapToHardwareFormat(NwmProjectDto project, byte[] bitmap)
    {
        switch (project.Magic)
        {
            case ScreensaverMagic.NwE400_0xEC01:
                if (project.Width == E500VisibleWidth && project.Height == E500VisibleHeight && bitmap.Length == 540)
                {
                    return MangleE500Bitmap(LogicalLandscape120x36ToRaw48x120(bitmap));
                }

                if (project.Width == 36 && project.Height == 120 && bitmap.Length == 540)
                {
                    return MangleE500Bitmap(LogicalPortrait36x120ToRaw48x120(bitmap));
                }

                if (project.Width == E500RawWidth && project.Height == E500RawHeight && bitmap.Length == E500FrameBytes)
                {
                    return MangleE500Bitmap(bitmap);
                }

                throw new ArgumentException($"Unsupported 0xEC01 geometry {project.Width}x{project.Height} ({bitmap.Length} bytes).");

            case ScreensaverMagic.NwE002_0xEC02:
                if (project.Width == E002VisibleWidth && project.Height == E002VisibleHeight && bitmap.Length == E002FrameBytes)
                {
                    return Logical108x16ToRaw16x108(bitmap);
                }

                if (project.Width == E002RawWidth && project.Height == E002RawHeight && bitmap.Length == E002FrameBytes)
                {
                    return bitmap.ToArray();
                }

                throw new ArgumentException($"Unsupported 0xEC02 geometry {project.Width}x{project.Height} ({bitmap.Length} bytes).");

            case ScreensaverMagic.NwA1000_0xD301:
                if (project.Width == A1000Size && project.Height == A1000Size && bitmap.Length == A1000FrameBytes)
                {
                    return bitmap.ToArray();
                }

                throw new ArgumentException($"Unsupported 0xD301 geometry {project.Width}x{project.Height} ({bitmap.Length} bytes).");

            default:
                throw new NotSupportedException($"Unsupported magic {project.Magic}.");
        }
    }

    private static byte[] LogicalPortrait36x120ToRaw48x120(ReadOnlySpan<byte> portrait36x120)
    {
        byte[] raw = new byte[E500FrameBytes];
        for (int y = 0; y < E500RawHeight; y++)
        {
            for (int x = 0; x < 36; x++)
            {
                int srcBitIdx = y * 36 + x;
                bool lit = (portrait36x120[srcBitIdx >> 3] & (0x80 >> (srcBitIdx & 7))) != 0;
                if (lit)
                {
                    int px = 47 - x;
                    raw[y * 6 + (px >> 3)] |= (byte)(0x80 >> (px & 7));
                }
            }
        }

        return raw;
    }

    private static byte[] Logical108x16ToRaw16x108(ReadOnlySpan<byte> logical108x16)
    {
        // Equivalent to 90 CCW: (col, row) -> (px = row, py = 107 - col)
        byte[] raw = new byte[E002FrameBytes];
        for (int row = 0; row < E002VisibleHeight; row++)
        {
            for (int col = 0; col < E002VisibleWidth; col++)
            {
                int srcBit = row * E002VisibleWidth + col;
                bool lit = (logical108x16[srcBit >> 3] & (0x80 >> (srcBit & 7))) != 0;
                if (lit)
                {
                    int px = row;
                    int py = (E002VisibleWidth - 1) - col;
                    int dstBit = py * E002RawWidth + px;
                    raw[dstBit >> 3] |= (byte)(0x80 >> (dstBit & 7));
                }
            }
        }

        return raw;
    }

    private static byte[] Raw16x108ToLogical108x16(ReadOnlySpan<byte> raw16x108)
    {
        // Inverse of 90 CCW (270 CCW / 90 CW): (px, py) -> (col = 107 - py, row = px)
        byte[] logical = new byte[E002FrameBytes];
        for (int py = 0; py < E002RawHeight; py++)
        {
            int col = (E002VisibleWidth - 1) - py;
            for (int px = 0; px < E002RawWidth; px++)
            {
                int srcBit = py * E002RawWidth + px;
                bool lit = (raw16x108[srcBit >> 3] & (0x80 >> (srcBit & 7))) != 0;
                if (lit)
                {
                    int row = px;
                    int dstBit = row * E002VisibleWidth + col;
                    logical[dstBit >> 3] |= (byte)(0x80 >> (dstBit & 7));
                }
            }
        }

        return logical;
    }

    private static byte[] PackE500(NwmProjectDto project, List<NwmFrameEntry> timeline, List<byte[]> bitmaps)
    {
        byte[] nameBytes = EncodeStrictAscii(project.Name, nameof(project.Name));
        byte[] authorBytes = EncodeStrictAscii(project.Author, nameof(project.Author));
        ushort dataOffset = checked((ushort)(16 + nameBytes.Length + authorBytes.Length));
        int totalLen = dataOffset + timeline.Count * 2 + bitmaps.Count * E500FrameBytes;

        byte[] output = new byte[totalLen];
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(0, 2), (ushort)ScreensaverMagic.NwE400_0xEC01);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2, 2), checked((ushort)timeline.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), checked((ushort)bitmaps.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), dataOffset);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), checked((ushort)nameBytes.Length));

        int pos = 10;
        nameBytes.CopyTo(output, pos);
        pos += nameBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos, 2), checked((ushort)authorBytes.Length));
        pos += 2;
        authorBytes.CopyTo(output, pos);
        pos += authorBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos, 2), project.TimingFlag1);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos + 2, 2), project.TimingFlag2);
        pos += 4;

        foreach (var frame in timeline)
        {
            output[pos++] = frame.BitmapIndex;
            output[pos++] = frame.Flags;
        }

        foreach (var bmp in bitmaps)
        {
            bmp.CopyTo(output, pos);
            pos += bmp.Length;
        }

        return output;
    }

    private static byte[] PackE002(NwmProjectDto project, List<NwmFrameEntry> timeline, List<byte[]> bitmaps)
    {
        byte[] nameBytes = EncodeStrictAscii(project.Name, nameof(project.Name));
        ushort dataOffset = checked((ushort)(10 + nameBytes.Length));
        int totalLen = dataOffset + timeline.Count * 2 + bitmaps.Count * E002FrameBytes;

        byte[] output = new byte[totalLen];
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(0, 2), (ushort)ScreensaverMagic.NwE002_0xEC02);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2, 2), checked((ushort)timeline.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), checked((ushort)bitmaps.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), dataOffset);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), checked((ushort)nameBytes.Length));

        int pos = 10;
        nameBytes.CopyTo(output, pos);
        pos += nameBytes.Length;

        foreach (var frame in timeline)
        {
            output[pos++] = frame.BitmapIndex;
            output[pos++] = frame.Flags;
        }

        foreach (var bmp in bitmaps)
        {
            bmp.CopyTo(output, pos);
            pos += bmp.Length;
        }

        return output;
    }

    private static byte[] PackA1000(NwmProjectDto project, List<NwmFrameEntry> timeline, List<byte[]> bitmaps)
    {
        byte[] thumbBytes = BuildA1000Thumbnail(bitmaps[timeline[0].BitmapIndex]);
        int prePadTimelineEnd = 16 + A1000ThumbBytes + 6 + timeline.Count * 2;
        int timelinePad = (16 - (prePadTimelineEnd % 16)) % 16;
        int totalLen = prePadTimelineEnd + timelinePad + bitmaps.Count * A1000FrameBytes;

        byte[] output = new byte[totalLen];
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(0, 2), (ushort)ScreensaverMagic.NwA1000_0xD301);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2, 2), checked((ushort)timeline.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), checked((ushort)bitmaps.Count));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), 0x0010);

        int pos = 16;
        thumbBytes.CopyTo(output, pos);
        pos += A1000ThumbBytes + 6;

        foreach (var frame in timeline)
        {
            output[pos++] = frame.BitmapIndex;
            output[pos++] = frame.Flags;
        }

        pos += timelinePad;
        foreach (var bmp in bitmaps)
        {
            bmp.CopyTo(output, pos);
            pos += bmp.Length;
        }

        return output;
    }

    private static byte[] BuildA1000Thumbnail(ReadOnlySpan<byte> frame80x80_2bpp)
    {
        // Downsample 80x80 (2bpp) to 18x18 (aligned to 20px width = 90 bytes)
        byte[] thumb = new byte[A1000ThumbBytes];
        for (int ty = 0; ty < A1000ThumbHeight; ty++)
        {
            int sy = (ty * A1000Size) / A1000ThumbHeight;
            for (int tx = 0; tx < A1000ThumbWidth; tx++)
            {
                int sx = (tx * A1000Size) / A1000ThumbWidth;
                int srcPxIdx = sy * A1000Size + sx;
                int srcByte = frame80x80_2bpp[srcPxIdx >> 2];
                int srcShift = (3 - (srcPxIdx & 3)) * 2;
                int val = (srcByte >> srcShift) & 0x03;

                int dstPxIdx = ty * A1000ThumbAlignedWidth + tx;
                int dstShift = (3 - (dstPxIdx & 3)) * 2;
                thumb[dstPxIdx >> 2] |= (byte)(val << dstShift);
            }
        }

        return thumb;
    }

    private static NwmProjectDto UnpackE500(ReadOnlySpan<byte> data, ushort frameCount, ushort bitmapCount)
    {
        ushort dataOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        int pos = 10;
        if (pos + nameLen + 2 > data.Length)
        {
            throw new InvalidDataException("Truncated 0xEC01 .nwm header reading Name.");
        }

        string name = Encoding.ASCII.GetString(data.Slice(pos, nameLen));
        pos += nameLen;

        ushort authorLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        if (pos + authorLen + 4 > data.Length)
        {
            throw new InvalidDataException("Truncated 0xEC01 .nwm header reading Author.");
        }

        string author = Encoding.ASCII.GetString(data.Slice(pos, authorLen));
        pos += authorLen;

        ushort u1 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        ushort u2 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));

        int bodyNeeded = dataOffset + frameCount * 2 + bitmapCount * E500FrameBytes;
        if (dataOffset < pos + 4 || data.Length < bodyNeeded)
        {
            throw new InvalidDataException("Truncated 0xEC01 .nwm body.");
        }

        var timeline = ParseTimeline(data.Slice(dataOffset, frameCount * 2), bitmapCount);
        int bmpStart = dataOffset + frameCount * 2;

        var bitmaps = new List<byte[]>(bitmapCount);
        for (int i = 0; i < bitmapCount; i++)
        {
            ReadOnlySpan<byte> mangled = data.Slice(bmpStart + i * E500FrameBytes, E500FrameBytes);
            byte[] raw = UnmangleE500Bitmap(mangled);
            bitmaps.Add(Raw48x120ToLogicalLandscape120x36(raw));
        }

        return new NwmProjectDto(
            ScreensaverMagic.NwE400_0xEC01,
            name,
            author,
            u1,
            u2,
            E500VisibleWidth,
            E500VisibleHeight,
            1,
            timeline,
            bitmaps);
    }

    private static NwmProjectDto UnpackE002(ReadOnlySpan<byte> data, ushort frameCount, ushort bitmapCount)
    {
        ushort dataOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        if (10 + nameLen > data.Length || dataOffset < 10 + nameLen)
        {
            throw new InvalidDataException("Truncated 0xEC02 .nwm header.");
        }

        string name = Encoding.ASCII.GetString(data.Slice(10, nameLen));
        int bodyNeeded = dataOffset + frameCount * 2 + bitmapCount * E002FrameBytes;
        if (data.Length < bodyNeeded)
        {
            throw new InvalidDataException("Truncated 0xEC02 .nwm body.");
        }

        var timeline = ParseTimeline(data.Slice(dataOffset, frameCount * 2), bitmapCount);
        int bmpStart = dataOffset + frameCount * 2;

        var bitmaps = new List<byte[]>(bitmapCount);
        for (int i = 0; i < bitmapCount; i++)
        {
            bitmaps.Add(Raw16x108ToLogical108x16(data.Slice(bmpStart + i * E002FrameBytes, E002FrameBytes)));
        }

        return new NwmProjectDto(
            ScreensaverMagic.NwE002_0xEC02,
            name,
            "SONY",
            DefaultTimingFlag1,
            DefaultTimingFlag2,
            E002VisibleWidth,
            E002VisibleHeight,
            1,
            timeline,
            bitmaps);
    }

    private static NwmProjectDto UnpackA1000(ReadOnlySpan<byte> data, ushort frameCount, ushort bitmapCount)
    {
        ushort dataAreaStart = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        int timelinePos = dataAreaStart + A1000ThumbBytes + 6;
        int timelineEnd = timelinePos + frameCount * 2;
        int bmpStart = timelineEnd + ((16 - (timelineEnd % 16)) % 16);

        if (data.Length < bmpStart + bitmapCount * A1000FrameBytes)
        {
            throw new InvalidDataException("Truncated 0xD301 .nwm body.");
        }

        var timeline = ParseTimeline(data.Slice(timelinePos, frameCount * 2), bitmapCount);
        var bitmaps = new List<byte[]>(bitmapCount);
        for (int i = 0; i < bitmapCount; i++)
        {
            bitmaps.Add(data.Slice(bmpStart + i * A1000FrameBytes, A1000FrameBytes).ToArray());
        }

        return new NwmProjectDto(
            ScreensaverMagic.NwA1000_0xD301,
            "Screensaver",
            "SONY",
            DefaultTimingFlag1,
            DefaultTimingFlag2,
            A1000Size,
            A1000Size,
            2,
            timeline,
            bitmaps);
    }

    private static List<NwmFrameEntry> ParseTimeline(ReadOnlySpan<byte> bytes, ushort bitmapCount)
    {
        var list = new List<NwmFrameEntry>(bytes.Length / 2);
        for (int i = 0; i < bytes.Length; i += 2)
        {
            byte idx = bytes[i];
            byte flags = bytes[i + 1];
            if (idx >= bitmapCount)
            {
                throw new InvalidDataException($"Frame {i / 2} references invalid bitmap index {idx} >= {bitmapCount}.");
            }

            list.Add(new NwmFrameEntry(idx, flags));
        }

        return list;
    }

    private static int EstimateByteSize(NwmProjectDto project, int uniqueBitmapCount, int frameBytes)
    {
        int headerBytes = project.Magic switch
        {
            ScreensaverMagic.NwE400_0xEC01 => 16 + Encoding.ASCII.GetByteCount(project.Name) + Encoding.ASCII.GetByteCount(project.Author),
            ScreensaverMagic.NwE002_0xEC02 => 10 + Encoding.ASCII.GetByteCount(project.Name),
            ScreensaverMagic.NwA1000_0xD301 => 16 + A1000ThumbBytes + 6,
            _ => 16
        };

        int body = headerBytes + project.Timeline.Count * 2;
        if (project.Magic == ScreensaverMagic.NwA1000_0xD301)
        {
            body += (16 - (body % 16)) % 16;
        }

        return body + uniqueBitmapCount * frameBytes;
    }

    private static bool IsPureAscii(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] EncodeStrictAscii(string value, string paramName)
    {
        if (!IsPureAscii(value))
        {
            throw new ArgumentException($"Field '{paramName}' contains non-ASCII characters; Sony .nwm headers require pure ASCII.", paramName);
        }

        return Encoding.ASCII.GetBytes(value);
    }
}
