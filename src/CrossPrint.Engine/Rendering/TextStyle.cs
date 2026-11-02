using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 一份"字体 + 字号"的画字工具，文本/表格/一维码可读文本共用，保证三者度量口径一致。
/// 全程以 mm 作图：<see cref="SizeMm"/> 与 <see cref="SKPaint.TextSize"/> 都是毫米，
/// 因为 <see cref="TemplateRenderer"/> 已在画布入口施加了 <c>Scale(UnitsPerMm)</c>。
/// </summary>
internal sealed class TextStyle : IDisposable
{
    readonly RenderContext _ctx;

    /// <summary>创建样式。字体族缺失时按 <paramref name="probeText"/> 首字符回退并记告警。</summary>
    /// <param name="ctx">渲染上下文。</param>
    /// <param name="fontFamily">字体族名。</param>
    /// <param name="fontSizePt">字号（磅）。</param>
    /// <param name="bold">加粗。</param>
    /// <param name="italic">倾斜。</param>
    /// <param name="probeText">用于回退探测与告警定位的文本。</param>
    /// <param name="elementId">告警归属的元素 id。</param>
    public TextStyle(RenderContext ctx, string fontFamily, double fontSizePt, bool bold, bool italic, string probeText, string? elementId)
    {
        _ctx = ctx;
        ElementId = elementId;
        var probe = FirstProbeChar(probeText);
        Typeface = ctx.FontResolver.Resolve(fontFamily, bold, italic, probe);
        SizeMm = ctx.PtToUnits(fontSizePt);
        Paint = new SKPaint
        {
            Typeface = Typeface,
            TextSize = (float)SizeMm,
            Color = SKColors.Black,
            IsAntialias = true,
            SubpixelText = true,
        };

        // Resolve 承诺永不失败，所以"是否真的回退了"只能问 FamilyExists。
        // 自定义解析器默认回答"存在"，于是不产生告警——宁可漏报，不可误报。
        if (!ctx.FontResolver.FamilyExists(fontFamily))
        {
            ctx.Report.Add(elementId, RenderIssueKind.FontFallback,
                $"字体族 '{fontFamily}' 不可用，已回退为 '{Typeface.FamilyName}'。");
        }

        FontFamily = fontFamily;
    }

    /// <summary>字体族名（告警定位用）。</summary>
    public string FontFamily { get; }

    /// <summary>解析到的字形来源，可能来自回退。</summary>
    public SKTypeface Typeface { get; }

    /// <summary>字号（mm）。</summary>
    public double SizeMm { get; }

    /// <summary>字间距（mm）。0 表示按字体默认字距；非 0 时逐字绘制。</summary>
    public double LetterSpacingMm { get; set; }

    /// <summary>承载字号/字色/描边等绘制状态的画刷，归属本实例释放。</summary>
    public SKPaint Paint { get; }

    /// <summary>行距（mm）= 字号 × 倍数。</summary>
    public double LineHeight(double lineSpacing) => SizeMm * Math.Max(lineSpacing, 0.1);

    /// <summary>基线相对行顶的下移量（mm，正值）。</summary>
    public double AscentMm => -Paint.FontMetrics.Ascent;

    /// <summary>字形下缘到基线（mm，正值）。</summary>
    public double DescentMm => Paint.FontMetrics.Descent;

    /// <summary>
    /// 行内基线相对行顶的偏移（mm）。按 CSS 行盒模型：字号内容区（ascent+descent）在行高里居中，
    /// 上下各分一半 leading，基线落在内容区底部往上 <see cref="AscentMm"/> 处。
    /// 行高小于内容区时 leading 为负，基线上移——hiprint 的 9.75pt 固定行高配 13pt 字号就是这种情况。
    /// </summary>
    public double BaselineOffset(double lineHeight) => (lineHeight - AscentMm - DescentMm) / 2 + AscentMm;

    /// <summary>文本宽度（mm）。含字间距：<c>MeasureText</c> 只给字形步进，末字之后不加间距。</summary>
    public double Measure(string text)
        => text.Length == 0 ? 0 : Paint.MeasureText(text) + LetterSpacingMm * (text.Length - 1);

    /// <summary>单个字符的步进宽度（mm）。</summary>
    public double MeasureChar(char ch) => Paint.MeasureText(ch.ToString());

    /// <summary>
    /// 单行超宽时用省略号截断（hiprint <c>textContentWrap: ellipsis</c>）。
    /// 宽度已够时原样返回，连省略号都放不下时返回空串，交由元素框裁切。
    /// </summary>
    public string Truncate(string line, double maxWidthMm)
    {
        if (line.Length == 0 || maxWidthMm <= 0 || Measure(line) <= maxWidthMm)
            return line;

        const string ellipsis = "…";
        var tail = Measure(ellipsis);
        if (tail > maxWidthMm)
            return string.Empty;

        var text = line;
        while (text.Length > 0 && Measure(text) + tail > maxWidthMm)
            text = text[..^1];

        return text + ellipsis;
    }

