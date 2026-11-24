using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.M0.Tests;

/// <summary>
/// Milestone M3 verification suite covering L2.Codec.Pipeline (MP3 pass-through, LAME transcode,
/// 96kHz/24-bit resampling, atracdenc ATRAC3, vgmstream decode, device-to-device re-keying),
/// collectible ALC unload with native libmp3lame, and L1.FileManager bounded parallel sync queue.
/// </summary>
public sealed class MilestoneM3CodecAndSyncTests
{
    [Fact]
    public async Task CodecPipeline_LoadsInCollectibleAlc_WithNativeLame_AndUnloadsCleanly()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m3_alc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string wavPath = Path.Combine(tempDir, "tone_44k.wav");
            string omaPath = Path.Combine(tempDir, "10000001.OMA");
            WriteSyntheticPcmWav(wavPath, sampleRateHz: 44100, bitsPerSample: 16, channels: 2, durationSeconds: 1.0);

            var bus = new WalkmanEventBus();
            var mediator = new WalkmanMediator();
            var orchestrator = new ModuleOrchestrator(
                bus,
                mediator,
                moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

            var weakRefs = await ExecuteCodecInAlcAndUnloadAsync(orchestrator, wavPath, omaPath);
            Assert.True(File.Exists(omaPath));
            Assert.True(new FileInfo(omaPath).Length > 1024);

            for (int attempt = 0; attempt < 20 && weakRefs.Any(w => w.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.All(weakRefs, w => Assert.False(w.IsAlive));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CodecPipeline_HandlesWav44k_HiRes96k24b_Mp3PassThrough_AndAtrac3Gate()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m3_matrix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m3-matrix");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m3-matrix");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();

            const uint deviceKey = TestEkbFixtureHelper.SyntheticDeviceKeyA;

            // 1. Standard 44.1kHz 16-bit WAV -> LAME MP3 -> 0xFFFE .OMA -> extract .mp3
            string wav44k = Path.Combine(tempDir, "src44k.wav");
            WriteSyntheticPcmWav(wav44k, 44100, 16, 2, 1.0);
            var tags44k = new Ea3TagMetadata("Tone 44k", "Apollo", "M3 Suite", "Synth", 2026, 1, 0);

            string oma44k = Path.Combine(tempDir, "10000001.OMA");
            var res44k = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wav44k, oma44k, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 1, deviceKey, tags44k));

            Assert.True(res44k.Success, res44k.ErrorMessage);
            Assert.True(res44k.FrameCount > 30);
            Assert.True(res44k.DurationMs >= 900);

            string extractedMp3 = Path.Combine(tempDir, "extracted.mp3");
            Assert.True(await codec.ExtractFromOmaAsync(oma44k, extractedMp3, deviceKey));
            var extractedTags = codec.ExtractMetadata(extractedMp3);
            Assert.Equal("Tone 44k", extractedTags.Title);
            Assert.Equal("Apollo", extractedTags.Artist);

            // 2. Hi-Res 96kHz 24-bit WAV -> resampled strictly to 44.1kHz -> 320k MP3 .OMA
            string wav96k = Path.Combine(tempDir, "hires96k24b.wav");
            WriteSyntheticPcmWav(wav96k, 96000, 24, 2, 1.0);
            string omaFromHiRes = Path.Combine(tempDir, "10000002.OMA");

