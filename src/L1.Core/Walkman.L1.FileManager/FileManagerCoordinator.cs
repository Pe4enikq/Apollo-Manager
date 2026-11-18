using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L1.FileManager;

/// <summary>
/// Read-only Virtual File System snapshot of a connected Walkman's storage layout.
/// </summary>
public sealed record DeviceVfsSnapshot(
    ConnectedDeviceIdentity Device,
    VolumeSpaceInfo SpaceInfo,
    IReadOnlyList<string> DatabaseTableFiles,
    IReadOnlyList<string> AudioTrackFiles,
    IReadOnlyList<string> ScreensaverFiles);

/// <summary>
/// L1 VFS and transfer coordinator indexing Walkman directory trees through ITransportDriver.
/// </summary>
public sealed class FileManagerCoordinator
{
    private readonly IModuleOrchestrator _orchestrator;

    public FileManagerCoordinator(IModuleOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public IReadOnlyCollection<string> GetActiveModules() => _orchestrator.LoadedModuleIds;

    /// <summary>
    /// Mounts a read-only DeviceVfsSnapshot for the active device via the dynamically loaded ITransportDriver.
    /// </summary>
    public async Task<DeviceVfsSnapshot> MountDeviceVfsSnapshotAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var space = await transport.GetSpaceInfoAsync(device, ct).ConfigureAwait(false);

        var tableFiles = new List<string>();
        var trackFiles = new List<string>();
        var screensaverFiles = new List<string>();

        if (await transport.ExistsAsync(device, "OMGAUDIO", ct).ConfigureAwait(false))
        {
            var rootOmgFiles = await transport.ListFilesAsync(device, "OMGAUDIO", ct).ConfigureAwait(false);
            foreach (var file in rootOmgFiles)
            {
                if (file.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase))
                {
                    tableFiles.Add(file);
                }
            }

            if (!string.IsNullOrWhiteSpace(device.VolumeRootPath))
            {
                var omgFullPath = Path.Combine(device.VolumeRootPath, "OMGAUDIO");
                if (Directory.Exists(omgFullPath))
                {
                    foreach (var subDir in Directory.GetDirectories(omgFullPath, "10F*", SearchOption.TopDirectoryOnly)
                        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var folderName = Path.GetFileName(subDir);
                        var omaEntries = await transport.ListFilesAsync(device, $"OMGAUDIO/{folderName}", ct).ConfigureAwait(false);
                        trackFiles.AddRange(omaEntries.Where(f => f.EndsWith(".OMA", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        foreach (string screenDir in new[] { "SCREEN", "NWWM-SCR" })
        {
            if (await transport.ExistsAsync(device, screenDir, ct).ConfigureAwait(false))
            {
                var nwmFiles = await transport.ListFilesAsync(device, screenDir, ct).ConfigureAwait(false);
                screensaverFiles.AddRange(nwmFiles.Where(f => f.EndsWith(".nwm", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new DeviceVfsSnapshot(
            Device: device,
            SpaceInfo: space,
            DatabaseTableFiles: tableFiles,
            AudioTrackFiles: trackFiles,
            ScreensaverFiles: screensaverFiles);
    }
}
