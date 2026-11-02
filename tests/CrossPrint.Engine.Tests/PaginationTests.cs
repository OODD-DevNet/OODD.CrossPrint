using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 表格跨页分页（FR-BND-05 v1）取证：规划窗口连续无缝、窗口渲染真的装得下、
/// 序号跨页连续、汇总只算窗口内数据行、页码绑定回退、导出页数等于计划页数。
/// </summary>
public class PaginationTests
{
    const int RowCount = 120;
    const double TableHeightMm = 158;
    const double RowHeightMm = 6;

    [Fact]
    public void 分页规划把数据行切成连续无缝的窗口()
    {
        var (template, tableId) = BigInvoice();

        var plans = TemplatePaginator.Plan(template, PaginationOptions(), Payload(RowCount));

        plans.Count.Should().BeGreaterThan(1, "120 行 × 6mm 装不进 158mm 的表格元素");
        plans.Select(p => p.PageNo).Should().Equal(Enumerable.Range(1, plans.Count));
        plans.Select(p => p.PageTotal).Should().OnlyContain(total => total == plans.Count);
        plans.Select(p => p.TableElementId).Should().OnlyContain(id => id == tableId);

        // 窗口首尾相接铺满整个数据集合；第 1 页受元素高约束，续页表格顶格撑满内容区
        var next = 0;
        foreach (var plan in plans)
        {
            plan.Window.Start.Should().Be(next, "上一页的终点就是这一页的起点");
            var cap = plan.PageNo == 1
                ? (int)((template.Paper.PageHeightMm - template.Margins.Bottom - TableTopMm) / RowHeightMm)
                : (int)(template.Paper.PageHeightMm / RowHeightMm);
            plan.Window.Count.Should().BeInRange(1, cap);
            next += plan.Window.Count;
        }

        next.Should().Be(RowCount, "所有数据行都要被分到某一页，不许丢行");
        TemplatePaginator.GetPageCount(template, PaginationOptions(), Payload(RowCount)).Should().Be(plans.Count);
    }

    [Fact]
    public void 关闭开关维持单页旧行为()
    {
        var (template, _) = BigInvoice();

        var plans = TemplatePaginator.Plan(template, new RenderOptions(), Payload(RowCount));

        plans.Should().ContainSingle();
        plans[0].TableElementId.Should().BeEmpty("没有驱动表格 = 没有窗口语义");
        plans[0].PageNo.Should().Be(0);
        plans[0].PageTotal.Should().Be(0);
    }

    [Fact]
    public void 连续纸不启用分页()
    {
        var (template, _) = BigInvoice(pageHeightMm: 0);

        TemplatePaginator.Plan(template, PaginationOptions(), Payload(RowCount))
            .Should().ContainSingle("连续纸靠自动增高，没有固定页高可切");
    }

    [Fact]
    public void 内容装得下的表格不切页()
    {
        // 10 行 × 6mm + 表头/汇总 ≈ 72mm，200mm 元素里装得下
        var (template, _) = BigInvoice(tableHeightMm: 200);

        TemplatePaginator.Plan(template, PaginationOptions(), Payload(10))
            .Should().ContainSingle("内容没有超出元素高就轮不到分页");
    }

    [Fact]
    public void 两张表格同时溢出不启用分页()
    {
        var (template, _) = BigInvoice();
        var (second, _) = BigInvoice();
        var clone = (TableElement)second.Elements[0];
        clone.Id = "second-table";
        template.Elements.Add(clone);

        TemplatePaginator.Plan(template, PaginationOptions(), Payload(RowCount))
            .Should().ContainSingle("v1 只支持一张溢出表格驱动页数，猜着切两张比报错危险");
    }

    [Fact]
    public void 窗口渲染装得下而整表渲染报溢出()
    {
        var (template, tableId) = SmallOverflowInvoice();

        // 整表渲染：内容高 > 元素高 → TableOverflow
        var full = RenderIssues(template, windows: null);
        full.Should().Contain(i => i.Kind == RenderIssueKind.TableOverflow);

        // 窗口渲染：只画 2 行 → 装得下，不再溢出
        var windows = new Dictionary<string, TableWindow> { [tableId] = new TableWindow(0, 2) };
        var windowed = RenderIssues(template, windows, pageNo: 1, pageTotal: 3);
        windowed.Where(i => i.Kind != RenderIssueKind.FontFallback)
            .Should().BeEmpty("窗口内的内容按规划必然装得下");
    }

