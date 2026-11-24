using System.Diagnostics;
using System.Globalization;
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

public sealed class MetallicMedallionControl : Control
{
    public static readonly StyledProperty<StartScreenPhase> PhaseProperty =
        AvaloniaProperty.Register<MetallicMedallionControl, StartScreenPhase>(nameof(Phase), StartScreenPhase.IdleBlobs);

    public static readonly StyledProperty<WalkmanDeviceSummary?> ConnectedDeviceProperty =
        AvaloniaProperty.Register<MetallicMedallionControl, WalkmanDeviceSummary?>(nameof(ConnectedDevice));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<MetallicMedallionControl, bool>(nameof(IsDark));

    public static readonly StyledProperty<bool> IsStageActiveProperty =
        AvaloniaProperty.Register<MetallicMedallionControl, bool>(nameof(IsStageActive), true);

    public StartScreenPhase Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public WalkmanDeviceSummary? ConnectedDevice
    {
        get => GetValue(ConnectedDeviceProperty);
        set => SetValue(ConnectedDeviceProperty, value);
    }

    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public bool IsStageActive
    {
        get => GetValue(IsStageActiveProperty);
        set => SetValue(IsStageActiveProperty, value);
    }

    public event Action? LibraryRequested;
    public event Action? PairSyncRequested;
    public event Action? ExpandRingRequested;
    public event Action? CollapseRingRequested;

    private enum HoverTarget { None, UpperDish, LowerCrescent, PlusButton, CollapseButton }

    private const float CanvasW = 260f;
    private const float CanvasH = 380f;
    private const float CenterX = 130f;
    private const float CenterY = 190f; // Aligned at exact midpoint of control
    private const float MedallionRadius = 116f;
    private const float PlusCenterY = CenterY + 154f; // 344
    private const float PlusRadius = 26f;
    private const float CollapseCenterY = CenterY + MedallionRadius; // 306
    private const float CollapseRadius = 18f;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTimeSec;
    private double _lastRenderSec;
    private double _lastDotRenderSec;
    private const double MinFrameIntervalSec = 1.0 / 60.0;
    private bool _isAttached;
    private bool _frameRequested;

    private double _revealT;
    private double _expandT;
    private HoverTarget _hover = HoverTarget.None;

    private float _upperHoverT;
    private float _lowerHoverT;
    private float _plusHoverT;
    private float _collapseHoverT;

    public MetallicMedallionControl()
    {
        Width = CanvasW;
        Height = CanvasH;
    }

    static MetallicMedallionControl()
    {
        PhaseProperty.Changed.AddClassHandler<MetallicMedallionControl>((c, _) => c.OnPhaseChanged());
        ConnectedDeviceProperty.Changed.AddClassHandler<MetallicMedallionControl>((c, _) => c.OnDeviceChanged());
        IsDarkProperty.Changed.AddClassHandler<MetallicMedallionControl>((c, _) => c.InvalidateVisual());
        IsStageActiveProperty.Changed.AddClassHandler<MetallicMedallionControl>((c, _) => c.StartAnimationIfNeeded());
    }

