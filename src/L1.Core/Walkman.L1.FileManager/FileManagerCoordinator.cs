using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
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

public sealed record DeviceTableUpdateRequest(
    IReadOnlyList<TrackGroupDto> UpdatedGroups,
    IReadOnlyList<string>? OrderedGroupNames = null,
    IReadOnlyDictionary<string, string>? RenamedGroups = null,
    IReadOnlyList<TrackMetadataPatch>? TagPatches = null);

/// <summary>
/// L1 VFS and bounded parallel transfer queue coordinator.
/// Stages audio conversions concurrently into a temp buffer, flushes .OMA files atomically,
/// and commits OMGAUDIO .DAT index tables in one atomic transaction at batch end.
/// </summary>
public sealed class FileManagerCoordinator
{
    private sealed record SyncJobItem(
        int TrackId,
        SyncTrackRequest Request,
        Ea3TagMetadata Tags,
        OmaEncryptionMode EncryptionMode,
        string StagedOmaPath,
        SignedMagicGateSlotTemplate? RecycledSlot,
        byte[]? EkbBytes);

    private readonly IModuleOrchestrator _orchestrator;
    private readonly IWalkmanEventBus? _eventBus;
    private readonly MagicGateSlotPoolStore _slotPoolStore;

    public FileManagerCoordinator(
        IModuleOrchestrator orchestrator,
        IWalkmanEventBus? eventBus = null,
        string? slotPoolDirectory = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _eventBus = eventBus;
        _slotPoolStore = new MagicGateSlotPoolStore(slotPoolDirectory);
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

    /// <summary>
    /// Inspects pre-signed OpenMG 0x0001 slots on the device (harvesting active or orphaned signed .OMA headers
    /// into the persistent local SlotPoolStore) and queries the optional Windows Hardware ICV bridge.
    /// </summary>
    public async Task<MagicGateSlotPoolStatus> GetMagicGateSlotPoolStatusAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default,
        OmgDatabaseSnapshot? preloadedSnapshot = null,
        bool harvestActiveSlots = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        var crypto = _orchestrator.TryResolveEngine<IOpenMgCryptoService>();
        var icvBridge = _orchestrator.TryResolveEngine<IOpenMgIcvBridge>();

        var snapshot = preloadedSnapshot ?? await db.ReadDatabaseFastAsync(device, ct).ConfigureAwait(false);
        bool hasMaclist = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);

        IcvBridgeCapability? bridgeCap = icvBridge?.ProbeCapability();
        HardwareIcvSnapshot? hwIcv = icvBridge is not null
            ? await icvBridge.ReadHardwareIcvAsync(device, ct).ConfigureAwait(false)
            : null;
        bool isHardwareIcvInitialized = hwIcv is null ||
            (hwIcv.BoundEkbRevision != 0 &&
             hwIcv.Maclist0Umac8 is { Length: 8 } &&
             hwIcv.Maclist0Umac8.AsSpan().IndexOfAnyExcept((byte)0) >= 0);

        var activeIds = snapshot.Tracks.Select(t => t.TrackId).ToHashSet();
        var active0001Ids = snapshot.Tracks
            .Where(t => t.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
            .Select(t => t.TrackId)
            .ToHashSet();
        var activeSignedIds = new List<int>();
        var freeRecycledIds = new List<int>();
        var brokenUnsignedIds = new List<int>();
        int totalSigned = 0;
        string? healthWarning = null;

        if (hasMaclist)
        {
            byte[] maclist0 = await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
            byte[]? ekbBytes = await transport.ExistsAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                ? await transport.ReadFileAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                : null;

            var signedIds = EnumerateSignedSlotIds(maclist0);
            totalSigned = signedIds.Count;

            if (!isHardwareIcvInitialized)
            {
                brokenUnsignedIds.AddRange(signedIds.Union(active0001Ids).OrderBy(id => id));
                if (brokenUnsignedIds.Count > 0 || active0001Ids.Count > 0 || totalSigned > 0)
                {
                    healthWarning = "Hardware ICV is uninitialized (0 valid slots). Existing 0x0001/AT3+ tracks are unsigned and will trigger MG ERROR on Walkman hardware.";
                }
            }
            else
            {
                var verifiedTemplates = _slotPoolStore.LoadTemplates(maclist0);
                IReadOnlyList<int> idsToHarvest = harvestActiveSlots
                    ? signedIds
                    : signedIds.Where(id => !verifiedTemplates.ContainsKey(id)).ToArray();

                if (crypto is not null && idsToHarvest.Count > 0)
                {
                    var harvested = await HarvestSignedSlotsAsync(device, transport, crypto, maclist0, ekbBytes, idsToHarvest, ct).ConfigureAwait(false);
                    foreach (var kv in harvested)
                    {
                        verifiedTemplates[kv.Key] = kv.Value;
                    }
                }

                var signedSet = signedIds.ToHashSet();
                foreach (int tid in signedIds)
                {
                    if (verifiedTemplates.ContainsKey(tid))
                    {
                        if (activeIds.Contains(tid))
                        {
                            activeSignedIds.Add(tid);
                        }
                        else
                        {
                            freeRecycledIds.Add(tid);
                        }
                    }
                    else
                    {
                        brokenUnsignedIds.Add(tid);
                    }
                }

                foreach (int tid in active0001Ids.Where(id => !signedSet.Contains(id)).OrderBy(id => id))
                {
                    brokenUnsignedIds.Add(tid);
                }

                if (brokenUnsignedIds.Count > 0)
                {
                    healthWarning = $"{brokenUnsignedIds.Count} 0x0001 slot(s) failed cryptographic MACLIST0/OMG_ULINF verification.";
                }
            }
        }
        else if (active0001Ids.Count > 0)
        {
            brokenUnsignedIds.AddRange(active0001Ids.OrderBy(id => id));
            healthWarning = "OMGAUDIO/MACLIST0.DAT is missing; existing 0x0001 tracks are unsigned.";
        }

        return new MagicGateSlotPoolStatus(
            HasMaclist0: hasMaclist,
            TotalSignedSlotsInMaclist0: totalSigned,
            ActiveSignedTrackCount: activeSignedIds.Count,
            RecycledFreeSlotCount: freeRecycledIds.Count,
            FreeRecycledTrackIds: freeRecycledIds,
            ActiveSignedTrackIds: activeSignedIds,
            HardwareIcv: hwIcv,
            WindowsIcvBridge: bridgeCap,
            BrokenUnsignedSlotCount: brokenUnsignedIds.Count,
            BrokenUnsignedTrackIds: brokenUnsignedIds,
            IsHardwareIcvInitialized: isHardwareIcvInitialized,
            HealthWarning: healthWarning);
    }

