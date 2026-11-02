using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Tests;

/// <summary>栅格结果取证：墨迹包围盒、行剖面、行程长度。全部以 mm 报告，DPI 只用于换算。</summary>
internal static class Ink
{
    /// <summary>
    /// 毫米矩形。取证结果一律用 double 而不是 <see cref="SKRect"/>：
    /// FluentAssertions 的 <c>BeApproximately</c> 只挂在 double 断言上，float 主题会退化成可空重载并编译失败，
    /// 而"像素转毫米"本来就丢掉了 float 的意义。
    /// </summary>
    public readonly record struct RectMm(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;

        public double Height => Bottom - Top;
    }

    /// <summary>
    /// 把位图渲染结果转成"是否墨迹"的网格。默认阈值 128 只认深色；
    /// <paramref name="threshold"/> 调高就能把样张里的浅灰分隔线（#8899AA 亮度 150）这类浅色也算进来。
    /// </summary>
    public static bool[,] DarkMask(SKBitmap bitmap, byte threshold = 128)
    {
        var mask = new bool[bitmap.Width, bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                // 透明像素视作纸白；非透明按亮度判定
                mask[x, y] = c.Alpha > 16 && (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000 < threshold;
            }
        }

        return mask;
    }

    /// <summary>深色像素的包围盒（mm）。无墨迹返回 null。</summary>
    public static RectMm? BoundingBoxMm(SKBitmap bitmap, double dpi)
    {
        var mask = DarkMask(bitmap);
        int minX = -1, minY = -1, maxX = -1, maxY = -1;
        for (var y = 0; y < mask.GetLength(1); y++)
        {
            for (var x = 0; x < mask.GetLength(0); x++)
            {
                if (!mask[x, y])
                    continue;
                if (minX < 0 || x < minX) minX = x;
                if (maxX < 0 || x > maxX) maxX = x;
                if (minY < 0 || y < minY) minY = y;
                maxY = y;
            }
        }

        if (minX < 0)
            return null;

        return PxRectToMm(minX, minY, maxX, maxY, dpi);
    }

    /// <summary>取指定 mm 矩形窗口内的墨迹包围盒（mm），用于只看某个元素而忽略整页。</summary>
    /// <param name="bitmap">渲染结果。</param>
    /// <param name="dpi">渲染密度。</param>
    /// <param name="windowMm">只看这块毫米窗口。</param>
    /// <param name="threshold">亮度阈值；样张里的浅灰线要调到 200 才看得见。</param>
    public static RectMm? BoundingBoxInMm(SKBitmap bitmap, double dpi, SKRect windowMm, byte threshold = 128)
    {
        var mask = DarkMask(bitmap, threshold);
        var sx = (int)Math.Floor(windowMm.Left * dpi / 25.4);
        var ex = Math.Min(mask.GetLength(0) - 1, (int)Math.Ceiling(windowMm.Right * dpi / 25.4));
        var sy = (int)Math.Floor(windowMm.Top * dpi / 25.4);
        var ey = Math.Min(mask.GetLength(1) - 1, (int)Math.Ceiling(windowMm.Bottom * dpi / 25.4));

        int minX = -1, minY = -1, maxX = -1, maxY = -1;
        for (var y = Math.Max(0, sy); y <= ey; y++)
        {
            for (var x = Math.Max(0, sx); x <= ex; x++)
            {
                if (!mask[x, y])
                    continue;
                if (minX < 0 || x < minX) minX = x;
                if (maxX < 0 || x > maxX) maxX = x;
                if (minY < 0 || y < minY) minY = y;
                maxY = y;
            }
        }

        return minX < 0 ? null : PxRectToMm(minX, minY, maxX, maxY, dpi);
    }

