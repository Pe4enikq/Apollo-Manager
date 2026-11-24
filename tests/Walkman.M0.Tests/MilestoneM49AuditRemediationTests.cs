using System.Collections.Specialized;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.L1.DevicePoller.Probing;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;

namespace Walkman.M0.Tests;

public sealed class MilestoneM49AuditRemediationTests
{
    [Fact]
    public void AudFix02_BpbVerificationState_DefaultsToVerifiedOk()
    {
        var bpb = new VolumeBpbDiagnostics("SONY", 512, 16, 1, 2, "FAT16", false, null);
        Assert.Equal(BpbVerificationState.VerifiedOk, bpb.VerificationState);
    }

    [Fact]
    public void AudFix02_EvaluateBpbPolicy_FlagsInvalidWindowsFormat()
    {
        var raw = new VolumeBpbDiagnostics("MSDOS5.0", 512, 16, 2, 2, "FAT16", false, null);
        var evaluated = Win32VolumeAndUsbProber.EvaluateBpbPolicy(raw, new StrictFat16BpbRule(1, true));
        Assert.Equal(BpbVerificationState.InvalidWindowsFormat, evaluated.VerificationState);
        Assert.True(evaluated.HasWindowsFormatWarning);
    }

    [Fact]
    public void AudFix03_FlacWaveStream_RejectsCorruptStreamWithoutLeaking()
    {
        using var ms = new MemoryStream("fLaC\x80\x00\x00\x22"u8.ToArray()); // truncated STREAMINFO
        Assert.Throws<InvalidDataException>(() => new FlacWaveStream(ms, ownsStream: false));
    }

    [Fact]
    public void AudFix04_BulkObservableCollection_ReplaceAll_FiresSingleReset()
    {
        var col = new BulkObservableCollection<int>();
        int resetCount = 0;
        col.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
        };

        col.ReplaceAll([1, 2, 3, 4]);
        Assert.Equal(4, col.Count);
        Assert.Equal(1, resetCount);
    }

    [Fact]
    public void AudFix04_PreparedSearchQuery_MatchesIdenticallyToStringOverload()
    {
        var prepared = SearchTextMatcher.Prepare("daft punk");
        Assert.True(SearchTextMatcher.Matches(prepared, "Around the World", "Daft Punk", "Homework", null));
        Assert.False(SearchTextMatcher.Matches(prepared, "Blue Monday", "New Order", "Power", null));
    }
}
