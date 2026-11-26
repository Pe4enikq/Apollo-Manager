using System.Buffers.Binary;
using NAudio.Wave;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.M0.Tests;

public sealed class V020BetaCodingTicketsTests
{
    private static string DetectBuildConfiguration() =>
        AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static string ResolvePluginDllPath(string codebaseRoot, string configuration, string moduleId)
    {
        string projectName = moduleId switch
        {
            "L2.Transport.Msc" => "Walkman.L2.Transport.Msc",
            "L2.Database.OmgV1" => "Walkman.L2.Database.OmgV1",
            "L2.Crypto.OpenMG" => "Walkman.L2.Crypto.OpenMG",
            "L2.Codec.Pipeline" => "Walkman.L2.Codec.Pipeline",
            "L2.Custom.NwmEngine" => "Walkman.L2.Custom.NwmEngine",
            "L2.Telemetry.Dumper" => "Walkman.L2.Telemetry.Dumper",
            _ => throw new InvalidOperationException($"Unknown moduleId '{moduleId}'.")
        };

        return Path.Combine(codebaseRoot, "src", "L2.Engines", projectName, "bin", configuration, "net8.0", $"{projectName}.dll");
    }

    private static async Task<ModuleOrchestrator> CreateLoadedOrchestratorAsync(params string[] modules)
    {
        string codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        string cfg = DetectBuildConfiguration();
        var orch = new ModuleOrchestrator(
            new WalkmanEventBus(),
            new WalkmanMediator(),
            id => ResolvePluginDllPath(codebaseRoot, cfg, id));
        foreach (string m in modules)
        {
            await orch.LoadModuleAsync(m, "test-scope");
        }
        return orch;
    }

    private static DeviceProfile CreateE403Profile() => new(
        SeriesId: "NW-E400",
        DisplayName: "Sony NW-E400 Series",
        Generation: WalkmanGeneration.Gen3_OmgV1,
        Transport: TransportProtocol.MassStorage,
        DatabaseFormat: DatabaseFormat.OmgV1,
        RequiresHardwareDvIdKey: true,
        ExpectedDvIdSegment: 296,
        UsbIds: [new UsbIdentifier(0x054C, 0x01FB, "NW-E405")],
        CapacityTiers: [new CapacityTierRule("NW-E405", 512, 380_000_000, 600_000_000, false)],
        ColorVariants: [],
        Codecs:
        [
            new CodecCapability(AudioCodecId.Mp3, [128, 192, 320], [44100], true, OmaEncryptionMode.HardwareXor_0xFFFE),
            new CodecCapability(AudioCodecId.Atrac3, [66, 105, 132], [44100], false, OmaEncryptionMode.MagicGateLsi_0x0001),
            new CodecCapability(AudioCodecId.Atrac3Plus, [48, 64, 96, 128, 160, 192, 256, 320, 352], [44100], false, OmaEncryptionMode.MagicGateLsi_0x0001)
        ],
        Screen: new ScreenSpecification(120, 36, 48, 120, 1, 720, ScreensaverMagic.NwE400_0xEC01, true, 45056, 56, ["NWWM-SCR", "SCREEN"]),
        Markers: new FilesystemMarkers(["OMGAUDIO"], ["NWWM-SCR"], [], []),
        RequiredL2Modules: [],
        SupportedL3Modules: [],
        SupportStatus: SupportStatus.Verified);

    private static ConnectedDeviceIdentity CreateMockDevice(string rootDir, DeviceProfile? profile = null) => new(
        DeviceInstanceId: "TEST-E405-001",
        Profile: profile ?? CreateE403Profile(),
        ResolvedModelCode: "NW-E405",
        ColorSuffix: "B",
        VolumeRootPath: rootDir,
        VolumeSerialNumber: 0x12345678u,
        HardwareDvIdKey: 0x11223344u,
        RawDvIdBytes: null);