    /// <summary>
    /// 按最大宽度折行。先按硬换行切段，再对每段贪心填充：
    /// 优先在空格后断行（西文），无空格可断时逐字断开（CJK）。
    /// 逐字累加宽度与整串 <c>MeasureText</c> 的差值在本机实测为 0（探针 §7，中英混排 158.608mm 两边相等），
    /// 因此用 O(n) 增量测宽；最终每行的对齐位移仍用整串 MeasureText 计算。
    /// </summary>
    public List<string> Wrap(string text, double maxWidthMm, bool wrap)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (!wrap || maxWidthMm <= 0)
            {
                lines.Add(paragraph);
                continue;
            }

            AppendWrapped(lines, paragraph, maxWidthMm);
        }

        return lines;
    }

    void AppendWrapped(List<string> lines, string paragraph, double maxWidthMm)
    {
        if (paragraph.Length == 0)
        {
            lines.Add(string.Empty);
            return;
        }

        var start = 0;
        var width = 0d;
        var lastBreak = -1; // 可断点的下一个起点（空格之后）

        for (var i = 0; i < paragraph.Length; i++)
        {
            var ch = paragraph[i];
            width += MeasureChar(ch);
            if (i > start)
                width += LetterSpacingMm;

            if (width > maxWidthMm)
            {
                if (lastBreak > start)
                {
                    lines.Add(paragraph[start..lastBreak].TrimEnd());
                    start = lastBreak;
                }
                else
                {
                    // 无空格可断（CJK 或单个超长词）：在超出前的上一个字符处硬断。
                    // 首字符就已超宽时不产生空行，让它单独占一行并被裁剪。
                    var cut = i == start ? i + 1 : i;
                    lines.Add(paragraph[start..cut].TrimEnd());
                    start = cut;
                }

                width = Measure(paragraph[start..(i + 1)]);
                lastBreak = -1;
                if (paragraph[i] == ' ')
                    lastBreak = i + 1;
            }
            else if (ch == ' ')
            {
                lastBreak = i + 1;
            }
        }

        if (start < paragraph.Length)
            lines.Add(paragraph[start..].TrimEnd());
        else if (lines.Count == 0)
            lines.Add(string.Empty);
    }

    /// <summary>
    /// 画一行文本。位图目标与 PDF 的矢量文本走 <see cref="SKCanvas.DrawText(string, float, float, SKPaint)"/>；
    /// <see cref="RenderOptions.PdfTextAsOutlines"/> 打开时改为字形轮廓，同一条基线与 x，故栅格结果一致。
    /// </summary>
    public void DrawLine(SKCanvas canvas, string line, double x, double baselineY, SKColor color)
        => DrawLine(canvas, line, x, baselineY, color, extraSpacingMm: 0);

    /// <summary>
    /// 画一行文本，可附加逐字额外间距（两端对齐用）。字间距为 0 且无附加间距时走整串
    /// <see cref="SKCanvas.DrawText(string, float, float, SKPaint)"/> 快路径；否则逐字推进。
    /// </summary>
    public void DrawLine(SKCanvas canvas, string line, double x, double baselineY, SKColor color, double extraSpacingMm)
    {
        if (line.Length == 0)
            return;

        Paint.Color = color;
        var spacing = LetterSpacingMm + extraSpacingMm;

        if (spacing == 0)
        {
            DrawSingle(canvas, line, x, baselineY);
            return;
        }

        var cursor = x;
        for (var i = 0; i < line.Length; i++)
        {
            var text = line[i].ToString();
            DrawSingle(canvas, text, cursor, baselineY);
            cursor += MeasureChar(line[i]) + spacing;
        }
    }

    void DrawSingle(SKCanvas canvas, string text, double x, double baselineY)
    {
        if (_ctx.Options.PdfTextAsOutlines)
        {
            using var path = Paint.GetTextPath(text, (float)x, (float)baselineY);
            var style = Paint.Style;
            Paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(path, Paint);
            Paint.Style = style;
            return;
        }

        canvas.DrawText(text, (float)x, (float)baselineY, Paint);
    }

    /// <summary>
    /// 计算某一行的起笔 x：手动做水平对齐而不是用 <see cref="SKTextAlign"/>，
    /// 这样轮廓模式与文本模式共用同一套坐标，两种导出不会出现字形位移差。
    /// </summary>
    public double AlignedX(double boxLeft, double boxWidth, string line, HorizontalAlign align)
        => align switch
        {
            HorizontalAlign.Center => boxLeft + (boxWidth - Measure(line)) / 2,
            HorizontalAlign.Right => boxLeft + boxWidth - Measure(line),
            _ => boxLeft,
        };

    /// <summary>告警归属的元素 id。</summary>
    public string? ElementId { get; }

    /// <summary>
    /// 取探针文本的首个可绘制字符（样式池的分桶键必须与此同口径，否则"缓存里的字体"和"实测用的字体"对不上）。
    /// </summary>
    internal static char FirstProbeChar(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsControl(ch))
                return ch;
        }

        return 'A';
    }

    /// <summary>释放本实例拥有的画刷；字形来自解析器缓存，不得在此释放。</summary>
    public void Dispose() => Paint.Dispose();
}