            var resHiRes = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wav96k, omaFromHiRes, AudioCodecId.Mp3, 320, OmaEncryptionMode.HardwareXor_0xFFFE, 2, deviceKey,
                new Ea3TagMetadata("HiRes 96k", "Apollo", "M3 Suite", "Synth", 2026, 2, 0)));

            Assert.True(resHiRes.Success, resHiRes.ErrorMessage);
            string extractedHiResMp3 = Path.Combine(tempDir, "extracted_hires.mp3");
            Assert.True(await codec.ExtractFromOmaAsync(omaFromHiRes, extractedHiResMp3, deviceKey));
            var mp3Info = crypto.InspectMp3Payload(await File.ReadAllBytesAsync(extractedHiResMp3));
            Assert.Equal(44100, mp3Info.SampleRateHz);

            // 3. MP3 Pass-Through Wrapper (zero re-encoding: exact MPEG frame count preserved)
            var origMp3Metrics = crypto.InspectMp3Payload(await File.ReadAllBytesAsync(extractedMp3));
            string omaPassthrough = Path.Combine(tempDir, "10000003.OMA");
            var resPass = await codec.ProcessToOmaAsync(new TranscodeRequest(
                extractedMp3, omaPassthrough, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 3, deviceKey,
                new Ea3TagMetadata("Passthrough MP3", "Apollo", "M3 Suite", "Synth", 2026, 3, 0)));

            Assert.True(resPass.Success, resPass.ErrorMessage);
            Assert.Equal(origMp3Metrics.FrameCount, resPass.FrameCount);

            // 4. Native ATRAC3plus encoding now succeeds and decodes cleanly via vgmstream
            string omaAtrac3Plus = Path.Combine(tempDir, "native_at3p.oma");
            var resAt3p = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wav44k, omaAtrac3Plus, AudioCodecId.Atrac3Plus, 256, OmaEncryptionMode.Unprotected_0xFFFF, 4, null, tags44k));
            Assert.True(resAt3p.Success, resAt3p.ErrorMessage);

            string decodedAt3pWav = Path.Combine(tempDir, "native_at3p.wav");
            Assert.True(await codec.ExtractFromOmaAsync(omaAtrac3Plus, decodedAt3pWav, null));
            Assert.True(new FileInfo(decodedAt3pWav).Length > 100_000);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m3-matrix");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CodecPipeline_Atrac3EncodeViaAtracdenc_AndDecodeViaVgmstream()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m3_atrac3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m3-atrac3");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m3-atrac3");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();

            string wavSrc = Path.Combine(tempDir, "src.wav");
            WriteSyntheticPcmWav(wavSrc, 44100, 16, 2, 1.0);

            string omaAtrac3 = Path.Combine(tempDir, "10000001.OMA");
            var result = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavSrc, omaAtrac3, AudioCodecId.Atrac3, 132, OmaEncryptionMode.Unprotected_0xFFFF, 1, null,
                new Ea3TagMetadata("ATRAC3 LP2 Test", "Sony", "NetMD", "Test", 2026, 1, 0)));

            Assert.True(result.Success, result.ErrorMessage);
            var parsed = crypto.ParseHeader(await File.ReadAllBytesAsync(omaAtrac3));
            Assert.Equal(AudioCodecId.Atrac3, parsed.CodecId);
            Assert.Equal("ATRAC3 LP2 Test", parsed.Tags.Title);
            Assert.True(parsed.FrameCount > 30);

            string decodedWav = Path.Combine(tempDir, "decoded_vgm.wav");
            Assert.True(await codec.ExtractFromOmaAsync(omaAtrac3, decodedWav, null));
            Assert.True(File.Exists(decodedWav));
            Assert.True(new FileInfo(decodedWav).Length > 100_000);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m3-atrac3");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CodecPipeline_RekeyOma_TransfersBetweenTwoDevicesWithoutReencoding()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m3_rekey_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m3-rekey");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m3-rekey");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            const uint keyA = TestEkbFixtureHelper.SyntheticDeviceKeyA;
            const uint keyB = 0xCAFEBABEu;

            string wavSrc = Path.Combine(tempDir, "src.wav");
            WriteSyntheticPcmWav(wavSrc, 44100, 16, 2, 1.0);

            string playerAOma = Path.Combine(tempDir, "10000001.OMA");
            var resA = await codec.ProcessToOmaAsync(new TranscodeRequest(
                wavSrc, playerAOma, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 1, keyA,
                new Ea3TagMetadata("Device Transfer", "Apollo", "M3", "Synth", 2026, 1, 0)));
            Assert.True(resA.Success);

            string playerBOma = Path.Combine(tempDir, "10000005.OMA");
            var resB = await codec.RekeyOmaAsync(playerAOma, playerBOma, 5, keyA, keyB, OmaEncryptionMode.HardwareXor_0xFFFE);
            Assert.True(resB.Success, resB.ErrorMessage);
            Assert.Equal(resA.FrameCount, resB.FrameCount);

            string mp3FromA = Path.Combine(tempDir, "fromA.mp3");
            string mp3FromB = Path.Combine(tempDir, "fromB.mp3");
            Assert.True(await codec.ExtractFromOmaAsync(playerAOma, mp3FromA, keyA));
            Assert.True(await codec.ExtractFromOmaAsync(playerBOma, mp3FromB, keyB));

            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var infoA = crypto.InspectMp3Payload(await File.ReadAllBytesAsync(mp3FromA));
            var infoB = crypto.InspectMp3Payload(await File.ReadAllBytesAsync(mp3FromB));
            Assert.Equal(infoA.CleanPayloadLength, infoB.CleanPayloadLength);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m3-rekey");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FileManager_BoundedSyncQueue_TransfersBatchAndCommitsDatabaseAtomically()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Live_Backup"));
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_m3_sync_{Guid.NewGuid():N}");
        var sourceMediaDir = Path.Combine(Path.GetTempPath(), $"apollo_m3_media_{Guid.NewGuid():N}");
        CopyDirectoryRecursive(backupRoot, sandboxRoot);
        Directory.CreateDirectory(sourceMediaDir);

        var poller = new DevicePollerCoordinator(registry);
        var outcome = poller.InspectFixtureVolume(
            sandboxRoot,
            vendorId: 0x054C,
            productId: 0x01FB,
            usbProductString: "SONY NWWM MEM AAD2 USB Device",
            overrideTotalBytes: 255_492_096,
            overrideVolumeSerial: null);

        var device = outcome.Resolution.Identity! with { VolumeSerialNumber = null };

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var progressEvents = new List<TransferProgressEvent>();
        using var sub = bus.Subscribe<TransferProgressEvent>(e =>
        {
            lock (progressEvents) { progressEvents.Add(e); }
        });

        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            string wav1 = Path.Combine(sourceMediaDir, "batch1.wav");
            string wav2 = Path.Combine(sourceMediaDir, "batch2_96k.wav");
            string wav3 = Path.Combine(sourceMediaDir, "batch3.wav");
            WriteSyntheticPcmWav(wav1, 44100, 16, 2, 0.8);
            WriteSyntheticPcmWav(wav2, 96000, 24, 2, 0.8);
            WriteSyntheticPcmWav(wav3, 44100, 16, 2, 0.8);

            var fileManager = new FileManagerCoordinator(orchestrator, bus);
            var batch = new SyncTrackRequest[]
            {
                new(wav1, AudioCodecId.Mp3, 192, "Smtng else", new Ea3TagMetadata("Batch One", "Apollo", "Sync", "Synth", 2026, 1, 0)),
                new(wav2, AudioCodecId.Mp3, 320, "Smtng else", new Ea3TagMetadata("Batch Two 96k", "Apollo", "Sync", "Synth", 2026, 2, 0)),
                new(wav3, AudioCodecId.Mp3, 128, "Electronic", new Ea3TagMetadata("Batch Three", "Apollo", "Sync", "Synth", 2026, 3, 0))
            };

            var result = await fileManager.SyncTracksToDeviceAsync(device, batch);

            Assert.Equal(3, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(8, progressEvents.Count);
            Assert.Equal(100.0, progressEvents[^1].PercentComplete, 2);

            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var reloaded = await db.ReadDatabaseAsync(device);
            Assert.Equal(27, reloaded.Tracks.Count); // 24 existing + 3 synced
            Assert.Contains(reloaded.Tracks, t => t.TrackId == 25 && t.Title == "Batch One");
            Assert.Contains(reloaded.Tracks, t => t.TrackId == 26 && t.Title == "Batch Two 96k");
            Assert.Contains(reloaded.Tracks, t => t.TrackId == 27 && t.Title == "Batch Three");
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandboxRoot)) Directory.Delete(sandboxRoot, recursive: true);
            if (Directory.Exists(sourceMediaDir)) Directory.Delete(sourceMediaDir, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExecuteCodecInAlcAndUnloadAsync(
        ModuleOrchestrator orchestrator,
        string wavPath,
        string omaPath)
    {
        const string scopeId = "m3-alc-test";
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, scopeId);
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, scopeId);

        var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
        var res = await codec.ProcessToOmaAsync(new TranscodeRequest(
            wavPath, omaPath, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 1, TestEkbFixtureHelper.SyntheticDeviceKeyA,
            new Ea3TagMetadata("ALC Test", "Apollo", "Test", "Test", 2026, 1, 0)));

        Assert.True(res.Success, res.ErrorMessage);

        var weakRefs = orchestrator.GetScopeContextWeakReferences(scopeId).ToArray();
        Assert.Equal(2, weakRefs.Length);

        await orchestrator.UnloadScopeAsync(scopeId);
        return weakRefs;
    }

    private static void WriteSyntheticPcmWav(
        string filePath,
        int sampleRateHz,
        short bitsPerSample,
        short channels,
        double durationSeconds)
    {
        int sampleCount = (int)(sampleRateHz * durationSeconds);
        int blockAlign = channels * (bitsPerSample / 8);
        int dataBytes = sampleCount * blockAlign;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header.Slice(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 36 + dataBytes);
        "WAVE"u8.CopyTo(header.Slice(8, 4));
        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), sampleRateHz * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        "data"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataBytes);
        fs.Write(header);

        byte[] pcm = new byte[dataBytes];
        int bytesPerSample = bitsPerSample / 8;
        for (int i = 0; i < sampleCount; i++)
        {
            double val = Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRateHz) * 0.25;
            for (int c = 0; c < channels; c++)
            {
                int pos = (i * channels + c) * bytesPerSample;
                if (bitsPerSample == 16)
                {
                    short s = (short)(val * short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(pos, 2), s);
                }
                else if (bitsPerSample == 24)
                {
                    int s24 = (int)(val * 8388607.0);
                    pcm[pos] = (byte)(s24 & 0xFF);
                    pcm[pos + 1] = (byte)((s24 >> 8) & 0xFF);
                    pcm[pos + 2] = (byte)((s24 >> 16) & 0xFF);
                }
            }
        }

        fs.Write(pcm);
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

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(MilestoneM3CodecAndSyncTests)
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
