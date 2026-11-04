using CrossPrint.Core;
using CrossPrint.Core.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 文字水印渲染：启用真出半透明像素、关闭没有（模板无元素时非白像素只可能来自水印），
/// 与位置无关的元素路径不被水印污染（水印垫底）。
/// </summary>
public class WatermarkRenderTests
{
    [Fact]
    public void 启用水印_画布上真出像素_关闭则没有()
    {
        const double unitsPerMm = 96.0 / 25.4;
        var width = (int)(210 * unitsPerMm);
        var height = (int)(297 * unitsPerMm);

        SKBitmap Render(bool enabled)
        {
            var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
            template.Elements.Clear();
            template.Watermark = new WatermarkOptions
            {
                Enabled = enabled,
                Text = "内部资料",
                FontSizePt = 14,
                Opacity = 0.5,
            };
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            new TemplateRenderer().Render(template, canvas, new RenderOptions { Dpi = 96 });
            return bitmap;
        }

        using (var on = Render(true))
        using (var off = Render(false))
        {
            CountWatermarkPixels(on).Should().BeGreaterThan(0, "启用水印后应画出半透明文字");
            CountWatermarkPixels(off).Should().Be(0, "关闭水印不应有任何非白像素（模板无元素）");
        }
    }

    [Fact]
    public void 水印占位符_数据命中出墨_数据缺失不画字面占位符()
    {
        const double unitsPerMm = 96.0 / 25.4;
        var width = (int)(210 * unitsPerMm);
        var height = (int)(297 * unitsPerMm);

        SKBitmap Render(object? data)
        {
            var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
            template.Elements.Clear();
            template.Watermark = new WatermarkOptions
            {
                Enabled = true,
                Text = "{{company}}",
                FontSizePt = 14,
                Opacity = 0.5,
            };
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            new TemplateRenderer().Render(template, canvas, new RenderOptions { Dpi = 96 }, data);
            return bitmap;
        }

        // 数据命中：绑成字段值出墨；数据缺失：BindText 把占位符换空串 → 一个字的墨都没有
        // （若不解析，字面 "{{company}}" 本身就有墨，两个断言都无法区分）。
        using (var bound = Render(new Dictionary<string, object> { ["company"] = "星辰制造" }))
        using (var missing = Render(null))
        {
            CountWatermarkPixels(bound).Should().BeGreaterThan(0, "占位符解析到数据后应画出字段值");
            CountWatermarkPixels(missing).Should().Be(0, "缺数据时占位符应替换为空串，不能把字面 {{company}} 印上去");
        }
    }

    [Fact]
    public void 水印字段绑定_纯字段名等价占位符_测试数据兜底()
    {
        const double unitsPerMm = 96.0 / 25.4;
        var width = (int)(210 * unitsPerMm);
        var height = (int)(297 * unitsPerMm);

        SKBitmap Render(string? binding, string? testData, object? data)
        {
            var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
            template.Elements.Clear();
            template.Watermark = new WatermarkOptions
            {
                Enabled = true,
                Text = "内部资料",
                Binding = binding,
                TestData = testData,
                FontSizePt = 14,
                Opacity = 0.5,
            };
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            new TemplateRenderer().Render(template, canvas, new RenderOptions { Dpi = 96 }, data);
            return bitmap;
        }

        // 绑定命中：印字段值（有墨）；绑定缺失：印空串（无墨）——与元素 binding 同口径。
        using (var bound = Render("order.no", null, new Dictionary<string, object> { ["order"] = new Dictionary<string, object> { ["no"] = "SO-1" } }))
        using (var missing = Render("order.no", null, null))
        {
            CountWatermarkPixels(bound).Should().BeGreaterThan(0, "绑定命中应印出字段值");
            CountWatermarkPixels(missing).Should().Be(0, "绑定缺失应整段替换为空串（水印无字可画），不能回落文字内容");
        }

        // 测试数据：真实打印（preferTestData=false）时仅作缺字段兜底。
        using (var fallback = Render("order.no", "SO-TEST", null))
        {
            CountWatermarkPixels(fallback).Should().BeGreaterThan(0, "缺字段时测试数据应兜底，避免整页水印静默消失");
        }
    }

    static long CountWatermarkPixels(SKBitmap bitmap)
    {
        long count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) is { } px && !(px.Red == 255 && px.Green == 255 && px.Blue == 255))
                    count++;
        return count;
    }
}
