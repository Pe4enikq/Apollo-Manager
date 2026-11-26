using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Walkman.L3.AppShell.Controls;

public sealed class AeroScrubberControl : Control
{
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<AeroScrubberControl, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AeroScrubberControl, double>(nameof(Value), 0);

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<AeroScrubberControl, bool>(nameof(IsDark));

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public event Action<double>? SeekRequested;

    private bool _dragging;
    private bool _isHovered;
    private double _dragValue;

    public AeroScrubberControl()
    {
        Height = 18;
        Cursor = AeroSkiaHelpers.HandCursor;
    }

    static AeroScrubberControl()
    {
        AffectsRender<AeroScrubberControl>(MaximumProperty, ValueProperty, IsDarkProperty);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (!_isHovered)
        {
            _isHovered = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_isHovered)
        {
            _isHovered = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
            _dragValue = ComputeSeconds(e.GetPosition(this).X);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            _dragValue = ComputeSeconds(e.GetPosition(this).X);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            double target = ComputeSeconds(e.GetPosition(this).X);
            _dragValue = target;
            SeekRequested?.Invoke(target);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
        }
    }

    private double ComputeSeconds(double x)
    {
        double pad = 6;
        double trackW = Math.Max(1, Bounds.Width - pad * 2);
        double ratio = Math.Clamp((x - pad) / trackW, 0.0, 1.0);
        return ratio * Math.Max(1, Maximum);
    }

    public override void Render(DrawingContext context)
    {
        double max = Math.Max(1.0, Maximum);
        double activeValue = _dragging ? _dragValue : Value;
        double ratio = Math.Clamp(activeValue / max, 0.0, 1.0);
        context.Custom(new ScrubberDrawOp(new Rect(Bounds.Size), (float)ratio, IsDark, _isHovered || _dragging));
    }

    private sealed class ScrubberDrawOp(Rect bounds, float ratio, bool isDark, bool emphasized) : ICustomDrawOperation
    {
        private static readonly SKMaskFilter ThumbBlurNormal = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f);
        private static readonly SKMaskFilter ThumbBlurEmphasized = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5.5f);

        public Rect Bounds { get; } = bounds;
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => Bounds.Contains(p);
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
            if (w <= 12 || h <= 4) return;

            float pad = 6f;
            float trackH = 4.5f;
            float trackY = (h - trackH) * 0.5f;
            float trackW = w - pad * 2f;
            float thumbX = pad + trackW * ratio;
            float cy = h * 0.5f;

            // Dark recessed trough
            using (var troughPaint = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(8, 11, 16, 210) : new SKColor(28, 38, 54, 210)
            })
            {
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(pad, trackY, w - pad, trackY + trackH), 2.5f, 2.5f), troughPaint);
            }

            // Active cyan -> lime fill
            if (ratio > 0.002f)
            {
                using var fillShader = SKShader.CreateLinearGradient(
                    new SKPoint(pad, cy),
                    new SKPoint(MathF.Max(pad + 6f, thumbX), cy),
                    [new SKColor(56, 189, 248), new SKColor(142, 228, 78)],
                    (float[]?)null,
                    SKShaderTileMode.Clamp);
                using var fillPaint = new SKPaint { IsAntialias = true, Shader = fillShader };
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(pad, trackY, thumbX, trackY + trackH), 2.5f, 2.5f), fillPaint);
            }

            // Crisp sky-blue thumb glow & sphere (cached static mask filters)
            float thumbR = emphasized ? 6.2f : 5.2f;
            using (var thumbGlow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(56, 189, 248, (byte)(emphasized ? 175 : 120)),
                MaskFilter = emphasized ? ThumbBlurEmphasized : ThumbBlurNormal
            })
            {
                canvas.DrawCircle(thumbX, cy, thumbR + 1f, thumbGlow);
            }

            using (var thumbPaint = new SKPaint { IsAntialias = true, Color = new SKColor(38, 174, 248) })
            {
                canvas.DrawCircle(thumbX, cy, thumbR, thumbPaint);
            }

            using (var thumbSpec = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 200) })
            {
                canvas.DrawCircle(thumbX - thumbR * 0.27f, cy - thumbR * 0.27f, thumbR * 0.31f, thumbSpec);
            }
        }
    }
}