    /// <summary>
    /// Deletes tracks safely: if OMGAUDIO/MACLIST0.DAT exists on the device, harvests all 0x0001 slot
    /// templates first into the local SlotPoolStore and preserves MACLIST0.DAT intact so Hardware ICV stays valid.
    /// </summary>
    public async Task DeleteTracksAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<int> trackIds,
        CancellationToken ct = default,
        IProgress<TransferProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(trackIds);

        int[] distinctIds = trackIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (distinctIds.Length == 0)
        {
            return;
        }

        string jobId = $"del-{Guid.NewGuid().ToString("N")[..8]}";
        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        var crypto = _orchestrator.TryResolveEngine<IOpenMgCryptoService>();

        progress?.Report(new TransferProgressEvent(jobId, 0, distinctIds.Length, "Checking MagicGate 0x0001 slots...", 5.0));

        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
        var signedSet = new HashSet<int>();
        var verifiedTemplates = new Dictionary<int, SignedMagicGateSlotTemplate>();

        if (maclistExists)
        {
            byte[] maclist0 = await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
            byte[]? ekbBytes = await transport.ExistsAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                ? await transport.ReadFileAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                : null;

            var signedIds = EnumerateSignedSlotIds(maclist0);
            signedSet = signedIds.ToHashSet();
            verifiedTemplates = crypto is not null
                ? await HarvestSignedSlotsAsync(device, transport, crypto, maclist0, ekbBytes, signedIds, ct).ConfigureAwait(false)
                : _slotPoolStore.LoadTemplates(maclist0);
            foreach (int tid in distinctIds)
            {
                if (signedSet.Contains(tid) && (!verifiedTemplates.ContainsKey(tid) || crypto is null))
                {
                    throw new InvalidOperationException(
                        $"Refusing to delete signed 0x0001 track #{tid}: slot template could not be harvested/verified, which would permanently strand the signed slot.");
                }
            }

            progress?.Report(new TransferProgressEvent(jobId, 0, distinctIds.Length, "Harvested signed 0x0001 slots", 15.0));
        }

