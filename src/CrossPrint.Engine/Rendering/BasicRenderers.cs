using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>文本元素渲染器。度量、折行与对齐全部委托 <see cref="TextStyle"/>。</summary>
public sealed class TextRenderer : ElementRenderer<TextElement>
{
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
        var o = element.Text;
        var content = DataBinder.BindText(o.Content, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context);
        // formatter（hiprint 同款）：返回值替换显示内容；无脚本/降级保留原文本
        content = ScriptSupport.FormatContent(context, element, o.Content, content);
        if (content.Length == 0)
            return;

        // 样式库级联：库样式打底、内联覆盖（非空内联字段等于模型默认值视为未设置，哨兵口径）
        var named = StyleCascade.Find(context, o.StyleId);
        var fontFamily = StyleCascade.Pick(o.FontFamily, "Microsoft YaHei", named?.FontFamily);
        var fontSizePt = StyleCascade.Pick(o.FontSizePt, 12, named?.FontSizePt);
        var bold = StyleCascade.Pick(o.Bold, false, named?.Bold);
        var italic = StyleCascade.Pick(o.Italic, false, named?.Italic);
        var underline = StyleCascade.Pick(o.Underline, false, named?.Underline);
        var strikethrough = StyleCascade.Pick(o.Strikethrough, false, named?.Strikethrough);
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

        using var style = new TextStyle(context, fontFamily, fontSizePt, bold, italic, content, element.Id);
        style.LetterSpacingMm = Math.Max(0, context.PtToUnits(letterSpacingPt));

        // 内边距把可排区域从元素框里缩出来：hiprint 的 contentPadding 是文本真正能占的盒子，
        // 对齐、换行、垂直居中都以它为准，而不是元素框。
        var leftPad = Math.Max(0, padLeft);
        var innerLeft = element.XMm + leftPad;
        var innerTop = element.YMm + Math.Max(0, padTop);
        var innerWidth = Math.Max(0, element.WidthMm - leftPad - Math.Max(0, padRight));
        var innerHeight = Math.Max(0, element.HeightMm - Math.Max(0, padTop) - Math.Max(0, padBottom));

        var lineHeight = style.LineHeight(lineSpacing);
        var lines = style.Wrap(content, innerWidth, o.Wrap);
        if (!o.Wrap && o.Ellipsis)
        {
            for (var i = 0; i < lines.Count; i++)
                lines[i] = style.Truncate(lines[i], innerWidth);
        }

        var blockHeight = lines.Count * lineHeight;
        var top = vAlign switch
        {
            VerticalAlign.Middle => innerTop + (innerHeight - blockHeight) / 2,
            VerticalAlign.Bottom => innerTop + innerHeight - blockHeight,
            _ => innerTop,
        };
        var baselineOffset = style.BaselineOffset(lineHeight);

        using var decorationPaint = underline || strikethrough
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
            style.DrawLine(canvas, lines[i], x, baseline, color, extra);

            if (decorationPaint is null || lines[i].Length == 0)
                continue;

            var width = style.Measure(lines[i]);
            if (underline)
            {
                var y = baseline + style.SizeMm * 0.12;
                canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
            }

            if (strikethrough)
            {
                var y = baseline - style.SizeMm * 0.3;
                canvas.DrawLine((float)x, (float)y, (float)(x + width), (float)y, decorationPaint);
            }
        }
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
        var radius = (float)Math.Clamp(o.CornerRadiusMm, 0, Math.Min(rect.Width, rect.Height) / 2);

        if (fill is { } fillColor)
        {
            using var fillPaint = Paints.Fill(fillColor);
            if (radius > 0)
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
            if (radius > 0)
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
            canvas.DrawOval(rect, strokePaint);
        }
    }
}
