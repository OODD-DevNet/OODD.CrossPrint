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
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.AllowTablePagination || template.Paper.PageHeightMm <= 0)
            return [SinglePage(template)];

        var measureReport = new RenderReport();
        var measureContext = new RenderContext(options, measureReport, fontResolver, images, data);
        // 样式库与正式渲染同一口径：列/表格样式会影响行高测量，不注入会算错分页窗口
        measureContext.Styles = TemplateRenderer.BuildStyleMap(template);
        // 列 formatter/styler 会改变单元格文本与字体形态，同样影响行高测量
        measureContext.Scripts = ScriptRunner.HasScripts(template) ? new ScriptRunner() : null;

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
