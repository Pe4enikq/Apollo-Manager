using System.Buffers.Binary;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Database.OmgV1;

/// <summary>
/// Clean-room Big-Endian Span&lt;byte&gt; reader, incremental updater, and scratch rebuilder
/// for Sony OMGAUDIO v1 databases (supporting both SonicStage 34-slot and JSymphonic 45-slot layouts).
/// </summary>
public sealed class OmgV1DatabaseEngine : IDatabaseEngine
{
    private const int TreeGplbAllocatedLength = 0x4010;
    private const int TreeTplbStartAddress = 0x4040;
    private const int TreinfAllocatedLength = 0x1F00;
    private const int CntinfElementSize = 0x290;
    private const int CidlElementSize = 0x30;
    private const int Ginf01ElementSize = 0x310;
    private const int GinfSimpleElementSize = 0x90;
    private const int SonicStageSlotCount = 0x22; // 34 slots observed on NW-E403

    private readonly Func<ITransportDriver> _transportResolver;
    private readonly Func<IOpenMgCryptoService?>? _cryptoResolver;

    public OmgV1DatabaseEngine(ITransportDriver transportDriver)
        : this(() => transportDriver ?? throw new ArgumentNullException(nameof(transportDriver)), null)
    {
    }

    public OmgV1DatabaseEngine(Func<ITransportDriver> transportResolver)
        : this(transportResolver, null)
    {
    }

    public OmgV1DatabaseEngine(
        Func<ITransportDriver> transportResolver,
        Func<IOpenMgCryptoService?>? cryptoResolver)
    {
        _transportResolver = transportResolver ?? throw new ArgumentNullException(nameof(transportResolver));
        _cryptoResolver = cryptoResolver;
    }

    public DatabaseFormat Format => DatabaseFormat.OmgV1;

