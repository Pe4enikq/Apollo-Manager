using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using System.Reflection;

namespace Walkman.M0.Tests;

/// <summary>
/// Milestone M2 verification suite covering clean-room OpenMG ea3/EA3 + 0xFFFE XOR,
/// MP3 frame hygiene, tombstone-safe OMGAUDIO v1 parsing, and byte-exact table generation.
/// </summary>
public sealed class MilestoneM2DatabaseAndCryptoTests
{
    [Fact]
    public async Task OpenMgCrypto_ParsesRealNwE403OmaHeader_AndRoundTripsXorAndHeader()
    {
        if (!TestEkbFixtureHelper.ShouldRunHardwareBackupTests()) return;
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = FindNwE403BackupRoot(codebaseRoot);
        var configuration = DetectBuildConfiguration();

        var omaPath = Path.Combine(backupRoot, "OMGAUDIO", "10F00", "10000001.OMA");
        byte[] realOmaPrefix = await File.ReadAllBytesAsync(omaPath);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "test-crypto");
        try
        {
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var parsed = crypto.ParseHeader(realOmaPrefix);

            Assert.Equal("Memory Lane", parsed.Tags.Title);
            Assert.Equal("AcesToAces", parsed.Tags.Artist);
            Assert.Equal("Memory Lane", parsed.Tags.Album);
            Assert.Equal("Electronic;Instrumental;Future Funk", parsed.Tags.Genre);
            Assert.Equal(2018, parsed.Tags.Year);
            Assert.Equal(1, parsed.Tags.TrackNumber);
            Assert.Equal(135079u, parsed.DurationMs);
            Assert.Equal(5171u, parsed.FrameCount);
            Assert.Equal(AudioCodecId.Mp3, parsed.CodecId);
            Assert.Equal(0x0080DE10u, parsed.CodecParams24);
            Assert.Equal(OmaEncryptionMode.HardwareXor_0xFFFE, parsed.EncryptionId);
            Assert.Equal(0x400, parsed.Ea3BlockSize);
            Assert.Equal(0x460, parsed.AudioPayloadOffset);

            // Verify 0xFFFE XOR descramble and re-scramble parity on real Track 1 payload slice
            uint trackKey = crypto.ComputeTrackXorKey(1, TestEkbFixtureHelper.SyntheticDeviceKeyA);
            Assert.Equal(0x5BB089BAu, trackKey);

            byte[] originalSlice = realOmaPrefix.AsSpan(parsed.AudioPayloadOffset, 64).ToArray();
            byte[] working = originalSlice.ToArray();

            crypto.ApplyXorScrambleInPlace(working, trackKey);
            Assert.Equal((byte)0xFF, working[0]);
            Assert.Equal((byte)0xFB, working[1]);
            Assert.Equal((byte)0xE0, working[2]);
            Assert.Equal((byte)'I', working[36]);
            Assert.Equal((byte)'n', working[37]);
            Assert.Equal((byte)'f', working[38]);
            Assert.Equal((byte)'o', working[39]);

            crypto.ApplyXorScrambleInPlace(working, trackKey);
            Assert.Equal(originalSlice, working);

            // Verify BuildHeader -> ParseHeader round-trip
            byte[] rebuiltHeader = crypto.BuildHeader(parsed);
            Assert.Equal(0x460, rebuiltHeader.Length);

            var reparsed = crypto.ParseHeader(rebuiltHeader);
            Assert.Equal(parsed.Tags.Title, reparsed.Tags.Title);
            Assert.Equal(parsed.Tags.Artist, reparsed.Tags.Artist);
            Assert.Equal(parsed.Tags.Album, reparsed.Tags.Album);
            Assert.Equal(parsed.Tags.Genre, reparsed.Tags.Genre);
            Assert.Equal(parsed.Tags.Year, reparsed.Tags.Year);
            Assert.Equal(parsed.Tags.TrackNumber, reparsed.Tags.TrackNumber);
            Assert.Equal(parsed.DurationMs, reparsed.DurationMs);
            Assert.Equal(parsed.FrameCount, reparsed.FrameCount);
            Assert.Equal(parsed.CodecParams24, reparsed.CodecParams24);
            Assert.Equal(parsed.EncryptionId, reparsed.EncryptionId);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("test-crypto");
        }
    }

    [Fact]
    public async Task OpenMgCrypto_WrapsStrippedMp3ToOma_AndUnwrapsBackWithId3Tags()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "test-crypto-wrap");
        try
        {
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();

            // Build synthetic 10-frame MPEG-1 Layer III 128 kbps 44.1 kHz CBR stream (417 bytes/frame)
            byte[] cleanFrames = BuildSyntheticMpeg1L3Stream(frameCount: 10, bitrateIndex: 9, frameBytes: 417);
            byte[] taggedMp3 = PrependId3v2AndAppendId3v1(cleanFrames);

            var tags = new Ea3TagMetadata(
                Title: "Apollo M2 Synthetic",
                Artist: "Apollo Test Artist",
                Album: "Apollo Test Album",
                Genre: "Electronic",
                Year: 2026,
                TrackNumber: 1,
                DurationMs: 0);

            byte[] omaBytes = crypto.WrapMp3ToOma(
                taggedMp3,
                trackId: 28,
                hardwareDeviceKey: TestEkbFixtureHelper.SyntheticDeviceKeyA,
                encryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
                tags: tags,
                ea3BlockSize: 1024);

            var omaInfo = crypto.ParseHeader(omaBytes);
            Assert.Equal(10u, omaInfo.FrameCount);
            Assert.Equal(261u, omaInfo.DurationMs); // round(10 * 1152 * 1000 / 44100)
            Assert.Equal(0x0080D910u, omaInfo.CodecParams24); // CBR, MPEG1 L3 128k, Stereo
            Assert.Equal(cleanFrames.Length, omaBytes.Length - omaInfo.AudioPayloadOffset);

            byte[] unwrappedMp3 = crypto.UnwrapOmaToMp3(omaBytes, trackId: 28, hardwareDeviceKey: TestEkbFixtureHelper.SyntheticDeviceKeyA);
            var unwrappedInfo = crypto.InspectMp3Payload(unwrappedMp3);

            Assert.Equal(10u, unwrappedInfo.FrameCount);
            Assert.Equal(cleanFrames.Length, unwrappedInfo.CleanPayloadLength);
            Assert.Equal(cleanFrames, unwrappedMp3.AsSpan(unwrappedInfo.AudioOffset, unwrappedInfo.CleanPayloadLength).ToArray());
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("test-crypto-wrap");
        }
    }

    [Fact]
    public async Task OmgV1Database_ReadsRealBackupWithoutTombstones_AndGeneratesByteExactTables()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = FindNwE403BackupRoot(codebaseRoot);
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var poller = new DevicePollerCoordinator(registry);
        var outcome = poller.InspectFixtureVolume(
            backupRoot,
            vendorId: 0x054C,
            productId: 0x01FB,
            usbProductString: "SONY NWWM MEM AAD2 USB Device",
            overrideTotalBytes: 255_492_096,
            overrideVolumeSerial: TestEkbFixtureHelper.SyntheticVolumeSerialA);

        Assert.True(outcome.Resolution.IsRecognized);
        var device = outcome.Resolution.Identity!;

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_m2_db_{Guid.NewGuid():N}");
        CopyDirectoryRecursive(backupRoot, sandboxRoot);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();

            // 1. Verify read-only snapshot on real NW-E403 backup filters out all deleted tombstones
            var snapshot = await db.ReadDatabaseAsync(device);
            Assert.Equal(24, snapshot.Tracks.Count);
            Assert.Equal(3, snapshot.Groups.Count);
            Assert.Equal(5_684_232u, snapshot.TotalDurationMs);

            var track1 = snapshot.Tracks.First(t => t.TrackId == 1);
            Assert.Equal("Memory Lane", track1.Title);
            Assert.Equal("AcesToAces", track1.Artist);
            Assert.Equal(AudioCodecId.Mp3, track1.Codec);
            Assert.Equal(320, track1.BitrateKbps);
            Assert.Equal(135079u, track1.DurationMs);
            Assert.Equal(5171u, track1.FrameCount);
            Assert.Equal(4096L, track1.FileSizeBytes);
            Assert.Equal("Smtng else", track1.GroupName);
            Assert.NotNull(track1.LsiKeyring48);

            // 2. Commit a prepared 28th track into sandbox copy and verify zero data loss + byte-exact fixed tables
            var sandboxDevice = device with { VolumeRootPath = sandboxRoot, VolumeSerialNumber = null };
            byte[] cleanFrames = BuildSyntheticMpeg1L3Stream(frameCount: 15, bitrateIndex: 14, frameBytes: 1044);
            var newTags = new Ea3TagMetadata(
                Title: "Apollo Test Song",
                Artist: "Apollo Artist",
                Album: "Apollo Album",
                Genre: "Electronic",
                Year: 2026,
                TrackNumber: 1,
                DurationMs: 0);

            byte[] wrappedOma = crypto.WrapMp3ToOma(
                cleanFrames,
                trackId: 28,
                hardwareDeviceKey: TestEkbFixtureHelper.SyntheticDeviceKeyA,
                encryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
                tags: newTags);

            var parsedNewOma = crypto.ParseHeader(wrappedOma);
            await transport.WriteFileAtomicAsync(sandboxDevice, "OMGAUDIO/10F00/1000001C.OMA", wrappedOma);

            var newTrackDto = new AudioTrackDto(
                TrackId: 28,
                Title: newTags.Title,
                Artist: newTags.Artist,
                Album: newTags.Album,
                Genre: newTags.Genre,
                SortTitle: null,
                TrackNumber: 1,
                Year: 2026,
                DurationMs: parsedNewOma.DurationMs,
                FrameCount: parsedNewOma.FrameCount,
                Codec: AudioCodecId.Mp3,
                BitrateKbps: 320,
                EncryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
                FilePropertiesBigEndian: ((uint)AudioCodecId.Mp3 << 24) | parsedNewOma.CodecParams24,
                LsiKeyring48: null,
                FileSizeBytes: wrappedOma.Length,
                GroupName: "Smtng else");

            await db.CommitTransactionAsync(
                sandboxDevice,
                new OmgDatabaseTransaction([newTrackDto], Array.Empty<int>(), Array.Empty<TrackGroupDto>()));

            // Re-read sandbox database and verify all 24 original tracks + new Track 28 are intact
            var updatedSnapshot = await db.ReadDatabaseAsync(sandboxDevice);
            Assert.Equal(25, updatedSnapshot.Tracks.Count);
            Assert.Equal(3, updatedSnapshot.Groups.Count);
            Assert.Equal(5_684_232u + parsedNewOma.DurationMs, updatedSnapshot.TotalDurationMs);

            var reloadedNew = updatedSnapshot.Tracks.First(t => t.TrackId == 28);
            Assert.Equal("Apollo Test Song", reloadedNew.Title);
            Assert.Equal("Smtng else", reloadedNew.GroupName);
            Assert.Equal((long)wrappedOma.Length, reloadedNew.FileSizeBytes);

            // Verify byte-exact structural parity with real NW-E403 00GTRLST, 01TREE22, 03GINF22
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(backupRoot, "OMGAUDIO", "00GTRLST.DAT")),
                await File.ReadAllBytesAsync(Path.Combine(sandboxRoot, "OMGAUDIO", "00GTRLST.DAT")));
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(backupRoot, "OMGAUDIO", "01TREE22.DAT")),
                await File.ReadAllBytesAsync(Path.Combine(sandboxRoot, "OMGAUDIO", "01TREE22.DAT")));
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(backupRoot, "OMGAUDIO", "03GINF22.DAT")),
                await File.ReadAllBytesAsync(Path.Combine(sandboxRoot, "OMGAUDIO", "03GINF22.DAT")));

            // Verify deleting Track #28 commits clean tables first and removes 1000001C.OMA without losing original 24 tracks
            await db.CommitTransactionAsync(
                sandboxDevice,
                new OmgDatabaseTransaction(Array.Empty<AudioTrackDto>(), [28], Array.Empty<TrackGroupDto>()));
            await transport.DeleteFileAsync(sandboxDevice, "OMGAUDIO/10F00/1000001C.OMA");

            Assert.False(await transport.ExistsAsync(sandboxDevice, "OMGAUDIO/10F00/1000001C.OMA"));
            var afterDeleteSnapshot = await db.ReadDatabaseAsync(sandboxDevice);
            Assert.Equal(24, afterDeleteSnapshot.Tracks.Count);
            Assert.Equal(3, afterDeleteSnapshot.Groups.Count);
            Assert.Equal(5_684_232u, afterDeleteSnapshot.TotalDurationMs);

            // Verify 02TREINF.DAT matches real NW-E403 02TREINF.DAT byte-for-byte after deletion
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(backupRoot, "OMGAUDIO", "02TREINF.DAT")),
                await File.ReadAllBytesAsync(Path.Combine(sandboxRoot, "OMGAUDIO", "02TREINF.DAT")));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    private static byte[] BuildSyntheticMpeg1L3Stream(int frameCount, byte bitrateIndex, int frameBytes)
    {
        byte[] stream = new byte[frameCount * frameBytes];
        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * frameBytes;
            // MPEG-1 (11), Layer III (01), No CRC (1) -> 0xFF 0xFB
            stream[offset] = 0xFF;
            stream[offset + 1] = 0xFB;
            // bitrateIndex << 4 | srateIndex=0 (44.1kHz) | pad=0 -> (bitrateIndex << 4)
            stream[offset + 2] = (byte)(bitrateIndex << 4);
            // Joint Stereo (01) -> 0x64
            stream[offset + 3] = 0x64;
        }

        return stream;
    }

    private static byte[] PrependId3v2AndAppendId3v1(byte[] mp3Frames)
    {
        byte[] id3v2 = new byte[128];
        id3v2[0] = (byte)'I';
        id3v2[1] = (byte)'D';
        id3v2[2] = (byte)'3';
        id3v2[3] = 0x03;
        // 118 bytes synchsafe = 0x00000076
        id3v2[9] = 0x76;

        byte[] id3v1 = new byte[128];
        id3v1[0] = (byte)'T';
        id3v1[1] = (byte)'A';
        id3v1[2] = (byte)'G';

        byte[] combined = new byte[id3v2.Length + mp3Frames.Length + id3v1.Length];
        id3v2.CopyTo(combined, 0);
        mp3Frames.CopyTo(combined, id3v2.Length);
        id3v1.CopyTo(combined, id3v2.Length + mp3Frames.Length);
        return combined;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    private static string FindNwE403BackupRoot(string codebaseRoot)
    {
        var path = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Live_Backup"));
        Assert.True(Directory.Exists(path), $"Expected NW-E403 backup fixture at '{path}'.");
        return path;
    }

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(MilestoneM2DatabaseAndCryptoTests)
            .Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>();

        return string.IsNullOrWhiteSpace(attr?.Configuration) ? "Debug" : attr.Configuration;
    }

    private static string ResolvePluginDllPath(string codebaseRoot, string configuration, string moduleId)
    {
        string relativeProjectDir = moduleId switch
        {
            WellKnownModules.L2TransportMsc => Path.Combine("src", "L2.Engines", "Walkman.L2.Transport.Msc"),
            WellKnownModules.L2DatabaseOmgV1 => Path.Combine("src", "L2.Engines", "Walkman.L2.Database.OmgV1"),
            WellKnownModules.L2CryptoOpenMg => Path.Combine("src", "L2.Engines", "Walkman.L2.Crypto.OpenMG"),
            WellKnownModules.L2CodecPipeline => Path.Combine("src", "L2.Engines", "Walkman.L2.Codec.Pipeline"),
            WellKnownModules.L2CustomNwmEngine => Path.Combine("src", "L2.Engines", "Walkman.L2.Custom.NwmEngine"),
            WellKnownModules.L2TelemetryDumper => Path.Combine("src", "L2.Engines", "Walkman.L2.Telemetry.Dumper"),
            WellKnownModules.L3ScreensaverStudio => Path.Combine("src", "L3.Presentation", "Walkman.L3.ScreensaverStudio"),
            WellKnownModules.L3DumpWizard => Path.Combine("src", "L3.Presentation", "Walkman.L3.DumpWizard"),
            _ => throw new ArgumentOutOfRangeException(nameof(moduleId), moduleId, "Unknown module id.")
        };

        return Path.Combine(
            codebaseRoot,
            relativeProjectDir,
            "bin",
            configuration,
            "net8.0",
            $"Walkman.{moduleId}.dll");
    }
}
