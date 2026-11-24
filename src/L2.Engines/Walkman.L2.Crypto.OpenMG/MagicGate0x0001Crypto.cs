using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Clean-room implementation of Sony OpenMG 0x0001 MagicGate cryptography:
/// EKB traversal (00010021.DAT and embedded 0x0001001d), KEYRING derivation,
/// continuous DES-CBC payload cipher, GEOB (OMG_ULINF / OMG_BKLSI / OMG_OLINF) framing,
/// and MACLIST0.DAT DES-CBC-MAC authentication.
/// </summary>
internal static class MagicGate0x0001Crypto
{
    private const int Ea3DescriptorSize = 96;

    // Public OpenMG Leaf Key Table (k0, k1 little-endian pairs).
    private static readonly (ulong K0, ulong K1)[] LeafTable =
    [
        (0xd79e8283acea4620UL, 0x7a9762f445afd0d8UL),
        (0x354d60a60b8c79f1UL, 0x584e1cde00b07aeeUL),
        (0x1573cd93da7df623UL, 0x47f98d79620dd535UL)
    ];

    private static readonly byte[] KeyringHeaderPrefix = Encoding.ASCII.GetBytes("KEYRING     ");
    private static readonly byte[] UlinfDescriptor20 = Convert.FromHexString("0010000300280001000300000000000000010021");
    private static readonly byte[] CidTagPrefix20 = Convert.FromHexString("2143494420202020202020200010000200000000");
    private static readonly byte[] BindPTag32 = Convert.FromHexString("42494e445f5020202020202000080001636f756e742020200000000000000001");
    private static readonly byte[] BindSTag32 = Convert.FromHexString("42494e445f5320202020202000080001636f756e742020200000000000000001");
    private static readonly byte[] RefIdUlinf32 = Convert.FromHexString("2152454649442020202020200010000101010303000000000000000000000000");

    public static MagicGate0x0001WrapResult WrapAtracToOma(
        ReadOnlySpan<byte> rawAtracFrames,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        int trackId,
        uint? hardwareDeviceKey,
        OmaEncryptionMode encryptionMode,
        Ea3TagMetadata tags,
        int ea3BlockSize,
        byte[]? contentId20,
        byte[] baseTagFrames,
        ReadOnlySpan<byte> ekb00010021Bytes = default)
    {
        if (rawAtracFrames.Length == 0 || (rawAtracFrames.Length % 8 != 0 && encryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001))
        {
            throw new InvalidDataException("ATRAC payload length must be a positive multiple of 8 bytes for MagicGate DES-CBC framing.");
        }

        byte[] cid20 = contentId20 is { Length: 20 }
            ? contentId20.ToArray()
            : GenerateContentId20(trackId, codecParams24);

        byte[] lsiKeyring48 = new byte[48];
        cid20.CopyTo(lsiKeyring48, 4);

        if (encryptionMode != OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            byte[] plainHeader = BuildEa3AndDescriptor(
                baseTagFrames,
                leadingGeobs: ReadOnlySpan<byte>.Empty,
                trailingGeobs: ReadOnlySpan<byte>.Empty,
                requestedEa3BlockSize: Math.Max(1024, ea3BlockSize),
                ea3Version: 0x02,
                encryptionMode: encryptionMode,
                cid20: cid20,
                codecId: codecId,
                codecParams24: codecParams24,
                durationMs: durationMs,
                frameCount: frameCount,
                iv8: ReadOnlySpan<byte>.Empty);

            byte[] output = new byte[plainHeader.Length + rawAtracFrames.Length];
            plainHeader.CopyTo(output, 0);
            rawAtracFrames.CopyTo(output.AsSpan(plainHeader.Length));

            if (encryptionMode == OmaEncryptionMode.HardwareXor_0xFFFE)
            {
                if (!hardwareDeviceKey.HasValue)
                {
                    throw new InvalidOperationException("HardwareDeviceKey is required for 0xFFFE encryption.");
                }

                uint xorKey = unchecked((0x2465u + (uint)trackId * 0x5296E435u) ^ hardwareDeviceKey.Value);
                ApplyXorInPlace(output.AsSpan(plainHeader.Length), xorKey);
            }

            return new MagicGate0x0001WrapResult(output, cid20, lsiKeyring48, null);
        }

        if (ekb00010021Bytes.Length < 140)
        {
            throw new InvalidOperationException("Device OMGAUDIO/00010021.DAT EKB (>= 140 bytes) is required for 0x0001 MagicGate wrapping.");
        }

        // Derive root EKB key r_21 from the connected Walkman's 00010021.DAT block 1
        byte[] r21 = DeriveEkbRootKey(ekb00010021Bytes, leafIndex: 2, blockIndex: 1);

        byte[] kc = RandomNumberGenerator.GetBytes(8);
        byte[] iv = RandomNumberGenerator.GetBytes(8);
        byte[] mUl = RandomNumberGenerator.GetBytes(8);
        byte[] tokenUl = RandomNumberGenerator.GetBytes(8);
        byte[] returnSRid = RandomNumberGenerator.GetBytes(16);

        byte[] sUl = DesCore.EncryptBlock(mUl, new byte[8]);

        byte[] mUlEnc = TripleDesEcbEncrypt(r21, mUl);
        byte[] eUlEnc = DesCore.DecryptBlock(mUl, kc);

        byte[] ulinfBody = BuildUlinfBody(mUlEnc, eUlEnc, tokenUl, cid20, returnSRid);

        byte[] maclistEntry8 = DesCbcMac(sUl, ulinfBody.AsSpan(64, 176));

        using var leadingGeobStream = new MemoryStream(320);
        WriteGeobFrame(leadingGeobStream, "OMG_ULINF", ulinfBody);
        byte[] leadingGeobs = leadingGeobStream.ToArray();

        byte[] mgHeader = BuildEa3AndDescriptor(
            baseTagFrames,
            leadingGeobs,
            trailingGeobs: ReadOnlySpan<byte>.Empty,
            requestedEa3BlockSize: Math.Max(2048, ea3BlockSize),
            ea3Version: 0x01,
            encryptionMode: OmaEncryptionMode.MagicGateLsi_0x0001,
            cid20: cid20,
            codecId: codecId,
            codecParams24: codecParams24,
            durationMs: durationMs,
            frameCount: frameCount,
            iv8: iv);

        byte[] result = new byte[mgHeader.Length + rawAtracFrames.Length];
        mgHeader.CopyTo(result, 0);
        DesCbcEncryptContinuous(kc, iv, rawAtracFrames, result.AsSpan(mgHeader.Length));

        return new MagicGate0x0001WrapResult(result, cid20, lsiKeyring48, maclistEntry8);
    }

