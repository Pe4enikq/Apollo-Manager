using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Controls;

public sealed class BlobMorphCanvasControl : Control
{
    public static readonly StyledProperty<StartScreenPhase> PhaseProperty =
        AvaloniaProperty.Register<BlobMorphCanvasControl, StartScreenPhase>(nameof(Phase), StartScreenPhase.IdleBlobs);

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<BlobMorphCanvasControl, bool>(nameof(IsDark));

    public static readonly StyledProperty<bool> IsStageActiveProperty =
        AvaloniaProperty.Register<BlobMorphCanvasControl, bool>(nameof(IsStageActive), true);

    public static readonly StyledProperty<bool> ReducedBlurProperty =
        AvaloniaProperty.Register<BlobMorphCanvasControl, bool>(nameof(ReducedBlur));

    public StartScreenPhase Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
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

    public bool ReducedBlur
    {
        get => GetValue(ReducedBlurProperty);
        set => SetValue(ReducedBlurProperty, value);
    }

    private const float MedallionRadius = 116f;
    private const float PlusRadius = 26f;
    private const float PlusOffsetY = 154f;
    private const double MinFrameIntervalSec = 1.0 / 60.0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTimeSec;
    private double _lastRenderSec;
    private double _animTimeSec;
    private double _morphT;
    private double _orbitAngle = -0.75;
    private bool _hasCaptured;
    private float _capturedOrbitX;
    private float _capturedOrbitY;
    private double _ambientAlpha;
    private bool _isAttached;
    private bool _frameRequested;
    private bool _settledInRing;
    private Window? _hostWindow;

    public BlobMorphCanvasControl()
    {
        IsHitTestVisible = false;
    }

