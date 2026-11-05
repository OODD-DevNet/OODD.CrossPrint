using CrossPrint.Core;
using CrossPrint.Core.Rendering;
using CrossPrint.Engine;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 连续纸（小票）表格自动增高：表格内容超过元素高度时**不裁剪**，而是增高表格、
/// 顺移下方元素、页高随内容自适应（用户口径：小票数据想加多少行就多少行，
/// 此前 complex-receipt 表格元素只有 30mm，加再多行也只显示 4~5 条）。
/// 固定纸张不适用（那是分页语义，归 TemplatePaginator），设计态预览也保持原版式。
/// </summary>
public sealed class TemplateAutoGrowTests
{
    const double Dpi = 96;

    /// <summary>76mm 宽连续纸小票：表格元素高 30mm（约装 4 行），下方 Y=45 有合计文本。</summary>
    static PrintTemplate Receipt()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 76, HeightMm = 0 }, // 高 0 = 连续纸
        };
        template.Elements.Add(new TableElement
        {
            Id = "t",
            XMm = 2,
            YMm = 10,
            WidthMm = 72,
            HeightMm = 30,
            Table = new TableOptions
            {
                Columns =
                {
                    new TableColumn { Title = "品名", WidthMm = 40, Field = "lines.name" },
                    new TableColumn { Title = "金额", WidthMm = -1, Field = "lines.amount" },
                },
                RowHeightMm = 6,
            },
        });
        template.Elements.Add(new TextElement
        {
            Id = "total",
            XMm = 2,
            YMm = 45,
            WidthMm = 72,
            HeightMm = 5,
            Text = new TextOptions { Content = "合计 ￥{{subtotal}}" },
        });
        return template;
    }

    static object Payload(int count)
        => new
        {
            lines = Enumerable.Range(1, count)
                .Select(i => new { name = $"商品#{i}", amount = i * 1.37m })
                .ToList(),
        };

    [Fact]
    public void 表格内容超高_自动增高并顺移下方元素_原模板不动()
    {
        var template = Receipt(); // 表头 6 + 10×6 = 66mm > 元素高 30mm
        var context = new RenderContext(new RenderOptions { Dpi = Dpi }, new RenderReport(), data: Payload(10));

        var working = TemplateRenderer.AutoGrowContinuousTables(template, context);

        working.Should().NotBeSameAs(template, "返回工作副本，绝不改调用方模板（撤销栈/设计器还持有原对象）");
        working.Elements.OfType<TableElement>().Single().HeightMm.Should().Be(66, "表头 6 + 10 行 × 6");
        working.Elements.OfType<TextElement>().Single().YMm.Should().Be(81, "表格底边 40→76，下方元素顺移 +36");

        // 原模板零污染
        template.Elements.OfType<TableElement>().Single().HeightMm.Should().Be(30);
        template.Elements.OfType<TextElement>().Single().YMm.Should().Be(45);

        // 页高随内容自适应：最底元素 81+5=86 + 2mm 余量
        TemplateRenderer.ResolvePageHeightMm(working, new RenderOptions { Dpi = Dpi })
            .Should().Be(88);
    }

    [Fact]
    public void 行数装得下_原样返回不改任何东西()
    {
        var template = Receipt(); // 3 行 = 6 + 18 = 24 ≤ 30
        var context = new RenderContext(new RenderOptions { Dpi = Dpi }, new RenderReport(), data: Payload(3));

        var working = TemplateRenderer.AutoGrowContinuousTables(template, context);

        working.Should().BeSameAs(template, "没有增长就不需要工作副本");
        working.Elements.OfType<TableElement>().Single().HeightMm.Should().Be(30);
        working.Elements.OfType<TextElement>().Single().YMm.Should().Be(45);
    }

    [Fact]
    public void 渲染_行墨迹伸过原表格底边_且不再记裁剪告警()
    {
        var template = Receipt(); // 原表格底边 40mm；增高后行画到 76mm
        var report = new RenderReport();

        using var bitmap = Fixture.Render(template, Dpi, Payload(10), report);

        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.TableOverflow,
            "表格已经自动增高，不存在被裁剪的内容");
        // 原表格底边之下（42~72mm）现在有行墨迹——旧实现这里只会是空白（被裁剪）
        Ink.DarkPixelCountInMm(bitmap, Dpi, new SKRect(2, 42, 74, 72))
            .Should().BeGreaterThan(0, "第 5~10 行必须画出来");
    }
}
