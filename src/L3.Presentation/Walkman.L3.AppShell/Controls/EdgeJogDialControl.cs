using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Controls;

public sealed class EdgeJogDialControl : Control
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<EdgeJogDialControl, bool>(nameof(IsOpen));

    public static readonly StyledProperty<string> ActiveModuleIdProperty =
        AvaloniaProperty.Register<EdgeJogDialControl, string>(nameof(ActiveModuleId), ModuleIds.Library);

    public static readonly StyledProperty<bool> DeviceConnectedProperty =
        AvaloniaProperty.Register<EdgeJogDialControl, bool>(nameof(DeviceConnected));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<EdgeJogDialControl, bool>(nameof(IsDark));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string ActiveModuleId
    {
        get => GetValue(ActiveModuleIdProperty);
        set => SetValue(ActiveModuleIdProperty, value);
    }

    public bool DeviceConnected
    {
        get => GetValue(DeviceConnectedProperty);
        set => SetValue(DeviceConnectedProperty, value);
    }

    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public event Action<string>? ModuleActivated;
    public event Action? ReturnStartRequested;

    private enum DialHoverKind { None, CollapsedTab, HomeHalf, HideHalf, FanSector }

    private const float CollapsedWidth = 74f;
    private const float CollapsedHeight = 148f;
    private const float ExpandedWidth = 620f;
    private const float ExpandedHeight = 680f;
    private const float DialRadius = 64f;
    private const float ExpandedCenterX = 96f;
    private const float ExpandedCenterY = ExpandedHeight * 0.5f; // 340

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, float> _hoverT = new(StringComparer.Ordinal);
    private double _lastTimeSec;
    private bool _isAttached;
    private bool _frameRequested;

    private double _openT;
    private double _fanSwapT = 1.0;
    private float _orbHoverT;
    private bool _pendingSwap;
    private string? _pendingParentId;
    private int? _pendingLockedBackIdx;
    private string? _submenuParentId;
    private int? _lockedBackIndex;

    private static readonly IReadOnlyDictionary<string, float> EmptyHover = new Dictionary<string, float>(0, StringComparer.Ordinal);
    private IReadOnlyList<ComputedRingSector> _sectors = Array.Empty<ComputedRingSector>();
    private AeroSkiaHelpers.RefCountedSectorBatch? _cachedBatch;
    private AeroSkiaHelpers.RefCountedSkImage? _cachedOrbImage;
    private IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> _cachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
    private readonly Dictionary<(bool IsDark, bool DeviceConnected, string SubmenuKey), (IReadOnlyList<ComputedRingSector> Sectors, IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> Items, AeroSkiaHelpers.RefCountedSectorBatch Batch)> _batchCache = new();
    private readonly Dictionary<bool, AeroSkiaHelpers.RefCountedSkImage> _orbCache = new();
    private Window? _hostWindow;
    private DialHoverKind _hoverKind = DialHoverKind.None;
    private string? _hoveredSectorId;
    private IReadOnlyList<string>? _tooltipCategories;
    private const float OrbPad = 24f;
    private const double MinFrameIntervalSec = 1.0 / 60.0;
    private double _lastRenderSec;

    public EdgeJogDialControl()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Width = CollapsedWidth;
        Height = CollapsedHeight;
        BakeOrbImage();
        RebuildSectors();
    }

    static EdgeJogDialControl()
    {
        IsOpenProperty.Changed.AddClassHandler<EdgeJogDialControl>((c, _) => c.OnIsOpenChanged());
        DeviceConnectedProperty.Changed.AddClassHandler<EdgeJogDialControl>((c, _) => c.RebuildSectors());
        ActiveModuleIdProperty.Changed.AddClassHandler<EdgeJogDialControl>((c, _) => c.InvalidateVisual());
        IsDarkProperty.Changed.AddClassHandler<EdgeJogDialControl>((c, _) =>
        {
            c.BakeOrbImage();
            c.BakeSectorImagesOnly();
        });
        IsVisibleProperty.Changed.AddClassHandler<EdgeJogDialControl>((c, _) => c.StartAnimationIfNeeded());
    }

    public void SnapOpenForCapture(bool open, string? hoveredSectorId = null, string? submenuParentId = null)
    {
        IsOpen = open;
        _submenuParentId = submenuParentId;
        if (!string.IsNullOrEmpty(submenuParentId))
        {
            var root = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.JogDial, DeviceConnected, null);
            int idx = root.Items.ToList().FindIndex(m => m.Id == submenuParentId);
            _lockedBackIndex = idx >= 0 ? idx : null;
        }
        else
        {
            _lockedBackIndex = null;
        }

        _openT = open ? 1.0 : 0.0;
        _fanSwapT = 1.0;
        _pendingSwap = false;
        Width = open ? ExpandedWidth : CollapsedWidth;
        Height = open ? ExpandedHeight : CollapsedHeight;
        RebuildSectors();

        _hoveredSectorId = hoveredSectorId;
        _hoverT.Clear();

        if (hoveredSectorId is not null)
        {
            _hoverKind = DialHoverKind.FanSector;
            _hoverT[hoveredSectorId] = 1f;
            var s = _sectors.FirstOrDefault(sec => sec.Module.Id == hoveredSectorId);
            if (s?.Module.Children is { Count: > 0 } children)
            {
                _tooltipCategories = children.Select(c => c.Title).ToArray();
            }
        }
        else
        {
            _hoverKind = DialHoverKind.None;
            _tooltipCategories = null;
        }

        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }
        if (_cachedOrbImage is null)
        {
            BakeOrbImage();
        }
        if (_cachedBatch is null)
        {
            RebuildSectors();
        }
        StartAnimationIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }
        _cachedBatch = null;
        _cachedOrbImage = null;
        _cachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        foreach (var entry in _batchCache.Values)
        {
            entry.Batch.Release();
        }
        _batchCache.Clear();
        foreach (var orb in _orbCache.Values)
        {
            orb.Release();
        }
        _orbCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            StartAnimationIfNeeded();
        }
    }

    private void BakeOrbImage()
    {
        if (!_isAttached)
        {
            return;
        }
        if (_orbCache.TryGetValue(IsDark, out var cachedOrb))
        {
            _cachedOrbImage = cachedOrb;
            InvalidateVisual();
            return;
        }
        const float ss = 2f;
        float logicalSize = (DialRadius + OrbPad) * 2f;
        int pxSize = (int)(logicalSize * ss);
        float c = logicalSize * 0.5f;

        using var bmp = new SKBitmap(pxSize, pxSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(ss);

            using (var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f))
            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = IsDark ? new SKColor(0, 0, 0, 140) : new SKColor(40, 62, 88, 56),
                MaskFilter = shadowBlur
            })
            {
                canvas.DrawCircle(c, c + 5f, DialRadius, shadow);
            }

            SKColor[] sweepColors = IsDark
                ? [new SKColor(59, 67, 82), new SKColor(39, 45, 56), new SKColor(76, 86, 105), new SKColor(34, 39, 49), new SKColor(63, 72, 88), new SKColor(41, 47, 59), new SKColor(59, 67, 82)]
                : [new SKColor(238, 243, 248), new SKColor(210, 220, 230), new SKColor(248, 251, 255), new SKColor(202, 213, 226), new SKColor(242, 246, 250), new SKColor(214, 224, 234), new SKColor(238, 243, 248)];

            using (var sweep = SKShader.CreateSweepGradient(new SKPoint(c, c), sweepColors, [0f, 0.14f, 0.33f, 0.54f, 0.76f, 0.91f, 1f]))
            using (var fill = new SKPaint { IsAntialias = true, IsDither = true, Shader = sweep })
            {
                canvas.DrawCircle(c, c, DialRadius, fill);
            }
        }

        AeroSkiaHelpers.ApplyTriangularDither(bmp, 0.95f);
        var holder = new AeroSkiaHelpers.RefCountedSkImage(SKImage.FromBitmap(bmp));
        _orbCache[IsDark] = holder;
        _cachedOrbImage = holder;
    }

    private void OnIsOpenChanged()
    {
        if (!IsOpen)
        {
            _submenuParentId = null;
            _lockedBackIndex = null;
            _pendingSwap = false;
            _fanSwapT = 1.0;
            _hoveredSectorId = null;
            _tooltipCategories = null;
            _hoverKind = DialHoverKind.None;
            RebuildSectors();
        }
        else
        {
            Width = ExpandedWidth;
            Height = ExpandedHeight;
        }

        StartAnimationIfNeeded();
    }

    private void RebuildSectors()
    {
        if (!_isAttached)
        {
            var (itemsPreview, _) = ModuleRegistry.ResolveActiveRingModules(
                ModuleRegistry.DefaultModules,
                RingSurface.JogDial,
                DeviceConnected,
                _submenuParentId,
                _lockedBackIndex);

            _sectors = ModuleRegistry.ComputeJogDialFanSectors(
                itemsPreview,
                ExpandedCenterX,
                ExpandedCenterY,
                innerRadius: DialRadius + 24,
                thicknessScale: 1.24,
                minAngleDeg: -72,
                maxAngleDeg: 58,
                gapDeg: 4.0,
                cornerRadius: 17);
            return;
        }

        string submenuKey = $"{_submenuParentId ?? string.Empty}|{_lockedBackIndex?.ToString() ?? ""}";
        var key = (IsDark, DeviceConnected, submenuKey);
        if (_batchCache.TryGetValue(key, out var cached))
        {
            _sectors = cached.Sectors;
            _cachedSectors = cached.Items;
            _cachedBatch = cached.Batch;
            InvalidateVisual();
            return;
        }

        var (items, _) = ModuleRegistry.ResolveActiveRingModules(
            ModuleRegistry.DefaultModules,
            RingSurface.JogDial,
            DeviceConnected,
            _submenuParentId,
            _lockedBackIndex);

        _sectors = ModuleRegistry.ComputeJogDialFanSectors(
            items,
            ExpandedCenterX,
            ExpandedCenterY,
            innerRadius: DialRadius + 24,
            thicknessScale: 1.24,
            minAngleDeg: -72,
            maxAngleDeg: 58,
            gapDeg: 4.0,
            cornerRadius: 17);

        BakeSectorImagesOnly();
    }

    private void BakeSectorImagesOnly()
    {
        if (!_isAttached)
        {
            return;
        }
        string submenuKey = $"{_submenuParentId ?? string.Empty}|{_lockedBackIndex?.ToString() ?? ""}";
        var key = (IsDark, DeviceConnected, submenuKey);
        if (_batchCache.TryGetValue(key, out var hit))
        {
            _sectors = hit.Sectors;
            _cachedSectors = hit.Items;
            _cachedBatch = hit.Batch;
            InvalidateVisual();
            return;
        }

        _cachedSectors = AeroSkiaHelpers.BakeSectorCache(
            _sectors,
            ExpandedCenterX,
            ExpandedCenterY,
            IsDark,
            isActiveSelector: null,
            glassOpacityBoost: 1.24f,
            bakeRotatedLabel: true);
        _cachedBatch = new AeroSkiaHelpers.RefCountedSectorBatch(_cachedSectors);
        _batchCache[key] = (_sectors, _cachedSectors, _cachedBatch);
        InvalidateVisual();
    }

    private bool HoverNeedsAnimation()
    {
        foreach (var sec in _sectors)
        {
            float target = sec.Module.Id == _hoveredSectorId ? 1f : 0f;
            if (MathF.Abs(_hoverT.GetValueOrDefault(sec.Module.Id) - target) > 0.005f)
            {
                return true;
            }
        }

        foreach (var kv in _hoverT)
        {
            if (kv.Key != _hoveredSectorId && kv.Value > 0.005f)
            {
                return true;
            }
        }

        return false;
    }

    private bool NeedsAnimation()
    {
        if (!IsVisible || _hostWindow?.WindowState == WindowState.Minimized)
        {
            return false;
        }

        double openTarget = IsOpen ? 1.0 : 0.0;
        double swapTarget = _pendingSwap ? 0.0 : 1.0;
        float orbTarget = _hoverKind is DialHoverKind.CollapsedTab or DialHoverKind.HomeHalf or DialHoverKind.HideHalf ? 1f : 0f;
        return Math.Abs(_openT - openTarget) > 0.004
               || _pendingSwap
               || Math.Abs(_fanSwapT - swapTarget) > 0.004
               || MathF.Abs(_orbHoverT - orbTarget) > 0.005f
               || HoverNeedsAnimation();
    }

    private void StartAnimationIfNeeded()
    {
        if (!_isAttached || _frameRequested || !NeedsAnimation())
        {
            return;
        }

        _lastTimeSec = _clock.Elapsed.TotalSeconds;
        _lastRenderSec = _lastTimeSec - MinFrameIntervalSec;
        ScheduleNextFrame();
    }

    private void ScheduleNextFrame()
    {
        if (!_isAttached || _frameRequested || !NeedsAnimation())
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _frameRequested = false;
        if (!_isAttached || !NeedsAnimation())
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastRenderSec < MinFrameIntervalSec)
        {
            ScheduleNextFrame();
            return;
        }

        _lastRenderSec = now;
        double dt = Math.Clamp(now - _lastTimeSec, 0.001, 0.05);
        _lastTimeSec = now;

        double openTarget = IsOpen ? 1.0 : 0.0;
        _openT = AeroSkiaHelpers.Damp(_openT, openTarget, 16.0, dt);
        if (Math.Abs(_openT - openTarget) < 0.004)
        {
            _openT = openTarget;
            if (!IsOpen)
            {
                Width = CollapsedWidth;
                Height = CollapsedHeight;
            }
        }

        if (_pendingSwap)
        {
            _fanSwapT = AeroSkiaHelpers.Damp(_fanSwapT, 0.0, 22.0, dt);
            if (_fanSwapT <= 0.06)
            {
                _fanSwapT = 0.06;
                _submenuParentId = _pendingParentId;
                _lockedBackIndex = _pendingLockedBackIdx;
                _pendingSwap = false;
                _hoverT.Clear();
                RebuildSectors();
            }
        }
        else if (_fanSwapT < 1.0)
        {
            _fanSwapT = AeroSkiaHelpers.Damp(_fanSwapT, 1.0, 18.0, dt);
            if (Math.Abs(_fanSwapT - 1.0) < 0.005)
            {
                _fanSwapT = 1.0;
            }
        }

        foreach (var sec in _sectors)
        {
            string id = sec.Module.Id;
            float target = id == _hoveredSectorId ? 1f : 0f;
            float next = AeroSkiaHelpers.Damp(_hoverT.GetValueOrDefault(id), target, 18f, dt);
            if (MathF.Abs(next - target) < 0.005f)
            {
                next = target;
            }

            _hoverT[id] = next;
        }

        float orbTarget = _hoverKind is DialHoverKind.CollapsedTab or DialHoverKind.HomeHalf or DialHoverKind.HideHalf ? 1f : 0f;
        _orbHoverT = AeroSkiaHelpers.Damp(_orbHoverT, orbTarget, 18f, dt);
        if (MathF.Abs(_orbHoverT - orbTarget) < 0.005f)
        {
            _orbHoverT = orbTarget;
        }

        InvalidateVisual();
        ScheduleNextFrame();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        var (kind, sector) = HitTest(pt);

        Cursor = kind == DialHoverKind.None ? Cursor.Default : AeroSkiaHelpers.HandCursor;
        string? nextSectorId = kind == DialHoverKind.FanSector ? sector?.Module.Id : null;

        if (kind != _hoverKind || nextSectorId != _hoveredSectorId)
        {
            _hoverKind = kind;
            _hoveredSectorId = nextSectorId;
            _tooltipCategories = (kind == DialHoverKind.FanSector && sector?.Module.Children is { Count: > 0 } children)
                ? children.Select(c => c.Title).ToArray()
                : null;
            StartAnimationIfNeeded();
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        bool hadState = _hoverKind != DialHoverKind.None || _hoveredSectorId is not null || _tooltipCategories is not null;
        _hoverKind = DialHoverKind.None;
        _hoveredSectorId = null;
        _tooltipCategories = null;
        Cursor = Cursor.Default;
        if (hadState)
        {
            StartAnimationIfNeeded();
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (kind, sector) = HitTest(e.GetPosition(this));
        switch (kind)
        {
            case DialHoverKind.CollapsedTab:
                IsOpen = true;
                e.Handled = true;
                break;
            case DialHoverKind.HideHalf:
                IsOpen = false;
                e.Handled = true;
                break;
            case DialHoverKind.HomeHalf:
                IsOpen = false;
                ReturnStartRequested?.Invoke();
                e.Handled = true;
                break;
            case DialHoverKind.FanSector when sector is not null:
                e.Handled = true;
                if (sector.Module.Id == ModuleIds.Back)
                {
                    TriggerSubmenuSwap(null, null);
                }
                else if (sector.Module.Children is { Count: > 0 })
                {
                    int idx = _sectors.ToList().FindIndex(s => s.Module.Id == sector.Module.Id);
                    TriggerSubmenuSwap(sector.Module.Id, idx >= 0 ? idx : null);
                }
                else
                {
                    ModuleActivated?.Invoke(sector.Module.Id);
                    IsOpen = false;
                }
                break;
            case DialHoverKind.None when IsOpen:
                IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void TriggerSubmenuSwap(string? nextParentId, int? nextLockedBackIdx)
    {
        _hoveredSectorId = null;
        _tooltipCategories = null;
        _pendingSwap = true;
        _pendingParentId = nextParentId;
        _pendingLockedBackIdx = nextLockedBackIdx;
        StartAnimationIfNeeded();
    }

    private (DialHoverKind Kind, ComputedRingSector? Sector) HitTest(Point pt)
    {
        double cy = Bounds.Height * 0.5;
        float cx = Math.Max(0f, (float)_openT * ExpandedCenterX);
        if (!IsOpen)
        {
            if (AeroSkiaHelpers.Hypot((float)pt.X - cx, (float)(pt.Y - cy)) <= DialRadius + 6f)
            {
                return (DialHoverKind.CollapsedTab, null);
            }

            return (DialHoverKind.None, null);
        }

        float distDial = AeroSkiaHelpers.Hypot((float)pt.X - cx, (float)(pt.Y - cy));
        if (distDial <= DialRadius + 2f)
        {
            return pt.X < cx ? (DialHoverKind.HomeHalf, null) : (DialHoverKind.HideHalf, null);
        }

        if (!_pendingSwap && _fanSwapT > 0.35)
        {
            float px = (float)pt.X;
            float py = (float)pt.Y;
            foreach (var cached in _cachedSectors)
            {
                if (cached.Path.Contains(px, py))
                {
                    return (DialHoverKind.FanSector, cached.Sector);
                }
            }
        }

        return (DialHoverKind.None, null);
    }

    private static bool IsSectorActive(ModuleDescriptor mod, string activeId)
    {
        if (mod.Id == activeId)
        {
            return true;
        }

        return mod.Children is not null && mod.Children.Any(c => c.Id == activeId);
    }

    public override void Render(DrawingContext context)
    {
        var batch = _cachedBatch;
        var sectors = batch?.TryAcquire();
        if (sectors is null)
        {
            batch = null;
            sectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        }

        var orbHolder = _cachedOrbImage;
        var orbImage = orbHolder?.TryAcquire();
        if (orbImage is null)
        {
            orbHolder = null;
        }

        IReadOnlyDictionary<string, float> hoverSnapshot = _hoverT.Count == 0
            ? EmptyHover
            : new Dictionary<string, float>(_hoverT, StringComparer.Ordinal);
        context.Custom(new JogDialDrawOp(
            new Rect(Bounds.Size),
            IsOpen,
            (float)_openT,
            (float)_fanSwapT,
            IsDark,
            DeviceConnected,
            ActiveModuleId,
            batch,
            sectors,
            orbHolder,
            orbImage,
            hoverSnapshot,
            _hoverKind,
            _orbHoverT,
            _tooltipCategories));
    }

    private sealed class JogDialDrawOp(
        Rect bounds,
        bool isOpen,
        float openT,
        float fanSwapT,
        bool isDark,
        bool deviceConnected,
        string activeModuleId,
        AeroSkiaHelpers.RefCountedSectorBatch? sectorBatch,
        IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> cachedSectors,
        AeroSkiaHelpers.RefCountedSkImage? orbHolder,
        SKImage? orbImage,
        IReadOnlyDictionary<string, float> hoverValues,
        DialHoverKind hoverKind,
        float orbHoverT,
        IReadOnlyList<string>? tooltipCategories) : ICustomDrawOperation
    {
        private static readonly SKMaskFilter TooltipShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
        private AeroSkiaHelpers.RefCountedSectorBatch? _sectorBatch = sectorBatch;
        private AeroSkiaHelpers.RefCountedSkImage? _orbHolder = orbHolder;

        public Rect Bounds { get; } = bounds;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose()
        {
            Interlocked.Exchange(ref _sectorBatch, null)?.Release();
            Interlocked.Exchange(ref _orbHolder, null)?.Release();
        }

        public bool HitTest(Point p)
        {
            float cy = (float)Bounds.Height * 0.5f;
            float px = (float)p.X;
            float py = (float)p.Y;
            if (!isOpen && openT <= 0.05f)
            {
                return AeroSkiaHelpers.Hypot(px, py - cy) <= DialRadius + 4f;
            }

            float cx = openT * ExpandedCenterX;
            if (AeroSkiaHelpers.Hypot(px - cx, py - cy) <= DialRadius + 2f)
            {
                return true;
            }

            if (openT > 0.25f)
            {
                var batch = Volatile.Read(ref _sectorBatch);
                var sectors = batch?.TryAcquire();
                if (sectors is not null)
                {
                    try
                    {
                        foreach (var cached in sectors)
                        {
                            if (cached.Path.Contains(px, py))
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        batch!.Release();
                    }
                }
            }

            return false;
        }

        public void Render(ImmediateDrawingContext context)
        {
            var batch = Volatile.Read(ref _sectorBatch);
            var sectors = batch?.TryAcquire();
            if (batch is not null && sectors is null)
            {
                return;
            }

            var orbHolder = Volatile.Read(ref _orbHolder);
            var acquiredOrb = orbHolder?.TryAcquire();
            try
            {
                if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                {
                    return;
                }

                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

            float cy = (float)Bounds.Height * 0.5f;
            float cx = openT * ExpandedCenterX;
            float fanT = Math.Clamp(openT * fanSwapT, 0f, 1f);

            // 1. Right-side concentric optical glass fan
            if (fanT > 0.02f && cachedSectors.Count > 0)
            {
                float fanSlide = (1f - fanT) * -48f;
                float fanScale = 0.72f + 0.28f * fanT;

                canvas.Save();
                canvas.Translate(fanSlide + ExpandedCenterX, cy);
                canvas.Scale(fanScale);
                canvas.Translate(-ExpandedCenterX, -cy);

                foreach (var cached in cachedSectors)
                {
                    var sec = cached.Sector;
                    bool isActive = IsSectorActive(sec.Module, activeModuleId);
                    float hoverT = hoverValues.GetValueOrDefault(sec.Module.Id);
                    AeroSkiaHelpers.DrawCachedGlassSector(
                        canvas,
                        cached,
                        hoverT,
                        isActive,
                        isDark,
                        opacity: fanT);

                    var (tx, ty) = AeroSkiaHelpers.ComputePopOffset(sec, hoverT, isActive);

                    // 2. Pre-baked horizontal (0 deg) neon-under-frosted-glass icon (strictly hover-only)
                    if (hoverT > 0.01f && cached.NeonGlowImage is not null)
                    {
                        using var neonPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * hoverT * fanT))
                        };
                        canvas.DrawImage(cached.NeonGlowImage, cached.NeonOriginX + tx, cached.NeonOriginY + ty, neonPaint);
                    }

                    // 3. Separate 2x supersampled rotated label drawn ON TOP of both glass and neon glow
                    if (cached.LabelImage is not null)
                    {
                        using var lblPaint = new SKPaint
                        {
                            IsAntialias = true,
                            FilterQuality = SKFilterQuality.Medium,
                            Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * fanT))
                        };
                        var lblRect = new SKRect(
                            cached.LabelOriginX + tx,
                            cached.LabelOriginY + ty,
                            cached.LabelOriginX + tx + cached.LabelLogicalW,
                            cached.LabelOriginY + ty + cached.LabelLogicalH);
                        canvas.DrawImage(cached.LabelImage, lblRect, lblPaint);
                    }
                }

                canvas.Restore();
            }

            // 2. Full / Half Metallic Jog Dial Orb
            DrawDialOrb(canvas, cx, cy);

            // 3. Fixed non-overlapping category hover tooltip
            if (isOpen && openT > 0.75f && fanSwapT > 0.75f && tooltipCategories is { Count: > 0 })
            {
                DrawTooltip(canvas, ExpandedCenterX + DialRadius + 24f, 18f, tooltipCategories);
            }
            }
            finally
            {
                if (acquiredOrb is not null) orbHolder!.Release();
                if (sectors is not null) batch!.Release();
            }
        }

        private void DrawDialOrb(SKCanvas canvas, float cx, float cy)
        {
            if (orbImage is not null)
            {
                float half = DialRadius + OrbPad;
                using var imgPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
                canvas.DrawImage(orbImage, new SKRect(cx - half, cy - half, cx + half, cy + half), imgPaint);
            }

            using (var rim = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.25f + 0.8f * orbHoverT,
                Color = orbHoverT > 0.02f
                    ? AeroSkiaHelpers.Mix(
                        isDark ? new SKColor(145, 165, 190, 96) : new SKColor(172, 190, 210, 200),
                        isDark ? new SKColor(120, 210, 255, 220) : new SKColor(68, 172, 246, 235),
                        orbHoverT)
                    : (isDark ? new SKColor(145, 165, 190, 96) : new SKColor(172, 190, 210, 200))
            })
            {
                canvas.DrawCircle(cx, cy, DialRadius, rim);
            }

            // Subtle luminous hover wash on collapsed orb or hovered HOME/HIDE half
            if (orbHoverT > 0.02f)
            {
                canvas.Save();
                using var orbClip = new SKPath();
                orbClip.AddCircle(cx, cy, DialRadius - 1f);
                canvas.ClipPath(orbClip, SKClipOperation.Intersect, true);
                if (isOpen && hoverKind == DialHoverKind.HomeHalf)
                {
                    canvas.ClipRect(new SKRect(cx - DialRadius, cy - DialRadius, cx, cy + DialRadius));
                }
                else if (isOpen && hoverKind == DialHoverKind.HideHalf)
                {
                    canvas.ClipRect(new SKRect(cx, cy - DialRadius, cx + DialRadius, cy + DialRadius));
                }

                using var hoverWash = new SKPaint
                {
                    IsAntialias = true,
                    Color = isDark
                        ? new SKColor(110, 196, 255, (byte)Math.Round(36 * orbHoverT))
                        : new SKColor(120, 204, 255, (byte)Math.Round(48 * orbHoverT))
                };
                canvas.DrawCircle(cx, cy, DialRadius, hoverWash);
                canvas.Restore();
            }

            float collapsedAlpha = Math.Clamp(1f - openT * 2.2f, 0f, 1f);
            if (collapsedAlpha > 0.01f)
            {
                byte CA(byte v) => (byte)Math.Round(v * collapsedAlpha);

                using (var statusArc = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.6f,
                    StrokeCap = SKStrokeCap.Round,
                    Color = (deviceConnected ? new SKColor(126, 224, 62) : new SKColor(96, 190, 250)).WithAlpha(CA(255))
                })
                {
                    var arcRect = new SKRect(cx - DialRadius + 2.5f, cy - DialRadius + 2.5f, cx + DialRadius - 2.5f, cy + DialRadius - 2.5f);
                    canvas.DrawArc(arcRect, -24f, 48f, false, statusArc);
                }

                using var chev = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.5f,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    Color = isDark ? new SKColor(226, 232, 240, CA(255)) : new SKColor(30, 41, 59, CA(255))
                };
                canvas.DrawLine(cx + 27f, cy - 8f, cx + 35f, cy, chev);
                canvas.DrawLine(cx + 35f, cy, cx + 27f, cy + 8f, chev);
            }

            float expandedAlpha = Math.Clamp((openT - 0.3f) / 0.7f, 0f, 1f);
            if (expandedAlpha > 0.01f)
            {
                byte EA(byte v) => (byte)Math.Round(v * expandedAlpha);

                using (var div = new SKPaint
                {
                    IsAntialias = true,
                    StrokeWidth = 1.2f,
                    Color = isDark ? new SKColor(145, 165, 190, EA(100)) : new SKColor(156, 174, 194, EA(180))
                })
                {
                    canvas.DrawLine(cx, cy - DialRadius + 10f, cx, cy + DialRadius - 10f, div);
                }

                SKColor labelCol = isDark ? new SKColor(226, 232, 240, EA(255)) : new SKColor(51, 65, 85, EA(255));
                using var iconStroke = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.8f,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    Color = labelCol
                };

                float hx = cx - 29f;
                float hy = cy - 6f;
                using (var homePath = new SKPath())
                {
                    homePath.MoveTo(hx - 7.5f, hy);
                    homePath.LineTo(hx, hy - 6.5f);
                    homePath.LineTo(hx + 7.5f, hy);
                    homePath.MoveTo(hx - 5.5f, hy - 1.5f);
                    homePath.LineTo(hx - 5.5f, hy + 7f);
                    homePath.LineTo(hx + 5.5f, hy + 7f);
                    homePath.LineTo(hx + 5.5f, hy - 1.5f);
                    canvas.DrawPath(homePath, iconStroke);
                }

                float rx = cx + 29f;
                canvas.DrawLine(rx + 3.5f, cy - 11f, rx - 3.5f, cy - 5f, iconStroke);
                canvas.DrawLine(rx - 3.5f, cy - 5f, rx + 3.5f, cy + 1f, iconStroke);

                using var txtPaint = new SKPaint
                {
                    IsAntialias = true,
                    TextSize = 10.5f,
                    TextAlign = SKTextAlign.Center,
                    Typeface = AeroSkiaHelpers.InterBold,
                    Color = labelCol
                };
                canvas.DrawText("HOME", hx, cy + 19.5f, txtPaint);
                canvas.DrawText("HIDE", rx, cy + 19.5f, txtPaint);
            }
        }

        private void DrawTooltip(SKCanvas canvas, float anchorX, float anchorY, IReadOnlyList<string> categories)
        {
            string prefix = "Contains: ";
            string body = string.Join(" · ", categories);

            using var pPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 12.5f,
                Typeface = AeroSkiaHelpers.InterSemiBold,
                Color = isDark ? new SKColor(125, 211, 252) : new SKColor(3, 105, 161)
            };
            using var bPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 12.5f,
                Typeface = AeroSkiaHelpers.InterRegular,
                Color = isDark ? new SKColor(241, 245, 249) : new SKColor(30, 41, 59)
            };

            float pw = pPaint.MeasureText(prefix);
            float bw = bPaint.MeasureText(body);
            float boxW = pw + bw + 26f;
            float boxH = 32f;
            float x = Math.Clamp(anchorX, ExpandedCenterX + DialRadius + 18f, ExpandedWidth - boxW - 12f);
            float y = Math.Clamp(anchorY, 12f, ExpandedHeight - boxH - 12f);

            var rect = new SKRoundRect(new SKRect(x, y, x + boxW, y + boxH), 11f, 11f);
            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(0, 0, 0, 120) : new SKColor(38, 66, 94, 42),
                MaskFilter = TooltipShadowBlur
            })
            {
                canvas.DrawRoundRect(rect, shadow);
            }

            using (var bg = new SKPaint { IsAntialias = true, Color = isDark ? new SKColor(22, 30, 42, 242) : new SKColor(255, 255, 255, 244) })
            {
                canvas.DrawRoundRect(rect, bg);
            }

            using (var bd = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = isDark ? new SKColor(120, 170, 220, 100) : new SKColor(190, 216, 238, 220) })
            {
                canvas.DrawRoundRect(rect, bd);
            }

            canvas.DrawText(prefix, x + 13f, y + 20.5f, pPaint);
            canvas.DrawText(body, x + 13f + pw, y + 20.5f, bPaint);
        }
    }
}
