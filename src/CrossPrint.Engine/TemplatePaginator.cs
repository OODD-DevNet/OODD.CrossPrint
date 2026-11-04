using CrossPrint.Core;
using CrossPrint.Engine.Rendering;

namespace CrossPrint.Engine;

/// <summary>
/// 表格跨页分页规划器（FR-BND-05）：找出模板里"内容超出元素高"的那张表格，
/// 按真实行高把集合数据行贪心切进若干窗口，为每一页构建模板变体并批量渲染。
/// </summary>
/// <remarks>
/// 页面语义（真正的"表格跨页"，而不是每页重复整套信头）：
/// <list type="bullet">
/// <item><description><b>第 1 页</b>：完整模板布局（信头 + 表格开头一段行），表格克隆体原地撑到内容区底——
/// 后面还有数据时，表格元素框下方的空白没有保留价值；表格区域**下方**的落款区（合计、签字等）
/// 本页不画，留给最后一页——表格还在续，全单合计出现在第 1 页是错的。</description></item>
/// <item><description><b>中间页</b>：纯表格续行——表格克隆体移到上留白处、撑满整个内容区接着排，
/// 页面上只保留表格与绑定页码的文本元素（页脚页码）。</description></item>
/// <item><description><b>最后一页</b>：同中间页，再回填落款区元素（保持模板原位）；尾页窗口容量按
/// "表格底边 − 上留白"预留，保证最后一段行不会压到落款。</description></item>
/// </list>
/// 页码元素 = 内容绑定 <c>{{pageNo}}/{{pageCount}}/{{totalPages}}</c> 的文本元素，每页都画。
/// 变体只过滤/移动元素，不动渲染器：表格行窗口仍由 <see cref="RenderContext.TableWindows"/> 切片，
/// 页码仍由 <c>pageNo/pageTotal</c> 回退，序号与"本页小计"语义天然延续。
/// </remarks>
/// <para>
/// v1 边界（任一不满足就维持旧行为：单页渲染 + <see cref="RenderIssueKind.TableOverflow"/> 告警）：
/// 只对定高纸（<c>paper.heightMm &gt; 0</c>）启用；只支持一张溢出表格驱动页数；
/// 续页可用高度放不下"固定部分 + 一行"时放弃。
/// 规划期的绑定告警一律丢弃（用一次性 <see cref="RenderReport"/> 测量），正式渲染会重新绑定一遍。
/// </para>
public static class TemplatePaginator
{
    /// <summary>一页的分页计划：溢出表格本页的数据行窗口与页码。</summary>
    /// <param name="TableElementId">驱动分页的表格元素 id。</param>
    /// <param name="Window">本页渲染的数据行区间 <c>[Start, Start + Count)</c>。</param>
    /// <param name="PageNo">当前页码（1 起）。</param>
    /// <param name="PageTotal">总页数。</param>
    public sealed record PagePlan(string TableElementId, TableWindow Window, int PageNo, int PageTotal);

    /// <summary>单页渲染载荷：该页真正要画的模板变体 + 数据行窗口 + 页码。</summary>
    /// <param name="TableElementId">驱动分页的表格元素 id；空串表示未分页（整模板单页）。</param>
    /// <param name="Template">本页模板变体——未分页时就是原模板，分页时是按页面语义过滤/移动过的克隆。</param>
    /// <param name="Window">本页渲染的数据行窗口。</param>
    /// <param name="PageNo">当前页码（1 起）；0 表示无页码语义。</param>
    /// <param name="PageTotal">总页数；0 表示无页码语义。</param>
    public sealed record PaginationPage(string TableElementId, PrintTemplate Template, TableWindow Window, int PageNo, int PageTotal);

    /// <summary>
    /// 规划分页窗口。返回长度为 1 的列表表示无需分页（此时 <see cref="PagePlan.TableElementId"/> 为空串、页码为 0）；
    /// 长度大于 1 时每个元素对应一页，窗口在数据集合上连续无缝。
    /// </summary>
    public static IReadOnlyList<PagePlan> Plan(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null)
        => PlanLayout(template, options, data, fontResolver, images)
            .Select(p => new PagePlan(p.TableElementId, p.Window, p.PageNo, p.PageTotal))
            .ToList();