    public static SignedMagicGateSlotTemplate? ExtractSignedSlotTemplate(
        ReadOnlySpan<byte> omaBytes,
        int trackId,
        ReadOnlySpan<byte> ekb00010021Bytes)
    {
        if (omaBytes.Length < 10 + Ea3DescriptorSize ||
            omaBytes[0] != (byte)'e' ||
            omaBytes[1] != (byte)'a' ||
            omaBytes[2] != (byte)'3')
        {
            return null;
        }

        int bodySize =
            ((omaBytes[6] & 0x7F) << 21) |
            ((omaBytes[7] & 0x7F) << 14) |
            ((omaBytes[8] & 0x7F) << 7) |
            (omaBytes[9] & 0x7F);
        int ea3TotalSize = 10 + bodySize;
        if (ea3TotalSize + Ea3DescriptorSize > omaBytes.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> ea3 = omaBytes.Slice(ea3TotalSize, Ea3DescriptorSize);
        if (ea3[0] != (byte)'E' || ea3[1] != (byte)'A' || ea3[2] != (byte)'3')
        {
            return null;
        }

        var encMode = (OmaEncryptionMode)BinaryPrimitives.ReadUInt16BigEndian(ea3.Slice(6, 2));
        if (encMode != OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            return null;
        }

        byte[]? ulinf240 = null;
        byte[]? bklsi428 = null;
        byte[]? olinf64 = null;

        int pos = 10;
        ReadOnlySpan<byte> tagRegion = omaBytes.Slice(0, ea3TotalSize);
        while (pos + 10 <= tagRegion.Length)
        {
            if (tagRegion[pos] == 0x00)
            {
                break;
            }

            int frameSize = BinaryPrimitives.ReadInt32BigEndian(tagRegion.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > tagRegion.Length)
            {
                break;
            }

            if (tagRegion[pos] == (byte)'G' &&
                tagRegion[pos + 1] == (byte)'E' &&
                tagRegion[pos + 2] == (byte)'O' &&
                tagRegion[pos + 3] == (byte)'B')
            {
                ReadOnlySpan<byte> body = tagRegion.Slice(pos + 10, frameSize);
                if (TryExtractGeobData(body, "OMG_ULINF", out var ulSpan) && ulSpan.Length >= 240)
                {
                    ulinf240 = ulSpan.Slice(0, 240).ToArray();
                }
                else if (TryExtractGeobData(body, "OMG_BKLSI", out var bkSpan) && bkSpan.Length >= 428)
                {
                    bklsi428 = bkSpan.Slice(0, 428).ToArray();
                }
                else if (TryExtractGeobData(body, "OMG_OLINF", out var olSpan) && olSpan.Length >= 64)
                {
                    olinf64 = olSpan.Slice(0, 64).ToArray();
                }
            }

            pos += 10 + frameSize;
        }

        if (ulinf240 is null || ekb00010021Bytes.Length < 140)
        {
            return null;
        }

        byte[] r21 = DeriveEkbRootKey(ekb00010021Bytes, leafIndex: 2, blockIndex: 1);
        byte[] mUl = TripleDesEcbDecrypt(r21, ulinf240.AsSpan(32, 8));
        byte[] sUl = DesCore.EncryptBlock(mUl, new byte[8]);
        byte[] mac8 = DesCbcMac(sUl, ulinf240.AsSpan(64, 176));

        return new SignedMagicGateSlotTemplate(
            TrackId: trackId,
            Ulinf240: ulinf240,
            Bklsi428: bklsi428,
            Olinf64: olinf64,
            ContentId20: ea3.Slice(12, 20).ToArray(),
            Iv8: ea3.Slice(0x58, 8).ToArray(),
            MaclistEntry8: mac8);
    }

    public static MagicGate0x0001WrapResult WrapAtracIntoSignedSlot(
        ReadOnlySpan<byte> rawAtracFrames,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        SignedMagicGateSlotTemplate slotTemplate,
        byte[] baseTagFrames,
        ReadOnlySpan<byte> ekb00010021Bytes,
        int ea3BlockSize)
    {
        ArgumentNullException.ThrowIfNull(slotTemplate);
        if (rawAtracFrames.Length == 0 || rawAtracFrames.Length % 8 != 0)
        {
            throw new InvalidDataException("ATRAC payload length must be a positive multiple of 8 bytes for MagicGate DES-CBC framing.");
        }

        if (slotTemplate.Ulinf240 is not { Length: 240 })
        {
            throw new InvalidDataException("Signed slot template OMG_ULINF must be exactly 240 bytes.");
        }

        if (slotTemplate.ContentId20 is not { Length: 20 })
        {
            throw new InvalidDataException("Signed slot template ContentId20 must be exactly 20 bytes.");
        }

        if (slotTemplate.Iv8 is not { Length: 8 })
        {
            throw new InvalidDataException("Signed slot template Iv8 must be exactly 8 bytes.");
        }

        if (ekb00010021Bytes.Length < 140)
        {
            throw new InvalidOperationException("Device OMGAUDIO/00010021.DAT EKB (>= 140 bytes) is required for signed-slot 0x0001 wrapping.");
        }

        byte[] r21 = DeriveEkbRootKey(ekb00010021Bytes, leafIndex: 2, blockIndex: 1);
        byte[] mUl = TripleDesEcbDecrypt(r21, slotTemplate.Ulinf240.AsSpan(32, 8));
        byte[] sUl = DesCore.EncryptBlock(mUl, new byte[8]);
        byte[] kc = DesCore.EncryptBlock(mUl, slotTemplate.Ulinf240.AsSpan(40, 8));
        byte[] verifiedMac8 = DesCbcMac(sUl, slotTemplate.Ulinf240.AsSpan(64, 176));

        if (slotTemplate.MaclistEntry8 is { Length: 8 } &&
            !verifiedMac8.AsSpan().SequenceEqual(slotTemplate.MaclistEntry8))
        {
            throw new InvalidDataException("Signed slot template MAC does not match OMG_ULINF payload.");
        }

        using var leadingGeobStream = new MemoryStream(320);
        WriteGeobFrame(leadingGeobStream, "OMG_ULINF", slotTemplate.Ulinf240);
        byte[] leadingGeobs = leadingGeobStream.ToArray();

        using var trailingGeobStream = new MemoryStream(640);
        if (slotTemplate.Bklsi428 is { Length: >= 428 })
        {
            WriteGeobFrame(trailingGeobStream, "OMG_BKLSI", slotTemplate.Bklsi428.AsSpan(0, 428).ToArray());
        }

        if (slotTemplate.Olinf64 is { Length: >= 64 })
        {
            WriteGeobFrame(trailingGeobStream, "OMG_OLINF", slotTemplate.Olinf64.AsSpan(0, 64).ToArray());
        }

        byte[] trailingGeobs = trailingGeobStream.ToArray();

        byte[] mgHeader = BuildEa3AndDescriptor(
            baseTagFrames,
            leadingGeobs,
            trailingGeobs,
            requestedEa3BlockSize: Math.Max(2048, ea3BlockSize),
            ea3Version: 0x01,
            encryptionMode: OmaEncryptionMode.MagicGateLsi_0x0001,
            cid20: slotTemplate.ContentId20,
            codecId: codecId,
            codecParams24: codecParams24,
            durationMs: durationMs,
            frameCount: frameCount,
            iv8: slotTemplate.Iv8);

        byte[] result = new byte[mgHeader.Length + rawAtracFrames.Length];
        mgHeader.CopyTo(result, 0);
        DesCbcEncryptContinuous(kc, slotTemplate.Iv8, rawAtracFrames, result.AsSpan(mgHeader.Length));

        byte[] lsiKeyring48 = new byte[48];
        slotTemplate.ContentId20.CopyTo(lsiKeyring48, 4);

        return new MagicGate0x0001WrapResult(result, slotTemplate.ContentId20.ToArray(), lsiKeyring48, verifiedMac8);
    }

    public static byte[] BuildSignedSlotDummyStub(SignedMagicGateSlotTemplate slotTemplate)
    {
        ArgumentNullException.ThrowIfNull(slotTemplate);
        if (slotTemplate.Ulinf240 is not { Length: 240 })
        {
            throw new InvalidDataException("Signed slot template OMG_ULINF must be exactly 240 bytes.");
        }

        if (slotTemplate.ContentId20 is not { Length: 20 })
        {
            throw new InvalidDataException("Signed slot template ContentId20 must be exactly 20 bytes.");
        }

        if (slotTemplate.Iv8 is not { Length: 8 })
        {
            throw new InvalidDataException("Signed slot template Iv8 must be exactly 8 bytes.");
        }

        using var leadingGeobStream = new MemoryStream(320);
        WriteGeobFrame(leadingGeobStream, "OMG_ULINF", slotTemplate.Ulinf240);
        byte[] leadingGeobs = leadingGeobStream.ToArray();

        using var trailingGeobStream = new MemoryStream(640);
        if (slotTemplate.Bklsi428 is { Length: >= 428 })
        {
            WriteGeobFrame(trailingGeobStream, "OMG_BKLSI", slotTemplate.Bklsi428.AsSpan(0, 428).ToArray());
        }

        if (slotTemplate.Olinf64 is { Length: >= 64 })
        {
            WriteGeobFrame(trailingGeobStream, "OMG_OLINF", slotTemplate.Olinf64.AsSpan(0, 64).ToArray());
        }

        byte[] trailingGeobs = trailingGeobStream.ToArray();

        // 2,048 B ea3 + 96 B EA3 = 2,144 B (0 audio frames) dummy stub preserving signed 0x0001 slot
        return BuildEa3AndDescriptor(
            baseTagFrames: ReadOnlySpan<byte>.Empty,
            leadingGeobs,
            trailingGeobs,
            requestedEa3BlockSize: 2048,
            ea3Version: 0x01,
            encryptionMode: OmaEncryptionMode.MagicGateLsi_0x0001,
            cid20: slotTemplate.ContentId20,
            codecId: AudioCodecId.Atrac3Plus,
            codecParams24: 0x0028FFu,
            durationMs: 0,
            frameCount: 0,
            iv8: slotTemplate.Iv8);
    }

    public static byte[] DecryptOmaPayload(
        ReadOnlySpan<byte> omaBytes,
        int ea3TotalSize,
        int trackId,
        uint? hardwareDeviceKey,
        ReadOnlySpan<byte> ekb00010021Bytes = default)
    {
        int ea3Start = ea3TotalSize;
        if (ea3Start + Ea3DescriptorSize > omaBytes.Length)
        {
            throw new InvalidDataException("OMA buffer too short to contain EA3 descriptor.");
        }

        ReadOnlySpan<byte> ea3 = omaBytes.Slice(ea3Start, Ea3DescriptorSize);
        var encMode = (OmaEncryptionMode)BinaryPrimitives.ReadUInt16BigEndian(ea3.Slice(6, 2));
        int payloadOffset = ea3Start + Ea3DescriptorSize;
        ReadOnlySpan<byte> payload = omaBytes.Slice(payloadOffset);

        if (encMode == OmaEncryptionMode.Unprotected_0xFFFF)
        {
            return payload.ToArray();
        }

        if (encMode == OmaEncryptionMode.HardwareXor_0xFFFE)
        {
            if (!hardwareDeviceKey.HasValue)
            {
                throw new InvalidOperationException("HardwareDeviceKey is required to decrypt 0xFFFE payload.");
            }

            byte[] plain = payload.ToArray();
            uint xorKey = unchecked((0x2465u + (uint)trackId * 0x5296E435u) ^ hardwareDeviceKey.Value);
            ApplyXorInPlace(plain, xorKey);
            return plain;
        }

        if (encMode != OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            throw new NotSupportedException($"Unsupported OMA encryption mode 0x{(ushort)encMode:X4}.");
        }

        byte[]? kc = TryDeriveContentKeyFromEa3Geobs(omaBytes.Slice(0, ea3TotalSize), ekb00010021Bytes);
        if (kc is null)
        {
            throw new InvalidDataException("Failed to derive OpenMG content key from OMG_BKLSI / OMG_ULINF GEOB frames.");
        }

        if (payload.Length % 8 != 0)
        {
            throw new InvalidDataException("Encrypted 0x0001 payload length must be a multiple of 8 bytes.");
        }

        ReadOnlySpan<byte> iv = ea3.Slice(0x58, 8);
        byte[] decrypted = new byte[payload.Length];
        DesCbcDecryptContinuous(kc, iv, payload, decrypted);
        return decrypted;
    }

    private static byte[]? TryDeriveContentKeyFromEa3Geobs(
        ReadOnlySpan<byte> ea3TagRegion,
        ReadOnlySpan<byte> ekb00010021Bytes = default)
    {
        byte[]? ulinfBody = null;
        byte[]? bklsiBody = null;

        int pos = 10;
        while (pos + 10 <= ea3TagRegion.Length)
        {
            if (ea3TagRegion[pos] == 0x00)
            {
                break;
            }

            int frameSize = BinaryPrimitives.ReadInt32BigEndian(ea3TagRegion.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > ea3TagRegion.Length)
            {
                break;
            }

            if (ea3TagRegion[pos] == (byte)'G' &&
                ea3TagRegion[pos + 1] == (byte)'E' &&
                ea3TagRegion[pos + 2] == (byte)'O' &&
                ea3TagRegion[pos + 3] == (byte)'B')
            {
                ReadOnlySpan<byte> body = ea3TagRegion.Slice(pos + 10, frameSize);
                if (TryExtractGeobData(body, "OMG_BKLSI", out var bkSpan))
                {
                    bklsiBody = bkSpan.ToArray();
                }
                else if (TryExtractGeobData(body, "OMG_ULINF", out var ulSpan))
                {
                    ulinfBody = ulSpan.ToArray();
                }
            }

            pos += 10 + frameSize;
        }

        if (bklsiBody is { Length: >= 16 + 64 })
        {
            int kSize = BinaryPrimitives.ReadUInt16BigEndian(bklsiBody.AsSpan(2, 2));
            int eSize = BinaryPrimitives.ReadUInt16BigEndian(bklsiBody.AsSpan(4, 2));
            int iSize = BinaryPrimitives.ReadUInt16BigEndian(bklsiBody.AsSpan(6, 2));
            int ekbStart = 16 + kSize;
            int iStart = ekbStart + eSize;

            if (iStart + iSize + 8 <= bklsiBody.Length &&
                TryDecryptWithEkb(bklsiBody.AsSpan(ekbStart, eSize), bklsiBody.AsSpan(16, 64), bklsiBody.AsSpan(iStart, iSize), bklsiBody.AsSpan(iStart + iSize, 8), out byte[] kc))
            {
                return kc;
            }
        }

        if (ulinfBody is { Length: >= 64 } && ekb00010021Bytes.Length >= 140)
        {
            int krPos = ulinfBody.AsSpan().IndexOf("KEYRING"u8);
            if (krPos >= 0 && krPos + 48 <= ulinfBody.Length)
            {
                byte[] r21 = DeriveEkbRootKey(ekb00010021Bytes, leafIndex: 2, blockIndex: 1);
                byte[] mUl = TripleDesEcbDecrypt(r21, ulinfBody.AsSpan(krPos + 32, 8));
                return DesCore.EncryptBlock(mUl, ulinfBody.AsSpan(krPos + 40, 8));
            }
        }

        return null;
    }

    private static bool TryDecryptWithEkb(
        ReadOnlySpan<byte> ekb,
        ReadOnlySpan<byte> keyring64,
        ReadOnlySpan<byte> iData,
        ReadOnlySpan<byte> expectedMac,
        out byte[] kc)
    {
        kc = Array.Empty<byte>();
        int pos = ekb.Length >= 4 && ekb[..4].SequenceEqual("EKB "u8) ? 32 : 0;
        if (pos + 44 > ekb.Length)
        {
            return false;
        }

        int tagLen = BinaryPrimitives.ReadInt32BigEndian(ekb.Slice(pos + 32, 4));
        int dataLen = BinaryPrimitives.ReadInt32BigEndian(ekb.Slice(pos + 36, 4));
        int blkStart = pos + 44 + tagLen;
        int blkCount = dataLen >> 4;

        if (tagLen < 0 || dataLen < 0 || blkStart + blkCount * 16 > ekb.Length)
        {
            return false;
        }

        byte[] leafKey16 = new byte[16];
        foreach (var (k0, k1) in LeafTable)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(0, 8), k0);
            BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(8, 8), k1);

