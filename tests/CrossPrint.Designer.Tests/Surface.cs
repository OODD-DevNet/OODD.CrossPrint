using SkiaSharp;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 叠加层测试的位面工具。全部是"量像素"的原语——叠加层没有模型可断言，
/// 它唯一的产出就是像素，所以只能用颜色去找它画在哪。
/// </summary>
internal static class Surface
{
    /// <summary>白底位面。</summary>
    internal static SKBitmap White(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        return bitmap;
    }

    /// <summary>取一个像素。</summary>
    internal static SKColor Pixel(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    /// <summary>以 (x,y) 为中心、边长 2r+1 的窗口里是否存在满足条件的像素。</summary>
    internal static bool AnyIn(SKBitmap bitmap, double x, double y, int radius, Func<SKColor, bool> match)
    {
        var cx = (int)Math.Round(x);
        var cy = (int)Math.Round(y);
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var px = cx + dx;
                var py = cy + dy;
                if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height)
                    continue;
                if (match(bitmap.GetPixel(px, py)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>满足条件的像素的外接框；一个都没有返回 null（正对照要单独断言它非空）。</summary>
    internal static SKRect? InkBox(SKBitmap bitmap, Func<SKColor, bool> match)
        => InkBox(bitmap, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), match);

    /// <summary>限定窗口内的外接框。</summary>
    internal static SKRect? InkBox(SKBitmap bitmap, SKRect window, Func<SKColor, bool> match)
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        var x0 = Math.Max(0, (int)Math.Floor(window.Left));
        var y0 = Math.Max(0, (int)Math.Floor(window.Top));
        var x1 = Math.Min(bitmap.Width - 1, (int)Math.Ceiling(window.Right));
        var y1 = Math.Min(bitmap.Height - 1, (int)Math.Ceiling(window.Bottom));

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!match(bitmap.GetPixel(x, y)))
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left ? null : new SKRect(left, top, right + 1, bottom + 1);
    }

    /// <summary>纯黑（引擎画的内容）。</summary>
    internal static bool IsDark(SKColor c) => c.Red < 64 && c.Green < 64 && c.Blue < 64;

    /// <summary>选中色/手柄描边（#1E88E5：红低蓝高）。</summary>
    internal static bool IsAccent(SKColor c) => c.Red < 150 && c.Blue > 160;

    /// <summary>非纸白（任何画了东西的像素，含浅灰网格与半透明幽灵）。</summary>
    internal static bool NotWhite(SKColor c) => c.Red < 250 || c.Green < 250 || c.Blue < 250;
}
