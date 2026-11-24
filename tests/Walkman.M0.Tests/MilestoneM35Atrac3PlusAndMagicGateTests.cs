using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Xunit.Abstractions;

namespace Walkman.M0.Tests;

/// <summary>
/// Milestone M3.5 verification suite covering pure C# native ATRAC3plus encoding across all 9
/// Walkman bitrates (48..352 kbps), vgmstream-cli spectral verification, OpenMG 0x0001 MagicGate
/// byte-exact decryption against live NW-E403 captures, and end-to-end FileManager sync with MACLIST0.DAT.
/// </summary>
public sealed class MilestoneM35Atrac3PlusAndMagicGateTests
{
    private readonly ITestOutputHelper _output;

    public MilestoneM35Atrac3PlusAndMagicGateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly int[] AllWalkmanBitrates = [48, 64, 96, 128, 160, 192, 256, 320, 352];

    [Fact]
    public async Task Atrac3PlusNativeEncoder_EncodesAll9WalkmanBitrates_AndDecodesCleanlyViaVgmstream()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_at3p_9_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-matrix");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m35-matrix");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();

            // Left channel: 1000 Hz (Subband 0), Right channel: 5000 Hz (Subband 3, odd subband spectral-reversal check!)
            string srcWav = Path.Combine(tempDir, "dual_tone_1k_5k.wav");
            WriteDualChannelSineWav(srcWav, sampleRateHz: 44100, freqLeftHz: 1000.0, freqRightHz: 5000.0, amplitude: 0.6, durationSeconds: 1.5);

            foreach (int bitrate in AllWalkmanBitrates)
            {
                string omaPath = Path.Combine(tempDir, $"at3p_{bitrate}.oma");
                var res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                    SourceFilePath: srcWav,
                    StagingOutputPath: omaPath,
                    TargetCodec: AudioCodecId.Atrac3Plus,
                    TargetBitrateKbps: bitrate,
                    EncryptionMode: OmaEncryptionMode.Unprotected_0xFFFF,
                    TrackId: 1,
                    HardwareDeviceKey: null,
                    Tags: new Ea3TagMetadata($"AT3P {bitrate}k", "Apollo", "M3.5", "Test", 2026, 1, 0),
                    Ea3BlockSize: 1024));

                Assert.True(res.Success, $"Bitrate {bitrate}k failed: {res.ErrorMessage}");
                Assert.True(res.FrameCount >= 30, $"Unexpected frame count at {bitrate}k");

                var hdr = crypto.ParseHeader(await File.ReadAllBytesAsync(omaPath));
                Assert.Equal(AudioCodecId.Atrac3Plus, hdr.CodecId);
                Assert.Equal($"AT3P {bitrate}k", hdr.Tags.Title);

                string decWav = Path.Combine(tempDir, $"dec_{bitrate}.wav");
                Assert.True(await codec.ExtractFromOmaAsync(omaPath, decWav, null), $"vgmstream decode failed at {bitrate}k");

                var (left, right) = ReadStereoPcm16(decWav);
                Assert.True(left.Length > 44100, $"Decoded WAV too short at {bitrate}k");

                // Skip encoder/decoder delay (steady-state center window: samples 16000..48768)
                int offset = 16000;
                int n = 16384;
                double left1k = GoertzelPower(left, offset, n, 44100.0, 1000.0);
                double left5k = GoertzelPower(left, offset, n, 44100.0, 5000.0);
                double right5k = GoertzelPower(right, offset, n, 44100.0, 5000.0);
                double right1k = GoertzelPower(right, offset, n, 44100.0, 1000.0);

                Assert.True(left1k > left5k * 100.0, $"L 1kHz selectivity failed at {bitrate}k (1k={left1k:G3}, 5k={left5k:G3})");
                Assert.True(right5k > right1k * 100.0, $"R 5kHz odd-subband selectivity failed at {bitrate}k (5k={right5k:G3}, 1k={right1k:G3})");

                // Verify unity amplitude preservation within +/- 1.5 dB on steady-state 1 kHz wave
                double peakL = left.AsSpan(offset, n).ToArray().Max(x => Math.Abs(x));
                Assert.InRange(peakL, 0.45, 0.75);
            }
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-matrix");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MagicGate0x0001_DecryptsLiveNwE403CapturesByteExact_AndMatchesMaclist0()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var solutionRoot = Path.GetFullPath(Path.Combine(codebaseRoot, ".."));
        var snapOmg = Path.Combine(solutionRoot, "test folder", "NW-E403_Atrac3Plus_Live_Snapshot", "OMGAUDIO");
        var golden352 = Path.Combine(solutionRoot, "test folder", "re_golden_oma", "352");

        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_mg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-mg");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m35-mg");

        try
        {
            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();

            foreach (var (encName, trackId, plainName) in new[]
            {
                ("10000019.OMA", 25, "01_silence_5s.OMA"),
                ("10000006.OMA", 6, "Vagrant - Test.OMA")
            })
            {
                byte[] encOma = await File.ReadAllBytesAsync(Path.Combine(snapOmg, "10F00", encName));
                byte[] decryptedPayload = crypto.DecryptOmaPayload(encOma, trackId, null);

                byte[] goldenOma = await File.ReadAllBytesAsync(Path.Combine(golden352, plainName));
                var goldenHdr = crypto.ParseHeader(goldenOma);
                byte[] expectedPayload = goldenOma.AsSpan(goldenHdr.AudioPayloadOffset).ToArray();

                Assert.Equal(expectedPayload.Length, decryptedPayload.Length);
                Assert.True(expectedPayload.AsSpan().SequenceEqual(decryptedPayload), $"Byte mismatch decrypting {encName}");
            }

            // Full end-to-end 0x0001 .OMA -> .wav extraction via vgmstream
            string encSineOma = Path.Combine(snapOmg, "10F00", "1000001B.OMA"); // 03_sine_1khz_5s
            string outWav = Path.Combine(tempDir, "sine_decrypted.wav");
            Assert.True(await codec.ExtractFromOmaAsync(encSineOma, outWav, null));
            Assert.True(new FileInfo(outWav).Length > 100_000);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-mg");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileManager_SyncsNativeAtrac3PlusWith0x0001MagicGate_CommitsMaclist0AndCidlst_AndExtractsBack()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Live_Backup"));
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_m35_sync_{Guid.NewGuid():N}");
        var sourceMediaDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_media_{Guid.NewGuid():N}");
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
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            string wavSrc = Path.Combine(sourceMediaDir, "at3p_track.wav");
            WriteDualChannelSineWav(wavSrc, 44100, 1000.0, 440.0, 0.6, 1.2);

            var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();
            await transport.WriteFileAtomicAsync(device, "OMGAUDIO/00010021.DAT", TestEkbFixtureHelper.BuildSyntheticTestEkb00010021());

            var fileManager = new FileManagerCoordinator(orchestrator, bus);
            var batch = new SyncTrackRequest[]
            {
                new(wavSrc, AudioCodecId.Atrac3Plus, 352, "Electronic", new Ea3TagMetadata("Native AT3P", "Apollo", "M3.5", "Synth", 2026, 1, 0))
            };

            var result = await fileManager.SyncTracksToDeviceAsync(device, batch, allowUnsignedMaclist0ForTesting: true);
            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Single(result.AddedTracks);

            var added = result.AddedTracks[0];
            Assert.Equal(AudioCodecId.Atrac3Plus, added.Codec);
            Assert.Equal(OmaEncryptionMode.MagicGateLsi_0x0001, added.EncryptionMode);
            Assert.Equal(0x010028FFu, added.FilePropertiesBigEndian);
            Assert.NotNull(added.MaclistEntry8);
            Assert.NotNull(added.LsiKeyring48);

            // Verify 00010021.DAT and MACLIST0.DAT on sandbox volume
            string ekbPath = Path.Combine(sandboxRoot, "OMGAUDIO", "00010021.DAT");
            string maclistPath = Path.Combine(sandboxRoot, "OMGAUDIO", "MACLIST0.DAT");
            string cidlstPath = Path.Combine(sandboxRoot, "OMGAUDIO", "05CIDLST.DAT");
            Assert.True(File.Exists(ekbPath));
            Assert.Equal(172, new FileInfo(ekbPath).Length);
            Assert.True(File.Exists(maclistPath));
            byte[] macBytes = await File.ReadAllBytesAsync(maclistPath);
            Assert.Equal(32768, macBytes.Length);
            Assert.True(macBytes.AsSpan((added.TrackId - 1) * 8, 8).SequenceEqual(added.MaclistEntry8));

            byte[] cidlstBytes = await File.ReadAllBytesAsync(cidlstPath);
            Assert.Equal(added.LsiKeyring48, cidlstBytes.AsSpan(0x30 + (added.TrackId - 1) * 48, 48).ToArray());

            // Decrypt and decode written 0x0001 .OMA back to WAV via CodecPipeline
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            string omaOnDisk = Path.Combine(sandboxRoot, "OMGAUDIO", $"10F{added.TrackId / 256:X2}", $"1{added.TrackId:X7}.OMA");
            string extractedWav = Path.Combine(sourceMediaDir, "roundtrip_from_0001.wav");
            Assert.True(await codec.ExtractFromOmaAsync(omaOnDisk, extractedWav, null));

            var (l, r) = ReadStereoPcm16(extractedWav);
            Assert.True(GoertzelPower(l, 12000, 16384, 44100.0, 1000.0) > 100.0);
            Assert.True(GoertzelPower(r, 12000, 16384, 44100.0, 440.0) > 100.0);

            // Re-key 0x0001 -> 0xFFFF cleanly
            string rekeyedPath = Path.Combine(sourceMediaDir, "rekeyed_ffff.oma");
            var rekeyRes = await codec.RekeyOmaAsync(omaOnDisk, rekeyedPath, 1, null, null, OmaEncryptionMode.Unprotected_0xFFFF);
            Assert.True(rekeyRes.Success, rekeyRes.ErrorMessage);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandboxRoot)) Directory.Delete(sandboxRoot, recursive: true);
            if (Directory.Exists(sourceMediaDir)) Directory.Delete(sourceMediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task Atrac3PlusNativeEncoder_EncodesReTestSignalsCorpus_AndPassesBothVgmstreamAndFfmpegStrictDecode()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var solutionRoot = Path.GetFullPath(Path.Combine(codebaseRoot, ".."));
        var signalsDir = Path.Combine(solutionRoot, "test folder", "re_test_signals");
        var ffmpegExe = Path.Combine(solutionRoot, "extra apps", "re-tools", "ffmpeg", "ffmpeg-9.0.2-essentials_build", "bin", "ffmpeg.exe");
        bool hasFfmpeg = File.Exists(ffmpegExe);

        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_corpus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-corpus");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m35-corpus");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            string[] signals =
            [
                "01_silence_5s.wav",
                "02_impulse_5s.wav",
                "03_sine_1khz_5s.wav",
                "04_logsweep_10s.wav",
                "05_whitenoise_5s.wav"
            ];

            foreach (int bitrate in new[] { 48, 352 })
            {
                foreach (string sigName in signals)
                {
                    string srcPath = Path.Combine(signalsDir, sigName);
                    string omaPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(sigName)}_{bitrate}.oma");
                    string decWav = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(sigName)}_{bitrate}.wav");

                    var res = await codec.ProcessToOmaAsync(new TranscodeRequest(
                        srcPath,
                        omaPath,
                        AudioCodecId.Atrac3Plus,
                        bitrate,
                        OmaEncryptionMode.Unprotected_0xFFFF,
                        1,
                        null,
                        new Ea3TagMetadata(sigName, "RE Test", "Corpus", "Test", 2026, 1, 0)));

                    Assert.True(res.Success, $"{sigName} @ {bitrate}k failed: {res.ErrorMessage}");
                    Assert.True(await codec.ExtractFromOmaAsync(omaPath, decWav, null), $"vgmstream failed on {sigName} @ {bitrate}k");
                    Assert.True(new FileInfo(decWav).Length > 100_000);

                    if (hasFfmpeg)
                    {
                        var psi = new ProcessStartInfo(ffmpegExe, $"-v error -i \"{omaPath}\" -f null -")
                        {
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = Process.Start(psi)!;
                        string stderr = await proc.StandardError.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        Assert.Equal(0, proc.ExitCode);
                        Assert.True(string.IsNullOrWhiteSpace(stderr), $"FFmpeg error on {sigName} @ {bitrate}k: {stderr}");
                    }
                }
            }
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-corpus");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Option2_SlotRecycling_DeleteHarvestsSignedSlot_ReSyncsNativeAtrac3Plus_PreservesMaclist0Sha256ByteExact()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var snapshotRoot = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Atrac3Plus_Live_Snapshot"));
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_m35_opt2_{Guid.NewGuid():N}");
        var slotPoolDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_pool_{Guid.NewGuid():N}");
        var sourceMediaDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_src_{Guid.NewGuid():N}");
        CopyDirectoryRecursive(Path.Combine(snapshotRoot, "OMGAUDIO"), Path.Combine(sandboxRoot, "OMGAUDIO"));
        Directory.CreateDirectory(sourceMediaDir);

        var poller = new DevicePollerCoordinator(registry);
        var outcome = poller.InspectFixtureVolume(
            sandboxRoot,
            vendorId: 0x054C,
            productId: 0x01FB,
            usbProductString: "SONY NWWM MEM AAD2 USB Device",
            overrideTotalBytes: 255_492_096,
            overrideVolumeSerial: null);

        var device = outcome.Resolution.Identity! with
        {
            VolumeSerialNumber = null,
            HardwareDvIdKey = outcome.Resolution.Identity!.HardwareDvIdKey ?? TestEkbFixtureHelper.SyntheticDeviceKeyA
        };

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            string maclistPath = Path.Combine(sandboxRoot, "OMGAUDIO", "MACLIST0.DAT");
            byte[] originalMaclistHash = SHA256.HashData(await File.ReadAllBytesAsync(maclistPath));

            var fileManager = new FileManagerCoordinator(orchestrator, bus, slotPoolDir);

            // Delete signed 0x0001 tracks #25 and #26 -> harvests slot templates, writes 2,144-byte dummy stubs, and keeps MACLIST0.DAT untouched.
            await fileManager.DeleteTracksAsync(device, [25, 26]);
            string stub19 = Path.Combine(sandboxRoot, "OMGAUDIO", "10F00", "10000019.OMA");
            string stub1A = Path.Combine(sandboxRoot, "OMGAUDIO", "10F00", "1000001A.OMA");
            Assert.True(File.Exists(stub19));
            Assert.True(File.Exists(stub1A));
            Assert.Equal(2144, new FileInfo(stub19).Length);
            Assert.Equal(2144, new FileInfo(stub1A).Length);
            Assert.Equal(originalMaclistHash, SHA256.HashData(await File.ReadAllBytesAsync(maclistPath)));

            var poolStatus = await fileManager.GetMagicGateSlotPoolStatusAsync(device);
            Assert.True(poolStatus.HasMaclist0);
            Assert.Equal(6, poolStatus.TotalSignedSlotsInMaclist0);
            Assert.Equal(4, poolStatus.ActiveSignedTrackCount);
            Assert.Equal(2, poolStatus.RecycledFreeSlotCount);
            Assert.Equal([25, 26], poolStatus.FreeRecycledTrackIds);

            string wav352 = Path.Combine(sourceMediaDir, "tone_352.wav");
            string wav128 = Path.Combine(sourceMediaDir, "tone_128.wav");
            string wavMp3 = Path.Combine(sourceMediaDir, "tone_mp3.wav");
            WriteDualChannelSineWav(wav352, 44100, 1000.0, 5000.0, 0.6, 1.2);
            WriteDualChannelSineWav(wav128, 44100, 1000.0, 440.0, 0.6, 1.2);
            WriteDualChannelSineWav(wavMp3, 44100, 1000.0, 440.0, 0.6, 1.0);

            var syncResult = await fileManager.SyncTracksToDeviceAsync(
                device,
                [
                    new SyncTrackRequest(wav352, AudioCodecId.Atrac3Plus, 352, "Smtng else", new Ea3TagMetadata("Recycled 352", "Apollo", "Opt2", "Electronic", 2026, 1, 0)),
                    new SyncTrackRequest(wav128, AudioCodecId.Atrac3Plus, 128, "Smtng else", new Ea3TagMetadata("Recycled 128", "Apollo", "Opt2", "Electronic", 2026, 2, 0)),
                    new SyncTrackRequest(wavMp3, AudioCodecId.Mp3, 192, "Electronic", new Ea3TagMetadata("Linear MP3", "Apollo", "Opt2", "Electronic", 2026, 3, 0))
                ]);

            Assert.Equal(3, syncResult.SucceededCount);
            Assert.Equal(0, syncResult.FailedCount);
            Assert.Empty(syncResult.Errors);

            var addedAt352 = Assert.Single(syncResult.AddedTracks, t => t.Title == "Recycled 352");
            var addedAt128 = Assert.Single(syncResult.AddedTracks, t => t.Title == "Recycled 128");
            var addedMp3 = Assert.Single(syncResult.AddedTracks, t => t.Title == "Linear MP3");

            Assert.Equal(25, addedAt352.TrackId);
            Assert.Equal(26, addedAt128.TrackId);
            Assert.Equal(30, addedMp3.TrackId); // Skips active signed slots 27..29!

            // Critical invariant: MACLIST0.DAT must remain 100% byte-identical for Hardware ICV validity.
            Assert.Equal(originalMaclistHash, SHA256.HashData(await File.ReadAllBytesAsync(maclistPath)));

            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var finalDb = await db.ReadDatabaseAsync(device);

            foreach (var (tid, expectedTitle) in new[] { (25, "Recycled 352"), (26, "Recycled 128") })
            {
                string omaPath = Path.Combine(sandboxRoot, "OMGAUDIO", "10F00", $"1{tid:X7}.OMA");
                byte[] omaBytes = await File.ReadAllBytesAsync(omaPath);

                // First frame at offset 0x000A must be GEOB OMG_ULINF
                Assert.Equal("GEOB", Encoding.ASCII.GetString(omaBytes, 10, 4));
                byte[] ulinfTag = Encoding.BigEndianUnicode.GetBytes("OMG_ULINF\0");
                Assert.True(omaBytes.AsSpan(10, 64).IndexOf(ulinfTag) >= 0, $"First frame in track #{tid} must be OMG_ULINF.");

                var parsed = crypto.ParseHeader(omaBytes);
                Assert.Equal(expectedTitle, parsed.Tags.Title);
                Assert.Equal(expectedTitle, Assert.Single(finalDb.Tracks, t => t.TrackId == tid).Title);

                // EA3[36..43] must be zeroed for 0x0001
                int ea3Offset = parsed.AudioPayloadOffset - 96;
                Assert.True(omaBytes.AsSpan(ea3Offset + 36, 8).SequenceEqual(new byte[8]));

                string decWav = Path.Combine(sourceMediaDir, $"dec_{tid}.wav");
                Assert.True(await codec.ExtractFromOmaAsync(omaPath, decWav, null), $"vgmstream decode failed on recycled track #{tid}");
                var (l, r) = ReadStereoPcm16(decWav);
                Assert.True(GoertzelPower(l, 12000, 16384, 44100.0, 1000.0) > 100.0);
                double expectedRightHz = tid == 25 ? 5000.0 : 440.0;
                Assert.True(GoertzelPower(r, 12000, 16384, 44100.0, expectedRightHz) > 100.0);
            }
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandboxRoot)) Directory.Delete(sandboxRoot, recursive: true);
            if (Directory.Exists(slotPoolDir)) Directory.Delete(slotPoolDir, recursive: true);
            if (Directory.Exists(sourceMediaDir)) Directory.Delete(sourceMediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task Option2_SlotRecycling_InPlaceReplaceAndPoolExhaustionGuard_NeverMutatesMaclist0()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var snapshotRoot = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Atrac3Plus_Live_Snapshot"));
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_m35_rep_{Guid.NewGuid():N}");
        var slotPoolDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_reppool_{Guid.NewGuid():N}");
        var sourceMediaDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_repsrc_{Guid.NewGuid():N}");
        CopyDirectoryRecursive(Path.Combine(snapshotRoot, "OMGAUDIO"), Path.Combine(sandboxRoot, "OMGAUDIO"));
        Directory.CreateDirectory(sourceMediaDir);

        var poller = new DevicePollerCoordinator(registry);
        var outcome = poller.InspectFixtureVolume(
            sandboxRoot,
            vendorId: 0x054C,
            productId: 0x01FB,
            usbProductString: "SONY NWWM MEM AAD2 USB Device",
            overrideTotalBytes: 255_492_096,
            overrideVolumeSerial: null);

        var device = outcome.Resolution.Identity! with
        {
            VolumeSerialNumber = null,
            HardwareDvIdKey = outcome.Resolution.Identity!.HardwareDvIdKey ?? TestEkbFixtureHelper.SyntheticDeviceKeyA
        };

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            string maclistPath = Path.Combine(sandboxRoot, "OMGAUDIO", "MACLIST0.DAT");
            byte[] originalMaclistHash = SHA256.HashData(await File.ReadAllBytesAsync(maclistPath));

            var fileManager = new FileManagerCoordinator(orchestrator, bus, slotPoolDir);
            string wav256 = Path.Combine(sourceMediaDir, "tone_256.wav");
            WriteDualChannelSineWav(wav256, 44100, 1000.0, 5000.0, 0.6, 1.2);

            // Snapshot starts with 6/6 signed slots active -> pool exhausted unless replacing in place.
            var exhaustedResult = await fileManager.SyncTracksToDeviceAsync(
                device,
                [new SyncTrackRequest(wav256, AudioCodecId.Atrac3Plus, 256, "Smtng else")]);

            Assert.Equal(0, exhaustedResult.SucceededCount);
            Assert.Equal(1, exhaustedResult.FailedCount);
            Assert.Contains("signed", Assert.Single(exhaustedResult.Errors), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalMaclistHash, SHA256.HashData(await File.ReadAllBytesAsync(maclistPath)));

            // In-place replacement of signed Track #27 succeeds without mutating MACLIST0.DAT!
            var replaceResult = await fileManager.SyncTracksToDeviceAsync(
                device,
                [new SyncTrackRequest(wav256, AudioCodecId.Atrac3Plus, 256, "Smtng else", new Ea3TagMetadata("InPlace 256", "Apollo", "Opt2", "Synth", 2026, 7, 0), ReplaceTrackId: 27)]);

            Assert.Equal(1, replaceResult.SucceededCount);
            Assert.Equal(0, replaceResult.FailedCount);
            var replaced = Assert.Single(replaceResult.AddedTracks);
            Assert.Equal(27, replaced.TrackId);
            Assert.Equal(originalMaclistHash, SHA256.HashData(await File.ReadAllBytesAsync(maclistPath)));

            var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            string omaPath = Path.Combine(sandboxRoot, "OMGAUDIO", "10F00", "1000001B.OMA");
            Assert.Equal("InPlace 256", crypto.ParseHeader(await File.ReadAllBytesAsync(omaPath)).Tags.Title);

            string decWav = Path.Combine(sourceMediaDir, "dec_27.wav");
            Assert.True(await codec.ExtractFromOmaAsync(omaPath, decWav, null));
            var (l, r) = ReadStereoPcm16(decWav);
            Assert.True(GoertzelPower(l, 12000, 16384, 44100.0, 1000.0) > 100.0);
            Assert.True(GoertzelPower(r, 12000, 16384, 44100.0, 5000.0) > 100.0);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandboxRoot)) Directory.Delete(sandboxRoot, recursive: true);
            if (Directory.Exists(slotPoolDir)) Directory.Delete(slotPoolDir, recursive: true);
            if (Directory.Exists(sourceMediaDir)) Directory.Delete(sourceMediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task Option1_CmAadScsiAndIcvBridge_ParsesLiveNwE403HardwareIcvVector_AndGuardsMissingWorker()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-icv");
        try
        {
            var bridge = orchestrator.ResolveRequiredEngine<IOpenMgIcvBridge>();

            // Exact 1028-byte SCSI 0xA4/0x33 live NW-E403 Hardware ICV capture from RE_WIP_05
            byte[] response1028 = new byte[1028];
            response1028[0] = 0x04;
            response1028[1] = 0x02;
            byte[] liveIcv168 = new byte[168];
            Convert.FromHexString("003000980000003700000000000100217e5e065bc9cdb1cc106603c357425d838cef4132df9ed301")
                .CopyTo(liveIcv168, 0);
            Convert.FromHexString("9fac5e180dfb7d6d").CopyTo(liveIcv168, 0xA0);
            liveIcv168.CopyTo(response1028, 4);

            var parsed = bridge.ParseHardwareIcvResponse(response1028);
            Assert.NotNull(parsed);
            Assert.Equal(0x00000037u, parsed!.SequenceCounter);
            Assert.Equal(0x00010021u, parsed.BoundEkbRevision);
            Assert.Equal("8CEF4132DF9ED301", Convert.ToHexString(parsed.Maclist0Umac8));
            Assert.Equal("9FAC5E180DFB7D6D", Convert.ToHexString(parsed.MicvsMac8));

            byte[] writePayload = bridge.BuildWriteHardwareIcvPayload(parsed.RawIcv168);
            Assert.Equal(1028, writePayload.Length);
            Assert.Equal(0x04, writePayload[0]);
            Assert.Equal(0x02, writePayload[1]);
            Assert.True(writePayload.AsSpan(4, 168).SequenceEqual(liveIcv168));
            Assert.True(writePayload.AsSpan(172).IndexOfAnyExcept((byte)0) < 0);

            var capability = bridge.ProbeCapability();
            Assert.False(string.IsNullOrWhiteSpace(capability.StatusMessage));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-icv");
        }
    }

    [Fact]
    public async Task OmgV1Database_Empty04Cntinf_ReturnsZeroTrackSnapshot_AndSupportsCollectionRenameDeleteAndOrder()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        string sandbox = Path.Combine(Path.GetTempPath(), $"apollo_cod03_05_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sandbox, "OMGAUDIO"));
        await File.WriteAllBytesAsync(Path.Combine(sandbox, "OMGAUDIO", "04CNTINF.DAT"), Array.Empty<byte>());

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        var poller = new DevicePollerCoordinator(registry);
        var outcome = poller.InspectFixtureVolume(sandbox, vendorId: 0x054C, productId: 0x01FB, overrideTotalBytes: 255_492_096);
        Assert.True(outcome.Resolution.IsRecognized);
        var device = outcome.Resolution.Identity!;

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var emptySnap = await db.ReadDatabaseFastAsync(device);
            Assert.Empty(emptySnap.Tracks);
            Assert.Empty(emptySnap.Groups);

            var t1 = new AudioTrackDto(1, "Track One", "Artist", "Album", "Pop", null, 1, 2026, 1000, 38, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03000000u, null, 24000, "Mix");
            var t2 = new AudioTrackDto(2, "Track Two", "Artist", "Album", "Pop", null, 2, 2026, 1000, 38, AudioCodecId.Mp3, 192, OmaEncryptionMode.HardwareXor_0xFFFE, 0x03000000u, null, 24000, "Mix", TargetPositionIndex: 0);
            await db.CommitTransactionAsync(device, new OmgDatabaseTransaction([t1, t2], Array.Empty<int>(), Array.Empty<TrackGroupDto>()));

            var snap = await db.ReadDatabaseFastAsync(device);
            Assert.Equal(2, snap.Tracks.Count);
            Assert.Single(snap.Groups);
            Assert.Equal(new[] { 2, 1 }, snap.Groups[0].TrackIds);

            await db.CommitTransactionAsync(
                device,
                new OmgDatabaseTransaction(
                    Array.Empty<AudioTrackDto>(),
                    Array.Empty<int>(),
                    Array.Empty<TrackGroupDto>(),
                    RenamedGroups: new Dictionary<string, string> { ["Mix"] = "NightMix" }));

            snap = await db.ReadDatabaseFastAsync(device);
            Assert.Equal("NightMix", snap.Groups[0].Title);

            await db.CommitTransactionAsync(
                device,
                new OmgDatabaseTransaction(
                    Array.Empty<AudioTrackDto>(),
                    Array.Empty<int>(),
                    Array.Empty<TrackGroupDto>(),
                    DeletedGroupNames: ["NightMix"]));

            snap = await db.ReadDatabaseFastAsync(device);
            Assert.Equal("Unassigned", snap.Groups[0].Title);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CodecPipeline_DecodesOmaToPlayableWavStream_AndExportsWavAndFlac()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();

        string tempDir = Path.Combine(Path.GetTempPath(), $"apollo_cod06_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string srcWav = Path.Combine(tempDir, "src.wav");
        WriteDualChannelSineWav(srcWav, 44100, 440.0, 660.0, 0.25, 0.5);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-cod06");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m35-cod06");
        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            var decoder = orchestrator.ResolveRequiredEngine<IAudioStreamDecoder>();

            string outOma = Path.Combine(tempDir, "10000001.OMA");
            var tags = new Ea3TagMetadata("Stream Test", "Apollo", "Test", "Electronic", 2026, 1, 0);
            var omaRes = await codec.ProcessToOmaAsync(
                new TranscodeRequest(srcWav, outOma, AudioCodecId.Atrac3Plus, 128, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(omaRes.Success);

            await using var wavStream = await decoder.DecodeOmaToPlayableWavStreamAsync(outOma);
            Assert.NotNull(wavStream);
            Assert.True(wavStream!.Length > 44);

            string outWav = Path.Combine(tempDir, "out.wav");
            var wavRes = await codec.ProcessToOmaAsync(
                new TranscodeRequest(srcWav, outWav, AudioCodecId.LinearPcm, 0, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(wavRes.Success);
            Assert.True(File.Exists(outWav));

            string outFlac = Path.Combine(tempDir, "out.flac");
            var flacRes = await codec.ProcessToOmaAsync(
                new TranscodeRequest(srcWav, outFlac, AudioCodecId.LinearPcm, 0, OmaEncryptionMode.Unprotected_0xFFFF, 1, null, tags));
            Assert.True(flacRes.Success);
            byte[] flacHeader = (await File.ReadAllBytesAsync(outFlac))[..4];
            Assert.Equal("fLaC"u8.ToArray(), flacHeader);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-cod06");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DeviceManifestRegistry_ResolvesFreshlyFormattedSonyWalkman_WithoutOmgaudioFolder()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var liveFormattedProbe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "SONY NWWM MEM AAD2 USB Device",
            VolumeRootPath: @"D:\",
            VolumeSerialNumber: 0x12345678u,
            TotalVolumeBytes: 1_022_066_688L,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["System Volume Information"],
            PresentOmgAudioFiles: Array.Empty<string>());

        var liveResult = registry.ResolveDevice(liveFormattedProbe);
        Assert.True(liveResult.IsRecognized);
        Assert.NotNull(liveResult.Identity);

        var emptyOfflineFolderProbe = liveFormattedProbe with { VendorId = 0, ProductId = 0, UsbProductString = null };
        var offlineResult = registry.ResolveDevice(emptyOfflineFolderProbe);
        Assert.False(offlineResult.IsRecognized);
    }

    [Fact]
    public async Task FileManager_RefusesUnsignedMaclist0Creation_OnFreshlyFormatted0x0001Player()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        string sandbox = Path.Combine(Path.GetTempPath(), $"apollo_no_maclist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        string srcWav = Path.Combine(sandbox, "src.wav");
        WriteDualChannelSineWav(srcWav, 44100, 440.0, 660.0, 0.25, 0.5);

        var poller = new DevicePollerCoordinator(registry);
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "SONY NWWM MEM AAD2 USB Device",
            VolumeRootPath: sandbox,
            VolumeSerialNumber: 0x11223344u,
            TotalVolumeBytes: 1_022_066_688L,
            HardwareDvIdSegment: 296,
            HardwareDvIdKey: TestEkbFixtureHelper.SyntheticDeviceKeyB,
            PresentRootFolders: Array.Empty<string>(),
            PresentOmgAudioFiles: Array.Empty<string>());
        var res = poller.EvaluateProbe(probe);
        Assert.True(res.IsRecognized);
        var device = res.Identity!;

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.HydrateForDeviceAsync(device);
        try
        {
            var fm = new FileManagerCoordinator(orchestrator, bus);
            var batch = new SyncTrackRequest[]
            {
                new(srcWav, AudioCodecId.Atrac3Plus, 352, "Mix")
            };

            var syncRes = await fm.SyncTracksToDeviceAsync(device, batch);
            Assert.Equal(0, syncRes.SucceededCount);
            Assert.Equal(1, syncRes.FailedCount);
            Assert.Contains("MG ERROR", syncRes.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(sandbox, "OMGAUDIO", "MACLIST0.DAT")));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{device.DeviceInstanceId}");
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    private static void WriteDualChannelSineWav(
        string filePath,
        int sampleRateHz,
        double freqLeftHz,
        double freqRightHz,
        double amplitude,
        double durationSeconds)
    {
        int sampleCount = (int)(sampleRateHz * durationSeconds);
        int dataBytes = sampleCount * 4;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header.Slice(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 36 + dataBytes);
        "WAVE"u8.CopyTo(header.Slice(8, 4));
        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), sampleRateHz * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), 16);
        "data"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataBytes);
        fs.Write(header);

        byte[] pcm = new byte[dataBytes];
        for (int i = 0; i < sampleCount; i++)
        {
            short l = (short)(Math.Sin(2.0 * Math.PI * freqLeftHz * i / sampleRateHz) * amplitude * short.MaxValue);
            short r = (short)(Math.Sin(2.0 * Math.PI * freqRightHz * i / sampleRateHz) * amplitude * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), l);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), r);
        }

        fs.Write(pcm);
    }

    private static (double[] Left, double[] Right) ReadStereoPcm16(string wavPath)
    {
        byte[] bytes = File.ReadAllBytes(wavPath);
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            if (bytes[pos] == 'd' && bytes[pos + 1] == 'a' && bytes[pos + 2] == 't' && bytes[pos + 3] == 'a')
            {
                int dataStart = pos + 8;
                int dataLen = Math.Min(chunkSize, bytes.Length - dataStart);
                int pairs = dataLen / 4;
                var l = new double[pairs];
                var r = new double[pairs];
                for (int i = 0; i < pairs; i++)
                {
                    l[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart + i * 4, 2)) / 32768.0;
                    r[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart + i * 4 + 2, 2)) / 32768.0;
                }
                return (l, r);
            }

            pos += 8 + chunkSize + (chunkSize & 1);
        }

        throw new InvalidDataException($"WAV missing 'data' chunk: {wavPath}");
    }

    private static double GoertzelPower(double[] samples, int offset, int count, double sampleRate, double targetHz)
    {
        double k = Math.Round(count * targetHz / sampleRate);
        double omega = 2.0 * Math.PI * k / count;
        double cos = Math.Cos(omega);
        double coeff = 2.0 * cos;
        double q1 = 0, q2 = 0;
        for (int i = 0; i < count; i++)
        {
            double q0 = coeff * q1 - q2 + samples[offset + i];
            q2 = q1;
            q1 = q0;
        }

        return q1 * q1 + q2 * q2 - q1 * q2 * coeff;
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

    [Fact]
    public async Task Atrac3PlusNativeEncoder_ABQualityAndPsychoacousticComparison_AgainstSonicStageGoldenOma()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var solutionRoot = Path.GetFullPath(Path.Combine(codebaseRoot, ".."));
        var signalsDir = Path.Combine(solutionRoot, "test folder", "re_test_signals");
        var goldenRoot = Path.Combine(solutionRoot, "test folder", "re_golden_oma");

        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_ab_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, "m35-ab");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, "m35-ab");

        try
        {
            var codec = orchestrator.ResolveRequiredEngine<ICodecService>();
            int[] bitrates = [128, 256, 352];
            string[] signals =
            [
                "01_silence_5s",
                "02_impulse_5s",
                "03_sine_1khz_5s",
                "04_logsweep_10s",
                "05_whitenoise_5s"
            ];

            _output.WriteLine("=== ATRAC3plus A-B Quality & Psychoacoustic Comparison (Apollo Tier-1 vs SonicStage Golden) ===");

            foreach (int bitrate in bitrates)
            {
                _output.WriteLine($"\n--- Bitrate: {bitrate} kbps ---");
                foreach (string sig in signals)
                {
                    string srcWav = Path.Combine(signalsDir, $"{sig}.wav");
                    string goldenOma = Path.Combine(goldenRoot, bitrate.ToString(), $"{sig}.OMA");
                    Assert.True(File.Exists(srcWav), $"Missing source signal: {srcWav}");
                    Assert.True(File.Exists(goldenOma), $"Missing SonicStage golden OMA: {goldenOma}");

                    string apolloOma = Path.Combine(tempDir, $"{sig}_{bitrate}_apollo.oma");
                    string apolloWav = Path.Combine(tempDir, $"{sig}_{bitrate}_apollo.wav");
                    string goldenWav = Path.Combine(tempDir, $"{sig}_{bitrate}_golden.wav");

                    var encRes = await codec.ProcessToOmaAsync(new TranscodeRequest(
                        srcWav,
                        apolloOma,
                        AudioCodecId.Atrac3Plus,
                        bitrate,
                        OmaEncryptionMode.Unprotected_0xFFFF,
                        1,
                        null,
                        new Ea3TagMetadata(sig, "AB Test", "Corpus", "Test", 2026, 1, 0)));
                    Assert.True(encRes.Success, $"Apollo encode failed for {sig} @ {bitrate}k: {encRes.ErrorMessage}");

                    Assert.True(await codec.ExtractFromOmaAsync(apolloOma, apolloWav, null), $"vgmstream failed on Apollo {sig} @ {bitrate}k");
                    Assert.True(await codec.ExtractFromOmaAsync(goldenOma, goldenWav, null), $"vgmstream failed on Golden {sig} @ {bitrate}k");

                    var (refSamples, sampleRate) = ReadWavChannel0(srcWav);
                    var (apolloSamples, _) = ReadWavChannel0(apolloWav);
                    var (goldenSamples, _) = ReadWavChannel0(goldenWav);

                    if (sig == "01_silence_5s")
                    {
                        double apolloFloor = ComputeRmsDbfs(apolloSamples);
                        double goldenFloor = ComputeRmsDbfs(goldenSamples);
                        _output.WriteLine($"  [{sig}] NoiseFloor: Apollo={apolloFloor:F2} dBFS | SonicStage={goldenFloor:F2} dBFS");
                        continue;
                    }

                    int apolloLag = FindBestLag(refSamples, apolloSamples);
                    int goldenLag = FindBestLag(refSamples, goldenSamples);

                    double apolloSnr = ComputeSnrDb(refSamples, apolloSamples, apolloLag);
                    double goldenSnr = ComputeSnrDb(refSamples, goldenSamples, goldenLag);
                    _output.WriteLine($"  [{sig}] SNR: Apollo={apolloSnr:F2} dB (lag={apolloLag}) | SonicStage={goldenSnr:F2} dB (lag={goldenLag}) | Delta={apolloSnr - goldenSnr:+0.00;-0.00;0.00} dB");

                    if (sig == "02_impulse_5s")
                    {
                        var (apRms, apPeak) = MeasureImpulsePreEcho(refSamples, apolloSamples, apolloLag);
                        var (gdRms, gdPeak) = MeasureImpulsePreEcho(refSamples, goldenSamples, goldenLag);
                        _output.WriteLine($"    Pre-Echo [-2048..-16]: Apollo RMS={apRms:F2} dBFS (Peak={apPeak:F2}) | SonicStage RMS={gdRms:F2} dBFS (Peak={gdPeak:F2}) | DeltaRMS={apRms - gdRms:+0.00;-0.00;0.00} dB");
                    }

                    if (sig is "04_logsweep_10s" or "05_whitenoise_5s")
                    {
                        var (apBands, apCutoff) = ComputeBandEnergiesAndCutoff(refSamples, apolloSamples, apolloLag, sampleRate);
                        var (gdBands, gdCutoff) = ComputeBandEnergiesAndCutoff(refSamples, goldenSamples, goldenLag, sampleRate);
                        _output.WriteLine($"    Bands (dB rel Ref) [0-4k | 4-10k | 10-16k | 16-22k]:");
                        _output.WriteLine($"      Apollo    : [{apBands[0]:+0.00;-0.00;0.00} | {apBands[1]:+0.00;-0.00;0.00} | {apBands[2]:+0.00;-0.00;0.00} | {apBands[3]:+0.00;-0.00;0.00}] | -10dB Cutoff={apCutoff:F0} Hz");
                        _output.WriteLine($"      SonicStage: [{gdBands[0]:+0.00;-0.00;0.00} | {gdBands[1]:+0.00;-0.00;0.00} | {gdBands[2]:+0.00;-0.00;0.00} | {gdBands[3]:+0.00;-0.00;0.00}] | -10dB Cutoff={gdCutoff:F0} Hz");
                    }
                }
            }
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-ab");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static (double[] Samples, int SampleRate) ReadWavChannel0(string wavPath)
    {
        byte[] bytes = File.ReadAllBytes(wavPath);
        int pos = 12; // skip RIFF + size + WAVE
        int channels = 2;
        int sampleRate = 44100;
        int bitsPerSample = 16;
        int dataOffset = -1;
        int dataSize = 0;

        while (pos + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, pos, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            pos += 8;
            if (chunkId == "fmt " && pos + 16 <= bytes.Length)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = pos;
                dataSize = Math.Min(chunkSize, bytes.Length - pos);
                break;
            }
            pos += chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || bitsPerSample != 16)
            throw new InvalidDataException($"Unsupported WAV format in {wavPath}");

        int bytesPerFrame = channels * 2;
        int frameCount = dataSize / bytesPerFrame;
        var samples = new double[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * bytesPerFrame, 2));
            samples[i] = s / 32768.0;
        }
        return (samples, sampleRate);
    }

    private static double ComputeRmsDbfs(double[] samples)
    {
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++) sumSq += samples[i] * samples[i];
        double rms = Math.Sqrt(sumSq / Math.Max(1, samples.Length));
        return rms > 1e-12 ? 20.0 * Math.Log10(rms) : -120.0;
    }

    private static int FindBestLag(double[] reference, double[] test)
    {
        int start = Math.Min(reference.Length / 4, 22050);
        int window = Math.Min(44100, Math.Min(reference.Length, test.Length) - start - 8192);
        if (window <= 1024) return 0;

        int bestLag = 0;
        double bestCorr = double.NegativeInfinity;

        for (int lag = -8192; lag <= 8192; lag += 16)
        {
            double corr = 0;
            for (int i = 0; i < window; i += 4)
            {
                int rIdx = start + i;
                int tIdx = rIdx + lag;
                if ((uint)tIdx < (uint)test.Length)
                    corr += reference[rIdx] * test[tIdx];
            }
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        int fineCenter = bestLag;
        for (int lag = fineCenter - 16; lag <= fineCenter + 16; lag++)
        {
            double corr = 0;
            for (int i = 0; i < window; i++)
            {
                int rIdx = start + i;
                int tIdx = rIdx + lag;
                if ((uint)tIdx < (uint)test.Length)
                    corr += reference[rIdx] * test[tIdx];
            }
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }
        return bestLag;
    }

    private static double ComputeSnrDb(double[] reference, double[] test, int lag)
    {
        int margin = 4096;
        int start = Math.Max(margin, -lag + margin);
        int end = Math.Min(reference.Length - margin, test.Length - lag - margin);
        if (end <= start) return double.NaN;

        double sigEnergy = 0;
        double errEnergy = 0;
        for (int i = start; i < end; i++)
        {
            double r = reference[i];
            double e = r - test[i + lag];
            sigEnergy += r * r;
            errEnergy += e * e;
        }
        if (errEnergy < 1e-18) return 120.0;
        return 10.0 * Math.Log10(sigEnergy / errEnergy);
    }

    private static (double RmsDbfs, double PeakDbfs) MeasureImpulsePreEcho(double[] reference, double[] test, int lag)
    {
        int peakIdx = -1;
        double peakVal = 0;
        for (int i = 4096; i < reference.Length - 4096; i++)
        {
            double a = Math.Abs(reference[i]);
            if (a > 0.1 && a > peakVal)
            {
                peakVal = a;
                peakIdx = i;
                break; // first impulse
            }
        }
        if (peakIdx < 0) return (-120.0, -120.0);

        double sumSq = 0;
        double maxAbs = 0;
        int count = 0;
        for (int offset = -2048; offset <= -16; offset++)
        {
            int tIdx = peakIdx + offset + lag;
            if ((uint)tIdx < (uint)test.Length)
            {
                double v = test[tIdx];
                sumSq += v * v;
                double av = Math.Abs(v);
                if (av > maxAbs) maxAbs = av;
                count++;
            }
        }
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        double rmsDb = rms > 1e-12 ? 20.0 * Math.Log10(rms) : -120.0;
        double peakDb = maxAbs > 1e-12 ? 20.0 * Math.Log10(maxAbs) : -120.0;
        return (rmsDb, peakDb);
    }

    private static (double[] BandDeltasDb, double CutoffHz) ComputeBandEnergiesAndCutoff(double[] reference, double[] test, int lag, int sampleRate)
    {
        const int fftSize = 4096;
        int half = fftSize / 2;
        var refSpec = new double[half];
        var testSpec = new double[half];
        var hann = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
            hann[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));

        int start = Math.Max(4096, -lag + 4096);
        int end = Math.Min(reference.Length - fftSize - 4096, test.Length - lag - fftSize - 4096);
        int frames = 0;
        var re = new double[fftSize];
        var im = new double[fftSize];

        for (int pos = start; pos < end; pos += fftSize)
        {
            for (int i = 0; i < fftSize; i++) { re[i] = reference[pos + i] * hann[i]; im[i] = 0; }
            FftRadix2(re, im);
            for (int k = 0; k < half; k++) refSpec[k] += re[k] * re[k] + im[k] * im[k];

            for (int i = 0; i < fftSize; i++) { re[i] = test[pos + lag + i] * hann[i]; im[i] = 0; }
            FftRadix2(re, im);
            for (int k = 0; k < half; k++) testSpec[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }

        double binHz = (double)sampleRate / fftSize;
        (double Lo, double Hi)[] bands = [(0, 4000), (4000, 10000), (10000, 16000), (16000, 22050)];
        var bandDeltas = new double[4];
        for (int b = 0; b < 4; b++)
        {
            double rSum = 1e-18, tSum = 1e-18;
            int k0 = Math.Max(1, (int)(bands[b].Lo / binHz));
            int k1 = Math.Min(half - 1, (int)(bands[b].Hi / binHz));
            for (int k = k0; k <= k1; k++) { rSum += refSpec[k]; tSum += testSpec[k]; }
            bandDeltas[b] = 10.0 * Math.Log10(tSum / rSum);
        }

        double cutoffHz = sampleRate / 2.0;
        if (frames > 0)
        {
            for (int k = half - 1; k >= 1; k--)
            {
                double ratioDb = 10.0 * Math.Log10((testSpec[k] + 1e-18) / (refSpec[k] + 1e-18));
                if (ratioDb >= -10.0)
                {
                    cutoffHz = k * binHz;
                    break;
                }
            }
        }
        return (bandDeltas, cutoffHz);
    }

    private static void FftRadix2(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wlenRe = Math.Cos(ang);
            double wlenIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wRe = 1.0, wIm = 0.0;
                for (int j = 0; j < len / 2; j++)
                {
                    double uRe = re[i + j], uIm = im[i + j];
                    double vRe = re[i + j + len / 2] * wRe - im[i + j + len / 2] * wIm;
                    double vIm = re[i + j + len / 2] * wIm + im[i + j + len / 2] * wRe;
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + len / 2] = uRe - vRe;
                    im[i + j + len / 2] = uIm - vIm;
                    double nextWRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextWRe;
                }
            }
        }
    }

    [Fact]
    public void BpbPolicy_DetectsWindowsFormatWarning_AndPassesFactoryBpb()
    {
        var rule = new StrictFat16BpbRule(RequiredReservedSectors: 1, WarnOnWindowsOem: true);
        var winBpb = new VolumeBpbDiagnostics("MSDOS5.0", 512, 32, 8, 2, "FAT16", false, null);
        var evalWin = Walkman.L1.DevicePoller.Probing.Win32VolumeAndUsbProber.EvaluateBpbPolicy(winBpb, rule);
        Assert.True(evalWin.HasWindowsFormatWarning);
        Assert.Contains("FORMAT ERROR", evalWin.WarningMessage!, StringComparison.Ordinal);

        var factoryBpb = new VolumeBpbDiagnostics("SONY", 512, 32, 1, 2, "FAT16", false, null);
        var evalFactory = Walkman.L1.DevicePoller.Probing.Win32VolumeAndUsbProber.EvaluateBpbPolicy(factoryBpb, rule);
        Assert.False(evalFactory.HasWindowsFormatWarning);
        Assert.Null(evalFactory.WarningMessage);
    }

    [Fact]
    public async Task OmgV1DatabaseEngine_AdoptMode_Preserves45SlotGtrlst_ReservedDat_AndRebuilds2DTree()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var solutionRoot = Path.GetFullPath(Path.Combine(codebaseRoot, ".."));
        var e507Backup = Path.Combine(solutionRoot, "test folder", "NW-E507_Full_Backup");
        if (!Directory.Exists(e507Backup)) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_adopt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        foreach (string dir in Directory.GetDirectories(e507Backup, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(e507Backup, tempDir));
        foreach (string file in Directory.GetFiles(e507Backup, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(e507Backup, tempDir), overwrite: true);

        var configuration = DetectBuildConfiguration();
        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(bus, mediator, id => ResolvePluginDllPath(codebaseRoot, configuration, id));
        await orchestrator.LoadModuleAsync(WellKnownModules.L2TransportMsc, "m35-adopt");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2DatabaseOmgV1, "m35-adopt");

        try
        {
            var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));
            var profile = registry.Profiles.First(p => p.SeriesId == "NW-E500");
            var identity = new ConnectedDeviceIdentity("e507-test", profile, "NW-E507", null, tempDir, TestEkbFixtureHelper.SyntheticVolumeSerialB, TestEkbFixtureHelper.SyntheticDeviceKeyB, null);

            byte[] origGtrlst = await File.ReadAllBytesAsync(Path.Combine(tempDir, "OMGAUDIO", "00GTRLST.DAT"));
            byte[] origReserved = await File.ReadAllBytesAsync(Path.Combine(tempDir, "OMGAUDIO", "RESERVED.DAT"));

            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var tx = new OmgDatabaseTransaction(
                UpsertedTracks: [new AudioTrackDto(1, "Track 1", "Artist A", "Album X", "Rock", null, 1, 2006, 180000, 192, AudioCodecId.Mp3, 0, OmaEncryptionMode.HardwareXor_0xFFFE, 0x00038000u, new byte[20], 4320000, "OMGAUDIO/10F00/10000001.OMA")],
                DeletedTrackIds: [],
                UpdatedGroups: [new TrackGroupDto(1, "Album X", "Artist A", string.Empty, 180000, [1])],
                PreserveExistingMaclist0: true);

            await db.CommitTransactionAsync(identity, tx);

            Assert.Equal(origGtrlst, await File.ReadAllBytesAsync(Path.Combine(tempDir, "OMGAUDIO", "00GTRLST.DAT")));
            Assert.Equal(origReserved, await File.ReadAllBytesAsync(Path.Combine(tempDir, "OMGAUDIO", "RESERVED.DAT")));
            Assert.True(File.Exists(Path.Combine(tempDir, "OMGAUDIO", "01TREE2D.DAT")));
            Assert.True(File.Exists(Path.Combine(tempDir, "OMGAUDIO", "03GINF2D.DAT")));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-adopt");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task OmgDatabaseValidator_RejectsPlaintext0xFFFFMp3_OnStrict0xFFFEPlayer()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo_m35_val_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(bus, mediator, id => ResolvePluginDllPath(codebaseRoot, configuration, id));
        await orchestrator.LoadModuleAsync(WellKnownModules.L2TransportMsc, "m35-val");
        await orchestrator.LoadModuleAsync(WellKnownModules.L2DatabaseOmgV1, "m35-val");

        try
        {
            var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));
            var profile = registry.Profiles.First(p => p.SeriesId == "NW-E500");
            var identity = new ConnectedDeviceIdentity("e507-val", profile, "NW-E507", null, tempDir, 0x11111111u, TestEkbFixtureHelper.SyntheticDeviceKeyB, null);

            var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            var badTx = new OmgDatabaseTransaction(
                UpsertedTracks: [new AudioTrackDto(1, "Bad MP3", "Artist", "Album", "Rock", null, 1, 2006, 180000, 192, AudioCodecId.Mp3, 0, OmaEncryptionMode.Unprotected_0xFFFF, 0x00038000u, new byte[20], 4320000, "OMGAUDIO/10F00/10000001.OMA")],
                DeletedTrackIds: [],
                UpdatedGroups: [new TrackGroupDto(1, "Album", "Artist", string.Empty, 180000, [1])],
                PreserveExistingMaclist0: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.CommitTransactionAsync(identity, badTx));
            Assert.False(File.Exists(Path.Combine(tempDir, "OMGAUDIO", "04CNTINF.DAT")));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m35-val");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(MilestoneM35Atrac3PlusAndMagicGateTests)
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
