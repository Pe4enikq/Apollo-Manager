using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Optional Windows-only out-of-process 32-bit OpenMG ICV signer bridge &amp; SCSI 0xA3/0xA4 harness (Option 1).
/// Zero Sony binaries are bundled; activates only when the host OS already has Sony OpenMG COM runtime
/// and an external 32-bit bridge worker available.
/// </summary>
public sealed class WindowsOpenMgIcvBridge : IOpenMgIcvBridge
{
    private const string WorkerExecutableName = "Walkman.OpenMgIcvWorker.exe";

    private static readonly string DefaultHostRuntimePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Common Files",
        "Sony Shared",
        "OpenMG",
        "hdwmcom.dll");

    private readonly string? _customWorkerPath;
    private readonly string? _customHostRuntimePath;

    public WindowsOpenMgIcvBridge()
        : this(null, null)
    {
    }

    internal WindowsOpenMgIcvBridge(string? customWorkerPath, string? customHostRuntimePath)
    {
        _customWorkerPath = customWorkerPath;
        _customHostRuntimePath = customHostRuntimePath;
    }

    /// <inheritdoc />
    public IcvBridgeCapability ProbeCapability()
    {
        bool isWindows = OperatingSystem.IsWindows();
        if (!isWindows)
        {
            return new IcvBridgeCapability(
                IsSupportedOs: false,
                IsHostRuntimePresent: false,
                IsWorkerAvailable: false,
                StatusMessage: "Option 1 COM ICV Bridge requires Windows; using cross-platform Option 2 slot-recycling.");
        }

        string runtimePath = string.IsNullOrWhiteSpace(_customHostRuntimePath)
            ? DefaultHostRuntimePath
            : _customHostRuntimePath;
        bool hasRuntime = File.Exists(runtimePath);

        string? workerPath = ResolveWorkerExecutablePath();
        bool hasWorker = workerPath is not null;

        string message = (hasRuntime, hasWorker) switch
        {
            (false, _) => $"Host Sony OpenMG COM runtime not found at '{runtimePath}'; using Option 2 slot-recycling.",
            (true, false) => $"Host Sony OpenMG COM runtime present, optional 32-bit worker '{WorkerExecutableName}' not installed; Option 2 slot-recycling active.",
            (true, true) => $"Ready (x86 COM ICV bridge worker: '{workerPath}')."
        };

        return new IcvBridgeCapability(isWindows, hasRuntime, hasWorker, message);
    }

    /// <inheritdoc />
    public HardwareIcvSnapshot? ParseHardwareIcvResponse(ReadOnlySpan<byte> response1028) =>
        CmAadScsiClient.ParseHardwareIcvResponse(response1028);

    /// <inheritdoc />
    public byte[] BuildWriteHardwareIcvPayload(ReadOnlySpan<byte> icv168) =>
        CmAadScsiClient.BuildWriteIcvPayload(icv168);

    /// <inheritdoc />
    public Task<HardwareIcvSnapshot?> ReadHardwareIcvAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => CmAadScsiClient.TryReadHardwareIcv(device.VolumeRootPath), ct);
    }

    /// <inheritdoc />
    public async Task<IcvCommitResult> MintAndCommitSlotsAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<IcvMintTrackRequest> mintRequests,
        IReadOnlyList<int> deleteTrackIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mintRequests);
        ArgumentNullException.ThrowIfNull(deleteTrackIds);
        ct.ThrowIfCancellationRequested();

        var cap = ProbeCapability();
        if (!cap.IsSupportedOs || !cap.IsHostRuntimePresent || !cap.IsWorkerAvailable)
        {
            return new IcvCommitResult(false, null, null, null, cap.StatusMessage);
        }

        string? workerExe = ResolveWorkerExecutablePath();
        if (workerExe is null || string.IsNullOrWhiteSpace(device.VolumeRootPath))
        {
            return new IcvCommitResult(false, null, null, null, "ICV bridge worker path or mounted volume root unavailable.");
        }

        var payload = new
        {
            VolumeRootPath = device.VolumeRootPath,
            DeleteTrackIds = deleteTrackIds,
            MintRequests = mintRequests.Select(m => new
            {
                m.TrackId,
                ContentIdBase64 = Convert.ToBase64String(m.ContentId20),
                PlaintextKeyBase64 = Convert.ToBase64String(m.PlaintextContentKey8)
            }).ToArray()
        };

        var psi = new ProcessStartInfo(workerExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return new IcvCommitResult(false, null, null, null, $"Failed to launch '{workerExe}'.");
        }

        await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
        proc.StandardInput.Close();

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new IcvCommitResult(
                false,
                null,
                null,
                null,
                string.IsNullOrWhiteSpace(stderr)
                    ? $"ICV bridge worker exited with code {proc.ExitCode}."
                    : stderr.Trim());
        }

        var parsed = JsonSerializer.Deserialize<WorkerCommitResponseDto>(stdout);
        if (parsed is null || !parsed.Success)
        {
            return new IcvCommitResult(false, null, null, null, parsed?.ErrorMessage ?? "Worker reported commit failure.");
        }

        var minted = new Dictionary<int, SignedMagicGateSlotTemplate>();
        if (parsed.MintedSlots is not null)
        {
            foreach (var item in parsed.MintedSlots)
            {
                minted[item.TrackId] = new SignedMagicGateSlotTemplate(
                    item.TrackId,
                    Convert.FromBase64String(item.UlinfBase64),
                    item.BklsiBase64 is not null ? Convert.FromBase64String(item.BklsiBase64) : null,
                    item.OlinfBase64 is not null ? Convert.FromBase64String(item.OlinfBase64) : null,
                    Convert.FromBase64String(item.ContentIdBase64),
                    Convert.FromBase64String(item.IvBase64),
                    Convert.FromBase64String(item.MaclistEntryBase64));
            }
        }

        return new IcvCommitResult(
            true,
            parsed.UpdatedMaclist0Base64 is not null ? Convert.FromBase64String(parsed.UpdatedMaclist0Base64) : null,
            parsed.UpdatedIcv168Base64 is not null ? Convert.FromBase64String(parsed.UpdatedIcv168Base64) : null,
            minted,
            null);
    }

    private string? ResolveWorkerExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_customWorkerPath))
        {
            return File.Exists(_customWorkerPath) ? Path.GetFullPath(_customWorkerPath) : null;
        }

        string direct = Path.Combine(AppContext.BaseDirectory, WorkerExecutableName);
        if (File.Exists(direct))
        {
            return direct;
        }

        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "tools", "openmg-icv-worker", WorkerExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private sealed class WorkerCommitResponseDto
    {
        public bool Success { get; set; }
        public string? UpdatedMaclist0Base64 { get; set; }
        public string? UpdatedIcv168Base64 { get; set; }
        public WorkerSlotDto[]? MintedSlots { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class WorkerSlotDto
    {
        public int TrackId { get; set; }
        public string UlinfBase64 { get; set; } = string.Empty;
        public string? BklsiBase64 { get; set; }
        public string? OlinfBase64 { get; set; }
        public string ContentIdBase64 { get; set; } = string.Empty;
        public string IvBase64 { get; set; } = string.Empty;
        public string MaclistEntryBase64 { get; set; } = string.Empty;
    }
}
