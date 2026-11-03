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

    /// <summary>样式实例会被表格样式池按脚本桶复用，复杂度只能逐串判定：
    /// 同一个桶里阿拉伯格与泰米尔格共用一个实例，按构造探针定方向会把阿拉伯文当 LTR 画。</summary>
    static bool IsComplex(string text) => Classify(text) != ScriptClassifier.Complexity.Simple;

    static bool IsRtl(string text) => Classify(text) == ScriptClassifier.Complexity.ComplexRtl;

    static ScriptClassifier.Complexity Classify(string text) => ScriptClassifier.Classify(text.AsSpan());

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

    /// <summary>
    /// 文本宽度（mm）。含字间距：字间距只加在相邻单元之间，末字之后不加。
    /// 复杂脚本走 HarfBuzz OpenType shaping（连字 + 词形式替换 + 方向反转），简单脚本走 Skia 快路径；
    /// shaped 路径的间距按**字形数**计——连字会把两个字符合成一个步进单元，按字符数算会凭空加宽。
    /// </summary>
    public double Measure(string text)
    {
        if (text.Length == 0) return 0;

        if (IsComplex(text))
        {
            var shaped = HarfBuzzShaper.Shape(text, Typeface, SizeMm, IsRtl(text));
            if (shaped is not null)
                return shaped.TotalAdvanceMm + LetterSpacingMm * Math.Max(0, shaped.Glyphs.Count - 1);
            // HarfBuzz 失败（字体无原始数据）时降级 Skia，避免崩掉
        }

        return Paint.MeasureText(text) + LetterSpacingMm * (text.Length - 1);
    }

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
    /// 简单脚本（西文 + CJK）优先在空格后断行，无空格可断时逐字断开。
    /// 复杂脚本（阿拉伯文/天城文等）**不做逐字硬断**——元音附着、合字、连字都必须交给 HarfBuzz 整串 shape，
    /// 硬断字会让连接符掉、元音错位、连字拆散。只在空格处断；单个超长词整段放一个行让它被裁切。
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

        if (IsComplex(paragraph))
        {
            AppendWrappedComplex(lines, paragraph, maxWidthMm);
            return;
        }

        // === Skia 快路径：空格处优先断，无空格逐字硬断 ===
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
    /// 复杂脚本专用折行：只在空格处断，完整的阿拉伯词/天城文词整段交给 HarfBuzz shape。
    /// 单个词超宽时整段溢出交给元素框裁切——把元音附着拆散会比整词溢出更难读。
    /// </summary>
    void AppendWrappedComplex(List<string> lines, string paragraph, double maxWidthMm)
    {
        var words = paragraph.Split(' ');
        // words[i] 是词，words[i].Length == 0 表示连续空格

        var current = string.Empty;
        foreach (var word in words)
        {
            // 试试把当前词（带空格）加到现有行
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(candidate) <= maxWidthMm || current.Length == 0)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current.TrimEnd());
                current = word;
            }
        }

        if (current.Length > 0)
            lines.Add(current.TrimEnd());
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
    /// 画一行文本，可附加逐字额外间距（两端对齐用）。
    /// 复杂脚本走 HarfBuzz OpenType shaping → SKTextBlob 直接用 glyph ID 绘制（避免 Skia 重 shape 掉连字）；
    /// 简单脚本（字间距/附加间距都为 0 时）走整串 <see cref="SKCanvas.DrawText(string, float, float, SKPaint)"/> 快路径。
    /// </summary>
    public void DrawLine(SKCanvas canvas, string line, double x, double baselineY, SKColor color, double extraSpacingMm)
    {
        if (line.Length == 0)
            return;

        Paint.Color = color;

        if (IsComplex(line))
        {
            DrawShaped(canvas, line, x, baselineY, extraSpacingMm);
            return;
        }

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

    /// <summary>
    /// HarfBuzz shape 路径：拿到 glyph 序列后用 SKTextBlobBuilder + AddPositionedRun 直接用 glyph IDs 构建 blob，
    /// 避免 SkiaSharp 二次 shaping 掉连字。
    /// </summary>
    void DrawShaped(SKCanvas canvas, string line, double x, double baselineY, double extraSpacingMm)
    {
        var shaped = HarfBuzzShaper.Shape(line, Typeface, SizeMm, IsRtl(line));
        if (shaped is null || shaped.Glyphs.Count == 0)
        {
            // HarfBuzz 失败降级 Skia 快路径
            DrawSingle(canvas, line, x, baselineY);
            return;
        }

        var glyphs = shaped.Glyphs;
        var spacing = LetterSpacingMm + extraSpacingMm;

        if (_ctx.Options.PdfTextAsOutlines)
        {
            DrawShapedOutlines(canvas, glyphs, x, baselineY, spacing);
            return;
        }

        var n = glyphs.Count;
        var glyphIds = new ushort[n];
        var positions = new SKPoint[n];
        double cursor = x;

        // HarfBuzz 对 RTL 已经做过 hb_buffer_reverse：返回的序列本身就是视觉从左到右
        // （实测 "السلام" 的 cluster 依次是 5,4,3,2,1,0），步进也都是正值。
        // 这里再反转一次会把逻辑首位摆到最左，整行镜像。
        for (int idx = 0; idx < n; idx++)
        {
            var g = glyphs[idx];
            glyphIds[idx] = g.GlyphId;
            positions[idx] = new SKPoint(
                (float)(cursor + g.OffsetXMm),
                (float)(baselineY - g.OffsetYMm));

            // 推进：advance 是 HarfBuzz 给的字体度量，加字间距（两端对齐补的间距）
            cursor += g.AdvanceMm + spacing;
        }

        using var font = new SKFont(Typeface, (float)SizeMm);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphIds.AsSpan(), font, positions.AsSpan());
        using var blob = builder.Build();

        // AddPositionedRun 落的是绝对坐标，而 DrawText 的 (x, y) 是对整个 blob 的**额外平移**：
        // 传 x/baselineY 会把整行推到两倍偏移上。实测传 (x, y) 时 20mm/40mm 基线的墨迹落在 40mm/73mm。
        canvas.DrawText(blob, 0, 0, Paint);
    }

    /// <summary>
    /// 轮廓模式下的 shaped 绘制：逐 glyph 取 <see cref="SKFont.GetGlyphPath"/>，
    /// 平移到与 blob 模式同一个点位，保证 <c>PdfTextAsOutlines</c> 开关前后栅格结果一致。
    /// SkiaSharp 2.88 没有"从 glyph ID 串直接拿整条路径"的入口，只有逐字形这一条。
    /// </summary>
    void DrawShapedOutlines(SKCanvas canvas, IReadOnlyList<HarfBuzzShaper.ShapedGlyph> glyphs, double x, double baselineY, double spacing)
    {
        using var font = new SKFont(Typeface, (float)SizeMm);
        var style = Paint.Style;
        Paint.Style = SKPaintStyle.Fill;

        var n = glyphs.Count;
        double cursor = x;

        for (var idx = 0; idx < n; idx++)
        {
            var g = glyphs[idx];
            using var path = font.GetGlyphPath(g.GlyphId);
            if (path is not null && !path.IsEmpty)
            {
                var state = canvas.Save();
                canvas.Translate((float)(cursor + g.OffsetXMm), (float)(baselineY - g.OffsetYMm));
                canvas.DrawPath(path, Paint);
                canvas.RestoreToCount(state);
            }

            cursor += g.AdvanceMm + spacing;
        }

        Paint.Style = style;
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
    ///
    /// RTL 脚本（阿拉伯文/希伯来文）下语义反转：Left = 贴右边缘，Right = 贴左边缘。
    /// Center / Justify 不受方向影响。样式实例会被样式池按脚本桶复用，方向只能逐串判。
    /// </summary>
    public double AlignedX(double boxLeft, double boxWidth, string line, HorizontalAlign align)
    {
        // RTL 反转左右对齐语义
        var rtl = IsRtl(line);
        var effectiveAlign = align;
        if (rtl && align == HorizontalAlign.Left) effectiveAlign = HorizontalAlign.Right;
        else if (rtl && align == HorizontalAlign.Right) effectiveAlign = HorizontalAlign.Left;

        return effectiveAlign switch
        {
            HorizontalAlign.Center => boxLeft + (boxWidth - Measure(line)) / 2,
            HorizontalAlign.Right => boxLeft + boxWidth - Measure(line),
            _ => boxLeft,
        };
    }

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
