using Avalonia.Input;
using SkiaSharp;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Controls;

internal static class AeroSkiaHelpers
{
    public static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static readonly SKFontStyle SemiBoldStyle = new(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    public static readonly SKTypeface InterRegular = SKTypeface.FromFamilyName("Inter", SKFontStyle.Normal);
    public static readonly SKTypeface InterSemiBold = SKTypeface.FromFamilyName("Inter", SemiBoldStyle);
    public static readonly SKTypeface InterBold = SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold);
    public static readonly SKTypeface DisplayBold = SKTypeface.FromFamilyName("Century Gothic", SKFontStyle.Bold) ?? InterBold;
    public static readonly SKTypeface MonoRegular = SKTypeface.FromFamilyName("JetBrains Mono", SKFontStyle.Normal);
    public static readonly SKTypeface MonoSemiBold = SKTypeface.FromFamilyName("JetBrains Mono", SemiBoldStyle);

    private static readonly SKBitmap LightGrainBitmap = CreateGrainBitmap(isDark: false);
    private static readonly SKBitmap DarkGrainBitmap = CreateGrainBitmap(isDark: true);
    private static readonly SKShader LightGrainShader = SKShader.CreateBitmap(LightGrainBitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
    private static readonly SKShader DarkGrainShader = SKShader.CreateBitmap(DarkGrainBitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);

    public static SKShader GetBrushedGrainShader(bool isDark) => isDark ? DarkGrainShader : LightGrainShader;

    private static SKBitmap CreateGrainBitmap(bool isDark)
    {
        const int width = 256;
        const int height = 64;
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        // Seeded stochastic brushed-aluminum hairline micro-scratches without periodic ladder bands.
        var rng = new Random(isDark ? 9187 : 4219);
        using var scratchPaint = new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };

        for (int i = 0; i < 420; i++)
        {
            int y = rng.Next(0, height);
            float startX = (float)(rng.NextDouble() * width);
            float len = 14f + (float)(rng.NextDouble() * 118f);
            bool isBright = (i & 1) == 0;
            byte alpha = isBright
                ? (byte)rng.Next(isDark ? 3 : 5, isDark ? 10 : 15)
                : (byte)rng.Next(isDark ? 4 : 3, isDark ? 11 : 8);

            scratchPaint.Color = isBright
                ? new SKColor(255, 255, 255, alpha)
                : new SKColor(18, 30, 48, alpha);

            canvas.DrawLine(startX, y + 0.5f, startX + len, y + 0.5f, scratchPaint);
            if (startX + len > width)
            {
                // Wrap across tile boundary for seamless horizontal repeat
                canvas.DrawLine(startX - width, y + 0.5f, startX + len - width, y + 0.5f, scratchPaint);
            }
        }

        return bmp;
    }

    public static float Damp(float current, float target, float speed, double dt)
    {
        float factor = 1f - MathF.Exp(-speed * (float)dt);
        return current + (target - current) * factor;
    }

    public static double Damp(double current, double target, double speed, double dt)
    {
        double factor = 1.0 - Math.Exp(-speed * dt);
        return current + (target - current) * factor;
    }

    public static float Hypot(float dx, float dy) => MathF.Sqrt(dx * dx + dy * dy);

