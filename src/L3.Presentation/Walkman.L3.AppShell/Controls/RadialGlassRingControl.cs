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

public sealed class RadialGlassRingControl : Control
{
    public static readonly StyledProperty<bool> ExpandedProperty =
        AvaloniaProperty.Register<RadialGlassRingControl, bool>(nameof(Expanded));

    public static readonly StyledProperty<bool> DeviceConnectedProperty =
        AvaloniaProperty.Register<RadialGlassRingControl, bool>(nameof(DeviceConnected));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<RadialGlassRingControl, bool>(nameof(IsDark));

    public bool Expanded
    {
        get => GetValue(ExpandedProperty);
        set => SetValue(ExpandedProperty, value);
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

    private const double RingSize = 700.0;
    private const double Center = RingSize / 2.0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, float> _hoverT = new(StringComparer.Ordinal);
    private double _lastTimeSec;
    private double _lastRenderSec;
    private const double MinFrameIntervalSec = 1.0 / 60.0;
    private bool _isAttached;
    private bool _frameRequested;

    private string? _submenuParentId;
    private double? _anchorAngleDeg;
    private int? _lockedBackIndex;
    private bool _pendingSwap;
    private string? _pendingParentId;
    private double? _pendingAnchorDeg;
    private int? _pendingLockedBackIndex;
    private double _expandT;
    private double _liveSwapT = 1.0;

    private string? _hoveredId;
    private Point _cursorPos;
    private ComputedRingSector? _tooltipSector;
    private IReadOnlyList<string>? _tooltipCategories;

    private static readonly IReadOnlyDictionary<string, float> EmptyHover = new Dictionary<string, float>(0, StringComparer.Ordinal);
    private IReadOnlyList<ComputedRingSector> _sectors = Array.Empty<ComputedRingSector>();
    private AeroSkiaHelpers.RefCountedSectorBatch? _cachedBatch;
    private AeroSkiaHelpers.RefCountedSectorBatch? _prevCachedBatch;
    private IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> _cachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
    private IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
    private readonly Dictionary<(bool IsDark, bool DeviceConnected, string SubmenuKey), (IReadOnlyList<ComputedRingSector> Sectors, IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> Items, AeroSkiaHelpers.RefCountedSectorBatch Batch)> _batchCache = new();
    private Window? _hostWindow;

    public RadialGlassRingControl()
    {
        Width = RingSize;
        Height = RingSize;
        RebuildSectors(animateLiveTransition: false);
    }

    static RadialGlassRingControl()
    {
        ExpandedProperty.Changed.AddClassHandler<RadialGlassRingControl>((c, _) => c.OnExpandedChanged());
        DeviceConnectedProperty.Changed.AddClassHandler<RadialGlassRingControl>((c, _) => c.OnDeviceConnectedChanged());
        IsDarkProperty.Changed.AddClassHandler<RadialGlassRingControl>((c, _) => c.RebuildSectors(animateLiveTransition: false));
        IsVisibleProperty.Changed.AddClassHandler<RadialGlassRingControl>((c, _) => c.StartAnimationIfNeeded());
    }

    public string? ActiveSubmenuParentId => _submenuParentId;

    public void SnapExpandedForCapture(bool expanded, string? submenuParentId = null)
    {
        Expanded = expanded;
        _pendingSwap = false;
        _submenuParentId = submenuParentId;
        if (!string.IsNullOrEmpty(submenuParentId))
        {
            var root = ModuleRegistry.ResolveActiveRingModules(ModuleRegistry.DefaultModules, RingSurface.StartRing, DeviceConnected, null);
            var rootSectors = ModuleRegistry.ComputeRingSectors(root.Items, Center, Center, 136, 7, 22);
            int idx = rootSectors.ToList().FindIndex(s => s.Module.Id == submenuParentId);
            _lockedBackIndex = idx >= 0 ? idx : null;
            _anchorAngleDeg = idx >= 0 ? rootSectors[idx].MidAngleDeg : 90.0;
        }
        else
        {
            _lockedBackIndex = null;
            _anchorAngleDeg = null;
        }

        _expandT = expanded ? 1.0 : 0.0;
        _liveSwapT = 1.0;
        _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        IsHitTestVisible = expanded;
        _hoverT.Clear();
        RebuildSectors(animateLiveTransition: false);
        InvalidateVisual();
    }

    public void SetSimulatedHoverForCapture(string? moduleId)
    {
        _hoveredId = moduleId;
        _hoverT.Clear();
        var sector = _sectors.FirstOrDefault(s => s.Module.Id == moduleId);
        if (moduleId is not null)
        {
            _hoverT[moduleId] = 1f;
        }

        if (sector?.Module.Children is { Count: > 0 } children)
        {
            _cursorPos = new Point(sector.LabelX, sector.LabelY - 6);
            _tooltipSector = sector;
            _tooltipCategories = children.Select(c => c.Title).ToArray();
        }
        else
        {
            _tooltipSector = null;
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
        if (_cachedBatch is null)
        {
            RebuildSectors(animateLiveTransition: false);
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
        _prevCachedBatch = null;
        _cachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        foreach (var entry in _batchCache.Values)
        {
            entry.Batch.Release();
        }
        _batchCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            StartAnimationIfNeeded();
        }
    }

    private void OnExpandedChanged()
    {
        if (!Expanded)
        {
            _submenuParentId = null;
            _anchorAngleDeg = null;
            _lockedBackIndex = null;
            _pendingSwap = false;
            _hoveredId = null;
            _tooltipSector = null;
            _tooltipCategories = null;
            _liveSwapT = 1.0;
            _prevCachedBatch = null;
            _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
            RebuildSectors(animateLiveTransition: false);
        }

        IsHitTestVisible = Expanded;
        StartAnimationIfNeeded();
    }

    private void OnDeviceConnectedChanged()
    {
        bool smooth = Expanded && string.IsNullOrEmpty(_submenuParentId) && !_pendingSwap;
        RebuildSectors(animateLiveTransition: smooth);
        if (smooth)
        {
            StartAnimationIfNeeded();
        }
    }

    private void RebuildSectors(bool animateLiveTransition)
    {
        if (!_isAttached)
        {
            return;
        }
        if (animateLiveTransition && _cachedBatch is not null && _cachedSectors.Count > 0)
        {
            _prevCachedBatch = _cachedBatch;
            _prevCachedSectors = _cachedSectors;
            _liveSwapT = 0.0;
        }
        else
        {
            _prevCachedBatch = null;
            _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
            _liveSwapT = 1.0;
        }

        string submenuKey = $"{_submenuParentId ?? string.Empty}|{_lockedBackIndex?.ToString() ?? ""}";
        var cacheKey = (IsDark, DeviceConnected, submenuKey);
        if (_batchCache.TryGetValue(cacheKey, out var hit))
        {
            _sectors = hit.Sectors;
            _cachedSectors = hit.Items;
            _cachedBatch = hit.Batch;
            InvalidateVisual();
            return;
        }

        var (items, backIdx) = ModuleRegistry.ResolveActiveRingModules(
            ModuleRegistry.DefaultModules,
            RingSurface.StartRing,
            DeviceConnected,
            _submenuParentId,
            _lockedBackIndex);

        _sectors = ModuleRegistry.ComputeRingSectors(
            items,
            Center,
            Center,
            innerRadius: 136,
            gapDeg: 7,
            cornerRadius: 22,
            anchorIndex: _submenuParentId is not null && backIdx >= 0 ? backIdx : null,
            anchorAngleDeg: _anchorAngleDeg);

        _cachedSectors = AeroSkiaHelpers.BakeSectorCache(_sectors, Center, Center, IsDark, bakeHorizontalLabel: true);
        _cachedBatch = new AeroSkiaHelpers.RefCountedSectorBatch(_cachedSectors);
        _batchCache[cacheKey] = (_sectors, _cachedSectors, _cachedBatch);
        InvalidateVisual();
    }

    private bool HoverNeedsAnimation()
    {
        foreach (var sec in _sectors)
        {
            float target = sec.Module.Id == _hoveredId ? 1f : 0f;
            float curr = _hoverT.GetValueOrDefault(sec.Module.Id);
            if (MathF.Abs(curr - target) > 0.005f)
            {
                return true;
            }
        }

        foreach (var kv in _hoverT)
        {
            if (kv.Key != _hoveredId && kv.Value > 0.005f)
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

        double target = Expanded && !_pendingSwap ? 1.0 : 0.0;
        return _pendingSwap
               || Math.Abs(_expandT - target) > 0.004
               || _liveSwapT < 0.996
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

        double target = Expanded && !_pendingSwap ? 1.0 : 0.0;
        double speed = _pendingSwap ? 20.0 : 14.0;
        _expandT = AeroSkiaHelpers.Damp(_expandT, target, speed, dt);

        if (_pendingSwap && _expandT <= 0.06)
        {
            _expandT = 0.06;
            _submenuParentId = _pendingParentId;
            _anchorAngleDeg = _pendingAnchorDeg;
            _lockedBackIndex = _pendingLockedBackIndex;
            _pendingSwap = false;
            _hoverT.Clear();
            RebuildSectors(animateLiveTransition: false);
        }
        else if (!_pendingSwap && Math.Abs(_expandT - target) < 0.005)
        {
            _expandT = target;
        }

        if (_liveSwapT < 1.0)
        {
            _liveSwapT = AeroSkiaHelpers.Damp(_liveSwapT, 1.0, 11.0, dt);
            if (_liveSwapT >= 0.995)
            {
                _liveSwapT = 1.0;
                _prevCachedBatch = null;
                _prevCachedSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
            }
        }

        foreach (var sec in _sectors)
        {
            string id = sec.Module.Id;
            float htTarget = id == _hoveredId ? 1f : 0f;
            float htCurr = _hoverT.GetValueOrDefault(id);
            float htNext = AeroSkiaHelpers.Damp(htCurr, htTarget, 18f, dt);
            if (MathF.Abs(htNext - htTarget) < 0.005f)
            {
                htNext = htTarget;
            }

            _hoverT[id] = htNext;
        }

        InvalidateVisual();
        ScheduleNextFrame();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!Expanded || _pendingSwap)
        {
            return;
        }

        var pt = e.GetPosition(this);
        var hit = HitTestSector(pt);
        string? nextId = hit?.Module.Id;
        Cursor = hit is not null ? AeroSkiaHelpers.HandCursor : Cursor.Default;

        bool tooltipMoved = false;
        if (hit?.Module.Children is { Count: > 0 } children)
        {
            if (Math.Abs(pt.X - _cursorPos.X) > 8.0 || Math.Abs(pt.Y - _cursorPos.Y) > 8.0 || _tooltipSector?.Module.Id != hit.Module.Id)
            {
                _cursorPos = pt;
                _tooltipSector = hit;
                _tooltipCategories = children.Select(c => c.Title).ToArray();
                tooltipMoved = true;
            }
        }
        else if (_tooltipCategories is not null)
        {
            _tooltipSector = null;
            _tooltipCategories = null;
            tooltipMoved = true;
        }

        if (nextId != _hoveredId)
        {
            _hoveredId = nextId;
            StartAnimationIfNeeded();
            InvalidateVisual();
        }
        else if (tooltipMoved)
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        bool hadState = _hoveredId is not null || _tooltipCategories is not null;
        _hoveredId = null;
        _tooltipSector = null;
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
        if (!Expanded || _pendingSwap || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hit = HitTestSector(e.GetPosition(this));
        if (hit is null)
        {
            return;
        }

        e.Handled = true;
        if (hit.Module.Id == ModuleIds.Back)
        {
            TriggerSubmenuSwap(null, null, null);
        }
        else if (hit.Module.Children is { Count: > 0 })
        {
            int idx = _sectors.ToList().FindIndex(s => s.Module.Id == hit.Module.Id);
            TriggerSubmenuSwap(hit.Module.Id, hit.MidAngleDeg, idx >= 0 ? idx : null);
        }
        else
        {
            ModuleActivated?.Invoke(hit.Module.Id);
        }
    }

    private void TriggerSubmenuSwap(string? nextParentId, double? nextAnchorDeg, int? nextLockedBackIdx)
    {
        _hoveredId = null;
        _tooltipSector = null;
        _tooltipCategories = null;
        _pendingSwap = true;
        _pendingParentId = nextParentId;
        _pendingAnchorDeg = nextAnchorDeg;
        _pendingLockedBackIndex = nextLockedBackIdx;
        StartAnimationIfNeeded();
    }

    private ComputedRingSector? HitTestSector(Point pt)
    {
        float x = (float)pt.X;
        float y = (float)pt.Y;
        foreach (var cached in _cachedSectors)
        {
            if (cached.Path.Contains(x, y))
            {
                return cached.Sector;
            }
        }

        return null;
    }

    public override void Render(DrawingContext context)
    {
        if (_expandT <= 0.01)
        {
            return;
        }

        var batch = _cachedBatch;
        var sectors = batch?.TryAcquire();
        if (sectors is null)
        {
            batch = null;
            sectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        }

        var prevBatch = _liveSwapT < 0.99 ? _prevCachedBatch : null;
        var prevSectors = prevBatch?.TryAcquire();
        if (prevSectors is null)
        {
            prevBatch = null;
            prevSectors = Array.Empty<AeroSkiaHelpers.CachedSectorItem>();
        }

        IReadOnlyDictionary<string, float> hoverSnapshot = _hoverT.Count == 0
            ? EmptyHover
            : new Dictionary<string, float>(_hoverT, StringComparer.Ordinal);
        context.Custom(new RingDrawOp(
            new Rect(Bounds.Size),
            batch,
            sectors,
            prevBatch,
            prevSectors,
            IsDark,
            (float)_expandT,
            (float)_liveSwapT,
            hoverSnapshot,
            _cursorPos,
            _tooltipSector,
            _tooltipCategories));
    }

    private sealed class RingDrawOp(
        Rect bounds,
        AeroSkiaHelpers.RefCountedSectorBatch? sectorBatch,
        IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> cachedSectors,
        AeroSkiaHelpers.RefCountedSectorBatch? prevSectorBatch,
        IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> prevCachedSectors,
        bool isDark,
        float expandT,
        float liveSwapT,
        IReadOnlyDictionary<string, float> hoverValues,
        Point cursorPos,
        ComputedRingSector? tooltipSector,
        IReadOnlyList<string>? tooltipCategories) : ICustomDrawOperation
    {
        private static readonly SKMaskFilter TooltipShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
        private AeroSkiaHelpers.RefCountedSectorBatch? _sectorBatch = sectorBatch;
        private AeroSkiaHelpers.RefCountedSectorBatch? _prevSectorBatch = prevSectorBatch;

        public Rect Bounds { get; } = bounds;
        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p)
        {
            if (expandT <= 0.25f)
            {
                return false;
            }

            float px = (float)p.X;
            float py = (float)p.Y;
            float dist = AeroSkiaHelpers.Hypot(px - (float)Center, py - (float)Center);
            if (dist < 132f || dist > 332f)
            {
                return false;
            }

            var batch = Volatile.Read(ref _sectorBatch);
            var sectors = batch?.TryAcquire();
            if (sectors is null)
            {
                return false;
            }

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

            return false;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _sectorBatch, null)?.Release();
            Interlocked.Exchange(ref _prevSectorBatch, null)?.Release();
        }

        public void Render(ImmediateDrawingContext context)
        {
            var batch = Volatile.Read(ref _sectorBatch);
            var sectors = batch?.TryAcquire();
            if (batch is not null && sectors is null)
            {
                return;
            }

            var prevBatch = Volatile.Read(ref _prevSectorBatch);
            var prevSectors = prevBatch?.TryAcquire();
            try
            {
                if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                {
                    return;
                }

                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

            float scale = 0.54f + 0.46f * expandT;

            canvas.Save();
            canvas.Translate((float)Center, (float)Center);
            canvas.Scale(scale);
            canvas.Translate(-(float)Center, -(float)Center);

            // Crossfade previous ring state out on live USB connect/disconnect
            if (liveSwapT < 0.99f && prevCachedSectors.Count > 0)
            {
                float fadeOut = expandT * (1f - liveSwapT);
                if (fadeOut > 0.02f)
                {
                    DrawSectorBatch(canvas, prevCachedSectors, hoverValues, isDark, fadeOut);
                }
            }

            float fadeIn = expandT * Math.Clamp(liveSwapT, 0f, 1f);
            if (fadeIn > 0.01f)
            {
                DrawSectorBatch(canvas, cachedSectors, hoverValues, isDark, fadeIn);
            }

            canvas.Restore();

            if (tooltipCategories is { Count: > 0 } && expandT > 0.7f)
            {
                DrawCategoryTooltip(canvas, cursorPos, tooltipSector, tooltipCategories, isDark);
            }
            }
            finally
            {
                if (prevSectors is not null) prevBatch!.Release();
                if (sectors is not null) batch!.Release();
            }
        }

        private static void DrawSectorBatch(
            SKCanvas canvas,
            IReadOnlyList<AeroSkiaHelpers.CachedSectorItem> batch,
            IReadOnlyDictionary<string, float> hoverValues,
            bool isDark,
            float batchOpacity)
        {
            foreach (var cached in batch)
            {
                var sector = cached.Sector;
                float hoverT = hoverValues.GetValueOrDefault(sector.Module.Id);
                AeroSkiaHelpers.DrawCachedGlassSector(canvas, cached, hoverT, isActive: false, isDark, opacity: batchOpacity);

                double rad = sector.MidAngleDeg * Math.PI / 180.0;
                double popDist = 9.0 * hoverT;
                float tx = (float)(Math.Cos(rad) * popDist);
                float ty = (float)(Math.Sin(rad) * popDist);

                // Pre-baked 0 deg neon-under-glass icon centered behind label (strictly hover-only, zero live blur)
                if (hoverT > 0.01f && cached.NeonGlowImage is not null)
                {
                    using var neonPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * hoverT * batchOpacity))
                    };
                    canvas.DrawImage(cached.NeonGlowImage, cached.NeonOriginX + tx, cached.NeonOriginY + ty, neonPaint);
                }

                // Pre-baked 2x supersampled label bitmap (eliminates per-frame CPU text layout)
                if (cached.LabelImage is not null)
                {
                    using var lblPaint = new SKPaint
                    {
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.Medium,
                        Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * batchOpacity))
                    };
                    var lblRect = new SKRect(
                        cached.LabelOriginX + tx,
                        cached.LabelOriginY + ty,
                        cached.LabelOriginX + tx + cached.LabelLogicalW,
                        cached.LabelOriginY + ty + cached.LabelLogicalH);
                    canvas.DrawImage(cached.LabelImage, lblRect, lblPaint);
                }
            }
        }

        private static void DrawCategoryTooltip(
            SKCanvas canvas,
            Point cursorPos,
            ComputedRingSector? tooltipSector,
            IReadOnlyList<string> categories,
            bool isDark)
        {
            string prefix = "Contains: ";
            string body = string.Join(" · ", categories);

            using var prefixPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 12.5f,
                Typeface = AeroSkiaHelpers.InterSemiBold,
                Color = isDark ? new SKColor(125, 211, 252) : new SKColor(3, 105, 161)
            };
            using var bodyPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 12.5f,
                Typeface = AeroSkiaHelpers.InterRegular,
                Color = isDark ? new SKColor(241, 245, 249) : new SKColor(30, 41, 59)
            };

            float pw = prefixPaint.MeasureText(prefix);
            float bw = bodyPaint.MeasureText(body);
            float boxW = pw + bw + 26f;
            float boxH = 32f;

            // Fixed, centered top pill (never jumps or overlaps petals/medallion)
            float x = Math.Clamp((float)Center - boxW * 0.5f, 12f, (float)RingSize - boxW - 12f);
            float y = 14f;

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

            using (var fill = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(24, 31, 42, 240) : new SKColor(255, 255, 255, 244)
            })
            {
                canvas.DrawRoundRect(rect, fill);
            }

            using (var border = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color = isDark ? new SKColor(120, 170, 220, 110) : new SKColor(190, 216, 238, 225)
            })
            {
                canvas.DrawRoundRect(rect, border);
            }

            canvas.DrawText(prefix, x + 13f, y + 20.5f, prefixPaint);
            canvas.DrawText(body, x + 13f + pw, y + 20.5f, bodyPaint);
        }
    }
}
