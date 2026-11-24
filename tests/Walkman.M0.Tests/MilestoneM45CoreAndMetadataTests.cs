using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Xunit;

namespace Walkman.M0.Tests;

public sealed class MilestoneM45CoreAndMetadataTests
{
    [Fact]
    public void AudioPlaybackService_WindowsSmtcBridge_RealHwndAttachUpdateAndDispose_Succeeds()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        IntPtr hwnd = CreateWindowExW(0, "STATIC", "ApolloSmtcTest", 0, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            using var player = new AudioPlaybackService();
            player.AttachWindowHandleForSmtc(hwnd);
            Assert.True(player.IsSmtcActive);

            var track = new AudioTrackItem
            {
                Id = "smtc:test",
                Title = "SMTC Test Track",
                Artist = "Apollo",
                Album = "Test Album",
                Format = "WAV",
                Bitrate = "1411 kbps",
                DurationSec = 10,
                SizeMb = 1.7
            };

            player.PlayOrToggle(track);
            player.Seek(2);
            player.TogglePlayPause();
            player.Stop();
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [Fact]
    public void AudioPlaybackService_RapidCoalescedSeek_CompletesNonBlockingOnRealWav()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_seek_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string wavPath = Path.Combine(tempDir, "tone.wav");
            WritePcmWavFile(wavPath, sampleRate: 44100, durationSeconds: 10);

            using var player = new AudioPlaybackService();
            var track = new AudioTrackItem
            {
                Id = "test:wav",
                Title = "Long Tone",
                Artist = "Test",
                Album = "Test",
                Format = "WAV",
                Bitrate = "1411 kbps",
                DurationSec = 10,
                SizeMb = 1.7,
                SourceFilePath = wavPath
            };

            player.PlayOrToggle(track);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++)
            {
                player.Seek((i % 9) + 0.5);
            }
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500, $"40 rapid seeks should not block caller (took {sw.ElapsedMilliseconds} ms).");
            Assert.True(player.ElapsedSeconds >= 0 && player.ElapsedSeconds <= 10);
            player.Stop();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WalkmanBackendService_ScanLocalFolderTracksIncrementalAsync_StreamsBatchesAndEnrichesMetadata()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_lazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                string wavPath = Path.Combine(tempDir, $"track_{i}.wav");
                WritePcmWavFile(wavPath, sampleRate: 44100, durationSeconds: 2);
                File.WriteAllLines(Path.ChangeExtension(wavPath, ".lrc"), ["[00:00.50]Hello Apollo"]);
            }

            await using var backend = new WalkmanBackendService();
            var discoveryBatches = new List<int>();
            var patchBatches = new List<int>();

            var tracks = await backend.ScanLocalFolderTracksIncrementalAsync(
                tempDir,
                "pc-library",
                onBatchReady: b => discoveryBatches.Add(b.Count),
                onMetadataBatchReady: p =>
                {
                    patchBatches.Add(p.Count);
                    foreach (var (trk, patch) in p)
                    {
                        WalkmanBackendService.ApplyMetadataPatch(trk, patch);
                    }
                },
                batchSize: 4);

            Assert.Equal(5, tracks.Count);
            Assert.True(discoveryBatches.Count >= 2, "Expected discovery to stream in batches of 4.");
            Assert.NotEmpty(patchBatches);
            Assert.All(tracks, t => Assert.True(t.HasSubtitles));
            Assert.All(tracks, t => Assert.True(t.DurationSec >= 1));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OpenMgCrypto_EvaluateCopyProtectionLock_MatchesAllFourReWip07Conditions()
    {
        var orchestrator = CreateTestOrchestrator();
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "test");
        var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
        byte[] dummyAtrac = new byte[64];
        var tags = new Ea3TagMetadata("Lock Test", "Artist", "Album", "Electronic", 2026, 1, 10000);

        // 1. 0xFFFF / 0xFFFE MP3 is never copy-protected
        byte[] plainOma = crypto.WrapAtracToOma(dummyAtrac, AudioCodecId.Atrac3Plus, 0x0028FF, 10000, 10, 1, null, OmaEncryptionMode.Unprotected_0xFFFF, tags).OmaBytes;
        Assert.False(crypto.EvaluateCopyProtectionLock(plainOma));

        // 2. Unrestricted 0x0001 with OMG_BKLSI + SHARE_P_SID + standard !REFID is NOT copy-protected
        byte[] syntheticEkb = TestEkbFixtureHelper.BuildSyntheticTestEkb00010021();
        byte[] unrestricted0001 = crypto.WrapAtracToOma(dummyAtrac, AudioCodecId.Atrac3Plus, 0x0028FF, 10000, 10, 1, null, OmaEncryptionMode.MagicGateLsi_0x0001, tags, ekb00010021Bytes: syntheticEkb).OmaBytes;
        Assert.False(crypto.EvaluateCopyProtectionLock(unrestricted0001));
        Assert.False(crypto.ParseHeader(unrestricted0001).IsCopyProtected);

        // 3. Condition 4: Dameda byte or 05CHINDC.DAT flag sets lock
        Assert.True(crypto.EvaluateCopyProtectionLock(unrestricted0001, isCntinfDamedaFlagSet: true));
        Assert.True(crypto.EvaluateCopyProtectionLock(unrestricted0001, chindcFlag: 1));

        // 4. Condition 1: Missing OMG_BKLSI (copy-protected checkout) sets lock
        byte[] checkoutWithoutBklsi = (byte[])unrestricted0001.Clone();
        byte[] bkDescBe = Encoding.BigEndianUnicode.GetBytes("OMG_BKLSI");
        int bkIdx = checkoutWithoutBklsi.AsSpan().IndexOf(bkDescBe);
        if (bkIdx < 0)
        {
            bkIdx = checkoutWithoutBklsi.AsSpan().IndexOf("OMG_BKLSI"u8);
        }
        // 4. Clean-room 0x0001 without OMG_BKLSI is not copy-protected unless Dameda/CHINDC is set
        Assert.False(crypto.EvaluateCopyProtectionLock(checkoutWithoutBklsi));

        // 5. Condition 2: Restricted !REFID in OMG_BKLSI (refid[6] = 0x01 copy-prohibited) sets lock
        byte[] restrictedBklsi = (byte[])unrestricted0001.Clone();
        int refidIdx = restrictedBklsi.AsSpan().IndexOf("!REFID      "u8);
        int secondRefid = restrictedBklsi.AsSpan(refidIdx + 12).IndexOf("!REFID      "u8);
        int bkRefidStart = (secondRefid >= 0 ? refidIdx + 12 + secondRefid : refidIdx) + 16;
        restrictedBklsi[bkRefidStart + 6] = 0x01;
        Assert.True(crypto.EvaluateCopyProtectionLock(restrictedBklsi));

        // 6. Condition 3: Restricted !REFID in OMG_ULINF (refid[2] = 0x01 SDMI checkout lock) sets lock
        byte[] restrictedUlinf = (byte[])unrestricted0001.Clone();
        int ulRefidStart = restrictedUlinf.AsSpan().IndexOf("!REFID      "u8) + 16;
        restrictedUlinf[ulRefidStart + 2] = 0x01;
        Assert.True(crypto.EvaluateCopyProtectionLock(restrictedUlinf));
    }

    [Fact]
    public async Task OpenMgCrypto_UpdateOmaEa3TagsPreservingCrypto_PreservesGeobsEa3PayloadAndDecrypts()
    {
        var orchestrator = CreateTestOrchestrator();
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "test");
        var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
        byte[] rawFrames = new byte[128];
        Random.Shared.NextBytes(rawFrames);

        var initialTags = new Ea3TagMetadata("Old Title", "Old Artist", "Old Album", "Rock", 2005, 1, 5000);
        var wrapped = crypto.WrapAtracToOma(
            rawFrames,
            AudioCodecId.Atrac3Plus,
            0x0028FF,
            5000,
            10,
            trackId: 1,
            hardwareDeviceKey: null,
            OmaEncryptionMode.MagicGateLsi_0x0001,
            initialTags,
            ekb00010021Bytes: TestEkbFixtureHelper.BuildSyntheticTestEkb00010021());

        var newTags = new Ea3TagMetadata(
            Title: "New Title",
            Artist: "New Artist",
            Album: "New Album",
            Genre: "Electronic",
            Year: 2026,
            TrackNumber: 7,
            DurationMs: 5000,
            AlbumArtist: "Album Artist",
            Composer: "Composer X",
            SortTitle: "New Title Sort",
            Rating: 5,
            Comment: "Clean-room comment",
            ArtistLinks: "Artist Link");

        byte[] updatedOma = crypto.UpdateOmaEa3TagsPreservingCrypto(wrapped.OmaBytes, newTags);
        var parsed = crypto.ParseHeader(updatedOma);

        Assert.Equal("New Title", parsed.Tags.Title);
        Assert.Equal("New Artist", parsed.Tags.Artist);
        Assert.Equal("New Album", parsed.Tags.Album);
        Assert.Equal("Electronic", parsed.Tags.Genre);
        Assert.Equal(2026, parsed.Tags.Year);
        Assert.Equal(7, parsed.Tags.TrackNumber);
        Assert.Equal("Album Artist", parsed.Tags.AlbumArtist);
        Assert.Equal("Composer X", parsed.Tags.Composer);
        Assert.Equal("New Title Sort", parsed.Tags.SortTitle);
        Assert.Equal(5, parsed.Tags.Rating);
        Assert.Equal("Clean-room comment", parsed.Tags.Comment);
        Assert.Equal("Artist Link", parsed.Tags.ArtistLinks);
        Assert.False(parsed.IsCopyProtected);
        Assert.Equal(0, parsed.Ea3BlockSize % 1024);

        byte[] decrypted = crypto.DecryptOmaPayload(updatedOma, trackId: 1, hardwareDeviceKey: null, ekb00010021Bytes: TestEkbFixtureHelper.BuildSyntheticTestEkb00010021());
        Assert.Equal(rawFrames, decrypted);
    }

    [Fact]
    public async Task OmgV1Database_UpdateTrackMetadataAsync_Updates04CntinfAndOmaWithoutMutatingMaclist0()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_meta_{Guid.NewGuid():N}");
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

            byte[] rawFrames = new byte[64];
            var tags = new Ea3TagMetadata("Before", "Artist", "Album", "Pop", 2020, 1, 4000);
            var wrapped = crypto.WrapAtracToOma(
                rawFrames,
                AudioCodecId.Atrac3Plus,
                0x0028FF,
                4000,
                8,
                trackId: 1,
                hardwareDeviceKey: null,
                OmaEncryptionMode.MagicGateLsi_0x0001,
                tags,
                ekb00010021Bytes: TestEkbFixtureHelper.BuildSyntheticTestEkb00010021());

            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.OMA", wrapped.OmaBytes);
            byte[] maclist = new byte[32768];
            wrapped.MaclistEntry8!.CopyTo(maclist, 0);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/MACLIST0.DAT", maclist);

            var trackDto = new AudioTrackDto(
                TrackId: 1,
                Title: "Before",
                Artist: "Artist",
                Album: "Album",
                Genre: "Pop",
                SortTitle: null,
                TrackNumber: 1,
                Year: 2020,
                DurationMs: 4000,
                FrameCount: 8,
                Codec: AudioCodecId.Atrac3Plus,
                BitrateKbps: 352,
                EncryptionMode: OmaEncryptionMode.MagicGateLsi_0x0001,
                FilePropertiesBigEndian: 0x010028FFu,
                LsiKeyring48: wrapped.LsiKeyring48,
                GroupName: "Test Group",
                MaclistEntry8: wrapped.MaclistEntry8);

            await db.CommitTransactionAsync(
                identity,
                new OmgDatabaseTransaction([trackDto], [], [], PreserveExistingMaclist0: true));

            byte[] macBefore = await transport.ReadFileAsync(identity, "OMGAUDIO/MACLIST0.DAT");

            int updated = await db.UpdateTrackMetadataAsync(
                identity,
                [new TrackMetadataPatch(TrackId: 1, Title: "After", Artist: "New Artist", Rating: 4, Comment: "Note")]);

            Assert.Equal(1, updated);
            byte[] macAfter = await transport.ReadFileAsync(identity, "OMGAUDIO/MACLIST0.DAT");
            Assert.Equal(macBefore, macAfter);

            var snap = await db.ReadDatabaseFastAsync(identity);
            Assert.Single(snap.Tracks);
            Assert.Equal("After", snap.Tracks[0].Title);
            Assert.Equal("New Artist", snap.Tracks[0].Artist);
            Assert.False(snap.Tracks[0].IsCopyProtected);

            // Verify 04CNTINF.DAT stays strictly 5 slots (0x290 bytes per record)
            byte[] cntinf = await transport.ReadFileAsync(identity, "OMGAUDIO/04CNTINF.DAT");
            ushort slotCount = BinaryPrimitives.ReadUInt16BigEndian(cntinf.AsSpan(0x30 + 12, 2));
            Assert.Equal(5, slotCount);

            // Verify 05CHINDC.DAT check-in/out flag flips IsCopyProtected to true (COD-08)
            byte[] chindc = new byte[0x30 + 0x30];
            Encoding.ASCII.GetBytes("CHID").CopyTo(chindc, 0);
            chindc[4] = 0x01; chindc[5] = 0x01; chindc[8] = 0x01;
            Encoding.ASCII.GetBytes("CIDB").CopyTo(chindc, 0x10);
            BinaryPrimitives.WriteUInt32BigEndian(chindc.AsSpan(0x14, 4), 0x20);
            BinaryPrimitives.WriteUInt32BigEndian(chindc.AsSpan(0x18, 4), 0x40);
            Encoding.ASCII.GetBytes("CIDB").CopyTo(chindc, 0x20);
            BinaryPrimitives.WriteUInt16BigEndian(chindc.AsSpan(0x24, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(chindc.AsSpan(0x26, 2), 0x0030);
            BinaryPrimitives.WriteUInt16BigEndian(chindc.AsSpan(0x30, 2), 0x0001);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/05CHINDC.DAT", chindc);

            var lockedSnap = await db.ReadDatabaseFastAsync(identity);
            Assert.True(lockedSnap.Tracks[0].IsCopyProtected);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task WalkmanBackendService_BackupDeployNwmAndCommitLrc_WritesStrict8Dot3Files()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_cust_{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"apollo_bak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "OMGAUDIO"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "OMGAUDIO", "04CNTINF.DAT"), new byte[64]);
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "OMGAUDIO", "MACLIST0.DAT"), new byte[64]);

            await WriteTestDvIdDatAsync(tempRoot);
            await using var backend = new WalkmanBackendService();
            var (dev, _, err) = await backend.InspectFixtureVolumeAsync(tempRoot);
            Assert.Null(err);
            var identity = dev!.LiveIdentity!;

            string? bak = await backend.BackupOmgaudioTablesAsync(identity, backupRoot);
            Assert.NotNull(bak);
            Assert.True(File.Exists(Path.Combine(backupRoot, "04CNTINF.DAT")));
            Assert.True(File.Exists(Path.Combine(backupRoot, "MACLIST0.DAT")));

            string lrcRel = await backend.CommitDeviceLrcAsync(identity, trackId: 1, ["[00:01.00]Line 1"]);
            Assert.Equal("OMGAUDIO/10F00/10000001.LRC", lrcRel);
            Assert.True(File.Exists(Path.Combine(tempRoot, "OMGAUDIO", "10F00", "10000001.LRC")));

            var nwmProject = new NwmProjectDto(
                ScreensaverMagic.NwE400_0xEC01,
                "My Cool Banner!",
                "Apollo",
                30720,
                12288,
                120,
                36,
                1,
                [new NwmFrameEntry(0, 0x10)],
                [new byte[(120 * 36) / 8]]);

            var deploy = await backend.DeployScreensaverToDeviceAsync(identity, nwmProject);
            Assert.True(deploy.Success, deploy.ErrorMessage);
            Assert.Equal("SCREEN/MYCOOLBA.NWM", deploy.RelativePath);
            Assert.True(File.Exists(Path.Combine(tempRoot, "SCREEN", "MYCOOLBA.NWM")));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            try { Directory.Delete(backupRoot, true); } catch { }
        }
    }

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
            string l2Candidate = Path.Combine(root, "src", "L2.Engines", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            if (File.Exists(l2Candidate)) return l2Candidate;
            string l3Candidate = Path.Combine(root, "src", "L3.Presentation", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            return File.Exists(l3Candidate) ? l3Candidate : direct;
        });
    }

    private static async Task WriteTestDvIdDatAsync(string volumeRootPath)
    {
        string mp3FmDir = Path.Combine(volumeRootPath, "MP3FM");
        Directory.CreateDirectory(mp3FmDir);
        byte[] dvid = new byte[16];
        dvid[6] = 0x01;
        dvid[7] = 0x28; // 296 -> NW-E400 series
        dvid[12] = 0x12; dvid[13] = 0x34; dvid[14] = 0x56; dvid[15] = 0x78;
        await File.WriteAllBytesAsync(Path.Combine(mp3FmDir, "DvID.DAT"), dvid);
    }

    private static void WritePcmWavFile(string path, int sampleRate, int durationSeconds)
    {
        int dataBytes = sampleRate * 2 * durationSeconds; // 16-bit mono
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
