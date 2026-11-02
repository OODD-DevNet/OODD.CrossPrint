using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 文本/线条/形状渲染器的取证测试。断言全部落在"墨迹几何"上且给出毫米值，
/// 不比对具体字形位图——字体在各机器上不同，比字形等于把测试绑死在本机。
/// </summary>
public class TextAndShapeRendererTests
{
    const double Dpi = 150;

    [Fact]
    public void 字号翻倍时墨迹高度同步翻倍()
    {
        var small = Run("国", 12);
        var large = Run("国", 24);

        var h1 = Ink.BoundingBoxInMm(small, Dpi, Window())!.Value.Height;
        var h2 = Ink.BoundingBoxInMm(large, Dpi, Window())!.Value.Height;

        ((double)h2 / h1).Should().BeApproximately(2, 0.25, "pt 字号必须线性映射到 mm 墨迹，不能被 CTM 二次放大");

        static SKBitmap Run(string text, double pt)
        {
            var element = Fixture.Text(text, 10, 10, 60, 40);
            element.Text.FontSizePt = pt;
            element.Text.Wrap = false;
            return Fixture.RenderElement(element, Dpi);
        }
    }

    static SKRect Window() => new(5, 5, 75, 75);

    [Fact]
    public void 水平对齐把墨迹推到元素框的对应边()
    {
        var text = Fixture.Text("MMMM", 10, 10, 60, 20);
        text.Text.Wrap = false;

        var variants = new[] { HorizontalAlign.Left, HorizontalAlign.Center, HorizontalAlign.Right };
        var starts = new List<double>();
        var ends = new List<double>();

        foreach (var align in variants)
        {
            text.Text.HorizontalAlign = align;
            var bitmap = Fixture.RenderElement(text, Dpi);
            using (bitmap)
            {
                var box = Ink.BoundingBoxInMm(bitmap, Dpi, Window())!.Value;
                starts.Add(box.Left);
                ends.Add(box.Right);
            }
        }

        starts[0].Should().BeLessThan(starts[1], "居中应比左对齐更靠右");
        starts[1].Should().BeLessThan(starts[2], "右对齐应最靠右");
        ends[2].Should().BeApproximately(70, 2.5, "右对齐的墨迹右沿应贴住元素框右边（70mm）");
        starts[0].Should().BeApproximately(10, 2.5, "左对齐的墨迹左沿应贴住元素框左边（10mm）");
    }

