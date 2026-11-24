using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Walkman.L3.AppShell.Controls;

public sealed class AeroStageBackgroundControl : Control
{
    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<AeroStageBackgroundControl, bool>(nameof(IsDark));

    public static readonly StyledProperty<bool> ReducedBlurProperty =
        AvaloniaProperty.Register<AeroStageBackgroundControl, bool>(nameof(ReducedBlur));

    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public bool ReducedBlur
    {
        get => GetValue(ReducedBlurProperty);
        set => SetValue(ReducedBlurProperty, value);
    }

    private AeroSkiaHelpers.RefCountedSkImage? _cachedImage;
    private int _cachedW;
    private int _cachedH;
    private bool _cachedDark;
    private bool _cachedReduced;

    static AeroStageBackgroundControl()
    {
        AffectsRender<AeroStageBackgroundControl>(IsDarkProperty, ReducedBlurProperty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cachedImage?.Release();
        _cachedImage = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        int w = Math.Max(0, (int)Math.Ceiling(Bounds.Width));
        int h = Math.Max(0, (int)Math.Ceiling(Bounds.Height));
        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (_cachedImage is null || _cachedW != w || _cachedH != h || _cachedDark != IsDark || _cachedReduced != ReducedBlur)
        {
            _cachedImage?.Release();
            _cachedImage = new AeroSkiaHelpers.RefCountedSkImage(BakeBackgroundImage(w, h, IsDark, ReducedBlur));
            _cachedW = w;
            _cachedH = h;
            _cachedDark = IsDark;
            _cachedReduced = ReducedBlur;
        }

        context.Custom(new BackgroundDrawOp(new Rect(Bounds.Size), _cachedImage));
    }

    private static SKImage BakeBackgroundImage(int w, int h, bool isDark, bool reducedBlur)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            float fw = w;
            float fh = h;

            SKColor[] baseColors = isDark
                ? [new SKColor(19, 23, 30), new SKColor(26, 32, 42), new SKColor(18, 22, 29)]
                : [new SKColor(246, 250, 255), new SKColor(232, 240, 249), new SKColor(241, 248, 246)];

            using (var baseShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(fw * 0.9f, fh),
                baseColors,
                [0f, 0.52f, 1f],
                SKShaderTileMode.Clamp))
            using (var basePaint = new SKPaint { IsDither = true, Shader = baseShader })
            {
                canvas.DrawRect(0, 0, fw, fh, basePaint);
            }

            float scale = reducedBlur ? 0.55f : 1.0f;
            DrawBloom(canvas, fw * 0.20f, fh * 0.16f, MathF.Max(fw, fh) * 0.52f,
                isDark ? new SKColor(96, 186, 255, (byte)(30 * scale)) : new SKColor(112, 202, 255, (byte)(56 * scale)));

            DrawBloom(canvas, fw * 0.82f, fh * 0.84f, MathF.Max(fw, fh) * 0.48f,
                isDark ? new SKColor(138, 224, 72, (byte)(24 * scale)) : new SKColor(148, 230, 86, (byte)(48 * scale)));

            DrawBloom(canvas, fw * 0.52f, fh * 0.48f, MathF.Max(fw, fh) * 0.36f,
                isDark ? new SKColor(72, 164, 210, (byte)(14 * scale)) : new SKColor(188, 240, 232, (byte)(42 * scale)));
        }

        AeroSkiaHelpers.ApplyTriangularDither(bmp, 1.35f);
        return SKImage.FromBitmap(bmp);
    }

    private static void DrawBloom(SKCanvas canvas, float cx, float cy, float radius, SKColor centerColor)
    {
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy),
            radius,
            [centerColor, centerColor.WithAlpha((byte)(centerColor.Alpha / 3)), centerColor.WithAlpha(0)],
            [0f, 0.48f, 1f],
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, IsDither = true, Shader = shader };
        canvas.DrawCircle(cx, cy, radius, paint);
    }

    private sealed class BackgroundDrawOp : ICustomDrawOperation
    {
        private AeroSkiaHelpers.RefCountedSkImage? _holder;
        private readonly SKImage? _image;

        public BackgroundDrawOp(Rect bounds, AeroSkiaHelpers.RefCountedSkImage holder)
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
            lease.SkCanvas.DrawImage(_image, 0, 0);
        }
    }
}
