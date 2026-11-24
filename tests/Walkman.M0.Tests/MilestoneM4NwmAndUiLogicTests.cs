using System.Reflection;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;

namespace Walkman.M0.Tests;

public sealed class MilestoneM4NwmAndUiLogicTests
{
    [Fact]
    public async Task NwmEngine_PackAndUnpack_RoundTripsAllThreeFormats_WithDeduplication()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CustomNwmEngine, "m4-nwm-test");
        try
        {
            var packer = orchestrator.ResolveRequiredEngine<IScreensaverPacker>();

            // 1. 0xEC01 (120x36 1bpp) asymmetric test pattern + duplicate frame deduplication
            byte[] bmp0 = new byte[540];
            Set1bppPixel(bmp0, 120, 0, 0);
            Set1bppPixel(bmp0, 120, 119, 0);
            Set1bppPixel(bmp0, 120, 12, 35);

            byte[] bmp1 = (byte[])bmp0.Clone(); // Intentional duplicate of bmp0
            byte[] bmp2 = new byte[540];
            Set1bppPixel(bmp2, 120, 60, 18);

            var ec01Spec = new ScreenSpecification(120, 36, 48, 120, 1, 720, ScreensaverMagic.NwE400_0xEC01, true, 45056, 56, ["SCREEN"]);
            var ec01Project = new NwmProjectDto(
                ScreensaverMagic.NwE400_0xEC01,
                "ApolloTest",
                "Apollo",
                30720,
                12288,
                120,
                36,
                1,
                [new(0), new(1), new(2)],
                [bmp0, bmp1, bmp2]);

            var validation = packer.Validate(ec01Project, ec01Spec);
            Assert.True(validation.IsValid);

            byte[] ec01Packed = packer.Pack(ec01Project);
            var ec01Unpacked = packer.Unpack(ec01Packed);

            Assert.Equal(ScreensaverMagic.NwE400_0xEC01, ec01Unpacked.Magic);
            Assert.Equal("ApolloTest", ec01Unpacked.Name);
            Assert.Equal("Apollo", ec01Unpacked.Author);
            Assert.Equal(120, ec01Unpacked.Width);
            Assert.Equal(36, ec01Unpacked.Height);
            Assert.Equal(2, ec01Unpacked.UniqueBitmaps.Count); // Deduplicated bmp0 == bmp1
            Assert.Equal(0, ec01Unpacked.Timeline[0].BitmapIndex);
            Assert.Equal(0, ec01Unpacked.Timeline[1].BitmapIndex);
            Assert.Equal(1, ec01Unpacked.Timeline[2].BitmapIndex);
            Assert.Equal(bmp0, ec01Unpacked.UniqueBitmaps[0]);
            Assert.Equal(bmp2, ec01Unpacked.UniqueBitmaps[1]);

            // 2. 0xEC02 (108x16 1bpp)
            byte[] e002Bmp = new byte[216];
            Set1bppPixel(e002Bmp, 108, 5, 2);
            Set1bppPixel(e002Bmp, 108, 107, 15);
            var e002Proj = new NwmProjectDto(ScreensaverMagic.NwE002_0xEC02, "StickTest", "SONY", 30720, 12288, 108, 16, 1, [new(0)], [e002Bmp]);
            var e002Unpacked = packer.Unpack(packer.Pack(e002Proj));
            Assert.Equal(ScreensaverMagic.NwE002_0xEC02, e002Unpacked.Magic);
            Assert.Equal(e002Bmp, e002Unpacked.UniqueBitmaps[0]);

            // 3. 0xD301 (80x80 2bpp)
            byte[] a1000Bmp = new byte[1600];
            a1000Bmp[0] = 0b11_10_01_00;
            a1000Bmp[1599] = 0b01_10_11_00;
            var a1000Proj = new NwmProjectDto(ScreensaverMagic.NwA1000_0xD301, "A3000Test", "SONY", 30720, 12288, 80, 80, 2, [new(0)], [a1000Bmp]);
            var a1000Unpacked = packer.Unpack(packer.Pack(a1000Proj));
            Assert.Equal(ScreensaverMagic.NwA1000_0xD301, a1000Unpacked.Magic);
            Assert.Equal(80, a1000Unpacked.Width);
            Assert.Equal(a1000Bmp, a1000Unpacked.UniqueBitmaps[0]);
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m4-nwm-test");
        }
    }

    [Fact]
    public async Task NwmEngine_Validate_RejectsOverLimitBitmapsAndNonAsciiMetadata()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        await orchestrator.LoadModuleAsync(WellKnownModules.L2CustomNwmEngine, "m4-nwm-limits");
        try
        {
            var packer = orchestrator.ResolveRequiredEngine<IScreensaverPacker>();
            var spec = new ScreenSpecification(120, 36, 48, 120, 1, 720, ScreensaverMagic.NwE400_0xEC01, true, 45056, 56, ["SCREEN"]);

            var tooManyBitmaps = Enumerable.Range(0, 58).Select(i =>
            {
                byte[] b = new byte[540];
                b[0] = (byte)(i + 1);
                return b;
            }).ToList();

            var timeline = Enumerable.Range(0, 58).Select(i => new NwmFrameEntry((byte)i)).ToList();
            var badProj = new NwmProjectDto(ScreensaverMagic.NwE400_0xEC01, "TestÜnicode", "Author", 30720, 12288, 120, 36, 1, timeline, tooManyBitmaps);

            var res = packer.Validate(badProj, spec);
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("ASCII", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(res.Errors, e => e.Contains("Unique bitmap count", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("m4-nwm-limits");
        }
    }

    [Fact]
    public void ModuleRegistry_EnforcesOneShotIntro_DeviceGating_AndBackSlotReplacement()
    {
        var phase = StartScreenPhase.IdleBlobs;
        Assert.Equal(StartScreenPhase.IdleBlobs, ModuleRegistry.TransitionStartState(phase, StartScreenEvent.PointerLeave));

        phase = ModuleRegistry.TransitionStartState(phase, StartScreenEvent.PointerEnter);
        Assert.Equal(StartScreenPhase.MedallionReady, phase);

        // PointerLeave never reverts to blobs once entered
        Assert.Equal(StartScreenPhase.MedallionReady, ModuleRegistry.TransitionStartState(phase, StartScreenEvent.PointerLeave));

        phase = ModuleRegistry.TransitionStartState(phase, StartScreenEvent.ClickPlus);
        Assert.Equal(StartScreenPhase.RingExpanded, phase);

        // Disconnected Start Ring hides device-only File Manager
        var (discItems, _) = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.StartRing, deviceConnected: false, activeSubmenuParentId: null);
        Assert.DoesNotContain(discItems, m => m.Id == ModuleIds.FileManager);
        Assert.Contains(discItems, m => m.Id == ModuleIds.Converter);

        // Connected Start Ring places thicker File Manager first
        var (connItems, _) = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.StartRing, deviceConnected: true, activeSubmenuParentId: null);
        Assert.Equal(ModuleIds.FileManager, connItems[0].Id);
        Assert.True(connItems[0].RadialThickness > connItems[1].RadialThickness);

        // Customization drill-down replaces its exact slot index with Back (3 children, Firmware moved to Device & System)
        int customIndex = connItems.ToList().FindIndex(m => m.Id == ModuleIds.Customization);
        var (subItems, backIdx) = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.StartRing, deviceConnected: true, activeSubmenuParentId: ModuleIds.Customization);
        Assert.Equal(customIndex, backIdx);
        Assert.Equal(ModuleIds.Back, subItems[backIdx].Id);
        Assert.Contains(subItems, m => m.Id == ModuleIds.CustomOled);
        Assert.DoesNotContain(subItems, m => m.Id == ModuleIds.CustomFirmware);

        // Device & System drill-down exposes Pair & Sync, Firmware, Dump Wizard, Technical Info
        var (devSub, devBackIdx) = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.StartRing, deviceConnected: true, activeSubmenuParentId: ModuleIds.DeviceSystem);
        Assert.True(devBackIdx >= 0);
        Assert.Contains(devSub, m => m.Id == ModuleIds.PairSync);
        Assert.Contains(devSub, m => m.Id == ModuleIds.CustomFirmware);
        Assert.Contains(devSub, m => m.Id == ModuleIds.DeviceDumpWizard);
        Assert.Contains(devSub, m => m.Id == ModuleIds.DeviceTechInfo);

        // Frozen lockedBackIndex remains stable even when USB connect state flips inside an open submenu
        var (_, frozenDiscBack) = ModuleRegistry.ResolveActiveRingModules(
            ModuleRegistry.DefaultModules,
            RingSurface.StartRing,
            deviceConnected: false,
            activeSubmenuParentId: ModuleIds.Customization,
            lockedBackIndex: customIndex);
        Assert.Equal(customIndex, frozenDiscBack);
    }

    [Fact]
    public void DemoModeOverlay_And_AppSettingsStore_IsolateSyntheticDataAndPersistSafely()
    {
        var demoDevices = DemoModeOverlay.CreateDemoDevices();
        Assert.NotEmpty(demoDevices);
        Assert.All(demoDevices, d => Assert.True(d.IsDemoDevice));

        var demoTracks = DemoModeOverlay.CreateDemoSampleTracks();
        Assert.NotEmpty(demoTracks);
        Assert.All(demoTracks, t => Assert.True(t.IsDemoItem));

        var demoLoc = DemoModeOverlay.CreateDemoSourceLocation();
        Assert.True(demoLoc.IsDemoLocation);

        var pcLocs = PcLocationCatalog.BuildDefaultPcLocations(@"C:\Music\Lib", @"C:\Music\Out");
        Assert.Equal(2, pcLocs.Count);
        Assert.DoesNotContain(pcLocs, l => l.Detail.Contains(@"D:\Audio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pcLocs, l => l.Detail.Contains(@"Users\Downloads", StringComparison.OrdinalIgnoreCase));

        string tempDir = Path.Combine(Path.GetTempPath(), $"ApolloSettingsTest_{Guid.NewGuid():N}");
        try
        {
            var initial = AppSettingsStore.Load(tempDir);
            Assert.False(initial.IsDemoModeEnabled);
            Assert.True(initial.EnableColdStartIntro);
            Assert.False(initial.UseSidebarRail);

            var modified = initial with
            {
                IsDarkTheme = true,
                IsDemoModeEnabled = true,
                UseSidebarRail = true,
                PlayerVolume = 0.42
            };
            AppSettingsStore.Save(modified, tempDir);

            var reloaded = AppSettingsStore.Load(tempDir);
            Assert.True(reloaded.IsDarkTheme);
            Assert.True(reloaded.IsDemoModeEnabled);
            Assert.True(reloaded.UseSidebarRail);
            Assert.Equal(0.42, reloaded.PlayerVolume, precision: 2);

            // Corrupt JSON falls back cleanly to defaults
            File.WriteAllText(AppSettingsStore.GetSettingsPath(tempDir), "{ invalid-json");
            var fallback = AppSettingsStore.Load(tempDir);
            Assert.False(fallback.IsDemoModeEnabled);
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
    public void FileManagerAndConverterStates_EnforceLeftToRightSwap_AndRememberChoiceLocks()
    {
        var fm = FileManagerTransferConfig.Default;
        var swapped = FileManagerStateLogic.SwapTransferEndpoints(fm);
        Assert.Equal(fm.DestLocationId, swapped.SourceLocationId);
        Assert.Equal(fm.SourceLocationId, swapped.DestLocationId);

        Assert.False(FileManagerStateLogic.IsTransferControlLocked(true, TransferControlKind.Swap));
        Assert.False(FileManagerStateLogic.IsTransferControlLocked(true, TransferControlKind.CustomCodec));
        Assert.True(FileManagerStateLogic.IsTransferControlLocked(true, TransferControlKind.UploadMode));
        Assert.True(FileManagerStateLogic.IsTransferControlLocked(true, TransferControlKind.NormalizeVolume));

        Assert.Equal(UnderGlowTone.Green, FileManagerStateLogic.GetControlUnderGlow(isActive: true, isLocked: false));
        Assert.Equal(UnderGlowTone.Blue, FileManagerStateLogic.GetControlUnderGlow(isActive: true, isLocked: true));
        Assert.Equal(UnderGlowTone.None, FileManagerStateLogic.GetControlUnderGlow(isActive: false, isLocked: true));

        Assert.False(ConverterStateLogic.IsConverterControlLocked(true, ConverterControlKind.SourceFolder));
        Assert.False(ConverterStateLogic.IsConverterControlLocked(true, ConverterControlKind.OutputFolder));
        Assert.True(ConverterStateLogic.IsConverterControlLocked(true, ConverterControlKind.TargetCodec));
        Assert.Equal(".oma", ConverterStateLogic.GetOutputExtension(TargetCodec.Atrac3Plus));
        Assert.True(ConverterStateLogic.EstimateConvertedSizeMb(240, TargetCodec.Atrac3Plus, BitrateOption.K256) < 10.0);
    }

    private static void Set1bppPixel(byte[] buffer, int stridePixels, int x, int y)
    {
        int bitIdx = y * stridePixels + x;
        buffer[bitIdx >> 3] |= (byte)(0x80 >> (bitIdx & 7));
    }

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(MilestoneM4NwmAndUiLogicTests)
            .Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>();

        return string.IsNullOrWhiteSpace(attr?.Configuration) ? "Debug" : attr.Configuration;
    }

    [Fact]
    public void V3SortingGroupingStagedPreviewAndPlaybackControlsWork()
    {
        var tracks = new[]
        {
            new AudioTrackItem { Id = "t1", Title = "Zebra", Artist = "B", Album = "Rock Album", Format = "FLAC", Bitrate = "900", Folder = "Rock", Rating = 2, DurationSec = 200 },
            new AudioTrackItem { Id = "t2", Title = "Alpha", Artist = "A", Album = "Elec Album", Format = "MP3", Bitrate = "320", Folder = "Electronic", Rating = 5, DurationSec = 120 }
        };

        var sortedByRating = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildSortedTrackEntries(
            tracks,
            TrackSortField.Rating,
            descending: true,
            groupByFolder: false,
            "test",
            new HashSet<string>());
        Assert.Equal(2, sortedByRating.Count);
        Assert.Equal("t2", sortedByRating[0].Track?.Id);

        var grouped = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildSortedTrackEntries(
            tracks,
            TrackSortField.Title,
            descending: false,
            groupByFolder: true,
            "test",
            new HashSet<string>());
        Assert.Equal(4, grouped.Count);
        Assert.True(grouped[0].IsHeader);
        Assert.Equal("Electronic", grouped[0].HeaderTitle);

        var destEntries = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildDestinationCollectionEntries(
            Array.Empty<AudioTrackItem>(),
            [tracks[1]],
            new Dictionary<string, string> { ["t2"] = "Staged Mix" },
            "Default",
            ["Staged Mix"],
            TrackSortField.Title,
            false,
            new HashSet<string>());
        Assert.Equal(2, destEntries.Count);
        Assert.True(destEntries[0].IsStagedHeader);
        Assert.Equal("Staged Mix", destEntries[0].HeaderTitle);
        Assert.Equal("0 +1", destEntries[0].HeaderCountLabel);
        Assert.True(destEntries[1].Track?.IsStagedPreview);

        // Empty collections are preserved
        var emptyPreserved = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildDestinationCollectionEntries(
            Array.Empty<AudioTrackItem>(),
            Array.Empty<AudioTrackItem>(),
            new Dictionary<string, string>(),
            "Default",
            ["EmptyGroup"],
            TrackSortField.Title,
            false,
            new HashSet<string>());
        Assert.Single(emptyPreserved);
        Assert.Equal("EmptyGroup", emptyPreserved[0].HeaderTitle);

        // RefCountedSkImage safe reference counting
        using var bmp = new SkiaSharp.SKBitmap(4, 4);
        var holder = new Walkman.L3.AppShell.Controls.AeroSkiaHelpers.RefCountedSkImage(SkiaSharp.SKImage.FromBitmap(bmp));
        Assert.NotNull(holder.TryAcquire());
        holder.Release(); // still held by acquired ref
        Assert.NotNull(holder.TryAcquire());
        holder.Release();
        holder.Release(); // final release disposes
        Assert.Null(holder.TryAcquire());
    }

    [Fact]
    public void Ui03_TrackOrderingAndBpbWarningDiagnosticsWork()
    {
        var existing = new[]
        {
            new AudioTrackItem { Id = "d1", Title = "First", Artist = "A", Album = "Alb", Format = "MP3", Bitrate = "192", GroupName = "Mix" },
            new AudioTrackItem { Id = "d2", Title = "Second", Artist = "B", Album = "Alb", Format = "MP3", Bitrate = "192", GroupName = "Mix" }
        };
        var staged = new[]
        {
            new AudioTrackItem { Id = "s1", Title = "Inserted", Artist = "C", Album = "Alb", Format = "FLAC", Bitrate = "900" }
        };

        var orderMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mix"] = ["d1", "staged:s1", "d2"]
        };

        var entries = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildDestinationCollectionEntries(
            existing,
            staged,
            new Dictionary<string, string> { ["s1"] = "Mix" },
            "Default",
            ["Mix"],
            TrackSortField.Order,
            false,
            new HashSet<string>(),
            orderMap);

        Assert.Equal(4, entries.Count); // 1 header + 3 tracks
        Assert.Equal("d1", entries[1].Track?.Id);
        Assert.Equal(1, entries[1].CollectionPosition);
        Assert.Equal("#1", entries[1].PositionLabel);
        Assert.Equal("staged:s1", entries[2].Track?.Id);
        Assert.Equal(2, entries[2].CollectionPosition);
        Assert.Equal("#2", entries[2].PositionLabel);
        Assert.Equal("d2", entries[3].Track?.Id);
        Assert.Equal(3, entries[3].CollectionPosition);
        Assert.Equal("#3", entries[3].PositionLabel);

        var ids = new List<string> { "d1", "staged:s1", "d2" };
        Assert.False(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.MoveTrackInOrder(ids, "d1", -1));
        Assert.True(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.MoveTrackInOrder(ids, "d1", +1));
        Assert.Equal(["staged:s1", "d1", "d2"], ids);
        Assert.True(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.SetTrackPositionInOrder(ids, "d2", 1));
        Assert.Equal(["d2", "staged:s1", "d1"], ids);

        var warnBpb = new Walkman.Contracts.Devices.VolumeBpbDiagnostics(
            "MSDOS5.0", 512, 16, 8, 2, "FAT16", true,
            "Windows-formatted FAT16 detected (Rsvd=8). Format on device first.");
        var warnProbe = new Walkman.Contracts.Devices.DeviceDetectionProbe(
            0x054C, 0x027A, "NW-E507", "E:\\", 0x1234, 512_000_000, 1, 1, [], [], warnBpb);
        var warnDevice = new WalkmanDeviceSummary(
            "live:e507", "NW-E507", 2005, "NW-E500", 0.5, 0.1, 90, ["MP3"], "OLED", ScreenTintMode.Cyan, "USB",
            IsLiveHardware: true, DetectionProbe: warnProbe);

        var notice = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildBpbWarningNotification(warnDevice);
        Assert.NotNull(notice);
        Assert.Contains("NW-E507", notice.Value.Title);
        Assert.Contains("Windows-formatted FAT16", notice.Value.Message);

        var okBpb = warnBpb with { HasWindowsFormatWarning = false, WarningMessage = null };
        var okDevice = warnDevice with { DetectionProbe = warnProbe with { BpbDiagnostics = okBpb } };
        Assert.Null(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildBpbWarningNotification(okDevice));
        Assert.Null(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildBpbWarningNotification(null));
    }

    [Fact]
    public void Ui04_05_06_SectorBatchAndUiPolishWork()
    {
        // RefCountedSectorBatch safe lifetime across UI/render thread handoff
        var path = new SkiaSharp.SKPath();
        path.AddRect(new SkiaSharp.SKRect(0, 0, 10, 10));
        var sector = ModuleRegistry.ComputeRingSectors([ModuleRegistry.DefaultModules[0]], 0, 0, 100)[0];
        var item = new Walkman.L3.AppShell.Controls.AeroSkiaHelpers.CachedSectorItem(
            sector, path, null, 0, 0, 10, 10, null, 0, 0, null, 0, 0, 0, 0);
        var batch = new Walkman.L3.AppShell.Controls.AeroSkiaHelpers.RefCountedSectorBatch([item]);

        var acquired = batch.TryAcquire();
        Assert.NotNull(acquired);
        batch.Release(); // UI thread replaced batch; DrawOp still holds ref
        Assert.NotNull(batch.TryAcquire());
        batch.Release(); // extra acquire released
        batch.Release(); // DrawOp disposed -> disposes batch
        Assert.Null(batch.TryAcquire());

        // Inline rating properties & stars
        var track = new AudioTrackItem { Id = "r1", Title = "Star", Artist = "A", Album = "Alb", Format = "FLAC", Bitrate = "900", Rating = 3 };
        Assert.True(track.RatingAtLeast1);
        Assert.True(track.RatingAtLeast3);
        Assert.False(track.RatingAtLeast4);
        Assert.Equal("★★★☆☆", track.RatingStars);

        // DnD insertion indicator property
        var entry = new TrackListEntry { Track = track };
        Assert.False(entry.IsDropTargetBefore);
        entry.IsDropTargetBefore = true;
        Assert.True(entry.IsDropTargetBefore);
    }

    [Fact]
    public void Ui07_08_FolderIndicatorsAndCleanRatingsWork()
    {
        var unrated = new AudioTrackItem { Id = "u1", Title = "Unrated", Artist = "A", Album = "Alb", Format = "MP3", Bitrate = "192", Folder = "FolderA", Rating = 0 };
        Assert.False(unrated.HasRating);
        Assert.Equal(string.Empty, unrated.RatingFilledStars);

        var rated = new AudioTrackItem { Id = "r2", Title = "Rated", Artist = "B", Album = "Alb", Format = "FLAC", Bitrate = "900", Folder = "FolderA", Rating = 4, IsSelected = true, IsNowPlaying = true };
        Assert.True(rated.HasRating);
        Assert.Equal("★★★★", rated.RatingFilledStars);

        var entries = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildSortedTrackEntries(
            [unrated, rated],
            TrackSortField.Title,
            false,
            true,
            "fmsrc",
            new HashSet<string>());
        Assert.True(entries[0].IsHeader);
        Assert.True(entries[0].HasSelectedTracks);
        Assert.True(entries[0].HasPlayingTrack);
        Assert.True(entries[0].HasPlayingAndSelected);

        Assert.Contains(ModuleRegistry.CustomizationSubmodules, m => m.Id == ModuleIds.CustomTags && m.Title == "Tags & Covers");
    }

    [Fact]
    public void Ui11_ExperimentalAndUnrecognizedDeviceNotificationsWork()
    {
        var verified = new WalkmanDeviceSummary(
            "live:e507", "NW-E507", 2005, "NW-E500", 1.0, 0.2, 90, ["ATRAC3plus"], "OLED", ScreenTintMode.Cyan, "1.0",
            IsLiveHardware: true, SupportStatus: Walkman.Contracts.Devices.SupportStatus.Verified);
        Assert.Null(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildExperimentalDeviceNotification(verified));

        var demoExp = new WalkmanDeviceSummary(
            "demo:a1000", "NW-A1000", 2005, "NW-A1000", 6.0, 1.0, 80, ["ATRAC3plus"], "OLED", ScreenTintMode.Cyan, "2.0",
            IsDemoDevice: true, SupportStatus: Walkman.Contracts.Devices.SupportStatus.Experimental);
        Assert.Null(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildExperimentalDeviceNotification(demoExp));

        var liveExp = demoExp with { Id = "live:a1000", IsDemoDevice = false, IsLiveHardware = true };
        var expNotice = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildExperimentalDeviceNotification(liveExp);
        Assert.NotNull(expNotice);
        Assert.Contains("NW-A1000", expNotice.Value.Title);
        Assert.Contains("not been hardware-verified", expNotice.Value.Message);

        var liveUnsup = liveExp with { SupportStatus = Walkman.Contracts.Devices.SupportStatus.Unsupported };
        var unsupNotice = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildExperimentalDeviceNotification(liveUnsup);
        Assert.NotNull(unsupNotice);
        Assert.Contains("Unsupported", unsupNotice.Value.Title);

        var probe = new Walkman.Contracts.Devices.DeviceDetectionProbe(
            0x054C, 0x0999, "SONY_UNKNOWN", "F:\\", 0xABCD1234, 2_000_000_000, 1, 1, [], []);
        var probeNotice = Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildUnrecognizedSonyProbeNotification(probe);
        Assert.NotNull(probeNotice);
        Assert.Contains("0x054C:0x0999", probeNotice.Value.Message);
        Assert.Contains("Dump Wizard", probeNotice.Value.Message);
        Assert.Null(Walkman.L3.AppShell.ViewModels.MainWindowViewModel.BuildUnrecognizedSonyProbeNotification(null));
    }

    [Fact]
    public void Ui12_13_14_PerfFileManagerUxAndLibraryPolishWork()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"apollo-ui14-{Guid.NewGuid():N}");
        try
        {
            // UI-14: Column visibility persistence
            var saved = new AppUserSettings(ShowLibColAlbum: false, ShowLibColRating: true, ShowLibColCodec: false, ShowLibColDuration: true);
            AppSettingsStore.Save(saved, tempDir);
            var loaded = AppSettingsStore.Load(tempDir);
            Assert.False(loaded.ShowLibColAlbum);
            Assert.True(loaded.ShowLibColRating);
            Assert.False(loaded.ShowLibColCodec);
            Assert.True(loaded.ShowLibColDuration);

            // UI-13: TrackListEntry mutable drop-over & play/selection notifications
            var entry = new TrackListEntry { IsHeader = true, HeaderKey = "fmdest:Rock", HeaderTitle = "Rock" };
            var changedProps = new List<string>();
            entry.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changedProps.Add(e.PropertyName); };
            entry.IsDropTargetOver = true;
            entry.HasPlayingTrack = true;
            entry.HasSelectedTracks = true;
            Assert.Contains(nameof(TrackListEntry.IsDropTargetOver), changedProps);
            Assert.Contains(nameof(TrackListEntry.HasPlayingTrack), changedProps);
            Assert.Contains(nameof(TrackListEntry.HasPlayingAndSelected), changedProps);

            // UI-13: ViewModel in-place folder expand/collapse, Shift+Click range, transfer lock & no-jitter playback
            var vm = new Walkman.L3.AppShell.ViewModels.MainWindowViewModel(
                new Walkman.L3.AppShell.Services.WalkmanBackendService(),
                new Walkman.L3.AppShell.Services.AudioPlaybackService(),
                suppressSettingsSave: true);

            Assert.True(vm.IsFileTransferUnlocked);
            vm.TransferJob = new ActiveTransferJob("j1", "song.flac", "Encoding", "ATRAC3+", 0, 1, 10, 5, false);
            Assert.False(vm.IsFileTransferUnlocked);
            Assert.False(vm.CanCommitPendingDeviceChanges);
            vm.TransferJob = null;
            Assert.True(vm.IsFileTransferUnlocked);

            // No-jitter playback: PlayOrToggleTrack must not rebuild FileManagerDestEntries
            var firstDestEntry = vm.FileManagerDestEntries.FirstOrDefault();
            if (firstDestEntry is not null && vm.FileManagerSourceTracks.Count > 0)
            {
                vm.PlayOrToggleTrack(vm.FileManagerSourceTracks[0]);
                Assert.Same(firstDestEntry, vm.FileManagerDestEntries.FirstOrDefault());
                vm.CloseGlobalPlayerCommand.Execute(null);
            }

            // In-place folder collapse/expand
            var firstSrcHeader = vm.FileManagerSourceEntries.FirstOrDefault(e => e.IsHeader);
            if (firstSrcHeader is not null)
            {
                int beforeCount = vm.FileManagerSourceEntries.Count;
                vm.ToggleFolderExpandedCommand.Execute(firstSrcHeader.HeaderKey);
                Assert.False(firstSrcHeader.IsExpanded);
                Assert.True(vm.FileManagerSourceEntries.Count < beforeCount);
                vm.ToggleFolderExpandedCommand.Execute(firstSrcHeader.HeaderKey);
                Assert.True(firstSrcHeader.IsExpanded);
                Assert.Equal(beforeCount, vm.FileManagerSourceEntries.Count);
            }

            // Shift+Click range selection
            var srcTracks = vm.FileManagerSourceEntries.Where(e => e.IsTrack && e.Track is not null).Select(e => e.Track!).ToList();
            if (srcTracks.Count >= 2)
            {
                foreach (var t in srcTracks) t.IsSelected = false;
                vm.SelectTrackRange(vm.FileManagerSourceEntries, srcTracks[0], srcTracks[1], isDestSelection: false, targetState: true);
                Assert.True(srcTracks[0].IsSelected);
                Assert.True(srcTracks[1].IsSelected);
            }
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
    public void Ui15_16_ScrollStableSelectionVectorInfoAndUnifiedCustomizationWork()
    {
        var vm = new Walkman.L3.AppShell.ViewModels.MainWindowViewModel(
            new Walkman.L3.AppShell.Services.WalkmanBackendService(),
            new Walkman.L3.AppShell.Services.AudioPlaybackService(),
            suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;

        // UI-15: "Update" button never shows trailing "*"
        vm.HasPendingDeviceTableChanges = false;
        Assert.Equal("Update", vm.UpdateDeviceTableButtonLabel);
        vm.HasPendingDeviceTableChanges = true;
        Assert.Equal("Update", vm.UpdateDeviceTableButtonLabel);

        // UI-15: NotifySelectionChanged preserves FileManagerSourceEntries references in-place (no scroll jump)
        var firstSrcEntry = vm.FileManagerSourceEntries.FirstOrDefault();
        if (firstSrcEntry is not null && vm.FileManagerSourceTracks.Count > 0)
        {
            vm.FileManagerSourceTracks[0].IsSelected = !vm.FileManagerSourceTracks[0].IsSelected;
            vm.NotifySelectionChanged();
            Assert.Same(firstSrcEntry, vm.FileManagerSourceEntries.FirstOrDefault());
        }

        // UI-15: NotifyDestSelectionChanged updates fmdest header HasSelectedTracks in-place
        var firstDestHeader = vm.FileManagerDestEntries.FirstOrDefault(e => e.IsHeader);
        if (firstDestHeader is not null && vm.FileManagerDestTracks.Count > 0)
        {
            var match = vm.FileManagerDestTracks.FirstOrDefault(t =>
                string.Equals(string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName, firstDestHeader.HeaderTitle, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                match.IsDestSelected = true;
                vm.NotifyDestSelectionChanged();
                Assert.True(firstDestHeader.HasSelectedTracks);
            }
        }

        // UI-16: CustomizationEntries grouped folders, sort, collapse/expand & SelectFolderByKey
        Assert.Contains(vm.CustomizationEntries, e => e.IsHeader);
        vm.CustomSortField = TrackSortField.Artist;
        vm.CustomSortDesc = true;
        var custHeader = vm.CustomizationEntries.FirstOrDefault(e => e.IsHeader);
        if (custHeader is not null)
        {
            int countBefore = vm.CustomizationEntries.Count;
            vm.ToggleFolderExpandedCommand.Execute(custHeader.HeaderKey);
            Assert.False(custHeader.IsExpanded);
            Assert.True(vm.CustomizationEntries.Count < countBefore);
            vm.ToggleFolderExpandedCommand.Execute(custHeader.HeaderKey);
            Assert.True(custHeader.IsExpanded);
            Assert.Equal(countBefore, vm.CustomizationEntries.Count);

            vm.SelectFolderByKeyCommand.Execute(custHeader.HeaderKey);
            Assert.True(custHeader.HasSelectedTracks);
        }
    }

    [Fact]
    public void AudFix04_SkiaSafetyPreparedSearchBulkCollectionAndIdleGuardsWork()
    {
        // BulkObservableCollection.ReplaceAll emits a single Reset notification
        var bulk = new BulkObservableCollection<int>();
        int resets = 0;
        bulk.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        bulk.ReplaceAll([1, 2, 3, 4]);
        Assert.Equal(1, resets);
        Assert.Equal([1, 2, 3, 4], bulk);

        // SearchTextMatcher.Prepare + zero-allocation Matches
        var pq = SearchTextMatcher.Prepare("midnight horizon");
        Assert.True(SearchTextMatcher.Matches(in pq, "Midnight Horizon (2004 Remaster)", "Mondo Grosso"));
        Assert.False(SearchTextMatcher.Matches(in pq, "Silver Rain", "Cornelius"));

        // IsFallbackDiscSpinning respects IsPlaying && !PlayerHasCoverBitmap && !ReducedBlur
        var vm = new Walkman.L3.AppShell.ViewModels.MainWindowViewModel(
            new Walkman.L3.AppShell.Services.WalkmanBackendService(),
            new Walkman.L3.AppShell.Services.AudioPlaybackService(),
            suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;
        Assert.False(vm.IsFallbackDiscSpinning);
        vm.ReducedBlur = true;
        Assert.False(vm.IsFallbackDiscSpinning);
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