    /// <summary>
    /// 规划分页并构建每页的模板变体。导出器（如 BatchPdfExporter）逐页渲染返回的载荷：
    /// 变体里"该页该画的元素"已经就位，表格窗口与页码再由渲染上下文注入。
    /// </summary>
    internal static IReadOnlyList<PaginationPage> PlanLayout(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (options is null) throw new ArgumentNullException(nameof(options));

        if (template.Paper.PageHeightMm <= 0)
            return [SinglePage(template)];

        // 长文本续页度量（只对带 ContinueOnOverflow 的文本做，且必须真的溢出才算"要分页"）。
        var longTexts = options.AllowGeneralPagination
            ? MeasureLongTexts(template, options, data, fontResolver, images)
            : [];

        // 通用分页（手动翻页类：pageBreak/fixed/showInPage/unShowInPage/paperNumber，以及长文本溢出续排）
        // 优先于表格溢出分页。
        if (options.AllowGeneralPagination && NeedsGeneralPagination(template, longTexts))
            return PlanGeneralPages(template, options, data, fontResolver, images, longTexts);

        if (!options.AllowTablePagination)
            return [SinglePage(template)];

        var measureContext = CreateMeasureContext(template, options, data, fontResolver, images);

        TableElement? driver = null;
        var fixedHeight = 0d;
        var rowHeights = new List<double>();
        foreach (var element in template.Elements)
        {
            if (element is not TableElement table)
                continue;
            if (!TableRenderer.TryMeasurePages(table, measureContext, out var fixedHeightMm, out var heights))
                continue;

            // 装得下的表格不参与分页；两张同时溢出则放弃分页（v1 边界）
            if (fixedHeightMm + heights.Sum() <= table.HeightMm + 0.01)
                continue;
            if (driver is not null)
                return [SinglePage(template)];

            driver = table;
            fixedHeight = fixedHeightMm;
            rowHeights = heights;
        }

        if (driver is null)
            return [SinglePage(template)];

        // 各页容量：第 1 页表格原地撑到内容区底（落款区首页不画，元素高之下的空白没有保留价值）；
        // 续页表格移到上留白、撑满内容区；尾页要给表格下方的落款区留出模板原位，容量到表格底边为止。
        var contentTop = Math.Max(0, template.Margins.Top);
        var contentBottom = Math.Max(contentTop, template.Paper.PageHeightMm - Math.Max(0, template.Margins.Bottom));
        var capFirst = contentBottom - driver.YMm - fixedHeight;
        var capMiddle = contentBottom - contentTop - fixedHeight;
        var capLast = Math.Min(driver.BottomMm - contentTop - fixedHeight, capMiddle);
        if (capFirst <= 0 || capMiddle <= 0 || capLast <= 0)
            return [SinglePage(template)];

        // 按页面语义归类元素：页码文本每页都画；表格下方的落款区只在尾页出现。
        var driverBottom = driver.BottomMm;
        var pageNumbers = new List<PaperElement>();
        var belowTable = new List<PaperElement>();
        foreach (var element in template.Elements)
        {
            if (element is TextElement text && BindsPageNumber(text.Text.Content))
                pageNumbers.Add(element);
            else if (element.YMm >= driverBottom - 0.05)
                belowTable.Add(element);
        }

        // 贪心装页：行高就是正式渲染会用的同一套布局度量，所以每页画出来必然装得下。
        var windows = new List<TableWindow>();
        var start = 0;
        var count = 0;
        var acc = 0d;
        var available = capFirst;
        for (var i = 0; i < rowHeights.Count; i++)
        {
            var height = rowHeights[i];
            if (count > 0 && acc + height > available + 0.01)
            {
                windows.Add(new TableWindow(start, count));
                start = i;
                count = 0;
                acc = 0;
                available = capMiddle;
            }

            acc += height;
            count++;
        }

        windows.Add(new TableWindow(start, count));
        if (windows.Count == 1)
        {
            // 内容超出元素高但首页撑满后一页就装得下：仍按分页语义出这一页——
            // 表格撑满、落款区让位（它设计在表格元素下方，此时已没有空位），页码语义 1/1。
            return [new PaginationPage(driver.Id, FirstPage(template, driver, belowTable, contentBottom), windows[0], 1, 1)];
        }

        // 最后一窗按尾页容量收口：塞不下的前缀行挪成新的倒数第二页（它不超续页容量，无需再切）。
        var lastHeight = 0d;
        var suffix = 0;
        while (suffix < windows[^1].Count
               && lastHeight + rowHeights[windows[^1].Start + windows[^1].Count - 1 - suffix] <= capLast + 0.01)
        {
            lastHeight += rowHeights[windows[^1].Start + windows[^1].Count - 1 - suffix];
            suffix++;
        }

        suffix = Math.Max(1, suffix); // 尾页至少一行
        var lastStart = windows[^1].Start;
        var lastCount = windows[^1].Count;
        if (lastCount - suffix > 0)
        {
            windows[^1] = new TableWindow(lastStart + lastCount - suffix, suffix);
            windows.Insert(windows.Count - 1, new TableWindow(lastStart, lastCount - suffix));
        }

        var total = windows.Count;
        var pages = new List<PaginationPage>(total);
        for (var p = 0; p < total; p++)
        {
            var pageTemplate = p == 0
                ? FirstPage(template, driver, belowTable, contentBottom) // 第 1 页：信头照画，落款区让位，表格撑到内容区底
                : BuildContinuation(template, driver, pageNumbers, p == total - 1 ? belowTable : null, contentTop,
                    p == total - 1 ? driver.BottomMm - contentTop : contentBottom - contentTop);
            pages.Add(new PaginationPage(driver.Id, pageTemplate, windows[p], p + 1, total));
        }

        return pages;
    }

