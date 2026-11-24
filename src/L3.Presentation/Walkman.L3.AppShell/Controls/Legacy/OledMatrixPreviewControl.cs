using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Walkman.Contracts.Engines;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Controls.Legacy;

/// <summary>
/// Archived prototype 1-bit/2-bit OLED matrix renderer moved to Legacy pending multi-generation
/// Walkman screen customization research (see Ticket UI-01 in report desk/TICKETS_UI_POST_MVP_RESEARCH.md).
/// </summary>
public sealed class OledMatrixPreviewControl : Control
{
    public static readonly StyledProperty<ScreenTintMode> TintModeProperty =
        AvaloniaProperty.Register<OledMatrixPreviewControl, ScreenTintMode>(nameof(TintMode), ScreenTintMode.Cyan);

    public static readonly StyledProperty<string> AnimationModeProperty =
        AvaloniaProperty.Register<OledMatrixPreviewControl, string>(nameof(AnimationMode), "spectrum");

    public static readonly StyledProperty<NwmProjectDto?> LoadedNwmProjectProperty =
        AvaloniaProperty.Register<OledMatrixPreviewControl, NwmProjectDto?>(nameof(LoadedNwmProject));

    public static readonly StyledProperty<bool> IsPreviewActiveProperty =
        AvaloniaProperty.Register<OledMatrixPreviewControl, bool>(nameof(IsPreviewActive), true);

    public ScreenTintMode TintMode
    {
        get => GetValue(TintModeProperty);
        set => SetValue(TintModeProperty, value);
    }

    public string AnimationMode
    {
        get => GetValue(AnimationModeProperty);
        set => SetValue(AnimationModeProperty, value);
    }

    public NwmProjectDto? LoadedNwmProject
    {
        get => GetValue(LoadedNwmProjectProperty);
        set => SetValue(LoadedNwmProjectProperty, value);
    }

    public bool IsPreviewActive
    {
        get => GetValue(IsPreviewActiveProperty);
        set => SetValue(IsPreviewActiveProperty, value);
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _isAttached;
    private bool _frameRequested;
    private double _lastRenderSec;

    static OledMatrixPreviewControl()
    {
        AffectsRender<OledMatrixPreviewControl>(TintModeProperty, AnimationModeProperty, LoadedNwmProjectProperty, IsPreviewActiveProperty);
        IsPreviewActiveProperty.Changed.AddClassHandler<OledMatrixPreviewControl>((c, _) => c.UpdateAnimationState());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateAnimationState()
    {
        if (!_isAttached || !IsPreviewActive || _frameRequested)
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
        if (!_isAttached || !IsPreviewActive)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastRenderSec >= 0.033)
        {
            _lastRenderSec = now;
            InvalidateVisual();
        }

        UpdateAnimationState();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new OledDrawOp(
            new Rect(Bounds.Size),
            TintMode,
            AnimationMode,
            LoadedNwmProject,
            (float)_clock.Elapsed.TotalSeconds));
    }

    private sealed class OledDrawOp(
        Rect bounds,
        ScreenTintMode tintMode,
        string animationMode,
        NwmProjectDto? nwmProject,
        float timeSec) : ICustomDrawOperation
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

            (SKColor onColor, SKColor dimColor, SKColor bgColor) = tintMode switch
            {
                ScreenTintMode.Lime => (new SKColor(156, 242, 92), new SKColor(32, 58, 26), new SKColor(11, 22, 10)),
                ScreenTintMode.Amber => (new SKColor(255, 200, 97), new SKColor(64, 46, 20), new SKColor(22, 16, 8)),
                _ => (new SKColor(123, 224, 255), new SKColor(24, 56, 74), new SKColor(9, 20, 30))
            };

