using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;
using Xunit;

namespace Walkman.M0.Tests;

public sealed class MilestoneM49Batch6LogicTests
{
    [Fact]
    public async Task RememberChoice_ConverterAndFileManager_PersistAndRestoreAcrossRestart()
    {
        string tempSettingsDir = Path.Combine(Path.GetTempPath(), $"apollo_m49_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSettingsDir);
        try
        {
            await using (var backend1 = new WalkmanBackendService())
            using (var player1 = new AudioPlaybackService())
            using (var vm1 = new MainWindowViewModel(
                       backend1,
                       player1,
                       autostart: true,
                       suppressSettingsSave: false,
                       settingsDirectoryOverride: tempSettingsDir))
            {
                vm1.FmUploadMode = UploadMode.Quality;
                vm1.FmNormalizeVolume = false;
                vm1.FmRewriteTables = true;
                vm1.FmSafeEjectAfter = true;
                vm1.FmRememberChoice = true;

                vm1.ConvTargetCodec = TargetCodec.Mp3;
                vm1.ConvBitrate = BitrateOption.K192;
                vm1.ConvSampleRate = SampleRateOption.Hz48000;
                vm1.ConvNormalizeVolume = false;
                vm1.ConvRememberChoice = true;
            }

            await using var backend2 = new WalkmanBackendService();
            using var player2 = new AudioPlaybackService();
            using var vm2 = new MainWindowViewModel(
                backend2,
                player2,
                autostart: true,
                suppressSettingsSave: false,
                settingsDirectoryOverride: tempSettingsDir);

            Assert.True(vm2.FmRememberChoice);
            Assert.True(vm2.IsFmModeLocked);
            Assert.Equal(UploadMode.Quality, vm2.FmUploadMode);
            Assert.False(vm2.FmNormalizeVolume);
            Assert.True(vm2.FmRewriteTables);
            Assert.True(vm2.FmSafeEjectAfter);

            Assert.True(vm2.ConvRememberChoice);
            Assert.True(vm2.IsConvCodecLocked);
            Assert.Equal(TargetCodec.Mp3, vm2.ConvTargetCodec);
            Assert.Equal(BitrateOption.K192, vm2.ConvBitrate);
            Assert.Equal(SampleRateOption.Hz48000, vm2.ConvSampleRate);
            Assert.False(vm2.ConvNormalizeVolume);
        }
        finally
        {
            try { Directory.Delete(tempSettingsDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DestLocations_ContainsOnlyWalkmanPlayers_AndRejectsPcFolders()
    {
        await using var backend = new WalkmanBackendService();
        using var player = new AudioPlaybackService();
        using var vm = new MainWindowViewModel(backend, player, autostart: true, suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;

        Assert.NotEmpty(vm.DestLocations);
        Assert.All(vm.DestLocations, loc => Assert.Equal("device", loc.Kind));
        Assert.DoesNotContain(vm.DestLocations, loc => loc.Id is "pc-library" or "pc-converted");

        var pcLoc = vm.AllLocations.First(l => l.Id == "pc-library");
        vm.SelectedDestLocation = pcLoc;
        Assert.NotNull(vm.SelectedDestLocation);
        Assert.Equal("device", vm.SelectedDestLocation!.Kind);

        vm.IsDemoModeEnabled = false;
        Assert.Empty(vm.DestLocations);
        Assert.Null(vm.SelectedDestLocation);
    }

    [Fact]
    public async Task MyLibrary_SelectFolder_DoesNotStageForTransfer_AndStageLibraryFolderForTransfer_StagesTracks()
    {
        await using var backend = new WalkmanBackendService();
        using var player = new AudioPlaybackService();
        using var vm = new MainWindowViewModel(backend, player, autostart: true, suppressSettingsSave: true);
        vm.IsDemoModeEnabled = true;

        foreach (var t in vm.FileManagerSourceTracks)
        {
            t.IsSelected = false;
        }
        vm.NotifySelectionChanged();
        Assert.Equal(0, vm.TransferSelectedCount);

        var firstLibHeader = vm.LibraryEntries.First(e => e.IsHeader);
        string folderKey = firstLibHeader.HeaderKey;
        Assert.StartsWith("lib:", folderKey, StringComparison.OrdinalIgnoreCase);

        vm.SelectFolderByKeyCommand.Execute(folderKey);
        Assert.True(vm.LibrarySelectedCount > 0);
        Assert.Equal(0, vm.TransferSelectedCount);
        Assert.True(firstLibHeader.HasSelectedTracks);

        vm.StageLibraryFolderForTransferCommand.Execute(folderKey);
        Assert.True(vm.TransferSelectedCount > 0);
    }
}
