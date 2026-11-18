using Walkman.Contracts.Devices;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller.Probing;
using Walkman.L1.Orchestrator.Manifests;

namespace Walkman.L1.DevicePoller;

/// <summary>
/// Full M1 discovery result pairing a raw detection probe, key source tier, and manifest resolution.
/// </summary>
public sealed record PolledDeviceOutcome(
    DeviceDetectionProbe Probe,
    DvIdResolutionResult DvIdResolution,
    DeviceResolutionResult Resolution);

/// <summary>
/// L1 Core Hardware Discovery &amp; Multi-Factor Capability Resolver.
/// Coordinates SetupAPI USB queries, Win32 volume probes, 3-tier DvID extraction, and ALC hydration.
/// </summary>
public sealed class DevicePollerCoordinator
{
    private readonly DeviceManifestRegistry _registry;
    private readonly Win32VolumeAndUsbProber _prober;
    private readonly DvIdHardwareKeyResolver _dvIdResolver;
    private readonly Dictionary<uint, string> _activeSessionsBySerial = new();

    public DevicePollerCoordinator(DeviceManifestRegistry registry)
        : this(registry, new Win32VolumeAndUsbProber(), new DvIdHardwareKeyResolver())
    {
    }

    public DevicePollerCoordinator(
        DeviceManifestRegistry registry,
        Win32VolumeAndUsbProber prober,
        DvIdHardwareKeyResolver dvIdResolver)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
        _dvIdResolver = dvIdResolver ?? throw new ArgumentNullException(nameof(dvIdResolver));
    }

    public DeviceResolutionResult EvaluateProbe(DeviceDetectionProbe probe) =>
        _registry.ResolveDevice(probe);

    /// <summary>
    /// Scans all mounted Walkman candidate drives on the host in strictly read-only mode.
    /// </summary>
    public IReadOnlyList<PolledDeviceOutcome> ScanConnectedDevices(bool allowScsiPassThrough = false)
    {
        var usbDevices = _prober.EnumerateSonyUsbDevices();
        var volumes = _prober.EnumerateWalkmanCandidateVolumes();

        var primaryUsb = usbDevices.FirstOrDefault();
        var outcomes = new List<PolledDeviceOutcome>(volumes.Count);

        foreach (var volume in volumes)
        {
            var dvId = _dvIdResolver.ResolveForVolume(volume.RootPath, allowScsiPassThrough);
            var probe = new DeviceDetectionProbe(
                VendorId: primaryUsb?.VendorId ?? 0,
                ProductId: primaryUsb?.ProductId ?? 0,
                UsbProductString: primaryUsb?.Description,
                VolumeRootPath: volume.RootPath,
                VolumeSerialNumber: volume.VolumeSerialNumber,
                TotalVolumeBytes: volume.TotalBytes,
                HardwareDvIdSegment: dvId.ModelSegment,
                HardwareDvIdKey: dvId.DeviceKey,
                PresentRootFolders: volume.PresentRootFolders,
                PresentOmgAudioFiles: volume.PresentOmgAudioFiles);

            var resolution = _registry.ResolveDevice(probe);
            if (resolution.IsRecognized && resolution.Identity is not null && dvId.RawDvIdBytes is not null)
            {
                resolution = resolution with
                {
                    Identity = resolution.Identity with { RawDvIdBytes = dvId.RawDvIdBytes }
                };
            }

            outcomes.Add(new PolledDeviceOutcome(probe, dvId, resolution));
        }

        return outcomes;
    }

    /// <summary>
    /// Inspects a specific directory or virtual FAT image root in read-only mode using the same resolution pipeline.
    /// </summary>
    public PolledDeviceOutcome InspectFixtureVolume(
        string fixtureRootPath,
        ushort vendorId = 0,
        ushort productId = 0,
        string? usbProductString = null,
        long? overrideTotalBytes = null,
        uint? overrideVolumeSerial = null)
    {
        var volume = _prober.InspectDirectoryAsVolume(
            fixtureRootPath,
            overrideTotalBytes,
            overrideVolumeSerial: overrideVolumeSerial);

        var dvId = _dvIdResolver.ResolveForVolume(volume.RootPath, allowScsiPassThrough: false);
        var probe = new DeviceDetectionProbe(
            VendorId: vendorId,
            ProductId: productId,
            UsbProductString: usbProductString,
            VolumeRootPath: volume.RootPath,
            VolumeSerialNumber: volume.VolumeSerialNumber,
            TotalVolumeBytes: volume.TotalBytes,
            HardwareDvIdSegment: dvId.ModelSegment,
            HardwareDvIdKey: dvId.DeviceKey,
            PresentRootFolders: volume.PresentRootFolders,
            PresentOmgAudioFiles: volume.PresentOmgAudioFiles);

        var resolution = _registry.ResolveDevice(probe);
        if (resolution.IsRecognized && resolution.Identity is not null && dvId.RawDvIdBytes is not null)
        {
            resolution = resolution with
            {
                Identity = resolution.Identity with { RawDvIdBytes = dvId.RawDvIdBytes }
            };
        }

        return new PolledDeviceOutcome(probe, dvId, resolution);
    }

    /// <summary>
    /// Scans live devices, unloads disappeared volume scopes, and hydrates Branch B / Branch C plugins.
    /// </summary>
    public async Task<IReadOnlyList<PolledDeviceOutcome>> ScanAndHydrateAsync(
        IModuleOrchestrator orchestrator,
        IWalkmanEventBus? eventBus = null,
        bool allowScsiPassThrough = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        var outcomes = ScanConnectedDevices(allowScsiPassThrough);
        var presentSerials = new HashSet<uint>(
            outcomes
                .Where(o => o.Probe.VolumeSerialNumber.HasValue)
                .Select(o => o.Probe.VolumeSerialNumber!.Value));

        var disconnectedSerials = _activeSessionsBySerial.Keys
            .Where(s => !presentSerials.Contains(s))
            .ToArray();

        foreach (var serial in disconnectedSerials)
        {
            var instanceId = _activeSessionsBySerial[serial];
            _activeSessionsBySerial.Remove(serial);
            await orchestrator.UnloadScopeAsync($"device:{instanceId}", ct).ConfigureAwait(false);
            eventBus?.Publish(new DeviceDisconnectedEvent(instanceId, serial, DateTimeOffset.UtcNow));
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.Resolution.IsRecognized && outcome.Resolution.Identity is not null)
            {
                var identity = outcome.Resolution.Identity;
                if (identity.VolumeSerialNumber.HasValue)
                {
                    _activeSessionsBySerial[identity.VolumeSerialNumber.Value] = identity.DeviceInstanceId;
                }

                await orchestrator.HydrateForDeviceAsync(identity, ct).ConfigureAwait(false);
            }
            else if (outcome.Probe.VendorId == 0x054C ||
                     outcome.Probe.PresentRootFolders.Any(f => string.Equals(f, "OMGAUDIO", StringComparison.OrdinalIgnoreCase)))
            {
                await orchestrator.HydrateForUnrecognizedDeviceAsync(outcome.Probe, ct).ConfigureAwait(false);
            }
        }

        return outcomes;
    }
}
