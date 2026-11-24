namespace Walkman.L3.AppShell.Core;

public static class ModuleIds
{
    public const string FileManager = "file-manager";
    public const string Library = "library";
    public const string Converter = "converter";
    public const string PairSync = "pair-sync";
    public const string Customization = "customization";
    public const string CustomOled = "custom-oled";
    public const string CustomSubtitles = "custom-subtitles";
    public const string CustomTags = "custom-tags";
    public const string CustomFirmware = "custom-firmware";
    public const string DeviceSystem = "device-system";
    public const string DeviceDumpWizard = "device-dump";
    public const string DeviceTechInfo = "device-tech";
    public const string AllFeatures = "all-features";
    public const string Settings = "settings";
    public const string Back = "__back__";
}

public enum ModuleAccent
{
    Lime,
    Blue,
    Silver
}

public enum ModuleStatus
{
    Mvp,
    ComingSoon
}

public enum StartScreenPhase
{
    IdleBlobs,
    MedallionReady,
    RingExpanded
}

public enum StartScreenEvent
{
    PointerEnter,
    PointerLeave,
    ClickPlus,
    CollapseRing,
    ReplayIntro
}

public enum RingSurface
{
    StartRing,
    JogDial
}

public sealed record ModuleDescriptor(
    string Id,
    string Title,
    string ShortTitle,
    string Subtitle,
    string Group,
    int Order,
    double RadialWeight,
    double RadialThickness,
    bool IsLargeIcon,
    ModuleAccent Accent,
    ModuleStatus Status,
    bool RequiresDevice,
    bool ShowInStartRing,
    bool ShowInJogDial,
    IReadOnlyList<ModuleDescriptor>? Children = null);

public sealed record ComputedRingSector(
    ModuleDescriptor Module,
    double StartAngleDeg,
    double EndAngleDeg,
    double SweepAngleDeg,
    double MidAngleDeg,
    double InnerRadius,
    double OuterRadius,
    double Thickness,
    double CornerRadius,
    double LabelX,
    double LabelY);

public static class ModuleRegistry
{
    public static readonly IReadOnlyList<ModuleDescriptor> CustomizationSubmodules =
    [
        new(ModuleIds.CustomOled, "Screen & OLED", "Screen", "Post-MVP studio", "hardware", 10, 1.05, 140, true, ModuleAccent.Lime, ModuleStatus.ComingSoon, false, true, true),
        new(ModuleIds.CustomSubtitles, "Subtitles", "Subtitles", "Synced LRC", "hardware", 20, 1.0, 128, false, ModuleAccent.Blue, ModuleStatus.Mvp, false, true, true),
        new(ModuleIds.CustomTags, "Tags & Covers", "Tags", "ID3, OMA & covers", "hardware", 30, 1.0, 128, false, ModuleAccent.Silver, ModuleStatus.Mvp, false, true, true)
    ];

    public static readonly IReadOnlyList<ModuleDescriptor> DeviceSystemSubmodules =
    [
        new(ModuleIds.PairSync, "Pair & Sync", "USB Sync", "Live device scan", "hardware", 10, 1.06, 138, true, ModuleAccent.Lime, ModuleStatus.Mvp, false, true, true),
        new(ModuleIds.CustomFirmware, "Firmware", "Firmware", "Post-MVP patcher", "hardware", 20, 1.0, 126, false, ModuleAccent.Blue, ModuleStatus.ComingSoon, false, true, true),
        new(ModuleIds.DeviceDumpWizard, "Dump Wizard", "Dump ZIP", "Diagnostic archive", "hardware", 30, 1.0, 126, false, ModuleAccent.Lime, ModuleStatus.Mvp, false, true, true),
        new(ModuleIds.DeviceTechInfo, "Technical Info", "Tech Info", "SysInfo & registers", "hardware", 40, 1.0, 126, false, ModuleAccent.Silver, ModuleStatus.Mvp, false, true, true)
    ];