    [Fact]
    public void 窗口切片后序号连续且汇总只算窗口内行()
    {
        // 5 行金额 10/20/30/40/50，窗口取第 3、4 行：序号应为 3、4，小计应为 70
        var o = new TableOptions
        {
            FontFamily = "Microsoft YaHei",
            FontSizePt = 9,
            RowHeightMm = RowHeightMm,
            Columns =
            {
                new TableColumn { Title = "名称", WidthMm = 80, Field = "rows.name" },
                new TableColumn { Title = "序号", WidthMm = 20, Field = "rows.name", Sequence = true },
                new TableColumn { Title = "金额", WidthMm = -1, Field = "rows.amount", Summary = TableSummary.Sum, SummaryLabel = "本页小计：" },
            },
        };

        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report,
            data: PayloadOf(10, 20, 30, 40, 50),
            tableWindows: new Dictionary<string, TableWindow> { ["t"] = new TableWindow(2, 2) });

        var rows = TableRenderer.ResolveRows(o, context, "t", null, out var dataStart, out var dataItems, out var rawRows);
        report.HasIssues.Should().BeFalse();
        dataStart.Should().Be(0, "没有静态行时数据行从 0 开始");
        rows.Should().HaveCount(2, "窗口只放行 [2, 4) 区间的数据行");
        dataItems.Should().HaveCount(2);

        rows[0][1].Should().Be("3", "序号跨页连续：全局第 3 行");
        rows[1][1].Should().Be("4", "序号跨页连续：全局第 4 行");

