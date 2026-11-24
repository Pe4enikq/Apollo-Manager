using System.Buffers.Binary;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;
using Xunit;

namespace Walkman.M0.Tests;

public sealed class MilestoneM48Batch5FixesTests
{
    [Fact]
    public async Task AudioPlaybackService_RapidSeek_StaysPlayingAndNeverStucksIsSeeking()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m48_seek_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string wavPath = Path.Combine(tempDir, "seek_test.wav");
            WritePcmWavFile(wavPath, 44100, 8);

            using var player = new AudioPlaybackService();
            var trk = MakeTrack("seek1", "Seek Test", wavPath, durationSec: 8);
            player.PlayOrToggle(trk);

            for (int i = 0; i < 20; i++)
            {
                player.Seek((i % 6) + 0.5);
            }

            await Task.Delay(250);
            Assert.True(player.IsPlaying);
            Assert.False(player.IsSessionSeeking);
            player.Stop();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FlacWaveStream_DecodesAndSeeks_WithLargePaddingBlock()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m48_flac_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string flacPath = Path.Combine(tempDir, "padded.flac");
            WriteSyntheticFlacWithPadding(flacPath, sampleRate: 44100, channels: 2, blockSize: 1024, frameCount: 16, paddingBytes: 256 * 1024);

            using var flac = new FlacWaveStream(flacPath);
            Assert.Equal(44100, flac.WaveFormat.SampleRate);
            Assert.Equal(2, flac.WaveFormat.Channels);
            Assert.Equal(16, flac.WaveFormat.BitsPerSample);

            byte[] buf = new byte[4096];
            int read = flac.Read(buf, 0, buf.Length);
            Assert.Equal(4096, read);

            flac.CurrentTime = TimeSpan.FromMilliseconds(150);
            int readAfterSeek = flac.Read(buf, 0, buf.Length);
            Assert.True(readAfterSeek > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FlacWaveStream_MonstercatRealFile_DecodesAndSeeksIfPresent()
    {
        const string realFlac = @"Y:\CASSETTE RECORDS\Monstercat\[2020] Uncaged - Vol. 9\01. Tokyo Machine - JOURNEY.flac";
        if (!File.Exists(realFlac))
        {
            return;
        }

        using var flac = new FlacWaveStream(realFlac);
        Assert.Equal(44100, flac.WaveFormat.SampleRate);
        Assert.Equal(2, flac.WaveFormat.Channels);

        byte[] buf = new byte[16384];
        int read = flac.Read(buf, 0, buf.Length);
        Assert.Equal(16384, read);

        flac.CurrentTime = TimeSpan.FromSeconds(30);
        int readAt30 = flac.Read(buf, 0, buf.Length);
        Assert.Equal(16384, readAt30);
        Assert.Contains(buf, b => b != 0);

        using var player = new AudioPlaybackService();
        var trk = MakeTrack("realflac", "JOURNEY", realFlac, durationSec: 180, format: "FLAC");
        player.PlayOrToggle(trk);
        Assert.True(player.IsPlaying);
        player.Stop();
    }

    [Fact]
    public async Task GetActivePlaybackQueue_WhenAllFoldersCollapsed_PreservesFolderGroupedSortedOrder()
    {
        await using var backend = new WalkmanBackendService();
        using var player = new AudioPlaybackService();
        using var vm = new MainWindowViewModel(backend, player, autostart: true, suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;

        vm.OpenModule(ModuleIds.Library);
        vm.FilteredLibraryTracks.Clear();
        vm.FilteredLibraryTracks.Add(MakeTrack("b2", "Beta 2", null, folder: "Folder B"));
        vm.FilteredLibraryTracks.Add(MakeTrack("a2", "Alpha 2", null, folder: "Folder A"));
        vm.FilteredLibraryTracks.Add(MakeTrack("a1", "Alpha 1", null, folder: "Folder A"));
        vm.FilteredLibraryTracks.Add(MakeTrack("b1", "Beta 1", null, folder: "Folder B"));

        vm.LibrarySortField = TrackSortField.Title;
        vm.LibrarySortDesc = false;
        vm.RebuildV3ListEntries();
        var expandedOrder = vm.GetActivePlaybackQueue().Select(t => t.Id).ToArray();

        vm.CollapseAllFoldersCommand.Execute("lib");
        Assert.All(vm.LibraryEntries.Where(e => e.IsHeader), h => Assert.False(h.IsExpanded));

        var collapsedOrder = vm.GetActivePlaybackQueue().Select(t => t.Id).ToArray();
        Assert.Equal(expandedOrder, collapsedOrder);
        Assert.Equal(["a1", "a2", "b1", "b2"], collapsedOrder);
    }

    [Fact]
    public async Task DeleteTracksAsync_AndDeleteCollection_ReportRealProgressAndDeleteTracks()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_m48_del_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "OMGAUDIO", "10F00"));
        try
        {
            await WriteTestDvIdDatAsync(tempRoot);
            await using var backend = new WalkmanBackendService();
            var (dev, _, err) = await backend.InspectFixtureVolumeAsync(tempRoot);
            Assert.Null(err);
            var identity = dev!.LiveIdentity!;

            var orchestrator = CreateTestOrchestrator();
            await orchestrator.HydrateForDeviceAsync(identity);
            var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();
            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var fm = new FileManagerCoordinator(orchestrator);

            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.OMA", new byte[256]);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000002.OMA", new byte[256]);