    public static readonly IReadOnlyList<ModuleDescriptor> DefaultModules =
    [
        new(ModuleIds.FileManager, "File Manager", "Files", "Transfer & sync", "core", 10, 1.02, 166, true, ModuleAccent.Lime, ModuleStatus.Mvp, true, true, true),
        new(ModuleIds.Library, "My Library", "Library", "Catalog & player", "core", 15, 1.0, 138, true, ModuleAccent.Lime, ModuleStatus.Mvp, false, false, true),
        new(ModuleIds.Converter, "Codec Converter", "Converter", "Offline batch audio", "core", 20, 1.0, 134, false, ModuleAccent.Blue, ModuleStatus.Mvp, false, true, true),
        new(ModuleIds.Customization, "Customization", "Custom", "OLED, lyrics & tags", "hardware", 30, 1.02, 128, false, ModuleAccent.Silver, ModuleStatus.Mvp, false, true, true, CustomizationSubmodules),
        new(ModuleIds.DeviceSystem, "Device & System", "System", "Sync, FW, dump & info", "hardware", 40, 1.0, 128, false, ModuleAccent.Blue, ModuleStatus.Mvp, false, true, true, DeviceSystemSubmodules)
    ];

    public static StartScreenPhase TransitionStartState(StartScreenPhase current, StartScreenEvent ev) => ev switch
    {
        StartScreenEvent.PointerEnter => current == StartScreenPhase.IdleBlobs ? StartScreenPhase.MedallionReady : current,
        StartScreenEvent.PointerLeave => current,
        StartScreenEvent.ClickPlus => current == StartScreenPhase.MedallionReady ? StartScreenPhase.RingExpanded : current,
        StartScreenEvent.CollapseRing => current == StartScreenPhase.RingExpanded ? StartScreenPhase.MedallionReady : current,
        StartScreenEvent.ReplayIntro => StartScreenPhase.IdleBlobs,
        _ => current
    };

    public static (IReadOnlyList<ModuleDescriptor> Items, int BackSlotIndex) ResolveActiveRingModules(
        IReadOnlyList<ModuleDescriptor> registry,
        RingSurface surface,
        bool deviceConnected,
        string? activeSubmenuParentId,
        int? lockedBackIndex = null)
    {
        var rootVisible = registry
            .Where(m => surface == RingSurface.StartRing ? m.ShowInStartRing : m.ShowInJogDial)
            .Where(m => deviceConnected || !m.RequiresDevice)
            .OrderBy(m => m.Order)
            .ToList();

        if (string.IsNullOrEmpty(activeSubmenuParentId))
        {
            return (rootVisible, -1);
        }

        int parentIndex = rootVisible.FindIndex(m => m.Id == activeSubmenuParentId);
        var parent = parentIndex >= 0
            ? rootVisible[parentIndex]
            : registry.FirstOrDefault(m => m.Id == activeSubmenuParentId);
        if (parent is null || parent.Children is null || parent.Children.Count == 0)
        {
            return (rootVisible, -1);
        }

        var visibleChildren = parent.Children
            .Where(c => surface == RingSurface.StartRing ? c.ShowInStartRing : c.ShowInJogDial)
            .Where(c => deviceConnected || !c.RequiresDevice)
            .OrderBy(c => c.Order)
            .ToList();

        var backDescriptor = new ModuleDescriptor(
            Id: ModuleIds.Back,
            Title: "Back",
            ShortTitle: "Back",
            Subtitle: parent.ShortTitle,
            Group: parent.Group,
            Order: parent.Order,
            RadialWeight: parent.RadialWeight * 0.92,
            RadialThickness: Math.Max(108, parent.RadialThickness - 6),
            IsLargeIcon: false,
            Accent: ModuleAccent.Silver,
            Status: ModuleStatus.Mvp,
            RequiresDevice: false,
            ShowInStartRing: true,
            ShowInJogDial: true);

        int resolvedIndex = lockedBackIndex ?? Math.Max(0, parentIndex);
        int insertAt = Math.Clamp(resolvedIndex, 0, visibleChildren.Count);
        visibleChildren.Insert(insertAt, backDescriptor);
        return (visibleChildren, insertAt);
    }

