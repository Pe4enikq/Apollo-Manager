using System.Text.Json;
using System.Text.Json.Serialization;
using Walkman.Contracts.Devices;

namespace Walkman.L1.Orchestrator.Manifests;

/// <summary>
/// Loads declarative Walkman device capability profiles from manifests/devices.json and performs
/// deterministic multi-factor disambiguation across shared Sony 0x054C USB PIDs.
/// </summary>
public sealed class DeviceManifestRegistry
{
    private const ushort SonyVendorId = 0x054C;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<DeviceProfile> _profiles;

    private DeviceManifestRegistry(IReadOnlyList<DeviceProfile> profiles)
    {
        _profiles = profiles;
    }

    public IReadOnlyList<DeviceProfile> Profiles => _profiles;

    /// <summary>
    /// Loads and validates device capability profiles from the given devices.json file path.
    /// </summary>
    public static DeviceManifestRegistry LoadFromFile(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Device manifest file not found at '{manifestPath}'.", manifestPath);
        }

        using var stream = File.OpenRead(manifestPath);
        return LoadFromStream(stream);
    }

    /// <summary>
    /// Parses device capability profiles from a JSON stream.
    /// </summary>
    public static DeviceManifestRegistry LoadFromStream(Stream jsonStream)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);

        var document = JsonSerializer.Deserialize<DeviceManifestDocument>(jsonStream, JsonOptions)
            ?? throw new InvalidDataException("Device manifest JSON deserialized to null.");

        if (document.Devices is null || document.Devices.Count == 0)
        {
            throw new InvalidDataException("Device manifest must declare at least one DeviceProfile entry.");
        }

        return new DeviceManifestRegistry(document.Devices);
    }

    /// <summary>
    /// Resolves raw USB, SCSI 0xA4 DvID, capacity, and OMGAUDIO marker telemetry into a concrete DeviceProfile.
    /// </summary>
    public DeviceResolutionResult ResolveDevice(DeviceDetectionProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.VendorId != 0 && probe.VendorId != SonyVendorId)
        {
            return new DeviceResolutionResult(
                false,
                null,
                $"USB VendorId 0x{probe.VendorId:X4} is not Sony (0x054C).");
        }

        if (ContainsFolder(probe.PresentRootFolders, "HMDHIFI"))
        {
            return new DeviceResolutionResult(
                false,
                null,
                "Detected Hi-MD marker 'HMDHIFI', which is excluded from MSC OMGAUDIO resolution.");
        }

        var scoredCandidates = new List<(DeviceProfile Profile, int Score, CapacityTierRule? Tier)>();

        foreach (var profile in _profiles)
        {
            var evaluation = EvaluateCandidate(profile, probe);
            if (evaluation is not null)
            {
                scoredCandidates.Add(evaluation.Value);
            }
        }

        if (scoredCandidates.Count == 0)
        {
            return new DeviceResolutionResult(
                false,
                null,
                $"No manifest profile satisfied hardware/filesystem markers for PID 0x{probe.ProductId:X4}.");
        }

        var winner = scoredCandidates
            .OrderByDescending(c => c.Score)
            .First();

        var resolvedModelCode = ResolveModelCode(winner.Profile, winner.Tier, probe);
        var instanceId = BuildInstanceId(winner.Profile.SeriesId, probe);

        var identity = new ConnectedDeviceIdentity(
            DeviceInstanceId: instanceId,
            Profile: winner.Profile,
            ResolvedModelCode: resolvedModelCode,
            ColorSuffix: null,
            VolumeRootPath: probe.VolumeRootPath,
            VolumeSerialNumber: probe.VolumeSerialNumber,
            HardwareDvIdKey: probe.HardwareDvIdKey,
            RawDvIdBytes: null);

        return new DeviceResolutionResult(true, identity, null);
    }

    private static (DeviceProfile Profile, int Score, CapacityTierRule? Tier)? EvaluateCandidate(
        DeviceProfile profile,
        DeviceDetectionProbe probe)
    {
        // Reject if any explicitly excluded folder is present on the mounted volume.
        foreach (var excluded in profile.Markers.ExcludedFolders)
        {
            if (ContainsFolder(probe.PresentRootFolders, excluded))
            {
                return null;
            }
        }

        // Reject if required root markers (e.g., OMGAUDIO) are missing.
        foreach (var required in profile.Markers.RequiredFolders)
        {
            if (!ContainsFolder(probe.PresentRootFolders, required))
            {
                return null;
            }
        }

        // Disqualify on explicit hardware DvID[6..7] mismatch.
        bool dvIdMatched = false;
        if (probe.HardwareDvIdSegment.HasValue && profile.ExpectedDvIdSegment.HasValue)
        {
            if (probe.HardwareDvIdSegment.Value != profile.ExpectedDvIdSegment.Value)
            {
                return null;
            }

            dvIdMatched = true;
        }

        bool usbPidMatched = probe.VendorId != 0 && profile.UsbIds.Any(id =>
            id.VendorId == probe.VendorId && id.ProductId == probe.ProductId);

        int matchedTableMarkerCount = profile.Markers.RequiredTableMarkers.Count(marker =>
            ContainsFile(probe.PresentOmgAudioFiles, marker));

        // Prevent OmgV1 profiles from winning when OmgV2-specific Artist Link tables exist on the player.
        // Note: 01TREE22.DAT / 03GINF22.DAT are standard playlist slot 0x22 tables also present on OmgV1 (NW-E403).
        bool hasOmgV2Markers = ContainsFile(probe.PresentOmgAudioFiles, "01TREE05.DAT") ||
                               ContainsFile(probe.PresentOmgAudioFiles, "03GINF05.DAT");

        if (hasOmgV2Markers && profile.DatabaseFormat == DatabaseFormat.OmgV1)
        {
            return null;
        }

        if (profile.Markers.RequiredTableMarkers.Count > 0 &&
            probe.PresentOmgAudioFiles.Count > 0 &&
            matchedTableMarkerCount == 0 &&
            !usbPidMatched)
        {
            return null;
        }

        // When USB VID:PID telemetry is present, require either a known PID or known DvID hardware segment
        // so genuinely unknown Sony hardware routes to the telemetry dump wizard (Branch C).
        // Offline folder/virtual-image probes (VendorId == 0) may match via DvID segment or unique table markers.
        bool hasHardwareAnchor = probe.VendorId != 0 && probe.ProductId != 0
            ? (usbPidMatched || dvIdMatched)
            : (dvIdMatched || matchedTableMarkerCount > 0);

        if (!hasHardwareAnchor)
        {
            return null;
        }

        int score = 0;
        if (dvIdMatched)
        {
            score += 100;
        }

        score += matchedTableMarkerCount * 60;

        if (usbPidMatched)
        {
            score += 30;
        }

        foreach (var optionalFolder in profile.Markers.OptionalFolders)
        {
            if (ContainsFolder(probe.PresentRootFolders, optionalFolder))
            {
                score += 10;
            }
        }

        CapacityTierRule? matchedTier = null;
        if (probe.TotalVolumeBytes.HasValue && profile.CapacityTiers.Count > 0)
        {
            matchedTier = profile.CapacityTiers.FirstOrDefault(tier =>
                probe.TotalVolumeBytes.Value >= tier.MinBytes &&
                probe.TotalVolumeBytes.Value <= tier.MaxBytes);

            score += matchedTier is not null ? 50 : -40;
        }

        if (!string.IsNullOrWhiteSpace(probe.UsbProductString))
        {
            bool productMatchesSeriesOrTier =
                probe.UsbProductString.Contains(profile.SeriesId, StringComparison.OrdinalIgnoreCase) ||
                profile.CapacityTiers.Any(t =>
                    probe.UsbProductString.Contains(t.ModelCode, StringComparison.OrdinalIgnoreCase));

            if (productMatchesSeriesOrTier)
            {
                score += 25;
            }
        }

        return (profile, score, matchedTier);
    }

    private static string ResolveModelCode(
        DeviceProfile profile,
        CapacityTierRule? matchedTier,
        DeviceDetectionProbe probe)
    {
        if (!string.IsNullOrWhiteSpace(probe.UsbProductString))
        {
            var stringMatchedTier = profile.CapacityTiers.FirstOrDefault(t =>
                probe.UsbProductString.Contains(t.ModelCode, StringComparison.OrdinalIgnoreCase));

            if (stringMatchedTier is not null)
            {
                return stringMatchedTier.ModelCode;
            }
        }

        if (matchedTier is not null)
        {
            return matchedTier.ModelCode;
        }

        return profile.CapacityTiers.FirstOrDefault()?.ModelCode ?? profile.SeriesId;
    }

    private static string BuildInstanceId(string seriesId, DeviceDetectionProbe probe)
    {
        var serialPart = probe.VolumeSerialNumber?.ToString("X8") ?? "NOSERIAL";
        var keyPart = probe.HardwareDvIdKey?.ToString("X8") ?? "NOKEY";
        return $"{seriesId}-{serialPart}-{keyPart}";
    }

    private static bool ContainsFolder(IReadOnlyList<string> folders, string target) =>
        folders.Any(f => string.Equals(f.TrimEnd('/', '\\'), target, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsFile(IReadOnlyList<string> files, string target) =>
        files.Any(f => string.Equals(Path.GetFileName(f), target, StringComparison.OrdinalIgnoreCase));

    private sealed class DeviceManifestDocument
    {
        public string SchemaVersion { get; init; } = "1.0";
        public List<DeviceProfile> Devices { get; init; } = new();
    }
}
