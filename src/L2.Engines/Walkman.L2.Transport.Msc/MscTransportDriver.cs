using System.Runtime.InteropServices;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Transport.Msc;

/// <summary>
/// Mass Storage Class (MSC) filesystem transport enforcing volume serial binding,
/// uppercase 8.3 ASCII path rules on writes, safe temp staging, and hardware flush-to-disk.
/// </summary>
public sealed class MscTransportDriver : ITransportDriver
{
    private readonly Func<string, uint?>? _customVolumeSerialReader;

    public MscTransportDriver()
        : this(null)
    {
    }

    public MscTransportDriver(Func<string, uint?>? volumeSerialReader)
    {
        _customVolumeSerialReader = volumeSerialReader;
    }

    public TransportProtocol Protocol => TransportProtocol.MassStorage;

    public Task<VolumeSpaceInfo> GetSpaceInfoAsync(ConnectedDeviceIdentity device, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rootPath = ValidateDeviceRoot(device);

        var driveRoot = Path.GetPathRoot(rootPath) ?? rootPath;
        var driveInfo = new DriveInfo(driveRoot);

        uint serial = ResolveEffectiveSerial(rootPath) ?? device.VolumeSerialNumber ?? 0u;
        long totalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0L;
        long freeBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0L;

        return Task.FromResult(new VolumeSpaceInfo(totalBytes, freeBytes, serial));
    }