    /// <summary>页数便捷口径：无需分页为 1，分页时为窗口页数。</summary>
    public static int GetPageCount(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null)
        => Plan(template, options, data, fontResolver, images).Count;

    /// <summary>"无需分页"的载荷：原模板、无窗口、无页码语义。</summary>
    static PaginationPage SinglePage(PrintTemplate template) => new(string.Empty, template, default, 0, 0);

    /// <summary>
    /// 分页度量上下文：与正式渲染同一口径（样式库 + 脚本执行器都注入）。
    /// 表格行高与长文本折行都依赖级联后的字体/内边距，少注入一样就会把窗口算错。
    /// 规划期的告警一律进一次性报告并丢弃，正式渲染会重新绑定一遍。
    /// </summary>
    static RenderContext CreateMeasureContext(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver,
        IImageResolver? images)
    {
        var context = new RenderContext(options, new RenderReport(), fontResolver, images, data);
        context.Styles = TemplateRenderer.BuildStyleMap(template);
        context.Scripts = ScriptRunner.HasScripts(template) ? new ScriptRunner() : null;
        return context;
    }

    /// <summary>
    /// 长文本续页的度量结果：折行后的完整行集合 + 首页能装下的行数。
    /// 元素框高度扣掉上下内边距就是首页可排高度；续页用"整页内容区高度扣除同一份内边距"。
    /// </summary>
    sealed class LongTextFlow
    {
        /// <summary>源元素（同一个实例作为字典键，引用相等）。</summary>
        public required TextElement Element { get; init; }

        /// <summary>绑定 + 折行后的完整行集合。</summary>
        public required List<string> Lines { get; init; }

        /// <summary>单行行高（mm）。</summary>
        public required double LineHeight { get; init; }

        /// <summary>首页可排高度（mm，已扣上下内边距）。</summary>
        public required double InnerHeight { get; init; }

        /// <summary>元素框高度（mm），用于反推"上下内边距一共占了多少"。</summary>
        public required double BoxHeight { get; init; }

        /// <summary>首页能装下的行数（至少 1）。</summary>
        public required int FirstCapacity { get; init; }

        /// <summary>续页能装下的行数，规划时按内容区高度算出。</summary>
        public int ContinuationCapacity { get; set; }

        /// <summary>这个元素一共占几页（1 表示没溢出，不参与切片）。</summary>
        public int Pages { get; set; } = 1;

        /// <summary>首页页码（0 起）。</summary>
        public int StartPage { get; set; }

        /// <summary>是否真的溢出（装得下就不该改变分页语义）。</summary>
        public bool Overflows => Lines.Count > FirstCapacity;
    }

    /// <summary>
    /// 度量所有"内容溢出自动续页"的文本元素。装得下的也返回（调用方按 <see cref="LongTextFlow.Overflows"/> 过滤），
    /// 这样"是否要启用通用分页"与"每页切多少行"用的是同一次测量。
    /// </summary>
    static List<LongTextFlow> MeasureLongTexts(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver,
        IImageResolver? images)
    {
        List<LongTextFlow>? flows = null;
        RenderContext? context = null;
        foreach (var element in template.Elements)
        {
            if (element is not TextElement text || !text.Text.ContinueOnOverflow || text.HeightMm <= 0)
                continue;

            context ??= CreateMeasureContext(template, options, data, fontResolver, images);
            using var block = TextRenderer.BuildBlock(text, context);
            if (block is null)
                continue;

            flows ??= [];
            flows.Add(new LongTextFlow
            {
                Element = text,
                Lines = block.Lines,
                LineHeight = block.LineHeight,
                InnerHeight = block.InnerHeight,
                BoxHeight = text.HeightMm,
                FirstCapacity = Capacity(block.InnerHeight, block.LineHeight),
            });
        }

        return flows ?? [];
    }

