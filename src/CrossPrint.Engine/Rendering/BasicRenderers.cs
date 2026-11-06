using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>文本元素渲染器。度量、折行与对齐全部委托 <see cref="TextStyle"/>。</summary>
public sealed class TextRenderer : ElementRenderer<TextElement>
{
    /// <summary>单个文本元素参与度量的最大字符数（固定值，与元素宽高/纸张无关）。</summary>
    public const int MaxContentChars = 200_000;

    /// <summary>单个文本元素最多保留的折行行数（固定值）。</summary>
    public const int MaxLines = 20_000;

    /// <summary>单行最大字符数（固定值，防"一整段无换行"把 shaping 拖死）。</summary>
    public const int MaxCharsPerLine = 8_000;

    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Text;

    /// <inheritdoc/>
    /// <remarks>
    /// hiprint 的文本元素没有 overflow:hidden，溢出的标点下沿和字形降部照常打印。
    /// 实测：13pt 表头的"、"整个落在元素框下沿之外，裁掉后只剩一个几乎看不见的点；
    /// 18pt 的"NO"在框底被切掉半截。因此文本不再裁剪，超框属于模板设计问题，交给页面边界处理。
    /// </remarks>
    protected override bool ClipsToElement => false;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, TextElement element, RenderContext context)
    {
        using var block = BuildBlock(element, context);
        if (block is null)
            return;

        var style = block.Style;
        var lines = block.Lines;
        var innerLeft = block.InnerLeft;
        var innerWidth = block.InnerWidth;
        var lineHeight = block.LineHeight;
        var hAlign = block.HorizontalAlign;
        var color = block.Color;

        var blockHeight = lines.Count * lineHeight;
        var top = block.VerticalAlign switch
        {
            VerticalAlign.Middle => block.InnerTop + (block.InnerHeight - blockHeight) / 2,
            VerticalAlign.Bottom => block.InnerTop + block.InnerHeight - blockHeight,
            _ => block.InnerTop,
        };
        var baselineOffset = style.BaselineOffset(lineHeight);

        var firstIndentExtra = block.FirstLineIndentMm;
        using var decorationPaint = block.Underline || block.Strikethrough || block.Overline
            ? Paints.Stroke(color, Math.Max(0.05, style.SizeMm * 0.06), dashMm: null)
            : null;

        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = top + i * lineHeight + baselineOffset;
            var justify = hAlign == HorizontalAlign.Justify && i < lines.Count - 1;
            var extra = justify ? JustifySpacing(style, lines[i], innerWidth) : 0;
            var x = hAlign == HorizontalAlign.Justify
                ? innerLeft
                : style.AlignedX(innerLeft, innerWidth, lines[i], hAlign);
            if (i == 0)
                x += firstIndentExtra;
            style.DrawLine(canvas, lines[i], x, baseline, color, extra);

            if (decorationPaint is null || lines[i].Length == 0)
                continue;

            var width = style.Measure(lines[i]);
            if (block.Underline)
            {
                var y = baseline + style.SizeMm * 0.12;
                canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
            }

            if (block.Strikethrough)
            {
                var y = baseline - style.SizeMm * 0.3;
                canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
            }

            if (block.Overline)
            {
                var y = top + i * lineHeight + Math.Max(0.15, style.SizeMm * 0.1);
                canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
            }
        }
    }

    /// <summary>
    /// 一次性算出"这个文本元素最终会画成什么样"：绑定并格式化后的内容、级联后的字体/对齐/内边距、
    /// 可排区域、行高与折行结果。绘制与分页度量共用这条路径，避免两处各写一份级联逻辑后悄悄漂移。
    /// </summary>
    /// <returns>没有可画内容（空串）时返回 null；调用方负责 Dispose。</returns>
    internal static TextBlock? BuildBlock(TextElement element, RenderContext context)
    {
        var o = element.Text;
        // 元素带 binding（hiprint field 语义）时内容优先取数据源该字段。
        var content = DataBinder.BindContent(o.Content, element.Binding, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context);
        // formatter（hiprint 同款）：返回值替换显示内容；无脚本/降级保留原文本
        content = ScriptSupport.FormatContent(context, element, o.Content, content);
        if (content.Length == 0)
            return null;

        // 固定护栏：绑定/脚本可能塞进"雷霆大文本"（几 MB 文字），Wrap 会逐字测量并生成
        // 几十万行 Line 对象，度量（分页器）与渲染都扛不住。截断在度量前统一发生，
        // 绘制与分页器（MeasureLongTexts 也走 BuildBlock）拿到的是同一份内容，不会两边漂移。
        if (content.Length > MaxContentChars)
        {
            var original = content.Length;
            content = content[..MaxContentChars];
            context.Report.Add(element.Id, RenderIssueKind.TextTooLong,
                $"文本 {original} 字符超过固定上限 {MaxContentChars}，已截断（元素 {element.Id}）。");
        }

        // 样式库级联：库样式打底、内联覆盖（非空内联字段等于模型默认值视为未设置，哨兵口径）
        var named = StyleCascade.Find(context, o.StyleId);
        var fontFamily = StyleCascade.Pick(o.FontFamily, "Microsoft YaHei", named?.FontFamily);
        var fontSizePt = StyleCascade.Pick(o.FontSizePt, 12, named?.FontSizePt);
        var bold = StyleCascade.Pick(o.Bold, false, named?.Bold);
        var italic = StyleCascade.Pick(o.Italic, false, named?.Italic);
        var underline = StyleCascade.Pick(o.Underline, false, named?.Underline);
        var strikethrough = StyleCascade.Pick(o.Strikethrough, false, named?.Strikethrough);
        var overline = StyleCascade.Pick(o.Overline, false, named?.Overline);
        var indentMm = StyleCascade.Pick(o.IndentMm, 0, named?.IndentMm);
        var firstLineIndentMm = StyleCascade.Pick(o.FirstLineIndentMm, 0, named?.FirstLineIndentMm);
        var letterSpacingPt = StyleCascade.Pick(o.LetterSpacingPt, 0, named?.LetterSpacingPt);
        var lineSpacing = StyleCascade.Pick(o.LineSpacing, 1.2, named?.LineSpacing);
        var color = StyleCascade.Pick(o.Color, SKColors.Black, named?.TextColor);
        var hAlign = StyleCascade.Pick(o.HorizontalAlign, HorizontalAlign.Left, named?.HorizontalAlign);
        var vAlign = StyleCascade.Pick(o.VerticalAlign, VerticalAlign.Top, named?.VerticalAlign);
        var padLeft = StyleCascade.Pick(o.PaddingLeftMm, 0, named?.PaddingLeftMm);
        var padTop = StyleCascade.Pick(o.PaddingTopMm, 0, named?.PaddingTopMm);
        var padRight = StyleCascade.Pick(o.PaddingRightMm, 0, named?.PaddingRightMm);
        var padBottom = StyleCascade.Pick(o.PaddingBottomMm, 0, named?.PaddingBottomMm);

        // styler 逐字段覆盖级联结果（脚本 > 内联 > 样式库）；未出现的字段保持不变
        var script = ScriptSupport.StyleOf(context, element, o.Content);
        fontSizePt = script?.FontSizePt ?? fontSizePt;
        bold = script?.Bold ?? bold;
        italic = script?.Italic ?? italic;
        underline = script?.Underline ?? underline;
        strikethrough = script?.Strikethrough ?? strikethrough;
        letterSpacingPt = script?.LetterSpacingPt ?? letterSpacingPt;
        lineSpacing = script?.LineSpacing ?? lineSpacing;
        color = script?.Color ?? color;
        hAlign = script?.HorizontalAlign ?? hAlign;
        vAlign = script?.VerticalAlign ?? vAlign;
        padLeft = script?.PaddingLeftMm ?? padLeft;
        padTop = script?.PaddingTopMm ?? padTop;
        padRight = script?.PaddingRightMm ?? padRight;
        padBottom = script?.PaddingBottomMm ?? padBottom;

        var style = new TextStyle(context, fontFamily, fontSizePt, bold, italic, content, element.Id);
        style.LetterSpacingMm = Math.Max(0, context.PtToUnits(letterSpacingPt));

        // 内边距把可排区域从元素框里缩出来：hiprint 的 contentPadding 是文本真正能占的盒子，
        // 对齐、换行、垂直居中都以它为准，而不是元素框。
        var leftPad = Math.Max(0, padLeft);
        var indent = Math.Max(0, indentMm);
        var innerLeft = element.XMm + leftPad + indent;
        var innerTop = element.YMm + Math.Max(0, padTop);
        var innerWidth = Math.Max(0, element.WidthMm - leftPad - Math.Max(0, padRight) - indent);
        var innerHeight = Math.Max(0, element.HeightMm - Math.Max(0, padTop) - Math.Max(0, padBottom));

        var lineHeight = style.LineHeight(lineSpacing);
        var lines = style.Wrap(content, innerWidth, o.Wrap);

        // 行数与单行长度护栏：Wrap 后的结果才是实际要 shape/绘制的量，两道都卡在固定值上。
        if (lines.Count > MaxLines)
        {
            var originalLines = lines.Count;
            lines = lines.GetRange(0, MaxLines);
            context.Report.Add(element.Id, RenderIssueKind.TextTooLong,
                $"文本折行 {originalLines} 行超过固定上限 {MaxLines} 行，已截断（元素 {element.Id}）。");
        }

        var longLine = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length <= MaxCharsPerLine)
                continue;
            lines[i] = lines[i][..MaxCharsPerLine];
            longLine = true;
        }
        if (longLine)
        {
            context.Report.Add(element.Id, RenderIssueKind.TextTooLong,
                $"存在超过 {MaxCharsPerLine} 字符的超长行，已截断到该长度（元素 {element.Id}）。");
        }

        if (!o.Wrap && o.Ellipsis)
        {
            for (var i = 0; i < lines.Count; i++)
                lines[i] = style.Truncate(lines[i], innerWidth);
        }

        return new TextBlock
        {
            Style = style,
            Content = content,
            Lines = lines,
            LineHeight = lineHeight,
            InnerLeft = innerLeft,
            InnerTop = innerTop,
            InnerWidth = innerWidth,
            InnerHeight = innerHeight,
            HorizontalAlign = hAlign,
            VerticalAlign = vAlign,
            Color = color,
            Underline = underline,
            Strikethrough = strikethrough,
            Overline = overline,
            FirstLineIndentMm = Math.Max(0, firstLineIndentMm),
        };
    }

    /// <summary>文本排版结果（绘制与分页度量共用）。持有 <see cref="TextStyle"/>，用后必须 Dispose。</summary>
    internal sealed class TextBlock : IDisposable
    {
        /// <summary>字体/度量上下文。</summary>
        public required TextStyle Style { get; init; }

        /// <summary>绑定 + 格式化后的最终文本。</summary>
        public required string Content { get; init; }

        /// <summary>折行结果（已按可排宽度切好）。</summary>
        public required List<string> Lines { get; init; }

        /// <summary>单行行高（mm）。</summary>
        public required double LineHeight { get; init; }

        /// <summary>可排区域左边界（mm）。</summary>
        public required double InnerLeft { get; init; }

        /// <summary>可排区域上边界（mm）。</summary>
        public required double InnerTop { get; init; }

        /// <summary>可排区域宽度（mm）。</summary>
        public required double InnerWidth { get; init; }

        /// <summary>可排区域高度（mm）。</summary>
        public required double InnerHeight { get; init; }

        /// <summary>水平对齐。</summary>
        public required HorizontalAlign HorizontalAlign { get; init; }

        /// <summary>垂直对齐。</summary>
        public required VerticalAlign VerticalAlign { get; init; }

        /// <summary>字色。</summary>
        public required SKColor Color { get; init; }

        /// <summary>下划线。</summary>
        public required bool Underline { get; init; }

        /// <summary>删除线。</summary>
        public required bool Strikethrough { get; init; }

        /// <summary>上划线。</summary>
        public required bool Overline { get; init; }

        /// <summary>首行额外缩进（mm）。</summary>
        public required double FirstLineIndentMm { get; init; }

        /// <inheritdoc/>
        public void Dispose() => Style.Dispose();
    }

    /// <summary>两端对齐时每个字缝要补的间距（mm）；行太短或只有一个字时不补。</summary>
    static double JustifySpacing(TextStyle style, string line, double maxWidthMm)
    {
        if (line.Length < 2)
            return 0;

        var extra = maxWidthMm - style.Measure(line);
        return extra > 0 ? extra / (line.Length - 1) : 0;
    }
}