            using var bgPaint = new SKPaint { Color = bgColor };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, w, h), 8, 8), bgPaint);

            bool useCustomNwm = animationMode == "custom" && nwmProject is { Timeline.Count: > 0, UniqueBitmaps.Count: > 0 };
            int cols = useCustomNwm ? nwmProject!.Width : 64;
            int rows = useCustomNwm ? nwmProject!.Height : 24;

            float pad = 4f;
            float cellW = (w - pad * 2f) / cols;
            float cellH = (h - pad * 2f) / rows;
            float step = MathF.Min(cellW, cellH);
            float ox = (w - cols * step) * 0.5f;
            float oy = (h - rows * step) * 0.5f;
            float gap = MathF.Max(0.6f, step * 0.16f);
            float pxSize = MathF.Max(1f, step - gap);

            byte[]? activeNwmBitmap = null;
            if (useCustomNwm)
            {
                int frameIdx = (int)(timeSec * 12f) % nwmProject!.Timeline.Count;
                byte bmpIdx = nwmProject.Timeline[frameIdx].BitmapIndex;
                if (bmpIdx < nwmProject.UniqueBitmaps.Count)
                {
                    activeNwmBitmap = nwmProject.UniqueBitmaps[bmpIdx];
                }
            }

            using var dimPath = new SKPath();
            using var litPath = new SKPath();

            for (int y = 0; y < rows; y++)
            {
                float ry = oy + y * step + gap * 0.5f;
                for (int x = 0; x < cols; x++)
                {
                    bool lit = IsPixelLit(x, y, cols, rows, timeSec, animationMode, activeNwmBitmap, nwmProject);
                    float rx = ox + x * step + gap * 0.5f;
                    var cellRect = new SKRect(rx, ry, rx + pxSize, ry + pxSize);

                    if (lit)
                    {
                        litPath.AddRect(cellRect);
                    }
                    else
                    {
                        dimPath.AddRect(cellRect);
                    }
                }
            }

            if (dimPath.PointCount > 0)
            {
                using var dimPaint = new SKPaint { Color = dimColor };
                canvas.DrawPath(dimPath, dimPaint);
            }

            if (litPath.PointCount > 0)
            {
                using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(2f, step * 0.8f));
                using var glowPaint = new SKPaint
                {
                    Color = onColor.WithAlpha(78),
                    MaskFilter = glowBlur
                };
                canvas.DrawPath(litPath, glowPaint);

                using var onPaint = new SKPaint { Color = onColor };
                canvas.DrawPath(litPath, onPaint);
            }
        }

        private static bool IsPixelLit(
            int x,
            int y,
            int cols,
            int rows,
            float t,
            string mode,
            byte[]? nwmBitmap,
            NwmProjectDto? project)
        {
            if (nwmBitmap is not null && project is not null)
            {
                int bpp = project.BitsPerPixel;
                int bitIndex = y * cols + x;
                if (bpp == 1)
                {
                    int byteIdx = bitIndex >> 3;
                    return byteIdx < nwmBitmap.Length && (nwmBitmap[byteIdx] & (0x80 >> (bitIndex & 7))) != 0;
                }

                if (bpp == 2)
                {
                    int byteIdx = bitIndex >> 2;
                    if (byteIdx < nwmBitmap.Length)
                    {
                        int shift = (3 - (bitIndex & 3)) * 2;
                        return ((nwmBitmap[byteIdx] >> shift) & 0x03) >= 2;
                    }
                }
            }

            if (mode == "orbit")
            {
                float cx = cols * 0.5f;
                float cy = rows * 0.5f;
                float distCenter = AeroSkiaHelpers.Hypot((x - cx) * 0.75f, y - cy);
                float satX = cx + MathF.Cos(t * 1.7f) * 16f;
                float satY = cy + MathF.Sin(t * 1.7f) * 7.5f;
                float distSat = AeroSkiaHelpers.Hypot(x - satX, y - satY);
                return distCenter < 5.5f || distSat < 2.6f;
            }

            if (mode == "classic")
            {
                int waveY = (int)MathF.Round(12f + MathF.Sin(x * 0.24f - t * 4.2f) * 6.5f);
                int waveY2 = (int)MathF.Round(12f + MathF.Cos(x * 0.19f - t * 2.9f) * 4.2f);
                return Math.Abs(y - waveY) <= 1 || Math.Abs(y - waveY2) == 0;
            }

            int band = x / 4;
            bool isGapCol = (x % 4) == 3;
            int level = (int)MathF.Floor((MathF.Sin(t * 3.4f + band * 0.7f) * 0.5f +
                                          MathF.Cos(t * 2.1f - band * 1.1f) * 0.3f + 0.85f) * 11f) + 2;
            return !isGapCol && (rows - 1 - y) < Math.Min(rows - 2, level);
        }
    }
}
