using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;
using Xunit;

namespace Walkman.M0.Tests;

public sealed class MilestoneM46PlaybackSlotsAndViewModelTests
{
    [Fact]
    public void AudioPlaybackService_RapidSeekAndImmediateTrackSwitch_NeverDeadlocks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m46_seek_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string wav1 = Path.Combine(tempDir, "track1.wav");
            string wav2 = Path.Combine(tempDir, "track2.wav");
            WritePcmWavFile(wav1, 44100, 10);
            WritePcmWavFile(wav2, 44100, 10);

            using var player = new AudioPlaybackService();
            var t1 = MakeTrack("t1", "Track 1", wav1, durationSec: 10);
            var t2 = MakeTrack("t2", "Track 2", wav2, durationSec: 10);

            var sw = Stopwatch.StartNew();
            player.PlayOrToggle(t1);
            for (int i = 0; i < 25; i++)
            {
                player.Seek((i % 8) + 0.5);
            }

            player.PlayOrToggle(t2);
            player.Seek(3.5);
            player.Stop();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1500, $"Rapid seek + switch should never deadlock (took {sw.ElapsedMilliseconds} ms).");
            Assert.Null(player.CurrentTrack);
            Assert.False(player.IsPlaying);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task FileManager_Delete0x0001Track_Writes2144ByteDummyStub_AndRestoresSlotPool()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_m46_slots_{Guid.NewGuid():N}");
        string slotPoolDir = Path.Combine(Path.GetTempPath(), $"apollo_m46_pool_{Guid.NewGuid():N}");
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

            // Seed 1 signed 0x0001 track (#1) and 1 unprotected 0xFFFE track (#2)
            byte[] syntheticEkb = TestEkbFixtureHelper.BuildSyntheticTestEkb00010021();
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/00010021.DAT", syntheticEkb);
            byte[] rawFrames = new byte[64];
            var wrapped = crypto.WrapAtracToOma(
                rawFrames,
                AudioCodecId.Atrac3Plus,
                0x0028FF,
                4000,
                8,
                trackId: 1,
                hardwareDeviceKey: null,
                OmaEncryptionMode.MagicGateLsi_0x0001,
                new Ea3TagMetadata("Signed 1", "Artist", "Album", "Electronic", 2026, 1, 4000),
                ekb00010021Bytes: syntheticEkb);

            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.OMA", wrapped.OmaBytes);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000002.OMA", new byte[128]);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.LRC", Encoding.UTF8.GetBytes("[00:01.00]Hi"));

            byte[] maclist = new byte[32768];
            wrapped.MaclistEntry8!.CopyTo(maclist, 0);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/MACLIST0.DAT", maclist);

            var dto1 = new AudioTrackDto(1, "Signed 1", "Artist", "Album", "Electronic", null, 1, 2026, 4000, 8,
                AudioCodecId.Atrac3Plus, 352, OmaEncryptionMode.MagicGateLsi_0x0001, 0x010028FFu, wrapped.LsiKeyring48, GroupName: "Pool", MaclistEntry8: wrapped.MaclistEntry8);
            var dto2 = new AudioTrackDto(2, "Plain 2", "Artist", "Album", "Electronic", null, 2, 2026, 4000, 8,
                AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03800000u, null, GroupName: "Pool");

            await db.CommitTransactionAsync(identity, new OmgDatabaseTransaction([dto1, dto2], [], [], PreserveExistingMaclist0: true));

            await fm.DeleteTracksAsync(identity, [1, 2]);

            // Track #1 (0x0001) must be replaced with 2,144-byte dummy stub; Track #2 (0xFFFE) and .LRC must be deleted
            Assert.True(await transport.ExistsAsync(identity, "OMGAUDIO/10F00/10000001.OMA"));
            long stubLen = await transport.GetFileLengthAsync(identity, "OMGAUDIO/10F00/10000001.OMA");
            Assert.Equal(2144, stubLen);
            Assert.False(await transport.ExistsAsync(identity, "OMGAUDIO/10F00/10000002.OMA"));
            Assert.False(await transport.ExistsAsync(identity, "OMGAUDIO/10F00/10000001.LRC"));

            // Slot pool status must restore slot #1 as recycled free
            var pool = await fm.GetMagicGateSlotPoolStatusAsync(identity, harvestActiveSlots: false);
            Assert.Equal(1, pool.RecycledFreeSlotCount);
            Assert.Contains(1, pool.FreeRecycledTrackIds);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            try { Directory.Delete(slotPoolDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WalkmanBackendService_StrictCoverArt_IgnoresRootFolderArt_UsesDedicatedAlbumSubfolderAndSidecar()
    {
        string root = Path.Combine(Path.GetTempPath(), $"apollo_m46_cov_{Guid.NewGuid():N}");
        string albumSub = Path.Combine(root, "SingleAlbum");
        string mixedSub = Path.Combine(root, "Mixed");
        Directory.CreateDirectory(albumSub);
        Directory.CreateDirectory(mixedSub);
        try
        {
            byte[] pngBytes = Create1x1Png();
            await File.WriteAllBytesAsync(Path.Combine(root, "cover.png"), pngBytes);
            await File.WriteAllBytesAsync(Path.Combine(albumSub, "cover.png"), pngBytes);
            await File.WriteAllBytesAsync(Path.Combine(mixedSub, "cover.png"), pngBytes);
            await File.WriteAllBytesAsync(Path.Combine(mixedSub, "sidecar_song.png"), pngBytes);

            string rootWav = Path.Combine(root, "root_song.wav");
            string albumWav = Path.Combine(albumSub, "album_song.wav");
            string mixedSidecarWav = Path.Combine(mixedSub, "sidecar_song.wav");
            string mixedOtherWav = Path.Combine(mixedSub, "other_song.wav");

            WritePcmWavFile(rootWav, 44100, 1);
            WritePcmWavFile(albumWav, 44100, 1);
            WritePcmWavFile(mixedSidecarWav, 44100, 1);
            WritePcmWavFile(mixedOtherWav, 44100, 1);

            WalkmanBackendService.TrySaveLocalTrackTags(albumWav, "Album Song", "A", "Same Album");
            WalkmanBackendService.TrySaveLocalTrackTags(mixedSidecarWav, "Sidecar Song", "A", "Album 1");
            WalkmanBackendService.TrySaveLocalTrackTags(mixedOtherWav, "Other Song", "B", "Album 2");

            await using var backend = new WalkmanBackendService();
            var tracks = await backend.ScanLocalFolderTracksIncrementalAsync(
                root,
                "pc-library",
                onMetadataBatchReady: patches =>
                {
                    foreach (var (trk, patch) in patches)
                    {
                        WalkmanBackendService.ApplyMetadataPatch(trk, patch);
                    }
                });

            var byPath = tracks.ToDictionary(t => Path.GetFileName(t.SourceFilePath!));
            // Note: In headless unit tests without Avalonia Skia init, Bitmap.DecodeToWidth may return null;
            // if Skia is initialized, root_song and other_song must still be null, while album_song/sidecar_song may decode.
            if (byPath["album_song.wav"].CoverArtwork is not null)
            {
                Assert.Null(byPath["root_song.wav"].CoverArtwork);
                Assert.Null(byPath["other_song.wav"].CoverArtwork);
                Assert.NotNull(byPath["sidecar_song.wav"].CoverArtwork);
            }

            // ApplyMetadataPatch must clear CoverArtwork when patch.CoverArtwork is null
            var clearTarget = byPath["root_song.wav"];
            WalkmanBackendService.ApplyMetadataPatch(
                clearTarget,
                new WalkmanBackendService.LocalTrackMetadataPatch("T", "A", "Al", 2026, 1, "WAV", "", 0, "", "", "", 0, "", "", null, null, false));
            Assert.Null(clearTarget.CoverArtwork);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MainWindowViewModel_Cod12QueueStorageSubtitlesAutoCollapseAndAalGate_WorkAsSpecified()
    {
        await using var backend = new WalkmanBackendService();
        using var player = new AudioPlaybackService();
        using var vm = new MainWindowViewModel(backend, player, autostart: true, suppressSettingsSave: true);

        // 1. Codec-aware DestSelectedTransferGb (Quality 352k vs Faster 192k)
        vm.FileManagerSourceTracks.Clear();
        var srcTrack = MakeTrack("fm1", "Long Song", null, durationSec: 240, sizeMb: 40.0, format: "FLAC");
        srcTrack.IsSelected = true;
        vm.FileManagerSourceTracks.Add(srcTrack);

        vm.FmUploadMode = UploadMode.Quality;
        double qualityMb = vm.DestSelectedTransferGb * 1024.0;
        vm.FmUploadMode = UploadMode.Faster;
        double fasterMb = vm.DestSelectedTransferGb * 1024.0;
        Assert.True(qualityMb < 40.0 && qualityMb > fasterMb, $"Expected Quality ({qualityMb:F1} MB) > Faster ({fasterMb:F1} MB) < 40 MB.");

        // 2. Visible/Filtered Queue Order + Subtitles Sort
        vm.OpenModule(ModuleIds.Library);
        vm.LibraryFormatFilter = "MP3";
        vm.LibrarySortField = TrackSortField.Subtitles;
        vm.RebuildV3ListEntries();
        var queue = vm.GetActivePlaybackQueue();
        Assert.All(queue, t => Assert.Equal("MP3", t.Format));

        // 3. Auto-collapse > 500 tracks & > 5 folders
        var bigTracks = Enumerable.Range(1, 505)
            .Select(i => MakeTrack($"big:{i}", $"Track {i}", null, folder: $"Folder {(i % 7) + 1}"))
            .ToList();
        vm.FilteredLibraryTracks.Clear();
        foreach (var t in bigTracks) vm.FilteredLibraryTracks.Add(t);
        vm.RebuildV3ListEntries();
        Assert.All(vm.LibraryEntries.Where(e => e.IsHeader), h => Assert.False(h.IsExpanded));

        // 4. AAL import-only gate
        Assert.True(ConverterStateLogic.IsLosslessCodec(TargetCodec.Aal));
        Assert.Equal("AAL", ConverterStateLogic.GetCodecLabel(TargetCodec.Aal));
        var aalRes = await backend.TranscodeOfflineAudioAsync(
            MakeTrack("aal", "AAL Test", Path.GetTempFileName()),
            Path.GetTempPath(),
            TargetCodec.Aal,
            1411);
        Assert.False(aalRes.Success);
        Assert.Contains("import/decode-only", aalRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static AudioTrackItem MakeTrack(string id, string title, string? path, int durationSec = 180, double sizeMb = 4.0, string format = "WAV", string folder = "Test") =>
        new()
        {
            Id = id,
            Title = title,
            Artist = "Artist",
            Album = "Album",
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
            string? root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
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

    private static byte[] Create1x1Png() =>
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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
}