    static BlobMorphCanvasControl()
    {
        PhaseProperty.Changed.AddClassHandler<BlobMorphCanvasControl>((ctrl, _) => ctrl.OnPhaseChanged());
        IsDarkProperty.Changed.AddClassHandler<BlobMorphCanvasControl>((ctrl, _) => ctrl.InvalidateVisual());
        IsStageActiveProperty.Changed.AddClassHandler<BlobMorphCanvasControl>((ctrl, _) => ctrl.StartAnimationIfNeeded());
        ReducedBlurProperty.Changed.AddClassHandler<BlobMorphCanvasControl>((ctrl, _) =>
        {
            ctrl.StartAnimationIfNeeded();
            ctrl.InvalidateVisual();
        });
        IsVisibleProperty.Changed.AddClassHandler<BlobMorphCanvasControl>((ctrl, _) => ctrl.StartAnimationIfNeeded());
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
        _lastTimeSec = _clock.Elapsed.TotalSeconds;
        _lastRenderSec = _lastTimeSec - MinFrameIntervalSec;
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
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            StartAnimationIfNeeded();
        }
    }

    public void SnapAnimationStateForPhase(StartScreenPhase phase)
    {
        Phase = phase;
        _morphT = phase == StartScreenPhase.IdleBlobs ? 0.0 : 1.0;
        _ambientAlpha = phase == StartScreenPhase.RingExpanded ? 1.0 : 0.0;
        _hasCaptured = phase != StartScreenPhase.IdleBlobs;
        InvalidateVisual();
    }

    private void OnPhaseChanged()
    {
        _settledInRing = false;
        if (Phase == StartScreenPhase.IdleBlobs)
        {
            _morphT = 0.0;
            _hasCaptured = false;
        }
        else if (_morphT <= 0.001)
        {
            _hasCaptured = false;
        }

        StartAnimationIfNeeded();
        InvalidateVisual();
    }

    private bool NeedsAnimation() =>
        IsStageActive &&
        IsVisible &&
        !ReducedBlur &&
        _hostWindow?.WindowState != WindowState.Minimized &&
        (Phase == StartScreenPhase.IdleBlobs
         || _morphT < 0.999
         || (Phase == StartScreenPhase.RingExpanded && !_settledInRing)
         || (Phase != StartScreenPhase.RingExpanded && _ambientAlpha > 0.005));

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
        _animTimeSec += dt;

        float w = (float)Bounds.Width;
        float h = (float)Bounds.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;

        if (Phase == StartScreenPhase.IdleBlobs)
        {
            _morphT = 0.0;
            _orbitAngle += dt * 0.62;
        }
        else if (_morphT < 1.0)
        {
            if (!_hasCaptured)
            {
                float rx = MedallionRadius + 78f;
                float ry = MedallionRadius + 56f;
                _capturedOrbitX = cx + MathF.Cos((float)_orbitAngle) * rx;
                _capturedOrbitY = cy + MathF.Sin((float)_orbitAngle) * ry;
                _hasCaptured = true;
            }

            _morphT = Math.Min(1.0, _morphT + dt * 1.85);
        }

        double targetAmbient = Phase == StartScreenPhase.RingExpanded ? 1.0 : 0.0;
        _ambientAlpha = AeroSkiaHelpers.Damp(_ambientAlpha, targetAmbient, 5.5, dt);
        if (Math.Abs(_ambientAlpha - targetAmbient) < 0.003)
        {
            _ambientAlpha = targetAmbient;
            if (Phase == StartScreenPhase.RingExpanded && _morphT >= 0.999)
            {
                _settledInRing = true;
            }
        }

        InvalidateVisual();
        ScheduleNextFrame();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new BlobDrawOp(
            new Rect(Bounds.Size),
            IsDark,
            (float)_morphT,
            (float)_orbitAngle,
            _hasCaptured,
            _capturedOrbitX,
            _capturedOrbitY,
            (float)_ambientAlpha,
            (float)_animTimeSec));
    }

    private sealed class BlobDrawOp(
        Rect bounds,
        bool isDark,
        float morphT,
        float orbitAngle,
        bool hasCaptured,
        float capturedOrbitX,
        float capturedOrbitY,
        float ambientAlpha,
        float tSec) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            {
                return;
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            float w = (float)Bounds.Width;
            float h = (float)Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            float cx = w * 0.5f;
            float cy = h * 0.5f;
            float m = 1f - MathF.Pow(1f - morphT, 3f);

            // 1. Soft Aero caustic aura behind expanded crystal ring
            if (ambientAlpha > 0.01f)
            {
                float bx = cx - 150f + MathF.Cos(tSec * 0.35f) * 18f;
                float by = cy - 105f + MathF.Sin(tSec * 0.42f) * 14f;
                float lx = cx + 150f + MathF.Sin(tSec * 0.31f) * 18f;
                float ly = cy + 105f + MathF.Cos(tSec * 0.38f) * 16f;

                DrawRadialAura(canvas, bx, by, 285f,
                    isDark ? new SKColor(110, 196, 255, (byte)(64 * ambientAlpha)) : new SKColor(112, 200, 255, (byte)(102 * ambientAlpha)));
                DrawRadialAura(canvas, lx, ly, 275f,
                    isDark ? new SKColor(142, 230, 76, (byte)(54 * ambientAlpha)) : new SKColor(142, 228, 76, (byte)(90 * ambientAlpha)));
            }

            // 2. Orbiting Liquid Orbs during IdleBlobs / mid-morph
            float blobOpacity = 1f - MathF.Max(0f, (m - 0.55f) / 0.45f);
            if (blobOpacity <= 0.01f)
            {
                return;
            }

            byte A(byte val) => (byte)Math.Round(val * blobOpacity);

            // Outer breathing halo
            float haloR = MedallionRadius * (1.82f - m * 0.4f);
            using (var haloShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                haloR,
                [
                    isDark ? new SKColor(110, 196, 255, A(62)) : new SKColor(122, 203, 255, A(78)),
                    isDark ? new SKColor(142, 230, 76, A(32)) : new SKColor(148, 229, 82, A(48)),
                    new SKColor(122, 203, 255, 0)
                ],
                [0f, 0.55f, 1f],
                SKShaderTileMode.Clamp))
            using (var haloPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = haloShader })
            {
                canvas.DrawCircle(cx, cy, haloR, haloPaint);
            }

            // Large liquid orb -> MetallicMedallion center
            float mainR = MedallionRadius * (1.06f - m * 0.06f);
            using (var mainBlob = BuildOrganicBlob(cx, cy, mainR, tSec, 13.5f, m, 0.4f))
            {
                DrawRefractiveOrb(canvas, mainBlob, cx, cy, mainR, blobOpacity, isSatellite: false, isDark);
            }

            // Small satellite orb -> '+' expand button position
            float rx = MedallionRadius + 78f;
            float ry = MedallionRadius + 56f;
            float liveX = cx + MathF.Cos(orbitAngle) * rx;
            float liveY = cy + MathF.Sin(orbitAngle) * ry;
            float orbX = hasCaptured ? capturedOrbitX + (cx - capturedOrbitX) * m : liveX;
            float orbY = hasCaptured ? capturedOrbitY + (cy + PlusOffsetY - capturedOrbitY) * m : liveY;
            float smallR = PlusRadius * (1.12f - m * 0.12f);
            using (var smallBlob = BuildOrganicBlob(orbX, orbY, smallR, tSec * 1.4f, 4.8f, m, 2.3f))
            {
                DrawRefractiveOrb(canvas, smallBlob, orbX, orbY, smallR, blobOpacity, isSatellite: true, isDark);
            }
        }

        [ThreadStatic]
        private static SKPoint[]? t_coordsScratch;

        private static void DrawRefractiveOrb(
            SKCanvas canvas,
            SKPath path,
            float cx,
            float cy,
            float radius,
            float opacity,
            bool isSatellite,
            bool isDark)
        {
            byte A(byte val) => (byte)Math.Round(val * opacity);

            // Soft zero-blur radial caustic drop shadow (fast 60 FPS on CPU Skia)
            float shadowR = radius * 1.24f;
            SKColor shadowCol = isSatellite
                ? new SKColor(126, 220, 74, A(68))
                : new SKColor(92, 186, 248, A(62));
            using (var shadowShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy + 6f),
                shadowR,
                [shadowCol, shadowCol.WithAlpha((byte)(shadowCol.Alpha / 3)), SKColors.Transparent],
                [0f, 0.56f, 1f],
                SKShaderTileMode.Clamp))
            using (var shadow = new SKPaint { IsAntialias = true, IsDither = true, Shader = shadowShader })
            {
                canvas.DrawCircle(cx, cy + 6f, shadowR, shadow);
            }

            SKColor[] gradColors = isSatellite
                ? [new SKColor(166, 240, 102, A(240)), new SKColor(118, 204, 255, A(235))]
                : isDark
                    ? [new SKColor(128, 212, 255, A(224)), new SKColor(122, 216, 204, A(200)), new SKColor(150, 234, 92, A(218))]
                    : [new SKColor(124, 208, 255, A(236)), new SKColor(148, 228, 210, A(220)), new SKColor(156, 234, 96, A(236))];

            using (var fillShader = SKShader.CreateLinearGradient(
                new SKPoint(cx - radius, cy - radius),
                new SKPoint(cx + radius, cy + radius),
                gradColors,
                gradColors.Length == 3 ? [0f, 0.54f, 1f] : [0f, 1f],
                SKShaderTileMode.Clamp))
            using (var fillPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = fillShader })
            {
                canvas.DrawPath(path, fillPaint);
            }

            // Internal caustic lens highlight
            using (var sheenShader = SKShader.CreateRadialGradient(
                new SKPoint(cx - radius * 0.34f, cy - radius * 0.36f),
                radius * 0.96f,
                [new SKColor(255, 255, 255, A(175)), new SKColor(255, 255, 255, A(42)), new SKColor(255, 255, 255, 0)],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp))
            using (var sheenPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = sheenShader })
            {
                canvas.DrawPath(path, sheenPaint);
            }

            // Specular edge rim
            using (var rimPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f,
                Color = isDark ? new SKColor(225, 246, 255, A(105)) : new SKColor(255, 255, 255, A(220))
            })
            {
                canvas.DrawPath(path, rimPaint);
            }
        }

        private static SKPath BuildOrganicBlob(
            float cx,
            float cy,
            float baseRadius,
            float timeSec,
            float wobbleAmp,
            float tightness,
            float seed)
        {
            const int points = 48;
            float effAmp = wobbleAmp * (1f - tightness);
            var coords = t_coordsScratch ??= new SKPoint[points];

            for (int i = 0; i < points; i++)
            {
                float theta = (i / (float)points) * MathF.PI * 2f;
                float harmonic =
                    MathF.Sin(theta * 2f + timeSec * 1.35f + seed) * 0.55f +
                    MathF.Cos(theta * 3f - timeSec * 0.95f + seed * 1.7f) * 0.35f +
                    MathF.Sin(theta * 5f + timeSec * 1.75f - seed) * 0.1f;
                float r = baseRadius + harmonic * effAmp;
                coords[i] = new SKPoint(cx + MathF.Cos(theta) * r, cy + MathF.Sin(theta) * r);
            }

            var path = new SKPath();
            for (int i = 0; i < points; i++)
            {
                var curr = coords[i];
                var next = coords[(i + 1) % points];
                var mid = new SKPoint((curr.X + next.X) * 0.5f, (curr.Y + next.Y) * 0.5f);
                if (i == 0)
                {
                    var prev = coords[points - 1];
                    path.MoveTo((prev.X + curr.X) * 0.5f, (prev.Y + curr.Y) * 0.5f);
                }

                path.QuadTo(curr, mid);
            }

            path.Close();
            return path;
        }

        private static void DrawRadialAura(SKCanvas canvas, float cx, float cy, float radius, SKColor color)
        {
            using var shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                radius,
                [color, color.WithAlpha(0)],
                (float[]?)null,
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { IsAntialias = true, IsDither = true, Shader = shader };
            canvas.DrawCircle(cx, cy, radius, paint);
        }
    }
}