    public static IReadOnlyList<ComputedRingSector> ComputeRingSectors(
        IReadOnlyList<ModuleDescriptor> items,
        double cx,
        double cy,
        double innerRadius = 136,
        double gapDeg = 7,
        double cornerRadius = 22,
        int? anchorIndex = null,
        double? anchorAngleDeg = null)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ComputedRingSector>();
        }

        double totalGap = gapDeg * items.Count;
        double availableDeg = Math.Max(0, 360.0 - totalGap);
        double totalWeight = items.Sum(m => Math.Max(0.25, m.RadialWeight));
        double[] sweeps = items.Select(m => (Math.Max(0.25, m.RadialWeight) / totalWeight) * availableDeg).ToArray();

        double startOffset = -90.0 - sweeps[0] / 2.0;
        if (anchorIndex.HasValue && anchorAngleDeg.HasValue && anchorIndex.Value >= 0 && anchorIndex.Value < items.Count)
        {
            double offsetBeforeAnchor = 0;
            for (int i = 0; i < anchorIndex.Value; i++)
            {
                offsetBeforeAnchor += sweeps[i] + gapDeg;
            }

            double anchorMidFromStart = offsetBeforeAnchor + sweeps[anchorIndex.Value] / 2.0;
            startOffset = anchorAngleDeg.Value - anchorMidFromStart;
        }

        var sectors = new List<ComputedRingSector>(items.Count);
        double cursor = startOffset;
        for (int i = 0; i < items.Count; i++)
        {
            var mod = items[i];
            double sweep = sweeps[i];
            double startDeg = cursor;
            double endDeg = cursor + sweep;
            double midDeg = startDeg + sweep / 2.0;
            cursor = endDeg + gapDeg;

            double outerRadius = innerRadius + mod.RadialThickness;
            double labelRadius = innerRadius + mod.RadialThickness * 0.51;
            double rad = midDeg * Math.PI / 180.0;

            sectors.Add(new ComputedRingSector(
                Module: mod,
                StartAngleDeg: startDeg,
                EndAngleDeg: endDeg,
                SweepAngleDeg: sweep,
                MidAngleDeg: midDeg,
                InnerRadius: innerRadius,
                OuterRadius: outerRadius,
                Thickness: mod.RadialThickness,
                CornerRadius: cornerRadius,
                LabelX: cx + labelRadius * Math.Cos(rad),
                LabelY: cy + labelRadius * Math.Sin(rad)));
        }

        return sectors;
    }

    public static IReadOnlyList<ComputedRingSector> ComputeJogDialFanSectors(
        IReadOnlyList<ModuleDescriptor> items,
        double cx,
        double cy,
        double innerRadius = 88,
        double thicknessScale = 1.28,
        double minAngleDeg = -78,
        double maxAngleDeg = 54,
        double gapDeg = 4.2,
        double cornerRadius = 17)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ComputedRingSector>();
        }

        double totalSpan = maxAngleDeg - minAngleDeg;
        double totalGap = gapDeg * Math.Max(0, items.Count - 1);
        double availableDeg = Math.Max(0, totalSpan - totalGap);
        double totalWeight = items.Sum(m => Math.Max(0.25, m.RadialWeight));

        var sectors = new List<ComputedRingSector>(items.Count);
        double cursor = minAngleDeg;

        foreach (var mod in items)
        {
            double sweep = (Math.Max(0.25, mod.RadialWeight) / totalWeight) * availableDeg;
            double startDeg = cursor;
            double endDeg = cursor + sweep;
            double midDeg = startDeg + sweep / 2.0;
            cursor = endDeg + gapDeg;

            double thickness = Math.Round(mod.RadialThickness * thicknessScale);
            double outerRadius = innerRadius + thickness;
            double labelRadius = innerRadius + thickness * 0.54;
            double rad = midDeg * Math.PI / 180.0;

            sectors.Add(new ComputedRingSector(
                Module: mod,
                StartAngleDeg: startDeg,
                EndAngleDeg: endDeg,
                SweepAngleDeg: sweep,
                MidAngleDeg: midDeg,
                InnerRadius: innerRadius,
                OuterRadius: outerRadius,
                Thickness: thickness,
                CornerRadius: cornerRadius,
                LabelX: cx + labelRadius * Math.Cos(rad),
                LabelY: cy + labelRadius * Math.Sin(rad)));
        }

        return sectors;
    }
}