/// <summary>线段元素渲染器。不裁剪，保证描边完整。</summary>
public sealed class LineRenderer : ElementRenderer<LineElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Line;

    /// <inheritdoc/>
    protected override bool ClipsToElement => false;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, LineElement element, RenderContext context)
    {
        var o = element.Line;
        // 线段本质是一条边框：颜色/线宽/虚线走样式的边框族；styler 的 color/borderColor 映射线色（脚本最高）
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element);
        var color = script?.BorderColor ?? script?.Color ?? StyleCascade.Pick(o.StrokeColor, SKColors.Black, named?.BorderColor);
        var width = script?.BorderWidthMm ?? StyleCascade.Pick(o.StrokeWidthMm, 0.2, named?.BorderWidthMm);
        if (width <= 0)
            return;

        var dash = script?.BorderDashPatternMm ?? o.DashPatternMm ?? named?.BorderDashPatternMm;
        using var paint = Paints.Stroke(color, width, dash);
        if (o.Direction == LineDirection.Horizontal)
            canvas.DrawLine((float)element.XMm, (float)element.YMm, (float)element.RightMm, (float)element.YMm, paint);
        else
            canvas.DrawLine((float)element.XMm, (float)element.YMm, (float)element.XMm, (float)element.BottomMm, paint);
    }
}

