using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Walkman.OpenMgIcvWorker;

/// <summary>
/// Clean-room 32-bit (x86) Windows out-of-process COM &amp; SCSI 0xA3/0xA4 bridge for Sony OpenMG 0x0001 Hardware ICV commits.
/// Zero Sony binaries are bundled; this process activates pre-installed host COM classes (CLSID_OmgHdwmAuth / CLSID_OmgHdwmSac)
/// via CoCreateInstance only when the user already has Sony OpenMG Secure Module (hdwmcom.dll) installed.
/// </summary>
internal static class Program
{
    private static readonly string DefaultHostRuntimePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Common Files",
        "Sony Shared",
        "OpenMG",
        "hdwmcom.dll");

    public static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
        {
            bool isWin = OperatingSystem.IsWindows();
            bool is32Bit = IntPtr.Size == 4;
            bool hasDll = isWin && File.Exists(DefaultHostRuntimePath);
            var probe = new WorkerProbeResponseDto
            {
                Success = isWin && is32Bit && hasDll,
                IsWindows = isWin,
                Is32Bit = is32Bit,
                HostRuntimePath = DefaultHostRuntimePath,
                HostRuntimePresent = hasDll
            };
            Console.WriteLine(JsonSerializer.Serialize(probe, WorkerJsonContext.Default.WorkerProbeResponseDto));
            return (isWin && is32Bit && hasDll) ? 0 : 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            WriteFailure("OpenMG ICV COM bridge is only supported on Windows.");
            return 1;
        }

        if (IntPtr.Size != 4)
        {
            WriteFailure("Walkman.OpenMgIcvWorker must execute as a 32-bit (x86) process to load 32-bit hdwmcom.dll.");
            return 1;
        }

        if (!File.Exists(DefaultHostRuntimePath))
        {
            WriteFailure($"Host Sony OpenMG Secure Module (hdwmcom.dll) not found at '{DefaultHostRuntimePath}'.");
            return 1;
        }

        try
        {
            string stdinJson = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(stdinJson))
            {
                WriteFailure("Empty JSON request on stdin.");
                return 1;
            }

            var req = JsonSerializer.Deserialize(stdinJson, WorkerJsonContext.Default.WorkerCommitRequestDto);
            if (req is null || string.IsNullOrWhiteSpace(req.VolumeRootPath))
            {
                WriteFailure("Invalid commit request: VolumeRootPath is required.");
                return 1;
            }

            var result = OpenMgComSession.ExecuteCommit(req);
            Console.WriteLine(JsonSerializer.Serialize(result, WorkerJsonContext.Default.WorkerCommitResponseDto));
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteFailure($"OpenMG ICV worker error: {ex.Message}");
            return 1;
        }
    }

    private static void WriteFailure(string message)
    {
        var resp = new WorkerCommitResponseDto
        {
            Success = false,
            ErrorMessage = message
        };
        Console.WriteLine(JsonSerializer.Serialize(resp, WorkerJsonContext.Default.WorkerCommitResponseDto));
    }
}