    /// <summary>给定可排高度与行高，能排几行（至少 1 行，避免除零把分页卡死）。</summary>
    static int Capacity(double heightMm, double lineHeightMm)
        => lineHeightMm <= 0 ? int.MaxValue : Math.Max(1, (int)Math.Floor(heightMm / lineHeightMm + 1e-9));

    /// <summary>
    /// 通用分页（手动翻页类）：把模板当作一张「逻辑长页」按页面高度切片成多页。
    /// 元素按 Y 坐标落入第 <c>floor((Y - 上留白) / 页高)</c> 页；<c>pageBreak</c> 强制其后元素另起一页；
    /// <c>fixed</c> 元素与绑定页码占位符的文本元素每页重复；<c>showInPage</c>/<c>unShowInPage</c>
    /// 按 1 起页号过滤；<c>paperNumber</c> 为每页追加页码脚注。
    /// 落在第 k 页的流元素整体向下平移 k×页高，使每页视口内坐标一致。
    /// </summary>
    static IReadOnlyList<PaginationPage> PlanGeneralPages(
        PrintTemplate template,
        RenderOptions options,
        object? data,
        IFontResolver? fontResolver,
        IImageResolver? images,
        IReadOnlyList<LongTextFlow> longTexts)
    {
        var contentTop = Math.Max(0, template.Margins.Top);
        var contentBottom = Math.Max(contentTop, template.Paper.PageHeightMm - Math.Max(0, template.Margins.Bottom));
        var pageHeight = contentBottom - contentTop;
        if (pageHeight <= 0)
            return [SinglePage(template)];

        // 常驻每页的元素：fixed，以及绑定了页码占位符的文本（这两类从流元素里排除，避免重复）
        var pageNumberEls = template.Elements
            .OfType<TextElement>()
            .Where(t => BindsPageNumber(t.Text.Content))
            .Cast<PaperElement>()
            .ToList();
        var pageNumberIds = new HashSet<string>(pageNumberEls.Select(e => e.Id));

        var fixedEls = template.Elements
            .Where(e => e.Fixed && !pageNumberIds.Contains(e.Id))
            .ToList();
        var flowEls = template.Elements
            .Where(e => !e.Fixed && !pageNumberIds.Contains(e.Id))
            .OrderBy(e => e.YMm)
            .ThenBy(e => e.XMm)
            .ToList();

        // 长文本续页：只对"真的溢出"且属于流元素的文本切片（fixed 元素每页重复，续页语义冲突，不切片）
        var flows = longTexts
            .Where(f => f.Overflows && !f.Element.Fixed && !pageNumberIds.Contains(f.Element.Id))
            .ToList();
        var flowByEl = new Dictionary<PaperElement, LongTextFlow>();
        foreach (var flow in flows)
        {
            // 续页用整页内容区高度，扣掉与首页相同的上下内边距占位（BoxHeight - InnerHeight）
            var padding = Math.Max(0, flow.BoxHeight - flow.InnerHeight);
            flow.ContinuationCapacity = Capacity(pageHeight - padding, flow.LineHeight);
            flowByEl[flow.Element] = flow;
        }

        // 切片 + 强制分页：元素的自然页号由 Y 决定，pageBreak 把后续元素推到下一页起，
        // 长文本把后续元素推到它续排结束的下一页起（与 hiprint 的 longText 顺延语义一致）
        var pageOf = new Dictionary<PaperElement, int>();
        var forcedMin = 0;
        foreach (var el in flowEls)
        {
            var natural = (int)Math.Floor((el.YMm - contentTop) / pageHeight);
            if (natural < 0) natural = 0;
            var pg = Math.Max(natural, forcedMin);
            pageOf[el] = pg;

            if (flowByEl.TryGetValue(el, out var flow))
            {
                var capacity = Math.Max(1, flow.ContinuationCapacity);
                var remaining = flow.Lines.Count - flow.FirstCapacity;
                flow.Pages = 1 + (remaining > 0 ? (remaining + capacity - 1) / capacity : 0);
                flow.StartPage = pg;
                forcedMin = Math.Max(forcedMin, pg + flow.Pages);
            }

            if (el.PageBreakAfter)
                forcedMin = Math.Max(forcedMin, pg + 1);
        }

        var maxPage = pageOf.Count > 0 ? pageOf.Values.Max() : 0;
        foreach (var flow in flows)
            maxPage = Math.Max(maxPage, flow.StartPage + flow.Pages - 1);
        var total = maxPage + 1;

        var pages = new List<PaginationPage>(total);
        for (var p = 0; p < total; p++)
        {
            var displayPage = p + 1;
            var elements = new List<PaperElement>();

            foreach (var el in flowEls)
            {
                if (!IsVisibleOnPage(el, displayPage))
                    continue;

                if (flowByEl.TryGetValue(el, out var flow))
                {
                    var slicePage = p - flow.StartPage;
                    if (slicePage < 0 || slicePage >= flow.Pages)
                        continue;
                    var slice = BuildLongTextSlice(flow, slicePage, p, contentTop, contentBottom, pageHeight);
                    if (slice is not null)
                        elements.Add(slice);
                    continue;
                }

                if (pageOf[el] != p)
                    continue;
                var clone = (PaperElement)el.CloneDeep();
                clone.YMm = el.YMm - p * pageHeight;
                elements.Add(clone);
            }

            foreach (var el in fixedEls)
                elements.Add((PaperElement)el.CloneDeep());

            foreach (var el in pageNumberEls)
                elements.Add((PaperElement)el.CloneDeep());

            if (template.PaperNumberEnabled)
            {
                var footer = BuildPaperNumberFooter(template, contentTop, contentBottom);
                if (footer is not null)
                    elements.Add(footer);
            }

            pages.Add(new PaginationPage(string.Empty, ShallowWithElements(template, elements), default, displayPage, total));
        }

        return pages;
    }

