using Walkman.Contracts.Devices;
using Walkman.L1.Orchestrator.Manifests;

namespace Walkman.M0.Tests;

/// <summary>
/// Verifies multi-factor Walkman identification against codebase/manifests/devices.json.
/// </summary>
public sealed class DeviceManifestResolutionTests
{
    private readonly DeviceManifestRegistry _registry;

    public DeviceManifestResolutionTests()
    {
        var manifestPath = Path.Combine(ArchitectureDependencyTests.FindCodebaseRoot(), "manifests", "devices.json");
        _registry = DeviceManifestRegistry.LoadFromFile(manifestPath);
    }

    [Theory]
    [InlineData(250_000_000L, "NW-E403")]
    [InlineData(500_000_000L, "NW-E405")]
    [InlineData(980_000_000L, "NW-E407")]
    public void SharedPid01FB_WithDvId0128_ResolvesCapacityTierAccurately(long capacityBytes, string expectedModel)
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: null,
            VolumeRootPath: @"E:\",
            VolumeSerialNumber: 0x01020304,
            TotalVolumeBytes: capacityBytes,
            HardwareDvIdSegment: 0x0128,
            HardwareDvIdKey: 0x11223344,
            PresentRootFolders: ["OMGAUDIO", "SCREEN"],
            PresentOmgAudioFiles: ["04CNTINF.DAT"]);

        var result = _registry.ResolveDevice(probe);

        Assert.True(result.IsRecognized);
        Assert.NotNull(result.Identity);
        Assert.Equal("NW-E400", result.Identity.Profile.SeriesId);
        Assert.Equal(expectedModel, result.Identity.ResolvedModelCode);
        Assert.True(result.Identity.Profile.RequiresHardwareDvIdKey);
        Assert.NotNull(result.Identity.Profile.Screen);
        Assert.Equal(ScreensaverMagic.NwE400_0xEC01, result.Identity.Profile.Screen.Magic);
        Assert.Equal(48, result.Identity.Profile.Screen.PhysicalFrameWidth);
        Assert.Equal(120, result.Identity.Profile.Screen.PhysicalFrameHeight);
    }

    [Fact]
    public void SharedPid01FB_WithNwE505ProductString_ResolvesToNwE500Series()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "SONY NW-E505",
            VolumeRootPath: @"E:\",
            VolumeSerialNumber: 0xAA551122,
            TotalVolumeBytes: 510_000_000,
            HardwareDvIdSegment: 0x0128,
            HardwareDvIdKey: 0x55667788,
            PresentRootFolders: ["OMGAUDIO", "SCREEN"],
            PresentOmgAudioFiles: ["04CNTINF.DAT"]);

        var result = _registry.ResolveDevice(probe);

        Assert.True(result.IsRecognized);
        Assert.Equal("NW-E500", result.Identity!.Profile.SeriesId);
        Assert.Equal("NW-E505", result.Identity.ResolvedModelCode);
    }

    [Fact]
    public void SharedPid027C_WithOmgV2MarkerAnd512Mb_ResolvesToNwA605()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x027C,
            UsbProductString: "NETWORK WALKMAN",
            VolumeRootPath: @"F:\",
            VolumeSerialNumber: 0x22334455,
            TotalVolumeBytes: 505_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["OMGAUDIO", "SCREEN"],
            PresentOmgAudioFiles: ["01TREE05.DAT", "04CNTINF.DAT"]);

        var result = _registry.ResolveDevice(probe);

        Assert.True(result.IsRecognized);
        Assert.Equal("NW-A600", result.Identity!.Profile.SeriesId);
        Assert.Equal("NW-A605", result.Identity.ResolvedModelCode);
        Assert.Equal(DatabaseFormat.OmgV2, result.Identity.Profile.DatabaseFormat);
    }

    [Fact]
    public void SharedPid027C_WithOmgV2MarkersAnd6Gb_ResolvesToNwA1000()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x027C,
            UsbProductString: "NETWORK WALKMAN",
            VolumeRootPath: @"G:\",
            VolumeSerialNumber: 0x99887766,
            TotalVolumeBytes: 6_000_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["OMGAUDIO"],
            PresentOmgAudioFiles: ["01TREE05.DAT", "03GINF22.DAT"]);

        var result = _registry.ResolveDevice(probe);

        Assert.True(result.IsRecognized);
        Assert.Equal("NW-A1000-A3000", result.Identity!.Profile.SeriesId);
        Assert.Equal("NW-A1000", result.Identity.ResolvedModelCode);
        Assert.Equal(ScreensaverMagic.NwA1000_0xD301, result.Identity.Profile.Screen!.Magic);
    }

    [Fact]
    public void HiMdDevice_WithHmdHifiFolder_IsRejectedFromMscResolution()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x0219,
            UsbProductString: "Net MD",
            VolumeRootPath: @"H:\",
            VolumeSerialNumber: 0x00001111,
            TotalVolumeBytes: 1_000_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["HMDHIFI"],
            PresentOmgAudioFiles: []);

        var result = _registry.ResolveDevice(probe);

        Assert.False(result.IsRecognized);
        Assert.Null(result.Identity);
        Assert.Contains("HMDHIFI", result.UnrecognizedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainMicroVaultFlashDrive_WithoutOmgaudioFolder_IsNotMisidentifiedAsNwHd5()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x0243,
            UsbProductString: "MicroVault Flash Drive",
            VolumeRootPath: @"I:\",
            VolumeSerialNumber: 0x44445555,
            TotalVolumeBytes: 20_000_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["DOCUMENTS"],
            PresentOmgAudioFiles: []);

        var result = _registry.ResolveDevice(probe);

        Assert.False(result.IsRecognized);
    }

    [Fact]
    public void UnknownSonyPid_WithOmgaudio_ReturnsUnrecognizedForDumpWizardBranch()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x0999,
            UsbProductString: "Future Walkman",
            VolumeRootPath: @"J:\",
            VolumeSerialNumber: 0x77778888,
            TotalVolumeBytes: 2_000_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["OMGAUDIO"],
            PresentOmgAudioFiles: ["01TREE05.DAT"]);

        var result = _registry.ResolveDevice(probe);

        Assert.False(result.IsRecognized);
    }

    [Fact]
    public void NonSonyVendorId_IsRejectedImmediately()
    {
        var probe = new DeviceDetectionProbe(
            VendorId: 0x046D,
            ProductId: 0xC52B,
            UsbProductString: "Logitech Receiver",
            VolumeRootPath: null,
            VolumeSerialNumber: null,
            TotalVolumeBytes: null,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: [],
            PresentOmgAudioFiles: []);

        var result = _registry.ResolveDevice(probe);

        Assert.False(result.IsRecognized);
        Assert.Contains("0x046D", result.UnrecognizedReason, StringComparison.OrdinalIgnoreCase);
    }
}
