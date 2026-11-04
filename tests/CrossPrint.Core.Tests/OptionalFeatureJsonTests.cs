using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>可选增强功能的 JSON 往返：水印绑定/测试数据、二维码 Logo、条形码标题、表格跨页表头。</summary>
public class OptionalFeatureJsonTests
{
    [Fact]
    public void Watermark_Binding_TestData_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Watermark.Binding = "order.no";
        t.Watermark.TestData = "SO-2026-001";

        var json = TemplateJson.Serialize(t);
        var back = TemplateJson.Deserialize(json);
        back.Watermark.Binding.Should().Be("order.no");
        back.Watermark.TestData.Should().Be("SO-2026-001");

        // 未设置时不得长出噪音字段（默认模板体积口径）。
        TemplateJson.Serialize(new PrintTemplate())
            .Should().NotContain("\"binding\"").And.NotContain("\"testData\"");
    }

    [Fact]
    public void Shape_Element_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new ShapeElement
        {
            XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 30,
            Shape = new PolygonOptions
            {
                Kind = ShapeKind.Star5,
                FillColor = new SKColor(255, 200, 0),
                StrokeColor = SKColors.Black,
                StrokeWidthMm = 0.35,
                CornerRadiusMm = 2,
                DashPatternMm = new[] { 1.5, 1.0 },
            },
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        var shape = back.Elements.OfType<ShapeElement>().Single();
        shape.Shape.Kind.Should().Be(ShapeKind.Star5);
        shape.Shape.FillColor.Should().Be(new SKColor(255, 200, 0));
        shape.Shape.StrokeWidthMm.Should().Be(0.35);
        shape.Shape.CornerRadiusMm.Should().Be(2);
        shape.Shape.DashPatternMm.Should().BeEquivalentTo(new[] { 1.5, 1.0 });

        // kind 以枚举成员名的字符串形态写入 JSON（人可读、手改友好）。
        var shapeJson = TemplateJson.Serialize(t);
        (shapeJson.Contains("\"kind\": \"Star5\"", StringComparison.Ordinal)
            || shapeJson.Contains("\"kind\":\"Star5\"", StringComparison.Ordinal))
            .Should().BeTrue("kind 应以枚举名字符串写入 JSON");
    }

    [Fact]
    public void Element_Binding_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new TextElement
        {
            XMm = 5, YMm = 5, WidthMm = 60, HeightMm = 10,
            Binding = "order.customer.name",
            TestData = "张三",
            Text = new TextOptions { Content = string.Empty },
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        var el = back.Elements.OfType<TextElement>().Single();
        el.Binding.Should().Be("order.customer.name");
        el.TestData.Should().Be("张三");

        // 未绑定的元素不得长出噪音字段（默认模板体积口径）。
        var plain = new PrintTemplate();
        plain.Add(new TextElement { XMm = 1, YMm = 1, WidthMm = 10, HeightMm = 10 });
        TemplateJson.Serialize(plain).Should().NotContain("\"binding\"");
    }

    [Fact]
    public void QrCode_LogoOptions_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new QrCodeElement
        {
            XMm = 5, YMm = 5, WidthMm = 40, HeightMm = 40,
            QrCode = new QrCodeOptions
            {
                Content = "https://example.com",
                ShowCaption = true,
                Caption = "扫码下单",
                LogoData = "data:image/png;base64,AAAA",
                LogoField = "{{logo}}",
                LogoFit = QrLogoFit.Fill,
            },
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        var o = back.Elements.OfType<QrCodeElement>().Single().QrCode;
        o.ShowCaption.Should().BeTrue();
        o.Caption.Should().Be("扫码下单");
        o.LogoData.Should().Be("data:image/png;base64,AAAA");
        o.LogoField.Should().Be("{{logo}}");
        o.LogoFit.Should().Be(QrLogoFit.Fill);
    }

    [Fact]
    public void Barcode_CaptionOptions_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new BarcodeElement
        {
            XMm = 5, YMm = 5, WidthMm = 80, HeightMm = 24,
            Barcode = new BarcodeOptions
            {
                Content = "ABC",
                Symbology = BarcodeSymbology.Code128,
                ShowCaption = true,
                Caption = "HELLO {{name}}",
                CaptionFontSizePt = 12,
                CaptionColor = new SKColor(0xFF, 0x00, 0x00),
                CaptionAlign = HorizontalAlign.Left,
            },
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        var o = back.Elements.OfType<BarcodeElement>().Single().Barcode;
        o.ShowCaption.Should().BeTrue();
        o.Caption.Should().Be("HELLO {{name}}");
        o.CaptionFontSizePt.Should().Be(12);
        o.CaptionColor.Should().Be(new SKColor(0xFF, 0x00, 0x00));
        o.CaptionAlign.Should().Be(HorizontalAlign.Left);
    }

    [Fact]
    public void Table_RepeatHeaderOption_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new TableElement
        {
            XMm = 5, YMm = 5, WidthMm = 100, HeightMm = 60,
            Table = new TableOptions { ShowHeader = true, RepeatHeaderOnEachPage = false },
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        var o = back.Elements.OfType<TableElement>().Single().Table;
        o.ShowHeader.Should().BeTrue();
        o.RepeatHeaderOnEachPage.Should().BeFalse();
    }

    [Fact]
    public void PaperNumber_EnabledOptions_RoundTrip()
    {
        var t = new PrintTemplate();
        t.PaperNumberEnabled = true;
        t.PaperNumberFormat = "{page}/{pages}";
        t.PaperNumberXMm = 180;
        t.PaperNumberYMm = 285;

        var json = TemplateJson.Serialize(t);
        var back = TemplateJson.Deserialize(json);
        back.PaperNumberEnabled.Should().BeTrue();
        back.PaperNumberFormat.Should().Be("{page}/{pages}");
        back.PaperNumberXMm.Should().Be(180);
        back.PaperNumberYMm.Should().Be(285);

        // 再序列化文本一致（确定性往返）
        TemplateJson.Serialize(back).Should().Be(json);
    }

    [Fact]
    public void PaperNumber_MissingField_ReadsBackDisabled()
    {
        const string json = """
            { "schemaVersion": "0.1", "paper": { "widthMm": 210, "heightMm": 297, "orientation": "Portrait" } }
            """;

        var back = TemplateJson.Deserialize(json);
        back.PaperNumberEnabled.Should().BeFalse();
        back.PaperNumberFormat.Should().BeNull();
        back.PaperNumberXMm.Should().BeNull();
        back.PaperNumberYMm.Should().BeNull();
    }

    [Fact]
    public void PaginationFlags_RoundTrip()
    {
        var t = new PrintTemplate();
        t.Add(new TextElement
        {
            XMm = 5, YMm = 5, WidthMm = 100, HeightMm = 10,
            Text = new TextOptions { Content = "表头" },
            Fixed = true,
            ShowInPages = "1,3-5",
            UnShowInPages = "7-8",
        });
        t.Add(new TextElement
        {
            XMm = 5, YMm = 20, WidthMm = 100, HeightMm = 10,
            Text = new TextOptions { Content = "分页点" },
            PageBreakAfter = true,
        });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));

        var header = (TextElement)back.Elements[0];
        header.Fixed.Should().BeTrue();
        header.ShowInPages.Should().Be("1,3-5");
        header.UnShowInPages.Should().Be("7-8");
        header.PageBreakAfter.Should().BeFalse();

        var breakPoint = (TextElement)back.Elements[1];
        breakPoint.PageBreakAfter.Should().BeTrue();
        breakPoint.Fixed.Should().BeFalse();
        breakPoint.ShowInPages.Should().BeNull();
    }
}
