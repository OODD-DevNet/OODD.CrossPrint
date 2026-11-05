using CrossPrint.Core;
using CrossPrint.Core.Rendering;
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
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (options is null) throw new ArgumentNullException(nameof(options));

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

    /// <summary>
    /// 连续纸（小票）的"任意行数"语义：表格内容超过元素高度时**不裁剪**，
    /// 而是把表格增高到内容高、表格下方的元素整体顺移，页高随内容自适应
    /// （<see cref="ResolvePageHeightMm"/> 取最底元素下缘）。这是小票打印机的真实行为——
    /// 纸随内容走，版块跟着往下推。
    /// 返回的是<b>深拷贝工作副本</b>：绝不改调用方模板（撤销栈、设计器都还持有原对象）。
    /// 固定纸张不做（那是分页语义，归 <see cref="TemplatePaginator"/>）。
    /// 测量期绑定告警走一次性报告，不混进正式渲染报告（正式渲染会重新绑定一遍）。
    /// </summary>
    internal static PrintTemplate AutoGrowContinuousTables(PrintTemplate template, RenderContext context)
    {
        if (!template.Paper.IsContinuous)
            return template;

        var tables = template.Elements.OfType<TableElement>().OrderBy(e => e.YMm).ToList();
        if (tables.Count == 0)
            return template;

        var working = template.Clone();
        var workingTables = working.Elements.OfType<TableElement>().OrderBy(e => e.YMm).ToList();

        var scratchReport = new RenderReport();
        var savedReport = context.Report;
        var grown = false;
        try
        {
            context.Report = scratchReport;
            for (var i = 0; i < workingTables.Count; i++)
            {
                var table = workingTables[i];
                TableRenderer.TryMeasurePages(table, context, out var fixedHeightMm, out var rowHeights);
                var content = fixedHeightMm + (rowHeights.Count > 0 ? rowHeights.Sum() : 0);
                if (content <= table.HeightMm)
                    continue;

                grown = true;
                var delta = content - table.HeightMm;
                var oldBottom = table.BottomMm;
                table.HeightMm = content;
                // 只顺移"起始于表格底边之下"的元素（并排侧放不受影响——小票版式里没有这种场景）
                foreach (var e in working.Elements.Where(e => !ReferenceEquals(e, table) && e.YMm >= oldBottom - 0.01))
                    e.YMm += delta;
            }
        }
        finally
        {
            context.Report = savedReport;
        }

        return grown ? working : template;
    }

    /// <summary>渲染一页到画布。</summary>
    /// <param name="template">模板。</param>
    /// <param name="canvas">目标画布。</param>
    /// <param name="context">渲染上下文（选项、告警、解析器、数据）。</param>
    public void Render(PrintTemplate template, SKCanvas canvas, RenderContext context)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (canvas is null) throw new ArgumentNullException(nameof(canvas));
        if (context is null) throw new ArgumentNullException(nameof(context));

        // 样式库注入：元素/载荷上的 StyleId 由此查表，做「库样式打底、内联覆盖」级联。
        // 必须先于连续纸表格增高：测量要按同一套样式口径。
        context.Styles = BuildStyleMap(template);
        // 脚本执行器注入：模板带 formatter/styler 才创建，无脚本模板零开销
        context.Scripts = ScriptRunner.HasScripts(template) ? new ScriptRunner() : null;

        // 连续纸：表格自动增高 + 下方元素顺移（任意行数小票）；固定纸张原样返回
        var working = AutoGrowContinuousTables(template, context);

        var pageWidth = working.Paper.PageWidthMm;
        var pageHeight = ResolvePageHeightMm(working, context.Options);
        var unitsPerMm = context.UnitsPerMm;

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

            // 文字水印：垫在所有元素之下、背景之上，打印/导出与设计画布一致（mm 空间 pxPerMm=1）。
            // 文字支持 {{字段}} 占位符：与文本元素同一套绑定（设计态测试/设计数据优先，打印/导出走宿主数据）。
            if (working.Watermark is { Enabled: true } watermark && pageWidth > 0 && pageHeight > 0)
            {
                var effective = watermark;
                // 字段绑定（纯字段名自动包 {{}}）与 {{字段}} 占位符同一套 BindContent 口径；
                // 设计态测试数据优先（preferTestData），打印时测试数据仅作缺字段兜底——与元素一致。
                if (!string.IsNullOrWhiteSpace(watermark.Binding) || DataBinder.HasPlaceholder(watermark.Text))
                {
                    var resolved = DataBinder.BindContent(
                        watermark.Text, watermark.Binding, context.Data, context.Report, "watermark",
                        missingFallback: watermark.TestData, preferTestData: context.PreferTestData, context: context);
                    if (!string.Equals(resolved, watermark.Text, StringComparison.Ordinal))
                    {
                        effective = watermark.Clone(); // 不污染模板模型（撤销栈还持有原对象）
                        effective.Text = resolved;
                    }
                }

                var wmSave = canvas.Save();
                canvas.ClipRect(new SKRect(0, 0, (float)pageWidth, (float)pageHeight));
                WatermarkPainter.Draw(canvas, effective, pageWidth, pageHeight, pxPerMm: 1.0);
                canvas.RestoreToCount(wmSave);
            }

            // 面板整体偏移：整张页面内容平移（背景色保持铺满），实现 hiprint 的 leftOffset/topOffset。
            var ox = working.LeftOffsetMm;
            var oy = working.TopOffsetMm;
            if (ox != 0 || oy != 0)
                canvas.Translate((float)ox, (float)oy);

            foreach (var element in working.Elements.OrderBy(e => e.ZIndex))
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
                    var opacity = !double.IsNaN(element.Opacity) && !double.IsInfinity(element.Opacity) ? Math.Max(0.0, Math.Min(1.0, element.Opacity)) : 1.0;
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
                        // scale/translate 同样作用于整块内容（围绕元素中心缩放，再整体平移）。
                        var rotation = !double.IsNaN(element.RotationDegrees) && !double.IsInfinity(element.RotationDegrees) ? element.RotationDegrees : 0;
                        var scaleX = !double.IsNaN(element.ScaleX) && !double.IsInfinity(element.ScaleX) ? element.ScaleX : 1;
                        var scaleY = !double.IsNaN(element.ScaleY) && !double.IsInfinity(element.ScaleY) ? element.ScaleY : 1;
                        var translateX = !double.IsNaN(element.TranslateMmX) && !double.IsInfinity(element.TranslateMmX) ? element.TranslateMmX : 0;
                        var translateY = !double.IsNaN(element.TranslateMmY) && !double.IsInfinity(element.TranslateMmY) ? element.TranslateMmY : 0;

                        var cx = element.XMm + element.WidthMm / 2;
                        var cy = element.YMm + element.HeightMm / 2;
                        if (rotation != 0 || scaleX != 1 || scaleY != 1)
                        {
                            canvas.Translate((float)cx, (float)cy);
                            if (rotation != 0)
                                canvas.RotateDegrees((float)rotation);
                            if (scaleX != 1 || scaleY != 1)
                                canvas.Scale((float)scaleX, (float)scaleY);
                            canvas.Translate((float)-cx, (float)-cy);
                        }

                        if (translateX != 0 || translateY != 0)
                            canvas.Translate((float)translateX, (float)translateY);

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