    /// <summary>统计一条水平扫描线上的墨迹行程数（mm）。用于数虚线段、数横条。</summary>
    public static List<(double StartMm, double LengthMm)> DarkRunsOnRow(SKBitmap bitmap, double dpi, int yPixel)
    {
        var mask = DarkMask(bitmap);
        var runs = new List<(double, double)>();
        var start = -1;
        for (var x = 0; x < mask.GetLength(0); x++)
        {
            if (mask[x, yPixel])
            {
                if (start < 0)
                    start = x;
            }
            else if (start >= 0)
            {
                runs.Add((PxToMm(start, dpi), PxToMm(x - start, dpi)));
                start = -1;
            }
        }

        if (start >= 0)
            runs.Add((PxToMm(start, dpi), PxToMm(mask.GetLength(0) - start, dpi)));

        return runs;
    }

    /// <summary>统计一条垂直扫描线上的墨迹行程（mm）。用于量表格横线位置与间距。</summary>
    public static List<(double StartMm, double LengthMm)> DarkRunsInColumn(SKBitmap bitmap, double dpi, int xPixel)
    {
        var mask = DarkMask(bitmap);
        var runs = new List<(double, double)>();
        var start = -1;
        for (var y = 0; y < mask.GetLength(1); y++)
        {
            if (mask[xPixel, y])
            {
                if (start < 0)
                    start = y;
            }
            else if (start >= 0)
            {
                runs.Add((PxToMm(start, dpi), PxToMm(y - start, dpi)));
                start = -1;
            }
        }

        if (start >= 0)
            runs.Add((PxToMm(start, dpi), PxToMm(mask.GetLength(1) - start, dpi)));

        return runs;
    }

    /// <summary>整页里有多少个不连续的"有墨的行带"，用于数表格行数、数文本行数。</summary>
    public static int InkBandCount(SKBitmap bitmap, int minPixels = 1)
    {
        var mask = DarkMask(bitmap);
        var bands = 0;
        var inBand = false;
        var run = 0;
        for (var y = 0; y < mask.GetLength(1); y++)
        {
            var has = false;
            for (var x = 0; x < mask.GetLength(0); x++)
            {
                if (!mask[x, y])
                    continue;
                has = true;
                break;
            }

            if (has)
            {
                run++;
                inBand = true;
            }
            else if (inBand)
            {
                if (run >= minPixels)
                    bands++;
                inBand = false;
                run = 0;
            }
        }

        if (inBand && run >= minPixels)
            bands++;

        return bands;
    }

    /// <summary>取指定 mm 区域内的墨迹像素数。</summary>
    public static int DarkPixelCountInMm(SKBitmap bitmap, double dpi, SKRect windowMm)
    {
        var mask = DarkMask(bitmap);
        var sx = Math.Max(0, (int)Math.Floor(windowMm.Left * dpi / 25.4));
        var ex = Math.Min(mask.GetLength(0) - 1, (int)Math.Ceiling(windowMm.Right * dpi / 25.4));
        var sy = Math.Max(0, (int)Math.Floor(windowMm.Top * dpi / 25.4));
        var ey = Math.Min(mask.GetLength(1) - 1, (int)Math.Ceiling(windowMm.Bottom * dpi / 25.4));

        var n = 0;
        for (var y = sy; y <= ey; y++)
        {
            for (var x = sx; x <= ex; x++)
            {
                if (mask[x, y])
                    n++;
            }
        }

        return n;
    }

    /// <summary>指定 mm 区域内"非纸白"像素数。占位框那种浅灰（200）不算墨迹，但确实画了东西，用它取证。</summary>
    public static int NonWhitePixelCountInMm(SKBitmap bitmap, double dpi, SKRect windowMm, byte threshold = 245)
    {
        var sx = Math.Max(0, (int)Math.Floor(windowMm.Left * dpi / 25.4));
        var ex = Math.Min(bitmap.Width - 1, (int)Math.Ceiling(windowMm.Right * dpi / 25.4));
        var sy = Math.Max(0, (int)Math.Floor(windowMm.Top * dpi / 25.4));
        var ey = Math.Min(bitmap.Height - 1, (int)Math.Ceiling(windowMm.Bottom * dpi / 25.4));

        var n = 0;
        for (var y = sy; y <= ey; y++)
        {
            for (var x = sx; x <= ex; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < threshold || c.Green < threshold || c.Blue < threshold)
                    n++;
            }
        }