        var widths = TableRenderer.ResolveWidths(o, 190);
        using var pool = new TableRenderer.TableStylePool(context);
        var footer = TableRenderer.LayoutFooter(o, pool, rows, rawRows, dataStart, widths, "t");
        footer.Should().NotBeNull();
        footer!.Cells[2][0].Should().Be("本页小计：70.00", "汇总只统计窗口内的数据行，不是全单");
    }

    [Fact]
    public void 页码绑定回退读渲染上下文()
    {
        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report, pageNo: 3, pageTotal: 22);

        var text = DataBinder.BindText("第 {{pageNo}} 页 / 共 {{pageCount}} 页", null, report, "e", context: context);

        text.Should().Be("第 3 页 / 共 22 页");
        report.HasIssues.Should().BeFalse("页码回退成功就不该报缺字段");
    }

    [Fact]
    public void 数据字段优先于页码回退()
    {
        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report, pageNo: 3, pageTotal: 22);

        var text = DataBinder.BindText("{{pageNo}}/{{pageCount}}", new { pageNo = "X" }, report, "e", context: context);

        text.Should().Be("X/22", "数据源里真有 pageNo 字段时以数据为准");
    }

    [Fact]
    public void 无页码语义时照常报缺字段()
    {
        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report);

        var text = DataBinder.BindText("{{pageNo}}", null, report, "e", context: context);

        text.Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField,
            "页码是打印会话的事实，静默印成空串比一句告警更难排查");
    }

    [Fact]
    public void 续页是纯表格只带页码页脚()
    {
        var (template, tableId) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));

        pages.Count.Should().BeGreaterThan(2, "120 行要有首页、中间页、尾页三种形态");
        pages.Should().Contain(p => p.PageNo > 1 && p.PageNo < p.PageTotal, "120 行必然产生中间续页");

        // 中间页：只画表格克隆体 + 页码页脚，信头与落款区都不出现
        foreach (var page in pages.Where(p => p.PageNo > 1 && p.PageNo < p.PageTotal))
        {
            page.Template.Elements.Select(e => e.Id).Should()
                .BeEquivalentTo(["big-table", "page-footer"], "中间页 = 纯表格续行 + 页码");
            var table = page.Template.Elements.OfType<TableElement>().Single();
            table.Id.Should().Be(tableId);
            table.YMm.Should().Be(template.Margins.Top, "续页表格顶格接着排，不重复信头留白");
            table.HeightMm.Should().Be(
                template.Paper.PageHeightMm - template.Margins.Bottom - template.Margins.Top,
                "续页表格撑满整个内容区");
        }
    }

    [Fact]
    public void 首页画信头不画落款区()
    {
        var (template, _) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));

        var first = pages[0].Template.Elements.Select(e => e.Id).ToList();
        first.Should().Contain(["letterhead", "big-table", "page-footer"], "首页 = 信头 + 表格开头 + 页码");
        first.Should().NotContain(["grand-total", "signature"], "表格还在续，全单合计与签字只属于最后一页");
    }

    [Fact]
    public void 首页表格撑到内容区底()
    {
        var (template, _) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        var table = pages[0].Template.Elements.OfType<TableElement>().Single();

        table.YMm.Should().Be(TableTopMm, "首页表格不挪位");
        table.HeightMm.Should().Be(
            template.Paper.PageHeightMm - template.Margins.Bottom - TableTopMm,
            "落款区首页不画，表格应撑到内容区底，不在元素高处留一大段空白");
        template.Elements.OfType<TableElement>().Single().HeightMm.Should().Be(TableHeightMm, "原模板不被改动");
    }

    [Fact]
    public void 内容超出元素高但一页装得下时按一页出()
    {
        // 元素高 40mm：固定高 + 10 行(60mm) 装不下 → 触发分页；首页撑到内容区底后一页就够
        var (template, tableId) = BigInvoice(tableHeightMm: 40);

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(10));

        pages.Should().ContainSingle();
        pages[0].TableElementId.Should().Be(tableId, "虽然只有一页，但仍是分页语义：表格被撑高、落款区让位");
        pages[0].PageNo.Should().Be(1);
        pages[0].PageTotal.Should().Be(1);
        pages[0].Template.Elements.OfType<TableElement>().Single().HeightMm.Should().Be(
            template.Paper.PageHeightMm - template.Margins.Bottom - TableTopMm);
    }

    [Fact]
    public void 尾页回填落款区且窗口不压落款()
    {
        var (template, tableId) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        var last = pages[^1];

        // 尾页 = 表格续行 + 落款区回填原位 + 页码；信头仍然不画
        var ids = last.Template.Elements.Select(e => e.Id).ToList();
        ids.Should().Contain(["big-table", "page-footer", "grand-total", "signature"], "全单合计与签字落在最后一页");
        ids.Should().NotContain("letterhead");

        var table = last.Template.Elements.OfType<TableElement>().Single();
        table.YMm.Should().Be(template.Margins.Top);
        table.HeightMm.Should().Be(TableHeightMm + TableTopMm - template.Margins.Top,
            "尾页表格高度到模板表格底边为止，给落款区留出模板原位");

        // 尾页窗口按预留容量切片，真实渲染不得溢出
        var report = new RenderReport();
        RenderOnBitmap(last.Template, report, new Dictionary<string, TableWindow> { [tableId] = last.Window },
            last.PageNo, last.PageTotal);
        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow,
            "尾页窗口被'表格底边 − 上留白'收口，不会压到落款区");
    }

    [Fact]
    public void 续页窗口渲染无溢出()
    {
        var (template, tableId) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));

        foreach (var page in pages.Where(p => p.PageNo > 1))
        {
            var report = new RenderReport();
            RenderOnBitmap(page.Template, report,
                new Dictionary<string, TableWindow> { [tableId] = page.Window }, page.PageNo, page.PageTotal);
            report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow,
                $"第 {page.PageNo} 页的窗口按该页容量切出，必然装得下");
        }
    }

    [Fact]
    public void 续页每一行都画出行文字()
    {
        // 回归：LayoutRows 曾把行号 r 与列数比较，行号 ≥ 列数的行整行换成空串——
        // 续页从第「列数+1」行起只见底色不见文字。这里逐行扫描中间续页的文字墨迹。
        var (template, tableId) = ChromeInvoice();

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        var page = pages[1];

        using var bitmap = new SKBitmap(new SKImageInfo(
            (int)Math.Ceiling(template.Paper.PageWidthMm * 96 / 25.4),
            (int)Math.Ceiling(template.Paper.PageHeightMm * 96 / 25.4),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var report = new RenderReport();
        TemplateRenderer.Default.Render(page.Template, canvas, new RenderContext(
            new RenderOptions { Dpi = 96 }, report, data: Payload(RowCount),
            tableWindows: new Dictionary<string, TableWindow> { [tableId] = page.Window },
            pageNo: page.PageNo, pageTotal: page.PageTotal));

        var pxPerMm = 96 / 25.4;
        var bodyTop = (template.Margins.Top + RowHeightMm) * pxPerMm; // 单行表头高 = RowHeightMm
        var rowH = RowHeightMm * pxPerMm;
        for (var r = 0; r < 8; r++)
        {
            var center = (int)(bodyTop + (r + 0.5) * rowH);
            var hasText = false;
            for (var dy = -3; dy <= 3 && !hasText; dy++)
            {
                for (var x = 8; x < bitmap.Width - 8 && !hasText; x += 2)
                {
                    var c = bitmap.GetPixel(x, center + dy);
                    if (c.Red < 90 && c.Green < 90 && c.Blue < 90)
                        hasText = true;
                }
            }

            hasText.Should().BeTrue($"续页第 {r + 1} 行必须画出文字，不能是有底色的空白行");
        }
    }

    [Fact]
    public void 批量导出的页数等于分页计划页数()
    {
        var (template, _) = BigInvoice();

        using var stream = new MemoryStream();
        var (report, pages) = BatchPdfExporter.ExportPages(
            [template], stream, PaginationOptions(), _ => Payload(RowCount));

        pages.Should().BeGreaterThan(1);
        PdfProbe.PageCount(stream.ToArray()).Should().Be(pages, "回传的页数必须与真实页框数一致");
        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow, "分页后每页都装得下");
    }

    [Fact]
    public void 分页变体保留样式库_续页命名样式墨迹不丢()
    {
        // 回归：ShallowWithElements 曾丢 Styles，页面变体样式库为空——缺样式的引用按设计静默忽略，
        // 于是第 2 页起所有命名样式（颜色/加粗）无声降级，比报错更难发现。
        var (template, tableId) = StyledFooterInvoice(styled: true);

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        pages.Should().HaveCountGreaterThan(1, "120 行必然产生续页变体");

        foreach (var page in pages)
        {
            using var bitmap = RenderPage(page.Template, tableId, page);
            Ink.ColorCountInMm(bitmap, 96, FooterWindowMm, SKColors.Red, tolerance: 127)
                .Should().BeGreaterThan(20, $"第 {page.PageNo} 页页脚必须画出命名样的红字（缺样式是静默降级，只能取色取证）");

            page.Template.Styles.Should().NotBeEmpty($"第 {page.PageNo} 页的变体必须带上样式库");
        }
    }

    [Fact]
    public void 无样式孪生模板页脚不出红_正向对照()
    {
        // 正向对照：同一版式去掉样式库，页脚照印有墨、但没有红——红色判据不是虚设
        var (template, tableId) = StyledFooterInvoice(styled: false);

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        var page = pages[1];
        using var bitmap = RenderPage(page.Template, tableId, page);
        Ink.ColorCountInMm(bitmap, 96, FooterWindowMm, SKColors.Red, tolerance: 127)
            .Should().Be(0, "没有样式库就没有红字");
        Ink.DarkPixelCountInMm(bitmap, 96, FooterWindowMm)
            .Should().BeGreaterThan(20, "对照页只是没上色，文字本身必须照常画出");
    }

    [Fact]
    public void 字面含pageNo的标签不被当页码元素每页重印()
    {
        // 取证 BindsPageNumber 的误报：页码判据必须落在 {{...}} 绑定占位符上，
        // 字面提到 pageNo 的普通标签（字段名清单之类）不是页码，不该出现在每个中间续页。
        var (template, tableId) = StyledFooterInvoice(styled: false, literalPageNoLabel: true);

        var pages = TemplatePaginator.PlanLayout(template, PaginationOptions(), Payload(RowCount));
        pages.Should().HaveCountGreaterThan(2);

        foreach (var page in pages.Where(p => p.PageNo > 1 && p.PageNo < p.PageTotal))
        {
            page.Template.Elements.Select(e => e.Id).Should().NotContain("no-label",
                $"第 {page.PageNo} 页是纯表格续行，字面含 pageNo 的信头标签不属于这里");
            page.Template.Elements.Select(e => e.Id).Should().Contain("page-footer",
                "真正的 {{pageNo}} 绑定页码仍然每页都画");
        }

        pages[0].Template.Elements.Select(e => e.Id).Should().Contain("no-label", "首页照常画信头标签");
    }

    // ── 造数 ──

    static RenderOptions PaginationOptions() => new() { AllowTablePagination = true };

    static List<InvoiceRow> Rows(int count)
        => Enumerable.Range(1, count)
            .Select(i => new InvoiceRow { Name = $"商品#{i:D4}", Qty = i % 4 + 1, Amount = i * 1.37m })
            .ToList();

    /// <summary>绑定源：模板列字段是 <c>rows.name</c> 等路径，数据要带 <c>rows</c> 集合属性。</summary>
    static object Payload(int count) => new { rows = Rows(count) };

    /// <summary>指定金额序列的绑定源（窗口切片单测用）。</summary>
    static object PayloadOf(params int[] amounts)
        => new { rows = amounts.Select((a, i) => new InvoiceRow { Name = $"商品{i + 1}", Qty = 1, Amount = a }).ToList() };

    const double TableTopMm = 92;

    /// <summary>
    /// 带页面装饰的大对账单：信头（表格上方）、落款区（表格下方：合计 + 签字）、页码页脚。
    /// 用于验证三段式页面语义——续页纯表格、首页不画落款、尾页回填落款。
    /// </summary>
    static (PrintTemplate Template, string TableId) ChromeInvoice()
    {
        var (template, tableId) = BigInvoice();
        template.Elements.Add(new TextElement
        {
            Id = "letterhead",
            XMm = 10,
            YMm = 10,
            WidthMm = 190,
            HeightMm = 12,
            Text = new TextOptions { Content = "{{invoiceNo}}", FontFamily = "Microsoft YaHei", FontSizePt = 14 },
        });
        template.Elements.Add(new TextElement
        {
            Id = "grand-total",
            XMm = 100,
            YMm = 253,
            WidthMm = 100,
            HeightMm = 6,
            Text = new TextOptions { Content = "合计：￥{{totalAmount}}", FontFamily = "Microsoft YaHei", FontSizePt = 10 },
        });
        template.Elements.Add(new TextElement
        {
            Id = "signature",
            XMm = 10,
            YMm = 272,
            WidthMm = 190,
            HeightMm = 5.5,
            Text = new TextOptions { Content = "收款人：{{payee}}", FontFamily = "Microsoft YaHei", FontSizePt = 8.5 },
        });
        template.Elements.Add(new TextElement
        {
            Id = "page-footer",
            XMm = 10,
            YMm = 284,
            WidthMm = 190,
            HeightMm = 4,
            Text = new TextOptions { Content = "第 {{pageNo}} 页 / 共 {{pageCount}} 页", FontFamily = "Microsoft YaHei", FontSizePt = 6.5 },
        });
        return (template, tableId);
    }

    /// <summary>
    /// 带命名样式的可分页对账单：页脚绑定 {{pageNo}}（每页都画），样式库给页脚配红粗体；
    /// <c>styled: false</c> 是同版式无样式库的对照孪生；<c>literalPageNoLabel</c> 加一条字面含
    /// "pageNo" 的信头标签（无占位符，验证页码判据不被字面词误伤）。
    /// </summary>
    static (PrintTemplate Template, string TableId) StyledFooterInvoice(bool styled, bool literalPageNoLabel = false)
    {
        var (template, tableId) = BigInvoice();
        if (styled)
            template.Styles.Add(new NamedStyle { Id = "st-alert", TextColor = SKColors.Red, Bold = true });
        template.Elements.Add(new TextElement
        {
            Id = "page-footer",
            XMm = 10,
            YMm = 284,
            WidthMm = 190,
            HeightMm = 6,
            Text = new TextOptions
            {
                Content = "第 {{pageNo}} 页 红字取证",
                StyleId = "st-alert",
                FontFamily = "Microsoft YaHei",
                FontSizePt = 10,
            },
        });
        if (literalPageNoLabel)
            template.Elements.Add(new TextElement
            {
                Id = "no-label",
                XMm = 10,
                YMm = 20,
                WidthMm = 100,
                HeightMm = 6,
                Text = new TextOptions { Content = "pageNoList：随单流水清单", FontFamily = "Microsoft YaHei", FontSizePt = 10 },
            });
        return (template, tableId);
    }

    /// <summary>页脚取色窗口（mm）：只看页脚元素那一横条。</summary>
    static readonly SKRect FooterWindowMm = new(10, 283, 200, 291);

    static SKBitmap RenderPage(PrintTemplate template, string tableId, TemplatePaginator.PaginationPage page)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            (int)Math.Ceiling(template.Paper.PageWidthMm * 96 / 25.4),
            (int)Math.Ceiling(template.Paper.PageHeightMm * 96 / 25.4),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var report = new RenderReport();
        TemplateRenderer.Default.Render(template, canvas, new RenderContext(
            new RenderOptions { Dpi = 96 }, report, data: Payload(RowCount),
            tableWindows: new Dictionary<string, TableWindow> { [tableId] = page.Window },
            pageNo: page.PageNo, pageTotal: page.PageTotal));
        report.Issues.Where(i => i.Kind == RenderIssueKind.ElementSkipped).Should().BeEmpty();
        return bitmap;
    }

    /// <summary>大对账单：A4 定高纸 + 6mm 行高的溢出表格，必然可分页。</summary>
    static (PrintTemplate Template, string TableId) BigInvoice(double pageHeightMm = 297, double tableHeightMm = TableHeightMm)
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 210, HeightMm = pageHeightMm },
        };
        var table = new TableElement
        {
            Id = "big-table",
            XMm = 10,
            YMm = 92,
            WidthMm = 190,
            HeightMm = tableHeightMm,
            Table = new TableOptions
            {
                Columns =
                {
                    new TableColumn { Title = "货物名称", WidthMm = 90, Field = "rows.name" },
                    new TableColumn { Title = "数量", WidthMm = 40, Field = "rows.qty" },
                    new TableColumn { Title = "金额", WidthMm = -1, Field = "rows.amount", Summary = TableSummary.Sum, SummaryLabel = "本页小计：" },
                },
                FontFamily = "Microsoft YaHei",
                FontSizePt = 9,
                RowHeightMm = RowHeightMm,
            },
        };
        template.Elements.Add(table);
        return (template, table.Id);
    }

    /// <summary>小而确定的溢出案例：5 行 × 6mm + 表头 ≈ 36mm > 22mm 元素高；窗口取 2 行必然装得下。</summary>
    static (PrintTemplate Template, string TableId) SmallOverflowInvoice()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 210, HeightMm = 297 },
        };
        var table = new TableElement
        {
            Id = "small-table",
            XMm = 10,
            YMm = 10,
            WidthMm = 190,
            HeightMm = 22,
            Table = new TableOptions
            {
                Columns =
                {
                    new TableColumn { Title = "货物名称", WidthMm = 90, Field = "rows.name" },
                    new TableColumn { Title = "金额", WidthMm = -1, Field = "rows.amount" },
                },
                FontFamily = "Microsoft YaHei",
                FontSizePt = 9,
                RowHeightMm = RowHeightMm,
            },
        };
        template.Elements.Add(table);
        return (template, table.Id);
    }

    static IReadOnlyList<RenderIssue> RenderIssues(
        PrintTemplate template,
        IReadOnlyDictionary<string, TableWindow>? windows,
        int pageNo = 0,
        int pageTotal = 0)
    {
        var report = new RenderReport();
        RenderOnBitmap(template, report, windows, pageNo, pageTotal, Payload(5));
        return report.Issues;
    }

    static void RenderOnBitmap(
        PrintTemplate template,
        RenderReport report,
        IReadOnlyDictionary<string, TableWindow>? windows,
        int pageNo,
        int pageTotal,
        object? data = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            (int)Math.Ceiling(template.Paper.PageWidthMm * 96 / 25.4),
            (int)Math.Ceiling(template.Paper.PageHeightMm * 96 / 25.4),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        TemplateRenderer.Default.Render(template, canvas, new RenderContext(
            new RenderOptions { Dpi = 96 }, report, data: data ?? Payload(RowCount),
            tableWindows: windows, pageNo: pageNo, pageTotal: pageTotal));
    }

    sealed class InvoiceRow
    {
        public string Name { get; init; } = "";
        public int Qty { get; init; }
        public decimal Amount { get; init; }
    }
}