    public static SKColor Mix(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)Math.Round(a.Red + (b.Red - a.Red) * t),
            (byte)Math.Round(a.Green + (b.Green - a.Green) * t),
            (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t),
            (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    public static SKPath BuildRoundedSectorPath(
        double cx,
        double cy,
        double innerR,
        double outerR,
        double startDeg,
        double endDeg,
        double cornerRadius = 18)
    {
        var path = new SKPath();
        double sweepDeg = endDeg - startDeg;
        if (sweepDeg <= 0 || outerR <= innerR)
        {
            return path;
        }

        double thickness = outerR - innerR;
        double rCorner = Math.Min(cornerRadius, thickness * 0.42);
        double outerAngularInset = Math.Min((rCorner / outerR) * (180.0 / Math.PI), sweepDeg * 0.34);
        double innerAngularInset = Math.Min((rCorner / innerR) * (180.0 / Math.PI), sweepDeg * 0.34);

        var pOuterStartArc = Polar(cx, cy, outerR, startDeg + outerAngularInset);
        var pOuterEndArc = Polar(cx, cy, outerR, endDeg - outerAngularInset);
        var pOuterCornerStart = Polar(cx, cy, outerR, startDeg);
        var pOuterCornerEnd = Polar(cx, cy, outerR, endDeg);

        var pEdgeEndOuter = Polar(cx, cy, outerR - rCorner, endDeg);
        var pEdgeEndInner = Polar(cx, cy, innerR + rCorner, endDeg);
        var pEdgeStartInner = Polar(cx, cy, innerR + rCorner, startDeg);
        var pEdgeStartOuter = Polar(cx, cy, outerR - rCorner, startDeg);

        var pInnerEndArc = Polar(cx, cy, innerR, endDeg - innerAngularInset);
        var pInnerStartArc = Polar(cx, cy, innerR, startDeg + innerAngularInset);
        var pInnerCornerEnd = Polar(cx, cy, innerR, endDeg);
        var pInnerCornerStart = Polar(cx, cy, innerR, startDeg);

        var outerRect = new SKRect((float)(cx - outerR), (float)(cy - outerR), (float)(cx + outerR), (float)(cy + outerR));
        var innerRect = new SKRect((float)(cx - innerR), (float)(cy - innerR), (float)(cx + innerR), (float)(cy + innerR));

        float outerStart = (float)(startDeg + outerAngularInset);
        float outerSweep = (float)(sweepDeg - outerAngularInset * 2);
        float innerStart = (float)(endDeg - innerAngularInset);
        float innerSweep = -(float)(sweepDeg - innerAngularInset * 2);

        path.MoveTo(pOuterStartArc);
        path.ArcTo(outerRect, outerStart, outerSweep, false);
        path.QuadTo(pOuterCornerEnd, pEdgeEndOuter);
        path.LineTo(pEdgeEndInner);
        path.QuadTo(pInnerCornerEnd, pInnerEndArc);
        path.ArcTo(innerRect, innerStart, innerSweep, false);
        path.QuadTo(pInnerCornerStart, pEdgeStartInner);
        path.LineTo(pEdgeStartOuter);
        path.QuadTo(pOuterCornerStart, pOuterStartArc);
        path.Close();
        return path;
    }

    public static void DrawOpticalGlassSector(
        SKCanvas canvas,
        ComputedRingSector sector,
        double cx,
        double cy,
        bool isDark,
        float hoverT,
        bool isActive,
        float opacity = 1f,
        float glassOpacityBoost = 1f,
        bool applyPopOffset = true)
    {
        if (opacity <= 0.01f)
        {
            return;
        }

        hoverT = Math.Clamp(hoverT, 0f, 1f);
        float tx = 0f;
        float ty = 0f;
        if (applyPopOffset)
        {
            double rad = sector.MidAngleDeg * Math.PI / 180.0;
            double popDist = isActive ? Math.Max(3.5, 9.0 * hoverT) : 9.0 * hoverT;
            tx = (float)(Math.Cos(rad) * popDist);
            ty = (float)(Math.Sin(rad) * popDist);
        }

        canvas.Save();
        canvas.Translate(tx, ty);

        using var path = BuildRoundedSectorPath(
            cx,
            cy,
            sector.InnerRadius,
            sector.OuterRadius,
            sector.StartAngleDeg,
            sector.EndAngleDeg,
            sector.CornerRadius);

        var bounds = path.Bounds;
        byte alphaScale = (byte)Math.Round(255 * Math.Clamp(opacity, 0f, 1f));

        // 1. Caustic tinted drop shadow
        byte shadowAlpha = sector.Module.Accent switch
        {
            ModuleAccent.Lime => (byte)Math.Round(((isDark ? 50f : 46f) + 38f * hoverT) * opacity),
            ModuleAccent.Blue => (byte)Math.Round(((isDark ? 52f : 46f) + 38f * hoverT) * opacity),
            _ => (byte)Math.Round((isDark ? 110f : 44f) * opacity)
        };
        SKColor shadowColor = sector.Module.Accent switch
        {
            ModuleAccent.Lime => isDark ? new SKColor(126, 224, 64, shadowAlpha) : new SKColor(102, 198, 48, shadowAlpha),
            ModuleAccent.Blue => isDark ? new SKColor(92, 186, 255, shadowAlpha) : new SKColor(68, 162, 232, shadowAlpha),
            _ => isDark ? new SKColor(0, 0, 0, shadowAlpha) : new SKColor(68, 102, 138, shadowAlpha)
        };

        using (var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f + 4f * hoverT))
        using (var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = shadowColor,
            MaskFilter = shadowBlur
        })
        {
            canvas.Save();
            canvas.Translate(0f, 4f + 2f * hoverT);
            canvas.DrawPath(path, shadowPaint);
            canvas.Restore();
        }