    [Fact]
    public void 垂直对齐移动整块文本而不改变行数()
    {
        var text = Fixture.Text("第一行\n第二行", 10, 10, 60, 40);
        text.Text.LineSpacing = 1.4;

        var topBox = Box(text, VerticalAlign.Top);
        var middleBox = Box(text, VerticalAlign.Middle);
        var bottomBox = Box(text, VerticalAlign.Bottom);

        topBox.Top.Should().BeLessThan(middleBox.Top);
        middleBox.Top.Should().BeLessThan(bottomBox.Top);
        (bottomBox.Top - topBox.Top).Should().BeGreaterThan(10, "40mm 高的元素里三种垂直对齐应拉开明显距离");

        static Ink.RectMm Box(TextElement element, VerticalAlign align)
        {
            element.Text.VerticalAlign = align;
            var bitmap = Fixture.RenderElement(element, Dpi);
            using (bitmap)
            {
                return Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(5, 5, 75, 75))!.Value;
            }
        }
    }

    [Fact]
    public void 自动换行增加行数且不越出元素宽度()
    {
        var oneLine = Fixture.Text("跨境打印模板设计器CrossPrint示例文本", 10, 10, 80, 30);
        oneLine.Text.FontSizePt = 14;
        oneLine.Text.Wrap = false;

        var narrow = Fixture.Text("跨境打印模板设计器CrossPrint示例文本", 10, 10, 30, 40);
        narrow.Text.FontSizePt = 14;
        narrow.Text.Wrap = true;

        var single = Fixture.RenderElement(oneLine, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var wrapped = Fixture.RenderElement(narrow, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        using (single)
        using (wrapped)
        {
            Ink.InkBandCount(single, 3).Should().Be(1, "80mm 宽一行放得下");
            Ink.InkBandCount(wrapped, 3).Should().BeGreaterThanOrEqualTo(2, "30mm 宽必须折行");

            var box = Ink.BoundingBoxInMm(wrapped, Dpi, new SKRect(0, 0, 100, 60))!.Value;
            box.Right.Should().BeLessThanOrEqualTo(40.5f, "折行必须把每行挡在元素右沿（40mm）之内");
        }
    }

    [Fact]
    public void 行高按CSS行盒模型把基线推下半行距()
    {
        // 同一段文字、同一字号，只把行距从 1.0 改成 2.0：CSS 行盒把多出的行高上下各分一半，
        // 因此顶对齐时基线（和墨迹上沿）应下移 0.5 行高 = 10pt = 3.53mm。
        var tight = Fixture.Text("国", 10, 10, 60, 60);
        var loose = Fixture.Text("国", 10, 10, 60, 60);
        foreach (var e in new[] { tight, loose })
        {
            e.Text.FontSizePt = 20;
            e.Text.Wrap = false;
            e.Text.VerticalAlign = VerticalAlign.Top;
        }
        tight.Text.LineSpacing = 1.0;
        loose.Text.LineSpacing = 2.0;

        using var tightBitmap = Fixture.RenderElement(tight, Dpi);
        using var looseBitmap = Fixture.RenderElement(loose, Dpi);
        var a = Ink.BoundingBoxInMm(tightBitmap, Dpi, Window())!.Value;
        var b = Ink.BoundingBoxInMm(looseBitmap, Dpi, Window())!.Value;

        (b.Top - a.Top).Should().BeApproximately(3.53, 0.8, "半行距必须进基线，否则行距只影响下一行、首行纹丝不动");    }

    [Fact]
    public void 文本溢出元素框时不被裁掉()
    {
        // 2mm 高的框放 24pt 字：hiprint 的文本元素没有 overflow:hidden，墨迹会照常穿出框底。
        var element = Fixture.Text("国", 10, 10, 10, 2);
        element.Text.FontSizePt = 24;
        element.Text.Wrap = false;
        element.Text.VerticalAlign = VerticalAlign.Top;

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 40, pageHeightMm: 40);
        using (bitmap)
        {
            var box = Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(0, 0, 40, 40))!.Value;
            box.Bottom.Should().BeGreaterThan(12.5, "元素框下沿在 12mm，24pt 字形必须能画到框外");
        }
    }

    [Fact]
    public void 绑定缺失字段时打印空白而不是花括号()
    {
        var element = Fixture.Text("单号：{{missing}}", 5, 5, 60, 10);
        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.BindingMissingField).ElementId.Should().Be("t");
            var box = Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(0, 0, 80, 80))!.Value;
            // 只剩"单号："四个字符，宽度应远小于整句
            box.Width.Should().BeLessThan(40);
        }
    }

    [Fact]
    public void 矩形填充与描边各自生效且描边不越出元素框()
    {
        var onlyFill = Fixture.Rect(10, 10, 30, 20, fill: SKColors.Black, stroke: null);
        var onlyStroke = Fixture.Rect(10, 10, 30, 20, fill: null, stroke: SKColors.Black, strokeMm: 1);

        var fillBitmap = Fixture.RenderElement(onlyFill, Dpi);
        var strokeBitmap = Fixture.RenderElement(onlyStroke, Dpi);
        using (fillBitmap)
        using (strokeBitmap)
        {
            var fillBox = Ink.BoundingBoxInMm(fillBitmap, Dpi, Window())!.Value;
            fillBox.Left.Should().BeApproximately(10, 0.6);
            fillBox.Top.Should().BeApproximately(10, 0.6);
            fillBox.Right.Should().BeApproximately(40, 0.6);
            fillBox.Bottom.Should().BeApproximately(30, 0.6);
            Ink.DarkPixelCountInMm(strokeBitmap, Dpi, new SKRect(15, 15, 35, 25)).Should().Be(0, "空心矩形中心不应有墨");

            var box = Ink.BoundingBoxInMm(strokeBitmap, Dpi, Window())!.Value;
            box.Left.Should().BeGreaterThanOrEqualTo(9.5f, "1mm 描边内收半格，外沿不应超出元素框");
            box.Right.Should().BeLessThanOrEqualTo(40.5f);
        }
    }

    [Fact]
    public void 圆角半径超出短边一半时仍画出形状()
    {
        // 20×6mm 矩形配 50mm 圆角：不夹取的话 Skia 会画成空路径，用户只看到一个"没画出来"的框
        var element = Fixture.Rect(10, 10, 20, 6, fill: SKColors.Black, stroke: null);
        element.Rectangle.CornerRadiusMm = 50;

        var bitmap = Fixture.RenderElement(element, Dpi);
        using (bitmap)
        {
            var box = Ink.BoundingBoxInMm(bitmap, Dpi, Window())!.Value;
            box.Width.Should().BeApproximately(20, 1.5);
            box.Height.Should().BeApproximately(6, 1.5);
        }
    }

    [Fact]
    public void 虚线按毫米段长生成行程()
    {
        var element = new LineElement
        {
            Id = "l",
            XMm = 10,
            YMm = 20,
            WidthMm = 60,
            HeightMm = 0.4,
            Line = new LineOptions { StrokeWidthMm = 0.4, DashPatternMm = [2, 2] },
        };

        var solid = new LineElement
        {
            Id = "l2",
            XMm = 10,
            YMm = 40,
            WidthMm = 60,
            HeightMm = 0.4,
            Line = new LineOptions { StrokeWidthMm = 0.4 },
        };

        var template = Fixture.Page(100, 80);
        template.Elements.Add(element);
        template.Elements.Add(solid);

        var bitmap = Fixture.Render(template, Dpi);
        using (bitmap)
        {
            var dashed = Ink.DarkRunsOnRow(bitmap, Dpi, (int)Math.Round(20 * Dpi / 25.4));
            var plain = Ink.DarkRunsOnRow(bitmap, Dpi, (int)Math.Round(40 * Dpi / 25.4));

            plain.Should().HaveCount(1, "实线是一段到底");
            dashed.Count.Should().BeGreaterThanOrEqualTo(10, "60mm 里 2mm 实 + 2mm 空应有约 15 段");
            dashed.Where(r => r.StartMm > 12 && r.StartMm < 65).Select(r => r.LengthMm).Average()
                .Should().BeApproximately(2, 1.0, "虚线段长必须按 mm 而不是按像素");
        }
    }

    [Fact]
    public void 椭圆内切于元素矩形且四角留白()
    {
        var element = new EllipseElement
        {
            Id = "e",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Ellipse = new ShapeOptions { FillColor = SKColors.Black, StrokeColor = null },
        };

        var bitmap = Fixture.RenderElement(element, Dpi);
        using (bitmap)
        {
            var box = Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(5, 5, 75, 75))!.Value;
            box.Width.Should().BeApproximately(40, 1.5);
            box.Height.Should().BeApproximately(20, 1.5);
            Ink.DarkPixelCountInMm(bitmap, Dpi, new SKRect(10, 10, 14, 13)).Should().Be(0, "左上角应在椭圆之外");
        }
    }

    [Fact]
    public void 线宽为零时什么都不画()
    {
        var element = new LineElement
        {
            Id = "l",
            XMm = 5,
            YMm = 5,
            WidthMm = 50,
            Line = new LineOptions { StrokeWidthMm = 0 },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, report: report);
        using (bitmap)
        {
            Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(0, 0, 100, 100)).Should().BeNull();
            report.Issues.Should().BeEmpty("退化线宽是合法输入，不该记为告警");
        }
    }
}
