using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Walkman.Contracts.Devices;
using Walkman.L1.DevicePoller.Interop;
using Windows.Win32;

namespace Walkman.L1.DevicePoller.Probing;

/// <summary>
/// Connected Sony USB PnP device discovered via Windows SetupAPI (zero WMI).
/// </summary>
public sealed record UsbSonyDeviceInfo(
    ushort VendorId,
    ushort ProductId,
    string InstanceId,
    string? Description,
    string? SerialOrContainerId = null);

/// <summary>
/// Mounted storage volume and Walkman filesystem markers collected in read-only mode.
/// </summary>
public sealed record MountedVolumeSnapshot(
    string RootPath,
    DriveType DriveType,
    string? DriveFormat,
    long TotalBytes,
    long FreeBytes,
    uint? VolumeSerialNumber,
    IReadOnlyList<string> PresentRootFolders,
    IReadOnlyList<string> PresentOmgAudioFiles);

/// <summary>
/// Fast (<5 ms) hardware and filesystem prober combining CsWin32 volume calls and SetupAPI USB queries.
/// </summary>
public sealed partial class Win32VolumeAndUsbProber
{
    private const ushort SonyVendorId = 0x054C;

    private static readonly string[] KnownWalkmanMarkers =
    [
        "OMGAUDIO",
        "NWWM",
        "NWWM-SCR",
        "SCREEN",
        "MP3FM",
        "ESYS",
        "HIFI",
        "HMDHIFI"
    ];

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled)]
    private static partial Regex VidPidRegex();

    /// <summary>
    /// Enumerates present Sony USB devices (VID_054C) and enriches descriptions from USBSTOR when available.
    /// </summary>
    public IReadOnlyList<UsbSonyDeviceInfo> EnumerateSonyUsbDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<UsbSonyDeviceInfo>();
        }

        var usbEntries = SetupApiNative.EnumeratePresentPnpDevices("USB");
        var usbStorEntries = SetupApiNative.EnumeratePresentPnpDevices("USBSTOR");

        var sonyStorDescriptions = usbStorEntries
            .Where(e => e.InstanceId.Contains("VEN_SONY", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Description)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        var devices = new List<UsbSonyDeviceInfo>();
        foreach (var (instanceId, description) in usbEntries)
        {
            var match = VidPidRegex().Match(instanceId);
            if (!match.Success)
            {
                continue;
            }

            if (!ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vid) ||
                vid != SonyVendorId)
            {
                continue;
            }

            if (!ushort.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pid))
            {
                continue;
            }

            string? serialOrContainer = ExtractUsbInstanceSerial(instanceId);

            string? effectiveDesc = description;
            if (string.IsNullOrWhiteSpace(effectiveDesc) ||
                effectiveDesc.Equals("USB Mass Storage Device", StringComparison.OrdinalIgnoreCase))
            {
                string? matchedStorDesc = null;
                if (!string.IsNullOrWhiteSpace(serialOrContainer))
                {
                    matchedStorDesc = usbStorEntries
                        .FirstOrDefault(e =>
                            e.InstanceId.Contains($"\\{serialOrContainer}&", StringComparison.OrdinalIgnoreCase) ||
                            e.InstanceId.EndsWith($"\\{serialOrContainer}", StringComparison.OrdinalIgnoreCase))
                        .Description;
                }

                if (!string.IsNullOrWhiteSpace(matchedStorDesc))
                {
                    effectiveDesc = matchedStorDesc;
                }
                else if (sonyStorDescriptions.Length == 1)
                {
                    effectiveDesc = sonyStorDescriptions[0];
                }
            }

            devices.Add(new UsbSonyDeviceInfo(vid, pid, instanceId, effectiveDesc, serialOrContainer));
        }

        return devices;
    }

    private static string? ExtractUsbInstanceSerial(string instanceId)
    {
        int firstSlash = instanceId.IndexOf('\\');
        if (firstSlash < 0)
        {
            return null;
        }

        int secondSlash = instanceId.IndexOf('\\', firstSlash + 1);
        if (secondSlash < 0 || secondSlash + 1 >= instanceId.Length)
        {
            return null;
        }

        string tail = instanceId[(secondSlash + 1)..];
        int amp = tail.IndexOf('&');
        return amp >= 0 ? tail[..amp] : tail;
    }

    /// <summary>
    /// Correlates a mounted volume root (e.g. "D:\") to its exact Sony USB device via
    /// IOCTL_STORAGE_QUERY_PROPERTY (VendorId, ProductId, SerialNumber) when multiple Sony devices are present.
    /// </summary>
    public UsbSonyDeviceInfo? ResolveUsbDeviceForVolume(
        string volumeRootPath,
        IReadOnlyList<UsbSonyDeviceInfo> usbDevices,
        bool allowFallbackToFirst = true)
    {
        if (usbDevices.Count == 0)
        {
            return null;
        }

        if ((usbDevices.Count == 1 && allowFallbackToFirst) || !OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
        {
            return allowFallbackToFirst ? usbDevices[0] : null;
        }

        var (vendorId, productId, serialNumber) = TryQueryStorageDeviceDescriptor(volumeRootPath);
        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            var serialMatch = usbDevices.FirstOrDefault(d =>
                (!string.IsNullOrWhiteSpace(d.SerialOrContainerId) &&
                 d.SerialOrContainerId.Contains(serialNumber, StringComparison.OrdinalIgnoreCase)) ||
                d.InstanceId.Contains(serialNumber, StringComparison.OrdinalIgnoreCase));
            if (serialMatch is not null)
            {
                return serialMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(productId))
        {
            var prodMatch = usbDevices.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.Description) &&
                d.Description.Contains(productId, StringComparison.OrdinalIgnoreCase));
            if (prodMatch is not null)
            {
                return prodMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(vendorId) &&
            vendorId.Contains("SONY", StringComparison.OrdinalIgnoreCase))
        {
            return usbDevices[0];
        }

        return allowFallbackToFirst ? usbDevices[0] : null;
    }

    private static unsafe (string? VendorId, string? ProductId, string? SerialNumber) TryQueryStorageDeviceDescriptor(string volumeRootPath)
    {
        if (string.IsNullOrWhiteSpace(volumeRootPath))
        {
            return (null, null, null);
        }

        string? root = Path.GetPathRoot(Path.GetFullPath(volumeRootPath));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return (null, null, null);
        }

        string devicePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
        using var handle = CreateFileW(
            devicePath,
            0u, // Metadata-only query
            0x00000003u, // FILE_SHARE_READ | FILE_SHARE_WRITE
            IntPtr.Zero,
            3u, // OPEN_EXISTING
            0u,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return (null, null, null);
        }

        const uint IoctlStorageQueryProperty = 0x002D1400u;
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = 0, // StorageDeviceProperty
            QueryType = 0   // PropertyStandardQuery
        };

        byte[] outBuffer = new byte[1024];
        fixed (byte* pOut = outBuffer)
        {
            if (!DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    ref query,
                    (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    (IntPtr)pOut,
                    (uint)outBuffer.Length,
                    out uint bytesReturned,
                    IntPtr.Zero) || bytesReturned < 36)
            {
                return (null, null, null);
            }
        }

        uint vendorOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outBuffer.AsSpan(12, 4));
        uint productOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outBuffer.AsSpan(16, 4));
        uint serialOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outBuffer.AsSpan(24, 4));

        return (
            ReadNullTerminatedAscii(outBuffer, vendorOffset),
            ReadNullTerminatedAscii(outBuffer, productOffset),
            ReadNullTerminatedAscii(outBuffer, serialOffset));
    }

    private static string? ReadNullTerminatedAscii(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length)
        {
            return null;
        }

        int start = (int)offset;
        int end = Array.IndexOf(buffer, (byte)0, start);
        if (end < 0)
        {
            end = buffer.Length;
        }

        string text = System.Text.Encoding.ASCII.GetString(buffer, start, end - start).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Scans mounted drives (Removable first, skipping A:\ and B:\) for Walkman marker folders.
    /// </summary>
    public IReadOnlyList<MountedVolumeSnapshot> EnumerateWalkmanCandidateVolumes(
        IReadOnlyList<UsbSonyDeviceInfo>? sonyUsbDevices = null)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d =>
                d.IsReady &&
                !d.Name.StartsWith("A:", StringComparison.OrdinalIgnoreCase) &&
                !d.Name.StartsWith("B:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.DriveType == DriveType.Removable ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<MountedVolumeSnapshot>();

        foreach (var drive in drives)
        {
            // Check GetDriveTypeW on Windows to align with Win32 volume filtering; ignore Network/CDRom/Ram here.
            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
            {
                uint winType = PInvoke.GetDriveType(drive.Name);
                // DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3
                if (winType != 2 && winType != 3)
                {
                    continue;
                }
            }
            else if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
            {
                continue;
            }

            var snapshot = InspectVolumeRoot(drive.Name, drive.DriveType, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace);

            bool hasMarker = snapshot.PresentRootFolders.Any(f =>
                KnownWalkmanMarkers.Contains(f, StringComparer.OrdinalIgnoreCase));

            bool isFreshlyFormattedSonyVolume = !hasMarker &&
                drive.DriveType == DriveType.Removable &&
                sonyUsbDevices is { Count: > 0 } &&
                ResolveUsbDeviceForVolume(drive.Name, sonyUsbDevices, allowFallbackToFirst: false) is not null;

            if (hasMarker || isFreshlyFormattedSonyVolume)
            {
                results.Add(snapshot);
            }
        }

        return results;
    }

    /// <summary>
    /// Reads root marker folders, OMGAUDIO table filenames, and FAT volume serial from a mounted drive or directory fixture.
    /// Strictly read-only.
    /// </summary>
    public MountedVolumeSnapshot InspectDirectoryAsVolume(
        string directoryRoot,
        long? overrideTotalBytes = null,
        long? overrideFreeBytes = null,
        uint? overrideVolumeSerial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryRoot);
        var fullPath = Path.GetFullPath(directoryRoot);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Volume/fixture root directory not found: '{fullPath}'.");
        }

        var rootDrive = Path.GetPathRoot(fullPath);
        long totalBytes = overrideTotalBytes ?? 0;
        long freeBytes = overrideFreeBytes ?? 0;
        string? format = null;
        DriveType driveType = DriveType.Removable;

        if (!string.IsNullOrWhiteSpace(rootDrive))
        {
            var di = new DriveInfo(rootDrive);
            if (di.IsReady)
            {
                if (!overrideTotalBytes.HasValue)
                {
                    totalBytes = di.TotalSize;
                }

                if (!overrideFreeBytes.HasValue)
                {
                    freeBytes = di.AvailableFreeSpace;
                }

                format = di.DriveFormat;
                driveType = di.DriveType;
            }
        }

        return InspectVolumeRoot(fullPath, driveType, format, totalBytes, freeBytes, overrideVolumeSerial);
    }

    /// <summary>
    /// Reads the 32-bit FAT/NTFS volume serial number for a drive root via CsWin32 GetVolumeInformationW.
    /// </summary>
    public static unsafe uint? TryGetWindowsVolumeSerial(string volumeRootPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600) || string.IsNullOrWhiteSpace(volumeRootPath))
        {
            return null;
        }

        string normalized = EnsureTrailingSeparator(volumeRootPath);
        uint serial = 0;
        uint maxCompLen = 0;
        uint flags = 0;

        fixed (char* pVol = new char[261])
        fixed (char* pFs = new char[261])
        {
            bool ok = PInvoke.GetVolumeInformation(
                normalized,
                pVol,
                261,
                &serial,
                &maxCompLen,
                &flags,
                pFs,
                261);

            return ok ? serial : null;
        }
    }

    public unsafe VolumeBpbDiagnostics? TryReadFat16Bpb(string volumeRootPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600) || string.IsNullOrWhiteSpace(volumeRootPath))
        {
            return null;
        }

        string? root = Path.GetPathRoot(Path.GetFullPath(volumeRootPath));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        string devicePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
        using var handle = CreateFileW(
            devicePath,
            0x80000000u, // GENERIC_READ
            0x00000003u, // FILE_SHARE_READ | FILE_SHARE_WRITE
            IntPtr.Zero,
            3u, // OPEN_EXISTING
            0u,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) // ERROR_ACCESS_DENIED
            {
                return new VolumeBpbDiagnostics(
                    OemName: string.Empty,
                    BytesPerSector: 0,
                    SectorsPerCluster: 0,
                    ReservedSectors: 0,
                    FatCount: 0,
                    FileSystemType: string.Empty,
                    HasWindowsFormatWarning: false,
                    WarningMessage: "FAT16 BPB verification requires Administrator privileges (access denied).",
                    VerificationState: BpbVerificationState.UnverifiedAccessDenied);
            }

            return null;
        }

        byte[] sector0 = new byte[512];
        if (!ReadFile(handle, sector0, 512, out int bytesRead, IntPtr.Zero) || bytesRead < 62)
        {
            return null;
        }

        string oem = System.Text.Encoding.ASCII.GetString(sector0, 3, 8).TrimEnd('\0', ' ');
        ushort bps = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(11, 2));
        byte spc = sector0[13];
        ushort rsvd = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(14, 2));
        byte fats = sector0[16];
        string fsType = System.Text.Encoding.ASCII.GetString(sector0, 54, 8).TrimEnd('\0', ' ');

        return new VolumeBpbDiagnostics(
            OemName: oem,
            BytesPerSector: bps,
            SectorsPerCluster: spc,
            ReservedSectors: rsvd,
            FatCount: fats,
            FileSystemType: fsType,
            HasWindowsFormatWarning: false,
            WarningMessage: null);
    }

    public static VolumeBpbDiagnostics EvaluateBpbPolicy(VolumeBpbDiagnostics raw, StrictFat16BpbRule? rule)
    {
        if (raw.VerificationState == BpbVerificationState.UnverifiedAccessDenied || rule is null)
        {
            return raw;
        }

        bool oemWarn = rule.WarnOnWindowsOem && raw.OemName.StartsWith("MSDOS", StringComparison.OrdinalIgnoreCase);
        bool rsvdWarn = raw.ReservedSectors != rule.RequiredReservedSectors;
        if (!oemWarn && !rsvdWarn)
        {
            return raw with
            {
                HasWindowsFormatWarning = false,
                WarningMessage = null,
                VerificationState = BpbVerificationState.VerifiedOk
            };
        }

        return raw with
        {
            HasWindowsFormatWarning = true,
            VerificationState = BpbVerificationState.InvalidWindowsFormat,
            WarningMessage =
                $"Volume was formatted by Windows (OEM='{raw.OemName}', Rsvd={raw.ReservedSectors}, expected Rsvd={rule.RequiredReservedSectors}), which causes FORMAT ERROR on this player — run on-device Format first."
        };
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        [Out] byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    private static T ExecuteDirectoryIoWithRetry<T>(Func<T> action, T fallback)
    {
        int[] backoffsMs = [35, 80];
        for (int attempt = 0; attempt <= backoffsMs.Length; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (ex is not DirectoryNotFoundException and not FileNotFoundException && attempt < backoffsMs.Length)
            {
                Thread.Sleep(backoffsMs[attempt]);
            }
            catch (IOException)
            {
                return fallback;
            }
        }

        return fallback;
    }

    private static MountedVolumeSnapshot InspectVolumeRoot(
        string rootPath,
        DriveType driveType,
        string? driveFormat,
        long totalBytes,
        long freeBytes,
        uint? overrideSerial = null)
    {
        var rootFolders = ExecuteDirectoryIoWithRetry(
            () => Directory.GetDirectories(rootPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray(),
            Array.Empty<string>());

        string? omgFolder = rootFolders.FirstOrDefault(f =>
            string.Equals(f, "OMGAUDIO", StringComparison.OrdinalIgnoreCase));

        string[] omgFiles = Array.Empty<string>();
        if (omgFolder is not null)
        {
            var omgPath = Path.Combine(rootPath, omgFolder);
            if (Directory.Exists(omgPath))
            {
                omgFiles = ExecuteDirectoryIoWithRetry(
                    () => Directory.GetFiles(omgPath)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .ToArray(),
                    Array.Empty<string>());
            }
        }

        uint? serial = overrideSerial;
        if (!serial.HasValue && IsDriveRoot(rootPath))
        {
            serial = TryGetWindowsVolumeSerial(rootPath);
        }

        return new MountedVolumeSnapshot(
            RootPath: rootPath,
            DriveType: driveType,
            DriveFormat: driveFormat,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes,
            VolumeSerialNumber: serial,
            PresentRootFolders: rootFolders,
            PresentOmgAudioFiles: omgFiles);
    }

    private static bool IsDriveRoot(string path)
    {
        var full = EnsureTrailingSeparator(Path.GetFullPath(path));
        var root = EnsureTrailingSeparator(Path.GetPathRoot(full) ?? string.Empty);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
