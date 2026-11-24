using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Services;
using Xunit;

namespace Walkman.M0.Tests;

public sealed class MilestoneM47SlotHealthAndSupportTierTests
{
    [Theory]
    [InlineData("NW-E400", SupportStatus.Verified)]
    [InlineData("NW-E500", SupportStatus.Verified)]
    [InlineData("NW-HD3-HD5", SupportStatus.Experimental)]
    [InlineData("NW-A600", SupportStatus.Experimental)]
    [InlineData("NW-A1000-A3000", SupportStatus.Experimental)]
    [InlineData("NW-E000", SupportStatus.Experimental)]
    public void DevicesManifest_AllSixSeries_DeserializeSupportStatusAccurately(string seriesId, SupportStatus expectedStatus)
    {
        var manifestPath = Path.Combine(ArchitectureDependencyTests.FindCodebaseRoot(), "manifests", "devices.json");
        var registry = DeviceManifestRegistry.LoadFromFile(manifestPath);

        var profile = Assert.Single(registry.Profiles, p => p.SeriesId == seriesId);
        Assert.Equal(expectedStatus, profile.SupportStatus);
    }

    [Fact]
    public void DeviceManifestRegistry_1GbSharedPid01FB_ResolvesCombinedE407E507_UnlessExplicitStringPresent()
    {
        var manifestPath = Path.Combine(ArchitectureDependencyTests.FindCodebaseRoot(), "manifests", "devices.json");
        var registry = DeviceManifestRegistry.LoadFromFile(manifestPath);

        var ambiguousProbe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "SONY NWWM MEM AAD2 USB Device",
            VolumeRootPath: @"D:\",
            VolumeSerialNumber: TestEkbFixtureHelper.SyntheticVolumeSerialB,
            TotalVolumeBytes: 980_000_000,
            HardwareDvIdSegment: 0x0128,
            HardwareDvIdKey: null,
            PresentRootFolders: ["OMGAUDIO"],
            PresentOmgAudioFiles: ["04CNTINF.DAT"]);

        var ambiguousResult = registry.ResolveDevice(ambiguousProbe);
        Assert.True(ambiguousResult.IsRecognized);
        Assert.Equal("NW-E407 / E507", ambiguousResult.Identity!.ResolvedModelCode);

        var explicitE507Probe = ambiguousProbe with { UsbProductString = "SONY NW-E507" };
        var explicitResult = registry.ResolveDevice(explicitE507Probe);
        Assert.True(explicitResult.IsRecognized);
        Assert.Equal("NW-E507", explicitResult.Identity!.ResolvedModelCode);
    }

    [Fact]
    public async Task FileManager_GetMagicGateSlotPoolStatusAsync_DistinguishesValidVsBrokenAndZeroedIcv()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"apollo_m47_health_{Guid.NewGuid():N}");
        string slotPoolDir = Path.Combine(Path.GetTempPath(), $"apollo_m47_pool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "OMGAUDIO", "10F00"));
        try
        {
            string mp3FmDir = Path.Combine(tempRoot, "MP3FM");
            Directory.CreateDirectory(mp3FmDir);
            byte[] dvid = new byte[16];
            dvid[6] = 0x01; dvid[7] = 0x28;
            dvid[12] = 0x12; dvid[13] = 0x34; dvid[14] = 0x56; dvid[15] = 0x78;
            await File.WriteAllBytesAsync(Path.Combine(mp3FmDir, "DvID.DAT"), dvid);

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

            // Track #1: valid signed 0x0001
            byte[] syntheticEkb = TestEkbFixtureHelper.BuildSyntheticTestEkb00010021();
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/00010021.DAT", syntheticEkb);
            var wrapped1 = crypto.WrapAtracToOma(
                new byte[64], AudioCodecId.Atrac3Plus, 0x0028FF, 4000, 8, 1, null, OmaEncryptionMode.MagicGateLsi_0x0001,
                new Ea3TagMetadata("Valid 1", "Artist", "Album", "Electronic", 2026, 1, 4000),
                ekb00010021Bytes: syntheticEkb);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000001.OMA", wrapped1.OmaBytes);

            // Track #2: broken/unsigned 0x0001 (bogus MACLIST0 entry with no matching OMG_ULINF)
            byte[] bogusMac = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/10F00/10000002.OMA", new byte[128]);

            byte[] maclist = new byte[32768];
            wrapped1.MaclistEntry8!.CopyTo(maclist, 0);
            bogusMac.CopyTo(maclist, 8);
            await transport.WriteFileAtomicAsync(identity, "OMGAUDIO/MACLIST0.DAT", maclist);

            var dto1 = new AudioTrackDto(1, "Valid 1", "Artist", "Album", "Electronic", null, 1, 2026, 4000, 8,
                AudioCodecId.Atrac3Plus, 352, OmaEncryptionMode.MagicGateLsi_0x0001, 0x010028FFu, wrapped1.LsiKeyring48, GroupName: "Pool", MaclistEntry8: wrapped1.MaclistEntry8);
            var dto2 = new AudioTrackDto(2, "Broken 2", "Artist", "Album", "Electronic", null, 2, 2026, 4000, 8,
                AudioCodecId.Atrac3Plus, 352, OmaEncryptionMode.MagicGateLsi_0x0001, 0x010028FFu, new byte[48], GroupName: "Pool", MaclistEntry8: bogusMac);
            await db.CommitTransactionAsync(identity, new OmgDatabaseTransaction([dto1, dto2], [], [], PreserveExistingMaclist0: true));

            var status = await fm.GetMagicGateSlotPoolStatusAsync(identity, harvestActiveSlots: true);
            Assert.True(status.HasMaclist0);
            Assert.Equal(2, status.TotalSignedSlotsInMaclist0);
            Assert.Equal(1, status.ActiveSignedTrackCount);
            Assert.Equal(0, status.RecycledFreeSlotCount);
            Assert.Equal(1, status.BrokenUnsignedSlotCount);
            Assert.Equal([2], status.BrokenUnsignedTrackIds);
            Assert.NotNull(status.HealthWarning);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            try { Directory.Delete(slotPoolDir, true); } catch { }
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
            string l2 = Path.Combine(root, "src", "L2.Engines", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            if (File.Exists(l2)) return l2;
            string l3 = Path.Combine(root, "src", "L3.Presentation", $"Walkman.{moduleId}", "bin", config, "net8.0", dllName);
            return File.Exists(l3) ? l3 : direct;
        });
    }
}