[JsonSerializable(typeof(WorkerProbeResponseDto))]
[JsonSerializable(typeof(WorkerCommitRequestDto))]
[JsonSerializable(typeof(WorkerCommitResponseDto))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext
{
}

internal sealed class WorkerProbeResponseDto
{
    public bool Success { get; set; }
    public bool IsWindows { get; set; }
    public bool Is32Bit { get; set; }
    public string HostRuntimePath { get; set; } = string.Empty;
    public bool HostRuntimePresent { get; set; }
}

internal sealed class WorkerCommitRequestDto
{
    public string VolumeRootPath { get; set; } = string.Empty;
    public int[]? DeleteTrackIds { get; set; }
    public WorkerMintRequestDto[]? MintRequests { get; set; }
}

internal sealed class WorkerMintRequestDto
{
    public int TrackId { get; set; }
    public string ContentIdBase64 { get; set; } = string.Empty;
    public string PlaintextKeyBase64 { get; set; } = string.Empty;
}

internal sealed class WorkerCommitResponseDto
{
    public bool Success { get; set; }
    public string? UpdatedMaclist0Base64 { get; set; }
    public string? UpdatedIcv168Base64 { get; set; }
    public WorkerSlotDto[]? MintedSlots { get; set; }
    public string? ErrorMessage { get; set; }
}

internal sealed class WorkerSlotDto
{
    public int TrackId { get; set; }
    public string UlinfBase64 { get; set; } = string.Empty;
    public string? BklsiBase64 { get; set; }
    public string? OlinfBase64 { get; set; }
    public string ContentIdBase64 { get; set; } = string.Empty;
    public string IvBase64 { get; set; } = string.Empty;
    public string MaclistEntryBase64 { get; set; } = string.Empty;
}

internal static unsafe class OpenMgComSession
{
    // Verified CLSIDs / IIDs from RE_WIP_05_OpenMG_ICV_SCSI_And_MG_ERROR_Solution.md
    private static readonly Guid ClsidOmgHdwmAuth = new("0A0140D2-AF25-40DB-B2DB-313FDF6ECC48");
    private static readonly Guid IidIOmgHdwmAuth = new("594C9C4D-B1D2-48DB-A23A-E08763C6801D");
    private static readonly Guid ClsidOmgHdwmSac = new("74683513-BCE0-46AB-9890-C934877F083A");
    private static readonly Guid IidIOmgHdwmSac3 = new("6DE1C2F0-F5C5-4D38-8F3D-1852046188C4");

    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitApartmentThreaded = 0x2;
    private const int MaclistSize = 32768;
    private const int IcvSize = 168;

    public static WorkerCommitResponseDto ExecuteCommit(WorkerCommitRequestDto request)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(request.VolumeRootPath));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"Invalid volume root '{request.VolumeRootPath}'." };
        }

        int hrInit = CoInitializeEx(nint.Zero, CoinitApartmentThreaded);
        bool shouldUninit = hrInit >= 0;

        nint authPtr = nint.Zero;
        nint sacPtr = nint.Zero;
        nint volumeHandle = nint.Zero;

        try
        {
            Guid clsidAuth = ClsidOmgHdwmAuth;
            Guid iidAuth = IidIOmgHdwmAuth;
            int hr = CoCreateInstance(ref clsidAuth, nint.Zero, ClsctxInprocServer, ref iidAuth, out authPtr);
            if (hr < 0 || authPtr == nint.Zero)
            {
                return new WorkerCommitResponseDto
                {
                    Success = false,
                    ErrorMessage = $"Failed to activate host COM class CLSID_OmgHdwmAuth (HRESULT 0x{hr:X8}). Ensure Sony OpenMG Secure Module is registered."
                };
            }

            Guid clsidSac = ClsidOmgHdwmSac;
            Guid iidSac = IidIOmgHdwmSac3;
            hr = CoCreateInstance(ref clsidSac, nint.Zero, ClsctxInprocServer, ref iidSac, out sacPtr);
            if (hr < 0 || sacPtr == nint.Zero)
            {
                return new WorkerCommitResponseDto
                {
                    Success = false,
                    ErrorMessage = $"Failed to activate host COM class CLSID_OmgHdwmSac (HRESULT 0x{hr:X8})."
                };
            }

            string volumeDevicePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
            volumeHandle = WorkerScsiClient.OpenVolume(volumeDevicePath);
            if (volumeHandle == nint.Zero || volumeHandle == new nint(-1))
            {
                int err = Marshal.GetLastWin32Error();
                return new WorkerCommitResponseDto
                {
                    Success = false,
                    ErrorMessage = $"Could not open volume handle '{volumeDevicePath}' for SCSI pass-through (Win32 error {err}; administrator rights may be required)."
                };
            }

            // Step 1: IOmgHdwmAuth::step1 (vtable slot 3) -> produce 16-byte Auth 1 token
            byte[] token1 = new byte[16];
            int token1Len = token1.Length;
            nint* authVtbl = *(nint**)authPtr;
            var step1Fn = (delegate* unmanaged[Stdcall]<nint, byte*, int*, int>)authVtbl[3];
            fixed (byte* pTok1 = token1)
            {
                hr = step1Fn(authPtr, pTok1, &token1Len);
            }
            if (hr < 0)
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmAuth::step1 failed (HRESULT 0x{hr:X8})." };
            }

            // SCSI Auth 1 (0xA3 / 0x30)
            byte[] auth1Payload = new byte[20];
            auth1Payload[0] = 0x00;
            auth1Payload[1] = 0x12;
            token1.CopyTo(auth1Payload.AsSpan(4, 16));
            if (!WorkerScsiClient.Execute(volumeHandle, [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x00, 0x14, 0x30, 0x00], 0x00, auth1Payload))
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = "SCSI CM-AAD Auth 1 (0xA3/0x30) failed on Walkman device." };
            }

            // SCSI Auth 2 (0xA4 / 0x31) -> 1084 bytes (4B hdr + 16B DvID + 24B tokens + device EKB)
            byte[] auth2Resp = new byte[1084];
            if (!WorkerScsiClient.Execute(volumeHandle, [0xA4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x3C, 0x31, 0x00], 0x01, auth2Resp))
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = "SCSI CM-AAD Auth 2 (0xA4/0x31) failed on Walkman device." };
            }

            // Step 2: IOmgHdwmAuth::step2 (vtable slot 4) -> process Auth 2 response, emit 8B auth response + host EKB
            byte[] authResp8 = new byte[8];
            byte[] hostEkb = new byte[1040];
            int hostEkbLen = hostEkb.Length;
            var step2Fn = (delegate* unmanaged[Stdcall]<nint, byte*, int, byte*, byte*, int*, int>)authVtbl[4];
            fixed (byte* pA2 = auth2Resp, pResp8 = authResp8, pHostEkb = hostEkb)
            {
                hr = step2Fn(authPtr, pA2 + 4, auth2Resp.Length - 4, pResp8, pHostEkb, &hostEkbLen);
            }
            if (hr < 0)
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmAuth::step2 failed (HRESULT 0x{hr:X8})." };
            }

            // SCSI Auth 3 (0xA3 / 0x32)
            byte[] auth3Payload = new byte[1052];
            auth3Payload[0] = 0x04;
            auth3Payload[1] = 0x1A;
            authResp8.CopyTo(auth3Payload.AsSpan(4, 8));
            int ekbCopyLen = Math.Clamp(hostEkbLen, 0, 1040);
            hostEkb.AsSpan(0, ekbCopyLen).CopyTo(auth3Payload.AsSpan(12));
            if (!WorkerScsiClient.Execute(volumeHandle, [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x1C, 0x32, 0x00], 0x00, auth3Payload))
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = "SCSI CM-AAD Auth 3 (0xA3/0x32) failed on Walkman device." };
            }

            // Read Hardware ICV (0xA4 / 0x33)
            byte[] readIcvResp = new byte[1028];
            if (!WorkerScsiClient.Execute(volumeHandle, [0xA4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x04, 0x33, 0x00], 0x01, readIcvResp))
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = "SCSI Read Hardware ICV (0xA4/0x33) failed." };
            }

            byte[] currentIcv = readIcvResp.AsSpan(4, IcvSize).ToArray();
            string omgDir = Path.Combine(request.VolumeRootPath, "OMGAUDIO");
            Directory.CreateDirectory(omgDir);
            string maclistPath = Path.Combine(omgDir, "MACLIST0.DAT");
            byte[] maclist0 = File.Exists(maclistPath) ? File.ReadAllBytes(maclistPath) : new byte[MaclistSize];
            if (maclist0.Length < MaclistSize)
            {
                Array.Resize(ref maclist0, MaclistSize);
            }

            // Step 3a: IOmgHdwmAuth::step3a (vtable slot 5) binds ICV + MACLIST0 into session
            var step3aFn = (delegate* unmanaged[Stdcall]<nint, byte*, int, byte*, int, int>)authVtbl[5];
            fixed (byte* pIcv = currentIcv, pMac = maclist0)
            {
                hr = step3aFn(authPtr, pIcv, currentIcv.Length, pMac, maclist0.Length);
            }
            if (hr < 0)
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmAuth::step3a failed (HRESULT 0x{hr:X8})." };
            }

            nint* sacVtbl = *(nint**)sacPtr;
            var mintedSlots = new List<WorkerSlotDto>();

            // Delete tracks if requested (IOmgHdwmSac3::deleteTracks, slot 4)
            if (request.DeleteTrackIds is { Length: > 0 })
            {
                var delFn = (delegate* unmanaged[Stdcall]<nint, int*, int, int>)sacVtbl[4];
                fixed (int* pDel = request.DeleteTrackIds)
                {
                    hr = delFn(sacPtr, pDel, request.DeleteTrackIds.Length);
                }
                if (hr < 0)
                {
                    return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmSac3::deleteTracks failed (HRESULT 0x{hr:X8})." };
                }
            }

            // Mint requested slots via IOmgHdwmSac3::checkOut (slot 5)
            if (request.MintRequests is { Length: > 0 })
            {
                var checkOutFn = (delegate* unmanaged[Stdcall]<nint, int, byte*, byte*, byte*, int*, int>)sacVtbl[5];
                foreach (var mint in request.MintRequests)
                {
                    byte[] cid20 = Convert.FromBase64String(mint.ContentIdBase64);
                    byte[] key8 = Convert.FromBase64String(mint.PlaintextKeyBase64);
                    byte[] ulinfOut = new byte[240];
                    int ulinfLen = ulinfOut.Length;

                    fixed (byte* pCid = cid20, pKey = key8, pUlinf = ulinfOut)
                    {
                        hr = checkOutFn(sacPtr, mint.TrackId, pCid, pKey, pUlinf, &ulinfLen);
                    }
                    if (hr < 0)
                    {
                        return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmSac3::checkOut failed for track #{mint.TrackId} (HRESULT 0x{hr:X8})." };
                    }

                    byte[] iv8 = RandomNumberGenerator.GetBytes(8);
                    mintedSlots.Add(new WorkerSlotDto
                    {
                        TrackId = mint.TrackId,
                        UlinfBase64 = Convert.ToBase64String(ulinfOut),
                        ContentIdBase64 = Convert.ToBase64String(cid20),
                        IvBase64 = Convert.ToBase64String(iv8),
                        MaclistEntryBase64 = string.Empty // Populated from updated MACLIST0 below
                    });
                }
            }

            // Retrieve updated 168-byte ICV & 32,768-byte MACLIST0 via IOmgHdwmSac3::getIcvMacList (slot 3)
            byte[] updatedIcv = new byte[IcvSize];
            byte[] updatedMaclist = new byte[MaclistSize];
            var getIcvMacFn = (delegate* unmanaged[Stdcall]<nint, byte*, byte*, int>)sacVtbl[3];
            fixed (byte* pNewIcv = updatedIcv, pNewMac = updatedMaclist)
            {
                hr = getIcvMacFn(sacPtr, pNewIcv, pNewMac);
            }
            if (hr < 0)
            {
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = $"IOmgHdwmSac3::getIcvMacList failed (HRESULT 0x{hr:X8})." };
            }

            // Populate each minted slot's signed 8-byte MACLIST0 entry
            foreach (var slot in mintedSlots)
            {
                int off = (slot.TrackId - 1) * 8;
                if (off >= 0 && off + 8 <= updatedMaclist.Length)
                {
                    slot.MaclistEntryBase64 = Convert.ToBase64String(updatedMaclist.AsSpan(off, 8));
                }
            }

            // SonicStage commit sequence: backup MACLIST0.DAT -> MACLIST0.BAK, flush new MACLIST0.DAT, then SCSI Write ICV (0xA3/0x34)
            string maclistBak = Path.Combine(omgDir, "MACLIST0.BAK");
            if (File.Exists(maclistPath))
            {
                File.Copy(maclistPath, maclistBak, overwrite: true);
            }

            string maclistTmp = Path.Combine(omgDir, "MACLIST0.TMP");
            using (var fs = new FileStream(maclistTmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(updatedMaclist, 0, updatedMaclist.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(maclistTmp, maclistPath, overwrite: true);

            byte[] writeIcvPayload = new byte[1028];
            writeIcvPayload[0] = 0x04;
            writeIcvPayload[1] = 0x02;
            updatedIcv.CopyTo(writeIcvPayload.AsSpan(4, IcvSize));
            if (!WorkerScsiClient.Execute(volumeHandle, [0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBC, 0x04, 0x04, 0x34, 0x00], 0x00, writeIcvPayload))
            {
                // Roll back MACLIST0.DAT from MACLIST0.BAK if Hardware ICV write fails
                if (File.Exists(maclistBak))
                {
                    File.Copy(maclistBak, maclistPath, overwrite: true);
                }
                return new WorkerCommitResponseDto { Success = false, ErrorMessage = "SCSI Write Hardware ICV (0xA3/0x34) failed; MACLIST0.DAT restored from backup." };
            }

            // Finalize PC OpenMG state via IOmgHdwmSac3::commit (slot 7)
            var commitFn = (delegate* unmanaged[Stdcall]<nint, int>)sacVtbl[7];
            _ = commitFn(sacPtr);

            return new WorkerCommitResponseDto
            {
                Success = true,
                UpdatedMaclist0Base64 = Convert.ToBase64String(updatedMaclist),
                UpdatedIcv168Base64 = Convert.ToBase64String(updatedIcv),
                MintedSlots = mintedSlots.ToArray()
            };
        }
        finally
        {
            if (volumeHandle != nint.Zero && volumeHandle != new nint(-1))
            {
                WorkerScsiClient.CloseVolume(volumeHandle);
            }
            if (sacPtr != nint.Zero)
            {
                Marshal.Release(sacPtr);
            }
            if (authPtr != nint.Zero)
            {
                Marshal.Release(authPtr);
            }
            if (shouldUninit)
            {
                CoUninitialize();
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out nint ppv);
}

internal static class WorkerScsiClient
{
    private const uint IoctlScsiPassThroughDirect = 0x0004D014u;
    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint FileShareReadWrite = 0x00000003u;
    private const uint OpenExisting = 3u;

    public static nint OpenVolume(string volumeDevicePath) =>
        CreateFileW(volumeDevicePath, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);

    public static void CloseVolume(nint handle) => CloseHandle(handle);

    public static bool Execute(nint volumeHandle, ReadOnlySpan<byte> cdb12, byte dataDirection, byte[] dataBuffer)
    {
        const int sptSize = 44; // 32-bit x86 SCSI_PASS_THROUGH_DIRECT
        const int senseSize = 32;
        byte[] spt = new byte[sptSize + senseSize];

        GCHandle pinned = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(spt.AsSpan(0, 2), sptSize);
            spt[6] = (byte)cdb12.Length;
            spt[7] = senseSize;
            spt[8] = dataDirection;
            BinaryPrimitives.WriteUInt32LittleEndian(spt.AsSpan(12, 4), (uint)dataBuffer.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(spt.AsSpan(16, 4), 15u);
            BinaryPrimitives.WriteInt32LittleEndian(spt.AsSpan(20, 4), pinned.AddrOfPinnedObject().ToInt32());
            BinaryPrimitives.WriteUInt32LittleEndian(spt.AsSpan(24, 4), sptSize);
            cdb12.CopyTo(spt.AsSpan(28, cdb12.Length));

            bool ok = DeviceIoControl(volumeHandle, IoctlScsiPassThroughDirect, spt, (uint)spt.Length, spt, (uint)spt.Length, out _, 0);
            return ok && spt[2] == 0x00;
        }
        finally
        {
            pinned.Free();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        nint hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