        return n;
    }

    /// <summary>指定 mm 区域内与给定颜色接近（各通道 ±tolerance）的像素数。用于取证图片实际落在哪块矩形上。</summary>
    public static int ColorCountInMm(SKBitmap bitmap, double dpi, SKRect windowMm, SKColor color, int tolerance = 12)
    {
        var sx = Math.Max(0, (int)Math.Floor(windowMm.Left * dpi / 25.4));
        var ex = Math.Min(bitmap.Width - 1, (int)Math.Ceiling(windowMm.Right * dpi / 25.4));
        var sy = Math.Max(0, (int)Math.Floor(windowMm.Top * dpi / 25.4));
        var ey = Math.Min(bitmap.Height - 1, (int)Math.Ceiling(windowMm.Bottom * dpi / 25.4));

        var n = 0;
        for (var y = sy; y <= ey; y++)
        {
            for (var x = sx; x <= ex; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.Red - color.Red) <= tolerance &&
                    Math.Abs(c.Green - color.Green) <= tolerance &&
                    Math.Abs(c.Blue - color.Blue) <= tolerance)
                    n++;
            }
        }

        return n;
    }

    public static double PxToMm(double px, double dpi) => px * 25.4 / dpi;

    public static int MmToPx(double mm, double dpi) => (int)Math.Round(mm * dpi / 25.4);

    static RectMm PxRectToMm(int minX, int minY, int maxX, int maxY, double dpi)
        => new(
            PxToMm(minX, dpi),
            PxToMm(minY, dpi),
            PxToMm(maxX + 1, dpi),
            PxToMm(maxY + 1, dpi));
}

/// <summary>PDF 取证：只依赖 MediaBox 文本与解压后的内容流，不引入 PDF 解析库。</summary>
internal static class PdfProbe
{
    /// <summary>读出全部页面的 MediaBox（mm）。找不到的页返回空集合。</summary>
    public static List<(double W, double H)> MediaBoxesMm(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<(double, double)>();
        foreach (Match m in Regex.Matches(text, @"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s*\]"))
        {
            var w = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            result.Add((w * 25.4 / 72, h * 25.4 / 72));
        }

        return result;
    }

    /// <summary>页面总数。优先读页树 <c>/Count</c>，取最大值以防中间节点各写一份。</summary>
    public static int PageCount(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var counts = Regex.Matches(text, @"/Count\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return counts.Count == 0 ? 0 : counts.Max();
    }

    /// <summary>拼接并解压全部流对象，便于检查 <c>BT/Tj/re f</c> 之类的内容流标记。</summary>
    public static string ContentStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(text, "stream\r?\n"))
        {
            var start = m.Index + m.Length;
            var endIdx = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (endIdx < 0)
                continue;

            var len = Math.Min(Encoding.Latin1.GetByteCount(text[start..endIdx]), pdf.Length - start);
            var raw = pdf[start..(start + len)];
            try
            {
                var body = raw.Length > 8 ? raw[2..(raw.Length - 4)] : raw;
                using var z = new DeflateStream(new MemoryStream(body), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                sb.Append(Encoding.Latin1.GetString(o.ToArray())).Append('\n');
            }
            catch (Exception)
            {
                sb.Append(Encoding.Latin1.GetString(raw)).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>字体对象里的键（判断是否内嵌字体文件）。</summary>
    public static bool Has(byte[] pdf, string token)
        => Encoding.Latin1.GetString(pdf).Contains(token, StringComparison.Ordinal);

    /// <summary>某个标记在文件里出现几次。用来验证"多页只嵌一份字体流"。</summary>
    public static int Count(byte[] pdf, string token)
        => Regex.Matches(Encoding.Latin1.GetString(pdf), Regex.Escape(token)).Count;
}
