using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Printing;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// 表格跨页分页的打印端到端：PrintedPages 必须是引擎回传的真实页数（续页后大于文档数），
/// 产物 PDF 装得下所有数据行且不再报 TableOverflow。
/// </summary>
public sealed class TablePaginationPrintTests : IDisposable
{
    readonly string _workDir = Path.Combine(Path.GetTempPath(), $"crossprint-paginate-{Guid.NewGuid():N}");

    public TablePaginationPrintTests() => Directory.CreateDirectory(_workDir);

    [Fact]
    public async Task 表格跨页打印按真实页数计页()
    {
        var (template, tableId) = BigInvoice();
        const int rowCount = 120;
        var outPath = Path.Combine(_workDir, "invoice-paginated.pdf");

        var issues = new RenderReport();
        var result = await new PdfFilePrintTarget().PrintAsync(
            new PrintDocument(template, Payload(rowCount)),
            new PrintSettings(outPath)
            {
                Options = new RenderOptions { AllowTablePagination = true, PdfTextAsOutlines = true },
            },
            issues,
            default);

        result.Success.Should().BeTrue(result.Message);
        var planned = TemplatePaginator.GetPageCount(
            template, new RenderOptions { AllowTablePagination = true }, Payload(rowCount));
        result.PrintedPages.Should().Be(planned, "PrintedPages 用引擎回传的真实页数，不是文档数");
        result.PrintedPages.Should().BeGreaterThan(1, "120 行 × 6mm 在 158mm 表格里必须续页");
        File.Exists(outPath).Should().BeTrue();
        issues.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow, "分页后每页都装得下");
        issues.Issues.Should().NotContain(i => i.ElementId == tableId && i.Kind == RenderIssueKind.BindingMissingField);
    }

    [Fact]
    public async Task 不开开关时维持单页并报溢出()
    {
        var (template, _) = BigInvoice();
        var outPath = Path.Combine(_workDir, "invoice-legacy.pdf");

        var issues = new RenderReport();
        var result = await new PdfFilePrintTarget().PrintAsync(
            new PrintDocument(template, Payload(120)),
            new PrintSettings(outPath) { Options = new RenderOptions { PdfTextAsOutlines = true } },
            issues,
            default);

        result.Success.Should().BeTrue(result.Message);
        result.PrintedPages.Should().Be(1, "开关没开就是一页，行为不悄悄变");
        issues.Issues.Should().Contain(i => i.Kind == RenderIssueKind.TableOverflow, "放不下的内容要有人说话");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── 造数 ──

    static List<InvoiceRow> Rows(int count)
        => Enumerable.Range(1, count)
            .Select(i => new InvoiceRow { Name = $"商品#{i:D4}", Qty = i % 4 + 1, Amount = i * 1.37m })
            .ToList();

    /// <summary>绑定源：模板列字段是 <c>rows.*</c> 路径，数据要带 <c>rows</c> 集合属性。</summary>
    static object Payload(int count) => new { rows = Rows(count) };

    static (PrintTemplate Template, string TableId) BigInvoice()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 210, HeightMm = 297 },
        };
        var table = new TableElement
        {
            Id = "big-table",
            XMm = 10,
            YMm = 92,
            WidthMm = 190,
            HeightMm = 158,
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
                RowHeightMm = 6,
            },
        };
        template.Elements.Add(table);
        return (template, table.Id);
    }

    sealed class InvoiceRow
    {
        public string Name { get; init; } = "";
        public int Qty { get; init; }
        public decimal Amount { get; init; }
    }
}