    public void SnapStateForPhase(StartScreenPhase phase)
    {
        Phase = phase;
        _revealT = phase == StartScreenPhase.IdleBlobs ? 0.0 : 1.0;
        _expandT = phase == StartScreenPhase.RingExpanded ? 1.0 : 0.0;
        _hover = HoverTarget.None;
        _upperHoverT = 0f;
        _lowerHoverT = 0f;
        _plusHoverT = 0f;
        _collapseHoverT = 0f;
        IsHitTestVisible = phase != StartScreenPhase.IdleBlobs;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        OnPhaseChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPhaseChanged()
    {
        IsHitTestVisible = Phase != StartScreenPhase.IdleBlobs;
        if (Phase == StartScreenPhase.IdleBlobs)
        {
            _hover = HoverTarget.None;
        }

        StartAnimationIfNeeded();
        InvalidateVisual();
    }

    private void OnDeviceChanged()
    {
        StartAnimationIfNeeded();
        InvalidateVisual();
    }

    private bool HoverNeedsAnim() =>
        MathF.Abs(_upperHoverT - (_hover == HoverTarget.UpperDish ? 1f : 0f)) > 0.005f ||
        MathF.Abs(_lowerHoverT - (_hover == HoverTarget.LowerCrescent ? 1f : 0f)) > 0.005f ||
        MathF.Abs(_plusHoverT - (_hover == HoverTarget.PlusButton ? 1f : 0f)) > 0.005f ||
        MathF.Abs(_collapseHoverT - (_hover == HoverTarget.CollapseButton ? 1f : 0f)) > 0.005f;

    private bool NeedsAnimation()
    {
        if (!IsStageActive || (TopLevel.GetTopLevel(this) as Window)?.WindowState == WindowState.Minimized)
        {
            return false;
        }

        double targetReveal = Phase == StartScreenPhase.IdleBlobs ? 0.0 : 1.0;
        double targetExpand = Phase == StartScreenPhase.RingExpanded ? 1.0 : 0.0;
        bool needDots = Phase != StartScreenPhase.IdleBlobs && ConnectedDevice is null;

        return Math.Abs(_revealT - targetReveal) > 0.003
               || Math.Abs(_expandT - targetExpand) > 0.003
               || needDots
               || HoverNeedsAnim();
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

        double targetReveal = Phase == StartScreenPhase.IdleBlobs ? 0.0 : 1.0;
        double targetExpand = Phase == StartScreenPhase.RingExpanded ? 1.0 : 0.0;

        _revealT = AeroSkiaHelpers.Damp(_revealT, targetReveal, 13.0, dt);
        _expandT = AeroSkiaHelpers.Damp(_expandT, targetExpand, 12.0, dt);
        if (Math.Abs(_revealT - targetReveal) < 0.003) _revealT = targetReveal;
        if (Math.Abs(_expandT - targetExpand) < 0.003) _expandT = targetExpand;

        _upperHoverT = StepHover(_upperHoverT, _hover == HoverTarget.UpperDish ? 1f : 0f, dt);
        _lowerHoverT = StepHover(_lowerHoverT, _hover == HoverTarget.LowerCrescent ? 1f : 0f, dt);
        _plusHoverT = StepHover(_plusHoverT, _hover == HoverTarget.PlusButton ? 1f : 0f, dt);
        _collapseHoverT = StepHover(_collapseHoverT, _hover == HoverTarget.CollapseButton ? 1f : 0f, dt);

        bool onlyDotsAnimating = Math.Abs(_revealT - targetReveal) <= 0.003
                                 && Math.Abs(_expandT - targetExpand) <= 0.003
                                 && !HoverNeedsAnim();

        if (!onlyDotsAnimating || now - _lastDotRenderSec >= 0.066)
        {
            _lastDotRenderSec = now;
            InvalidateVisual();
        }
        ScheduleNextFrame();
    }

    private static float StepHover(float current, float target, double dt)
    {
        float next = AeroSkiaHelpers.Damp(current, target, 16f, dt);
        return MathF.Abs(next - target) < 0.004f ? target : next;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Phase == StartScreenPhase.IdleBlobs)
        {
            return;
        }

        var pt = e.GetPosition(this);
        var nextHover = ResolveHitTarget(pt);
        Cursor = nextHover == HoverTarget.None ? Cursor.Default : AeroSkiaHelpers.HandCursor;
        if (nextHover != _hover)
        {
            _hover = nextHover;
            StartAnimationIfNeeded();
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != HoverTarget.None)
        {
            _hover = HoverTarget.None;
            StartAnimationIfNeeded();
            InvalidateVisual();
        }
        Cursor = Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Phase == StartScreenPhase.IdleBlobs || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hit = ResolveHitTarget(e.GetPosition(this));
        switch (hit)
        {
            case HoverTarget.CollapseButton:
                CollapseRingRequested?.Invoke();
                e.Handled = true;
                break;
            case HoverTarget.PlusButton:
                ExpandRingRequested?.Invoke();
                e.Handled = true;
                break;
            case HoverTarget.UpperDish:
                PairSyncRequested?.Invoke();
                e.Handled = true;
                break;
            case HoverTarget.LowerCrescent:
                LibraryRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private HoverTarget ResolveHitTarget(Point pt)
    {
        float x = (float)pt.X;
        float y = (float)pt.Y;

        if (Phase == StartScreenPhase.RingExpanded &&
            AeroSkiaHelpers.Hypot(x - CenterX, y - CollapseCenterY) <= CollapseRadius + 4f)
        {
            return HoverTarget.CollapseButton;
        }

        if (Phase == StartScreenPhase.MedallionReady &&
            AeroSkiaHelpers.Hypot(x - CenterX, y - PlusCenterY) <= PlusRadius + 4f)
        {
            return HoverTarget.PlusButton;
        }

        if (AeroSkiaHelpers.Hypot(x - CenterX, y - CenterY) <= MedallionRadius)
        {
            float seamY = CenterY + 37f; // ~66% height from top of 232px circle
            return y < seamY ? HoverTarget.UpperDish : HoverTarget.LowerCrescent;
        }

        return HoverTarget.None;
    }

    public override void Render(DrawingContext context)
    {
        if (_revealT <= 0.01)
        {
            return;
        }

        context.Custom(new MedallionDrawOp(
            new Rect(Bounds.Size),
            Phase,
            IsDark,
            ConnectedDevice,
            (float)_revealT,
            (float)_expandT,
            _upperHoverT,
            _lowerHoverT,
            _plusHoverT,
            _collapseHoverT,
            (float)_clock.Elapsed.TotalSeconds));
    }

    private sealed class MedallionDrawOp(
        Rect bounds,
        StartScreenPhase phase,
        bool isDark,
        WalkmanDeviceSummary? device,
        float revealT,
        float expandT,
        float upperHoverT,
        float lowerHoverT,
        float plusHoverT,
        float collapseHoverT,
        float timeSec) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p)
        {
            if (phase == StartScreenPhase.IdleBlobs)
            {
                return false;
            }

            float x = (float)p.X;
            float y = (float)p.Y;
            if (AeroSkiaHelpers.Hypot(x - CenterX, y - CenterY) <= MedallionRadius + 2f)
            {
                return true;
            }

            if (phase == StartScreenPhase.MedallionReady &&
                AeroSkiaHelpers.Hypot(x - CenterX, y - PlusCenterY) <= PlusRadius + 4f)
            {
                return true;
            }

            if (phase == StartScreenPhase.RingExpanded &&
                AeroSkiaHelpers.Hypot(x - CenterX, y - CollapseCenterY) <= CollapseRadius + 4f)
            {
                return true;
            }

            return false;
        }

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            {
                return;
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            float scale = 0.86f + 0.14f * revealT;
            canvas.Save();
            canvas.Translate(CenterX, CenterY);
            canvas.Scale(scale);
            canvas.Translate(-CenterX, -CenterY);

            DrawPlusButton(canvas);
            DrawMainMedallion(canvas);
            if (expandT > 0.05f)
            {
                DrawCollapseChevron(canvas);
            }

            canvas.Restore();
        }

        private static readonly SKMaskFilter MainShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18f);
        private static readonly SKMaskFilter InnerShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
        private static readonly SKMaskFilter PlusDropBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f);

        private void DrawMainMedallion(SKCanvas canvas)
        {
            byte A(byte val) => (byte)Math.Round(val * revealT);

            // Outer ambient elevation shadow
            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(0, 0, 0, A(145)) : new SKColor(42, 66, 94, A(58)),
                MaskFilter = MainShadowBlur
            })
            {
                canvas.DrawCircle(CenterX, CenterY + 10f, MedallionRadius, shadow);
            }

            // Lathe-turned outer brushed-metal bezel (conic sweep gradient)
            SKColor[] sweepColors = isDark
                ? [
                    new SKColor(62, 70, 86, A(255)),
                    new SKColor(38, 44, 55, A(255)),
                    new SKColor(82, 92, 112, A(255)),
                    new SKColor(34, 39, 49, A(255)),
                    new SKColor(66, 75, 92, A(255)),
                    new SKColor(42, 48, 60, A(255)),
                    new SKColor(62, 70, 86, A(255))
                  ]
                : [
                    new SKColor(236, 242, 248, A(255)),
                    new SKColor(206, 217, 229, A(255)),
                    new SKColor(250, 253, 255, A(255)),
                    new SKColor(196, 208, 222, A(255)),
                    new SKColor(242, 247, 252, A(255)),
                    new SKColor(210, 220, 232, A(255)),
                    new SKColor(236, 242, 248, A(255))
                  ];

            using (var bezelShader = SKShader.CreateSweepGradient(
                new SKPoint(CenterX, CenterY),
                sweepColors,
                [0f, 0.14f, 0.33f, 0.54f, 0.76f, 0.91f, 1f]))
            using (var bezelPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = bezelShader })
            {
                canvas.DrawCircle(CenterX, CenterY, MedallionRadius, bezelPaint);
            }

            // Concentric lathe micro-grooves on outer bezel
            using (var groovePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.8f,
                Color = isDark ? new SKColor(255, 255, 255, A(20)) : new SKColor(255, 255, 255, A(145))
            })
            {
                canvas.DrawCircle(CenterX, CenterY, MedallionRadius - 2.2f, groovePaint);
            }

            // Bezel outer rim stroke
            using (var bezelRim = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.25f,
                Color = isDark ? new SKColor(145, 165, 190, A(90)) : new SKColor(172, 190, 210, A(190))
            })
            {
                canvas.DrawCircle(CenterX, CenterY, MedallionRadius, bezelRim);
            }

            // Inner Core (radius = 111px) split at seamY = CenterY + 37f
            float innerR = MedallionRadius - 5f;
            float seamY = CenterY + 37f;

            using var innerCircle = new SKPath();
            innerCircle.AddCircle(CenterX, CenterY, innerR);

            canvas.Save();
            canvas.ClipPath(innerCircle, SKClipOperation.Intersect, true);

            // 1. UPPER DISH (~66%, Recessed/Concave metal background)
            canvas.Save();
            canvas.ClipRect(new SKRect(CenterX - innerR, CenterY - innerR, CenterX + innerR, seamY));

            SKColor[] upperGrad = isDark
                ? [new SKColor(33, 38, 48, A(255)), new SKColor(46, 54, 67, A(255)), new SKColor(68, 78, 95, A(255))]
                : [new SKColor(194, 206, 220, A(255)), new SKColor(228, 235, 243, A(255)), new SKColor(252, 254, 255, A(255))];

            using (var dishShader = SKShader.CreateLinearGradient(
                new SKPoint(CenterX, CenterY - innerR),
                new SKPoint(CenterX, seamY),
                upperGrad,
                [0f, 0.52f, 1f],
                SKShaderTileMode.Clamp))
            using (var dishPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = dishShader })
            {
                canvas.DrawRect(CenterX - innerR, CenterY - innerR, innerR * 2, innerR + 37f, dishPaint);
            }

            // Recessed dish top shadow lip
            using (var innerShadow = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 14f,
                Color = isDark ? new SKColor(0, 0, 0, A(140)) : new SKColor(52, 72, 96, A(74)),
                MaskFilter = InnerShadowBlur
            })
            {
                canvas.DrawCircle(CenterX, CenterY + 6f, innerR + 4f, innerShadow);
            }

            if (upperHoverT > 0.01f)
            {
                using var hoverGlow = SKShader.CreateRadialGradient(
                    new SKPoint(CenterX, CenterY - 20f),
                    innerR * 0.85f,
                    [
                        new SKColor(122, 203, 255, (byte)Math.Round(56 * upperHoverT * revealT)),
                        new SKColor(148, 229, 82, (byte)Math.Round(22 * upperHoverT * revealT)),
                        SKColors.Transparent
                    ],
                    [0f, 0.55f, 1f],
                    SKShaderTileMode.Clamp);
                using var hoverPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = hoverGlow };
                canvas.DrawCircle(CenterX, CenterY, innerR, hoverPaint);
            }

            canvas.Restore();

            // 2. LOWER CRESCENT (~34%, Raised/Convex metal background)
            canvas.Save();
            canvas.ClipRect(new SKRect(CenterX - innerR, seamY, CenterX + innerR, CenterY + innerR));

            SKColor[] lowerGrad = isDark
                ? [new SKColor(72, 82, 100, A(255)), new SKColor(48, 56, 70, A(255)), new SKColor(34, 40, 50, A(255))]
                : [new SKColor(254, 255, 255, A(255)), new SKColor(226, 234, 242, A(255)), new SKColor(196, 208, 222, A(255))];

            using (var lowerShader = SKShader.CreateLinearGradient(
                new SKPoint(CenterX, seamY),
                new SKPoint(CenterX, CenterY + innerR),
                lowerGrad,
                [0f, 0.52f, 1f],
                SKShaderTileMode.Clamp))
            using (var lowerPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = lowerShader })
            {
                canvas.DrawRect(CenterX - innerR, seamY, innerR * 2, innerR - 37f, lowerPaint);
            }

            if (lowerHoverT > 0.01f)
            {
                using var cresHover = SKShader.CreateLinearGradient(
                    new SKPoint(CenterX - 60f, seamY),
                    new SKPoint(CenterX + 60f, CenterY + innerR),
                    [
                        new SKColor(122, 203, 255, (byte)Math.Round(56 * lowerHoverT * revealT)),
                        new SKColor(148, 229, 82, (byte)Math.Round(68 * lowerHoverT * revealT))
                    ],
                    (float[]?)null,
                    SKShaderTileMode.Clamp);
                using var cresPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = cresHover };
                canvas.DrawRect(CenterX - innerR, seamY, innerR * 2, innerR - 37f, cresPaint);
            }

            // Convex top specular rim just beneath seam
            using (var topRim = new SKPaint { Color = isDark ? new SKColor(220, 238, 255, A(56)) : new SKColor(255, 255, 255, A(245)), StrokeWidth = 2f })
            {
                canvas.DrawLine(CenterX - innerR, seamY + 1.5f, CenterX + innerR, seamY + 1.5f, topRim);
            }

            canvas.Restore();

            // 3. Fixed calm satin studio specular highlight OVER metal surfaces, UNDER text & dots (matching mockup)
            float sx = CenterX - innerR * 0.44f;
            float sy = CenterY - innerR * 0.50f;
            using (var cursorSheen = SKShader.CreateRadialGradient(
                new SKPoint(sx, sy),
                innerR * 1.48f,
                [
                    new SKColor(255, 255, 255, (byte)((isDark ? 34 : 112) * revealT)),
                    new SKColor(196, 224, 248, (byte)((isDark ? 12 : 30) * revealT)),
                    new SKColor(140, 195, 232, (byte)((isDark ? 4 : 10) * revealT)),
                    SKColors.Transparent
                ],
                [0f, 0.30f, 0.56f, 0.82f],
                SKShaderTileMode.Clamp))
            using (var sheenPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = cursorSheen })
            {
                canvas.DrawCircle(CenterX, CenterY, innerR, sheenPaint);
            }

            // 4. Physical machined horizontal seam channel
            using (var seamShader = SKShader.CreateLinearGradient(
                new SKPoint(CenterX - innerR, seamY),
                new SKPoint(CenterX + innerR, seamY),
                [new SKColor(70, 88, 110, A(70)), new SKColor(38, 52, 70, A(155)), new SKColor(70, 88, 110, A(70))],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp))
            using (var seamPaint = new SKPaint { Shader = seamShader, StrokeWidth = 2.4f })
            {
                canvas.DrawLine(CenterX - innerR, seamY, CenterX + innerR, seamY, seamPaint);
            }

            // 5. Crisp Foreground Content (never obscured by specular sheen)
            canvas.Save();
            canvas.ClipRect(new SKRect(CenterX - innerR, CenterY - innerR, CenterX + innerR, seamY));
            DrawUpperDishContent(canvas, revealT);
            canvas.Restore();

            canvas.Save();
            canvas.ClipRect(new SKRect(CenterX - innerR, seamY, CenterX + innerR, CenterY + innerR));
            DrawLowerCrescentContent(canvas, seamY, revealT);
            canvas.Restore();

            canvas.Restore();
        }

        private void DrawUpperDishContent(SKCanvas canvas, float alpha)
        {
            byte A(byte val) => (byte)Math.Round(val * alpha);

            if (device is not null)
            {
                using var badgePaint = new SKPaint
                {
                    IsAntialias = true,
                    TextSize = 10.5f,
                    TextAlign = SKTextAlign.Center,
                    Typeface = AeroSkiaHelpers.InterBold,
                    Color = isDark ? new SKColor(125, 211, 252, A(255)) : new SKColor(3, 105, 161, A(255))
                };
                canvas.DrawText("PLAYER READY", CenterX, CenterY - 34f, badgePaint);

                using var modelPaint = new SKPaint
                {
                    IsAntialias = true,
                    TextSize = 21f,
                    TextAlign = SKTextAlign.Center,
                    Typeface = AeroSkiaHelpers.InterBold,
                    Color = isDark ? new SKColor(248, 250, 252, A(255)) : new SKColor(15, 23, 42, A(255))
                };
                canvas.DrawText(device.Model, CenterX, CenterY - 8f, modelPaint);

                using var statPaint = new SKPaint
                {
                    IsAntialias = true,
                    TextSize = 11f,
                    TextAlign = SKTextAlign.Center,
                    Typeface = AeroSkiaHelpers.MonoRegular,
                    Color = isDark ? new SKColor(203, 213, 225, A(255)) : new SKColor(71, 85, 105, A(255))
                };
                canvas.DrawText(
                    string.Create(CultureInfo.InvariantCulture, $"{device.BatteryPct}% · {device.StorageFreeGb:F1} GB free"),
                    CenterX,
                    CenterY + 11f,
                    statPaint);

                // Recessed capacity bar
                float barW = 96f;
                float barH = 4.5f;
                float bx = CenterX - barW * 0.5f;
                float by = CenterY + 21f;
                using var trackPaint = new SKPaint { IsAntialias = true, Color = isDark ? new SKColor(0, 0, 0, A(110)) : new SKColor(15, 23, 42, A(34)) };
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(bx, by, bx + barW, by + barH), 2.5f, 2.5f), trackPaint);

                float fillW = Math.Max(6f, barW * (device.StorageUsedPercent / 100f));
                using var fillShader = SKShader.CreateLinearGradient(
                    new SKPoint(bx, by),
                    new SKPoint(bx + fillW, by),
                    [new SKColor(92, 194, 255, A(255)), new SKColor(141, 226, 75, A(255))],
                    (float[]?)null,
                    SKShaderTileMode.Clamp);
                using var fillPaint = new SKPaint { IsAntialias = true, Shader = fillShader };
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(bx, by, bx + fillW, by + barH), 2.5f, 2.5f), fillPaint);
            }
            else
            {
                using var textPaint = new SKPaint
                {
                    IsAntialias = true,
                    TextSize = 14.5f,
                    TextAlign = SKTextAlign.Center,
                    Typeface = AeroSkiaHelpers.InterBold,
                    Color = isDark ? new SKColor(241, 245, 249, A(255)) : new SKColor(30, 41, 59, A(255))
                };
                canvas.DrawText("Please connect your", CenterX, CenterY - 22f, textPaint);
                canvas.DrawText("player", CenterX, CenterY - 3f, textPaint);

                // Three rhythmically bouncing aero waiting dots
                SKColor[] dotColors =
                [
                    isDark ? new SKColor(125, 211, 252, A(255)) : new SKColor(14, 165, 233, A(255)),
                    isDark ? new SKColor(94, 234, 212, A(255)) : new SKColor(45, 212, 191, A(255)),
                    isDark ? new SKColor(190, 242, 100, A(255)) : new SKColor(132, 204, 22, A(255))
                ];

                for (int i = 0; i < 3; i++)
                {
                    double phase = (timeSec * 1.45 - i * 0.14) % 1.0;
                    if (phase < 0) phase += 1.0;
                    float wave = phase < 0.6 ? MathF.Sin((float)(phase / 0.6 * Math.PI)) : 0f;
                    float dy = -4.2f * wave;
                    byte dotAlpha = (byte)Math.Round((0.46f + 0.54f * wave) * A(255));
                    using var dotPaint = new SKPaint { IsAntialias = true, Color = dotColors[i].WithAlpha(dotAlpha) };
                    canvas.DrawCircle(CenterX + (i - 1) * 14f, CenterY + 18f + dy, 3.2f, dotPaint);
                }
            }
        }

        private void DrawLowerCrescentContent(SKCanvas canvas, float seamY, float alpha)
        {
            byte A(byte val) => (byte)Math.Round(val * alpha);
            float labelY = seamY + 34f;

            SKColor baseText = isDark ? new SKColor(241, 245, 249, A(255)) : new SKColor(30, 41, 59, A(255));
            SKColor hoverText = isDark ? new SKColor(255, 255, 255, A(255)) : new SKColor(2, 6, 23, A(255));

            using var labelPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 13.5f,
                TextAlign = SKTextAlign.Center,
                Typeface = AeroSkiaHelpers.InterBold,
                Color = AeroSkiaHelpers.Mix(baseText, hoverText, lowerHoverT)
            };
            canvas.DrawText("My Library", CenterX, labelY, labelPaint);

            float ridgeW = 32f + 16f * lowerHoverT;
            float rx = CenterX - ridgeW * 0.5f;
            float ry = labelY + 7f;
            using var ridgeShader = SKShader.CreateLinearGradient(
                new SKPoint(rx, ry),
                new SKPoint(rx + ridgeW, ry),
                [new SKColor(56, 189, 248, A(190)), new SKColor(163, 230, 53, A(210))],
                (float[]?)null,
                SKShaderTileMode.Clamp);
            using var ridgePaint = new SKPaint { IsAntialias = true, Shader = ridgeShader };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(rx, ry, rx + ridgeW, ry + 2.5f), 1.5f, 1.5f), ridgePaint);
        }

        private void DrawPlusButton(SKCanvas canvas)
        {
            float vis = Math.Clamp((1f - expandT * 1.4f) * revealT, 0f, 1f);
            if (vis <= 0.01f)
            {
                return;
            }

            float slideY = PlusCenterY + expandT * 95f;
            float r = PlusRadius * (1f + 0.08f * plusHoverT);
            byte A(byte val) => (byte)Math.Round(val * vis);

            using (var drop = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(0, 0, 0, A(120)) : new SKColor(42, 66, 94, A(52)),
                MaskFilter = PlusDropBlur
            })
            {
                canvas.DrawCircle(CenterX, slideY + 4f, r, drop);
            }

            // Closed 5-stop sweep gradient so 0 deg -> 360 deg wraps without a radial seam
            SKColor[] sweepColors = isDark
                ? [
                    new SKColor(66, 74, 90, A(255)),
                    new SKColor(42, 48, 58, A(255)),
                    new SKColor(78, 88, 106, A(255)),
                    new SKColor(38, 44, 54, A(255)),
                    new SKColor(66, 74, 90, A(255))
                  ]
                : [
                    new SKColor(238, 244, 250, A(255)),
                    new SKColor(210, 222, 234, A(255)),
                    new SKColor(252, 254, 255, A(255)),
                    new SKColor(206, 218, 230, A(255)),
                    new SKColor(238, 244, 250, A(255))
                  ];

            using (var sweep = SKShader.CreateSweepGradient(new SKPoint(CenterX, slideY), sweepColors, [0f, 0.25f, 0.5f, 0.75f, 1f]))
            using (var fill = new SKPaint { IsAntialias = true, IsDither = true, Shader = sweep })
            {
                canvas.DrawCircle(CenterX, slideY, r, fill);
            }

            if (plusHoverT > 0.01f)
            {
                byte ht = (byte)Math.Round(92 * plusHoverT * vis);
                using var hoverTint = SKShader.CreateLinearGradient(
                    new SKPoint(CenterX - r, slideY - r),
                    new SKPoint(CenterX + r, slideY + r),
                    [new SKColor(122, 203, 255, ht), new SKColor(148, 229, 82, ht)],
                    (float[]?)null,
                    SKShaderTileMode.Clamp);
                using var tintPaint = new SKPaint { IsAntialias = true, Shader = hoverTint };
                canvas.DrawCircle(CenterX, slideY, r - 1.5f, tintPaint);
            }

            using var rim = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.15f,
                Color = isDark ? new SKColor(150, 170, 195, A(110)) : new SKColor(175, 192, 212, A(210))
            };
            canvas.DrawCircle(CenterX, slideY, r, rim);

            using var glyph = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.2f,
                StrokeCap = SKStrokeCap.Round,
                Color = isDark ? new SKColor(241, 245, 249, A(255)) : new SKColor(51, 65, 85, A(255))
            };
            canvas.Save();
            canvas.Translate(CenterX, slideY);
            canvas.RotateDegrees(plusHoverT * 90f);
            canvas.DrawLine(0f, -6f, 0f, 6f, glyph);
            canvas.DrawLine(-6f, 0f, 6f, 0f, glyph);
            canvas.Restore();
        }

        private void DrawCollapseChevron(SKCanvas canvas)
        {
            float vis = Math.Clamp(expandT * revealT, 0f, 1f);
            float r = CollapseRadius * (1f + 0.1f * collapseHoverT);
            byte A(byte val) => (byte)Math.Round(val * vis);

            using var glassGrad = SKShader.CreateLinearGradient(
                new SKPoint(CenterX - r, CollapseCenterY - r),
                new SKPoint(CenterX + r, CollapseCenterY + r),
                isDark
                    ? [new SKColor(72, 86, 108, A(220)), new SKColor(40, 48, 62, A(220))]
                    : [new SKColor(255, 255, 255, A(230)), new SKColor(220, 242, 252, A(200))],
                (float[]?)null,
                SKShaderTileMode.Clamp);
            using (var fill = new SKPaint { IsAntialias = true, Shader = glassGrad })
            {
                canvas.DrawCircle(CenterX, CollapseCenterY, r, fill);
            }

            using (var rim = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.25f,
                Color = isDark ? new SKColor(190, 218, 245, A(100)) : new SKColor(190, 214, 236, A(235))
            })
            {
                canvas.DrawCircle(CenterX, CollapseCenterY, r, rim);
            }

            using var chev = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.9f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = isDark ? new SKColor(226, 232, 240, A(255)) : new SKColor(51, 65, 85, A(255))
            };
            using var path = new SKPath();
            path.MoveTo(CenterX - 5f, CollapseCenterY + 2.2f);
            path.LineTo(CenterX, CollapseCenterY - 2.8f);
            path.LineTo(CenterX + 5f, CollapseCenterY + 2.2f);
            canvas.DrawPath(path, chev);
        }
    }
}
