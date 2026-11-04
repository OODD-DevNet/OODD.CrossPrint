using CrossPrint.Core.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>文字水印：默认模板留白与 JSON 往返；绘制像素级断言见 CrossPrint.Engine.Tests.WatermarkRenderTests。</summary>
public class WatermarkTests
{
    [Fact]
    public void 默认模板_留白10mm_安全区辅助线不再压在纸边上()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        template.Margins.Left.Should().Be(10);
        template.Margins.Top.Should().Be(10);
        template.Margins.Right.Should().Be(10);
        template.Margins.Bottom.Should().Be(10);
    }

    [Fact]
    public void 水印_JSON_往返无损()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        template.Watermark.Enabled = true;
        template.Watermark.Text = "绝密";
        template.Watermark.FontSizePt = 18;
        template.Watermark.Color = new SKColor(0x12, 0x34, 0x56);
        template.Watermark.Opacity = 0.25;
        template.Watermark.RotationDeg = -45;
        template.Watermark.SpacingMm = 80;

        var json = TemplateJson.Serialize(template, indented: false);
        var reloaded = TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant).Template;

        reloaded.Watermark.Enabled.Should().BeTrue();
        reloaded.Watermark.Text.Should().Be("绝密");
        reloaded.Watermark.FontSizePt.Should().Be(18);
        reloaded.Watermark.Color.Should().Be(new SKColor(0x12, 0x34, 0x56));
        reloaded.Watermark.Opacity.Should().Be(0.25);
        reloaded.Watermark.RotationDeg.Should().Be(-45);
        reloaded.Watermark.SpacingMm.Should().Be(80);

        // 旧 JSON 没有水印字段：读回是默认关闭，零迁移。
        var legacy = TemplateJson.Load(
            "{\"schemaVersion\":\"0.1\",\"paper\":{\"preset\":\"A4\"},\"elements\":[]}",
            TemplateJsonLoadOptions.Tolerant).Template;
        legacy.Watermark.Enabled.Should().BeFalse();
    }
}