    /// <summary>
    /// 元素在指定显示页（1 起）是否可见：unShowInPage 优先排除，其次 showInPage 限定。
    /// 解析口径统一交给 <see cref="PageRangeSpec"/>——设计器面板写进来的规格与这里读的是同一份。
    /// </summary>
    internal static bool IsVisibleOnPage(PaperElement el, int displayPage)
        => PageRangeSpec.IsVisibleOn(el.ShowInPages, el.UnShowInPages, displayPage);

    /// <summary>为每页构造页码脚注文本元素（内容绑定 <c>{{pageNo}}/{{pageCount}}</c>）。</summary>
    static PaperElement? BuildPaperNumberFooter(PrintTemplate template, double contentTop, double contentBottom)
    {
        var format = template.PaperNumberFormat ?? string.Empty;
        if (string.IsNullOrWhiteSpace(format))
            format = "第 {{pageNo}} / {{pageCount}} 页";

        var text = new TextElement
        {
            Id = template.NextElementId(),
            Text = new TextOptions { Content = format, HorizontalAlign = HorizontalAlign.Center },
            WidthMm = Math.Max(10, template.Paper.WidthMm - template.Margins.Left - template.Margins.Right),
            HeightMm = 6,
            ZIndex = int.MaxValue,
        };

        text.XMm = template.PaperNumberXMm
            ?? (template.Margins.Left + (template.Paper.WidthMm - template.Margins.Left - template.Margins.Right - text.WidthMm) / 2);
        text.YMm = template.PaperNumberYMm
            ?? Math.Max(contentTop, contentBottom - 6);

        return text;
    }

