using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using SkiaSharp;

namespace CrossPrint.Engine;

/// <summary>
/// 模板 → 画布的唯一渲染入口。设计器预览、PNG 导出、PDF 导出全部走这里，
/// 这是"预览所见即纸张所得"这条 PRD 最高约束的实现点：任何一条绘制路径另起炉灶都算破坏契约。
/// </summary>
/// <remarks>
/// 约定：
/// <list type="bullet">
/// <item><description>入口统一 <c>canvas.Scale(UnitsPerMm)</c>，渲染器内部一律以 mm 作图。</description></item>
/// <item><description>同 <see cref="PaperElement.ZIndex"/> 时按模板数组顺序绘制（<c>OrderBy</c> 在 LINQ to Objects 里是稳定排序）。</description></item>
/// <item><description>不裁剪到页面矩形：越界元素照样绘制并记 <see cref="RenderIssueKind.ElementOutOfBounds"/>，
/// 由介质（PDF MediaBox / 打印机可印区）自然切掉。设计器需要在页面外摆放元素，这条裁剪不能加在公共路径上。</description></item>
/// <item><description>任何元素异常都被吞掉并转成告警，渲染不中断。</description></item>
/// </list>
/// </remarks>
public sealed class TemplateRenderer
{
    /// <summary>创建渲染器。</summary>
    /// <param name="registry">渲染器注册表；每个实例自带一份默认注册表，互不污染。</param>
    public TemplateRenderer(ElementRendererRegistry? registry = null)
    {
        Registry = registry ?? ElementRendererRegistry.CreateDefault();
    }

    /// <summary>使用的注册表。</summary>
    public ElementRendererRegistry Registry { get; }

    /// <summary>进程级共享实例（使用共享默认注册表）。</summary>
    public static TemplateRenderer Default { get; } = new(ElementRendererRegistry.Default);

    /// <summary>
    /// 解析本次绘制使用的页面高度（mm）。
    /// 定高纸张（<c>heightMm &gt; 0</c>）用纸高；连续纸（<c>heightMm = 0</c>）一律按内容自适应——
    /// 连续纸没有"固定页高"这个概念，返回 0 会让整页画不出来，所以这里没有任何开关可关。
    /// </summary>
    public static double ResolvePageHeightMm(PrintTemplate template, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);

        var fixedHeight = template.Paper.PageHeightMm;
        if (fixedHeight > 0)
            return fixedHeight;

