using System.Buffers.Binary;

namespace Walkman.M0.Tests;

internal static class TestEkbFixtureHelper
{
    public const uint SyntheticDeviceKeyA = 0x12345678u;
    public const uint SyntheticDeviceKeyB = 0xCAFEBABEu;
    public const uint SyntheticVolumeSerialA = 0xA1B2C3D4u;
    public const uint SyntheticVolumeSerialB = 0xD5E6F708u;

    public static bool ShouldRunHardwareBackupTests() =>
        string.Equals(Environment.GetEnvironmentVariable("APOLLO_RUN_HARDWARE_BACKUP_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Builds a synthetic 172-byte EKB 0x00010021 buffer encrypted under public LeafTable[2]
    /// for clean-room unit tests without embedding any Sony binary EKB blob.
    /// </summary>
    public static byte[] BuildSyntheticTestEkb00010021()
    {
        // Header: "EKB " (32 bytes) + 44-byte EKB body header + 0-byte tag + 32-byte (2 x 16B) key blocks = 108 bytes;
        // pad to 172 bytes (>= 140 bytes required by MagicGate0x0001Crypto).
        byte[] ekb = new byte[172];
        "EKB "u8.CopyTo(ekb.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(ekb.AsSpan(32 + 32, 4), 0); // tagLen = 0
        BinaryPrimitives.WriteInt32BigEndian(ekb.AsSpan(32 + 36, 4), 32); // dataLen = 32 (2 blocks)

        // LeafTable[2] = (0x1573cd93da7df623UL, 0x47f98d79620dd535UL)
        byte[] leafKey16 = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(0, 8), 0x1573cd93da7df623UL);
        BinaryPrimitives.WriteUInt64LittleEndian(leafKey16.AsSpan(8, 8), 0x47f98d79620dd535UL);

        // Encrypt a deterministic 16-byte synthetic root key into blockIndex 1 (offset 32 + 44 + 16 = 92)
        using var tdes = System.Security.Cryptography.TripleDES.Create();
        tdes.Mode = System.Security.Cryptography.CipherMode.ECB;
        tdes.Padding = System.Security.Cryptography.PaddingMode.None;
        tdes.Key = leafKey16;

        byte[] syntheticRootKey16 = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01];
        byte[] encryptedBlock = new byte[16];
        using (var enc = tdes.CreateEncryptor())
        {
            enc.TransformBlock(syntheticRootKey16, 0, 16, encryptedBlock, 0);
        }

        encryptedBlock.CopyTo(ekb.AsSpan(32 + 44 + 16, 16));
        return ekb;
    }
}