/// <summary>矩形元素渲染器。</summary>
public sealed class RectangleRenderer : ElementRenderer<RectangleElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Rectangle;

    /// <inheritdoc/>
    protected override bool ClipsToElement => false;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, RectangleElement element, RenderContext context)
    {
        var o = element.Rectangle;
        // 填充走样式的底色族、描边走边框族；styler 的 backgroundColor→填充、color/borderColor→描边（脚本最高）
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element);
        var stroke = script?.BorderColor ?? script?.Color ?? StyleCascade.PickNullable(o.StrokeColor, SKColors.Black, named?.BorderColor);
        var strokeWidth = script?.BorderWidthMm ?? StyleCascade.Pick(o.StrokeWidthMm, 0.2, named?.BorderWidthMm);
        var fill = script?.BackgroundColor ?? o.FillColor ?? named?.BackgroundColor;

        var rect = Geometry.InsetByStroke(new SKRect((float)element.XMm, (float)element.YMm, (float)element.RightMm, (float)element.BottomMm), strokeWidth, stroke is not null);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        // 圆角半径夹到短边一半，否则 Skia 会把整条路径判空（画不出也不报错，属静默失败）
        var radius = (float)Math.Max(0, Math.Min(Math.Min(rect.Width, rect.Height) / 2, o.CornerRadiusMm));

        // 逐角模式：cornerRadiiMm 给了非空数组即启用（序「左上,右上,左下,右下」，
        // 缺省角沿用标量；显式 0 = 直角，压过标量）。填充与描边共用一条路径。
        var perCornerMode = o.CornerRadiiMm is { Length: > 0 };
        using var cornerPath = perCornerMode
            ? CornerRounding.Build(CornerRounding.RectPoints(rect), CornerRounding.RectRadii(o.CornerRadiusMm, o.CornerRadiiMm!))
            : null;

        if (fill is { } fillColor)
        {
            using var fillPaint = Paints.Fill(fillColor);
            if (cornerPath is not null)
            {
                canvas.DrawPath(cornerPath, fillPaint);
            }
            else if (perCornerMode)
            {
                canvas.DrawRect(rect, fillPaint); // 逐角全零＝直角
            }
            else if (radius > 0)
            {
                using var path = new SKPath();
                path.AddRoundRect(rect, radius, radius, SKPathDirection.Clockwise);
                canvas.DrawPath(path, fillPaint);
            }
            else
            {
                canvas.DrawRect(rect, fillPaint);
            }
        }

        if (stroke is { } strokeColor && strokeWidth > 0)
        {
            var dash = script?.BorderDashPatternMm ?? o.DashPatternMm ?? named?.BorderDashPatternMm;
            using var strokePaint = Paints.Stroke(strokeColor, strokeWidth, dash);
            var sides = element.BorderSides & BorderSide.All;
            if (sides == BorderSide.All)
            {
                if (cornerPath is not null)
                {
                    canvas.DrawPath(cornerPath, strokePaint);
                }
                else if (perCornerMode)
                {
                    canvas.DrawRect(rect, strokePaint);
                }
                else if (radius > 0)
                {
                    using var path = new SKPath();
                    path.AddRoundRect(rect, radius, radius, SKPathDirection.Clockwise);
                    canvas.DrawPath(path, strokePaint);
                }
                else
                {
                    canvas.DrawRect(rect, strokePaint);
                }
            }
            else if (sides != BorderSide.None)
            {
                DrawSides(canvas, rect, sides, strokePaint);
            }
        }
    }

    /// <summary>在矩形 <paramref name="rect"/> 上只描选中的边（分边线型）。</summary>
    internal static void DrawSides(SKCanvas canvas, SKRect rect, BorderSide sides, SKPaint paint)
    {
        if (sides.HasFlag(BorderSide.Top))
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, paint);
        if (sides.HasFlag(BorderSide.Right))
            canvas.DrawLine(rect.Right, rect.Top, rect.Right, rect.Bottom, paint);
        if (sides.HasFlag(BorderSide.Bottom))
            canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, paint);
        if (sides.HasFlag(BorderSide.Left))
            canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, paint);
    }
}

