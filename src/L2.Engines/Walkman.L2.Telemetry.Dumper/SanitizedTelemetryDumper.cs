using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;

namespace Walkman.L2.Telemetry.Dumper;

/// <summary>
/// Read-only sanitized diagnostic collector generating ~50 KB .zip bundles
/// containing metadata tables and truncated .OMA headers (zero copyrighted audio).
/// </summary>
public sealed class SanitizedTelemetryDumper : ITelemetryDumper
{
    private const int MaxTableRawBytes = 32 * 1024;
    private const int TableHeadSliceBytes = 4096;
    private const int MaxOmaSampleFiles = 5;
    private const int MaxOmaHeadBytes = 4160;
    private const int AudioSampleTailBytes = 64;

    private static readonly string[] IgnoredDirectories =
    [
        "System Volume Information",
        "$RECYCLE.BIN"
    ];

    private static readonly string[] TableSourceDirectories =
    [
        "OMGAUDIO",
        "NWWM",
        "MP3FM",
        "ESYS",
        "HIFI"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<DiagnosticDumpResult> CreateSanitizedDumpAsync(
        DiagnosticDumpRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputZipPath);

        var outputFullPath = Path.GetFullPath(request.OutputZipPath);
        var parentDir = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        int capturedFiles = 0;

        await using (var zipStream = new FileStream(outputFullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            // 1. Write probe_manifest.json
            var manifestPayload = new
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                VendorIdHex = $"0x{request.Probe.VendorId:X4}",
                ProductIdHex = $"0x{request.Probe.ProductId:X4}",
                request.Probe.UsbProductString,
                VolumeSerialHex = request.Probe.VolumeSerialNumber.HasValue
                    ? $"0x{request.Probe.VolumeSerialNumber.Value:X8}"
                    : null,
                request.Probe.TotalVolumeBytes,
                HardwareDvIdSegmentHex = request.Probe.HardwareDvIdSegment.HasValue
                    ? $"0x{request.Probe.HardwareDvIdSegment.Value:X4}"
                    : null,
                HardwareDvIdKeyHex = request.Probe.HardwareDvIdKey.HasValue
                    ? $"0x{request.Probe.HardwareDvIdKey.Value:X8}"
                    : null,
                request.Probe.PresentRootFolders,
                request.Probe.PresentOmgAudioFiles
            };

            await WriteTextEntryAsync(
                archive,
                "probe_manifest.json",
                JsonSerializer.Serialize(manifestPayload, JsonOptions),
                ct).ConfigureAwait(false);
            capturedFiles++;

            var rootPath = request.Probe.VolumeRootPath;
            if (!string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath))
            {
                // 2. Write directory_listing.txt
                var allFiles = EnumerateFilesSafely(rootPath);
                var listingBuilder = new StringBuilder();
                foreach (var (relPath, size) in allFiles)
                {
                    listingBuilder.AppendLine($"{size,12}  {relPath}");
                }

                await WriteTextEntryAsync(archive, "directory_listing.txt", listingBuilder.ToString(), ct)
                    .ConfigureAwait(false);
                capturedFiles++;

                // 3. Copy .DAT metadata tables (head-sliced if > 32 KB)
                foreach (var dirName in TableSourceDirectories)
                {
                    var dirFull = ResolveCaseInsensitiveDirectory(rootPath, dirName);
                    if (dirFull is null)
                    {
                        continue;
                    }

                    foreach (var file in Directory.GetFiles(dirFull, "*.DAT", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        var fileInfo = new FileInfo(file);
                        string entryBase = $"tables/{dirName}/{Path.GetFileName(file)}";

                        if (fileInfo.Length <= MaxTableRawBytes)
                        {
                            byte[] raw = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                            await WriteBinaryEntryAsync(archive, entryBase, raw, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            byte[] head = await ReadPrefixBytesAsync(file, TableHeadSliceBytes, ct).ConfigureAwait(false);
                            await WriteBinaryEntryAsync(archive, $"{entryBase}.head4k", head, ct).ConfigureAwait(false);
                        }

                        capturedFiles++;
                    }
                }

                // 4. Copy truncated header-only slices from up to 5 .OMA tracks (zero full audio payloads)
                var omgFull = ResolveCaseInsensitiveDirectory(rootPath, "OMGAUDIO");
                if (omgFull is not null)
                {
                    var omaFiles = Directory.GetDirectories(omgFull, "10F*", SearchOption.TopDirectoryOnly)
                        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                        .SelectMany(d => Directory.GetFiles(d, "*.OMA", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        .Take(MaxOmaSampleFiles);

                    foreach (var omaPath in omaFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        byte[] headerSlice = await ReadSanitizedOmaHeaderAsync(omaPath, ct).ConfigureAwait(false);
                        string entryName = $"oma_headers/{Path.GetFileName(omaPath)}.head";
                        await WriteBinaryEntryAsync(archive, entryName, headerSlice, ct).ConfigureAwait(false);
                        capturedFiles++;
                    }
                }
            }
        }

        long zipBytes = new FileInfo(outputFullPath).Length;
        return new DiagnosticDumpResult(true, outputFullPath, zipBytes, capturedFiles);
    }

    private static async Task<byte[]> ReadSanitizedOmaHeaderAsync(string omaPath, CancellationToken ct)
    {
        byte[] initial = await ReadPrefixBytesAsync(omaPath, MaxOmaHeadBytes, ct).ConfigureAwait(false);
        if (initial.Length < 10)
        {
            return initial;
        }

        if (initial[0] == (byte)'e' && initial[1] == (byte)'a' && initial[2] == (byte)'3')
        {
            int ea3BodySize =
                ((initial[6] & 0x7F) << 21) |
                ((initial[7] & 0x7F) << 14) |
                ((initial[8] & 0x7F) << 7) |
                (initial[9] & 0x7F);

            int targetLength = Math.Min(MaxOmaHeadBytes, 10 + ea3BodySize + 96 + AudioSampleTailBytes);
            if (targetLength > 0 && targetLength < initial.Length)
            {
                return initial[..targetLength];
            }
        }

        return initial;
    }

    private static async Task<byte[]> ReadPrefixBytesAsync(string filePath, int maxBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int toRead = (int)Math.Min(fs.Length, maxBytes);
        var buffer = new byte[toRead];
        int offset = 0;
        while (offset < toRead)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(offset, toRead - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == toRead ? buffer : buffer[..offset];
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await WriteBinaryEntryAsync(archive, entryName, bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteBinaryEntryAsync(
        ZipArchive archive,
        string entryName,
        byte[] data,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<(string RelativePath, long Length)> EnumerateFilesSafely(string rootPath)
    {
        var results = new List<(string, long)>();
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootPath);

        while (pendingDirs.Count > 0)
        {
            var currentDir = pendingDirs.Pop();
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var dir in subDirs)
            {
                var name = Path.GetFileName(dir);
                if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                pendingDirs.Push(dir);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    string rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    results.Add((rel, info.Length));
                }
                catch (IOException)
                {
                    // Skip transient/unreadable entries cleanly.
                }
            }
        }

        return results
            .OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveCaseInsensitiveDirectory(string rootPath, string folderName)
    {
        return Directory.GetDirectories(rootPath)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Mediator handler executing sanitized telemetry collection on behalf of L3.DumpWizard.
/// </summary>
public sealed class GenerateDiagnosticDumpCommandHandler
    : ICommandHandler<GenerateDiagnosticDumpCommand, DiagnosticDumpResult>
{
    private readonly ITelemetryDumper _dumper;

    public GenerateDiagnosticDumpCommandHandler(ITelemetryDumper dumper)
    {
        _dumper = dumper ?? throw new ArgumentNullException(nameof(dumper));
    }

    public Task<DiagnosticDumpResult> HandleAsync(
        GenerateDiagnosticDumpCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dumper.CreateSanitizedDumpAsync(command.Request, ct);
    }
}