    /// <summary>
    /// 模板是否触发通用分页：含任意分页元素标志、启用了自动页码，或有真的溢出需要续页的长文本。
    /// 长文本装得下时<b>不算</b>（否则仅带标记就会把模板切成按 Y 切片的通用分页，凭空多出页）。
    /// </summary>
    static bool NeedsGeneralPagination(PrintTemplate template, IReadOnlyList<LongTextFlow> longTexts)
    {
        if (template.PaperNumberEnabled)
            return true;
        if (longTexts.Any(f => f.Overflows && !f.Element.Fixed))
            return true;
        foreach (var el in template.Elements)
        {
            if (el.PageBreakAfter || el.Fixed
                || !string.IsNullOrWhiteSpace(el.ShowInPages)
                || !string.IsNullOrWhiteSpace(el.UnShowInPages))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 截取长文本元素在某一页要显示的行，生成该页的元素副本。
    /// 首页沿用元素原框与原 Y（只把视口按页平移掉）；续页贴内容区顶端、占满整页内容区、顶端对齐。
    /// </summary>
    static TextElement? BuildLongTextSlice(
        LongTextFlow flow,
        int slicePage,
        int pageIndex,
        double contentTop,
        double contentBottom,
        double pageHeight)
    {
        var capacity = Math.Max(1, flow.ContinuationCapacity);
        var start = slicePage == 0 ? 0 : flow.FirstCapacity + (slicePage - 1) * capacity;
        if (start >= flow.Lines.Count)
            return null;

        var count = Math.Min(slicePage == 0 ? flow.FirstCapacity : capacity, flow.Lines.Count - start);
        var clone = (TextElement)flow.Element.CloneDeep();
        clone.Text.Content = string.Join("\n", flow.Lines.Skip(start).Take(count));
        // 这些行已经是最终折行结果：关掉二次折行与省略号，避免渲染期在同一个宽度上再断一次行
        clone.Text.Wrap = false;
        clone.Text.Ellipsis = false;
        clone.Text.ContinueOnOverflow = false;

        if (slicePage == 0)
        {
            clone.YMm = flow.Element.YMm - pageIndex * pageHeight;
        }
        else
        {
            clone.YMm = contentTop;
            clone.HeightMm = Math.Max(0, contentBottom - contentTop);
            clone.Text.VerticalAlign = VerticalAlign.Top;
        }

        return clone;
    }

    /// <summary>
    /// 首页变体：完整布局（信头照画、落款区让位），表格克隆体原地撑到内容区底——
    /// 后面还有数据时，表格元素框下方的空白没有保留价值。原模板不被改动。
    /// </summary>
    static PrintTemplate FirstPage(
        PrintTemplate template,
        TableElement driver,
        IReadOnlyCollection<PaperElement> belowTable,
        double contentBottomMm)
    {
        var table = (TableElement)driver.CloneDeep();
        table.HeightMm = Math.Max(driver.HeightMm, contentBottomMm - driver.YMm);
        var elements = template.Elements
            .Where(e => !belowTable.Contains(e))
            .Select(e => ReferenceEquals(e, driver) ? (PaperElement)table : e)
            .ToList();
        return ShallowWithElements(template, elements);
    }

    /// <summary>
    /// 续页变体：表格克隆体移到上留白、按本页角色定高；页面上只保留表格、页码元素
    /// 与（尾页才有的）落款区元素。
    /// </summary>
    static PrintTemplate BuildContinuation(
        PrintTemplate template,
        TableElement driver,
        IReadOnlyList<PaperElement> pageNumbers,
        IReadOnlyCollection<PaperElement>? footerZone,
        double contentTopMm,
        double tableHeightMm)
    {
        var table = (TableElement)driver.CloneDeep();
        // 续页仅在 RepeatHeaderOnEachPage 为假时隐藏表头；首页保留原始 ShowHeader。
        if (!table.Table.RepeatHeaderOnEachPage)
            table.Table.ShowHeader = false;
        table.YMm = contentTopMm;
        table.HeightMm = Math.Max(0, tableHeightMm);

        var elements = new List<PaperElement> { table };
        elements.AddRange(pageNumbers);
        if (footerZone is not null)
            elements.AddRange(footerZone);
        return ShallowWithElements(template, elements);
    }

    static PrintTemplate ShallowWithElements(PrintTemplate template, IEnumerable<PaperElement> elements) => new()
    {
        SchemaVersion = template.SchemaVersion,
        Paper = template.Paper.Clone(),
        Margins = template.Margins.Clone(),
        DesignData = template.DesignData,
        // 样式库直接共享引用：NamedStyle.Clone 是 Core 的 internal，且渲染期样式只读，与 DesignData 同一口径。
        // 曾漏掉这行——续页变体样式库为空，命名样式引用按设计静默忽略，第 2 页起颜色/加粗无声丢失。
        Styles = template.Styles,
        Elements = elements.ToList(),
    };

    /// <summary>
    /// 内容绑定了页码占位符（pageNo/pageCount/totalPages）即视为页脚页码，每页重复。
    /// 只认 <c>{{...}}</c> 占位符形态：字面提到这些词的普通标签（如字段清单 "pageNoList：…"）不是页码。
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex PageNumberPlaceholder =
        new(@"\{\{[^{}]*(?:pageNo|pageCount|totalPages)[^{}]*\}\}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    static bool BindsPageNumber(string content) => PageNumberPlaceholder.IsMatch(content);
}
