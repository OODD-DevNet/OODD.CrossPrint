using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CrossPrint.Core;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// 本层自带的 PDF 字节取证（照 <c>Engine.Tests/Forensics.cs</c> 的思路重写，不跨测试工程引用）。
/// 只依赖 MediaBox / 页树 / 解压后的内容流，不引入 PDF 解析库——产物字节才是唯一可信的证据。
/// </summary>
internal static class Pdf
{
    /// <summary>文件头是不是 <c>%PDF-</c> 真签名。</summary>
    public static bool HasSignature(byte[] pdf)
        => pdf.Length >= 5 && Encoding.Latin1.GetString(pdf, 0, 5) == "%PDF-";

    /// <summary>页树里的最大 <c>/Count</c>，即页面总数。</summary>
    public static int PageCount(byte[] pdf)
    {
        var counts = Regex.Matches(Latin(pdf), @"/Count\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return counts.Count == 0 ? 0 : counts.Max();
    }

    /// <summary>某个标记在文件里出现几次。<c>/FontFile2</c> 计数就是"字库嵌了几份"的直接证据。</summary>
    public static int Count(byte[] pdf, string token)
        => Regex.Matches(Latin(pdf), Regex.Escape(token)).Count;

    /// <summary>读出全部页面的 MediaBox（mm），用于验纸张尺寸没被打印层改写。</summary>
    public static List<(double W, double H)> MediaBoxesMm(byte[] pdf)
    {
        var result = new List<(double, double)>();
        foreach (Match m in Regex.Matches(Latin(pdf), @"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s*\]"))
        {
            var w = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            result.Add((w * 25.4 / 72, h * 25.4 / 72));
        }

        return result;
    }

    /// <summary>
    /// 每条文字内容流（一页一条）。判据：解压后同时出现 <c>BT</c>、<c>Tf</c>、<c>Tj</c>——
    /// 字体流/图片流的二进制里不会凑齐这三个算子。用它来证明"每页画的是各自的数据"：
    /// 绑定坏掉时 100 条内容流会全等，绑定正常时逐条不同。
    /// </summary>
    public static List<string> TextContentStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var streams = new List<string>();
        foreach (Match m in Regex.Matches(text, "stream\r?\n"))
        {
            var start = m.Index + m.Length;
            var endIdx = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (endIdx < 0)
                continue;

            var len = Math.Min(Encoding.Latin1.GetByteCount(text[start..endIdx]), pdf.Length - start);
            var raw = pdf[start..(start + len)];
            string body;
            try
            {
                // Skia 的流是 zlib 包着：去掉 2 字节头 + 4 字节校验和再 inflate
                var zipped = raw.Length > 8 ? raw[2..(raw.Length - 4)] : raw;
                using var z = new DeflateStream(new MemoryStream(zipped), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                body = Encoding.Latin1.GetString(o.ToArray());
            }
            catch (Exception)
            {
                body = Encoding.Latin1.GetString(raw);
            }

            if (body.Contains("BT", StringComparison.Ordinal)
                && body.Contains("Tf", StringComparison.Ordinal)
                && body.Contains("Tj", StringComparison.Ordinal))
                streams.Add(body);
        }

        return streams;
    }

    static string Latin(byte[] pdf) => Encoding.Latin1.GetString(pdf);
}

/// <summary>
/// 模板与元素构造、临时工作区、基准数据落盘。测试之间互不打扰的关键是
/// <see cref="Workspace"/>：每条测试一个 GUID 目录，Dispose 时整目录删掉，
/// 绝不在仓库里留产物，也不跟并行的 Designer 测试抢固定路径。
/// </summary>
internal static class Fixture
{
    /// <summary>NFR-01 用西文字体：雅黑的整份 CJK 字库会把耗时变成字体大小的函数。</summary>
    public const string LatinFont = "Arial";

    /// <summary>中日韩归因用默认字体族，与 <see cref="TextOptions.FontFamily"/> 的默认值一致。</summary>
    public const string CjkFont = "Microsoft YaHei";

    static int _seq;

    /// <summary>自定义纸张模板（面单 100×150、A4 210×297 都走这里）。</summary>
    public static PrintTemplate Page(double widthMm, double heightMm)
        => new()
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = widthMm, HeightMm = heightMm },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };

    /// <summary>文本元素。id 自动递增，免得同一模板里撞名。</summary>
    public static TextElement Text(string content, double x, double y, double w, double h, string font = LatinFont)
        => new()
        {
            Id = $"t{Interlocked.Increment(ref _seq)}",
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Text = new TextOptions { Content = content, FontFamily = font, FontSizePt = 12 },
        };

    /// <summary>单号面单：一个 <c>{{orderNo}}</c> 占位符，数据来自每条记录自己。</summary>
    public static PrintTemplate OrderLabel(string font = LatinFont)
    {
        var template = Page(100, 150);
        template.Elements.Add(Text("{{orderNo}}", 8, 8, 80, 12, font));
        return template;
    }

    /// <summary>N 条订单数据 → N 个文档（同一模板实例，逐条换数据）。</summary>
    public static List<PrintDocument> Orders(int count, string font = LatinFont)
    {
        var template = OrderLabel(font);
        var docs = new List<PrintDocument>(count);
        for (var i = 1; i <= count; i++)
            docs.Add(new PrintDocument(template, new { orderNo = font == CjkFont ? $"订单-{i:D6}" : $"SO-{i:D6}" }));

        return docs;
    }

    /// <summary>本测试程序集是否以开启 JIT 优化的方式构建——决定性能预算用哪条线。</summary>
    public static bool IsOptimized
        => typeof(Fixture).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>() is { } attr && !attr.IsJITOptimizerDisabled;

    /// <summary>基准数据落到临时目录的 bench.log，便于把实测数字写进文档而不是凭记忆编。</summary>
    /// <param name="line">一行结果。</param>
    public static void Bench(string line)
    {
        var dir = Path.Combine(Path.GetTempPath(), "crossprint-printing-tests");
        Directory.CreateDirectory(dir);
        var tag = IsOptimized ? "Release" : "Debug";
        File.AppendAllText(Path.Combine(dir, "bench.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{tag}] {line}{Environment.NewLine}");
    }
}

/// <summary>每条测试独占的临时工作区。</summary>
internal sealed class Workspace : IDisposable
{
    /// <summary>工作区根目录。</summary>
    public string Root { get; }

    public Workspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "crossprint-printing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>工作区里某个文件的完整路径（父目录已存在）。</summary>
    public string PathOf(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    /// <summary>整目录删除。删不掉只是脏临时目录，不该让测试变红。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>临时文件残留检查：打印层的临时名一律带 <c>.crossprint-</c> 尾巴。</summary>
internal static class TempFiles
{
    /// <summary>目录里所有没清干净的临时名（含子目录）。</summary>
    public static string[] Leftovers(string root)
        => Directory.Exists(root)
            ? Directory.GetFiles(root, "*crossprint*", SearchOption.AllDirectories)
            : Array.Empty<string>();
}
