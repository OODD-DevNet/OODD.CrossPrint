using System.Globalization;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 表格元素渲染器。静态行来自 <see cref="TableOptions.Rows"/>（其中的 <c>{{}}</c> 同样参与绑定），
/// 数据行由 <see cref="TableOptions.Field"/> 指定（为空时退回列字段路径的**公共首段**）的集合展开而来，
/// 两者按顺序拼接。<see cref="TableColumn.Sequence"/> 列输出 1 起的行号，
/// <see cref="TableColumn.Summary"/> 列在正文之后追加汇总行；
/// <see cref="TableOptions.HeaderRows"/> 非空时按 colspan/rowspan 渲染多行表头，否则用列标题渲染单行表头。
/// 表体三层样式能力：通用条件样式（<see cref="TableOptions.ConditionalStyles"/>，整行或限定列），
/// 片段样式（<see cref="TableOptions.Highlights"/>，格内子串混排），合并单元格
/// （<see cref="TableOptions.RowSpans"/> 显式跨度 + <see cref="TableColumn.MergeSameValue"/> 同值合并）。
/// </summary>
public sealed class TableRenderer : ElementRenderer<TableElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Table;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, TableElement element, RenderContext context)
    {
        // 表级样式级联：库样式的字体族/字号/行距/字色作整表默认值（解析副本，不改原模板）
        var o = ResolveTableOptions(element.Table, context);
        if (o.Columns.Count == 0)
            return;

        var widths = ResolveWidths(o, element.WidthMm);
        var lefts = new double[widths.Length];
        for (var c = 0; c < widths.Length; c++)
            lefts[c] = element.XMm + widths.Take(c).Sum();

        var rows = ResolveRows(o, context, element.Id, element.TestData, out var dataStart, out var dataItems, out var rawRows);
        var aligns = ResolveAligns(o, context);

        using var pool = new TableStylePool(context);
        try
        {
            var header = o.ShowHeader ? LayoutHeader(o, lefts, widths, pool, element.Id, aligns) : null;
            var body = LayoutBody(o, rows, dataStart, dataItems, lefts, widths, pool, element.Id, aligns);
            var footer = LayoutFooter(o, pool, rows, rawRows, dataStart, widths, element.Id);

            var contentHeight = (header?.Height ?? 0) + body.RowHeights.Sum() + (footer?.Height ?? 0);
            if (contentHeight > element.HeightMm + 0.01)
            {
                context.Report.Add(element.Id, RenderIssueKind.TableOverflow,
                    $"表格内容高 {contentHeight:0.##}mm 超出元素高 {element.HeightMm:0.##}mm（{body.RowHeights.Length} 行数据），超出部分被裁切。");
            }

            Paint(canvas, element, o, header, body, footer, lefts, widths, aligns);
        }
        finally
        {
            pool.Dispose();
        }
    }

    /// <summary>一行的布局结果：行高 + 每列折行后的文本 + 折行所用的逐列样式（绘制必须复用同一实例，样式池按脚本分桶后"再取一次"可能落在别的桶）。</summary>
    internal sealed record RowBlock(double Height, List<string>[] Cells, TextStyle[] Styles);

    /// <summary>表头单元格的布局结果。</summary>
    internal sealed record HeaderCellLayout(
        HorizontalAlign Align,
        TextStyle Style,
        double X,
        double Top,
        double Width,
        double Height,
        List<string> Lines,
        SKColor? TextColor = null,
        SKColor? Background = null,
        bool Underline = false,
        bool Strikethrough = false);

    /// <summary>表头整体布局：行高数组 + 占用网格（供网格线判断合并边界）。</summary>
    internal sealed record HeaderLayout(double Height, double[] RowTops, double[] RowHeights, HeaderCellLayout?[,] Grid, List<HeaderCellLayout> Cells);

    /// <summary>一格文本的一个绘制片段：子串 + 样式 + 颜色覆盖 + 装饰线 + 实测宽。</summary>
    internal sealed record CellSegment(string Text, TextStyle Style, SKColor? TextColor, SKColor? Background, double Width, bool Underline = false, bool Strikethrough = false);

    /// <summary>一格文本的一个视觉行：混排片段序列 + 总宽 + 行高 + 基线偏移。</summary>
    internal sealed record StyledLine(List<CellSegment> Segments, double Width, double Height, double BaselineOffset);

    /// <summary>表体一格布局。合并单元格覆盖的多个格位共享同一实例（网格线判断与文本只画一次）。</summary>
    internal sealed record BodyCellLayout(
        double X,
        double Top,
        double Width,
        double Height,
        HorizontalAlign Align,
        List<StyledLine> Lines,
        SKColor? Background,
        SKColor? TextColor = null);

    /// <summary>表体整体布局：行高 + 占用网格（合并格共享引用）+ 去重的格子清单 + 每行条件底色。</summary>
    internal sealed record BodyLayout(
        double[] RowTops,
        double[] RowHeights,
        BodyCellLayout?[,] Grid,
        List<BodyCellLayout> Cells,
        SKColor?[] RowFills);

    /// <summary>一格的字体形态（列样式/条件样式覆盖后的有效值）。</summary>
    sealed record CellLook(
        double Pt,
        bool Bold,
        bool Italic,
        string Family,
        bool Underline = false,
        bool Strikethrough = false,
        double LetterSpacingPt = 0);

    /// <summary>关键词命中区间上的样式覆盖。</summary>
    sealed record SegmentSpan(
        int Start,
        int Length,
        SKColor? TextColor,
        SKColor? Background,
        bool? Bold,
        bool? Italic,
        double? FontSizePt,
        string? FontFamily,
        bool? Underline = null,
        bool? Strikethrough = null,
        double? LetterSpacingPt = null);

    /// <summary>经样式库解析后的关键词片段规则：Rule 的可空载荷已补上样式值；装饰线无内联字段，平行存放。</summary>
    sealed record ResolvedHighlight(TableHighlightRule Rule, bool? Underline, bool? Strikethrough, double? LetterSpacingPt);

    /// <summary>经样式库解析后的条件样式规则，同 <see cref="ResolvedHighlight"/>。</summary>
    sealed record ResolvedCondition(TableConditionalStyle Rule, bool? Underline, bool? Strikethrough, double? LetterSpacingPt);

    /// <summary>
    /// 表格共用样式池：同一 (字体族, 字号, 加粗, 倾斜, 字间距, 脚本桶) 组合只解析一次字体，
    /// 条件样式/片段样式让格子字号各异时避免重复解析。
    /// </summary>
    internal sealed class TableStylePool : IDisposable
    {
        readonly RenderContext _ctx;
        readonly Dictionary<(string Family, double Pt, bool Bold, bool Italic, double LetterSpacingPt, int Script), TextStyle> _styles = new();
        readonly List<TextStyle> _owned = new();

        public TableStylePool(RenderContext ctx) => _ctx = ctx;

        /// <summary>渲染上下文（列/表头格/规则按 StyleId 查样式库要用）。</summary>
        public RenderContext Context => _ctx;

        public TextStyle Get(string family, double fontSizePt, bool bold, bool italic, string elementId, double letterSpacingPt = 0, string? probeText = null)
        {
            // 脚本桶进键：回退按探针字符选字体，键里不带脚本时"谁先被测量"决定整列字体——
            // ASCII 格先测会把 CJK 格劫持到没有汉字字形的西文回退字体上，折行与行高全按豆腐块步进算。
            var key = (family, fontSizePt, bold, italic, letterSpacingPt, ScriptBucketOf(probeText));
            if (_styles.TryGetValue(key, out var style))
                return style;

            // 探针用真实内容而不是 "0"：字体族缺失时回退按探针字符选字体，
            // ASCII 探针会落到没有汉字字形的西文字体，折行与行高全按豆腐块的窄步进算。
            style = new TextStyle(_ctx, family, fontSizePt, bold, italic, probeText ?? "0", elementId);
            style.LetterSpacingMm = Math.Max(0, _ctx.PtToUnits(letterSpacingPt));
            _styles[key] = style;
            _owned.Add(style);
            return style;
        }

        /// <summary>
        /// 探针文本 → 脚本桶（与 <see cref="TextStyle.FirstProbeChar"/> 同口径取首字符再归类）。
        /// 三桶是缺字回退真正会分叉的最小划分：西文回退字体统一覆盖拉丁/希腊/西里尔，
        /// 东亚字体族统一覆盖汉字/假名/韩文，其余脚本（阿拉伯、天城、泰文等）自成一桶；
        /// 若把精确字符进键，缓存会随单元格内容无限增长，必须收在这三个常数桶内。
        /// </summary>
        static int ScriptBucketOf(string? probeText) => Bucket(TextStyle.FirstProbeChar(probeText ?? "0"));

        static int Bucket(char ch) => ch switch
        {
            < '\u0500' => 0,                          // 拉丁/希腊/西里尔：西文回退字体全覆盖
            >= '\u1100' and <= '\u11FF' => 1,         // 韩文 Jamo
            >= '\u2E80' and <= '\uD7FF' => 1,         // CJK 部首～韩文音节整块（含假名/汉字/彝文）
            >= '\uF900' and <= '\uFAFF' => 1,         // CJK 兼容表意文字
            >= '\uD840' => 1,                         // 高代理对：CJK 扩展 B 及以后
            _ => 2,                                    // 其他非拉丁脚本
        };

        public void Dispose()
        {
            foreach (var style in _owned)
                style.Dispose();
        }
    }

    internal static HeaderLayout? LayoutHeader(TableOptions o, double[] lefts, double[] widths, TableStylePool pool, string elementId, HorizontalAlign[] aligns)
    {
        var headerPt = o.HeaderFontSizePt ?? o.FontSizePt;
        if (o.HeaderRows.Count == 0)
            return LayoutSingleRowHeader(o, lefts, widths, pool, headerPt, elementId, aligns);

        var colCount = o.Columns.Count;
        var cells = o.HeaderRows.Where(c => c.ColSpan > 0 && c.RowSpan > 0).ToList();
        if (cells.Count == 0)
            return LayoutSingleRowHeader(o, lefts, widths, pool, headerPt, elementId, aligns);

        var rowCount = Math.Max(1, cells.Max(c => c.Row + Math.Max(1, c.RowSpan)));
        var spans = new List<(TableHeaderCell Source, int Row, int Column, int ColSpan, int RowSpan, TextStyle Style, List<string> Lines, NamedStyle? St)>();
        foreach (var cell in cells)
        {
            var row = Math.Clamp(cell.Row, 0, rowCount - 1);
            var column = Math.Clamp(cell.Column, 0, colCount - 1);
            var colSpan = Math.Clamp(cell.ColSpan, 1, colCount - column);
            var rowSpan = Math.Clamp(cell.RowSpan, 1, rowCount - row);
            // 表头格样式：字体族/字号/加粗/倾斜/装饰/字色/底色按 StyleId 级联
            var st = StyleCascade.Find(pool.Context, cell.StyleId);
            var style = pool.Get(
                st?.FontFamily ?? o.FontFamily,
                cell.FontSizePt ?? st?.FontSizePt ?? headerPt,
                st?.Bold ?? o.HeaderBold,
                st?.Italic ?? false,
                elementId,
                st?.LetterSpacingPt ?? 0,
                cell.Title);
            var widthMm = widths.Skip(column).Take(colSpan).Sum();
            var innerWidth = Math.Max(0, widthMm - 2 * o.CellPaddingMm);
            spans.Add((cell, row, column, colSpan, rowSpan, style, style.Wrap(cell.Title, innerWidth, wrap: true), st));
        }

        var rowHeights = new double[rowCount];
        for (var r = 0; r < rowCount; r++)
        {
            var starting = spans.Where(s => s.Row == r).ToList();
            var lineHeight = starting.Count == 0 ? UnitConverter.PtToMm(headerPt) * Math.Max(o.LineSpacing, 0.1) : starting.Max(s => s.Style.LineHeight(o.LineSpacing));
            var maxLines = starting.Count == 0 ? 1 : starting.Max(s => s.Lines.Count);
            rowHeights[r] = o.HeaderRowHeightMm > 0
                ? o.HeaderRowHeightMm
                : Math.Max(maxLines * lineHeight + 2 * o.CellPaddingMm, MinRowHeight(o, headerPt));
        }

        // 跨行单元格要求的总高可能超过各行自动高之和：把差额补到它覆盖的最后一行，
        // 否则文字会被格子边界裁掉一半，看起来像字体渲染坏了。
        foreach (var span in spans.Where(s => s.RowSpan > 1))
        {
            var required = span.Lines.Count * span.Style.LineHeight(o.LineSpacing) + 2 * o.CellPaddingMm;
            var available = 0d;
            for (var r = span.Row; r < span.Row + span.RowSpan; r++)
                available += rowHeights[r];
            if (required > available)
                rowHeights[span.Row + span.RowSpan - 1] += required - available;
        }

        var rowTops = new double[rowCount];
        for (var r = 1; r < rowCount; r++)
            rowTops[r] = rowTops[r - 1] + rowHeights[r - 1];

        var grid = new HeaderCellLayout?[rowCount, colCount];
        var layouts = new List<HeaderCellLayout>(spans.Count);
        foreach (var span in spans)
        {
            var x = lefts[span.Column];
            var width = widths.Skip(span.Column).Take(span.ColSpan).Sum();
            var top = rowTops[span.Row];
            var height = 0d;
            for (var r = span.Row; r < span.Row + span.RowSpan; r++)
                height += rowHeights[r];

            var align = StyleCascade.Pick(span.Source.Align, HorizontalAlign.Center, span.St?.HorizontalAlign);
            var layout = new HeaderCellLayout(
                align, span.Style, x, top, width, height, span.Lines,
                span.St?.TextColor, span.St?.BackgroundColor,
                span.St?.Underline ?? false, span.St?.Strikethrough ?? false);
            layouts.Add(layout);
            for (var r = span.Row; r < span.Row + span.RowSpan && r < rowCount; r++)
            {
                for (var c = span.Column; c < span.Column + span.ColSpan && c < colCount; c++)
                    grid[r, c] ??= layout;
            }
        }

        var total = rowHeights.Sum();
        return new HeaderLayout(total, rowTops, rowHeights, grid, layouts);
    }

    static HeaderLayout LayoutSingleRowHeader(TableOptions o, double[] lefts, double[] widths, TableStylePool pool, double headerPt, string elementId, HorizontalAlign[] aligns)
    {
        var style = pool.Get(o.FontFamily, headerPt, o.HeaderBold, italic: false, elementId,
            probeText: o.Columns.FirstOrDefault(c => !string.IsNullOrEmpty(c.Title))?.Title);
        var layouts = new List<HeaderCellLayout>(o.Columns.Count);
        var maxLines = 1;
        for (var c = 0; c < o.Columns.Count; c++)
        {
            var lines = style.Wrap(o.Columns[c].Title, Inner(widths[c], o), wrap: true);
            maxLines = Math.Max(maxLines, lines.Count);
            layouts.Add(new HeaderCellLayout(aligns[c], style, lefts[c], 0, widths[c], 0, lines));
        }

        var height = o.RowHeightMm > 0
            ? o.RowHeightMm
            : Math.Max(maxLines * style.LineHeight(o.LineSpacing) + 2 * o.CellPaddingMm, MinRowHeight(o, headerPt));

        // 单行表头没有合并，占用网格就是逐列一格
        var grid = new HeaderCellLayout?[1, layouts.Count];
        for (var c = 0; c < layouts.Count; c++)
            grid[0, c] = layouts[c];

        var filled = layouts.Select(l => l with { Height = height }).ToList();
        for (var c = 0; c < filled.Count; c++)
            grid[0, c] = filled[c];

        return new HeaderLayout(height, [0], [height], grid, filled);
    }

    /// <summary>
    /// 表体布局：条件样式求值 → 逐格折行（片段感知）→ 行高 → 普通格 → 合并格。
    /// 行高只由普通格决定；合并格的高度是覆盖行高之和，文字超出被裁剪（分页容量因此不受合并影响）。
    /// </summary>
    internal static BodyLayout LayoutBody(
        TableOptions o,
        List<List<string>> rows,
        int dataStart,
        IReadOnlyList<object?>? dataItems,
        double[] lefts,
        double[] widths,
        TableStylePool pool,
        string elementId,
        HorizontalAlign[] aligns)
    {
        var rowCount = rows.Count;
        var colCount = o.Columns.Count;
        var rowTops = new double[rowCount];
        var rowHeights = new double[rowCount];
        var grid = new BodyCellLayout?[rowCount, colCount];
        var boxes = new BodyCellLayout?[rowCount, colCount];
        var cells = new List<BodyCellLayout>(Math.Max(1, rowCount * colCount));

        // ---- 0. 列样式：列引用的命名样式作该列默认形态（列内联无对应字段，直接补位）----
        var colStyles = new NamedStyle?[colCount];
        for (var c = 0; c < colCount; c++)
            colStyles[c] = StyleCascade.Find(pool.Context, o.Columns[c].StyleId);

        // ---- 1. 条件样式求值：整行底色 + 逐格（背景/字色/字体形态）覆盖 ----
        var rowFills = new SKColor?[rowCount];
        var cellBg = new SKColor?[rowCount, colCount];
        var cellFg = new SKColor?[rowCount, colCount];
        var looks = new CellLook[rowCount, colCount];
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                var st = colStyles[c];
                looks[r, c] = new CellLook(
                    o.Columns[c].FontSizePt ?? st?.FontSizePt ?? o.FontSizePt,
                    st?.Bold ?? false,
                    st?.Italic ?? false,
                    st?.FontFamily ?? o.FontFamily,
                    st?.Underline ?? false,
                    st?.Strikethrough ?? false,
                    st?.LetterSpacingPt ?? 0);
                cellBg[r, c] = st?.BackgroundColor;
                cellFg[r, c] = st?.TextColor;
            }
        }

        ApplyConditionalStyles(o, rows, dataStart, dataItems, rowFills, cellBg, cellFg, looks, ResolveConditions(o, pool.Context));

        // ---- 1.5 列脚本样式：数据行逐格求值，优先级最高（覆盖条件样式/列内联/表样式）----
        var cellAlign = new HorizontalAlign[rowCount, colCount];
        for (var r = 0; r < rowCount; r++)
            for (var c = 0; c < colCount; c++)
                cellAlign[r, c] = aligns[c];

        var scriptStyles = new ScriptStyle?[rowCount, colCount];
        if (dataItems is not null)
        {
            var explicitField = !string.IsNullOrWhiteSpace(o.Field);
            for (var r = dataStart; r < rowCount; r++)
            {
                if (r - dataStart >= dataItems.Count)
                    continue;

                var item = dataItems[r - dataStart];
                for (var c = 0; c < colCount; c++)
                {
                    var col = o.Columns[c];
                    if (string.IsNullOrWhiteSpace(col.Styler))
                        continue;

                    var path = explicitField ? col.Field : DataBinder.StripFirstSegment(col.Field);
                    object? value;
                    if (path.Length == 0)
                        value = item;
                    else
                        DataBinder.TryResolve(item, path, out value);

                    var script = ScriptSupport.ColumnStyler(pool.Context, col, value, item, r - dataStart, elementId);
                    if (script is null)
                        continue;

                    scriptStyles[r, c] = script;
                    var look = looks[r, c];
                    looks[r, c] = look with
                    {
                        Pt = script.FontSizePt ?? look.Pt,
                        Bold = script.Bold ?? look.Bold,
                        Italic = script.Italic ?? look.Italic,
                        Underline = script.Underline ?? look.Underline,
                        Strikethrough = script.Strikethrough ?? look.Strikethrough,
                        LetterSpacingPt = script.LetterSpacingPt ?? look.LetterSpacingPt,
                    };
                    if (script.BackgroundColor is { } scriptBg)
                        cellBg[r, c] = scriptBg;
                    if (script.Color is { } scriptFg)
                        cellFg[r, c] = scriptFg;
                    if (script.HorizontalAlign is { } scriptAlign)
                        cellAlign[r, c] = scriptAlign;
                }
            }
        }

        // ---- 2. 片段样式：先算好每列适用的关键词规则（规则先经其 StyleId 解析）----
        var highlights = ResolveHighlights(o, pool.Context);
        var rulesByColumn = new List<ResolvedHighlight>?[colCount];
        for (var c = 0; c < colCount; c++)
        {
            if (highlights.Length == 0)
                continue;
            List<ResolvedHighlight>? list = null;
            foreach (var rule in highlights)
            {
                if (AppliesToColumn(rule.Rule.Column, o.Columns[c].Field))
                    (list ??= []).Add(rule);
            }
            rulesByColumn[c] = list;
        }

        // ---- 3. 逐格折行与行高 ----
        var lineCache = new List<StyledLine>[rowCount, colCount];
        for (var r = 0; r < rowCount; r++)
        {
            var height = 0d;
            for (var c = 0; c < colCount; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : string.Empty;
                var look = looks[r, c];
                var spans = rulesByColumn[c] is { } rules ? FindSegments(text, rules) : null;
                var lines = WrapStyled(text, spans, look, o, pool, elementId, Inner(widths[c], o));
                lineCache[r, c] = lines;
                var content = 0d;
                foreach (var line in lines)
                    content += line.Height;
                var cellHeight = o.RowHeightMm > 0
                    ? o.RowHeightMm
                    : Math.Max(content + 2 * o.CellPaddingMm, MinRowHeight(o, look.Pt));
                height = Math.Max(height, cellHeight);
            }

            rowHeights[r] = height;
            rowTops[r] = r == 0 ? 0 : rowTops[r - 1] + rowHeights[r - 1];
        }

        // ---- 3.5 脚本颜色收口：styler 给出的字色/底色也压过片段样式（styler 全场最高优先级）----
        for (var r = dataStart; r < rowCount; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                var script = scriptStyles[r, c];
                if (script is null || (script.Color is null && script.BackgroundColor is null))
                    continue;

                var lines = lineCache[r, c];
                if (lines is null)
                    continue;

                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    lines[i] = line with
                    {
                        Segments = line.Segments
                            .Select(seg => seg with
                            {
                                TextColor = script.Color ?? seg.TextColor,
                                Background = script.BackgroundColor ?? seg.Background,
                            })
                            .ToList(),
                    };
                }
            }
        }

        // ---- 4. 普通格 ----
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                var box = new BodyCellLayout(
                    lefts[c], rowTops[r], widths[c], rowHeights[r],
                    cellAlign[r, c], lineCache[r, c]!, cellBg[r, c], cellFg[r, c]);
                boxes[r, c] = box;
                grid[r, c] = box;
                cells.Add(box);
            }
        }

        // ---- 5. 合并：先到先得，与已合并区域冲突的跨度整体跳过 ----
        void Claim(int row, int column, int rowSpan, int colSpan)
        {
            if (rowCount == 0 || colCount == 0)
                return;
            row = Math.Clamp(row, 0, rowCount - 1);
            column = Math.Clamp(column, 0, colCount - 1);
            rowSpan = Math.Clamp(rowSpan, 1, rowCount - row);
            colSpan = Math.Clamp(colSpan, 1, colCount - column);
            if (rowSpan <= 1 && colSpan <= 1)
                return;

            // 全部覆盖格位必须还是自己的普通格——已被别的合并占走的就整体放弃
            for (var rr = row; rr < row + rowSpan; rr++)
            {
                for (var cc = column; cc < column + colSpan; cc++)
                {
                    if (grid[rr, cc] is null || !ReferenceEquals(grid[rr, cc], boxes[rr, cc]))
                        return;
                }
            }

            // 以左上角格为源：文本、字体形态、关键词片段、底色（格底色优先，其次行底色）
            var text = column < rows[row].Count ? rows[row][column] : string.Empty;
            var look = looks[row, column];
            var spans = rulesByColumn[column] is { } rules ? FindSegments(text, rules) : null;
            var width = widths.Skip(column).Take(colSpan).Sum();
            var lines = WrapStyled(text, spans, look, o, pool, elementId, Math.Max(0, width - 2 * o.CellPaddingMm));
            var height = 0d;
            for (var rr = row; rr < row + rowSpan; rr++)
                height += rowHeights[rr];

            var merged = new BodyCellLayout(
                lefts[column], rowTops[row], width, height,
                cellAlign[row, column], lines,
                cellBg[row, column] ?? rowFills[row],
                cellFg[row, column]);

            for (var rr = row; rr < row + rowSpan; rr++)
            {
                for (var cc = column; cc < column + colSpan; cc++)
                {
                    cells.Remove(boxes[rr, cc]!);
                    boxes[rr, cc] = null;
                    grid[rr, cc] = merged;
                }
            }

            cells.Add(merged);
        }

        // 静态行显式跨度（行下标在表体行序上，含数据行；跨页时按页窗口独立生效）
        foreach (var span in o.RowSpans)
            Claim(span.Row, span.Column, span.RowSpan, span.ColSpan);

        // 数据行同值合并：每列独立扫描连续等值段；空串不参与合并，跨页在窗口边界断开
        for (var c = 0; c < colCount; c++)
        {
            if (!o.Columns[c].MergeSameValue)
                continue;

            var r = dataStart;
            while (r < rowCount)
            {
                var value = c < rows[r].Count ? rows[r][c] : string.Empty;
                var runEnd = r + 1;
                if (value.Length > 0)
                {
                    while (runEnd < rowCount
                           && string.Equals(runEnd < rows[runEnd].Count ? rows[runEnd][c] : string.Empty, value, StringComparison.Ordinal))
                        runEnd++;

                    if (runEnd - r >= 2)
                        Claim(r, c, runEnd - r, 1);
                }

                r = Math.Max(runEnd, r + 1);
            }
        }

        return new BodyLayout(rowTops, rowHeights, grid, cells, rowFills);
    }

    /// <summary>
    /// 通用条件样式求值。数据行的 <see cref="TableConditionalStyle.Field"/> 从行对象取值；
    /// Field 留空的规则用命中列单元格自身文本（要求限定列，静态行与测试数据行也参与）。
    /// 规则按数组顺序评估，后命中覆盖先命中；无任何样式载荷或匹配不到列的规则直接跳过。
    /// </summary>
    static void ApplyConditionalStyles(
        TableOptions o,
        List<List<string>> rows,
        int dataStart,
        IReadOnlyList<object?>? dataItems,
        SKColor?[] rowFills,
        SKColor?[,] cellBg,
        SKColor?[,] cellFg,
        CellLook[,] looks,
        ResolvedCondition[] rules)
    {
        if (rules.Length == 0)
            return;

        var colCount = o.Columns.Count;
        var rowCount = rows.Count;

        // 预解析每条规则的命中列；Column 空 = 整行（所有列）
        var scopes = new List<int>[rules.Length];
        for (var i = 0; i < rules.Length; i++)
        {
            var rule = rules[i].Rule;
            if (string.IsNullOrWhiteSpace(rule.Column))
            {
                scopes[i] = Enumerable.Range(0, colCount).ToList();
                continue;
            }

            var matched = new List<int>();
            for (var c = 0; c < colCount; c++)
            {
                if (AppliesToColumn(rule.Column, o.Columns[c].Field))
                    matched.Add(c);
            }

            scopes[i] = matched;
        }

        for (var r = 0; r < rowCount; r++)
        {
            object? item = null;
            if (dataItems is not null && r >= dataStart && r - dataStart < dataItems.Count)
                item = dataItems[r - dataStart];

            for (var i = 0; i < rules.Length; i++)
            {
                var (rule, under, strike, letter) = rules[i];
                var rowScoped = string.IsNullOrWhiteSpace(rule.Column);
                if (IsNoPayload(rules[i]) || scopes[i].Count == 0)
                    continue;
                if (rule.Field.Length == 0 && rowScoped)
                    continue; // 没字段又没限定列：无从取值，规则不生效

                string? fieldLeft = null;
                if (rule.Field.Length > 0)
                {
                    if (item is null || !DataBinder.TryResolve(item, rule.Field, out var value))
                        continue;
                    fieldLeft = DataBinder.ToString(value);
                }

                foreach (var c in scopes[i])
                {
                    var left = fieldLeft ?? (c < rows[r].Count ? rows[r][c] : string.Empty);
                    if (!Matches(left, rule))
                        continue;

                    if (rule.BackgroundColor is { } bg)
                    {
                        if (rowScoped)
                            rowFills[r] = bg;
                        else
                            cellBg[r, c] = bg;
                    }

                    if (rule.TextColor is { } fg)
                        cellFg[r, c] = fg;

                    if (rule.Bold is { } || rule.Italic is { } || rule.FontSizePt is { } || rule.FontFamily is not null
                        || under is { } || strike is { } || letter is { })
                    {
                        var cur = looks[r, c];
                        looks[r, c] = new CellLook(
                            rule.FontSizePt ?? cur.Pt,
                            rule.Bold ?? cur.Bold,
                            rule.Italic ?? cur.Italic,
                            rule.FontFamily ?? cur.Family,
                            under ?? cur.Underline,
                            strike ?? cur.Strikethrough,
                            letter ?? cur.LetterSpacingPt);
                    }
                }
            }
        }
    }

    static bool IsNoPayload(ResolvedCondition rule) =>
        rule.Rule.BackgroundColor is null
        && rule.Rule.TextColor is null
        && rule.Rule.Bold is null
        && rule.Rule.Italic is null
        && rule.Rule.FontSizePt is null
        && rule.Rule.FontFamily is null
        && rule.Underline is null
        && rule.Strikethrough is null
        && rule.LetterSpacingPt is null;

    /// <summary>条件比较：数值优先（不变文化解析，失败回退当前文化），否则按忽略大小写的文本序；Contains/NotContains 按子串。</summary>
    static bool Matches(string left, TableConditionalStyle rule)
    {
        var right = rule.Value ?? string.Empty;
        if (rule.Operator is TableConditionOperator.Contains or TableConditionOperator.NotContains)
        {
            var contains = left.Contains(right, StringComparison.OrdinalIgnoreCase);
            return rule.Operator == TableConditionOperator.Contains ? contains : !contains;
        }

        if (TryParseNumber(left, out var a) && TryParseNumber(right, out var b))
        {
            return rule.Operator switch
            {
                TableConditionOperator.GreaterThan => a > b,
                TableConditionOperator.GreaterOrEqual => a >= b,
                TableConditionOperator.LessThan => a < b,
                TableConditionOperator.LessOrEqual => a <= b,
                TableConditionOperator.Equal => a == b,
                TableConditionOperator.NotEqual => a != b,
                _ => false,
            };
        }

        var cmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return rule.Operator switch
        {
            TableConditionOperator.GreaterThan => cmp > 0,
            TableConditionOperator.GreaterOrEqual => cmp >= 0,
            TableConditionOperator.LessThan => cmp < 0,
            TableConditionOperator.LessOrEqual => cmp <= 0,
            TableConditionOperator.Equal => cmp == 0,
            TableConditionOperator.NotEqual => cmp != 0,
            _ => false,
        };
    }

    static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    /// <summary>规则列限定匹配：Column 空 = 所有列；填了就与列字段路径（含剥首段形态）精确比对。</summary>
    static bool AppliesToColumn(string? ruleColumn, string field)
    {
        if (string.IsNullOrWhiteSpace(ruleColumn))
            return true;
        return string.Equals(field, ruleColumn, StringComparison.Ordinal)
               || string.Equals(DataBinder.StripFirstSegment(field), ruleColumn, StringComparison.Ordinal);
    }

    /// <summary>关键词 → 片段区间：找全部出现（忽略大小写按规则），空关键词整格命中，重叠先到先得。</summary>
    static List<SegmentSpan> FindSegments(string text, List<ResolvedHighlight> rules)
    {
        List<SegmentSpan>? spans = null;

        void TryAdd(int start, int length, ResolvedHighlight rule)
        {
            length = Math.Min(length, text.Length - start);
            if (length <= 0)
                return;
            var end = start + length;
            if (spans is not null)
            {
                foreach (var span in spans)
                {
                    if (start < span.Start + span.Length && span.Start < end)
                        return; // 与先到的片段重叠：先到先得
                }
            }

            (spans ??= []).Add(new SegmentSpan(
                start, length,
                rule.Rule.TextColor, rule.Rule.BackgroundColor, rule.Rule.Bold, rule.Rule.Italic,
                rule.Rule.FontSizePt, rule.Rule.FontFamily,
                rule.Underline, rule.Strikethrough, rule.LetterSpacingPt));
        }

        foreach (var rule in rules)
        {
            if (rule.Rule.Keyword.Length == 0)
            {
                TryAdd(0, text.Length, rule);
                continue;
            }

            var comparison = rule.Rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var from = 0;
            while (from <= text.Length - rule.Rule.Keyword.Length)
            {
                var idx = text.IndexOf(rule.Rule.Keyword, from, comparison);
                if (idx < 0)
                    break;
                TryAdd(idx, rule.Rule.Keyword.Length, rule);
                from = idx + rule.Rule.Keyword.Length;
            }
        }

        return spans ?? [];
    }

    /// <summary>
    /// 片段感知折行：与 <see cref="TextStyle.Wrap"/> 同一套贪心策略（空格优先断行，无空格逐字硬断），
    /// 但每个字符用自己所属片段的样式测宽；区间外字符用基础形态。
    /// 输出按片段归并的视觉行，供混排绘制。
    /// </summary>
    static List<StyledLine> WrapStyled(
        string text,
        List<SegmentSpan>? spans,
        CellLook baseLook,
        TableOptions o,
        TableStylePool pool,
        string elementId,
        double maxWidthMm)
    {
        TextStyle BaseStyle() => pool.Get(baseLook.Family, baseLook.Pt, baseLook.Bold, baseLook.Italic, elementId, baseLook.LetterSpacingPt, text);

        (TextStyle Style, SKColor? Fg, SKColor? Bg, bool Under, bool Strike) CharAt(int i)
        {
            if (spans is not null)
            {
                foreach (var span in spans)
                {
                    if (i >= span.Start && i < span.Start + span.Length)
                    {
                        var style = pool.Get(
                            span.FontFamily ?? baseLook.Family,
                            span.FontSizePt ?? baseLook.Pt,
                            span.Bold ?? baseLook.Bold,
                            span.Italic ?? baseLook.Italic,
                            elementId,
                            span.LetterSpacingPt ?? baseLook.LetterSpacingPt,
                            text);
                        return (style, span.TextColor, span.Background, span.Underline ?? baseLook.Underline, span.Strikethrough ?? baseLook.Strikethrough);
                    }
                }
            }

            return (BaseStyle(), null, null, baseLook.Underline, baseLook.Strikethrough);
        }

        double MeasureRange(int start, int end)
        {
            var width = 0d;
            for (var i = start; i < end; i++)
            {
                width += CharAt(i).Style.MeasureChar(text[i]);
                if (i > start)
                    width += CharAt(i).Style.LetterSpacingMm; // 字间距落在字符之间，与 Measure 的 (n-1) 口径一致
            }
            return width;
        }

        var lines = new List<StyledLine>();

        StyledLine EmptyLine()
        {
            var style = BaseStyle();
            var height = style.LineHeight(o.LineSpacing);
            return new StyledLine([], 0, height, style.BaselineOffset(height));
        }

        void EmitLine(int start, int end)
        {
            while (end > start && text[end - 1] == ' ')
                end--;
            if (end == start)
            {
                lines.Add(EmptyLine());
                return;
            }

            var segments = new List<CellSegment>();
            var width = 0d;
            var height = 0d;
            var i = start;
            while (i < end)
            {
                var (style, fg, bg, under, strike) = CharAt(i);
                var j = i + 1;
                while (j < end)
                {
                    var (nextStyle, nextFg, nextBg, nextUnder, nextStrike) = CharAt(j);
                    if (!ReferenceEquals(nextStyle, style) || !Nullable.Equals(nextFg, fg) || !Nullable.Equals(nextBg, bg)
                        || nextUnder != under || nextStrike != strike)
                        break;
                    j++;
                }

                var segText = text[i..j];
                var segWidth = style.Measure(segText);
                segments.Add(new CellSegment(segText, style, fg, bg, segWidth, under, strike));
                width += segWidth;
                height = Math.Max(height, style.LineHeight(o.LineSpacing));
                i = j;
            }

            // 行内基线一致：取最高片段的基线偏移
            var tallest = segments[0];
            foreach (var seg in segments)
            {
                if (seg.Style.SizeMm > tallest.Style.SizeMm)
                    tallest = seg;
            }

            lines.Add(new StyledLine(segments, width, height, tallest.Style.BaselineOffset(height)));
        }

        if (text.Length == 0)
        {
            lines.Add(EmptyLine());
            return lines;
        }

        // 硬换行切段（\r\n / \r / \n 都算一个断点）
        var paragraphStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\r' || text[i] == '\n')
            {
                var pEnd = i;
                var next = i + 1;
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    next = i + 2;

                if (maxWidthMm <= 0)
                {
                    // 不允许折行：整段一行
                    EmitLine(paragraphStart, pEnd);
                }
                else
                {
                    AppendWrapped(paragraphStart, pEnd);
                }

                paragraphStart = next;
            }
        }

        return lines;

        void AppendWrapped(int pStart, int pEnd)
        {
            if (pStart == pEnd)
            {
                lines.Add(EmptyLine());
                return;
            }

            var start = pStart;
            var width = 0d;
            var lastBreak = -1; // 可断点的下一个起点（空格之后）
            for (var i = pStart; i < pEnd; i++)
            {
                width += CharAt(i).Style.MeasureChar(text[i]);
                if (i > start)
                    width += CharAt(i).Style.LetterSpacingMm;
                if (width > maxWidthMm)
                {
                    int cut;
                    if (lastBreak > start)
                        cut = lastBreak;
                    else
                        cut = i == start ? i + 1 : i; // 无空格可断：超出前的上一字符处硬断
                    EmitLine(start, cut);
                    start = cut;
                    width = MeasureRange(start, i + 1);
                    lastBreak = -1;
                    if (text[i] == ' ')
                        lastBreak = i + 1;
                }
                else if (text[i] == ' ')
                {
                    lastBreak = i + 1;
                }
            }

            if (start < pEnd)
                EmitLine(start, pEnd);
            else if (lines.Count == 0)
                lines.Add(EmptyLine());
        }
    }

    /// <summary>
    /// 汇总行：<see cref="TableSummary.Count"/> 数数据行条数，Sum/Average/Min/Max 只统计能解析成不变文化的数值
    /// （空串、文字一律跳过，不当作 0），Text 输出 <see cref="TableColumn.SummaryLabel"/> 本身。
    /// </summary>
    internal static RowBlock? LayoutFooter(TableOptions o, TableStylePool pool, List<List<string>> rows, List<List<string>> rawRows, int dataStart, double[] widths, string elementId)
    {
        if (!o.Columns.Any(c => c.Summary != TableSummary.None))
            return null;

        var data = rows.Skip(dataStart).ToList();
        // 汇总吃 formatter 改写前的原始值：显示文本被格式化成 ¥3,200.00 / x2 之后就没法解析回数字了
        var rawData = rawRows.Skip(dataStart).ToList();
        var cells = new List<string>[o.Columns.Count];
        var styles = new TextStyle[o.Columns.Count];
        var maxLines = 1;
        var lineHeight = 0d;
        for (var c = 0; c < o.Columns.Count; c++)
        {
            var text = SummaryText(o.Columns[c], rawData, c);
            var style = pool.Get(o.FontFamily, o.Columns[c].FontSizePt ?? o.FontSizePt, bold: false, italic: false, elementId, probeText: text);
            styles[c] = style;
            cells[c] = style.Wrap(text, Inner(widths[c], o), wrap: true);
            maxLines = Math.Max(maxLines, cells[c].Count);
            lineHeight = Math.Max(lineHeight, style.LineHeight(o.LineSpacing));
        }

        var height = Math.Max(maxLines * lineHeight + 2 * o.CellPaddingMm, MinRowHeight(o));
        return new RowBlock(height, cells, styles);
    }

    static string SummaryText(TableColumn column, List<List<string>> data, int index)
    {
        var label = column.SummaryLabel ?? string.Empty;
        var decimals = Math.Clamp(column.SummaryDecimals, 0, 6);
        switch (column.Summary)
        {
            case TableSummary.None:
                return string.Empty;
            case TableSummary.Text:
                return label;
            case TableSummary.Count:
                return label + data.Count.ToString(CultureInfo.InvariantCulture);
            case TableSummary.Sum:
            case TableSummary.Average:
            case TableSummary.Min:
            case TableSummary.Max:
                var values = new List<double>(data.Count);
                foreach (var row in data)
                {
                    if (index < row.Count && double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        values.Add(value);
                }

                if (values.Count == 0)
                    return label;

                var aggregate = column.Summary switch
                {
                    TableSummary.Sum => values.Sum(),
                    TableSummary.Average => values.Average(),
                    TableSummary.Min => values.Min(),
                    _ => values.Max(),
                };

                return label + aggregate.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            default:
                return string.Empty;
        }
    }

    void Paint(
        SKCanvas canvas,
        TableElement element,
        TableOptions o,
        HeaderLayout? header,
        BodyLayout body,
        RowBlock? footer,
        double[] lefts,
        double[] widths,
        HorizontalAlign[] aligns)
    {
        var tableRight = element.XMm + widths.Sum();
        var y = element.YMm;

        if (header is not null)
        {
            if (o.HeaderFillColor is { } hf)
            {
                using var fill = Paints.Fill(hf);
                canvas.DrawRect(new SKRect((float)element.XMm, (float)y, (float)tableRight, (float)(y + header.Height)), fill);
            }

            DrawHeaderCells(canvas, o, header, y);
            y += header.Height;
        }

        var bodyTop = y;

        // 1. 整行底色：条件命中优先于斑马纹；合并格区域的行底色会被下面第 2 步的合并格底色盖成首行口径
        for (var r = 0; r < body.RowHeights.Length; r++)
        {
            var fill = body.RowFills[r] ?? (o.StripeColor is { } stripe && r % 2 == 1 ? stripe : null);
            if (fill is { } color)
            {
                using var fillPaint = Paints.Fill(color);
                canvas.DrawRect(new SKRect((float)element.XMm, (float)y, (float)tableRight, (float)(y + body.RowHeights[r])), fillPaint);
            }

            y += body.RowHeights[r];
        }

        // 2. 单元格底色：条件限定列命中的格 / 合并格（底色 = 组首行的格底色，其次行底色）
        foreach (var cell in body.Cells)
        {
            if (cell.Background is not { } bg)
                continue;
            using var paint = Paints.Fill(bg);
            canvas.DrawRect(new SKRect((float)cell.X, (float)(bodyTop + cell.Top), (float)(cell.X + cell.Width), (float)(bodyTop + cell.Top + cell.Height)), paint);
        }

        // 3. 文本：普通格与合并格统一走混排绘制
        foreach (var cell in body.Cells)
            DrawBodyCell(canvas, o, cell, bodyTop);

        if (footer is not null)
        {
            DrawFooterCells(canvas, o, footer, lefts, widths, y, aligns);
            y += footer.Height;
        }

        if (o.GridColor is { } gridColor && o.GridWidthMm > 0)
        {
            if (header is not null)
                PaintHeaderGrid(canvas, o, gridColor, header, lefts, widths, tableRight, element.YMm);

            PaintBodyGrid(canvas, o, gridColor, body, footer, bodyTop, y, element.XMm, tableRight, lefts);
        }
    }

    /// <summary>画表头文本。每格单独裁剪，合并单元格内部不画竖线由 <see cref="PaintHeaderGrid"/> 处理。</summary>
    static void DrawHeaderCells(SKCanvas canvas, TableOptions o, HeaderLayout header, double headerTop)
    {
        foreach (var cell in header.Cells)
        {
            var top = headerTop + cell.Top;
            var saved = canvas.Save();
            canvas.ClipRect(new SKRect((float)cell.X, (float)top, (float)(cell.X + cell.Width), (float)(top + cell.Height)));

            // 表头格底色叠在整行 HeaderFillColor 之上（表头格样式的 BackgroundColor）
            if (cell.Background is { } bg)
            {
                using var bgPaint = Paints.Fill(bg);
                canvas.DrawRect(new SKRect((float)cell.X, (float)top, (float)(cell.X + cell.Width), (float)(top + cell.Height)), bgPaint);
            }

            var color = cell.TextColor ?? o.TextColor ?? SKColors.Black;
            var innerLeft = cell.X + o.CellPaddingMm;
            var innerWidth = Math.Max(0, cell.Width - 2 * o.CellPaddingMm);
            var lineHeight = cell.Style.LineHeight(o.LineSpacing);
            var block = cell.Lines.Count * lineHeight;
            var textTop = top + (cell.Height - block) / 2;
            var baselineOffset = cell.Style.BaselineOffset(lineHeight);
            using var decorationPaint = cell.Underline || cell.Strikethrough
                ? Paints.Stroke(color, Math.Max(0.05, cell.Style.SizeMm * 0.06), dashMm: null)
                : null;
            for (var i = 0; i < cell.Lines.Count; i++)
            {
                var baseline = textTop + i * lineHeight + baselineOffset;
                var x = cell.Style.AlignedX(innerLeft, innerWidth, cell.Lines[i], cell.Align);
                cell.Style.DrawLine(canvas, cell.Lines[i], x, baseline, color);

                if (decorationPaint is null || cell.Lines[i].Length == 0)
                    continue;

                var width = cell.Style.Measure(cell.Lines[i]);
                if (cell.Underline)
                {
                    var y = baseline + cell.Style.SizeMm * 0.12;
                    canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
                }

                if (cell.Strikethrough)
                {
                    var y = baseline - cell.Style.SizeMm * 0.3;
                    canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
                }
            }

            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>
    /// 画一个表体格（普通或合并）：片段底色矩形 + 混排文本。
    /// 每格整体裁剪，长内容只压垮自己那一格，不侵入邻格。
    /// </summary>
    static void DrawBodyCell(SKCanvas canvas, TableOptions o, BodyCellLayout cell, double bodyTop)
    {
        var saved = canvas.Save();
        canvas.ClipRect(new SKRect((float)cell.X, (float)(bodyTop + cell.Top), (float)(cell.X + cell.Width), (float)(bodyTop + cell.Top + cell.Height)));

        var innerLeft = cell.X + o.CellPaddingMm;
        var innerWidth = Math.Max(0, cell.Width - 2 * o.CellPaddingMm);
        var block = 0d;
        foreach (var line in cell.Lines)
            block += line.Height;
        var top = bodyTop + cell.Top + (cell.Height - block) / 2;

        foreach (var line in cell.Lines)
        {
            var x = cell.Align switch
            {
                HorizontalAlign.Center => innerLeft + (innerWidth - line.Width) / 2,
                HorizontalAlign.Right => innerLeft + innerWidth - line.Width,
                _ => innerLeft,
            };
            var baseline = top + line.BaselineOffset;

            foreach (var seg in line.Segments)
            {
                if (seg.Background is { } bg)
                {
                    using var bgPaint = Paints.Fill(bg);
                    canvas.DrawRect(new SKRect((float)x, (float)top, (float)(x + seg.Width), (float)(top + line.Height)), bgPaint);
                }

                // 字色链：片段（关键词）→ 条件 → 列样式 → 表级（内联/样式库）→ 黑
                var color = seg.TextColor ?? cell.TextColor ?? o.TextColor ?? SKColors.Black;
                if (seg.Text.Length > 0)
                    seg.Style.DrawLine(canvas, seg.Text, x, baseline, color);

                if (seg.Underline || seg.Strikethrough)
                {
                    using var decorationPaint = Paints.Stroke(color, Math.Max(0.05, seg.Style.SizeMm * 0.06), dashMm: null);
                    if (seg.Underline)
                    {
                        var y = baseline + seg.Style.SizeMm * 0.12;
                        canvas.DrawLine((float)x, (float)y, (float)(x + seg.Width), (float)y, decorationPaint);
                    }

                    if (seg.Strikethrough)
                    {
                        var y = baseline - seg.Style.SizeMm * 0.3;
                        canvas.DrawLine((float)x, (float)y, (float)(x + seg.Width), (float)y, decorationPaint);
                    }
                }

                x += seg.Width;
            }

            top += line.Height;
        }

        canvas.RestoreToCount(saved);
    }

    /// <summary>画汇总行文本：无条件样式，逐列复用折行时定稿的样式（分页测量与绘制同一实例，字体口径不分叉）。</summary>
    static void DrawFooterCells(
        SKCanvas canvas,
        TableOptions o,
        RowBlock row,
        double[] lefts,
        double[] widths,
        double rowTop,
        HorizontalAlign[] aligns)
    {
        for (var c = 0; c < row.Cells.Length; c++)
        {
            var style = row.Styles[c];
            var cellLeft = lefts[c];
            var innerLeft = cellLeft + o.CellPaddingMm;
            var innerWidth = Math.Max(0, widths[c] - 2 * o.CellPaddingMm);

            var saved = canvas.Save();
            canvas.ClipRect(new SKRect((float)cellLeft, (float)rowTop, (float)(cellLeft + widths[c]), (float)(rowTop + row.Height)));

            var lineHeight = style.LineHeight(o.LineSpacing);
            var block = row.Cells[c].Count * lineHeight;
            var top = rowTop + (row.Height - block) / 2;
            var baselineOffset = style.BaselineOffset(lineHeight);
            for (var i = 0; i < row.Cells[c].Count; i++)
            {
                var baseline = top + i * lineHeight + baselineOffset;
                var x = style.AlignedX(innerLeft, innerWidth, row.Cells[c][i], aligns[c]);
                style.DrawLine(canvas, row.Cells[c][i], x, baseline, o.TextColor ?? SKColors.Black);
            }

            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>
    /// 表头网格线：横线按表头行边界画满整宽；竖线只在"相邻两格不是同一个合并单元格"的位置画，
    /// 否则合并单元格内部会被切出一道不该存在的线。
    /// </summary>
    static void PaintHeaderGrid(SKCanvas canvas, TableOptions o, SKColor color, HeaderLayout header, double[] lefts, double[] widths, double tableRight, double headerTop)
    {
        using var paint = Paints.Stroke(color, o.GridWidthMm, null);
        var rowCount = header.RowHeights.Length;
        var colCount = widths.Length;

        // 横线逐列画：上下两侧属于同一个 rowspan 单元格的列段要跳过，
        // 否则合并单元格中间会被横穿一道线。
        for (var r = 0; r <= rowCount; r++)
        {
            var y = headerTop + (r < rowCount ? header.RowTops[r] : header.Height);
            for (var c = 0; c < colCount; c++)
            {
                var above = r > 0 ? header.Grid[r - 1, c] : null;
                var below = r < rowCount ? header.Grid[r, c] : null;
                if (r != 0 && ReferenceEquals(above, below))
                    continue;

                var x2 = c + 1 < colCount ? lefts[c + 1] : tableRight;
                canvas.DrawLine((float)lefts[c], (float)y, (float)x2, (float)y, paint);
            }
        }

        for (var r = 0; r < rowCount; r++)
        {
            var top = headerTop + header.RowTops[r];
            var bottom = top + header.RowHeights[r];
            for (var b = 0; b <= colCount; b++)
            {
                var leftCell = b > 0 ? header.Grid[r, b - 1] : null;
                var rightCell = b < colCount ? header.Grid[r, b] : null;
                if (b != 0 && b != colCount && ReferenceEquals(leftCell, rightCell))
                    continue;

                var x = b < colCount ? lefts[b] : tableRight;
                canvas.DrawLine((float)x, (float)top, (float)x, (float)bottom, paint);
            }
        }
    }

    /// <summary>表体网格线：与表头同一套占用网格逻辑——合并单元格内部不画线。</summary>
    static void PaintBodyGrid(
        SKCanvas canvas,
        TableOptions o,
        SKColor color,
        BodyLayout body,
        RowBlock? footer,
        double bodyTop,
        double tableBottom,
        double tableLeft,
        double tableRight,
        double[] lefts)
    {
        using var paint = Paints.Stroke(color, o.GridWidthMm, null);
        var rowCount = body.RowHeights.Length;
        var colCount = o.Columns.Count;

        // 横线：上下两格同属一个合并格的列段跳过
        for (var r = 0; r <= rowCount; r++)
        {
            // RowTops 是表体内相对值要加 bodyTop；tableBottom 已是绝对值，不能再叠加
            var y = r < rowCount ? bodyTop + body.RowTops[r] : tableBottom - (footer?.Height ?? 0);
            for (var c = 0; c < colCount; c++)
            {
                var above = r > 0 ? body.Grid[r - 1, c] : null;
                var below = r < rowCount ? body.Grid[r, c] : null;
                if (r != 0 && r != rowCount && ReferenceEquals(above, below))
                    continue;

                var x2 = c + 1 < colCount ? lefts[c + 1] : tableRight;
                canvas.DrawLine((float)lefts[c], (float)y, (float)x2, (float)y, paint);
            }
        }

        // 竖线：左右两格同属一个合并格的行段跳过
        if (rowCount == 0)
        {
            for (var b = 0; b <= colCount; b++)
            {
                var x = b < colCount ? lefts[b] : tableRight;
                canvas.DrawLine((float)x, (float)bodyTop, (float)x, (float)tableBottom, paint);
            }
        }
        else
        {
            for (var r = 0; r < rowCount; r++)
            {
                var top = bodyTop + body.RowTops[r];
                var bottom = top + body.RowHeights[r];
                for (var b = 0; b <= colCount; b++)
                {
                    var leftCell = b > 0 ? body.Grid[r, b - 1] : null;
                    var rightCell = b < colCount ? body.Grid[r, b] : null;
                    if (b != 0 && b != colCount && ReferenceEquals(leftCell, rightCell))
                        continue;

                    var x = b < colCount ? lefts[b] : tableRight;
                    canvas.DrawLine((float)x, (float)top, (float)x, (float)bottom, paint);
                }
            }
        }

        // 汇总行底线（汇总行顶线就是表体最后一行底线）
        if (footer is not null)
            canvas.DrawLine((float)tableLeft, (float)tableBottom, (float)tableRight, (float)tableBottom, paint);

        // 外框比网格线粗一倍，让表格边界在面单上一眼可辨
        if (o.ShowBorder)
        {
            using var border = Paints.Stroke(color, o.GridWidthMm * 2, null);
            canvas.DrawRect(new SKRect((float)tableLeft, (float)bodyTop, (float)tableRight, (float)tableBottom), border);
        }
    }

    static double Inner(double columnWidthMm, TableOptions o) => Math.Max(0, columnWidthMm - 2 * o.CellPaddingMm);

    /// <summary>
    /// 表级样式解析：把 <see cref="TableOptions.StyleId"/> 引用的样式落到表级字段的默认位上
    /// （字体族/字号/行距是哨兵口径——内联等于模型默认值视为未设置；字色 null 即未设置）。
    /// 返回解析后的副本，原模板不被修改。测量（分页）与正式渲染共用，保证同一口径。
    /// </summary>
    internal static TableOptions ResolveTableOptions(TableOptions inline, RenderContext context)
    {
        var style = StyleCascade.Find(context, inline.StyleId);
        if (style is null)
            return inline;

        var o = inline.Clone();
        if (style.FontFamily is not null && o.FontFamily == "Microsoft YaHei")
            o.FontFamily = style.FontFamily;
        if (style.FontSizePt is { } pt && o.FontSizePt == 9)
            o.FontSizePt = pt;
        if (style.LineSpacing is { } lineSpacing && o.LineSpacing == 1.2)
            o.LineSpacing = lineSpacing;
        o.TextColor ??= style.TextColor;
        return o;
    }

    /// <summary>列对齐级联：列样式的 HorizontalAlign 在内联 Align 等于模型默认值（Left）时补位。</summary>
    static HorizontalAlign[] ResolveAligns(TableOptions o, RenderContext context)
    {
        var aligns = new HorizontalAlign[o.Columns.Count];
        for (var c = 0; c < aligns.Length; c++)
        {
            var style = StyleCascade.Find(context, o.Columns[c].StyleId);
            aligns[c] = StyleCascade.Pick(o.Columns[c].Align, HorizontalAlign.Left, style?.HorizontalAlign);
        }

        return aligns;
    }

    /// <summary>片段规则解析：可空载荷被其 StyleId 指向的样式补位；无样式引用的规则原样保留。</summary>
    static ResolvedHighlight[] ResolveHighlights(TableOptions o, RenderContext context)
    {
        var list = new ResolvedHighlight[o.Highlights.Count];
        for (var i = 0; i < list.Length; i++)
        {
            var rule = o.Highlights[i];
            var st = StyleCascade.Find(context, rule.StyleId);
            list[i] = st is null
                ? new ResolvedHighlight(rule, null, null, null)
                : new ResolvedHighlight(
                    new TableHighlightRule
                    {
                        StyleId = rule.StyleId,
                        Keyword = rule.Keyword,
                        Column = rule.Column,
                        IgnoreCase = rule.IgnoreCase,
                        BackgroundColor = rule.BackgroundColor ?? st.BackgroundColor,
                        TextColor = rule.TextColor ?? st.TextColor,
                        Bold = rule.Bold ?? st.Bold,
                        Italic = rule.Italic ?? st.Italic,
                        FontSizePt = rule.FontSizePt ?? st.FontSizePt,
                        FontFamily = rule.FontFamily ?? st.FontFamily,
                    },
                    st.Underline, st.Strikethrough, st.LetterSpacingPt);
        }

        return list;
    }

    /// <summary>条件规则解析：可空载荷被其 StyleId 指向的样式补位；无样式引用的规则原样保留。</summary>
    static ResolvedCondition[] ResolveConditions(TableOptions o, RenderContext context)
    {
        var list = new ResolvedCondition[o.ConditionalStyles.Count];
        for (var i = 0; i < list.Length; i++)
        {
            var rule = o.ConditionalStyles[i];
            var st = StyleCascade.Find(context, rule.StyleId);
            list[i] = st is null
                ? new ResolvedCondition(rule, null, null, null)
                : new ResolvedCondition(
                    new TableConditionalStyle
                    {
                        StyleId = rule.StyleId,
                        Field = rule.Field,
                        Operator = rule.Operator,
                        Value = rule.Value,
                        Column = rule.Column,
                        BackgroundColor = rule.BackgroundColor ?? st.BackgroundColor,
                        TextColor = rule.TextColor ?? st.TextColor,
                        Bold = rule.Bold ?? st.Bold,
                        Italic = rule.Italic ?? st.Italic,
                        FontSizePt = rule.FontSizePt ?? st.FontSizePt,
                        FontFamily = rule.FontFamily ?? st.FontFamily,
                    },
                    st.Underline, st.Strikethrough, st.LetterSpacingPt);
        }

        return list;
    }

    /// <summary>
    /// 分页规划用的内容测量（<see cref="TemplatePaginator"/> 专用）：量出每条**数据行**的真实行高
    /// 与每页固定高度（表头 + 静态行 + 汇总行）。绑定告警进 <paramref name="context"/>.Report——
    /// 调用方应传一次性报告，规划期告警不该混进正式渲染（正式渲染会重新绑定一遍）。
    /// </summary>
    /// <returns>false 表示没有可分页的数据行（无列、无集合行），分页无从谈起。</returns>
    internal static bool TryMeasurePages(
        TableElement element,
        RenderContext context,
        out double fixedHeightMm,
        out List<double> dataRowHeightsMm)
    {
        fixedHeightMm = 0;
        dataRowHeightsMm = new List<double>();
        // 与正式渲染同一口径：表级/列级样式会影响行高，分页测量必须先解析
        var o = ResolveTableOptions(element.Table, context);
        if (o.Columns.Count == 0)
            return false;

        var widths = ResolveWidths(o, element.WidthMm);
        var lefts = new double[widths.Length];
        for (var c = 0; c < widths.Length; c++)
            lefts[c] = element.XMm + widths.Take(c).Sum();

        var rows = ResolveRows(o, context, element.Id, element.TestData, out var dataStart, out var dataItems, out var rawRows);
        var aligns = ResolveAligns(o, context);
        using var pool = new TableStylePool(context);
        try
        {
            var header = o.ShowHeader ? LayoutHeader(o, lefts, widths, pool, element.Id, aligns) : null;
            var body = LayoutBody(o, rows, dataStart, dataItems, lefts, widths, pool, element.Id, aligns);
            var footer = LayoutFooter(o, pool, rows, rawRows, dataStart, widths, element.Id);

            // 静态行每页重复，是固定开销；数据行才是分页切分的对象
            fixedHeightMm = (header?.Height ?? 0)
                + body.RowHeights.Take(dataStart).Sum()
                + (footer?.Height ?? 0);
            for (var r = dataStart; r < body.RowHeights.Length; r++)
                dataRowHeightsMm.Add(body.RowHeights[r]);

            return dataRowHeightsMm.Count > 0;
        }
        finally
        {
            pool.Dispose();
        }
    }

    static double MinRowHeight(TableOptions o, double? fontSizePt = null)
        => UnitConverter.PtToMm(fontSizePt ?? o.FontSizePt) * Math.Max(o.LineSpacing, 1) + 2 * o.CellPaddingMm;

    /// <summary>
    /// 列宽解析：显式 mm 相加，剩余宽度由 <c>widthMm = -1</c> 的列均分。
    /// 均分列不存在而显式宽度之和超出元素宽时**不缩放**——多出来的部分被元素裁剪，
    /// 同时由 <see cref="TemplateRenderer"/> 的越界告警提示，比悄悄把用户设定的 mm 改掉诚实。
    /// </summary>
    internal static double[] ResolveWidths(TableOptions o, double totalWidthMm)
    {
        var widths = new double[o.Columns.Count];
        var fixedSum = 0d;
        var autoCount = 0;
        for (var i = 0; i < o.Columns.Count; i++)
        {
            if (o.Columns[i].WidthMm < 0)
                autoCount++;
            else
            {
                widths[i] = o.Columns[i].WidthMm;
                fixedSum += widths[i];
            }
        }

        if (autoCount > 0)
        {
            var each = Math.Max(0, totalWidthMm - fixedSum) / autoCount;
            for (var i = 0; i < widths.Length; i++)
            {
                if (o.Columns[i].WidthMm < 0)
                    widths[i] = each;
            }
        }

        return widths;
    }

    /// <summary>静态行 + 数据行；<paramref name="dataStart"/> 是第一条数据行的下标（前面是静态行）。</summary>
    /// <param name="o">表格选项。</param>
    /// <param name="context">渲染上下文。</param>
    /// <param name="elementId">元素 id（告警定位）。</param>
    /// <param name="testData">元素级测试数据：数据绑定解析不到集合时，按「换行分行、制表符分列」展开成测试数据行
    /// （计入 <paramref name="dataStart"/> 之后，参与尾行汇总）。为空或绑定有值时不参与。</param>
    /// <param name="dataStart">输出：第一条数据行（含集合行）在结果里的下标。</param>
    /// <summary>汇总计算用原始值行（formatter 改写前）：与 rows 同形状、同下标。</summary>
    internal static List<List<string>> ResolveRows(TableOptions o, RenderContext context, string elementId, string? testData, out int dataStart)
        => ResolveRows(o, context, elementId, testData, out dataStart, out _, out _);

    /// <summary><see cref="ResolveRows(TableOptions, RenderContext, string, string?, out int)"/> 的完整形态：
    /// 额外输出实际参与渲染的集合行来源（设计态测试数据盖过集合时为 null），供条件样式求值。</summary>
    /// <param name="o">表格选项。</param>
    /// <param name="context">渲染上下文。</param>
    /// <param name="elementId">元素 id（告警定位）。</param>
    /// <param name="testData">元素级测试数据，语义同简化重载。</param>
    /// <param name="dataStart">输出：第一条数据行（含集合行）在结果里的下标。</param>
    /// <param name="dataItems">输出：集合行来源；null 表示本次没有渲染任何集合行。</param>
    /// <param name="rawRows">输出：formatter 改写前的原始单元格文本（与结果同形状）。尾行汇总用它求和，
    /// 否则金额列格式化成 <c>¥3,200.00</c> 之后 Sum 就解析不出数字了。</param>
    internal static List<List<string>> ResolveRows(TableOptions o, RenderContext context, string elementId, string? testData, out int dataStart, out IReadOnlyList<object?>? dataItems, out List<List<string>> rawRows)
    {
        var rows = new List<List<string>>();
        var raws = new List<List<string>>();
        var colCount = o.Columns.Count;

        foreach (var raw in o.Rows)
        {
            var line = new List<string>(colCount);
            for (var c = 0; c < colCount; c++)
                line.Add(DataBinder.BindText(c < raw.Count ? raw[c] : string.Empty, context.Data, context.Report, elementId, context: context));
            rows.Add(line);
            raws.Add(line);
        }

        dataStart = rows.Count;
        var explicitField = !string.IsNullOrWhiteSpace(o.Field);
        var items = BoundRowSource(o, context);

        // 设计画布：测试数据优先——用户输入了测试行就显示测试行，盖过宿主/设计数据集合，
        // 否则"输入测试数据看效果"在有真实数据的模板上永远看不到变化。
        if (context.PreferTestData && !string.IsNullOrWhiteSpace(testData))
            items = null;

        // 表格跨页：命中本元素的分页窗口时，数据行只取 [Start, Start + Count)。
        // 静态行不参与窗口（每页照常重复）；退化窗口（Count ≤ 0）按"没有窗口"处理。
        TableWindow? window = null;
        if (context.TableWindows is { } windows && windows.TryGetValue(elementId, out var win) && win.Count > 0)
            window = win;

        if (window is { } page && items is not null)
        {
            var start = Math.Clamp(page.Start, 0, items.Count);
            var count = Math.Clamp(page.Count, 0, items.Count - start);
            if (start > 0 || count < items.Count)
            {
                var sliced = new List<object?>(count);
                for (var i = start; i < start + count; i++)
                    sliced.Add(items[i]);
                items = sliced;
            }
        }

        dataItems = items;

        if (items is not null)
        {
            var dataIndex = 0;
            foreach (var item in items)
            {
                var line = new List<string>(colCount);
                var rawLine = new List<string>(colCount);
                foreach (var col in o.Columns)
                {
                    if (string.IsNullOrWhiteSpace(col.Field))
                    {
                        line.Add(string.Empty);
                        rawLine.Add(string.Empty);
                        continue;
                    }

                    // 显式 TableOptions.Field 时列字段就是行对象上的路径（如 classTime）；
                    // 自动推断时列字段带公共首段（items.sku），首段已被集合本身消费，要剥掉。
                    var path = explicitField ? col.Field : DataBinder.StripFirstSegment(col.Field);
                    object? value;
                    if (path.Length == 0)
                        value = item;
                    else
                        DataBinder.TryResolve(item, path, out value);
                    var text = DataBinder.ToString(value);
                    rawLine.Add(text);

                    // 列 formatter（hiprint 同款）：function(value, row, index, options)，返回值替换单元格文本
                    if (ScriptSupport.ColumnFormatterResult(context, col, value, item, dataIndex, elementId) is { } formatted)
                        text = DataBinder.ToString(formatted);

                    line.Add(text);
                }

                rows.Add(line);
                raws.Add(rawLine);
                dataIndex++;
            }
        }
        else if (rows.Count == 0 && !string.IsNullOrWhiteSpace(testData))
        {
            // 元素级测试数据行：页面上一行内容都没有（静态行为空、集合也解析不到）时，用它占位看排版效果。
            // 静态行已经让表格非空就不再叠加——否则既有内容的表格会被测试数据行撑爆（内容重复 + TableOverflow）。
            // 单元格取字面值——用户敲进来的就是要看的内容，再做占位符替换反而看不见输入。
            foreach (var rawLine in testData.Split('\n'))
            {
                var lineText = rawLine.TrimEnd('\r');
                if (lineText.Length == 0)
                    continue;

                var cells = lineText.Split('\t');
                var row = new List<string>(colCount);
                for (var c = 0; c < colCount; c++)
                    row.Add(c < cells.Length ? cells[c] : string.Empty);
                rows.Add(row);
                raws.Add(row);
            }
        }

        if (o.Columns.Any(c => c.Sequence))
        {
            // 跨页时序号接着上一页排：数据行全局序号 = dataStart + 窗口起点 + 页内偏移 + 1；
            // 无窗口（offset 0）时公式退化为原来的 r + 1，静态行序号不受窗口影响。
            var sequenceOffset = window is { } seqPage ? seqPage.Start : 0;
            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < o.Columns.Count; c++)
                {
                    if (o.Columns[c].Sequence)
                    {
                        var seq = (r < dataStart
                            ? r + 1
                            : dataStart + sequenceOffset + (r - dataStart) + 1)
                            .ToString(CultureInfo.InvariantCulture);
                        rows[r][c] = seq;
                        raws[r][c] = seq;
                    }
                }
            }
        }

        rawRows = raws;
        return rows;
    }

    /// <summary>
    /// 行集合：优先用显式 <see cref="TableOptions.Field"/>；为空时才由列字段路径的公共首段推断。
    /// 列之间首段不一致时返回 null——把两个不相干的集合按行号硬配对，
    /// 比"什么都不多画"更容易印出一张看着没错的错单。
    /// </summary>
    static IReadOnlyList<object?>? BoundRowSource(TableOptions o, RenderContext context)
    {
        if (!string.IsNullOrWhiteSpace(o.Field))
            return DataBinder.TryResolveCollection(context.Data, o.Field, out var explicitRows) ? explicitRows : null;

        string? shared = null;
        foreach (var col in o.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.Field))
                continue;

            var head = DataBinder.FirstSegment(col.Field);
            if (head.Length == 0)
                continue;

            if (shared is null)
                shared = head;
            else if (!string.Equals(shared, head, StringComparison.Ordinal))
                return null;
        }

        return shared is not null && DataBinder.TryResolveCollection(context.Data, shared, out var items) ? items : null;
    }
}
