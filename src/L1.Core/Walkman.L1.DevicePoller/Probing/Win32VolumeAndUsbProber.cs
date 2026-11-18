using System.Globalization;
using System.Text.RegularExpressions;
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
    string? Description);

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

            string? effectiveDesc = description;
            if ((string.IsNullOrWhiteSpace(effectiveDesc) ||
                 effectiveDesc.Equals("USB Mass Storage Device", StringComparison.OrdinalIgnoreCase)) &&
                sonyStorDescriptions.Length == 1)
            {
                effectiveDesc = sonyStorDescriptions[0];
            }

            devices.Add(new UsbSonyDeviceInfo(vid, pid, instanceId, effectiveDesc));
        }

        return devices;
    }

    /// <summary>
    /// Scans mounted drives (Removable first, skipping A:\ and B:\) for Walkman marker folders.
    /// </summary>
    public IReadOnlyList<MountedVolumeSnapshot> EnumerateWalkmanCandidateVolumes()
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

            if (hasMarker)
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

    private static MountedVolumeSnapshot InspectVolumeRoot(
        string rootPath,
        DriveType driveType,
        string? driveFormat,
        long totalBytes,
        long freeBytes,
        uint? overrideSerial = null)
    {
        var rootFolders = Directory.GetDirectories(rootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();

        string? omgFolder = rootFolders.FirstOrDefault(f =>
            string.Equals(f, "OMGAUDIO", StringComparison.OrdinalIgnoreCase));

        string[] omgFiles = Array.Empty<string>();
        if (omgFolder is not null)
        {
            var omgPath = Path.Combine(rootPath, omgFolder);
            if (Directory.Exists(omgPath))
            {
                omgFiles = Directory.GetFiles(omgPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToArray();
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