            var dto1 = new AudioTrackDto(1, "Track 1", "Artist", "Album", "Electronic", null, 1, 2026, 4000, 8,
                AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03800000u, null, GroupName: "ColA");
            var dto2 = new AudioTrackDto(2, "Track 2", "Artist", "Album", "Electronic", null, 2, 2026, 4000, 8,
                AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03800000u, null, GroupName: "ColA");
            await db.CommitTransactionAsync(identity, new OmgDatabaseTransaction([dto1, dto2], [], []));

            var events = new List<TransferProgressEvent>();
            var progress = new SyncProgress(events.Add);
            await fm.DeleteCollectionAsync(identity, "ColA", deleteTracks: true, progress: progress);

            Assert.Contains(events, e => e.PercentComplete >= 5 && e.PercentComplete < 100);
            Assert.Equal(100.0, events[^1].PercentComplete);
            Assert.False(await transport.ExistsAsync(identity, "OMGAUDIO/10F00/10000001.OMA"));
            Assert.False(await transport.ExistsAsync(identity, "OMGAUDIO/10F00/10000002.OMA"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task OmgV1DatabaseEngine_ExcludesDummyStubs_AndOrdersGroupsExplicitly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_m48_omg_{Guid.NewGuid():N}");
        string slotPoolDir = Path.Combine(Path.GetTempPath(), $"apollo_m48_pool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "OMGAUDIO", "10F00"));
        try
        {
            await WriteTestDvIdDatAsync(tempRoot);
            await using var backend = new WalkmanBackendService();
            var (dev, _, err) = await backend.InspectFixtureVolumeAsync(tempRoot);
            Assert.Null(err);
            var identity = dev!.LiveIdentity!;

            var orchestrator = CreateTestOrchestrator();
            await orchestrator.HydrateForDeviceAsync(identity);
            var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var fm = new FileManagerCoordinator(orchestrator, slotPoolDirectory: slotPoolDir);

            byte[] syntheticEkb = TestEkbFixtureHelper.BuildSyntheticTestEkb00010021();
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/00010021.DAT", syntheticEkb);
            var wrapped1 = crypto.WrapAtracToOma(
                new byte[64], AudioCodecId.Atrac3Plus, 0x0028FF, 4000, 8, 1, null,
                OmaEncryptionMode.MagicGateLsi_0x0001, new Ea3TagMetadata("Real AT3+", "Artist", "AlbumA", "Electronic", 2026, 1, 4000),
                ekb00010021Bytes: syntheticEkb);
            var wrapped2 = crypto.WrapAtracToOma(
                new byte[64], AudioCodecId.Atrac3Plus, 0x0028FF, 4000, 8, 2, null,
                OmaEncryptionMode.MagicGateLsi_0x0001, new Ea3TagMetadata("Stub", "Artist", "AlbumB", "Electronic", 2026, 1, 0),
                ekb00010021Bytes: syntheticEkb);

            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.OMA", wrapped1.OmaBytes);
            var tpl2 = crypto.ExtractSignedSlotTemplate(wrapped2.OmaBytes, 2, syntheticEkb);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000002.OMA", crypto.BuildSignedSlotDummyStub(tpl2!));

            byte[] maclist = new byte[32768];
            wrapped1.MaclistEntry8!.CopyTo(maclist, 0);
            wrapped2.MaclistEntry8!.CopyTo(maclist, 8);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/MACLIST0.DAT", maclist);

            var dto1 = new AudioTrackDto(1, "Real AT3+", "Artist", "AlbumA", "Electronic", null, 1, 2026, 4000, 8,
                AudioCodecId.Atrac3Plus, 352, OmaEncryptionMode.MagicGateLsi_0x0001, 0x010028FFu, wrapped1.LsiKeyring48, GroupName: "GroupOne", MaclistEntry8: wrapped1.MaclistEntry8);
            var dto2 = new AudioTrackDto(2, "Stub", "Artist", "AlbumB", "Electronic", null, 2, 2026, 0, 0,
                AudioCodecId.Atrac3Plus, 352, OmaEncryptionMode.MagicGateLsi_0x0001, 0x010028FFu, wrapped2.LsiKeyring48, GroupName: "GroupTwo", MaclistEntry8: wrapped2.MaclistEntry8);
            var dto3 = new AudioTrackDto(3, "MP3 Track", "Artist", "AlbumC", "Electronic", null, 1, 2026, 4000, 8,
                AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03800000u, null, GroupName: "GroupTwo");

            await db.CommitTransactionAsync(identity, new OmgDatabaseTransaction([dto1, dto2, dto3], [], [], PreserveExistingMaclist0: true));

            var snap = await db.ReadDatabaseAsync(identity);
            Assert.DoesNotContain(snap.Tracks, t => t.TrackId == 2);
            Assert.Equal(2, snap.Tracks.Count);

            await fm.UpdateDeviceTableAsync(identity, new DeviceTableUpdateRequest(
                Array.Empty<TrackGroupDto>(),
                OrderedGroupNames: ["GroupTwo", "GroupOne"]));

            var reorderedSnap = await db.ReadDatabaseAsync(identity);
            Assert.Equal(["GroupTwo", "GroupOne"], reorderedSnap.Groups.Select(g => g.Title).ToArray());
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            try { Directory.Delete(slotPoolDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Cod20_SearchSplit_PunctuationFreeSearch_AndAlbumTrackNumberSort()
    {
        await using var backend = new WalkmanBackendService();
        using var player = new AudioPlaybackService();
        using var vm = new MainWindowViewModel(backend, player, autostart: true, suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;

        Assert.True(SearchTextMatcher.Matches("dont let go", "Don't Let Go"));
        Assert.True(SearchTextMatcher.Matches("2020 uncaged vol 9", "[2020] Uncaged - Vol. 9"));
        Assert.True(SearchTextMatcher.Matches("feint vagrant", "Vagrant", "Feint"));

        var sorted = MainWindowViewModel.SortTracks(
            [
                MakeTrack("t3", "Zeta", null, album: "Album B", trackNumber: 1),
                MakeTrack("t2", "Alpha", null, album: "Album A", trackNumber: 2),
                MakeTrack("t1", "Gamma", null, album: "Album A", trackNumber: 1)
            ],
            TrackSortField.AlbumTrackNumber,
            descending: false).Select(t => t.Id).ToArray();
        Assert.Equal(["t1", "t2", "t3"], sorted);

        foreach (var t in vm.FileManagerSourceTracks)
        {
            t.IsSelected = false;
        }
        Assert.True(vm.FileManagerSourceTracks.Count >= 2);
        var s1 = vm.FileManagerSourceTracks[0];
        var s2 = vm.FileManagerSourceTracks[1];
        s1.IsSelected = true;
        s2.IsSelected = true;

        double beforeGb = vm.DestSelectedTransferGb;
        Assert.Equal(2, vm.TransferSelectedCount);

        vm.FmSourceSearchQuery = s1.Title;
        Assert.Equal(2, vm.TransferSelectedCount);
        Assert.Equal(beforeGb, vm.DestSelectedTransferGb, 4);
    }

    private sealed class SyncProgress(Action<TransferProgressEvent> handler) : IProgress<TransferProgressEvent>
    {
        public void Report(TransferProgressEvent value) => handler(value);
    }

    private static AudioTrackItem MakeTrack(
        string id,
        string title,
        string? path,
        int durationSec = 180,
        double sizeMb = 4.0,
        string format = "WAV",
        string folder = "Test",
        string album = "Album",
        int trackNumber = 1) =>
        new()
        {
            Id = id,
            Title = title,
            Artist = "Artist",
            Album = album,
            TrackNumber = trackNumber,
            Format = format,
            Bitrate = "320 kbps",
            DurationSec = durationSec,
            SizeMb = sizeMb,
            Folder = folder,
            SourceFilePath = path
        };

    private static ModuleOrchestrator CreateTestOrchestrator()
    {
        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        string baseDir = AppContext.BaseDirectory;
        return new ModuleOrchestrator(bus, mediator, moduleId =>
        {
            string dllName = $"Walkman.{moduleId}.dll";
            string direct = Path.Combine(baseDir, dllName);
            if (File.Exists(direct)) return direct;
            string config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            string root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            string l2 = Path.Combine(root, "src", "L2.Engines", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            if (File.Exists(l2)) return l2;
            string l3 = Path.Combine(root, "src", "L3.Presentation", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            return File.Exists(l3) ? l3 : direct;
        });
    }

    private static async Task WriteTestDvIdDatAsync(string volumeRootPath)
    {
        string mp3FmDir = Path.Combine(volumeRootPath, "MP3FM");
        Directory.CreateDirectory(mp3FmDir);
        byte[] dvid = new byte[16];
        dvid[6] = 0x01;
        dvid[7] = 0x28;
        dvid[12] = 0x12; dvid[13] = 0x34; dvid[14] = 0x56; dvid[15] = 0x78;
        await File.WriteAllBytesAsync(Path.Combine(mp3FmDir, "DvID.DAT"), dvid);
    }

    private static void WritePcmWavFile(string path, int sampleRate, int durationSeconds)
    {
        int dataBytes = sampleRate * 2 * durationSeconds;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        bw.Write(new byte[dataBytes]);
    }

    private static void WriteSyntheticFlacWithPadding(string path, int sampleRate, int channels, int blockSize, int frameCount, int paddingBytes)
    {
        long totalSamples = (long)blockSize * frameCount;
        using var fs = File.Create(path);
        fs.Write("fLaC"u8);

        byte[] streamInfo = new byte[38];
        streamInfo[0] = 0x00;
        streamInfo[3] = 0x22;
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(4, 2), (ushort)blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(6, 2), (ushort)blockSize);
        ulong packed = ((ulong)(sampleRate & 0xFFFFF) << 44) | ((ulong)((channels - 1) & 0x07) << 41) | ((ulong)15 << 36) | ((ulong)totalSamples & 0xFFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(14, 8), packed);
        fs.Write(streamInfo);

        byte[] padHdr = [0x81, (byte)((paddingBytes >> 16) & 0xFF), (byte)((paddingBytes >> 8) & 0xFF), (byte)(paddingBytes & 0xFF)];
        fs.Write(padHdr);
        fs.Write(new byte[paddingBytes]);

        for (int f = 0; f < frameCount; f++)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0xFF);
            ms.WriteByte(0xF8);
            ms.WriteByte(0x69); // bs=1024 (6? no, use 7 = 16-bit bs-1), sr=44100 (9) -> 0x79
            ms.Position = 2;
            ms.WriteByte(0x79);
            ms.WriteByte((byte)(((channels - 1) << 4) | (0x04 << 1)));
            ms.WriteByte((byte)f); // UTF-8 frame number (<128)
            ms.WriteByte((byte)((blockSize - 1) >> 8));
            ms.WriteByte((byte)((blockSize - 1) & 0xFF));

            byte[] hdr = ms.ToArray();
            byte crc8 = 0;
            foreach (byte b in hdr)
            {
                crc8 ^= b;
                for (int bit = 0; bit < 8; bit++) crc8 = (byte)((crc8 & 0x80) != 0 ? ((crc8 << 1) ^ 0x07) : (crc8 << 1));
            }
            ms.WriteByte(crc8);

            for (int ch = 0; ch < channels; ch++)
            {
                ms.WriteByte(0x00); // CONSTANT
                ms.WriteByte(0x01);
                ms.WriteByte(0x00);
            }

            ms.WriteByte(0x00);
            ms.WriteByte(0x00);
            ms.Position = 0;
            ms.CopyTo(fs);
        }
    }
}