            for (int b = 0; b < blkCount; b++)
            {
                byte[] rVal = TripleDesEcbDecrypt(leafKey16, ekb.Slice(blkStart + b * 16, 16));
                byte[] mVal = TripleDesEcbDecrypt(rVal, keyring64.Slice(32, 8));
                byte[] sVal = DesCore.EncryptBlock(mVal, new byte[8]);
                byte[] actualMac = DesCbcMac(sVal, iData);
                if (actualMac.AsSpan().SequenceEqual(expectedMac))
                {
                    kc = DesCore.EncryptBlock(mVal, keyring64.Slice(40, 8));
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool TryExtractGeobData(ReadOnlySpan<byte> geobPayload, string targetDescription, out ReadOnlySpan<byte> data)
    {
        data = default;
        if (geobPayload.Length < 4)
        {
            return false;
        }

        byte enc = geobPayload[0];
        int mimeEnd = geobPayload.Slice(1).IndexOf((byte)0);
        if (mimeEnd < 0)
        {
            return false;
        }

        int pos = 1 + mimeEnd + 1;
        // Skip filename (UTF-16 if enc is 1 or 2, single-byte otherwise)
        if (enc is 1 or 2)
        {
            while (pos + 1 < geobPayload.Length && !(geobPayload[pos] == 0 && geobPayload[pos + 1] == 0))
            {
                pos += 2;
            }
            pos += 2;

            int descStart = pos;
            while (pos + 1 < geobPayload.Length && !(geobPayload[pos] == 0 && geobPayload[pos + 1] == 0))
            {
                pos += 2;
            }

            if (pos + 2 > geobPayload.Length)
            {
                return false;
            }

            string desc = Encoding.BigEndianUnicode.GetString(geobPayload.Slice(descStart, pos - descStart));
            if (!desc.Equals(targetDescription, StringComparison.Ordinal))
            {
                return false;
            }

            data = geobPayload.Slice(pos + 2);
            return true;
        }

        int fnEnd = geobPayload.Slice(pos).IndexOf((byte)0);
        if (fnEnd < 0)
        {
            return false;
        }
        pos += fnEnd + 1;

        int descNull = geobPayload.Slice(pos).IndexOf((byte)0);
        if (descNull < 0)
        {
            return false;
        }

        string latinDesc = Encoding.ASCII.GetString(geobPayload.Slice(pos, descNull));
        if (!latinDesc.Equals(targetDescription, StringComparison.Ordinal))
        {
            return false;
        }

        data = geobPayload.Slice(pos + descNull + 1);
        return true;
    }

    private static byte[] DeriveEkbRootKey(ReadOnlySpan<byte> ekb, int leafIndex, int blockIndex)
    {
        int pos = ekb[..4].SequenceEqual("EKB "u8) ? 32 : 0;
        int tagLen = BinaryPrimitives.ReadInt32BigEndian(ekb.Slice(pos + 32, 4));
        int blkStart = pos + 44 + tagLen + blockIndex * 16;

        byte[] leafKey16 = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(0, 8), LeafTable[leafIndex].K0);
        BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(8, 8), LeafTable[leafIndex].K1);

        return TripleDesEcbDecrypt(leafKey16, ekb.Slice(blkStart, 16));
    }