    public Task<bool> ExistsAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = ResolveSafePath(ValidateDeviceRoot(device), relativePath, enforceStrict8Dot3: false, out _);
        return Task.FromResult(File.Exists(fullPath) || Directory.Exists(fullPath));
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(ConnectedDeviceIdentity device, string relativeDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rootPath = ValidateDeviceRoot(device);

        string targetDir = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory is "." or "/" or "\\"
            ? rootPath
            : ResolveSafePath(rootPath, relativeDirectory, enforceStrict8Dot3: false, out _);

        if (!Directory.Exists(targetDir))
        {
            return Array.Empty<string>();
        }

        return await ExecuteWithIoRetryAsync<IReadOnlyList<string>>(
            _ =>
            {
                var files = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetRelativePath(rootPath, f).Replace('\\', '/'))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<string>>(files);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFileAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = ResolveSafePath(ValidateDeviceRoot(device), relativePath, enforceStrict8Dot3: false, out _);

        return await ExecuteWithIoRetryAsync(async token =>
        {
            await using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[fs.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead == buffer.Length ? buffer : buffer[..totalRead];
        }, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFilePrefixAsync(
        ConnectedDeviceIdentity device,
        string relativePath,
        int maxBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxBytes <= 0)
        {
            return Array.Empty<byte>();
        }

        var fullPath = ResolveSafePath(ValidateDeviceRoot(device), relativePath, enforceStrict8Dot3: false, out _);
        return await ExecuteWithIoRetryAsync(async token =>
        {
            await using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: Math.Min(maxBytes, 65536),
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            int toRead = (int)Math.Min(fs.Length, maxBytes);
            var buffer = new byte[toRead];
            int offset = 0;
            while (offset < toRead)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(offset, toRead - offset), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            return offset == toRead ? buffer : buffer[..offset];
        }, ct).ConfigureAwait(false);
    }

    public Task<long> GetFileLengthAsync(
        ConnectedDeviceIdentity device,
        string relativePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = ResolveSafePath(ValidateDeviceRoot(device), relativePath, enforceStrict8Dot3: false, out _);
        return ExecuteWithIoRetryAsync(_ =>
        {
            var info = new FileInfo(fullPath);
            return Task.FromResult(info.Exists ? info.Length : 0L);
        }, ct);
    }

    public async Task WriteFileAtomicAsync(
        ConnectedDeviceIdentity device,
        string relativePath,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rootPath = ValidateDeviceRoot(device);

        if (device.BpbDiagnostics?.VerificationState == BpbVerificationState.InvalidWindowsFormat)
        {
            throw new InvalidOperationException(
                device.BpbDiagnostics.WarningMessage
                ?? "Write blocked: device volume has an invalid Windows FAT16 BPB. Run on-device Format first.");
        }

        // Guard 2: Enforce strict uppercase FAT 8.3 ASCII path format on writes.
        var finalPath = ResolveSafePath(rootPath, relativePath, enforceStrict8Dot3: true, out _);
        var targetDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException($"Unable to determine directory for '{finalPath}'.");

        Directory.CreateDirectory(targetDirectory);

        await ExecuteWithIoRetryAsync(async token =>
        {
            // Guard 1: Verify mounted volume serial matches bound device session on every attempt.
            VerifyVolumeSerialBinding(device, rootPath);
            // Guard 3: Stage into an 8.3 temporary file, flush OS buffers to physical storage, then atomic move.
            string tempName = $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant() + ".TMP";
            string tempPath = Path.Combine(targetDirectory, tempName);

            try
            {
                await using (var fs = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    options: FileOptions.Asynchronous))
                {
                    await fs.WriteAsync(payload, token).ConfigureAwait(false);
                    await fs.FlushAsync(token).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best-effort cleanup of failed staging file.
                    }
                }

                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    public Task DeleteFileAsync(ConnectedDeviceIdentity device, string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rootPath = ValidateDeviceRoot(device);

        if (device.BpbDiagnostics?.VerificationState == BpbVerificationState.InvalidWindowsFormat)
        {
            throw new InvalidOperationException(
                device.BpbDiagnostics.WarningMessage
                ?? "Delete blocked: device volume has an invalid Windows FAT16 BPB. Run on-device Format first.");
        }

        var fullPath = ResolveSafePath(rootPath, relativePath, enforceStrict8Dot3: false, out _);
        return ExecuteWithIoRetryAsync(_ =>
        {
            VerifyVolumeSerialBinding(device, rootPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.CompletedTask;
        }, ct);
    }

    private static async Task<T> ExecuteWithIoRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        int[] backoffsMs = [40, 90, 180, 350];
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException && attempt < backoffsMs.Length)
            {
                await Task.Delay(backoffsMs[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    private static Task ExecuteWithIoRetryAsync(Func<CancellationToken, Task> operation, CancellationToken ct) =>
        ExecuteWithIoRetryAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, ct);

    private void VerifyVolumeSerialBinding(ConnectedDeviceIdentity device, string rootPath)
    {
        if (!device.VolumeSerialNumber.HasValue)
        {
            return;
        }

        uint? actualSerial;
        if (_customVolumeSerialReader is not null)
        {
            actualSerial = _customVolumeSerialReader(rootPath);
        }
        else if (IsDriveRoot(rootPath))
        {
            actualSerial = TryGetWindowsVolumeSerial(rootPath);
        }
        else
        {
            return;
        }

        if (!actualSerial.HasValue)
        {
            throw new InvalidOperationException(
                $"Volume '{rootPath}' is unavailable or disconnected; expected serial 0x{device.VolumeSerialNumber.Value:X8}.");
        }

        if (actualSerial.Value != device.VolumeSerialNumber.Value)
        {
            throw new InvalidOperationException(
                $"Volume serial mismatch on '{rootPath}': expected 0x{device.VolumeSerialNumber.Value:X8}, actual 0x{actualSerial.Value:X8}. Write aborted.");
        }
    }

    private uint? ResolveEffectiveSerial(string rootPath)
    {
        if (_customVolumeSerialReader is not null)
        {
            return _customVolumeSerialReader(rootPath);
        }

        return IsDriveRoot(rootPath) ? TryGetWindowsVolumeSerial(rootPath) : null;
    }

    private static string ValidateDeviceRoot(ConnectedDeviceIdentity device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.VolumeRootPath))
        {
            throw new InvalidOperationException($"Device '{device.DeviceInstanceId}' has no mounted VolumeRootPath.");
        }

        var fullRoot = Path.GetFullPath(device.VolumeRootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Mounted device root '{fullRoot}' does not exist.");
        }

        return EnsureTrailingSeparator(fullRoot);
    }

    private static string ResolveSafePath(
        string normalizedRootWithSep,
        string relativePath,
        bool enforceStrict8Dot3,
        out string normalizedRelative)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path cannot be null or empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('\\') || relativePath.StartsWith('/'))
        {
            throw new ArgumentException($"Path '{relativePath}' must be relative to the device root.", nameof(relativePath));
        }

        var rawSegments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            throw new ArgumentException($"Relative path '{relativePath}' contains no valid path segments.", nameof(relativePath));
        }

        var normalizedSegments = new string[rawSegments.Length];
        for (int i = 0; i < rawSegments.Length; i++)
        {
            string segment = rawSegments[i];
            if (segment is "." or "..")
            {
                throw new ArgumentException($"Path traversal segment '{segment}' is forbidden in '{relativePath}'.", nameof(relativePath));
            }

            if (enforceStrict8Dot3)
            {
                bool isDirectory = i < rawSegments.Length - 1;
                segment = ValidateAndNormalize8Dot3Segment(segment, isDirectory, relativePath);
            }

            normalizedSegments[i] = segment;
        }

        normalizedRelative = Path.Combine(normalizedSegments);
        string combinedFull = Path.GetFullPath(Path.Combine(normalizedRootWithSep, normalizedRelative));

        if (!combinedFull.StartsWith(normalizedRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Resolved path '{combinedFull}' escapes device root '{normalizedRootWithSep}'.", nameof(relativePath));
        }

        return combinedFull;
    }

    private static string ValidateAndNormalize8Dot3Segment(string rawSegment, bool isDirectory, string fullRelativePath)
    {
        string upper = rawSegment.ToUpperInvariant();
        int dotCount = upper.Count(c => c == '.');
        if (dotCount > 1 || (isDirectory && dotCount > 0))
        {
            throw new ArgumentException(
                $"Path segment '{rawSegment}' in '{fullRelativePath}' violates strict FAT 8.3 rules.",
                nameof(fullRelativePath));
        }

        string baseName;
        string extension;
        if (dotCount == 1)
        {
            int dotIndex = upper.IndexOf('.');
            baseName = upper[..dotIndex];
            extension = upper[(dotIndex + 1)..];
            if (extension.Length is < 1 or > 3)
            {
                throw new ArgumentException(
                    $"Extension '{extension}' in '{fullRelativePath}' must be 1..3 ASCII characters.",
                    nameof(fullRelativePath));
            }
        }
        else
        {
            baseName = upper;
            extension = string.Empty;
        }

        if (baseName is "CON" or "PRN" or "AUX" or "NUL" ||
            (baseName.Length == 4 && baseName[3] is >= '1' and <= '9' &&
             (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                $"Path segment '{rawSegment}' in '{fullRelativePath}' is a reserved Windows DOS device name.",
                nameof(fullRelativePath));
        }

        if (baseName.Length is < 1 or > 8)
        {
            throw new ArgumentException(
                $"Base name '{baseName}' in '{fullRelativePath}' exceeds 8-character FAT 8.3 limit.",
                nameof(fullRelativePath));
        }

        if (!IsStrictAscii8Dot3Token(baseName) || (!string.IsNullOrEmpty(extension) && !IsStrictAscii8Dot3Token(extension)))
        {
            throw new ArgumentException(
                $"Segment '{rawSegment}' in '{fullRelativePath}' contains characters forbidden in FAT 8.3 ASCII.",
                nameof(fullRelativePath));
        }

        return string.IsNullOrEmpty(extension) ? baseName : $"{baseName}.{extension}";
    }

    private static bool IsStrictAscii8Dot3Token(string token)
    {
        foreach (char c in token)
        {
            bool valid =
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c is '_' or '-';

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDriveRoot(string path)
    {
        var full = EnsureTrailingSeparator(Path.GetFullPath(path));
        var root = EnsureTrailingSeparator(Path.GetPathRoot(full) ?? string.Empty);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static uint? TryGetWindowsVolumeSerial(string volumeRootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetVolumeInformationW(
            EnsureTrailingSeparator(volumeRootPath),
            null,
            0,
            out uint serial,
            out _,
            out _,
            null,
            0)
            ? serial
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char[]? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}
