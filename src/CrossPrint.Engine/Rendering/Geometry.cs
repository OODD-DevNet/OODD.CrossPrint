using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>描边/填充画刷的统一构造点。虚线段长按 mm 给出，由画布的 mm 缩放自动生效。</summary>
internal static class Paints
{
    /// <summary>创建描边画刷。</summary>
    /// <param name="color">线色。</param>
    /// <param name="widthMm">线宽（mm）。</param>
    /// <param name="dashMm">虚线段长循环（mm），null 或空为实线。</param>
    public static SKPaint Stroke(SKColor color, double widthMm, double[]? dashMm)
    {
        var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)widthMm,
            StrokeCap = SKStrokeCap.Butt,
            IsAntialias = true,
        };

        ApplyDash(paint, dashMm);
        return paint;
    }

    /// <summary>
    /// 创建填充画刷。
    /// <paramref name="antialias"/> 默认开；**码制必须传 false**——探针 §21 实测，
    /// 40mm 码在 300dpi 下开抗锯齿后模块边缘变灰，连 ZXing 自己的读码器都定位不到图形，
    /// 等于印出一张"看着像二维码但扫不出来"的面单；关掉抗锯齿同一张图立刻解回原内容。
    /// </summary>
    public static SKPaint Fill(SKColor color, bool antialias = true)
        => new() { Color = color, Style = SKPaintStyle.Fill, IsAntialias = antialias };

    /// <summary>
    /// 挂虚线效果。段长全为 0 时视为实线——Skia 会静默丢弃退化路径效果，
    /// 但用户写 [0,0] 是想看实线，不该拿到一条按内部默认值画的虚线。
    /// </summary>
    public static void ApplyDash(SKPaint paint, double[]? dashMm)
    {
        if (dashMm is null || dashMm.Length < 2 || dashMm.All(v => v <= 0))
            return;

        var intervals = new float[dashMm.Length];
        for (var i = 0; i < dashMm.Length; i++)
            intervals[i] = (float)Math.Max(dashMm[i], 0);

        // 奇数段会被 Skia 按"重复一遍"处理，这里显式补齐，避免不同平台口径不一致
        if (intervals.Length % 2 != 0)
            Array.Resize(ref intervals, intervals.Length * 2);

        paint.PathEffect = SKPathEffect.CreateDash(intervals, 0);
    }
}

/// <summary>mm 空间几何小工具。</summary>
internal static class Geometry
{
    /// <summary>元素包围盒。</summary>
    public static SKRect Bounds(PaperElement e)
        => new((float)e.XMm, (float)e.YMm, (float)e.RightMm, (float)e.BottomMm);

    /// <summary>
    /// 描边的中心线本就落在盒子边界上，视觉外沿会超出元素框半个线宽。
    /// 这里向内收半个线宽，让"元素框等于墨迹边界"，设计器里看到的边框和纸上量到的一致。
    /// </summary>
    public static SKRect InsetByStroke(SKRect rect, double strokeWidthMm, bool hasStroke)
    {
        if (!hasStroke || strokeWidthMm <= 0)
            return rect;

        var half = (float)(strokeWidthMm / 2);
        rect.Inflate(-half, -half);
        return rect;
    }

    /// <summary>等比适配：把源尺寸放进目标框，返回居中后的目标矩形。</summary>
    public static SKRect Fit(double srcW, double srcH, SKRect dst, ImageFitMode mode)
    {
        if (srcW <= 0 || srcH <= 0 || dst.Width <= 0 || dst.Height <= 0)
            return dst;

        switch (mode)
        {
            case ImageFitMode.Fill:
                return dst;
            case ImageFitMode.None:
                {
                    // 原始像素按 96dpi 折算成 mm 后居中，不缩放
                    var w = UnitConverter.PxToMm(srcW, 96);
                    var h = UnitConverter.PxToMm(srcH, 96);
                    var left = dst.MidX - w / 2;
                    var top = dst.MidY - h / 2;
                    return new SKRect((float)left, (float)top, (float)(left + w), (float)(top + h));
                }
            case ImageFitMode.Cover:
                {
                    var scale = Math.Max(dst.Width / srcW, dst.Height / srcH);
                    return Centered(dst, srcW * scale, srcH * scale);
                }
            default:
                {
                    var scale = Math.Min(dst.Width / srcW, dst.Height / srcH);
                    return Centered(dst, srcW * scale, srcH * scale);
                }
        }
    }

    static SKRect Centered(SKRect dst, double w, double h)
        => new((float)(dst.MidX - w / 2), (float)(dst.MidY - h / 2), (float)(dst.MidX + w / 2), (float)(dst.MidY + h / 2));

    /// <summary>矩形是否落在页面可印范围外（只用于告警，不改变绘制）。</summary>
    public static bool OverflowsPage(PaperElement e, double pageWidthMm, double pageHeightMm)
        => e.XMm < 0 || e.YMm < 0 || (pageWidthMm > 0 && e.RightMm > pageWidthMm + Tolerance) || (pageHeightMm > 0 && e.BottomMm > pageHeightMm + Tolerance);

    const double Tolerance = 0.05; // 半个 0.1mm 步进以内不算越界
}
