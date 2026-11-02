using CrossPrint.Core.Hiprint;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// 文档示例的可执行校验：docs/template-schema.md 里的 JSON 必须真的能加载并无损往返。
/// 文档与实现脱节是这类项目最常见的腐化方式，用测试钉住。
/// </summary>
public class DocumentedSampleTests
{
    static string SamplePath() => SamplePath("kitchen-sink.template.json");

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

    [Fact]
    public void Documented_sample_loads_without_warnings()
    {
        var result = TemplateJson.Load(File.ReadAllText(SamplePath()));

        result.Warnings.Should().BeEmpty();
        result.Template.Elements.Should().HaveCount(8);
        result.Template.Elements.Select(e => e.TypeKey)
            .Should().Equal("text", "line", "rectangle", "ellipse", "image", "qrcode", "barcode", "table");
    }

    [Fact]
    public void Documented_sample_round_trips_byte_identical()
    {
        var template = TemplateJson.Deserialize(File.ReadAllText(SamplePath()));

        var first = TemplateJson.Serialize(template, indented: false);
        var again = TemplateJson.Serialize(TemplateJson.Deserialize(first), indented: false);

        again.Should().Be(first);
    }

    [Fact]
    public void Documented_sample_keeps_every_declared_field_value()
    {
        var t = TemplateJson.Deserialize(File.ReadAllText(SamplePath()));

        var text = ((TextElement)t.Elements[0]).Text;
        text.Content.Should().Be("{{title}}");
        text.FontSizePt.Should().Be(18);
        text.Bold.Should().BeTrue();
        text.Underline.Should().BeTrue("样例声明的文字装饰必须真的读进模型");
        text.Color.Should().Be(new SkiaSharp.SKColor(0x11, 0x22, 0x33));
        text.VerticalAlign.Should().Be(VerticalAlign.Middle);

        var rectElement = (RectangleElement)t.Elements[2];
        var rect = rectElement.Rectangle;
        rectElement.BorderColor.Should().Be(new SkiaSharp.SKColor(0x2F, 0x55, 0x97));
        rectElement.BorderWidthMm.Should().Be(0.4);
        rect.FillColor.Should().Be(new SkiaSharp.SKColor(0xF0, 0xF4, 0xF8));
        rect.DashPatternMm!.Should().Equal(1.5, 1.0);

        var ellipse = ((EllipseElement)t.Elements[3]).Ellipse;
        ellipse.StrokeColor.Should().BeNull();

        var qr = ((QrCodeElement)t.Elements[5]).QrCode;
        qr.ErrorCorrection.Should().Be(QrErrorCorrection.Q);
        qr.QuietZoneMm.Should().Be(1.5);

        var barcode = ((BarcodeElement)t.Elements[6]).Barcode;
        barcode.Symbology.Should().Be(BarcodeSymbology.Code128);
        barcode.BackgroundColor.Should().BeNull();

        var table = ((TableElement)t.Elements[7]).Table;
        table.Columns.Should().HaveCount(3);
        table.Columns[1].Align.Should().Be(HorizontalAlign.Right);
        table.Columns[1].FontSizePt.Should().Be(8);
        table.Columns[2].WidthMm.Should().Be(-1);
        table.StripeColor.Should().Be(new SkiaSharp.SKColor(0xFA, 0xFA, 0xFA));

        t.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
        t.Margins.Top.Should().Be(10);
    }

    [Fact]
    public void Sample_page_size_matches_the_a4_preset()
    {
        var t = TemplateJson.Deserialize(File.ReadAllText(SamplePath()));

        t.Paper.PageWidthMm.Should().Be(210);
        t.Paper.PageHeightMm.Should().Be(297);
    }

    [Fact]
    public void Complex_receipt_sample_loads_without_warnings()
    {
        var result = TemplateJson.Load(File.ReadAllText(SamplePath("complex-receipt.template.json")));

        result.Warnings.Should().BeEmpty();
        result.Template.Paper.IsContinuous.Should().BeTrue("80mm 连续纸：页高自适应内容，用一根长小票承载'多页'的复杂样例");
        result.Template.Elements.Select(e => e.TypeKey).Should().OnlyContain(k => ElementTypeKeys.All.Contains(k));
        result.Template.Elements.Select(e => e.ZIndex).Should().BeInAscendingOrder("样例的层次就该是'后放的在上'，按放置顺序给 ZIndex");

        // 绑定了占位符的元素都必须带测试数据——设计态没接宿主数据时也得有东西可看。
        var bound = result.Template.Elements.Where(e => e switch
        {
            TextElement t => t.Text.Content.Contains("{{", StringComparison.Ordinal),
            QrCodeElement q => q.QrCode.Content.Contains("{{", StringComparison.Ordinal),
            BarcodeElement b => b.Barcode.Content.Contains("{{", StringComparison.Ordinal),
            _ => false,
        });
        bound.Should().NotBeEmpty();
        bound.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.TestData));
    }

    [Fact]
    public void Complex_receipt_sample_round_trips_byte_identical()
    {
        var template = TemplateJson.Deserialize(File.ReadAllText(SamplePath("complex-receipt.template.json")));

        var first = TemplateJson.Serialize(template, indented: false);
        var again = TemplateJson.Serialize(TemplateJson.Deserialize(first), indented: false);

        again.Should().Be(first);
    }

    [Fact]
    public void Shipped_hiprint_sample_imports_without_warnings()
    {
        // 六个宿主样例的「文件 → 导入 hiprint 模板…」默认指向这份文件；
        // 它必须真的能导入且零告警——末尾元素的 formatter 会保留入库由引擎执行，不再按"无法执行"降级。
        var result = HiprintTemplateImporter.Import(File.ReadAllText(SamplePath("hiprint-sample.js")));

        result.HasErrors.Should().BeFalse();
        result.Template.Elements.Select(e => e.TypeKey)
            .Should().Equal("text", "line", "text", "text", "qrcode", "barcode", "table", "text");
        result.Issues.Should().BeEmpty();

        var formatted = result.Template.Elements.OfType<TextElement>().Last();
        formatted.Formatter.Should().Contain("NO-", "元素级 formatter 随导入入库");
    }
}
