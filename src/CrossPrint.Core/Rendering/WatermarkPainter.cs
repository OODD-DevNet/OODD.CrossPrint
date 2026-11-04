using SkiaSharp;

namespace CrossPrint.Core.Rendering;

/// <summary>
/// 文字水印平铺绘制：设计器画布（宿主 px 空间）与引擎打印/导出（mm 空间）共用，
/// 调用方传入当前坐标系的 <c>pxPerMm</c>（mm 空间传 1）即可，字号按 pt→mm→px 换算。
/// </summary>
public static class WatermarkPainter
{
    static readonly object TypefaceLock = new();
    static SKTypeface? _typeface;

    /// <summary>
    /// 平铺绘制水印。画布需已处于目标坐标系；是否裁剪到页面由调用方决定（打印不裁、设计器裁）。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="options">水印配置。</param>
    /// <param name="pageWidthMm">页面宽（mm），用于铺满整页。</param>
    /// <param name="pageHeightMm">页面高（mm）。</param>
    /// <param name="pxPerMm">当前坐标系下每毫米的像素数（mm 空间传 1）。</param>
    public static void Draw(
        SKCanvas canvas,
        WatermarkOptions options,
        double pageWidthMm,
        double pageHeightMm,
        double pxPerMm)
    {
        if (canvas is null || options is null || !options.Enabled || pageWidthMm <= 0 || pageHeightMm <= 0)
            return;

        var text = options.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var opacity = !double.IsNaN(options.Opacity) && !double.IsInfinity(options.Opacity)
            ? Clamp(options.Opacity, 0.0, 1.0)
            : 0.1;
        if (opacity <= 0)
            return;

        var fontSizePt = !double.IsNaN(options.FontSizePt) && !double.IsInfinity(options.FontSizePt) && options.FontSizePt > 0
            ? Math.Min(options.FontSizePt, 400)
            : 14;
        var rotation = Clamp(
            !double.IsNaN(options.RotationDeg) && !double.IsInfinity(options.RotationDeg) ? options.RotationDeg : -30,
            -90, 90);
        var spacing = Clamp(
            !double.IsNaN(options.SpacingMm) && !double.IsInfinity(options.SpacingMm) ? options.SpacingMm : 60,
            4, 400) * pxPerMm;

        var textSize = (float)(fontSizePt / 72.0 * 25.4 * pxPerMm);
        if (textSize <= 0)
            return;

        var typeface = ResolveTypeface(text);
        using var paint = new SKPaint
        {
            Color = options.Color.WithAlpha((byte)Math.Round(opacity * 255)),
            IsAntialias = true,
            Typeface = typeface,
            TextSize = textSize,
        };

        var textWidth = paint.MeasureText(text);
        // 平铺网格：横向 = 文本宽 + 间距；纵向 = 字高×1.6 + 间距，保证斜纹错落有致。
        var stepX = Math.Max(textWidth + spacing, spacing + 1);
        var stepY = Math.Max(textSize * 1.6f + spacing, spacing + 1);
        var spanX = pageWidthMm * pxPerMm + stepX;
        var spanY = pageHeightMm * pxPerMm + stepY;

        var rowIndex = 0;
        for (var y = stepY / 2; y < spanY; y += stepY, rowIndex++)
        {
            // 奇数行横向错开半步，斜纹观感更自然。
            var offsetX = (rowIndex % 2 == 0) ? 0 : stepX / 2;
            for (var x = -textWidth + offsetX; x < spanX; x += stepX)
            {
                canvas.Save();
                canvas.Translate((float)x, (float)y);
                canvas.RotateDegrees((float)rotation);
                canvas.DrawText(text, 0, 0, paint);
                canvas.Restore();
            }
        }
    }

    /// <summary>net461 没有 <c>Math.Clamp</c>，本地实现等价夹取。</summary>
    static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    /// <summary>
    /// 解析水印字体：优先中文字体族（Windows/macOS/Linux 常见），失败按首字符匹配系统字体。
    /// 结果进程级缓存——水印每帧平铺几十次，字体解析不能每次都跑。
    /// </summary>
    static SKTypeface ResolveTypeface(string text)
    {
        lock (TypefaceLock)
        {
            if (_typeface is not null)
                return _typeface;

            foreach (var family in new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC", "WenQuanYi Micro Hei" })
            {
                try
                {
                    var candidate = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
                    if (candidate is not null && !candidate.FamilyName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        _typeface = candidate;
                        return _typeface;
                    }
                    candidate?.Dispose();
                }
                catch
                {
                    // 该族不可用就试下一个。
                }
            }

            try
            {
                foreach (var ch in text)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;
                    var candidate = SKFontManager.Default.MatchCharacter(ch);
                    if (candidate is not null)
                    {
                        _typeface = candidate;
                        return _typeface;
                    }
                }
            }
            catch
            {
                // 匹配失败回落默认。
            }

            _typeface = SKTypeface.Default;
            return _typeface;
        }
    }
}