        progress?.Report(new TransferProgressEvent(jobId, 0, distinctIds.Length, "Committing OMGAUDIO tables...", 25.0));
        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(
                Array.Empty<AudioTrackDto>(),
                distinctIds,
                Array.Empty<TrackGroupDto>(),
                PreserveExistingMaclist0: maclistExists),
            ct).ConfigureAwait(false);

        await RemoveOrStubDeletedOmaFilesAsync(device, transport, crypto, distinctIds, signedSet, verifiedTemplates, ct, jobId, progress).ConfigureAwait(false);
        progress?.Report(new TransferProgressEvent(jobId, distinctIds.Length, distinctIds.Length, "Delete complete", 100.0));
    }

    private static async Task RemoveOrStubDeletedOmaFilesAsync(
        ConnectedDeviceIdentity device,
        ITransportDriver transport,
        IOpenMgCryptoService? crypto,
        IReadOnlyList<int> trackIds,
        HashSet<int> signedSet,
        Dictionary<int, SignedMagicGateSlotTemplate> verifiedTemplates,
        CancellationToken ct,
        string? jobId = null,
        IProgress<TransferProgressEvent>? progress = null)
    {
        for (int i = 0; i < trackIds.Count; i++)
        {
            int tid = trackIds[i];
            string relPath = $"OMGAUDIO/10F{tid / 256:X2}/1{tid:X7}.OMA";
            bool isStub = signedSet.Contains(tid) && verifiedTemplates.TryGetValue(tid, out var tpl) && crypto is not null;
            if (isStub)
            {
                byte[] stubBytes = crypto!.BuildSignedSlotDummyStub(verifiedTemplates[tid]);
                await transport.WriteFileAtomicAsync(device, relPath, stubBytes, ct).ConfigureAwait(false);
            }
            else
            {
                await transport.DeleteFileAsync(device, relPath, ct).ConfigureAwait(false);
            }

            string lrcRelPath = $"OMGAUDIO/10F{tid / 256:X2}/1{tid:X7}.LRC";
            if (await transport.ExistsAsync(device, lrcRelPath, ct).ConfigureAwait(false))
            {
                await transport.DeleteFileAsync(device, lrcRelPath, ct).ConfigureAwait(false);
            }

            if (progress is not null && jobId is not null)
            {
                double pct = Math.Round(35.0 + ((i + 1) * 60.0 / Math.Max(1, trackIds.Count)), 1);
                string label = isStub ? $"Preserving 0x0001 slot #{tid}" : $"Deleting 1{tid:X7}.OMA";
                progress.Report(new TransferProgressEvent(jobId, i + 1, trackIds.Count, label, pct));
            }
        }

        if (progress is not null && jobId is not null)
        {
            progress.Report(new TransferProgressEvent(jobId, trackIds.Count, trackIds.Count, "Flushing USB buffers...", 98.0));
        }
    }

    /// <summary>
    /// Transcodes/wraps a batch of source files across a bounded Channel worker pool into staging,
    /// flushes .OMA files via ITransportDriver, and commits all OMGAUDIO .DAT updates in one atomic batch.
    /// </summary>
    public async Task<SyncBatchResult> SyncTracksToDeviceAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<SyncTrackRequest> requests,
        string? stagingDirectory = null,
        CancellationToken ct = default,
        bool allowUnsignedMaclist0ForTesting = false,
        IReadOnlyList<string>? orderedGroupNames = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(requests);
        if (device.BpbDiagnostics?.VerificationState == BpbVerificationState.InvalidWindowsFormat)
        {
            throw new InvalidOperationException(
                device.BpbDiagnostics.WarningMessage
                ?? "Sync blocked: device volume has an invalid Windows FAT16 BPB. Run on-device Format first.");
        }

        if (requests.Count == 0)
        {
            return new SyncBatchResult(0, 0, Array.Empty<AudioTrackDto>(), Array.Empty<string>());
        }

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        var codec = _orchestrator.ResolveRequiredEngine<ICodecService>();
        var crypto = _orchestrator.TryResolveEngine<IOpenMgCryptoService>();
        var icvBridge = _orchestrator.TryResolveEngine<IOpenMgIcvBridge>();

        var snapshot = await db.ReadDatabaseAsync(device, ct).ConfigureAwait(false);
        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);

        byte[]? maclist0 = null;
        byte[]? ekbBytes = await transport.ExistsAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
            ? await transport.ReadFileAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
            : null;
        List<int> signedSlotIds = new();
        Dictionary<int, SignedMagicGateSlotTemplate> verifiedTemplates = new();

        if (maclistExists)
        {
            maclist0 = await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
            signedSlotIds = EnumerateSignedSlotIds(maclist0);
            if (crypto is not null)
            {
                verifiedTemplates = await HarvestSignedSlotsAsync(device, transport, crypto, maclist0, ekbBytes, signedSlotIds, ct).ConfigureAwait(false);
            }
        }

        HardwareIcvSnapshot? hwIcvForSync = icvBridge is not null &&
            requests.Any(r => ResolveRequiredEncryption(device, r.TargetCodec) == OmaEncryptionMode.MagicGateLsi_0x0001)
            ? await icvBridge.ReadHardwareIcvAsync(device, ct).ConfigureAwait(false)
            : null;
        bool icvZeroedForSync = hwIcvForSync is not null &&
            (hwIcvForSync.BoundEkbRevision == 0 ||
             hwIcvForSync.Maclist0Umac8 is not { Length: 8 } ||
             hwIcvForSync.Maclist0Umac8.AsSpan().IndexOfAnyExcept((byte)0) < 0);

        var existingByTrackId = snapshot.Tracks.ToDictionary(t => t.TrackId);
        var reservedSignedSet = signedSlotIds.ToHashSet();
        var usedTrackIds = new HashSet<int>(existingByTrackId.Keys);
        usedTrackIds.UnionWith(reservedSignedSet);

        // Identify explicit ReplaceTrackId claims first so recycled free pool never double-allocates them.
        var explicitReplaceIds = new HashSet<int>();
        var planningErrors = new ConcurrentBag<string>();
        foreach (var req in requests)
        {
            if (req.ReplaceTrackId.HasValue &&
                ResolveRequiredEncryption(device, req.TargetCodec) == OmaEncryptionMode.MagicGateLsi_0x0001)
            {
                explicitReplaceIds.Add(req.ReplaceTrackId.Value);
            }
        }

        var freeSignedQueue = new Queue<SignedMagicGateSlotTemplate>(
            signedSlotIds
                .Where(tid => !existingByTrackId.ContainsKey(tid) && !explicitReplaceIds.Contains(tid))
                .Where(verifiedTemplates.ContainsKey)
                .OrderBy(tid => tid)
                .Select(tid => verifiedTemplates[tid]));

        var claimedReplaceSlots = new HashSet<int>();
        int nextLinearId = (usedTrackIds.Count > 0 ? usedTrackIds.Max() : 0) + 1;

        // Gated for v0.1.1b until COD-36 COM/ICV minting is completed in v0.2.0b;
        // preserve Option 2 slot-recycling (freeSignedQueue + explicit ReplaceTrackId).
        const bool enableScsiAutoMint = false;
        int nonReplace0001Needed = requests.Count(r =>
            !r.ReplaceTrackId.HasValue &&
            ResolveRequiredEncryption(device, r.TargetCodec) == OmaEncryptionMode.MagicGateLsi_0x0001);
        int slotDeficit = Math.Max(0, nonReplace0001Needed - freeSignedQueue.Count);
        string? autoMintError = null;
        if (enableScsiAutoMint && !allowUnsignedMaclist0ForTesting && hwIcvForSync is not null && slotDeficit > 0 && icvBridge is not null)
        {
            var bridgeCap = icvBridge.ProbeCapability();
            if (bridgeCap is { IsSupportedOs: true, IsHostRuntimePresent: true, IsWorkerAvailable: true })
            {
                var mintRequests = new List<IcvMintTrackRequest>(slotDeficit);
                int candidateId = nextLinearId;
                for (int m = 0; m < slotDeficit; m++)
                {
                    while (usedTrackIds.Contains(candidateId))
                    {
                        candidateId++;
                    }

                    mintRequests.Add(new IcvMintTrackRequest(
                        TrackId: candidateId,
                        ContentId20: RandomNumberGenerator.GetBytes(20),
                        PlaintextContentKey8: RandomNumberGenerator.GetBytes(8)));
                    usedTrackIds.Add(candidateId);
                    candidateId++;
                }

                nextLinearId = candidateId;
                var mintResult = await icvBridge.MintAndCommitSlotsAsync(device, mintRequests, Array.Empty<int>(), ct).ConfigureAwait(false);
                if (mintResult.Success && mintResult.MintedSlots is { Count: > 0 })
                {
                    if (mintResult.UpdatedMaclist0 is { Length: >= 32768 })
                    {
                        maclist0 = mintResult.UpdatedMaclist0;
                        _slotPoolStore.UpsertTemplates(maclist0, mintResult.MintedSlots.Values);
                    }

                    maclistExists = true;
                    icvZeroedForSync = false;
                    foreach (var tpl in mintResult.MintedSlots.Values.OrderBy(t => t.TrackId))
                    {
                        verifiedTemplates[tpl.TrackId] = tpl;
                        signedSlotIds.Add(tpl.TrackId);
                        freeSignedQueue.Enqueue(tpl);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(mintResult.ErrorMessage))
                {
                    autoMintError = mintResult.ErrorMessage;
                }
            }
        }

        bool ownStagingDir = string.IsNullOrWhiteSpace(stagingDirectory);
        string effectiveStagingDir = ownStagingDir
            ? Path.Combine(Path.GetTempPath(), $"apollo_sync_{Guid.NewGuid():N}")
            : Path.GetFullPath(stagingDirectory!);
        Directory.CreateDirectory(effectiveStagingDir);

        string jobId = Guid.NewGuid().ToString("N")[..8];
        var jobs = new List<SyncJobItem>(requests.Count);

        try
        {
            for (int i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                var tags = req.TagOverrides ?? codec.ExtractMetadata(req.SourceFilePath);
                var encMode = ResolveRequiredEncryption(device, req.TargetCodec);

                int allocatedTrackId;
                SignedMagicGateSlotTemplate? recycledSlot = null;

                if (encMode == OmaEncryptionMode.MagicGateLsi_0x0001 && icvZeroedForSync)
                {
                    planningErrors.Add(
                        $"{Path.GetFileName(req.SourceFilePath)}: Hardware ICV is uninitialized (0 valid 0x0001 slots). Initialize at least one AT3+ track with SonicStage or use MP3 (0xFFFE).");
                    continue;
                }

                if (encMode == OmaEncryptionMode.MagicGateLsi_0x0001 && !maclistExists && !allowUnsignedMaclist0ForTesting)
                {
                    planningErrors.Add(
                        $"{Path.GetFileName(req.SourceFilePath)}: Player has no pre-signed OMGAUDIO/MACLIST0.DAT (freshly formatted). Creating an unsigned MACLIST0.DAT causes 'MG ERROR' on Walkman hardware — use Auto (MP3 0xFFFE) or initialize one 0x0001 track with SonicStage first.");
                    continue;
                }

                if (maclistExists && encMode == OmaEncryptionMode.MagicGateLsi_0x0001)
                {
                    if (req.ReplaceTrackId.HasValue)
                    {
                        int requestedId = req.ReplaceTrackId.Value;
                        if (!verifiedTemplates.TryGetValue(requestedId, out recycledSlot) || !claimedReplaceSlots.Add(requestedId))
                        {
                            planningErrors.Add(
                                $"{Path.GetFileName(req.SourceFilePath)}: Signed MagicGate slot #{requestedId} is unavailable or already claimed in this batch.");
                            continue;
                        }

                        allocatedTrackId = recycledSlot.TrackId;
                        if (string.IsNullOrWhiteSpace(req.GroupName) &&
                            existingByTrackId.TryGetValue(allocatedTrackId, out var existingTrack) &&
                            !string.IsNullOrWhiteSpace(existingTrack.GroupName))
                        {
                            req = req with { GroupName = existingTrack.GroupName };
                        }
                    }
                    else if (freeSignedQueue.TryDequeue(out recycledSlot))
                    {
                        allocatedTrackId = recycledSlot.TrackId;
                    }
                    else
                    {
                        string hint = !string.IsNullOrWhiteSpace(autoMintError)
                            ? $"Automatic 0x0001 slot minting failed: {autoMintError}"
                            : "Delete an existing 0x0001 track to recycle its pre-signed slot, specify ReplaceTrackId, or use MP3 (0xFFFE). (Direct SCSI slot minting is coming in v0.2.0b.)";

                        planningErrors.Add(
                            $"{Path.GetFileName(req.SourceFilePath)}: No free pre-signed OpenMG 0x0001 slot available ({signedSlotIds.Count} signed slot(s), 0 free) — refusing to mutate hardware-protected MACLIST0.DAT. {hint}");
                        continue;
                    }
                }
                else
                {
                    while (usedTrackIds.Contains(nextLinearId))
                    {
                        nextLinearId++;
                    }

                    allocatedTrackId = nextLinearId++;
                    usedTrackIds.Add(allocatedTrackId);
                }

                string stagedPath = Path.Combine(effectiveStagingDir, $"1{allocatedTrackId:X7}.OMA");
                jobs.Add(new SyncJobItem(allocatedTrackId, req, tags, encMode, stagedPath, recycledSlot, ekbBytes));
            }

            if (jobs.Count == 0)
            {
                return new SyncBatchResult(0, planningErrors.Count, Array.Empty<AudioTrackDto>(), planningErrors.ToArray());
            }

            int workerCount = Math.Clamp(Environment.ProcessorCount, 1, Math.Min(4, jobs.Count));
            var channel = Channel.CreateBounded<SyncJobItem>(new BoundedChannelOptions(Math.Max(2, workerCount * 2))
            {
                SingleWriter = true,
                SingleReader = false
            });

            var succeededJobs = new ConcurrentBag<(SyncJobItem Job, TranscodeResult Result)>();
            var errors = new ConcurrentBag<string>(planningErrors);
            int completedCounter = 0;

            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (var job in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var trReq = new TranscodeRequest(
                        SourceFilePath: job.Request.SourceFilePath,
                        StagingOutputPath: job.StagedOmaPath,
                        TargetCodec: job.Request.TargetCodec,
                        TargetBitrateKbps: job.Request.TargetBitrateKbps,
                        EncryptionMode: job.EncryptionMode,
                        TrackId: job.TrackId,
                        HardwareDeviceKey: device.HardwareDvIdKey,
                        Tags: job.Tags,
                        Ea3BlockSize: job.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 ? 2048 : 1024,
                        RecycledMagicGateSlot: job.RecycledSlot,
                        DeviceEkb00010021Bytes: job.EkbBytes);

                    var result = await codec.ProcessToOmaAsync(trReq, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        succeededJobs.Add((job, result));
                    }
                    else
                    {
                        errors.Add($"{Path.GetFileName(job.Request.SourceFilePath)}: {result.ErrorMessage ?? "Transcode failed"}");
                    }

                    int done = Interlocked.Increment(ref completedCounter);
                    _eventBus?.Publish(new TransferProgressEvent(
                        jobId,
                        done,
                        jobs.Count,
                        job.Tags.Title,
                        Math.Round(done * 55.0 / jobs.Count, 1)));
                }
            }, ct)).ToArray();

            foreach (var job in jobs)
            {
                await channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
            channel.Writer.Complete();

            await Task.WhenAll(workers).ConfigureAwait(false);

            var orderedSucceeded = succeededJobs.OrderBy(x => x.Job.TrackId).ToArray();
            if (orderedSucceeded.Length == 0)
            {
                return new SyncBatchResult(0, errors.Count, Array.Empty<AudioTrackDto>(), errors.ToArray());
            }

            // Stage -> Device flush first; single atomic .DAT commit at the very end.
            var addedTracks = new List<AudioTrackDto>(orderedSucceeded.Length);
            for (int i = 0; i < orderedSucceeded.Length; i++)
            {
                var (job, res) = orderedSucceeded[i];
                ct.ThrowIfCancellationRequested();
                byte[] omaBytes = await File.ReadAllBytesAsync(res.OutputOmaPath, ct).ConfigureAwait(false);
                string relDevicePath = $"OMGAUDIO/10F{job.TrackId / 256:X2}/1{job.TrackId:X7}.OMA";
                await transport.WriteFileAtomicAsync(device, relDevicePath, omaBytes, ct).ConfigureAwait(false);

                _eventBus?.Publish(new TransferProgressEvent(
                    jobId,
                    i + 1,
                    orderedSucceeded.Length,
                    job.Tags.Title,
                    Math.Round(55.0 + ((i + 1) * 35.0 / orderedSucceeded.Length), 1)));

                addedTracks.Add(new AudioTrackDto(
                    TrackId: job.TrackId,
                    Title: job.Tags.Title.Trim(),
                    Artist: job.Tags.Artist.Trim(),
                    Album: job.Tags.Album.Trim(),
                    Genre: job.Tags.Genre.Trim(),
                    SortTitle: null,
                    TrackNumber: job.Tags.TrackNumber ?? 0,
                    Year: job.Tags.Year ?? 0,
                    DurationMs: res.DurationMs,
                    FrameCount: res.FrameCount,
                    Codec: job.Request.TargetCodec,
                    BitrateKbps: job.Request.TargetBitrateKbps,
                    EncryptionMode: job.EncryptionMode,
                    FilePropertiesBigEndian: ((uint)job.Request.TargetCodec << 24) | res.CodecParams24,
                    LsiKeyring48: res.LsiKeyring48,
                    FileSizeBytes: omaBytes.Length,
                    GroupName: string.IsNullOrWhiteSpace(job.Request.GroupName) ? null : job.Request.GroupName.Trim(),
                    MaclistEntry8: res.MaclistEntry8,
                    TargetPositionIndex: job.Request.TargetPositionIndex));
            }

            _eventBus?.Publish(new TransferProgressEvent(
                jobId,
                orderedSucceeded.Length,
                orderedSucceeded.Length,
                "OMGAUDIO Tables",
                95.0));

            await db.CommitTransactionAsync(
                device,
                new OmgDatabaseTransaction(
                    addedTracks,
                    Array.Empty<int>(),
                    Array.Empty<TrackGroupDto>(),
                    PreserveExistingMaclist0: maclistExists,
                    OrderedGroupNames: orderedGroupNames),
                ct).ConfigureAwait(false);

            _eventBus?.Publish(new TransferProgressEvent(
                jobId,
                orderedSucceeded.Length,
                orderedSucceeded.Length,
                "OMGAUDIO Tables",
                100.0));

            return new SyncBatchResult(addedTracks.Count, errors.Count, addedTracks, errors.ToArray());
        }
        finally
        {
            if (ownStagingDir && Directory.Exists(effectiveStagingDir))
            {
                try
                {
                    Directory.Delete(effectiveStagingDir, recursive: true);
                }
                catch
                {
                    // Best-effort staging cleanup.
                }
            }
        }
    }

    /// <summary>
    /// Manually pre-allocates N fresh hardware-signed OpenMG 0x0001 slots via the optional Windows 32-bit COM ICV bridge,
    /// saves the harvested templates into the local SlotPoolStore, and writes 2,144-byte dummy stubs to OMGAUDIO/10FXX/.
    /// </summary>
    public async Task<IcvCommitResult> AllocateMagicGateSlotsAsync(
        ConnectedDeviceIdentity device,
        int slotCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (slotCount < 1 || slotCount > 512)
        {
            return new IcvCommitResult(false, null, null, null, "Slot allocation count must be between 1 and 512 (avoid over-allocating on large drives).");
        }

        var icvBridge = _orchestrator.TryResolveEngine<IOpenMgIcvBridge>();
        if (icvBridge is null)
        {
            return new IcvCommitResult(false, null, null, null, "OpenMG ICV bridge service is unavailable.");
        }

        var cap = icvBridge.ProbeCapability();
        if (!cap.IsSupportedOs || !cap.IsHostRuntimePresent || !cap.IsWorkerAvailable)
        {
            return new IcvCommitResult(false, null, null, null, cap.StatusMessage);
        }

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        var crypto = _orchestrator.TryResolveEngine<IOpenMgCryptoService>();

        var snapshot = await db.ReadDatabaseFastAsync(device, ct).ConfigureAwait(false);
        var usedIds = snapshot.Tracks.Select(t => t.TrackId).ToHashSet();

        if (await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false))
        {
            byte[] existingMaclist = await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
            foreach (int id in EnumerateSignedSlotIds(existingMaclist))
            {
                usedIds.Add(id);
            }
        }

        int nextId = (usedIds.Count > 0 ? usedIds.Max() : 0) + 1;
        var mintRequests = new List<IcvMintTrackRequest>(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            while (usedIds.Contains(nextId))
            {
                nextId++;
            }

            mintRequests.Add(new IcvMintTrackRequest(
                TrackId: nextId,
                ContentId20: RandomNumberGenerator.GetBytes(20),
                PlaintextContentKey8: RandomNumberGenerator.GetBytes(8)));
            usedIds.Add(nextId);
            nextId++;
        }

        var result = await icvBridge.MintAndCommitSlotsAsync(device, mintRequests, Array.Empty<int>(), ct).ConfigureAwait(false);
        if (!result.Success || result.MintedSlots is not { Count: > 0 })
        {
            return result;
        }

        if (result.UpdatedMaclist0 is { Length: >= 32768 })
        {
            _slotPoolStore.UpsertTemplates(result.UpdatedMaclist0, result.MintedSlots.Values);
        }

        if (crypto is not null)
        {
            foreach (var tpl in result.MintedSlots.Values)
            {
                string relPath = $"OMGAUDIO/10F{tpl.TrackId / 256:X2}/1{tpl.TrackId:X7}.OMA";
                byte[] stub = crypto.BuildSignedSlotDummyStub(tpl);
                await transport.WriteFileAtomicAsync(device, relPath, stub, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    public async Task RenameCollectionAsync(
        ConnectedDeviceIdentity device,
        string oldName,
        string newName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) ||
            string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);

        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(
                Array.Empty<AudioTrackDto>(),
                Array.Empty<int>(),
                Array.Empty<TrackGroupDto>(),
                PreserveExistingMaclist0: maclistExists,
                RenamedGroups: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [oldName.Trim()] = newName.Trim()
                }),
            ct).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(
        ConnectedDeviceIdentity device,
        string collectionName,
        bool deleteTracks = false,
        CancellationToken ct = default,
        IProgress<TransferProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return;
        }

        string jobId = $"delcol-{Guid.NewGuid().ToString("N")[..8]}";
        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        var crypto = _orchestrator.TryResolveEngine<IOpenMgCryptoService>();
        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);

        int[] deletedTrackIds = Array.Empty<int>();
        var signedSet = new HashSet<int>();
        var verifiedTemplates = new Dictionary<int, SignedMagicGateSlotTemplate>();

        if (deleteTracks)
        {
            var snapshot = await db.ReadDatabaseFastAsync(device, ct).ConfigureAwait(false);
            var matchedIds = new HashSet<int>();
            foreach (var grp in snapshot.Groups)
            {
                if (string.Equals(grp.Title, collectionName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (int id in grp.TrackIds)
                    {
                        matchedIds.Add(id);
                    }
                }
            }

            foreach (var t in snapshot.Tracks)
            {
                bool matchesTrack = string.Equals(t.GroupName, collectionName, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(collectionName, "Unassigned", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(t.GroupName));
                if (matchesTrack)
                {
                    matchedIds.Add(t.TrackId);
                }
            }

            deletedTrackIds = matchedIds.Where(id => id > 0).OrderBy(id => id).ToArray();
            progress?.Report(new TransferProgressEvent(jobId, 0, Math.Max(1, deletedTrackIds.Length), $"Checking MagicGate slots for '{collectionName}'...", 5.0));

            if (maclistExists && deletedTrackIds.Length > 0)
            {
                byte[] maclist0 = await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
                byte[]? ekbBytes = await transport.ExistsAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                    ? await transport.ReadFileAsync(device, "OMGAUDIO/00010021.DAT", ct).ConfigureAwait(false)
                    : null;
                var signedIds = EnumerateSignedSlotIds(maclist0);
                signedSet = signedIds.ToHashSet();
                verifiedTemplates = crypto is not null
                    ? await HarvestSignedSlotsAsync(device, transport, crypto, maclist0, ekbBytes, signedIds, ct).ConfigureAwait(false)
                    : _slotPoolStore.LoadTemplates(maclist0);
                progress?.Report(new TransferProgressEvent(jobId, 0, deletedTrackIds.Length, "Harvested signed 0x0001 slots", 15.0));
            }
        }

        progress?.Report(new TransferProgressEvent(jobId, 0, Math.Max(1, deletedTrackIds.Length), "Committing OMGAUDIO tables...", 25.0));
        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(
                Array.Empty<AudioTrackDto>(),
                deletedTrackIds,
                Array.Empty<TrackGroupDto>(),
                PreserveExistingMaclist0: maclistExists,
                DeletedGroupNames: [collectionName.Trim()]),
            ct).ConfigureAwait(false);

        if (deletedTrackIds.Length > 0)
        {
            await RemoveOrStubDeletedOmaFilesAsync(device, transport, crypto, deletedTrackIds, signedSet, verifiedTemplates, ct, jobId, progress).ConfigureAwait(false);
        }

        progress?.Report(new TransferProgressEvent(jobId, Math.Max(1, deletedTrackIds.Length), Math.Max(1, deletedTrackIds.Length), "Delete complete", 100.0));
    }

    public async Task UpdateDeviceTableAsync(
        ConnectedDeviceIdentity device,
        DeviceTableUpdateRequest update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(update);

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();

        if (update.TagPatches is { Count: > 0 })
        {
            await db.UpdateTrackMetadataAsync(device, update.TagPatches, ct).ConfigureAwait(false);
        }

        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(
                Array.Empty<AudioTrackDto>(),
                Array.Empty<int>(),
                update.UpdatedGroups,
                PreserveExistingMaclist0: maclistExists,
                RenamedGroups: update.RenamedGroups,
                OrderedGroupNames: update.OrderedGroupNames),
            ct).ConfigureAwait(false);
    }

    public async Task ReorderCollectionTracksAsync(
        ConnectedDeviceIdentity device,
        string collectionName,
        IReadOnlyList<int> orderedTrackIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(orderedTrackIds);
        if (string.IsNullOrWhiteSpace(collectionName) || orderedTrackIds.Count == 0)
        {
            return;
        }

        var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
        var snapshot = await db.ReadDatabaseFastAsync(device, ct).ConfigureAwait(false);

        var existingGroup = snapshot.Groups.FirstOrDefault(g =>
            string.Equals(g.Title, collectionName, StringComparison.OrdinalIgnoreCase));
        if (existingGroup is null)
        {
            return;
        }

        var updatedGroup = existingGroup with { TrackIds = orderedTrackIds.Distinct().ToArray() };
        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(
                Array.Empty<AudioTrackDto>(),
                Array.Empty<int>(),
                [updatedGroup],
                PreserveExistingMaclist0: maclistExists),
            ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, SignedMagicGateSlotTemplate>> HarvestSignedSlotsAsync(
        ConnectedDeviceIdentity device,
        ITransportDriver transport,
        IOpenMgCryptoService crypto,
        byte[] maclist0,
        byte[]? ekbBytes,
        IReadOnlyList<int> signedSlotIds,
        CancellationToken ct)
    {
        var templates = _slotPoolStore.LoadTemplates(maclist0);
        if (signedSlotIds.Count == 0)
        {
            return templates;
        }

        var newlyHarvested = new List<SignedMagicGateSlotTemplate>();
        byte[] effectiveEkb = ekbBytes ?? Array.Empty<byte>();

        foreach (int trackId in signedSlotIds)
        {
            if (templates.ContainsKey(trackId))
            {
                continue;
            }

            string relPath = $"OMGAUDIO/10F{trackId / 256:X2}/1{trackId:X7}.OMA";
            if (!await transport.ExistsAsync(device, relPath, ct).ConfigureAwait(false))
            {
                continue;
            }

            byte[] headerPrefix = await transport.ReadFilePrefixAsync(device, relPath, 4096, ct).ConfigureAwait(false);
            var tpl = crypto.ExtractSignedSlotTemplate(headerPrefix, trackId, effectiveEkb);
            if (tpl is null && headerPrefix.Length == 4096)
            {
                byte[] largerPrefix = await transport.ReadFilePrefixAsync(device, relPath, 16384, ct).ConfigureAwait(false);
                tpl = crypto.ExtractSignedSlotTemplate(largerPrefix, trackId, effectiveEkb);
                if (tpl is null && largerPrefix.Length == 16384)
                {
                    byte[] fullOma = await transport.ReadFileAsync(device, relPath, ct).ConfigureAwait(false);
                    tpl = crypto.ExtractSignedSlotTemplate(fullOma, trackId, effectiveEkb);
                }
            }

            if (tpl is null)
            {
                continue;
            }

            int offset = (trackId - 1) * 8;
            if (offset + 8 <= maclist0.Length &&
                tpl.MaclistEntry8.AsSpan().SequenceEqual(maclist0.AsSpan(offset, 8)))
            {
                templates[trackId] = tpl;
                newlyHarvested.Add(tpl);
            }
        }

        if (newlyHarvested.Count > 0)
        {
            _slotPoolStore.UpsertTemplates(maclist0, newlyHarvested);
        }

        return templates;
    }

    private static List<int> EnumerateSignedSlotIds(ReadOnlySpan<byte> maclist0)
    {
        int maxSlots = Math.Min(4096, maclist0.Length / 8);
        var ids = new List<int>();
        for (int trackId = 1; trackId <= maxSlots; trackId++)
        {
            ReadOnlySpan<byte> slot = maclist0.Slice((trackId - 1) * 8, 8);
            if (slot.IndexOfAnyExcept((byte)0) >= 0)
            {
                ids.Add(trackId);
            }
        }

        return ids;
    }

    private static OmaEncryptionMode ResolveRequiredEncryption(ConnectedDeviceIdentity device, AudioCodecId codec)
    {
        var cap = device.Profile.Codecs.FirstOrDefault(c => c.Codec == codec);
        if (cap is not null)
        {
            return cap.RequiredEncryption;
        }

        return device.Profile.RequiresHardwareDvIdKey
            ? OmaEncryptionMode.HardwareXor_0xFFFE
            : OmaEncryptionMode.Unprotected_0xFFFF;
    }
}
