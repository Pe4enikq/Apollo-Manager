using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Walkman.L3.AppShell.Controls;

/// <summary>
/// Precision anisotropic CNC brushed-metal container without CSS conic-pinch artifacts.
/// Used for wide File Manager / Codec Converter hardware consoles and both Floating Bottom Dock bars.
/// </summary>
public sealed class BrushedMetalPanel : Decorator
{
    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<BrushedMetalPanel, bool>(nameof(IsDark));

    public static readonly StyledProperty<double> CornerRadiusValueProperty =
        AvaloniaProperty.Register<BrushedMetalPanel, double>(nameof(CornerRadiusValue), 18);

    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public double CornerRadiusValue
    {
        get => GetValue(CornerRadiusValueProperty);
        set => SetValue(CornerRadiusValueProperty, value);
    }

    private const int ShadowPad = 22;
    private AeroSkiaHelpers.RefCountedSkImage? _cachedImage;
    private int _cachedW;
    private int _cachedH;
    private bool _cachedDark;
    private float _cachedRadius;

    public BrushedMetalPanel()
    {
        ClipToBounds = false;
    }

    static BrushedMetalPanel()
    {
        AffectsRender<BrushedMetalPanel>(IsDarkProperty, CornerRadiusValueProperty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cachedImage?.Release();
        _cachedImage = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        if (Opacity <= 0.005)
        {
            return;
        }

        int w = Math.Max(0, (int)Math.Ceiling(Bounds.Width));
        int h = Math.Max(0, (int)Math.Ceiling(Bounds.Height));
        float r = (float)CornerRadiusValue;
        if (w > 2 && h > 2)
        {
            if (_cachedImage is null || _cachedW != w || _cachedH != h || _cachedDark != IsDark || MathF.Abs(_cachedRadius - r) > 0.1f)
            {
                _cachedImage?.Release();
                _cachedImage = new AeroSkiaHelpers.RefCountedSkImage(BakeMetalImage(w, h, IsDark, r));
                _cachedW = w;
                _cachedH = h;
                _cachedDark = IsDark;
                _cachedRadius = r;
            }

            context.Custom(new MetalPanelDrawOp(new Rect(Bounds.Size), _cachedImage));
        }

        base.Render(context);
    }

    private static SKImage BakeMetalImage(int wInt, int hInt, bool isDark, float cornerRadius)
    {
        int totalW = wInt + ShadowPad * 2;
        int totalH = hInt + ShadowPad * 2;
        float w = wInt;
        float h = hInt;

        using var bmp = new SKBitmap(totalW, totalH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(ShadowPad, ShadowPad);

            var rect = new SKRect(0, 0, w, h);
            var rrect = new SKRoundRect(rect, cornerRadius, cornerRadius);

            // Ambient elevation shadow beneath machined aluminum housing
            using (var dropBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f))
            using (var dropPaint = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(0, 0, 0, 128) : new SKColor(38, 60, 86, 44),
                MaskFilter = dropBlur
            })
            {
                canvas.Save();
                canvas.Translate(0, 6);
                canvas.DrawRoundRect(rrect, dropPaint);
                canvas.Restore();
            }

            canvas.Save();
            canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, true);

            // Primary anisotropic brushed satin aluminum faceplate sweep
            SKColor[] colors = isDark
                ? [
                    new SKColor(52, 60, 74),
                    new SKColor(38, 44, 55),
                    new SKColor(62, 72, 88),
                    new SKColor(33, 38, 48),
                    new SKColor(48, 56, 68)
                  ]
                : [
                    new SKColor(232, 238, 246),
                    new SKColor(214, 224, 236),
                    new SKColor(246, 250, 255),
                    new SKColor(204, 215, 228),
                    new SKColor(228, 236, 245)
                  ];

            using (var metalShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(w, h),
                colors,
                [0f, 0.24f, 0.50f, 0.78f, 1f],
                SKShaderTileMode.Clamp))
            using (var metalPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = metalShader })
            {
                canvas.DrawRect(rect, metalPaint);
            }

            // Diagonal satin reflection band mimicking Walkman NW-S700/A-series anodized chassis
            using (var bandShader = SKShader.CreateLinearGradient(
                new SKPoint(w * 0.08f, 0),
                new SKPoint(w * 0.92f, h),
                [
                    SKColors.Transparent,
                    isDark ? new SKColor(215, 234, 255, 16) : new SKColor(255, 255, 255, 68),
                    SKColors.Transparent
                ],
                [0.12f, 0.44f, 0.82f],
                SKShaderTileMode.Clamp))
            using (var bandPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = bandShader })
            {
                canvas.DrawRect(rect, bandPaint);
            }

            // Soft top-left directional specular glow (without pinch)
            using (var sheenShader = SKShader.CreateRadialGradient(
                new SKPoint(w * 0.24f, h * 0.14f),
                MathF.Max(w, h) * 0.80f,
                [
                    isDark ? new SKColor(210, 232, 255, 18) : new SKColor(255, 255, 255, 86),
                    isDark ? new SKColor(110, 196, 255, 8) : new SKColor(180, 228, 255, 20),
                    SKColors.Transparent
                ],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp))
            using (var sheenPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = sheenShader })
            {
                canvas.DrawRect(rect, sheenPaint);
            }

            // Ultra-fine stochastic brushed hairline grain (256x64 seamless tile)
            using (var grainPaint = new SKPaint { Shader = AeroSkiaHelpers.GetBrushedGrainShader(isDark) })
            {
                canvas.DrawRect(rect, grainPaint);
            }

            // Top specular chamfer line + bottom inner bevel shadow
            using (var topEdge = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(220, 236, 255, 62) : new SKColor(255, 255, 255, 240),
                StrokeWidth = 1.5f
            })
            {
                canvas.DrawLine(cornerRadius * 0.5f, 1f, w - cornerRadius * 0.5f, 1f, topEdge);
            }

            using (var botEdge = new SKPaint
            {
                IsAntialias = true,
                Color = isDark ? new SKColor(0, 0, 0, 140) : new SKColor(118, 136, 158, 92),
                StrokeWidth = 1.5f
            })
            {
                canvas.DrawLine(cornerRadius * 0.5f, h - 1f, w - cornerRadius * 0.5f, h - 1f, botEdge);
            }

            canvas.Restore();

            // Outer precision machined border
            using (var borderPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.15f,
                Color = isDark ? new SKColor(126, 146, 174, 96) : new SKColor(168, 186, 206, 195)
            })
            {
                canvas.DrawRoundRect(rrect, borderPaint);
            }
        }

        AeroSkiaHelpers.ApplyTriangularDither(bmp, 0.9f);
        return SKImage.FromBitmap(bmp);
    }

    private sealed class MetalPanelDrawOp : ICustomDrawOperation
    {
        private AeroSkiaHelpers.RefCountedSkImage? _holder;
        private readonly SKImage? _image;

        public MetalPanelDrawOp(Rect bounds, AeroSkiaHelpers.RefCountedSkImage holder)
        {
            Bounds = bounds;
            _image = holder.TryAcquire();
            _holder = _image is not null ? holder : null;
        }

        public Rect Bounds { get; }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() => Interlocked.Exchange(ref _holder, null)?.Release();

        public void Render(ImmediateDrawingContext context)
        {
            if (_image is null || context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            {
                return;
            }

            using var lease = leaseFeature.Lease();
            lease.SkCanvas.DrawImage(_image, -ShadowPad, -ShadowPad);
        }
    }
}