    /// <inheritdoc />
    public Task<OmgDatabaseSnapshot> ReadDatabaseAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default) =>
        ReadDatabaseCoreAsync(device, probeOmaHeaders: true, ct);

    /// <inheritdoc />
    public Task<OmgDatabaseSnapshot> ReadDatabaseFastAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default) =>
        ReadDatabaseCoreAsync(device, probeOmaHeaders: false, ct);

    private async Task<OmgDatabaseSnapshot> ReadDatabaseCoreAsync(
        ConnectedDeviceIdentity device,
        bool probeOmaHeaders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var transport = _transportResolver();

        if (!await transport.ExistsAsync(device, "OMGAUDIO/04CNTINF.DAT", ct).ConfigureAwait(false))
        {
            return new OmgDatabaseSnapshot(Array.Empty<AudioTrackDto>(), Array.Empty<TrackGroupDto>(), 0);
        }

        byte[] cntinfBytes = await transport.ReadFileAsync(device, "OMGAUDIO/04CNTINF.DAT", ct).ConfigureAwait(false);
        if (cntinfBytes.Length < 0x30)
        {
            return new OmgDatabaseSnapshot(Array.Empty<AudioTrackDto>(), Array.Empty<TrackGroupDto>(), 0);
        }
        byte[]? cidlstBytes = await transport.ExistsAsync(device, "OMGAUDIO/05CIDLST.DAT", ct).ConfigureAwait(false)
            ? await transport.ReadFileAsync(device, "OMGAUDIO/05CIDLST.DAT", ct).ConfigureAwait(false)
            : null;
        byte[]? tree01Bytes = await transport.ExistsAsync(device, "OMGAUDIO/01TREE01.DAT", ct).ConfigureAwait(false)
            ? await transport.ReadFileAsync(device, "OMGAUDIO/01TREE01.DAT", ct).ConfigureAwait(false)
            : null;
        byte[]? ginf01Bytes = await transport.ExistsAsync(device, "OMGAUDIO/03GINF01.DAT", ct).ConfigureAwait(false)
            ? await transport.ReadFileAsync(device, "OMGAUDIO/03GINF01.DAT", ct).ConfigureAwait(false)
            : null;
        byte[]? chindcBytes = await transport.ExistsAsync(device, "OMGAUDIO/05CHINDC.DAT", ct).ConfigureAwait(false)
            ? await transport.ReadFileAsync(device, "OMGAUDIO/05CHINDC.DAT", ct).ConfigureAwait(false)
            : null;

        var allSlots = ParseCntinfSlots(cntinfBytes, cidlstBytes);
        var chindcFlags = chindcBytes is not null ? Parse05ChindcFlags(chindcBytes) : new Dictionary<int, ushort>();
        var crypto = _cryptoResolver?.Invoke();

        // Filter strictly via active GPLB/TPLB pointers so soft-deleted SonicStage tombstones are excluded.
        List<TrackGroupDto> activeGroups;
        HashSet<int> activeTrackIds;
        Dictionary<int, string> trackGroupMap = new();

        if (tree01Bytes is not null && ginf01Bytes is not null)
        {
            (activeGroups, activeTrackIds) = ParseActiveTree01Groups(tree01Bytes, ginf01Bytes);
            foreach (var grp in activeGroups)
            {
                foreach (var tid in grp.TrackIds)
                {
                    trackGroupMap[tid] = grp.Title;
                }
            }
        }
        else
        {
            activeTrackIds = allSlots
                .Where(s => s.IsPopulated)
                .Select(s => s.Track.TrackId)
                .ToHashSet();
            activeGroups = new List<TrackGroupDto>();
        }

        var activeTracks = new List<AudioTrackDto>(activeTrackIds.Count);
        var dummyStubTrackIds = new HashSet<int>();
        foreach (var slot in allSlots)
        {
            if (!slot.IsPopulated || !activeTrackIds.Contains(slot.Track.TrackId))
            {
                continue;
            }

            var track = slot.Track;
            int trackNumber = track.TrackNumber;
            int year = track.Year;
            uint frameCount = track.FrameCount;
            bool isCopyProtected = false;
            chindcFlags.TryGetValue(track.TrackId, out ushort chindcFlag);
            long fileSize;

            if (!probeOmaHeaders)
            {
                // Fast connect path: zero per-track USB file I/O (use 04CNTINF + 05CHINDC metadata only).
                if (track.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 && track.DurationMs == 0)
                {
                    dummyStubTrackIds.Add(track.TrackId);
                    continue;
                }

                fileSize = track.DurationMs > 0 && track.BitrateKbps > 0
                    ? (long)(track.DurationMs / 1000.0 * track.BitrateKbps * 125.0)
                    : 0L;

                if (track.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
                {
                    isCopyProtected = slot.IsDamedaFlagSet || chindcFlag != 0;
                }
            }
            else
            {
                string relOmaPath = OmgTableFraming.FormatTrackRelativePath(track.TrackId);
                fileSize = await transport.GetFileLengthAsync(device, relOmaPath, ct).ConfigureAwait(false);

                bool needOmaPrefix = fileSize > 0;
                if (needOmaPrefix)
                {
                    byte[] omaPrefix = await transport.ReadFilePrefixAsync(device, relOmaPath, 4096, ct).ConfigureAwait(false);
                    if (omaPrefix.Length >= 10 && omaPrefix[0] == (byte)'e' && omaPrefix[1] == (byte)'a' && omaPrefix[2] == (byte)'3')
                    {
                        int bodySz = ((omaPrefix[6] & 0x7F) << 21) | ((omaPrefix[7] & 0x7F) << 14) | ((omaPrefix[8] & 0x7F) << 7) | (omaPrefix[9] & 0x7F);
                        int needed = 10 + bodySz + 96;
                        if (needed > omaPrefix.Length && needed <= 65536)
                        {
                            omaPrefix = await transport.ReadFilePrefixAsync(device, relOmaPath, needed, ct).ConfigureAwait(false);
                        }
                    }

                    (frameCount, trackNumber, year) = ExtractMinimalOmaHeaderFields(omaPrefix);

                    if (track.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
                    {
                        // Exclude 2,144-byte (2,048 B ea3 + 96 B EA3, 0 audio frames) dummy slot stubs from active tracks.
                        if (fileSize <= 2144 && track.DurationMs == 0 && frameCount == 0)
                        {
                            dummyStubTrackIds.Add(track.TrackId);
                            continue;
                        }

                        isCopyProtected = crypto is not null
                            ? crypto.EvaluateCopyProtectionLock(omaPrefix, slot.IsDamedaFlagSet, chindcFlag)
                            : (slot.IsDamedaFlagSet || chindcFlag != 0);
                    }
                }
                else if (fileSize == 0)
                {
                    if (track.DurationMs > 0 && track.BitrateKbps > 0)
                    {
                        fileSize = (long)(track.DurationMs / 1000.0 * track.BitrateKbps * 125.0);
                    }

                    if (track.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001)
                    {
                        isCopyProtected = slot.IsDamedaFlagSet || chindcFlag != 0;
                    }
                }
            }

            trackGroupMap.TryGetValue(track.TrackId, out string? groupName);
            if (string.IsNullOrWhiteSpace(groupName) && !string.IsNullOrWhiteSpace(track.Album))
            {
                groupName = track.Album;
            }

            activeTracks.Add(track with
            {
                FrameCount = frameCount,
                TrackNumber = trackNumber,
                Year = year,
                FileSizeBytes = fileSize,
                GroupName = groupName,
                IsCopyProtected = isCopyProtected
            });
        }

        if (dummyStubTrackIds.Count > 0 || activeGroups.Any(g => string.IsNullOrWhiteSpace(g.Title)))
        {
            var trackById = activeTracks.ToDictionary(t => t.TrackId);
            for (int i = 0; i < activeGroups.Count; i++)
            {
                var grp = activeGroups[i];
                var filteredIds = dummyStubTrackIds.Count > 0
                    ? grp.TrackIds.Where(id => !dummyStubTrackIds.Contains(id)).ToArray()
                    : grp.TrackIds;

                string resolvedTitle = grp.Title;
                if (string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    string? albumFallback = filteredIds
                        .Select(id => trackById.TryGetValue(id, out var trk) ? trk.Album : null)
                        .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                    resolvedTitle = !string.IsNullOrWhiteSpace(albumFallback) ? albumFallback : $"Group {grp.GroupId}";
                    foreach (int tid in filteredIds)
                    {
                        if (trackById.TryGetValue(tid, out var trk) && string.IsNullOrWhiteSpace(trk.GroupName))
                        {
                            int idx = activeTracks.FindIndex(t => t.TrackId == tid);
                            if (idx >= 0)
                            {
                                activeTracks[idx] = trk with { GroupName = resolvedTitle };
                            }
                        }
                    }
                }

                activeGroups[i] = grp with { Title = resolvedTitle, TrackIds = filteredIds };
            }
        }

        uint totalDurationMs = activeGroups.Count > 0
            ? (uint)activeGroups.Sum(g => (long)g.TotalDurationMs)
            : (uint)activeTracks.Sum(t => (long)t.DurationMs);

        return new OmgDatabaseSnapshot(activeTracks, activeGroups, totalDurationMs);
    }

    /// <inheritdoc />
    public async Task CommitTransactionAsync(
        ConnectedDeviceIdentity device,
        OmgDatabaseTransaction transaction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(transaction);

        var transport = _transportResolver();
        var existingSnapshot = await ReadDatabaseAsync(device, ct).ConfigureAwait(false);

        var mergedTracks = new Dictionary<int, AudioTrackDto>();
        foreach (var track in existingSnapshot.Tracks)
        {
            mergedTracks[track.TrackId] = track;
        }

        foreach (int deletedId in transaction.DeletedTrackIds)
        {
            mergedTracks.Remove(deletedId);
        }

        foreach (var upserted in transaction.UpsertedTracks)
        {
            mergedTracks[upserted.TrackId] = upserted;
        }

        var trackList = mergedTracks.Values.OrderBy(t => t.TrackId).ToList();
        var mergedGroups = MergeGroups(
            existingSnapshot.Groups,
            transaction.UpdatedGroups,
            trackList,
            transaction.DeletedGroupNames,
            transaction.RenamedGroups,
            transaction.OrderedGroupNames);

        int maxTrackId = trackList.Count > 0 ? trackList.Max(t => t.TrackId) : 0;
        await WriteAllTablesAsync(
            device,
            transport,
            _cryptoResolver?.Invoke(),
            trackList,
            mergedGroups,
            maxTrackId,
            transaction.PreserveExistingMaclist0,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RebuildFromTracksAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<AudioTrackDto> tracks,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(tracks);

        var transport = _transportResolver();
        var ordered = tracks.OrderBy(t => t.TrackId).ToList();
        var groups = BuildGroupsFromTracks(ordered);
        int maxTrackId = ordered.Count > 0 ? ordered.Max(t => t.TrackId) : 0;

        await WriteAllTablesAsync(device, transport, _cryptoResolver?.Invoke(), ordered, groups, maxTrackId, preserveExistingMaclist0: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpdateTrackMetadataAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<TrackMetadataPatch> patches,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Count == 0)
        {
            return 0;
        }

        var transport = _transportResolver();
        var crypto = _cryptoResolver?.Invoke();
        var snapshot = await ReadDatabaseAsync(device, ct).ConfigureAwait(false);
        var byId = snapshot.Tracks.ToDictionary(t => t.TrackId);
        var upserted = new List<AudioTrackDto>(patches.Count);

        foreach (var patch in patches)
        {
            if (!byId.TryGetValue(patch.TrackId, out var existing))
            {
                continue;
            }

            var updatedDto = existing with
            {
                Title = patch.Title ?? existing.Title,
                Artist = patch.Artist ?? existing.Artist,
                Album = patch.Album ?? existing.Album,
                Genre = patch.Genre ?? existing.Genre,
                SortTitle = patch.SortTitle ?? existing.SortTitle,
                TrackNumber = patch.TrackNumber ?? existing.TrackNumber,
                Year = patch.Year ?? existing.Year
            };
            upserted.Add(updatedDto);

            string relOmaPath = OmgTableFraming.FormatTrackRelativePath(existing.TrackId);
            if (crypto is not null && await transport.ExistsAsync(device, relOmaPath, ct).ConfigureAwait(false))
            {
                byte[] existingOmaBytes = await transport.ReadFileAsync(device, relOmaPath, ct).ConfigureAwait(false);
                var parsed = crypto.ParseHeader(existingOmaBytes);
                var oldTags = parsed.Tags;
                var newTags = new Ea3TagMetadata(
                    Title: updatedDto.Title,
                    Artist: updatedDto.Artist,
                    Album: updatedDto.Album,
                    Genre: updatedDto.Genre,
                    Year: updatedDto.Year > 0 ? updatedDto.Year : oldTags.Year,
                    TrackNumber: updatedDto.TrackNumber > 0 ? updatedDto.TrackNumber : oldTags.TrackNumber,
                    DurationMs: existing.DurationMs > 0 ? existing.DurationMs : oldTags.DurationMs,
                    AlbumArtist: patch.AlbumArtist ?? oldTags.AlbumArtist,
                    Composer: patch.Composer ?? oldTags.Composer,
                    SortTitle: updatedDto.SortTitle ?? oldTags.SortTitle,
                    Rating: patch.Rating ?? oldTags.Rating,
                    Comment: patch.Comment ?? oldTags.Comment,
                    ArtistLinks: patch.ArtistLinks ?? oldTags.ArtistLinks);

                byte[] updatedOmaBytes = crypto.UpdateOmaEa3TagsPreservingCrypto(existingOmaBytes, newTags);
                await transport.WriteFileAtomicAsync(device, relOmaPath, updatedOmaBytes, ct).ConfigureAwait(false);
            }
        }

        if (upserted.Count == 0)
        {
            return 0;
        }

        await CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction(upserted, Array.Empty<int>(), Array.Empty<TrackGroupDto>(), PreserveExistingMaclist0: true),
            ct).ConfigureAwait(false);

        return upserted.Count;
    }

    private static async Task WriteAllTablesAsync(
        ConnectedDeviceIdentity device,
        ITransportDriver transport,
        IOpenMgCryptoService? crypto,
        IReadOnlyList<AudioTrackDto> activeTracks,
        IReadOnlyList<TrackGroupDto> activeGroups,
        int maxTrackId,
        bool preserveExistingMaclist0,
        CancellationToken ct)
    {
        uint totalDurationMs = (uint)activeTracks.Sum(t => (long)t.DurationMs);
        var schema = await OmgDatabaseSchemaIntrospector.IntrospectAsync(device, transport, ct).ConfigureAwait(false);

        byte[] gtrlstBytes = schema.Raw00GtrlstBytes
            ?? (schema.Has2DTree ? Build00GtrlstE500_45() : Build00GtrlstSonicStage34());
        byte[] treinfBytes = Build02Treinf(totalDurationMs, schema.TreinfElementCount);

        var (tree01Bytes, ginf01Bytes) = BuildTreeAndGinf01(activeGroups, activeTracks);
        var (tree02Bytes, ginf02Bytes) = BuildTagTreeAndGinf(activeTracks, t => t.Artist, "Artist");
        var (tree03Bytes, ginf03Bytes) = BuildTagTreeAndGinf(activeTracks, t => t.Album, "Album");
        var (tree04Bytes, ginf04Bytes) = BuildTagTreeAndGinf(activeTracks, t => t.Genre, string.Empty);

        byte[] cntinfBytes = Build04Cntinf(activeTracks, maxTrackId);
        byte[] cidlstBytes = Build05Cidlst(activeTracks, maxTrackId);

        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["OMGAUDIO/04CNTINF.DAT"] = cntinfBytes,
            ["OMGAUDIO/05CIDLST.DAT"] = cidlstBytes,
            ["OMGAUDIO/00GTRLST.DAT"] = gtrlstBytes,
            ["OMGAUDIO/02TREINF.DAT"] = treinfBytes,
            ["OMGAUDIO/01TREE01.DAT"] = tree01Bytes,
            ["OMGAUDIO/03GINF01.DAT"] = ginf01Bytes,
            ["OMGAUDIO/01TREE02.DAT"] = tree02Bytes,
            ["OMGAUDIO/03GINF02.DAT"] = ginf02Bytes,
            ["OMGAUDIO/01TREE03.DAT"] = tree03Bytes,
            ["OMGAUDIO/03GINF03.DAT"] = ginf03Bytes,
            ["OMGAUDIO/01TREE04.DAT"] = tree04Bytes,
            ["OMGAUDIO/03GINF04.DAT"] = ginf04Bytes
        };

        if (!await transport.ExistsAsync(device, "OMGAUDIO/01TREE22.DAT", ct).ConfigureAwait(false))
        {
            staged["OMGAUDIO/01TREE22.DAT"] = BuildEmptyTree22();
        }

        if (!await transport.ExistsAsync(device, "OMGAUDIO/03GINF22.DAT", ct).ConfigureAwait(false))
        {
            staged["OMGAUDIO/03GINF22.DAT"] = BuildEmptyGinf22();
        }

        if (schema.Has2DTree)
        {
            var (tree2dBytes, ginf2dBytes) = BuildTreeAndGinf2D(activeTracks);
            staged["OMGAUDIO/01TREE2D.DAT"] = tree2dBytes;
            staged["OMGAUDIO/03GINF2D.DAT"] = ginf2dBytes;
        }

        if (schema.HasReservedDat && !schema.IsAdoptMode)
        {
            staged["OMGAUDIO/RESERVED.DAT"] = [0x43];
        }

        // Layer 4: Pre-commit invariant validation (hard gate before any disk write)
        OmgDatabaseValidator.ValidateBeforeCommit(staged, activeTracks, device.Profile);

        // Pre-commit in-memory snapshot of existing .DAT files for atomic rollback on mid-commit I/O failure.
        var rollbackSnapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in staged.Keys)
        {
            if (await transport.ExistsAsync(device, path, ct).ConfigureAwait(false))
            {
                rollbackSnapshot[path] = await transport.ReadFileAsync(device, path, ct).ConfigureAwait(false);
            }
        }

        // Write index tables atomically after audio payloads are already flushed to disk (04CNTINF/05CIDLST first).
        try
        {
            foreach (var (path, bytes) in staged)
            {
                await transport.WriteFileAtomicAsync(device, path, bytes, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var (path, oldBytes) in rollbackSnapshot)
            {
                try
                {
                    await transport.WriteFileAtomicAsync(device, path, oldBytes, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort rollback.
                }
            }

            throw;
        }

        bool hasMagicGateTrack = activeTracks.Any(t => t.EncryptionMode == OmaEncryptionMode.MagicGateLsi_0x0001 || t.MaclistEntry8 is { Length: 8 });
        bool maclistExists = await transport.ExistsAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false);
        if (hasMagicGateTrack || maclistExists)
        {
            await UpdateMagicGateSupportFilesAsync(device, transport, crypto, activeTracks, maclistExists, preserveExistingMaclist0, ct).ConfigureAwait(false);
        }
    }

    private static async Task UpdateMagicGateSupportFilesAsync(
        ConnectedDeviceIdentity device,
        ITransportDriver transport,
        IOpenMgCryptoService? crypto,
        IReadOnlyList<AudioTrackDto> activeTracks,
        bool maclistExists,
        bool preserveExistingMaclist0,
        CancellationToken ct)
    {
        // OMGAUDIO/00010021.DAT is strictly read-only from the connected Walkman; never synthesize or write it.

        // CRITICAL GUARD (Option 2 Slot-Recycling): Never mutate MACLIST0.DAT once hardware-signed by ICV,
        // unless Option 1 COM ICV Bridge explicitly commits an updated ICV + MACLIST0.
        if (preserveExistingMaclist0 && maclistExists)
        {
            return;
        }

        const int maclistSize = 32768; // 4096 * 8-byte slots
        byte[] maclist = maclistExists
            ? await transport.ReadFileAsync(device, "OMGAUDIO/MACLIST0.DAT", ct).ConfigureAwait(false)
            : new byte[maclistSize];

        if (maclist.Length < maclistSize)
        {
            Array.Resize(ref maclist, maclistSize);
        }

        var lookup = activeTracks.ToDictionary(t => t.TrackId);
        for (int trackId = 1; trackId <= 4096; trackId++)
        {
            int offset = (trackId - 1) * 8;
            if (lookup.TryGetValue(trackId, out var track))
            {
                if (track.EncryptionMode != OmaEncryptionMode.MagicGateLsi_0x0001)
                {
                    maclist.AsSpan(offset, 8).Clear();
                }
                else if (track.MaclistEntry8 is { Length: 8 })
                {
                    track.MaclistEntry8.CopyTo(maclist.AsSpan(offset, 8));
                }
            }
            else
            {
                maclist.AsSpan(offset, 8).Clear();
            }
        }

        await transport.WriteFileAtomicAsync(device, "OMGAUDIO/MACLIST0.DAT", maclist, ct).ConfigureAwait(false);
    }

    private static List<(bool IsPopulated, bool IsDamedaFlagSet, AudioTrackDto Track)> ParseCntinfSlots(
        ReadOnlySpan<byte> cntinfBytes,
        ReadOnlySpan<byte> cidlstBytes)
    {
        if (cntinfBytes.Length < 0x30)
        {
            return new List<(bool, bool, AudioTrackDto)>();
        }

        var desc = OmgTableFraming.ReadClassDescriptor(cntinfBytes, 0);
        if (desc.StartAddress + OmgTableFraming.ClassHeaderSize > cntinfBytes.Length)
        {
            return new List<(bool, bool, AudioTrackDto)>();
        }

        var hdr = OmgTableFraming.ReadClassHeader(cntinfBytes, desc.StartAddress);
        if (hdr.ElementCount == 0 || hdr.ElementSize <= 0)
        {
            return new List<(bool, bool, AudioTrackDto)>();
        }

        byte[]? cidData = cidlstBytes.Length >= 0x30 ? cidlstBytes.ToArray() : null;
        int cidCount = 0;
        int cidStart = 0x30;
        if (cidData is not null)
        {
            var cidDesc = OmgTableFraming.ReadClassDescriptor(cidData, 0);
            var cidHdr = OmgTableFraming.ReadClassHeader(cidData, cidDesc.StartAddress);
            cidCount = cidHdr.ElementCount;
            cidStart = cidDesc.StartAddress + OmgTableFraming.ClassHeaderSize;
        }

        var slots = new List<(bool, bool, AudioTrackDto)>(hdr.ElementCount);
        int baseOffset = desc.StartAddress + OmgTableFraming.ClassHeaderSize;

        for (int i = 0; i < hdr.ElementCount; i++)
        {
            int elemOffset = baseOffset + i * hdr.ElementSize;
            if (elemOffset + hdr.ElementSize > cntinfBytes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> elem = cntinfBytes.Slice(elemOffset, hdr.ElementSize);
            bool isDamedaFlagSet = elem[1] != 0x00;
            ushort protection = BinaryPrimitives.ReadUInt16BigEndian(elem.Slice(2, 2));
            uint fileProps = BinaryPrimitives.ReadUInt32BigEndian(elem.Slice(4, 4));
            uint durationMs = BinaryPrimitives.ReadUInt32BigEndian(elem.Slice(8, 4));

            string title = OmgTableFraming.ReadUtf16BeSubElementValue(elem.Slice(16 + 0 * 0x80, 0x80));
            string artist = OmgTableFraming.ReadUtf16BeSubElementValue(elem.Slice(16 + 1 * 0x80, 0x80));
            string album = OmgTableFraming.ReadUtf16BeSubElementValue(elem.Slice(16 + 2 * 0x80, 0x80));
            string genre = OmgTableFraming.ReadUtf16BeSubElementValue(elem.Slice(16 + 3 * 0x80, 0x80));
            string sortTitle = OmgTableFraming.ReadUtf16BeSubElementValue(elem.Slice(16 + 4 * 0x80, 0x80));

            byte[]? lsiKey48 = null;
            if (cidData is not null && i < cidCount && cidStart + (i + 1) * CidlElementSize <= cidData.Length)
            {
                lsiKey48 = cidData.AsSpan(cidStart + i * CidlElementSize, CidlElementSize).ToArray();
            }

            var (codec, bitrateKbps) = OmgTableFraming.DecodeFileProperties(fileProps);
            bool isPopulated = protection != 0 || durationMs != 0 || !string.IsNullOrEmpty(title);

            var dto = new AudioTrackDto(
                TrackId: i + 1,
                Title: title,
                Artist: artist,
                Album: album,
                Genre: genre,
                SortTitle: string.IsNullOrEmpty(sortTitle) ? null : sortTitle,
                TrackNumber: 0,
                Year: 0,
                DurationMs: durationMs,
                FrameCount: 0,
                Codec: codec,
                BitrateKbps: bitrateKbps,
                EncryptionMode: (OmaEncryptionMode)protection,
                FilePropertiesBigEndian: fileProps,
                LsiKeyring48: lsiKey48);

            slots.Add((isPopulated, isDamedaFlagSet, dto));
        }

        return slots;
    }

    private static Dictionary<int, ushort> Parse05ChindcFlags(ReadOnlySpan<byte> chindcBytes)
    {
        var flags = new Dictionary<int, ushort>();
        if (chindcBytes.Length < 0x30)
        {
            return flags;
        }

        var desc = OmgTableFraming.ReadClassDescriptor(chindcBytes, 0);
        if (desc.StartAddress + OmgTableFraming.ClassHeaderSize > chindcBytes.Length)
        {
            return flags;
        }

        var hdr = OmgTableFraming.ReadClassHeader(chindcBytes, desc.StartAddress);
        int baseOffset = desc.StartAddress + OmgTableFraming.ClassHeaderSize;
        for (int i = 0; i < hdr.ElementCount; i++)
        {
            int elemOffset = baseOffset + i * hdr.ElementSize;
            if (elemOffset + 2 > chindcBytes.Length || hdr.ElementSize <= 0)
            {
                break;
            }

            ushort flag = BinaryPrimitives.ReadUInt16BigEndian(chindcBytes.Slice(elemOffset, 2));
            if (flag != 0)
            {
                flags[i + 1] = flag;
            }
        }

        return flags;
    }

    private static (List<TrackGroupDto> Groups, HashSet<int> ActiveTrackIds) ParseActiveTree01Groups(
        ReadOnlySpan<byte> treeBytes,
        ReadOnlySpan<byte> ginfBytes)
    {
        if (treeBytes.Length < 0x40 || ginfBytes.Length < 0x30)
        {
            return (new List<TrackGroupDto>(), new HashSet<int>());
        }

        var gplbDesc = OmgTableFraming.ReadClassDescriptor(treeBytes, 0);
        var tplbDesc = OmgTableFraming.ReadClassDescriptor(treeBytes, 1);

        if (gplbDesc.StartAddress + OmgTableFraming.ClassHeaderSize > treeBytes.Length ||
            tplbDesc.StartAddress + OmgTableFraming.ClassHeaderSize > treeBytes.Length)
        {
            return (new List<TrackGroupDto>(), new HashSet<int>());
        }

        var gplbHdr = OmgTableFraming.ReadClassHeader(treeBytes, gplbDesc.StartAddress);
        var tplbHdr = OmgTableFraming.ReadClassHeader(treeBytes, tplbDesc.StartAddress);

        var ginfDesc = OmgTableFraming.ReadClassDescriptor(ginfBytes, 0);
        if (ginfDesc.StartAddress + OmgTableFraming.ClassHeaderSize > ginfBytes.Length)
        {
            return (new List<TrackGroupDto>(), new HashSet<int>());
        }

        var ginfHdr = OmgTableFraming.ReadClassHeader(ginfBytes, ginfDesc.StartAddress);

        int activeTrackCount = Math.Min(tplbHdr.ElementCount, (int)tplbHdr.Complement1);
        var activeTrackIdsArray = new int[activeTrackCount];
        int tplbDataOffset = tplbDesc.StartAddress + OmgTableFraming.ClassHeaderSize;

        for (int i = 0; i < activeTrackCount; i++)
        {
            int pos = tplbDataOffset + i * tplbHdr.ElementSize;
            if (pos + 2 > treeBytes.Length)
            {
                activeTrackCount = i;
                activeTrackIdsArray = activeTrackIdsArray[..i];
                break;
            }
            activeTrackIdsArray[i] = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.Slice(pos, 2));
        }

        int gplbScanLimit = gplbHdr.Complement1 > 0
            ? Math.Min(gplbHdr.ElementCount, (int)gplbHdr.Complement1)
            : gplbHdr.ElementCount;

        int gplbDataOffset = gplbDesc.StartAddress + OmgTableFraming.ClassHeaderSize;
        var rawActiveGplb = new List<(int GroupId, int FirstTrackPos)>();

        for (int i = 0; i < gplbScanLimit; i++)
        {
            int pos = gplbDataOffset + i * gplbHdr.ElementSize;
            if (pos + 8 > treeBytes.Length)
            {
                break;
            }
            ushort groupId = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.Slice(pos, 2));
            ushort level = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.Slice(pos + 2, 2));
            ushort firstTrackPos = BinaryPrimitives.ReadUInt16BigEndian(treeBytes.Slice(pos + 4, 2));

            if (level == 0x0100 && groupId >= 1)
            {
                rawActiveGplb.Add((groupId, firstTrackPos));
            }
        }

        var groups = new List<TrackGroupDto>(rawActiveGplb.Count);
        int ginfDataOffset = ginfDesc.StartAddress + OmgTableFraming.ClassHeaderSize;
        var nonZeroStarts = rawActiveGplb
            .Where(x => x.FirstTrackPos > 0)
            .Select(x => x.FirstTrackPos)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        for (int i = 0; i < rawActiveGplb.Count; i++)
        {
            var (groupId, startPos1Based) = rawActiveGplb[i];
            int[] memberIds;
            if (startPos1Based <= 0)
            {
                memberIds = Array.Empty<int>();
            }
            else
            {
                int startIdx = Math.Max(0, startPos1Based - 1);
                int nextPos = nonZeroStarts.FirstOrDefault(p => p > startPos1Based);
                int endIdx = nextPos > 0
                    ? Math.Min(activeTrackCount, nextPos - 1)
                    : activeTrackCount;
                memberIds = startIdx < endIdx
                    ? activeTrackIdsArray[startIdx..endIdx]
                    : Array.Empty<int>();
            }

            string title = $"Group {groupId}";
            string artist = string.Empty;
            string genre = string.Empty;
            uint groupDurationMs = 0;

            if (groupId <= ginfHdr.ElementCount)
            {
                int elemOffset = ginfDataOffset + (groupId - 1) * ginfHdr.ElementSize;
                if (elemOffset + ginfHdr.ElementSize <= ginfBytes.Length)
                {
                    ReadOnlySpan<byte> gel = ginfBytes.Slice(elemOffset, ginfHdr.ElementSize);
                    groupDurationMs = BinaryPrimitives.ReadUInt32BigEndian(gel.Slice(8, 4));
                    ushort subCount = BinaryPrimitives.ReadUInt16BigEndian(gel.Slice(12, 2));
                    if (subCount >= 1)
                    {
                        string parsedTitle = OmgTableFraming.ReadUtf16BeSubElementValue(gel.Slice(16, 0x80));
                        title = string.IsNullOrWhiteSpace(parsedTitle) ? string.Empty : parsedTitle;
                    }

                    if (subCount >= 2)
                    {
                        artist = OmgTableFraming.ReadUtf16BeSubElementValue(gel.Slice(16 + 0x80, 0x80));
                    }

                    if (subCount >= 3)
                    {
                        genre = OmgTableFraming.ReadUtf16BeSubElementValue(gel.Slice(16 + 2 * 0x80, 0x80));
                    }
                }
            }

            groups.Add(new TrackGroupDto(groupId, title, artist, genre, groupDurationMs, memberIds));
        }

        return (groups, activeTrackIdsArray.ToHashSet());
    }

    private static (uint FrameCount, int TrackNumber, int Year) ExtractMinimalOmaHeaderFields(ReadOnlySpan<byte> omaPrefix)
    {
        if (omaPrefix.Length < 106 ||
            omaPrefix[0] != (byte)'e' ||
            omaPrefix[1] != (byte)'a' ||
            omaPrefix[2] != (byte)'3')
        {
            return (0, 0, 0);
        }

        int bodySize =
            ((omaPrefix[6] & 0x7F) << 21) |
            ((omaPrefix[7] & 0x7F) << 14) |
            ((omaPrefix[8] & 0x7F) << 7) |
            (omaPrefix[9] & 0x7F);
        int ea3Total = 10 + bodySize;

        int trackNum = 0;
        int year = 0;
        int pos = 10;
        while (pos + 10 <= Math.Min(ea3Total, omaPrefix.Length))
        {
            if (omaPrefix[pos] == 0x00)
            {
                break;
            }

            int frameSize = BinaryPrimitives.ReadInt32BigEndian(omaPrefix.Slice(pos + 4, 4));
            if (frameSize <= 0 || pos + 10 + frameSize > omaPrefix.Length)
            {
                break;
            }

            string id = Encoding.ASCII.GetString(omaPrefix.Slice(pos, 4));
            if (frameSize > 1 && omaPrefix[pos + 10] == 0x02)
            {
                string val = Encoding.BigEndianUnicode.GetString(omaPrefix.Slice(pos + 11, frameSize - 1)).TrimEnd('\0');
                if (id == "TYER" && int.TryParse(val, out int y))
                {
                    year = y;
                }
                else if (id == "TXXX")
                {
                    int nul = val.IndexOf('\0');
                    if (nul > 0 &&
                        val[..nul].Equals("OMG_TRACK", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(val[(nul + 1)..], out int tr))
                    {
                        trackNum = tr;
                    }
                }
            }

            pos += 10 + frameSize;
        }

        uint frameCount = 0;
        if (ea3Total + 44 <= omaPrefix.Length &&
            omaPrefix[ea3Total] == (byte)'E' &&
            omaPrefix[ea3Total + 1] == (byte)'A' &&
            omaPrefix[ea3Total + 2] == (byte)'3')
        {
            frameCount = BinaryPrimitives.ReadUInt32BigEndian(omaPrefix.Slice(ea3Total + 40, 4));
        }

        return (frameCount, trackNum, year);
    }

    private static List<TrackGroupDto> MergeGroups(
        IReadOnlyList<TrackGroupDto> existingGroups,
        IReadOnlyList<TrackGroupDto> updatedGroups,
        IReadOnlyList<AudioTrackDto> allActiveTracks,
        IReadOnlyList<string>? deletedGroupNames = null,
        IReadOnlyDictionary<string, string>? renamedGroups = null,
        IReadOnlyList<string>? orderedGroupNames = null)
    {
        var deletedSet = deletedGroupNames is { Count: > 0 }
            ? new HashSet<string>(deletedGroupNames.Select(OmgTableFraming.NormalizeGroupKey), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string>? normalizedRenames = null;
        if (renamedGroups is { Count: > 0 })
        {
            normalizedRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in renamedGroups)
            {
                string nk = OmgTableFraming.NormalizeGroupKey(k);
                string nv = OmgTableFraming.NormalizeGroupKey(v);
                if (!string.IsNullOrEmpty(nk) && !string.IsNullOrEmpty(nv))
                {
                    normalizedRenames[nk] = nv;
                }
            }
        }

        if (updatedGroups.Count > 0)
        {
            var reassignedTrackIds = updatedGroups.SelectMany(u => u.TrackIds).ToHashSet();
            var mergedByTitle = new Dictionary<string, (TrackGroupDto Group, List<int> TrackIds)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in existingGroups)
            {
                string normTitle = OmgTableFraming.NormalizeGroupKey(g.Title);
                if (deletedSet.Contains(normTitle))
                {
                    continue;
                }

                string effectiveTitle = normalizedRenames is not null && normalizedRenames.TryGetValue(normTitle, out var renamed)
                    ? renamed
                    : normTitle;
                var remainingIds = g.TrackIds.Where(id => !reassignedTrackIds.Contains(id)).ToList();
                if (mergedByTitle.TryGetValue(effectiveTitle, out var existingEntry))
                {
                    existingEntry.TrackIds.AddRange(remainingIds);
                }
                else
                {
                    mergedByTitle[effectiveTitle] = (g with { Title = effectiveTitle }, remainingIds);
                }
            }

            foreach (var u in updatedGroups)
            {
                string normU = OmgTableFraming.NormalizeGroupKey(u.Title);
                if (!deletedSet.Contains(normU))
                {
                    mergedByTitle[normU] = (u with { Title = normU }, new List<int>(u.TrackIds));
                }
            }

            var activeSet = allActiveTracks.ToDictionary(t => t.TrackId);
            var globalAssigned = new HashSet<int>();
            var finalized = new List<TrackGroupDto>(mergedByTitle.Count);
            int nextId = 1;
            foreach (var (_, (grp, tids)) in mergedByTitle)
            {
                var validTids = tids.Where(id => activeSet.ContainsKey(id) && globalAssigned.Add(id)).ToArray();
                if (validTids.Length == 0)
                {
                    continue;
                }

                uint dur = (uint)validTids.Sum(id => (long)activeSet[id].DurationMs);
                finalized.Add(new TrackGroupDto(nextId++, grp.Title, grp.Artist, grp.Genre, dur, validTids));
            }

            return ApplyExplicitGroupOrder(finalized, orderedGroupNames);
        }

        var trackById = allActiveTracks.ToDictionary(t => t.TrackId);
        var assignedTracks = new HashSet<int>();
        var resultByTitle = new Dictionary<string, (string Title, string Artist, string Genre, List<int> Members)>(StringComparer.OrdinalIgnoreCase);
        var insertionOrder = new List<string>();

        // Preserve existing group ordering, merge any duplicate normalized titles, and filter out deleted tracks.
        foreach (var g in existingGroups)
        {
            string normTitle = OmgTableFraming.NormalizeGroupKey(g.Title);
            if (string.IsNullOrEmpty(normTitle) || deletedSet.Contains(normTitle))
            {
                continue;
            }

            string effectiveTitle = normalizedRenames is not null && normalizedRenames.TryGetValue(normTitle, out var renamed)
                ? renamed
                : normTitle;

            if (!resultByTitle.TryGetValue(effectiveTitle, out var bucket))
            {
                bucket = (effectiveTitle, OmgTableFraming.NormalizeGroupKey(g.Artist), OmgTableFraming.NormalizeGroupKey(g.Genre), new List<int>());
                resultByTitle[effectiveTitle] = bucket;
                insertionOrder.Add(effectiveTitle);
            }

            foreach (int id in g.TrackIds)
            {
                if (!trackById.TryGetValue(id, out var t))
                {
                    continue;
                }

                string trackGrp = OmgTableFraming.NormalizeGroupKey(t.GroupName);
                bool belongsHere = string.IsNullOrEmpty(trackGrp) ||
                                   string.Equals(trackGrp, normTitle, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(trackGrp, effectiveTitle, StringComparison.OrdinalIgnoreCase);
                if (belongsHere && assignedTracks.Add(id))
                {
                    bucket.Members.Add(id);
                }
            }
        }

        // Attach newly upserted/unassigned tracks whose GroupName (or Album fallback) matches an existing group.
        foreach (var track in allActiveTracks)
        {
            if (assignedTracks.Contains(track.TrackId))
            {
                continue;
            }

            string rawGrp = OmgTableFraming.NormalizeGroupKey(track.GroupName);
            string normAlbum = OmgTableFraming.NormalizeGroupKey(track.Album);
            string candidate = !string.IsNullOrEmpty(rawGrp) ? rawGrp : normAlbum;
            if (!string.IsNullOrEmpty(candidate) && normalizedRenames is not null && normalizedRenames.TryGetValue(candidate, out var renamedCand))
            {
                candidate = renamedCand;
            }

            if (!string.IsNullOrEmpty(candidate) && resultByTitle.TryGetValue(candidate, out var existingBucket))
            {
                if (assignedTracks.Add(track.TrackId))
                {
                    if (track.TargetPositionIndex.HasValue)
                    {
                        int insertIdx = Math.Clamp(track.TargetPositionIndex.Value, 0, existingBucket.Members.Count);
                        existingBucket.Members.Insert(insertIdx, track.TrackId);
                    }
                    else
                    {
                        existingBucket.Members.Add(track.TrackId);
                    }
                }
            }
        }

        var result = new List<TrackGroupDto>();
        int groupIdCounter = 1;
        foreach (string titleKey in insertionOrder)
        {
            var bucket = resultByTitle[titleKey];
            if (bucket.Members.Count == 0)
            {
                continue;
            }

            uint dur = (uint)bucket.Members.Sum(id => (long)trackById[id].DurationMs);
            result.Add(new TrackGroupDto(groupIdCounter++, bucket.Title, bucket.Artist, bucket.Genre, dur, bucket.Members));
        }

        // Group any remaining unassigned tracks by normalized GroupName or Album.
        var unassigned = allActiveTracks.Where(t => !assignedTracks.Contains(t.TrackId)).ToList();
        foreach (var bucket in unassigned.GroupBy(
            t =>
            {
                string rawGrp = OmgTableFraming.NormalizeGroupKey(t.GroupName);
                string rawAlb = OmgTableFraming.NormalizeGroupKey(t.Album);
                string raw = !string.IsNullOrEmpty(rawGrp) ? rawGrp : (string.IsNullOrEmpty(rawAlb) ? "Unknown Album" : rawAlb);
                if (deletedSet.Contains(raw))
                {
                    return "Unassigned";
                }
                return normalizedRenames is not null && normalizedRenames.TryGetValue(raw, out var renamed) ? renamed : raw;
            },
            StringComparer.OrdinalIgnoreCase))
        {
            var orderedBucket = bucket
                .OrderBy(t => t.TargetPositionIndex ?? int.MaxValue)
                .ThenBy(t => t.TrackId)
                .Select(t => t.TrackId)
                .ToArray();
            uint dur = (uint)bucket.Sum(t => (long)t.DurationMs);
            string[] distinctArtists = bucket.Select(t => OmgTableFraming.NormalizeGroupKey(t.Artist)).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] distinctGenres = bucket.Select(t => OmgTableFraming.NormalizeGroupKey(t.Genre)).Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string grpArtist = distinctArtists.Length == 1 ? distinctArtists[0] : string.Empty;
            string grpGenre = distinctGenres.Length == 1 ? distinctGenres[0] : string.Empty;
            result.Add(new TrackGroupDto(groupIdCounter++, bucket.Key, grpArtist, grpGenre, dur, orderedBucket));
        }

        return ApplyExplicitGroupOrder(result, orderedGroupNames);
    }

    private static List<TrackGroupDto> BuildGroupsFromTracks(IReadOnlyList<AudioTrackDto> tracks)
    {
        var result = new List<TrackGroupDto>();
        int gid = 1;
        foreach (var bucket in tracks.GroupBy(
            t => !string.IsNullOrWhiteSpace(t.GroupName) ? t.GroupName! : (string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album),
            StringComparer.OrdinalIgnoreCase))
        {
            var ids = bucket.OrderBy(t => t.TrackNumber).ThenBy(t => t.TrackId).Select(t => t.TrackId).ToArray();
            uint dur = (uint)bucket.Sum(t => (long)t.DurationMs);
            result.Add(new TrackGroupDto(gid++, bucket.Key, string.Empty, string.Empty, dur, ids));
        }

        return result;
    }

    private static List<TrackGroupDto> ApplyExplicitGroupOrder(
        List<TrackGroupDto> groups,
        IReadOnlyList<string>? orderedGroupNames)
    {
        if (orderedGroupNames is null || orderedGroupNames.Count == 0 || groups.Count <= 1)
        {
            return groups;
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < orderedGroupNames.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(orderedGroupNames[i]) && !rank.ContainsKey(orderedGroupNames[i]))
            {
                rank[orderedGroupNames[i]] = i;
            }
        }

        var sorted = groups
            .Select((g, idx) => (Group: g, Rank: rank.TryGetValue(g.Title, out int r) ? r : int.MaxValue, OriginalIdx: idx))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.OriginalIdx)
            .Select((x, idx) => x.Group with { GroupId = idx + 1 })
            .ToList();

        return sorted;
    }

    private static byte[] Build00GtrlstSonicStage34()
    {
        const int sysbStart = 0x30;
        const int sysbLen = 0x70;
        const int gtlbStart = 0xA0;
        const int gtlbElemSize = 0x50;
        const int gtlbLen = OmgTableFraming.ClassHeaderSize + SonicStageSlotCount * gtlbElemSize; // 0xAB0

        byte[] data = new byte[gtlbStart + gtlbLen]; // 2,896 bytes
        var span = data.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "GTLT", classCount: 2);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "SYSB", sysbStart, sysbLen);
        OmgTableFraming.WriteClassDescriptor(span.Slice(32), "GTLB", gtlbStart, gtlbLen);

        OmgTableFraming.WriteClassHeader(span.Slice(sysbStart), "SYSB", 1, 0x50, 0xD0000000u, 0);
        OmgTableFraming.WriteClassHeader(span.Slice(gtlbStart), "GTLB", SonicStageSlotCount, gtlbElemSize, 0x00000005u, 0x03000000u);

        int elemBase = gtlbStart + OmgTableFraming.ClassHeaderSize;
        WriteGtlbElement(span.Slice(elemBase + 0 * gtlbElemSize, gtlbElemSize), 1, 1, 1, string.Empty);
        WriteGtlbElement(span.Slice(elemBase + 1 * gtlbElemSize, gtlbElemSize), 2, 3, 1, "TPE1");
        WriteGtlbElement(span.Slice(elemBase + 2 * gtlbElemSize, gtlbElemSize), 3, 3, 1, "TALB");
        WriteGtlbElement(span.Slice(elemBase + 3 * gtlbElemSize, gtlbElemSize), 4, 3, 1, "TCON");
        WriteGtlbElement(span.Slice(elemBase + 4 * gtlbElemSize, gtlbElemSize), 0x22, 2, 0, string.Empty);

        int slotIndex = 5;
        for (ushort fileRef = 5; fileRef <= 33; fileRef++)
        {
            WriteGtlbElement(span.Slice(elemBase + slotIndex * gtlbElemSize, gtlbElemSize), fileRef, 0, 0, string.Empty);
            slotIndex++;
        }

        return data;
    }

    private static byte[] Build00GtrlstE500_45()
    {
        const int slotCount = 45;
        const int sysbStart = 0x30;
        const int sysbLen = 0x70;
        const int gtlbStart = 0xA0;
        const int gtlbElemSize = 0x50;
        const int gtlbLen = OmgTableFraming.ClassHeaderSize + slotCount * gtlbElemSize;

        byte[] data = new byte[gtlbStart + gtlbLen];
        var span = data.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "GTLT", classCount: 2);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "SYSB", sysbStart, sysbLen);
        OmgTableFraming.WriteClassDescriptor(span.Slice(32), "GTLB", gtlbStart, gtlbLen);

        OmgTableFraming.WriteClassHeader(span.Slice(sysbStart), "SYSB", 1, 0x50, 0xD0000000u, 0);
        OmgTableFraming.WriteClassHeader(span.Slice(gtlbStart), "GTLB", slotCount, gtlbElemSize, 0x00000006u, 0x03000000u);

        int elemBase = gtlbStart + OmgTableFraming.ClassHeaderSize;
        WriteGtlbElement(span.Slice(elemBase + 0 * gtlbElemSize, gtlbElemSize), 1, 1, 1, string.Empty);
        WriteGtlbElement(span.Slice(elemBase + 1 * gtlbElemSize, gtlbElemSize), 2, 3, 1, "TPE1");
        WriteGtlbElement(span.Slice(elemBase + 2 * gtlbElemSize, gtlbElemSize), 3, 3, 1, "TALB");
        WriteGtlbElement(span.Slice(elemBase + 3 * gtlbElemSize, gtlbElemSize), 4, 3, 1, "TCON");
        WriteGtlbElement(span.Slice(elemBase + 4 * gtlbElemSize, gtlbElemSize), 0x22, 2, 0, string.Empty);
        WriteGtlbElement(span.Slice(elemBase + 5 * gtlbElemSize, gtlbElemSize), 0x2D, 2, 0, string.Empty);

        int slotIndex = 6;
        for (ushort fileRef = 5; fileRef < slotCount; fileRef++)
        {
            if (fileRef == 0x22 || fileRef == 0x2D) continue;
            WriteGtlbElement(span.Slice(elemBase + slotIndex * gtlbElemSize, gtlbElemSize), fileRef, 0, 0, string.Empty);
            slotIndex++;
        }

        return data;
    }

    private static void WriteGtlbElement(Span<byte> dest, ushort fileRef, ushort typeFlag, ushort tagCount, string tag1)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(0, 2), fileRef);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(2, 2), typeFlag);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(0x10, 2), tagCount);
        if (!string.IsNullOrEmpty(tag1))
        {
            Encoding.ASCII.GetBytes(tag1.AsSpan(0, 4), dest.Slice(0x14, 4));
        }
    }

    private static byte[] Build02TreinfSonicStage34(uint totalDurationMs) =>
        Build02Treinf(totalDurationMs, SonicStageSlotCount);

    private static byte[] Build02Treinf(uint totalDurationMs, int slotCount)
    {
        if (slotCount <= SonicStageSlotCount)
        {
            slotCount = SonicStageSlotCount;
        }

        uint allocLen = slotCount == SonicStageSlotCount
            ? (uint)TreinfAllocatedLength
            : (uint)(0x10 + slotCount * GinfSimpleElementSize + 0xAB0);
        byte[] data = new byte[0x20 + allocLen];
        var span = data.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "GTIF", classCount: 1);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "GTFB", 0x20, (int)allocLen);
        OmgTableFraming.WriteClassHeader(span.Slice(0x20), "GTFB", (ushort)slotCount, GinfSimpleElementSize);

        int baseOffset = 0x30;
        for (int i = 0; i < 4; i++)
        {
            Span<byte> el = span.Slice(baseOffset + i * GinfSimpleElementSize, GinfSimpleElementSize);
            BinaryPrimitives.WriteUInt32BigEndian(el.Slice(8, 4), totalDurationMs);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16), "TIT2", string.Empty);
        }

        // Slot 34 (index 33 -> 0x22 playlists) initialized with key=0, TIT2=""
        Span<byte> slot34 = span.Slice(baseOffset + 33 * GinfSimpleElementSize, GinfSimpleElementSize);
        BinaryPrimitives.WriteUInt16BigEndian(slot34.Slice(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(slot34.Slice(14, 2), OmgTableFraming.SubElementSize);
        OmgTableFraming.WriteUtf16BeSubElement(slot34.Slice(16), "TIT2", string.Empty);

        // Slot 45 (index 44 -> 0x2D Artist/Album hierarchy on 45-slot NW-E500/JSymphonic databases)
        if (slotCount >= 45)
        {
            Span<byte> slot45 = span.Slice(baseOffset + 44 * GinfSimpleElementSize, GinfSimpleElementSize);
            BinaryPrimitives.WriteUInt32BigEndian(slot45.Slice(8, 4), totalDurationMs);
            BinaryPrimitives.WriteUInt16BigEndian(slot45.Slice(12, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(slot45.Slice(14, 2), OmgTableFraming.SubElementSize);
            OmgTableFraming.WriteUtf16BeSubElement(slot45.Slice(16), "TIT2", "STD_TPE1");
        }

        return data;
    }


    private static (byte[] TreeBytes, byte[] GinfBytes) BuildTreeAndGinf2D(IReadOnlyList<AudioTrackDto> activeTracks)
    {
        // Level 1: Artist, Level 2: Album (0x110-byte GPFB elements)
        const ushort elemSize2D = 0x0110;
        const int DefaultGplbAllocatedLength = 0x4010;
        var artistGroups = activeTracks
            .GroupBy(t =>
            {
                string a = OmgTableFraming.NormalizeGroupKey(t.Artist);
                return string.IsNullOrEmpty(a) ? "Unknown Artist" : a;
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gplbNodes = new List<(ushort Id, byte Level, ushort FirstTrackIdx, ushort Unknown)>();
        var ginfRecords = new List<(string Title, uint DurationMs)>();
        var tplbTrackIds = new List<ushort>();

        ushort nextId = 1;
        ushort trackIdx = 1;
        foreach (var artist in artistGroups)
        {
            uint artistDur = (uint)artist.Sum(t => (long)t.DurationMs);
            gplbNodes.Add((nextId++, 1, 0, 0));
            ginfRecords.Add((artist.Key, artistDur));

            var albums = artist
                .GroupBy(t =>
                {
                    string alb = OmgTableFraming.NormalizeGroupKey(t.Album);
                    return string.IsNullOrEmpty(alb) ? "Unknown Album" : alb;
                }, StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var album in albums)
            {
                uint albumDur = (uint)album.Sum(t => (long)t.DurationMs);
                gplbNodes.Add((nextId++, 2, trackIdx, 0));
                ginfRecords.Add((album.Key, albumDur));

                foreach (var tr in album)
                {
                    tplbTrackIds.Add((ushort)tr.TrackId);
                    trackIdx++;
                }
            }
        }

        int tplbDataBytes = Math.Max(16, ((tplbTrackIds.Count * 2 + 15) / 16) * 16);
        uint tplbSectionLen = (uint)(16 + tplbDataBytes);
        byte[] treeBytes = new byte[0x30 + DefaultGplbAllocatedLength + tplbSectionLen];
        var tSpan = treeBytes.AsSpan();

        OmgTableFraming.WriteTableHeader(tSpan, "TREE", classCount: 2);
        OmgTableFraming.WriteClassDescriptor(tSpan.Slice(16), "GPLB", 0x30, DefaultGplbAllocatedLength);
        OmgTableFraming.WriteClassDescriptor(tSpan.Slice(32), "TPLB", 0x30 + DefaultGplbAllocatedLength, (int)tplbSectionLen);
        OmgTableFraming.WriteClassHeader(tSpan.Slice(0x30), "GPLB", (ushort)gplbNodes.Count, 8, complement1: (uint)gplbNodes.Count);

        for (int i = 0; i < gplbNodes.Count; i++)
        {
            var node = gplbNodes[i];
            Span<byte> nSpan = tSpan.Slice(0x40 + i * 8, 8);
            BinaryPrimitives.WriteUInt16BigEndian(nSpan.Slice(0, 2), node.Id);
            nSpan[2] = node.Level;
            nSpan[3] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(nSpan.Slice(4, 2), node.FirstTrackIdx);
            BinaryPrimitives.WriteUInt16BigEndian(nSpan.Slice(6, 2), node.Unknown);
        }

        int tplbBase = (int)(0x30 + DefaultGplbAllocatedLength);
        OmgTableFraming.WriteClassHeader(tSpan.Slice(tplbBase), "TPLB", (ushort)tplbTrackIds.Count, 2, complement1: (uint)tplbTrackIds.Count);
        for (int i = 0; i < tplbTrackIds.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(tSpan.Slice(tplbBase + 16 + i * 2, 2), tplbTrackIds[i]);
        }

        uint ginfSectionLen = (uint)(16 + ginfRecords.Count * elemSize2D);
        byte[] ginfBytes = new byte[0x20 + ginfSectionLen];
        var gSpan = ginfBytes.AsSpan();
        OmgTableFraming.WriteTableHeader(gSpan, "GPIF", classCount: 1);
        OmgTableFraming.WriteClassDescriptor(gSpan.Slice(16), "GPFB", 0x20, (int)ginfSectionLen);
        OmgTableFraming.WriteClassHeader(gSpan.Slice(0x20), "GPFB", (ushort)ginfRecords.Count, elemSize2D);

        for (int i = 0; i < ginfRecords.Count; i++)
        {
            Span<byte> el = gSpan.Slice(0x30 + i * elemSize2D, elemSize2D);
            BinaryPrimitives.WriteUInt32BigEndian(el.Slice(8, 4), ginfRecords[i].DurationMs);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 2);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16), "TIT2", ginfRecords[i].Title);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 0x80), "XSOT", ginfRecords[i].Title);
        }

        return (treeBytes, ginfBytes);
    }

    private static (byte[] TreeBytes, byte[] GinfBytes) BuildTreeAndGinf01(
        IReadOnlyList<TrackGroupDto> groups,
        IReadOnlyList<AudioTrackDto> tracks)
    {
        var trackLookup = tracks.ToDictionary(t => t.TrackId);
        var flatTrackIds = new List<int>(tracks.Count);
        var gplbEntries = new List<(ushort GroupId, ushort FirstTrackPos)>(groups.Count);

        for (int i = 0; i < groups.Count; i++)
        {
            ushort gid = (ushort)(i + 1);
            ushort firstPos = (ushort)(flatTrackIds.Count + 1);
            gplbEntries.Add((gid, firstPos));

            foreach (int tid in groups[i].TrackIds)
            {
                if (trackLookup.ContainsKey(tid))
                {
                    flatTrackIds.Add(tid);
                }
            }
        }

        byte[] tree = SerializeTreeTable(gplbEntries, flatTrackIds);

        int ginfLen = OmgTableFraming.ClassHeaderSize + groups.Count * Ginf01ElementSize;
        byte[] ginf = new byte[0x20 + ginfLen];
        var gspan = ginf.AsSpan();
        OmgTableFraming.WriteTableHeader(gspan, "GPIF", 1);
        OmgTableFraming.WriteClassDescriptor(gspan.Slice(16), "GPFB", 0x20, ginfLen);
        OmgTableFraming.WriteClassHeader(gspan.Slice(0x20), "GPFB", groups.Count, Ginf01ElementSize);

        for (int i = 0; i < groups.Count; i++)
        {
            var grp = groups[i];
            uint grpDuration = (uint)grp.TrackIds
                .Where(trackLookup.ContainsKey)
                .Sum(id => (long)trackLookup[id].DurationMs);

            Span<byte> el = gspan.Slice(0x30 + i * Ginf01ElementSize, Ginf01ElementSize);
            BinaryPrimitives.WriteUInt32BigEndian(el.Slice(8, 4), grpDuration);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 6);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 0 * 0x80), "TIT2", grp.Title);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 1 * 0x80), "TPE1", grp.Artist);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 2 * 0x80), "TCON", grp.Genre);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 3 * 0x80), "TSOP", string.Empty);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 4 * 0x80), "PICP", string.Empty);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 5 * 0x80), "PIC0", string.Empty);
        }

        return (tree, ginf);
    }

    private static (byte[] TreeBytes, byte[] GinfBytes) BuildTagTreeAndGinf(
        IReadOnlyList<AudioTrackDto> tracks,
        Func<AudioTrackDto, string> keySelector,
        string fallbackLabel)
    {
        var grouped = tracks
            .GroupBy(t =>
            {
                string key = OmgTableFraming.NormalizeGroupKey(keySelector(t));
                return string.IsNullOrEmpty(key) ? fallbackLabel : key;
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var flatTrackIds = new List<int>(tracks.Count);
        var gplbEntries = new List<(ushort GroupId, ushort FirstTrackPos)>(grouped.Length);
        var ginfEntries = new List<(string Label, uint DurationMs)>(grouped.Length);

        for (int i = 0; i < grouped.Length; i++)
        {
            ushort gid = (ushort)(i + 1);
            ushort firstPos = (ushort)(flatTrackIds.Count + 1);
            gplbEntries.Add((gid, firstPos));

            var orderedMembers = grouped[i]
                .OrderBy(t => t.TrackNumber)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TrackId)
                .ToArray();

            foreach (var member in orderedMembers)
            {
                flatTrackIds.Add(member.TrackId);
            }

            uint dur = (uint)orderedMembers.Sum(m => (long)m.DurationMs);
            ginfEntries.Add((grouped[i].Key, dur));
        }

        byte[] tree = SerializeTreeTable(gplbEntries, flatTrackIds);

        int ginfLen = OmgTableFraming.ClassHeaderSize + ginfEntries.Count * GinfSimpleElementSize;
        byte[] ginf = new byte[0x20 + ginfLen];
        var gspan = ginf.AsSpan();
        OmgTableFraming.WriteTableHeader(gspan, "GPIF", 1);
        OmgTableFraming.WriteClassDescriptor(gspan.Slice(16), "GPFB", 0x20, ginfLen);
        OmgTableFraming.WriteClassHeader(gspan.Slice(0x20), "GPFB", ginfEntries.Count, GinfSimpleElementSize);

        for (int i = 0; i < ginfEntries.Count; i++)
        {
            Span<byte> el = gspan.Slice(0x30 + i * GinfSimpleElementSize, GinfSimpleElementSize);
            BinaryPrimitives.WriteUInt32BigEndian(el.Slice(8, 4), ginfEntries[i].DurationMs);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);
            OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16), "TIT2", ginfEntries[i].Label);
        }

        return (tree, ginf);
    }

    private static byte[] SerializeTreeTable(
        IReadOnlyList<(ushort GroupId, ushort FirstTrackPos)> gplbEntries,
        IReadOnlyList<int> flatTrackIds)
    {
        int tplbLen = OmgTableFraming.Align16(OmgTableFraming.ClassHeaderSize + flatTrackIds.Count * 2);
        byte[] tree = new byte[TreeTplbStartAddress + tplbLen];
        var span = tree.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "TREE", 2);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "GPLB", 0x30, TreeGplbAllocatedLength);
        OmgTableFraming.WriteClassDescriptor(span.Slice(32), "TPLB", TreeTplbStartAddress, tplbLen);

        OmgTableFraming.WriteClassHeader(
            span.Slice(0x30),
            "GPLB",
            gplbEntries.Count,
            8,
            complement1: (uint)gplbEntries.Count);

        for (int i = 0; i < gplbEntries.Count; i++)
        {
            Span<byte> el = span.Slice(0x40 + i * 8, 8);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(0, 2), gplbEntries[i].GroupId);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(2, 2), 0x0100);
            BinaryPrimitives.WriteUInt16BigEndian(el.Slice(4, 2), gplbEntries[i].FirstTrackPos);
        }

        OmgTableFraming.WriteClassHeader(
            span.Slice(TreeTplbStartAddress),
            "TPLB",
            flatTrackIds.Count,
            2,
            complement1: (uint)flatTrackIds.Count);

        int tplbData = TreeTplbStartAddress + OmgTableFraming.ClassHeaderSize;
        for (int i = 0; i < flatTrackIds.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(tplbData + i * 2, 2), (ushort)flatTrackIds[i]);
        }

        return tree;
    }

    private static byte[] BuildEmptyTree22()
    {
        byte[] data = new byte[0x50]; // 80 bytes
        var span = data.AsSpan();
        OmgTableFraming.WriteTableHeader(span, "TREE", 2);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "GPLB", 0x30, 0x10);
        OmgTableFraming.WriteClassDescriptor(span.Slice(32), "TPLB", 0x40, 0x10);
        OmgTableFraming.WriteClassHeader(span.Slice(0x30), "GPLB", 0, 8);
        OmgTableFraming.WriteClassHeader(span.Slice(0x40), "TPLB", 0, 2);
        return data;
    }


    private static byte[] BuildEmptyGinf22()
    {
        byte[] data = new byte[0x30]; // 48 bytes
        var span = data.AsSpan();
        OmgTableFraming.WriteTableHeader(span, "GPIF", 1);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "GPFB", 0x20, 0x10);
        OmgTableFraming.WriteClassHeader(span.Slice(0x20), "GPFB", 0, Ginf01ElementSize);
        return data;
    }


    private static byte[] Build04Cntinf(IReadOnlyList<AudioTrackDto> activeTracks, int maxTrackId)
    {
        var lookup = activeTracks.ToDictionary(t => t.TrackId);
        int classLen = OmgTableFraming.ClassHeaderSize + maxTrackId * CntinfElementSize;
        byte[] data = new byte[0x20 + classLen];
        var span = data.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "CNIF", 1);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "CNFB", 0x20, classLen);
        OmgTableFraming.WriteClassHeader(span.Slice(0x20), "CNFB", maxTrackId, CntinfElementSize);

        for (int trackId = 1; trackId <= maxTrackId; trackId++)
        {
            Span<byte> el = span.Slice(0x30 + (trackId - 1) * CntinfElementSize, CntinfElementSize);
            if (lookup.TryGetValue(trackId, out var track))
            {
                BinaryPrimitives.WriteUInt16BigEndian(el.Slice(2, 2), (ushort)track.EncryptionMode);
                BinaryPrimitives.WriteUInt32BigEndian(el.Slice(4, 4), track.FilePropertiesBigEndian);
                BinaryPrimitives.WriteUInt32BigEndian(el.Slice(8, 4), track.DurationMs);
                BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 5);
                BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);

                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 0 * 0x80), "TIT2", track.Title);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 1 * 0x80), "TPE1", track.Artist);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 2 * 0x80), "TALB", track.Album);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 3 * 0x80), "TCON", track.Genre);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 4 * 0x80), "TSOP", track.SortTitle);
            }
            else
            {
                // Dense empty tombstone slot invariant.
                BinaryPrimitives.WriteUInt16BigEndian(el.Slice(12, 2), 5);
                BinaryPrimitives.WriteUInt16BigEndian(el.Slice(14, 2), OmgTableFraming.SubElementSize);

                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 0 * 0x80), "TIT2", string.Empty);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 1 * 0x80), "TPE1", string.Empty);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 2 * 0x80), "TALB", string.Empty);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 3 * 0x80), "TCON", string.Empty);
                OmgTableFraming.WriteUtf16BeSubElement(el.Slice(16 + 4 * 0x80), "TSOP", string.Empty);
            }
        }

        return data;
    }


    private static byte[] Build05Cidlst(IReadOnlyList<AudioTrackDto> activeTracks, int maxTrackId)
    {
        var lookup = activeTracks.ToDictionary(t => t.TrackId);
        int classLen = OmgTableFraming.ClassHeaderSize + maxTrackId * CidlElementSize;
        byte[] data = new byte[0x20 + classLen];
        var span = data.AsSpan();

        OmgTableFraming.WriteTableHeader(span, "CIDL", 1);
        OmgTableFraming.WriteClassDescriptor(span.Slice(16), "CILB", 0x20, classLen);
        OmgTableFraming.WriteClassHeader(span.Slice(0x20), "CILB", maxTrackId, CidlElementSize);

        for (int trackId = 1; trackId <= maxTrackId; trackId++)
        {
            if (lookup.TryGetValue(trackId, out var track) &&
                track.LsiKeyring48 is { Length: CidlElementSize })
            {
                track.LsiKeyring48.CopyTo(span.Slice(0x30 + (trackId - 1) * CidlElementSize, CidlElementSize));
            }
        }

        return data;
    }

}
