using System.Text;
using System.Text.RegularExpressions;
using CrossPrint.Core;
using CrossPrint.Engine;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 端到端复现"小票截断"：真实 complex-receipt 样例 + 500 行明细走批量 PDF 导出，
/// MediaBox 必须按自动增高后的纸高算（此前导出器用原模板算页高，纸盒比内容矮，
/// 打到一半戛然而止）。也顺便钉住页数语义：连续纸永远 1 页，行多只是纸变长。
/// </summary>
public sealed class ContinuousReceiptExportTests
{
    static string SamplePath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir, "docs", "samples", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"未找到 docs/samples/{fileName}（测试需要从输出目录向上定位仓库根）。");
    }

    static PrintTemplate Receipt() => TemplateJson.Deserialize(File.ReadAllText(SamplePath("complex-receipt.template.json")));

    static object Payload(int count)
        => new
        {
            lines = Enumerable.Range(1, count)
                .Select(i => new { name = $"商品#{i}", qty = i % 4 + 1, amount = i * 1.37m })
                .ToList(),
        };

    [Fact]
    public void 复杂小票_500行_批量导出_MediaBox覆盖全部内容()
    {
        var template = Receipt();
        using var stream = new MemoryStream();

        var (report, pageCount) = BatchPdfExporter.ExportPages(
            [template],
            stream,
            new RenderOptions { Dpi = 96 },
            _ => Payload(500));

        pageCount.Should().Be(1, "连续纸不分页：行多只是纸变长");
        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow,
            "表格自动增高后不存在被裁剪的内容");

        var pdf = Encoding.ASCII.GetString(stream.ToArray());
        var match = Regex.Match(pdf, @"MediaBox \[0 0 ([\d.]+) ([\d.]+)\]");
        match.Success.Should().BeTrue();
        var heightPt = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

        // 增高后的表格 ≈ 表头 6 + 500 行 × 6mm ≈ 3006mm + 尾块；折算 ≈ 8500pt 以上。
        // 若 MediaBox 还是原模板的内容高（~185mm ≈ 524pt），说明纸盒没跟着增高——正是"截断"的病根。
        heightPt.Should().BeGreaterThan(8500, "500 行小票的纸长约 3 米，MediaBox 必须覆盖到最后一行");
    }
}