        // 2. Optical translucent crystal glass fill
        SKColor[] fillColors = ResolveSectorGradient(sector.Module.Accent, isDark, isActive, hoverT, alphaScale, glassOpacityBoost);
        using (var fillShader = SKShader.CreateLinearGradient(
            new SKPoint(bounds.Left + bounds.Width * 0.1f, bounds.Top),
            new SKPoint(bounds.Right - bounds.Width * 0.1f, bounds.Bottom),
            fillColors,
            [0f, 0.36f, 0.74f, 1f],
            SKShaderTileMode.Clamp))
        using (var fillPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = fillShader })
        {
            canvas.DrawPath(path, fillPaint);
        }

        // 3. Soft global top-left directional Fresnel reflection + upper inner bevel (no opaque outer-arc sticker)
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, true);

        using (var fresnelShader = SKShader.CreateLinearGradient(
            new SKPoint(bounds.Left, bounds.Top),
            new SKPoint(bounds.Left + bounds.Width * 0.55f, bounds.Top + bounds.Height * 0.62f),
            [
                new SKColor(255, 255, 255, (byte)((isDark ? 48 : 136) * opacity)),
                new SKColor(240, 250, 255, (byte)((isDark ? 14 : 42) * opacity)),
                SKColors.Transparent
            ],
            [0f, 0.48f, 1f],
            SKShaderTileMode.Clamp))
        using (var fresnelPaint = new SKPaint { IsAntialias = true, IsDither = true, Shader = fresnelShader })
        {
            canvas.DrawPath(path, fresnelPaint);
        }

        using (var innerBevel = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            Color = new SKColor(255, 255, 255, (byte)((isDark ? 42 : 168) * opacity))
        })
        {
            canvas.Save();
            canvas.Translate(0f, 1.1f);
            canvas.DrawPath(path, innerBevel);
            canvas.Restore();
        }

        canvas.Restore();

        // 4. Prismatic refractive outer border + inner bevel thickness rim
        float rimBlend = MathF.Max(hoverT, isActive ? 1f : 0f);
        SKColor[] idleRim = isDark
            ? [new SKColor(232, 244, 255, (byte)(108 * opacity)), new SKColor(116, 200, 255, (byte)(126 * opacity)), new SKColor(144, 228, 86, (byte)(106 * opacity))]
            : [new SKColor(255, 255, 255, alphaScale), new SKColor(132, 206, 255, (byte)(210 * opacity)), new SKColor(148, 228, 92, (byte)(200 * opacity))];
        SKColor[] activeRim = sector.Module.Accent == ModuleAccent.Lime || isActive
            ? [new SKColor(255, 255, 255, alphaScale), new SKColor(116, 224, 58, alphaScale), new SKColor(138, 224, 255, alphaScale)]
            : [new SKColor(255, 255, 255, alphaScale), new SKColor(82, 184, 255, alphaScale), new SKColor(148, 229, 82, alphaScale)];

        SKColor[] rimColors =
        [
            Mix(idleRim[0], activeRim[0], rimBlend),
            Mix(idleRim[1], activeRim[1], rimBlend),
            Mix(idleRim[2], activeRim[2], rimBlend)
        ];

        using (var rimShader = SKShader.CreateLinearGradient(
            new SKPoint(bounds.Left, bounds.Top),
            new SKPoint(bounds.Right, bounds.Bottom),
            rimColors,
            [0f, 0.52f, 1f],
            SKShaderTileMode.Clamp))
        using (var rimPaint = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.45f + 0.65f * rimBlend,
            Shader = rimShader
        })
        {
            canvas.DrawPath(path, rimPaint);
        }

        using (var innerRimPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.85f,
            Color = isDark
                ? new SKColor(255, 255, 255, (byte)(44 * opacity))
                : new SKColor(255, 255, 255, (byte)(220 * opacity))
        })
        {
            canvas.DrawPath(path, innerRimPaint);
        }

        canvas.Restore();
    }

    public static void DrawEtchedModuleIcon(
        SKCanvas canvas,
        string moduleId,
        float cx,
        float cy,
        float size,
        SKColor inkColor,
        bool isDark,
        float opacity = 1f)
    {
        float scale = size / 24f;
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);
        canvas.Translate(-12f, -12f);

        // Engraved bottom light catch (glass micro-relief)
        canvas.Save();
        canvas.Translate(0f, 1.0f / Math.Max(0.5f, scale));
        DrawIconGeometry(canvas, moduleId, isDark
            ? new SKColor(0, 0, 0, (byte)(155 * opacity))
            : new SKColor(255, 255, 255, (byte)(235 * opacity)), 1.95f);
        canvas.Restore();

        // Primary etched glyph stroke
        DrawIconGeometry(canvas, moduleId, inkColor.WithAlpha((byte)(inkColor.Alpha * opacity)), 1.85f);
        canvas.Restore();
    }

    /// <summary>
    /// Renders an unrotated horizontal neon-under-frosted-glass icon glow strictly from hover state.
    /// Uses multi-pass blurred strokes with no crisp solid outline so it glows like a neon tube beneath frosted glass.
    /// </summary>
    public static void DrawNeonUnderGlassIcon(
        SKCanvas canvas,
        string moduleId,
        ModuleAccent accent,
        float cx,
        float cy,
        float size,
        bool isDark,
        float glowT)
    {
        glowT = Math.Clamp(glowT, 0f, 1f);
        if (glowT <= 0.01f)
        {
            return;
        }

        (SKColor outerColor, SKColor midColor, SKColor coreColor) = ResolveNeonColors(moduleId, accent, isDark);

        float scale = size / 24f;
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);
        canvas.Translate(-12f, -12f);

        // Outer wide phosphor wash
        DrawBlurredIconPass(canvas, moduleId, outerColor.WithAlpha((byte)Math.Round(outerColor.Alpha * glowT)), 3.8f, 8.2f / MathF.Max(1f, scale * 0.48f));
        // Mid saturated neon tube
        DrawBlurredIconPass(canvas, moduleId, midColor.WithAlpha((byte)Math.Round(midColor.Alpha * glowT)), 2.6f, 3.8f / MathF.Max(1f, scale * 0.58f));
        // Inner luminous lamp core (still softly diffused under glass, no hard crisp vector outline)
        DrawBlurredIconPass(canvas, moduleId, coreColor.WithAlpha((byte)Math.Round(coreColor.Alpha * glowT)), 1.6f, 1.5f / MathF.Max(1f, scale * 0.70f));

        canvas.Restore();
    }

    private static void DrawBlurredIconPass(SKCanvas canvas, string moduleId, SKColor color, float strokeWidth, float blurSigma)
    {
        if (color.Alpha == 0)
        {
            return;
        }

        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.8f, blurSigma));
        DrawIconGeometry(canvas, moduleId, color, strokeWidth, blur);
    }

    private static (SKColor Outer, SKColor Mid, SKColor Core) ResolveNeonColors(string moduleId, ModuleAccent accent, bool isDark)
    {
        if (moduleId == ModuleIds.Back)
        {
            return isDark
                ? (new SKColor(150, 204, 255, 130), new SKColor(198, 228, 255, 185), new SKColor(246, 252, 255, 220))
                : (new SKColor(48, 128, 214, 155), new SKColor(92, 168, 240, 205), new SKColor(228, 244, 255, 232));
        }

        return accent switch
        {
            ModuleAccent.Lime => isDark
                ? (new SKColor(102, 245, 62, 155), new SKColor(156, 255, 108, 215), new SKColor(232, 255, 214, 240))
                : (new SKColor(38, 196, 16, 175), new SKColor(84, 228, 42, 225), new SKColor(225, 255, 200, 238)),
            ModuleAccent.Blue => isDark
                ? (new SKColor(58, 188, 255, 160), new SKColor(126, 220, 255, 215), new SKColor(225, 248, 255, 240))
                : (new SKColor(14, 140, 240, 175), new SKColor(52, 182, 255, 225), new SKColor(218, 246, 255, 238)),
            _ => isDark
                ? (new SKColor(138, 204, 255, 148), new SKColor(196, 232, 255, 205), new SKColor(246, 252, 255, 235))
                : (new SKColor(28, 128, 225, 165), new SKColor(82, 172, 248, 215), new SKColor(228, 246, 255, 235))
        };
    }

    private static void DrawIconGeometry(SKCanvas canvas, string moduleId, SKColor color, float strokeWidth, SKMaskFilter? maskFilter = null)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color,
            MaskFilter = maskFilter
        };

        switch (moduleId)
        {
            case ModuleIds.FileManager:
                // Folder + bidirectional transfer arrows
                canvas.DrawLine(3, 7, 3, 19, paint);
                canvas.DrawLine(3, 19, 14, 19, paint);
                canvas.DrawLine(3, 7, 9, 7, paint);
                canvas.DrawLine(9, 7, 11, 9, paint);
                canvas.DrawLine(11, 9, 20, 9, paint);
                canvas.DrawLine(20, 9, 20, 12.5f, paint);
                canvas.DrawLine(15, 15.5f, 21, 15.5f, paint);
                canvas.DrawLine(18.5f, 13, 21, 15.5f, paint);
                canvas.DrawLine(18.5f, 18, 21, 15.5f, paint);
                canvas.DrawLine(14, 20.5f, 20, 20.5f, paint);
                canvas.DrawLine(16.5f, 18.5f, 14, 20.5f, paint);
                break;

            case ModuleIds.Converter:
                // Audio spectrum waveform
                canvas.DrawLine(3, 12, 5.5f, 12, paint);
                canvas.DrawLine(8, 7, 8, 17, paint);
                canvas.DrawLine(11.5f, 4, 11.5f, 20, paint);
                canvas.DrawLine(15, 8.5f, 15, 15.5f, paint);
                canvas.DrawLine(18.5f, 6, 18.5f, 18, paint);
                canvas.DrawLine(21, 12, 22, 12, paint);
                break;

            case ModuleIds.Customization:
                // Sliders horizontal
                canvas.DrawLine(4, 7, 20, 7, paint);
                canvas.DrawCircle(10, 7, 2, paint);
                canvas.DrawLine(4, 12, 20, 12, paint);
                canvas.DrawCircle(15, 12, 2, paint);
                canvas.DrawLine(4, 17, 20, 17, paint);
                canvas.DrawCircle(8, 17, 2, paint);
                break;

            case ModuleIds.PairSync:
                // USB trident plug
                canvas.DrawLine(12, 4, 12, 20, paint);
                canvas.DrawLine(10, 6.5f, 12, 4, paint);
                canvas.DrawLine(14, 6.5f, 12, 4, paint);
                canvas.DrawLine(12, 15, 7, 11, paint);
                canvas.DrawCircle(7, 10, 1.5f, paint);
                canvas.DrawLine(12, 13, 17, 9.5f, paint);
                canvas.DrawRect(16, 7.5f, 2.5f, 2.5f, paint);
                canvas.DrawCircle(12, 20.2f, 1.8f, paint);
                break;

            case ModuleIds.Library:
                // Beamed music notes
                canvas.DrawLine(9, 18, 9, 6, paint);
                canvas.DrawLine(9, 6, 19, 4.5f, paint);
                canvas.DrawLine(19, 4.5f, 19, 16, paint);
                canvas.DrawCircle(6.8f, 18, 2.3f, paint);
                canvas.DrawCircle(16.8f, 16, 2.3f, paint);
                break;

            case ModuleIds.CustomOled:
                // OLED monitor display + wave
                canvas.DrawRoundRect(new SKRect(3, 4.5f, 21, 16.5f), 2f, 2f, paint);
                canvas.DrawLine(8, 20, 16, 20, paint);
                canvas.DrawLine(12, 16.5f, 12, 20, paint);
                canvas.DrawLine(6.5f, 12, 9.5f, 9, paint);
                canvas.DrawLine(9.5f, 9, 13, 13, paint);
                canvas.DrawLine(13, 13, 17.5f, 8, paint);
                break;

            case ModuleIds.CustomSubtitles:
                // Captions box
                canvas.DrawRoundRect(new SKRect(3, 5.5f, 21, 18.5f), 2.5f, 2.5f, paint);
                canvas.DrawLine(7, 10.5f, 11, 10.5f, paint);
                canvas.DrawLine(13.5f, 10.5f, 17, 10.5f, paint);
                canvas.DrawLine(7, 14.2f, 13.5f, 14.2f, paint);
                canvas.DrawLine(15.5f, 14.2f, 17, 14.2f, paint);
                break;

            case ModuleIds.CustomTags:
                // Luggage/ID3 tag
                canvas.DrawLine(4, 12, 12, 4, paint);
                canvas.DrawLine(12, 4, 20, 4, paint);
                canvas.DrawLine(20, 4, 20, 12, paint);
                canvas.DrawLine(20, 12, 12, 20, paint);
                canvas.DrawLine(12, 20, 4, 12, paint);
                canvas.DrawCircle(15.5f, 8.5f, 1.3f, paint);
                break;

            case ModuleIds.CustomFirmware:
                // MCU Chip
                canvas.DrawRoundRect(new SKRect(6, 6, 18, 18), 2f, 2f, paint);
                canvas.DrawRoundRect(new SKRect(9.5f, 9.5f, 14.5f, 14.5f), 0.8f, 0.8f, paint);
                canvas.DrawLine(9.5f, 3, 9.5f, 6, paint);
                canvas.DrawLine(14.5f, 3, 14.5f, 6, paint);
                canvas.DrawLine(9.5f, 18, 9.5f, 21, paint);
                canvas.DrawLine(14.5f, 18, 14.5f, 21, paint);
                canvas.DrawLine(3, 9.5f, 6, 9.5f, paint);
                canvas.DrawLine(3, 14.5f, 6, 14.5f, paint);
                canvas.DrawLine(18, 9.5f, 21, 9.5f, paint);
                canvas.DrawLine(18, 14.5f, 21, 14.5f, paint);
                break;

            case ModuleIds.DeviceSystem:
                // Hardware Walkman chassis & wrench node
                canvas.DrawRoundRect(new SKRect(5.5f, 3.5f, 18.5f, 20.5f), 3f, 3f, paint);
                canvas.DrawRoundRect(new SKRect(8f, 6.2f, 16f, 11.2f), 1f, 1f, paint);
                canvas.DrawCircle(12f, 15.8f, 2.3f, paint);
                break;

            case ModuleIds.DeviceDumpWizard:
                // Diagnostic telemetry archive package
                canvas.DrawRoundRect(new SKRect(4, 5.5f, 20, 9.5f), 1.2f, 1.2f, paint);
                canvas.DrawLine(5.5f, 9.5f, 5.5f, 18.5f, paint);
                canvas.DrawLine(18.5f, 9.5f, 18.5f, 18.5f, paint);
                canvas.DrawLine(5.5f, 18.5f, 18.5f, 18.5f, paint);
                canvas.DrawLine(9.5f, 13.5f, 14.5f, 13.5f, paint);
                break;

            case ModuleIds.DeviceTechInfo:
                // Telemetry info circle
                canvas.DrawCircle(12, 12, 8.2f, paint);
                canvas.DrawLine(12, 10.5f, 12, 16.2f, paint);
                canvas.DrawCircle(12, 7.6f, 0.7f, paint);
                break;

            case ModuleIds.Back:
                // Undo curved arrow
                canvas.DrawLine(6, 9, 15, 9, paint);
                canvas.DrawArc(new SKRect(10, 9, 20, 18), -90, 180, false, paint);
                canvas.DrawLine(8, 18, 15, 18, paint);
                canvas.DrawLine(9.5f, 5.5f, 6, 9, paint);
                canvas.DrawLine(9.5f, 12.5f, 6, 9, paint);
                break;

            default:
                canvas.DrawCircle(12, 12, 6, paint);
                break;
        }
    }

    public static SKColor GetAccentIconColor(string moduleId, ModuleAccent accent, bool isDark, bool isActive)
    {
        if (moduleId == ModuleIds.Back)
        {
            return isDark ? new SKColor(226, 236, 246) : new SKColor(52, 68, 86);
        }

        if (isActive || accent == ModuleAccent.Lime)
        {
            return isDark ? new SKColor(186, 248, 134) : new SKColor(56, 122, 18);
        }

        if (accent == ModuleAccent.Blue)
        {
            return isDark ? new SKColor(148, 216, 255) : new SKColor(18, 102, 168);
        }

        return isDark ? new SKColor(216, 228, 242) : new SKColor(58, 74, 92);
    }

    private static SKColor[] ResolveSectorGradient(
        ModuleAccent accent,
        bool isDark,
        bool isActive,
        float hoverT,
        byte alphaScale,
        float glassOpacityBoost)
    {
        float boost = MathF.Max(1.0f + 0.16f * hoverT, isActive ? 1.12f : 1.0f) * glassOpacityBoost;
        byte A(int val) => (byte)Math.Clamp((int)Math.Round(val * (alphaScale / 255f) * boost), 0, 255);

        if (isDark)
        {
            if (isActive || accent == ModuleAccent.Lime)
            {
                return
                [
                    new SKColor(196, 248, 150, A(82)),
                    new SKColor(46, 76, 56, A(142)),
                    new SKColor(34, 58, 44, A(152)),
                    new SKColor(148, 232, 86, A(64))
                ];
            }

            if (accent == ModuleAccent.Blue)
            {
                return
                [
                    new SKColor(168, 224, 255, A(78)),
                    new SKColor(36, 58, 84, A(146)),
                    new SKColor(28, 44, 66, A(156)),
                    new SKColor(110, 196, 255, A(62))
                ];
            }

            return
            [
                new SKColor(235, 244, 255, A(62)),
                new SKColor(44, 54, 68, A(144)),
                new SKColor(34, 42, 54, A(152)),
                new SKColor(195, 216, 240, A(52))
            ];
        }

        if (isActive || accent == ModuleAccent.Lime)
        {
            return
            [
                new SKColor(255, 255, 255, A(214)),
                new SKColor(220, 252, 206, A(158)),
                new SKColor(168, 240, 118, A(128)),
                new SKColor(255, 255, 255, A(186))
            ];
        }

        if (accent == ModuleAccent.Blue)
        {
            return
            [
                new SKColor(255, 255, 255, A(214)),
                new SKColor(210, 240, 255, A(158)),
                new SKColor(138, 212, 255, A(122)),
                new SKColor(255, 255, 255, A(184))
            ];
        }

        return
        [
            new SKColor(255, 255, 255, A(218)),
            new SKColor(236, 246, 255, A(152)),
            new SKColor(210, 230, 246, A(136)),
            new SKColor(255, 255, 255, A(188))
        ];
    }

    private static SKPoint Polar(double cx, double cy, double radius, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new SKPoint(
            (float)(cx + radius * Math.Cos(rad)),
            (float)(cy + radius * Math.Sin(rad)));
    }

    /// <summary>
    /// Applies fast deterministic triangular-PDF dithering to an 8-bit BGRA8888 bitmap to eliminate gradient banding.
    /// </summary>
    public static unsafe void ApplyTriangularDither(SKBitmap bmp, float amplitude = 1.15f)
    {
        if (bmp.ColorType != SKColorType.Bgra8888 || bmp.Width <= 0 || bmp.Height <= 0)
        {
            return;
        }

        IntPtr pixelsPtr = bmp.GetPixels();
        if (pixelsPtr == IntPtr.Zero)
        {
            return;
        }

        int width = bmp.Width;
        int height = bmp.Height;
        int rowBytes = bmp.RowBytes;
        byte* basePtr = (byte*)pixelsPtr;

        for (int y = 0; y < height; y++)
        {
            byte* row = basePtr + y * rowBytes;
            uint yHash = (uint)(y * 73856093);
            for (int x = 0; x < width; x++)
            {
                int idx = x * 4;
                byte a = row[idx + 3];
                if (a == 0)
                {
                    continue;
                }

                uint h1 = (uint)(x * 19349663) ^ yHash;
                h1 ^= h1 >> 13;
                h1 *= 0x5bd1e995u;
                h1 ^= h1 >> 15;

                float u1 = (h1 & 0xFF) * (1f / 255f);
                float u2 = ((h1 >> 8) & 0xFF) * (1f / 255f);
                float noise = (u1 + u2 - 1f) * amplitude;

                int b = (int)MathF.Round(row[idx] + noise);
                int g = (int)MathF.Round(row[idx + 1] + noise);
                int r = (int)MathF.Round(row[idx + 2] + noise);

                row[idx] = (byte)Math.Clamp(b, 0, a);
                row[idx + 1] = (byte)Math.Clamp(g, 0, a);
                row[idx + 2] = (byte)Math.Clamp(r, 0, a);
            }
        }

        GC.KeepAlive(bmp);
    }

    internal sealed class RefCountedSkImage
    {
        private SKImage? _image;
        private int _refCount = 1;

        public RefCountedSkImage(SKImage image) => _image = image;

        public SKImage? TryAcquire()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return _image;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                Interlocked.Exchange(ref _image, null)?.Dispose();
            }
        }
    }

    internal sealed class RefCountedSectorBatch
    {
        private IReadOnlyList<CachedSectorItem>? _items;
        private int _refCount = 1;

        public RefCountedSectorBatch(IReadOnlyList<CachedSectorItem> items) => _items = items;

        public IReadOnlyList<CachedSectorItem>? TryAcquire()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return _items;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                var items = Interlocked.Exchange(ref _items, null);
                if (items is not null)
                {
                    foreach (var item in items)
                    {
                        item.GlassImage?.Dispose();
                        item.NeonGlowImage?.Dispose();
                        item.LabelImage?.Dispose();
                        item.Path.Dispose();
                    }
                }
            }
        }
    }

    internal sealed record CachedSectorItem(
        ComputedRingSector Sector,
        SKPath Path,
        SKImage? GlassImage,
        float OriginX,
        float OriginY,
        float LogicalWidth,
        float LogicalHeight,
        SKImage? NeonGlowImage,
        float NeonOriginX,
        float NeonOriginY,
        SKImage? LabelImage,
        float LabelOriginX,
        float LabelOriginY,
        float LabelLogicalW,
        float LabelLogicalH);

    public static (float Tx, float Ty) ComputePopOffset(ComputedRingSector sector, float hoverT, bool isActive)
    {
        hoverT = Math.Clamp(hoverT, 0f, 1f);
        double rad = sector.MidAngleDeg * Math.PI / 180.0;
        double popDist = isActive ? Math.Max(4.0, 9.0 * hoverT) : 9.0 * hoverT;
        float tx = (float)(Math.Cos(rad) * popDist);
        float ty = (float)(Math.Sin(rad) * popDist);
        if (hoverT <= 0.005f || hoverT >= 0.995f)
        {
            tx = MathF.Round(tx);
            ty = MathF.Round(ty);
        }

        return (tx, ty);
    }

    public static IReadOnlyList<CachedSectorItem> BakeSectorCache(
        IReadOnlyList<ComputedRingSector> sectors,
        double cx,
        double cy,
        bool isDark,
        Func<ModuleDescriptor, bool>? isActiveSelector = null,
        float glassOpacityBoost = 1f,
        bool bakeRotatedLabel = false,
        bool bakeHorizontalLabel = false)
    {
        var list = new List<CachedSectorItem>(sectors.Count);
        const float margin = 24f;
        const float ss = 2f; // 2x supersampling for razor-sharp text and smooth glass edges

        foreach (var sec in sectors)
        {
            var path = BuildRoundedSectorPath(
                cx,
                cy,
                sec.InnerRadius,
                sec.OuterRadius,
                sec.StartAngleDeg,
                sec.EndAngleDeg,
                sec.CornerRadius);

            var bounds = path.Bounds;
            float origX = MathF.Floor(bounds.Left - margin);
            float origY = MathF.Floor(bounds.Top - margin);
            float logicalW = MathF.Max(4f, MathF.Ceiling(bounds.Width + margin * 2f));
            float logicalH = MathF.Max(4f, MathF.Ceiling(bounds.Height + margin * 2f));
            int pxW = (int)(logicalW * ss);
            int pxH = (int)(logicalH * ss);

            bool isActive = isActiveSelector?.Invoke(sec.Module) ?? false;
            SKImage? image = null;
            using (var bmp = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Scale(ss);
                    canvas.Translate(-origX, -origY);
                    DrawOpticalGlassSector(
                        canvas,
                        sec,
                        cx,
                        cy,
                        isDark,
                        hoverT: 0f,
                        isActive: isActive,
                        opacity: 1f,
                        glassOpacityBoost: glassOpacityBoost,
                        applyPopOffset: false);
                }

                ApplyTriangularDither(bmp, 0.95f);
                image = SKImage.FromBitmap(bmp);
            }

            // Separate 2x supersampled label image so it always renders ON TOP of neon hover glow with zero per-frame CPU text shaping
            SKImage? labelImage = null;
            float labelOrigX = 0f;
            float labelOrigY = 0f;
            float labelLogW = 0f;
            float labelLogH = 0f;
            if (bakeRotatedLabel || bakeHorizontalLabel)
            {
                labelLogW = logicalW;
                labelLogH = logicalH;
                labelOrigX = origX;
                labelOrigY = origY;
                using var lblBmp = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var lCanvas = new SKCanvas(lblBmp))
                {
                    lCanvas.Clear(SKColors.Transparent);
                    lCanvas.Scale(ss);
                    lCanvas.Translate(-labelOrigX, -labelOrigY);

                    if (bakeRotatedLabel)
                    {
                        float textSize = sec.Module.IsLargeIcon ? 16.5f : 15.5f;
                        lCanvas.Translate((float)sec.LabelX, (float)sec.LabelY);
                        lCanvas.RotateDegrees((float)sec.MidAngleDeg);

                        using (var haloPaint = new SKPaint
                        {
                            IsAntialias = true,
                            TextSize = textSize,
                            TextAlign = SKTextAlign.Center,
                            Typeface = DisplayBold,
                            SubpixelText = true,
                            HintingLevel = SKPaintHinting.NoHinting,
                            Color = isDark ? new SKColor(8, 14, 22, 160) : new SKColor(255, 255, 255, 205)
                        })
                        {
                            lCanvas.DrawText(sec.Module.ShortTitle, 0f, 6.2f, haloPaint);
                        }

                        using (var labelPaint = new SKPaint
                        {
                            IsAntialias = true,
                            TextSize = textSize,
                            TextAlign = SKTextAlign.Center,
                            Typeface = DisplayBold,
                            SubpixelText = true,
                            HintingLevel = SKPaintHinting.NoHinting,
                            Color = isDark ? new SKColor(244, 248, 252, 255) : new SKColor(15, 23, 42, 255)
                        })
                        {
                            lCanvas.DrawText(sec.Module.ShortTitle, 0f, 5.2f, labelPaint);
                        }
                    }
                    else
                    {
                        float lx = (float)sec.LabelX;
                        float ly = (float)sec.LabelY;
                        float textSize = sec.Module.IsLargeIcon ? 16.5f : 15f;

                        using (var titleHalo = new SKPaint
                        {
                            IsAntialias = true,
                            TextSize = textSize,
                            TextAlign = SKTextAlign.Center,
                            Typeface = DisplayBold,
                            SubpixelText = true,
                            HintingLevel = SKPaintHinting.NoHinting,
                            Color = isDark ? new SKColor(8, 14, 22, 150) : new SKColor(255, 255, 255, 195)
                        })
                        {
                            lCanvas.DrawText(sec.Module.Title, lx, ly + 3f, titleHalo);
                        }

                        using (var titlePaint = new SKPaint
                        {
                            IsAntialias = true,
                            TextSize = textSize,
                            TextAlign = SKTextAlign.Center,
                            Typeface = DisplayBold,
                            SubpixelText = true,
                            HintingLevel = SKPaintHinting.NoHinting,
                            Color = isDark ? new SKColor(244, 248, 252, 255) : new SKColor(15, 23, 42, 255)
                        })
                        {
                            lCanvas.DrawText(sec.Module.Title, lx, ly + 2f, titlePaint);
                        }

                        using (var subPaint = new SKPaint
                        {
                            IsAntialias = true,
                            TextSize = 12.5f,
                            TextAlign = SKTextAlign.Center,
                            Typeface = InterSemiBold,
                            SubpixelText = true,
                            HintingLevel = SKPaintHinting.NoHinting,
                            Color = isDark ? new SKColor(203, 213, 225, 230) : new SKColor(58, 72, 92, 240)
                        })
                        {
                            lCanvas.DrawText(sec.Module.Subtitle, lx, ly + 21f, subPaint);
                        }
                    }
                }

                labelImage = SKImage.FromBitmap(lblBmp);
            }

            // Pre-bake 0-degree neon-under-glass hover icon so hovering runs zero live blur passes
            SKImage? neonImage = null;
            float iconSize = sec.Module.IsLargeIcon ? 62f : 52f;
            int neonW = Math.Max(4, (int)MathF.Ceiling(bounds.Width + 16f));
            int neonH = Math.Max(4, (int)MathF.Ceiling(bounds.Height + 16f));
            float neonOrigX = MathF.Floor(bounds.Left - 8f);
            float neonOrigY = MathF.Floor(bounds.Top - 8f);
            using (var neonSurface = SKSurface.Create(new SKImageInfo(neonW, neonH, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                if (neonSurface is not null)
                {
                    var nCanvas = neonSurface.Canvas;
                    nCanvas.Clear(SKColors.Transparent);
                    nCanvas.Translate(-neonOrigX, -neonOrigY);
                    nCanvas.ClipPath(path, SKClipOperation.Intersect, true);
                    DrawNeonUnderGlassIcon(
                        nCanvas,
                        sec.Module.Id,
                        sec.Module.Accent,
                        (float)sec.LabelX,
                        (float)sec.LabelY - (bakeRotatedLabel ? 0f : 2f),
                        iconSize,
                        isDark,
                        glowT: 1f);
                    neonImage = neonSurface.Snapshot();
                }
            }

            list.Add(new CachedSectorItem(sec, path, image, origX, origY, logicalW, logicalH, neonImage, neonOrigX, neonOrigY, labelImage, labelOrigX, labelOrigY, labelLogW, labelLogH));
        }

        return list;
    }

    public static void DrawCachedGlassSector(
        SKCanvas canvas,
        CachedSectorItem cached,
        float hoverT,
        bool isActive,
        bool isDark,
        float opacity)
    {
        if (opacity <= 0.01f)
        {
            return;
        }

        hoverT = Math.Clamp(hoverT, 0f, 1f);
        var (tx, ty) = ComputePopOffset(cached.Sector, hoverT, isActive);

        canvas.Save();
        canvas.Translate(tx, ty);

        if (cached.GlassImage is not null)
        {
            using var imgPaint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.Medium,
                Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * Math.Clamp(opacity, 0f, 1f)))
            };
            var destRect = new SKRect(cached.OriginX, cached.OriginY, cached.OriginX + cached.LogicalWidth, cached.OriginY + cached.LogicalHeight);
            canvas.DrawImage(cached.GlassImage, destRect, imgPaint);
        }

        if (isActive)
        {
            using var activeFillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = isDark
                    ? new SKColor(138, 230, 82, (byte)Math.Round(24 * opacity))
                    : new SKColor(168, 240, 118, (byte)Math.Round(34 * opacity))
            };
            canvas.DrawPath(cached.Path, activeFillPaint);
        }

        if (hoverT > 0.01f || isActive)
        {
            float rimBoost = MathF.Max(hoverT, isActive ? 0.65f : 0f) * opacity;
            SKColor rimCol = cached.Sector.Module.Accent switch
            {
                ModuleAccent.Lime => isDark ? new SKColor(156, 255, 92, (byte)Math.Round(185 * rimBoost)) : new SKColor(112, 224, 52, (byte)Math.Round(200 * rimBoost)),
                ModuleAccent.Blue => isDark ? new SKColor(116, 208, 255, (byte)Math.Round(185 * rimBoost)) : new SKColor(72, 178, 250, (byte)Math.Round(200 * rimBoost)),
                _ => isDark ? new SKColor(220, 236, 255, (byte)Math.Round(150 * rimBoost)) : new SKColor(160, 212, 248, (byte)Math.Round(180 * rimBoost))
            };

            using var rimPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f * MathF.Max(0.4f, rimBoost),
                Color = rimCol
            };
            canvas.DrawPath(cached.Path, rimPaint);
        }

        canvas.Restore();
    }
}
