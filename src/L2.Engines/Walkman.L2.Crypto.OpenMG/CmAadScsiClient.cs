using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Clean-room CM-AAD SCSI Pass-Through Direct protocol helper (Key Class 0xBC, commands 0xA3/0xA4 0x30..0x34)
/// for reading and committing the 168-byte (0xA8) Hardware ICV register on Sony NW-E403 / OpenMG devices.
/// </summary>
internal static class CmAadScsiClient
{
    public const int IcvPayloadSize = 168;
    public const int Auth1TransferLength = 0x0014; // 20 B
    public const int Auth2TransferLength = 0x043C; // 1084 B
    public const int Auth3TransferLength = 0x041C; // 1052 B
    public const int IcvTransferLength = 0x0404;   // 1028 B

    private const uint IoctlScsiPassThroughDirect = 0x0004D014u;
    private const byte ScsiIoctlDataOut = 0x00;
    private const byte ScsiIoctlDataIn = 0x01;
    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint FileShareReadWrite = 0x00000003u;
    private const uint OpenExisting = 3u;

    public static byte[] BuildAuth1Cdb() =>
        [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x00, 0x14, 0x30, 0x00];

    public static byte[] BuildAuth2Cdb() =>
        [0xA4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x3C, 0x31, 0x00];

    public static byte[] BuildAuth3Cdb() =>
        [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x1C, 0x32, 0x00];

    public static byte[] BuildReadIcvCdb() =>
        [0xA4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x04, 0x33, 0x00];

    public static byte[] BuildWriteIcvCdb() =>
        [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x04, 0x34, 0x00];

    public static byte[] BuildAuth1Payload(ReadOnlySpan<byte> token16)
    {
        if (token16.Length != 16)
        {
            throw new ArgumentException("Token 1 must be exactly 16 bytes.", nameof(token16));
        }

        byte[] payload = new byte[Auth1TransferLength];
        payload[0] = 0x00;
        payload[1] = 0x12;
        token16.CopyTo(payload.AsSpan(4, 16));
        return payload;
    }

    public static byte[] BuildAuth3Payload(ReadOnlySpan<byte> authResp8, ReadOnlySpan<byte> hostEkb)
    {
        if (authResp8.Length != 8)
        {
            throw new ArgumentException("Auth response must be exactly 8 bytes.", nameof(authResp8));
        }

        if (hostEkb.Length > Auth3TransferLength - 12)
        {
            throw new ArgumentException("Host EKB exceeds 1040-byte transfer window.", nameof(hostEkb));
        }

        byte[] payload = new byte[Auth3TransferLength];
        payload[0] = 0x04;
        payload[1] = 0x1A;
        authResp8.CopyTo(payload.AsSpan(4, 8));
        hostEkb.CopyTo(payload.AsSpan(12));
        return payload;
    }

    public static HardwareIcvSnapshot? ParseHardwareIcvResponse(ReadOnlySpan<byte> response1028)
    {
        if (response1028.Length < 4 + IcvPayloadSize)
        {
            return null;
        }

        ReadOnlySpan<byte> icv = response1028.Slice(4, IcvPayloadSize);
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(icv.Slice(4, 4));
        uint ekbRev = BinaryPrimitives.ReadUInt32BigEndian(icv.Slice(12, 4));
        byte[] umac = icv.Slice(0x20, 8).ToArray();
        byte[] micvs = icv.Slice(0xA0, 8).ToArray();

        return new HardwareIcvSnapshot(
            RawIcv168: icv.ToArray(),
            SequenceCounter: seq,
            BoundEkbRevision: ekbRev,
            Maclist0Umac8: umac,
            MicvsMac8: micvs);
    }

    public static byte[] BuildWriteIcvPayload(ReadOnlySpan<byte> icv168)
    {
        if (icv168.Length != IcvPayloadSize)
        {
            throw new ArgumentException($"Hardware ICV must be exactly {IcvPayloadSize} bytes.", nameof(icv168));
        }

        byte[] payload = new byte[IcvTransferLength];
        payload[0] = 0x04;
        payload[1] = 0x02;
        icv168.CopyTo(payload.AsSpan(4, IcvPayloadSize));
        return payload;
    }

    public static HardwareIcvSnapshot? TryReadHardwareIcv(string? volumeRootPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumeRootPath))
        {
            return null;
        }

        string? root = Path.GetPathRoot(Path.GetFullPath(volumeRootPath));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        string volumeDevicePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
        nint handle = CreateFileW(
            volumeDevicePath,
            GenericRead | GenericWrite,
            FileShareReadWrite,
            0,
            OpenExisting,
            0,
            0);

        if (handle == 0 || handle == new nint(-1))
        {
            return null;
        }

        try
        {
            byte[] response = new byte[IcvTransferLength];
            if (!TryExecuteScsiPassThroughDirect(handle, BuildReadIcvCdb(), ScsiIoctlDataIn, response))
            {
                return null;
            }

            return ParseHardwareIcvResponse(response);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool TryExecuteScsiPassThroughDirect(
        nint volumeHandle,
        ReadOnlySpan<byte> cdb12,
        byte dataDirection,
        byte[] dataBuffer)
    {
        if (!OperatingSystem.IsWindows() || cdb12.Length > 16)
        {
            return false;
        }

        int sptSize = nint.Size == 8 ? 56 : 44;
        const int senseSize = 32;
        byte[] sptBuffer = new byte[sptSize + senseSize];

        GCHandle pinnedData = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(sptBuffer.AsSpan(0, 2), (ushort)sptSize);
            sptBuffer[6] = (byte)cdb12.Length; // CdbLength
            sptBuffer[7] = senseSize;          // SenseInfoLength
            sptBuffer[8] = dataDirection;      // DataIn
            BinaryPrimitives.WriteUInt32LittleEndian(sptBuffer.AsSpan(12, 4), (uint)dataBuffer.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(sptBuffer.AsSpan(16, 4), 10u); // TimeOutValue (s)

            int dataBufferOffset = nint.Size == 8 ? 24 : 20;
            int senseOffsetField = nint.Size == 8 ? 32 : 24;
            int cdbOffset = nint.Size == 8 ? 36 : 28;

            nint dataPtr = pinnedData.AddrOfPinnedObject();
            if (nint.Size == 8)
            {
                BinaryPrimitives.WriteInt64LittleEndian(sptBuffer.AsSpan(dataBufferOffset, 8), dataPtr.ToInt64());
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(sptBuffer.AsSpan(dataBufferOffset, 4), dataPtr.ToInt32());
            }

            BinaryPrimitives.WriteUInt32LittleEndian(sptBuffer.AsSpan(senseOffsetField, 4), (uint)sptSize);
            cdb12.CopyTo(sptBuffer.AsSpan(cdbOffset, cdb12.Length));

            bool ok = DeviceIoControl(
                volumeHandle,
                IoctlScsiPassThroughDirect,
                sptBuffer,
                (uint)sptBuffer.Length,
                sptBuffer,
                (uint)sptBuffer.Length,
                out _,
                0);

            byte scsiStatus = sptBuffer[2];
            return ok && scsiStatus == 0x00;
        }
        finally
        {
            pinnedData.Free();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