    private static byte[] BuildUlinfBody(
        ReadOnlySpan<byte> mEnc,
        ReadOnlySpan<byte> eEnc,
        ReadOnlySpan<byte> token8,
        ReadOnlySpan<byte> cid20,
        ReadOnlySpan<byte> returnSRid16)
    {
        byte[] body = new byte[240];
        var span = body.AsSpan();

        KeyringHeaderPrefix.CopyTo(span.Slice(0, 12));
        UlinfDescriptor20.CopyTo(span.Slice(12, 20));
        mEnc.CopyTo(span.Slice(32, 8));
        eEnc.CopyTo(span.Slice(40, 8));
        token8.CopyTo(span.Slice(48, 8));

        CidTagPrefix20.CopyTo(span.Slice(64, 20));
        cid20.CopyTo(span.Slice(84, 20));
        BindPTag32.CopyTo(span.Slice(112, 32));

        Encoding.ASCII.GetBytes("RETURN_S_RID", span.Slice(144, 12));
        span[157] = 0x10;
        span[159] = 0x01;
        returnSRid16.CopyTo(span.Slice(160, 16));

        RefIdUlinf32.CopyTo(span.Slice(176, 32));
        BindSTag32.CopyTo(span.Slice(208, 32));

        return body;
    }

    private static void WriteGeobFrame(Stream stream, string description, byte[] bodyBytes)
    {
        byte[] mime = Encoding.ASCII.GetBytes("binary\0");
        byte[] desc = Encoding.BigEndianUnicode.GetBytes(description);
        int payloadLen = 1 + mime.Length + 2 + desc.Length + 2 + bodyBytes.Length;

        Span<byte> frameHdr = stackalloc byte[10];
        "GEOB"u8.CopyTo(frameHdr.Slice(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(frameHdr.Slice(4, 4), payloadLen);
        frameHdr[8] = 0;
        frameHdr[9] = 0;

        stream.Write(frameHdr);
        stream.WriteByte(0x02); // UTF-16BE description
        stream.Write(mime);
        stream.WriteByte(0x00); // Empty filename UTF-16 NUL
        stream.WriteByte(0x00);
        stream.Write(desc);
        stream.WriteByte(0x00); // Description UTF-16 NUL
        stream.WriteByte(0x00);
        stream.Write(bodyBytes);
    }

    private static byte[] BuildEa3AndDescriptor(
        ReadOnlySpan<byte> baseTagFrames,
        ReadOnlySpan<byte> leadingGeobs,
        ReadOnlySpan<byte> trailingGeobs,
        int requestedEa3BlockSize,
        byte ea3Version,
        OmaEncryptionMode encryptionMode,
        ReadOnlySpan<byte> cid20,
        AudioCodecId codecId,
        uint codecParams24,
        uint durationMs,
        uint frameCount,
        ReadOnlySpan<byte> iv8)
    {
        int minRequired = 10 + leadingGeobs.Length + baseTagFrames.Length + trailingGeobs.Length;
        int ea3TotalSize = requestedEa3BlockSize;
        while (ea3TotalSize < minRequired)
        {
            ea3TotalSize += 1024;
        }

        byte[] result = new byte[ea3TotalSize + Ea3DescriptorSize];
        var span = result.AsSpan();

        span[0] = (byte)'e';
        span[1] = (byte)'a';
        span[2] = (byte)'3';
        span[3] = 0x03;
        span[4] = 0x00;
        span[5] = 0x00;
        int bodySize = ea3TotalSize - 10;
        span[6] = (byte)((bodySize >> 21) & 0x7F);
        span[7] = (byte)((bodySize >> 14) & 0x7F);
        span[8] = (byte)((bodySize >> 7) & 0x7F);
        span[9] = (byte)(bodySize & 0x7F);

        int offset = 10;
        if (!leadingGeobs.IsEmpty)
        {
            leadingGeobs.CopyTo(span.Slice(offset));
            offset += leadingGeobs.Length;
        }

        baseTagFrames.CopyTo(span.Slice(offset));
        offset += baseTagFrames.Length;

        if (!trailingGeobs.IsEmpty)
        {
            trailingGeobs.CopyTo(span.Slice(offset));
        }

        Span<byte> ea3 = span.Slice(ea3TotalSize, Ea3DescriptorSize);
        ea3[0] = (byte)'E';
        ea3[1] = (byte)'A';
        ea3[2] = (byte)'3';
        ea3[3] = ea3Version;
        ea3[4] = 0x00;
        ea3[5] = Ea3DescriptorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ea3.Slice(6, 2), (ushort)encryptionMode);

        if (cid20.Length == 20)
        {
            cid20.CopyTo(ea3.Slice(12, 20));
        }

        ea3[32] = (byte)codecId;
        ea3[33] = (byte)((codecParams24 >> 16) & 0xFF);
        ea3[34] = (byte)((codecParams24 >> 8) & 0xFF);
        ea3[35] = (byte)(codecParams24 & 0xFF);
        if (encryptionMode != OmaEncryptionMode.MagicGateLsi_0x0001)
        {
            BinaryPrimitives.WriteUInt32BigEndian(ea3.Slice(36, 4), durationMs);
            BinaryPrimitives.WriteUInt32BigEndian(ea3.Slice(40, 4), frameCount);
        }

        if (iv8.Length == 8)
        {
            iv8.CopyTo(ea3.Slice(0x58, 8));
        }

        return result;
    }

    private static byte[] GenerateContentId20(int trackId, uint codecParams24)
    {
        byte[] cid = new byte[20];
        cid[0] = 0x01;
        cid[1] = 0x0F;
        cid[2] = 0x50;
        cid[3] = 0x00;
        cid[4] = 0x00;
        cid[5] = 0x04;
        cid[6] = 0x00;
        cid[7] = 0x00;
        cid[8] = 0x00;
        RandomNumberGenerator.Fill(cid.AsSpan(9, 2));
        cid[11] = 0x55;
        cid[12] = 0xB0;
        cid[13] = 0xDE;
        cid[14] = 0x2A;
        cid[15] = 0x56;
        RandomNumberGenerator.Fill(cid.AsSpan(16, 4));
        cid[19] ^= (byte)((uint)trackId ^ (codecParams24 & 0xFF));
        return cid;
    }

    private static void ApplyXorInPlace(Span<byte> data, uint trackKey)
    {
        byte k0 = (byte)((trackKey >> 24) & 0xFF);
        byte k1 = (byte)((trackKey >> 16) & 0xFF);
        byte k2 = (byte)((trackKey >> 8) & 0xFF);
        byte k3 = (byte)(trackKey & 0xFF);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= (i & 3) switch
            {
                0 => k0,
                1 => k1,
                2 => k2,
                _ => k3
            };
        }
    }