        return template.ResolvePageHeightMm();
    }

    /// <summary>页面尺寸（mm），已应用方向与自适应页高。</summary>
    /// <param name="template">模板。</param>
    /// <param name="options">渲染选项。</param>
    /// <returns>宽、高（mm）。</returns>
    public static SKSize GetPageSizeMm(PrintTemplate template, RenderOptions options)
        => new((float)template.Paper.PageWidthMm, (float)ResolvePageHeightMm(template, options));

    /// <summary>
    /// 模板样式库查表（样式 id → 样式）。空库返回 null，渲染器侧连字典都不用查。
    /// 分页规划（<see cref="TemplatePaginator"/>）的测量上下文也用它注入，保证测量与正式渲染同一口径。
    /// </summary>
    /// <remarks>
    /// 手改 JSON 或外部导入的模板可能带重复或空 id：这里绝不抛——重复取最后一条、null/空白 id 跳过。
    /// 本函数也被无告警通道的测量路径调用，所以连报告条目都不记，与"缺样式引用静默忽略"同一口径。
    /// </remarks>
    public static IReadOnlyDictionary<string, NamedStyle>? BuildStyleMap(PrintTemplate template)
    {
        if (template.Styles.Count == 0)
            return null;

        var map = new Dictionary<string, NamedStyle>(template.Styles.Count);
        foreach (var style in template.Styles)
        {
            if (string.IsNullOrWhiteSpace(style.Id))
                continue;
            map[style.Id] = style;
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>
    /// 便捷重载：临时建一份 <see cref="RenderReport"/> 并返回它，方便调用方直接看告警。
    /// </summary>
    /// <param name="template">模板。</param>
    /// <param name="canvas">目标画布（未缩放，本方法负责施加 UnitsPerMm 缩放）。</param>
    /// <param name="options">渲染选项；null 用默认（96dpi）。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="fontResolver">字体解析器；null 用系统默认。</param>
    /// <param name="images">图片解析器；null 表示只有内嵌图片可用。</param>
    /// <returns>本次渲染的告警汇总。</returns>
    public RenderReport Render(
        PrintTemplate template,
        SKCanvas canvas,
        RenderOptions? options = null,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null)
    {
        var report = new RenderReport();
        Render(template, canvas, new RenderContext(options ?? new RenderOptions(), report, fontResolver, images, data));
        return report;
    }

    /// <summary>渲染一页到画布。</summary>
    /// <param name="template">模板。</param>
    /// <param name="canvas">目标画布。</param>
    /// <param name="context">渲染上下文（选项、告警、解析器、数据）。</param>
    public void Render(PrintTemplate template, SKCanvas canvas, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(context);

        var pageWidth = template.Paper.PageWidthMm;
        var pageHeight = ResolvePageHeightMm(template, context.Options);
        var unitsPerMm = context.UnitsPerMm;

        // 样式库注入：元素/载荷上的 StyleId 由此查表，做「库样式打底、内联覆盖」级联
        context.Styles = BuildStyleMap(template);
        // 脚本执行器注入：模板带 formatter/styler 才创建，无脚本模板零开销
        context.Scripts = ScriptRunner.HasScripts(template) ? new ScriptRunner() : null;

        var saved = canvas.Save();
        try
        {
            canvas.Scale((float)unitsPerMm, (float)unitsPerMm);

            var bg = context.Options.PageBackgroundColor;
            if (bg.Alpha > 0 && pageWidth > 0 && pageHeight > 0)
            {
                using var bgPaint = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                canvas.DrawRect(new SKRect(0, 0, (float)pageWidth, (float)pageHeight), bgPaint);
            }

            foreach (var element in template.Elements.OrderBy(e => e.ZIndex))
            {
                var renderer = Registry.Find(element.TypeKey);
                if (renderer is null)
                {
                    context.Report.Add(element.Id, RenderIssueKind.RendererMissing,
                        $"元素类型 '{element.TypeKey}' 没有注册渲染器，已跳过。");
                    continue;
                }

                if (Geometry.OverflowsPage(element, pageWidth, pageHeight))
                {
                    context.Report.Add(element.Id, RenderIssueKind.ElementOutOfBounds,
                        $"元素超出页面：位于 ({element.XMm:0.##}, {element.YMm:0.##})mm，尺寸 {element.WidthMm:0.##}×{element.HeightMm:0.##}mm，页面 {pageWidth:0.##}×{pageHeight:0.##}mm。");
                }

                try
                {
                    // 公共外观先于内容、边框后于内容：内容不会盖住边框，背景也不会盖住内容。
                    // 不透明度用 SaveLayer 把背景、内容、边框一起合成，避免三者各带一次 alpha 叠出脏边。
                    var opacity = double.IsFinite(element.Opacity) ? Math.Clamp(element.Opacity, 0.0, 1.0) : 1.0;
                    var elementSave = canvas.Save();
                    try
                    {
                        if (opacity < 1.0)
                        {
                            using var layerPaint = new SKPaint
                            {
                                Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)),
                            };
                            canvas.SaveLayer(layerPaint);
                        }

                        // 旋转围绕元素中心：hiprint/浏览器 CSS transform 的默认原点也是元素中心，
                        // 顺时针为正（Skia 与 CSS 的 rotate 同向）。背景、内容、边框一起转。
                        var rotation = double.IsFinite(element.RotationDegrees) ? element.RotationDegrees : 0;
                        if (rotation != 0)
                            canvas.RotateDegrees((float)rotation,
                                (float)(element.XMm + element.WidthMm / 2),
                                (float)(element.YMm + element.HeightMm / 2));

                        // styler 每个元素每次渲染只跑一次，框底/边框与内容绘制共用结果（脚本 > 内联 > 库）
                        var named = StyleCascade.Find(context, element.StyleId);
                        var scriptStyle = ScriptSupport.StyleOf(context, element, ScriptSupport.ContentExpressionOf(element));
                        DrawBoxBackground(canvas, element, named, scriptStyle);
                        renderer.Render(canvas, element, context);
                        DrawBoxBorder(canvas, element, named, scriptStyle);
                    }
                    finally
                    {
                        canvas.RestoreToCount(elementSave);
                    }
                }
                catch (Exception ex)
                {
                    // 引擎对外承诺不抛：坏数据是用户问题，不是崩溃理由。
                    context.Report.Add(element.Id, RenderIssueKind.ElementSkipped,
                        $"{element.TypeKey} 绘制失败已跳过：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    static void DrawBoxBackground(SKCanvas canvas, PaperElement element, NamedStyle? style, ScriptStyle? scriptStyle)
    {
        // 元素框底色/边框族全类型通用：库样式只在内联未设置时补位（CSS 同款打底口径），脚本样式最高
        if ((scriptStyle?.BackgroundColor ?? element.BackgroundColor ?? style?.BackgroundColor) is not { } background)
            return;

        var rect = Geometry.Bounds(element);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var paint = Paints.Fill(background);
        canvas.DrawRect(rect, paint);
    }

    static void DrawBoxBorder(SKCanvas canvas, PaperElement element, NamedStyle? style, ScriptStyle? scriptStyle)
    {
        var color = scriptStyle?.BorderColor ?? element.BorderColor ?? style?.BorderColor;
        // 宽是哨兵口径：等于模型默认值视为未设置，让样式补位；脚本给出即最高
        var width = scriptStyle?.BorderWidthMm ?? StyleCascade.Pick(element.BorderWidthMm, 0.2, style?.BorderWidthMm);
        if (color is not { } borderColor || width <= 0)
            return;

        var sides = element.BorderSides & BorderSide.All;
        if (sides == BorderSide.None)
            return;

        var rect = Geometry.InsetByStroke(Geometry.Bounds(element), width, hasStroke: true);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var dash = scriptStyle?.BorderDashPatternMm ?? element.BorderDashPatternMm ?? style?.BorderDashPatternMm;
        using var paint = Paints.Stroke(borderColor, width, dash);
        if (sides == BorderSide.All)
        {
            canvas.DrawRect(rect, paint);
            return;
        }

        // 只画选中的边：相邻边串成一条折线，转角用 Miter 接上，
        // 逐边 DrawLine 会在拐角留半个线宽的缺口。
        if (TryBuildSidesPath(rect, sides, out var path))
        {
            using (path)
                canvas.DrawPath(path, paint);
        }
    }

    static bool TryBuildSidesPath(SKRect rect, BorderSide sides, out SKPath path)
    {
        path = new SKPath();
        var corners = new[]
        {
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new SKPoint(rect.Left, rect.Bottom),
        };

        var selected = new[]
        {
            sides.HasFlag(BorderSide.Top),
            sides.HasFlag(BorderSide.Right),
            sides.HasFlag(BorderSide.Bottom),
            sides.HasFlag(BorderSide.Left),
        };

        // 从"未选中的边"之后开始遍历，这样跨越数组末尾的相邻边会被收进同一段折线。
        var start = Array.IndexOf(selected, false);
        if (start < 0)
            return false;

        var drawing = false;
        var drew = false;
        for (var k = 1; k <= 4; k++)
        {
            var side = (start + k) % 4;
            if (!selected[side])
            {
                drawing = false;
                continue;
            }

            if (!drawing)
            {
                path.MoveTo(corners[side]);
                drawing = true;
                drew = true;
            }

            path.LineTo(corners[(side + 1) % 4]);
        }

        return drew;
    }

    /// <summary>渲染结果位图与其告警。</summary>
    /// <param name="Bitmap">调用方负责释放。</param>
    /// <param name="Report">告警。</param>
    public sealed record BitmapResult(SKBitmap Bitmap, RenderReport Report);

    /// <summary>
    /// 渲染成位图。像素尺寸 = <c>ceil(mm / 25.4 × Dpi)</c>，因此同一模板在 96 与 300dpi 下
    /// 每个元素的毫米位置完全一致，只是栅格更密——测试据此做 mm 一致性断言。
    /// </summary>
    /// <param name="template">模板。</param>
    /// <param name="options">渲染选项（Dpi 决定像素密度）。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <returns>位图与告警。</returns>
    public BitmapResult RenderToBitmap(
        PrintTemplate template,
        RenderOptions? options = null,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null)
    {
        var opt = options ?? new RenderOptions();
        var size = GetPageSizeMm(template, opt);
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * opt.UnitsPerMm));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * opt.UnitsPerMm));

        var report = new RenderReport();
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            Render(template, canvas, new RenderContext(opt, report, fontResolver, images, data));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return new BitmapResult(bitmap, report);
    }
}