    private static string WriteTestSineWav(string path, double seconds = 0.6)
    {
        var fmt = new WaveFormat(44100, 16, 2);
        int samples = (int)(44100 * seconds);
        using var writer = new WaveFileWriter(path, fmt);
        short[] buf = new short[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440.0 * i / 44100.0) * 14000);
            buf[i * 2] = v;
            buf[i * 2 + 1] = v;
        }
        writer.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private static string WriteTestAiff(string path, double seconds = 0.5)
    {
        int numFrames = (int)(44100 * seconds);
        int pcmBytes = numFrames * 4;
        int ssndChunkSize = 8 + pcmBytes;
        int commChunkSize = 18;
        int formSize = 4 + (8 + commChunkSize) + (8 + ssndChunkSize);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("FORM"u8);
        WriteBE32(bw, formSize);
        bw.Write("AIFF"u8);

        bw.Write("COMM"u8);
        WriteBE32(bw, commChunkSize);
        WriteBE16(bw, 2);
        WriteBE32(bw, numFrames);
        WriteBE16(bw, 16);
        bw.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        bw.Write("SSND"u8);
        WriteBE32(bw, ssndChunkSize);
        WriteBE32(bw, 0);
        WriteBE32(bw, 0);
        for (int i = 0; i < numFrames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440.0 * i / 44100.0) * 12000);
            WriteBE16(bw, v);
            WriteBE16(bw, v);
        }
        return path;
    }

    private static void WriteBE16(BinaryWriter bw, int val)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, (short)val);
        bw.Write(b);
    }

    private static void WriteBE32(BinaryWriter bw, int val)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, val);
        bw.Write(b);
    }

    [Fact]
    public async Task Cod29_SingleTrackAppendIntoExistingSonicStageAlbum_DoesNotDuplicateGroupOrAlbum()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod29_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "OMGAUDIO"));
        var orch = await CreateLoadedOrchestratorAsync("L2.Transport.Msc", "L2.Crypto.OpenMG", "L2.Database.OmgV1");
        try
        {
            var device = CreateMockDevice(dir);
            var db = orch.ResolveRequiredEngine<IDatabaseEngine>();

            var initialTracks = Enumerable.Range(1, 3).Select(id => new AudioTrackDto(
                TrackId: id,
                Title: $"Track {id}",
                Artist: "Benny Benassi",
                Album: "Satisfaction ",
                Genre: "Electronic",
                SortTitle: null,
                TrackNumber: id,
                Year: 2003,
                DurationMs: 200_000,
                FrameCount: 7600,
                Codec: AudioCodecId.Mp3,
                BitrateKbps: 192,
                EncryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
                FilePropertiesBigEndian: 0x0380DE10u,
                LsiKeyring48: null,
                GroupName: "Satisfaction ")).ToArray();

            await db.RebuildFromTracksAsync(device, initialTracks);

            var newTrack = new AudioTrackDto(
                TrackId: 4,
                Title: "Satisfaction (Single)",
                Artist: "Benny Benassi",
                Album: "satisfaction",
                Genre: "Electronic",
                SortTitle: null,
                TrackNumber: 4,
                Year: 2003,
                DurationMs: 210_000,
                FrameCount: 8000,
                Codec: AudioCodecId.Mp3,
                BitrateKbps: 192,
                EncryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
                FilePropertiesBigEndian: 0x0380DE10u,
                LsiKeyring48: null,
                GroupName: "satisfaction");

            await db.CommitTransactionAsync(device, new OmgDatabaseTransaction(
                UpsertedTracks: [newTrack],
                DeletedTrackIds: [],
                UpdatedGroups: []));

            var snap = await db.ReadDatabaseFastAsync(device);
            Assert.Equal(4, snap.Tracks.Count);
            Assert.Single(snap.Groups);
            Assert.Equal("Satisfaction", snap.Groups[0].Title);
            Assert.Equal(4, snap.Groups[0].TrackIds.Count);

            byte[] tree03 = await File.ReadAllBytesAsync(Path.Combine(dir, "OMGAUDIO", "01TREE03.DAT"));
            int gplbCount = BinaryPrimitives.ReadUInt16BigEndian(tree03.AsSpan(0x30 + 4, 2));
            uint tplbOff = BinaryPrimitives.ReadUInt32BigEndian(tree03.AsSpan(36, 4));
            int tplbCount = BinaryPrimitives.ReadUInt16BigEndian(tree03.AsSpan((int)tplbOff + 4, 2));
            Assert.Equal(1, gplbCount);
            Assert.Equal(4, tplbCount);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod31_DeployScreensaver_DeletesPriorNwmFilesSoOnlyOneRemains()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod31_{Guid.NewGuid():N}");
        string nwmDir = Path.Combine(dir, "NWWM-SCR");
        Directory.CreateDirectory(nwmDir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(nwmDir, "fading_wind.nwm"), new byte[128]);
            await File.WriteAllBytesAsync(Path.Combine(nwmDir, "OLD_ONE.NWM"), new byte[128]);

            await using var backend = new WalkmanBackendService();
            var device = CreateMockDevice(dir);
            var project = new NwmProjectDto(
                Magic: ScreensaverMagic.NwE400_0xEC01,
                Name: "RIBBON",
                Author: "Apollo",
                TimingFlag1: 0,
                TimingFlag2: 1,
                Width: 48,
                Height: 120,
                BitsPerPixel: 1,
                Timeline: [new NwmFrameEntry(0, 100)],
                UniqueBitmaps: [new byte[720]]);

            var res = await backend.DeployScreensaverToDeviceAsync(device, project, backupBeforeWrite: false);

            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal("NWWM-SCR/RIBBON.NWM", res.RelativePath);

            string[] remaining = Directory.GetFiles(nwmDir, "*.nwm");
            Assert.Single(remaining);
            Assert.Equal("RIBBON.NWM", Path.GetFileName(remaining[0]), ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod10_AutoMintAndManualSlotAllocation_WorkViaIcvBridgeContract()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod10_{Guid.NewGuid():N}");
        string poolDir = Path.Combine(dir, "_pool");
        Directory.CreateDirectory(Path.Combine(dir, "OMGAUDIO"));
        var orch = await CreateLoadedOrchestratorAsync("L2.Transport.Msc", "L2.Crypto.OpenMG", "L2.Database.OmgV1", "L2.Codec.Pipeline");
        try
        {
            var device = CreateMockDevice(dir);
            var fakeBridge = new FakeOpenMgIcvBridge(dir);
            var stubOrch = new DelegatingOrchestrator(orch, fakeBridge);
            var coordinator = new FileManagerCoordinator(stubOrch, slotPoolDirectory: poolDir);

            var allocRes = await coordinator.AllocateMagicGateSlotsAsync(device, 2);
            Assert.True(allocRes.Success, allocRes.ErrorMessage);
            Assert.Equal(2, allocRes.MintedSlots!.Count);
            Assert.True(File.Exists(Path.Combine(dir, "OMGAUDIO", "10F00", "10000001.OMA")));
            Assert.True(File.Exists(Path.Combine(dir, "OMGAUDIO", "10F00", "10000002.OMA")));

            var badRes = await coordinator.AllocateMagicGateSlotsAsync(device, 999);
            Assert.False(badRes.Success);

            await using var backend = new WalkmanBackendService();
            using var player = new AudioPlaybackService();
            using var vm = new MainWindowViewModel(backend, player, suppressSettingsSave: true);
            vm.FmUploadMode = UploadMode.Quality;
            vm.IsOpenMgRuntimePromptOpen = true;
            vm.SwitchToMp3ModeFromOpenMgPromptCommand.Execute(null);
            Assert.False(vm.IsOpenMgRuntimePromptOpen);
            Assert.Equal(UploadMode.Auto, vm.FmUploadMode);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod26_Atrac3AndAtrac1EncodingAndVgmstreamDecode_RoundTripCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod26_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var orch = await CreateLoadedOrchestratorAsync("L2.Crypto.OpenMG", "L2.Codec.Pipeline");
        try
        {
            // COD-37: ATRAC3 / ATRAC1 specs remain gated out of user-facing v0.1.1b CustomCodecSpecs while backend retains encoder coverage
            Assert.DoesNotContain(FileManagerStateLogic.CustomCodecSpecs, c => c.Codec == AudioCodecId.Atrac3);
            Assert.DoesNotContain(FileManagerStateLogic.CustomCodecSpecs, c => c.Codec == AudioCodecId.Atrac1);
            Assert.Equal(".aea", ConverterStateLogic.GetOutputExtension(TargetCodec.Atrac1));

            string wavPath = WriteTestSineWav(Path.Combine(dir, "sine.wav"), 0.5);
            var codec = orch.ResolveRequiredEngine<ICodecService>();
            var tags = new Ea3TagMetadata("Sine Test", "Apollo", "Codec Suite", "Test", 2026, 1, 500);

            string at3Oma = Path.Combine(dir, "out_lp2.oma");
            var at3Res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavPath, at3Oma, AudioCodecId.Atrac3, 132, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(at3Res.Success, at3Res.ErrorMessage);
            string at3DecodedWav = Path.Combine(dir, "out_lp2_dec.wav");
            Assert.True(await codec.ExtractFromOmaAsync(at3Oma, at3DecodedWav, null));
            Assert.True(new FileInfo(at3DecodedWav).Length > 44);

            string at1Aea = Path.Combine(dir, "out_sp.aea");
            var at1Res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavPath, at1Aea, AudioCodecId.Atrac1, 292, OmaEncryptionMode.Unprotected_0xFFFF, 2, null, tags));
            Assert.True(at1Res.Success, at1Res.ErrorMessage);
            Assert.True(new FileInfo(at1Aea).Length > 2048);
            string at1DecodedWav = Path.Combine(dir, "out_sp_dec.wav");
            Assert.True(await codec.ExtractFromOmaAsync(at1Aea, at1DecodedWav, null));
            Assert.True(new FileInfo(at1DecodedWav).Length > 44);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod28_ExtendedAudioExtensionsAndAiffTranscode_WorkAndExcludeForbiddenFormats()
    {
        foreach (string verified in new[] { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".oma", ".aa3", ".omg" })
        {
            Assert.Contains(verified, WalkmanBackendService.SupportedAudioExtensions);
        }

        // COD-37: Unverified COD-28 extended formats & forbidden formats are gated out of v0.1.1b SupportedAudioExtensions
        foreach (string gatedOrExcluded in new[] { ".m4a", ".aac", ".alac", ".wma", ".aiff", ".aif", ".wv", ".ape", ".at3", ".at9", ".aea", ".mpc", ".tta", ".dsf", ".dff" })
        {
            Assert.DoesNotContain(gatedOrExcluded, WalkmanBackendService.SupportedAudioExtensions);
        }

        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod28_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var orch = await CreateLoadedOrchestratorAsync("L2.Crypto.OpenMG", "L2.Codec.Pipeline");
        try
        {
            string aiffPath = WriteTestAiff(Path.Combine(dir, "tone.aiff"), 0.4);
            var codec = orch.ResolveRequiredEngine<ICodecService>();
            var tags = new Ea3TagMetadata("AIFF Tone", "Apollo", "Tests", "Test", 2026, 1, 400);

            string outOma = Path.Combine(dir, "aiff_to_atp.oma");
            var res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                aiffPath, outOma, AudioCodecId.Atrac3Plus, 192, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal(0x00288Bu, res.CodecParams24);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod30_PreEncodedAtrac3Plus192KbpsOmaAndAa3_RecognizedDecodedAndTransferred()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo_cod30_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var orch = await CreateLoadedOrchestratorAsync("L2.Crypto.OpenMG", "L2.Codec.Pipeline");
        try
        {
            string wavPath = WriteTestSineWav(Path.Combine(dir, "src.wav"), 0.5);
            var crypto = orch.ResolveRequiredEngine<IOpenMgCryptoService>();
            var codec = orch.ResolveRequiredEngine<ICodecService>();
            var tags = new Ea3TagMetadata("PreEncoded 192k", "Tester", "Album192", "Synth", 2026, 1, 500);

            string oma192 = Path.Combine(dir, "track_192.oma");
            var encRes = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavPath, oma192, AudioCodecId.Atrac3Plus, 192, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(encRes.Success, encRes.ErrorMessage);
            Assert.Equal(0x00288Bu, encRes.CodecParams24);

            byte[] fullOma = await File.ReadAllBytesAsync(oma192);
            var hdr = crypto.ParseHeader(fullOma);
            byte[] rawAa3 = fullOma.AsSpan(hdr.Ea3BlockSize).ToArray();
            rawAa3.AsSpan(36, 8).Clear();
            string aa3Path = Path.Combine(dir, "raw_192.aa3");
            await File.WriteAllBytesAsync(aa3Path, rawAa3);

            var aa3Hdr = crypto.ParseHeader(rawAa3);
            Assert.Equal(AudioCodecId.Atrac3Plus, aa3Hdr.CodecId);
            Assert.Equal(0x00288Bu, aa3Hdr.CodecParams24);
            Assert.True(aa3Hdr.FrameCount > 0);
            Assert.True(aa3Hdr.DurationMs > 0);

            await using var backend = new WalkmanBackendService();
            var scanned = backend.ScanLocalFolderTracks(dir, "pc-library");
            var scannedOma = Assert.Single(scanned, t => t.SourceFilePath == oma192);
            Assert.Equal("ATRAC3+", scannedOma.Format);
            Assert.Equal("192 kbps", scannedOma.Bitrate);
            var scannedAa3 = Assert.Single(scanned, t => t.SourceFilePath == aa3Path);
            Assert.Equal("ATRAC3+", scannedAa3.Format);
            Assert.Equal("192 kbps", scannedAa3.Bitrate);

            string rewrappedOma = Path.Combine(dir, "rewrapped_192.oma");
            var rewrapRes = await codec.ProcessToOmaAsync(new TranscodeRequest(
                aa3Path, rewrappedOma, AudioCodecId.Atrac3Plus, 192, OmaEncryptionMode.Unprotected_0xFFFF, 2, null, tags));
            Assert.True(rewrapRes.Success, rewrapRes.ErrorMessage);
            Assert.Equal(0x00288Bu, rewrapRes.CodecParams24);

            string decodedWav = Path.Combine(dir, "decoded_192.wav");
            Assert.True(await codec.ExtractFromOmaAsync(rewrappedOma, decodedWav, null));
            Assert.True(new FileInfo(decodedWav).Length > 44);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod32_AtracdencAllExposedBitratesWithSpacesInPath_EncodeAndDecodeCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"apollo cod32 spaces {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var orch = await CreateLoadedOrchestratorAsync("L2.Crypto.OpenMG", "L2.Codec.Pipeline");
        try
        {
            string wavPath = WriteTestSineWav(Path.Combine(dir, "source track with spaces.wav"), 0.5);
            var codec = orch.ResolveRequiredEngine<ICodecService>();
            var tags = new Ea3TagMetadata("Spaces Test", "Apollo", "Smoke Fix", "Test", 2026, 1, 500);

            foreach (int kbps in new[] { 66, 105, 132 })
            {
                string outOma = Path.Combine(dir, $"atrac3 {kbps}k out.oma");
                var res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                    wavPath, outOma, AudioCodecId.Atrac3, kbps, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
                Assert.True(res.Success, $"ATRAC3 {kbps}k failed: {res.ErrorMessage}");
                Assert.True(new FileInfo(outOma).Length > 1024);

                string decWav = Path.Combine(dir, $"atrac3 {kbps}k dec.wav");
                Assert.True(await codec.ExtractFromOmaAsync(outOma, decWav, null));
                Assert.True(new FileInfo(decWav).Length > 44);
            }

            string outAea = Path.Combine(dir, "atrac1 292k out.aea");
            var at1Res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavPath, outAea, AudioCodecId.Atrac1, 292, OmaEncryptionMode.Unprotected_0xFFFF, 2, null, tags));
            Assert.True(at1Res.Success, $"ATRAC1 292k failed: {at1Res.ErrorMessage}");
            Assert.True(new FileInfo(outAea).Length > 2048);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cod33_OpenMgIcvWorker_IsStagedInAppShellOutputAndDiscoveredByBridge()
    {
        string codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        string appShellProj = await File.ReadAllTextAsync(Path.Combine(codebaseRoot, "src", "L3.Presentation", "Walkman.L3.AppShell", "Walkman.L3.AppShell.csproj"));
        Assert.Contains("StageOpenMgIcvWorker", appShellProj, StringComparison.Ordinal);

        string packagerScript = await File.ReadAllTextAsync(Path.Combine(codebaseRoot, "build", "package-win-x64.ps1"));
        Assert.Contains("Walkman.OpenMgIcvWorker.csproj", packagerScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IncludeIcvWorker", packagerScript, StringComparison.OrdinalIgnoreCase);

        var orch = await CreateLoadedOrchestratorAsync("L2.Crypto.OpenMG");
        try
        {
            var bridge = orch.ResolveRequiredEngine<IOpenMgIcvBridge>();
            var parseMethod = bridge.GetType().GetMethod(
                "ParseWorkerExitResult",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(parseMethod);

            var errResult = Assert.IsType<IcvCommitResult>(parseMethod!.Invoke(
                null,
                [1, "{\"Success\":false,\"ErrorMessage\":\"IOmgHdwmAuth::step1 failed (HRESULT 0x80070057).\"}", string.Empty]));
            Assert.False(errResult.Success);
            Assert.Contains("HRESULT 0x80070057", errResult.ErrorMessage, StringComparison.Ordinal);

            var fallbackResult = Assert.IsType<IcvCommitResult>(parseMethod.Invoke(null, [1, string.Empty, "access denied"]));
            Assert.False(fallbackResult.Success);
            Assert.Equal("access denied", fallbackResult.ErrorMessage);
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
        }
    }

    [Fact]
    public async Task Cod34_Nwm0xEc01LimitsManifestAnd0xD301ThumbnailPreservation_WorkAsSpecified()
    {
        string codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var registry = Walkman.L1.Orchestrator.Manifests.DeviceManifestRegistry.LoadFromFile(
            Path.Combine(codebaseRoot, "manifests", "devices.json"));

        foreach (string seriesId in new[] { "NW-E400", "NW-E500", "NW-A600" })
        {
            var profile = Assert.Single(registry.Profiles, p => p.SeriesId == seriesId);
            Assert.NotNull(profile.Screen);
            Assert.Equal(80, profile.Screen!.MaxUniqueBitmaps);
            Assert.Equal(58368, profile.Screen.MaxFileSizeBytes);
            Assert.Equal(200, profile.Screen.MaxFrameCount);
        }

        var e400Screen = Assert.Single(registry.Profiles, p => p.SeriesId == "NW-E400").Screen!;
        var orch = await CreateLoadedOrchestratorAsync("L2.Custom.NwmEngine");
        try
        {
            var packer = orch.ResolveRequiredEngine<IScreensaverPacker>();

            // 1. Exact hardware max PASS: 80 unique bitmaps + 200 frames (58,030 B)
            var bitmaps80 = Enumerable.Range(0, 80).Select(i =>
            {
                byte[] b = new byte[540];
                b[0] = (byte)(i + 1);
                return b;
            }).ToList();
            var frames200 = Enumerable.Range(0, 200).Select(i => new NwmFrameEntry((byte)(i % 80))).ToList();
            var maxProj = new NwmProjectDto(ScreensaverMagic.NwE400_0xEC01, "RE07_MAX", "Apollo", 30720, 12288, 120, 36, 1, frames200, bitmaps80);
            var maxVal = packer.Validate(maxProj, e400Screen);
            Assert.True(maxVal.IsValid, string.Join("; ", maxVal.Errors));
            Assert.True(packer.Pack(maxProj).Length <= 58368);

            // 2. 81 unique bitmaps FAIL
            var bitmaps81 = bitmaps80.Concat([new byte[540]]).ToList();
            bitmaps81[80][0] = 0xFE;
            var overBitmaps = maxProj with { UniqueBitmaps = bitmaps81 };
            var overBmpRes = packer.Validate(overBitmaps, e400Screen);
            Assert.False(overBmpRes.IsValid);
            Assert.Contains(overBmpRes.Errors, e => e.Contains("Unique bitmap count", StringComparison.OrdinalIgnoreCase));

            // 3. 201 frames FAIL (even with 1 tiny bitmap)
            var frames201 = Enumerable.Range(0, 201).Select(_ => new NwmFrameEntry(0)).ToList();
            var overFrames = new NwmProjectDto(ScreensaverMagic.NwE400_0xEC01, "Over201", "Apollo", 30720, 12288, 120, 36, 1, frames201, [bitmaps80[0]]);
            var overFrmRes = packer.Validate(overFrames, e400Screen);
            Assert.False(overFrmRes.IsValid);
            Assert.Contains(overFrmRes.Errors, e => e.Contains("frame count", StringComparison.OrdinalIgnoreCase));

            // 4. Soft file-size warning on unverified/experimental ScreenSpecification
            var softSpec = e400Screen with { MaxFileSizeBytes = 1000, EnforceStrictFileSizeCap = false };
            var softProj = new NwmProjectDto(ScreensaverMagic.NwE400_0xEC01, "SoftCap", "Apollo", 30720, 12288, 120, 36, 1, [new NwmFrameEntry(0), new NwmFrameEntry(1)], bitmaps80.Take(2).ToList());
            var softRes = packer.Validate(softProj, softSpec);
            Assert.True(softRes.IsValid);
            Assert.NotNull(softRes.Warnings);
            Assert.NotEmpty(softRes.Warnings!);

            // 5. Pack >256 unique bitmaps error text shows (256)
            var bitmaps257 = Enumerable.Range(0, 257).Select(i =>
            {
                byte[] b = new byte[216];
                b[0] = (byte)(i & 0xFF);
                b[1] = (byte)((i >> 8) + 1);
                return b;
            }).ToList();
            var overflowProj = new NwmProjectDto(ScreensaverMagic.NwE002_0xEC02, "Over256", "Apollo", 30720, 12288, 16, 108, 1, [new NwmFrameEntry(0)], bitmaps257);
            var overflowEx = Assert.Throws<InvalidOperationException>(() => packer.Pack(overflowProj));
            Assert.Contains("(256)", overflowEx.Message);

            // 6. 0xD301 preserves custom 90-byte Thumbnail across Pack -> Unpack -> Pack
            byte[] customThumb = Enumerable.Range(0, 90).Select(i => (byte)((i * 7 + 3) & 0xFF)).ToArray();
            byte[] a1000Bmp = new byte[1600];
            a1000Bmp[0] = 0xAA;
            var d301Proj = new NwmProjectDto(ScreensaverMagic.NwA1000_0xD301, "ThumbTest", "SONY", 30720, 12288, 80, 80, 2, [new NwmFrameEntry(0)], [a1000Bmp], Thumbnail: customThumb);
            byte[] packedD301 = packer.Pack(d301Proj);
            var unpackedD301 = packer.Unpack(packedD301);
            Assert.NotNull(unpackedD301.Thumbnail);
            Assert.Equal(customThumb, unpackedD301.Thumbnail);
            Assert.Equal(packedD301, packer.Pack(unpackedD301));
        }
        finally
        {
            await orch.UnloadScopeAsync("test-scope");
        }
    }

    private sealed class FakeOpenMgIcvBridge : IOpenMgIcvBridge
    {
        private readonly string _deviceRoot;

        public FakeOpenMgIcvBridge(string deviceRoot) => _deviceRoot = deviceRoot;

        public IcvBridgeCapability ProbeCapability() => new(true, true, true, "Mock Ready");
        public HardwareIcvSnapshot? ParseHardwareIcvResponse(ReadOnlySpan<byte> response1028) => null;
        public byte[] BuildWriteHardwareIcvPayload(ReadOnlySpan<byte> icv168) => new byte[1028];
        public Task<HardwareIcvSnapshot?> ReadHardwareIcvAsync(ConnectedDeviceIdentity device, CancellationToken ct = default) =>
            Task.FromResult<HardwareIcvSnapshot?>(new HardwareIcvSnapshot(new byte[168], 1, 0x00010021u, [1, 2, 3, 4, 5, 6, 7, 8], [8, 7, 6, 5, 4, 3, 2, 1]));

        public Task<IcvCommitResult> MintAndCommitSlotsAsync(
            ConnectedDeviceIdentity device,
            IReadOnlyList<IcvMintTrackRequest> mintRequests,
            IReadOnlyList<int> deleteTrackIds,
            CancellationToken ct = default)
        {
            byte[] maclist = new byte[32768];
            var minted = new Dictionary<int, SignedMagicGateSlotTemplate>();
            foreach (var req in mintRequests)
            {
                byte[] mac8 = [0xAA, 0xBB, 0xCC, 0xDD, (byte)req.TrackId, 0x11, 0x22, 0x33];
                mac8.CopyTo(maclist.AsSpan((req.TrackId - 1) * 8, 8));
                byte[] ulinf = new byte[240];
                ulinf[0] = (byte)'K';
                minted[req.TrackId] = new SignedMagicGateSlotTemplate(
                    req.TrackId,
                    ulinf,
                    null,
                    null,
                    req.ContentId20,
                    [1, 2, 3, 4, 5, 6, 7, 8],
                    mac8);
            }

            File.WriteAllBytes(Path.Combine(_deviceRoot, "OMGAUDIO", "MACLIST0.DAT"), maclist);
            return Task.FromResult(new IcvCommitResult(true, maclist, new byte[168], minted, null));
        }
    }

    private sealed class DelegatingOrchestrator : IModuleOrchestrator
    {
        private readonly IModuleOrchestrator _inner;
        private readonly IOpenMgIcvBridge _bridgeOverride;

        public DelegatingOrchestrator(IModuleOrchestrator inner, IOpenMgIcvBridge bridgeOverride)
        {
            _inner = inner;
            _bridgeOverride = bridgeOverride;
        }

        public IReadOnlyCollection<string> LoadedModuleIds => _inner.LoadedModuleIds;
        public Task<IReadOnlyList<string>> HydrateForDeviceAsync(ConnectedDeviceIdentity device, CancellationToken ct = default) => _inner.HydrateForDeviceAsync(device, ct);
        public Task<IReadOnlyList<string>> HydrateForUnrecognizedDeviceAsync(DeviceDetectionProbe probe, CancellationToken ct = default) => _inner.HydrateForUnrecognizedDeviceAsync(probe, ct);
        public Task LoadModuleAsync(string moduleId, string scopeId, CancellationToken ct = default) => _inner.LoadModuleAsync(moduleId, scopeId, ct);
        public Task UnloadScopeAsync(string scopeId, CancellationToken ct = default) => _inner.UnloadScopeAsync(scopeId, ct);
        public TService? TryResolveEngine<TService>() where TService : class =>
            typeof(TService) == typeof(IOpenMgIcvBridge) ? (TService)(object)_bridgeOverride : _inner.TryResolveEngine<TService>();
        public TService ResolveRequiredEngine<TService>() where TService : class =>
            TryResolveEngine<TService>() ?? _inner.ResolveRequiredEngine<TService>();
    }
}