    private static byte[] TripleDesEcbEncrypt(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> input)
    {
        ReadOnlySpan<byte> k1 = key16.Slice(0, 8);
        ReadOnlySpan<byte> k2 = key16.Slice(8, 8);
        byte[] output = new byte[input.Length];
        for (int off = 0; off < input.Length; off += 8)
        {
            ulong block = BinaryPrimitives.ReadUInt64BigEndian(input.Slice(off, 8));
            ulong u1 = DesCore.EncryptBlockU64(k1, block);
            ulong u2 = DesCore.DecryptBlockU64(k2, u1);
            ulong u3 = DesCore.EncryptBlockU64(k1, u2);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(off, 8), u3);
        }

        return output;
    }

    private static byte[] TripleDesEcbDecrypt(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> input)
    {
        ReadOnlySpan<byte> k1 = key16.Slice(0, 8);
        ReadOnlySpan<byte> k2 = key16.Slice(8, 8);
        byte[] output = new byte[input.Length];
        for (int off = 0; off < input.Length; off += 8)
        {
            ulong block = BinaryPrimitives.ReadUInt64BigEndian(input.Slice(off, 8));
            ulong u1 = DesCore.DecryptBlockU64(k1, block);
            ulong u2 = DesCore.EncryptBlockU64(k2, u1);
            ulong u3 = DesCore.DecryptBlockU64(k1, u2);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(off, 8), u3);
        }

        return output;
    }

    private static byte[] DesCbcMac(ReadOnlySpan<byte> key8, ReadOnlySpan<byte> data)
    {
        ulong iv = 0;
        for (int off = 0; off < data.Length; off += 8)
        {
            ulong block = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(off, 8));
            iv = DesCore.EncryptBlockU64(key8, block ^ iv);
        }

        byte[] mac = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(mac, iv);
        return mac;
    }

    private static void DesCbcEncryptContinuous(
        ReadOnlySpan<byte> key8,
        ReadOnlySpan<byte> iv8,
        ReadOnlySpan<byte> plain,
        Span<byte> cipher)
    {
        ulong prev = BinaryPrimitives.ReadUInt64BigEndian(iv8);
        for (int off = 0; off < plain.Length; off += 8)
        {
            ulong p = BinaryPrimitives.ReadUInt64BigEndian(plain.Slice(off, 8));
            ulong c = DesCore.EncryptBlockU64(key8, p ^ prev);
            BinaryPrimitives.WriteUInt64BigEndian(cipher.Slice(off, 8), c);
            prev = c;
        }
    }

    private static void DesCbcDecryptContinuous(
        ReadOnlySpan<byte> key8,
        ReadOnlySpan<byte> iv8,
        ReadOnlySpan<byte> cipher,
        Span<byte> plain)
    {
        ulong prev = BinaryPrimitives.ReadUInt64BigEndian(iv8);
        for (int off = 0; off < cipher.Length; off += 8)
        {
            ulong c = BinaryPrimitives.ReadUInt64BigEndian(cipher.Slice(off, 8));
            ulong p = DesCore.DecryptBlockU64(key8, c) ^ prev;
            BinaryPrimitives.WriteUInt64BigEndian(plain.Slice(off, 8), p);
            prev = c;
        }
    }

    /// <summary>
    /// Standard FIPS 46-3 64-bit DES block cipher free of .NET weak/semi-weak key exceptions.
    /// </summary>
    private static class DesCore
    {
        private static readonly byte[] Ip =
        [
            58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
            62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
            57, 49, 41, 33, 25, 17, 9,  1, 59, 51, 43, 35, 27, 19, 11, 3,
            61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7
        ];

        private static readonly byte[] Fp =
        [
            40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
            38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
            36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
            34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9,  49, 17, 57, 25
        ];

        private static readonly byte[] Expand =
        [
            32, 1,  2,  3,  4,  5,  4,  5,  6,  7,  8,  9,
            8,  9,  10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
            16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25,
            24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1
        ];

        private static readonly byte[] PBox =
        [
            16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
            2,  8, 24, 14, 32, 27, 3,  9,  19, 13, 30, 6, 22, 11, 4, 25
        ];

        private static readonly byte[] Pc1 =
        [
            57, 49, 41, 33, 25, 17, 9,  1, 58, 50, 42, 34, 26, 18,
            10, 2,  59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
            63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22,
            14, 6,  61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4
        ];

        private static readonly byte[] Pc2 =
        [
            14, 17, 11, 24, 1,  5,  3,  28, 15, 6,  21, 10,
            23, 19, 12, 4,  26, 8,  16, 7,  27, 20, 13, 2,
            41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48,
            44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32
        ];

        private static readonly byte[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

        private static readonly byte[,] SBoxes =
        {
            {
                14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
                0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
                4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
                15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13
            },
            {
                15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
                3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
                0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
                13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9
            },
            {
                10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
                13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
                13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
                1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12
            },
            {
                7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
                13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
                10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
                3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14
            },
            {
                2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
                14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
                4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
                11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3
            },
            {
                12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
                10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
                9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
                4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13
            },
            {
                4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
                13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
                1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
                6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12
            },
            {
                13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
                1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
                7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
                2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11
            }
        };

        public static byte[] EncryptBlock(ReadOnlySpan<byte> key8, ReadOnlySpan<byte> block8)
        {
            byte[] res = new byte[8];
            ulong u = EncryptBlockU64(key8, BinaryPrimitives.ReadUInt64BigEndian(block8));
            BinaryPrimitives.WriteUInt64BigEndian(res, u);
            return res;
        }

        public static byte[] DecryptBlock(ReadOnlySpan<byte> key8, ReadOnlySpan<byte> block8)
        {
            byte[] res = new byte[8];
            ulong u = DecryptBlockU64(key8, BinaryPrimitives.ReadUInt64BigEndian(block8));
            BinaryPrimitives.WriteUInt64BigEndian(res, u);
            return res;
        }

        public static ulong EncryptBlockU64(ReadOnlySpan<byte> key8, ulong block) =>
            RunDes(BinaryPrimitives.ReadUInt64BigEndian(key8), block, encrypt: true);

        public static ulong DecryptBlockU64(ReadOnlySpan<byte> key8, ulong block) =>
            RunDes(BinaryPrimitives.ReadUInt64BigEndian(key8), block, encrypt: false);

        private static ulong RunDes(ulong key, ulong input, bool encrypt)
        {
            ulong pc1 = Permute(key, Pc1, 64);
            uint c = (uint)((pc1 >> 28) & 0x0FFF_FFFFu);
            uint d = (uint)(pc1 & 0x0FFF_FFFFu);

            Span<ulong> subkeys = stackalloc ulong[16];
            for (int r = 0; r < 16; r++)
            {
                int s = Shifts[r];
                c = ((c << s) | (c >> (28 - s))) & 0x0FFF_FFFFu;
                d = ((d << s) | (d >> (28 - s))) & 0x0FFF_FFFFu;
                ulong cd = ((ulong)c << 28) | d;
                subkeys[r] = Permute(cd, Pc2, 56);
            }

            ulong perm = Permute(input, Ip, 64);
            uint l = (uint)(perm >> 32);
            uint right = (uint)perm;

            for (int r = 0; r < 16; r++)
            {
                ulong rk = encrypt ? subkeys[r] : subkeys[15 - r];
                uint f = Feistel(right, rk);
                uint nextR = l ^ f;
                l = right;
                right = nextR;
            }

            ulong combined = ((ulong)right << 32) | l;
            return Permute(combined, Fp, 64);
        }

        private static uint Feistel(uint r, ulong subkey48)
        {
            ulong expanded = Permute(r, Expand, 32) ^ subkey48;
            uint sOut = 0;
            for (int box = 0; box < 8; box++)
            {
                int sixBits = (int)((expanded >> (42 - box * 6)) & 0x3FUL);
                int row = ((sixBits & 0x20) >> 4) | (sixBits & 0x01);
                int col = (sixBits >> 1) & 0x0F;
                sOut = (sOut << 4) | SBoxes[box, row * 16 + col];
            }

            return (uint)Permute(sOut, PBox, 32);
        }

        private static ulong Permute(ulong value, byte[] table, int inWidth)
        {
            ulong result = 0;
            for (int i = 0; i < table.Length; i++)
            {
                ulong bit = (value >> (inWidth - table[i])) & 1UL;
                result = (result << 1) | bit;
            }

            return result;
        }
    }
}