/// <summary>椭圆元素渲染器（内切于元素矩形）。</summary>
public sealed class EllipseRenderer : ElementRenderer<EllipseElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Ellipse;

    /// <inheritdoc/>
    protected override bool ClipsToElement => false;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, EllipseElement element, RenderContext context)
    {
        var o = element.Ellipse;
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element);
        var stroke = script?.BorderColor ?? script?.Color ?? StyleCascade.PickNullable(o.StrokeColor, SKColors.Black, named?.BorderColor);
        var strokeWidth = script?.BorderWidthMm ?? StyleCascade.Pick(o.StrokeWidthMm, 0.2, named?.BorderWidthMm);
        var fill = script?.BackgroundColor ?? o.FillColor ?? named?.BackgroundColor;

        var rect = Geometry.InsetByStroke(new SKRect((float)element.XMm, (float)element.YMm, (float)element.RightMm, (float)element.BottomMm), strokeWidth, stroke is not null);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (fill is { } fillColor)
        {
            using var fillPaint = Paints.Fill(fillColor);
            canvas.DrawOval(rect, fillPaint);
        }

        if (stroke is { } strokeColor && strokeWidth > 0)
        {
            var dash = script?.BorderDashPatternMm ?? o.DashPatternMm ?? named?.BorderDashPatternMm;
            using var strokePaint = Paints.Stroke(strokeColor, strokeWidth, dash);
            var sides = element.BorderSides & BorderSide.All;
            if (sides == BorderSide.All)
            {
                canvas.DrawOval(rect, strokePaint);
            }
            else if (sides != BorderSide.None)
            {
                // 椭圆分边用外接框四边近似（一条直线段代表被保留的那条边）
                RectangleRenderer.DrawSides(canvas, rect, sides, strokePaint);
            }
        }
    }
}
