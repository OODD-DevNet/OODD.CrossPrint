using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>模板内出现未注册类型时的替身元素。</summary>
public sealed class MysteryElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => "mystery";

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var copy = new MysteryElement();
        CopyCommonFrom(this);
        return copy;
    }
}

/// <summary>故意抛异常的渲染器，用来验证"引擎不抛"这条承诺。</summary>
public sealed class ThrowingRenderer : IElementRenderer
{
    /// <inheritdoc/>
    public string TypeKey => "text";

    /// <inheritdoc/>
    public void Render(SKCanvas canvas, PaperElement element, RenderContext context) => throw new InvalidOperationException("模拟渲染器内部崩溃");
}

/// <summary>只画一个实心矩形的极简替换渲染器，用于验证覆盖注册生效。</summary>
public sealed class SquareAsBarRenderer : IElementRenderer
{
    /// <inheritdoc/>
    public string TypeKey => "rectangle";

    /// <inheritdoc/>
    public void Render(SKCanvas canvas, PaperElement element, RenderContext context)
    {
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect((float)element.XMm, (float)element.YMm, (float)(element.XMm + 10), (float)(element.YMm + 2)), paint);
    }
}

public class TemplateRendererTests
{
    [Fact]
    public void 默认注册表覆盖全部内置类型()
    {
        ElementRendererRegistry.Default.RegisteredTypeKeys.Should()
            .BeEquivalentTo(ElementTypeKeys.All);
        ElementRendererRegistry.Default.CoversBuiltinTypes().Should().BeTrue();
    }

    [Fact]
    public void 文档样例整页渲染零失败告警()
    {
        // 样例里写了 {{title}}/{{orderNo}}/{{sku}} 三个占位符，不给数据就必然记缺失；
        // QR 内容绑定成空串同样编不出码。这条测试证明的是"样例配上它声明的数据后开箱即用"。
        var data = new
        {
            title = "跨境打印面单",
            orderNo = "CP-2026-0001",
            sku = "ABC-123",
        };

        var report = new RenderReport();
        var bitmap = Fixture.Render(Fixture.KitchenSink(), 150, data: data, report: report);
        using (bitmap)
        {
            report.Issues
                .Where(i => i.Kind != RenderIssueKind.ElementOutOfBounds && i.Kind != RenderIssueKind.FontFallback)
                .Should().BeEmpty("样例模板配上它声明的数据不该出现任何绘制失败（字体回退只取决于这台机器装了哪些字体）");
            Ink.BoundingBoxMm(bitmap, 150).Should().NotBeNull("整页必须有墨");
        }
    }

    [Fact]
    public void 样张模板逐元素在两种密度下毫米位置一致()
    {
        // 这就是"快照基线"的落地形式，只是提交的不是 PNG 而是判据：同一份样张在 96 与 300 DPI 下，
        // 每个元素框内的墨迹包围盒必须落在同一个毫米位置。
        // 为什么不用栅格快照：位图差不差 1% 取决于这台机器装了哪些字体、Skia 怎么 hinting、
        // 抗锯齿开关——换台机器（或换台 Linux）就飘，那种基线只能天天红灯。
        // 毫米几何与密度无关，正是"同源渲染"要守住的东西，所以它才配当长期门。
        var data = new
        {
            title = "跨境打印面单",
            orderNo = "CP-2026-0001",
            sku = "ABC-123",
        };

        var template = Fixture.KitchenSink();
        var low = Fixture.Render(template, 96, data: data);
        var high = Fixture.Render(template, 300, data: data);
        using (low)
        using (high)
        {
            foreach (var element in template.Elements)
            {
                // 窗口按元素框外扩 1mm：水平线是**贴着 yMm 画的**（BasicRenderers.cs:59），
                // 半个线宽落在 yMm 之上；而 96 DPI 一个像素 0.265mm，紧贴边框裁会把这半条线整条切没。
                var window = new SKRect(
                    (float)element.XMm - 1,
                    (float)element.YMm - 1,
                    (float)(element.XMm + element.WidthMm) + 1,
                    (float)(element.YMm + Math.Max(element.HeightMm, 0.5f)) + 1);

                // 阈值放宽到 200：样张里 el-2 是分隔线常见的浅灰 #8899AA（亮度 150），
                // 默认的 128 深色判据对它失明——那不是渲染失败，是探针太窄。
                var a = Ink.BoundingBoxInMm(low, 96, window, threshold: 200);
                var b = Ink.BoundingBoxInMm(high, 300, window, threshold: 200);

                a.Should().NotBeNull($"元素 {element.Id} 在 96 DPI 下必须留下墨迹");
                b.Should().NotBeNull($"元素 {element.Id} 在 300 DPI 下必须留下墨迹");

                // 96 DPI 一个像素就是 0.265mm，包围盒只能量化到这个粒度，容差按两像素给
                a!.Value.Left.Should().BeApproximately(b!.Value.Left, 0.8, $"元素 {element.Id} 横向漂移（96:{a.Value.Left:0.###} vs 300:{b.Value.Left:0.###}mm）");
                a.Value.Top.Should().BeApproximately(b.Value.Top, 0.8, $"元素 {element.Id} 纵向漂移（96:{a.Value.Top:0.###} vs 300:{b.Value.Top:0.###}mm）");
                a.Value.Width.Should().BeApproximately(b.Value.Width, 1.2, $"元素 {element.Id} 幅面漂移（96:{a.Value.Width:0.###} vs 300:{b.Value.Width:0.###}mm）");
                a.Value.Height.Should().BeApproximately(b.Value.Height, 1.2, $"元素 {element.Id} 高度漂移（96:{a.Value.Height:0.###} vs 300:{b.Value.Height:0.###}mm）");
            }

            // 反向对照：所有元素底边之下那条带子必须一张纸都是白的。
            // 用"非纸白"而不是"深色"来当对照，才可能真的抓住满页噪声；
            // 没有这条，上面八个 NotBeNull 可能被背景一起喂饱。
            var blank = new SKRect(12, 180, 198, 250);
            Ink.NonWhitePixelCountInMm(low, 96, blank).Should().Be(0, "留白区不该有任何墨迹");
            Ink.NonWhitePixelCountInMm(high, 300, blank).Should().Be(0, "留白区不该有任何墨迹");
        }
    }

    [Fact]
    public void 未注册类型记渲染器缺失且不中断后续元素()
    {
        var template = Fixture.Page(80, 60);
        template.Elements.Add(new MysteryElement { Id = "m", XMm = 1, YMm = 1, WidthMm = 10, HeightMm = 10 });
        template.Elements.Add(Fixture.Rect(10, 10, 20, 10, fill: SKColors.Black));

        var report = new RenderReport();
        var bitmap = Fixture.Render(template, 150, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.RendererMissing).ElementId.Should().Be("m");
            Ink.DarkPixelCountInMm(bitmap, 150, new SKRect(10, 10, 30, 20)).Should().BeGreaterThan(50, "坏类型之后的正常元素必须照样画出来");
        }
    }

    [Fact]
    public void 渲染器抛异常被降级为元素跳过()
    {
        var registry = ElementRendererRegistry.CreateDefault();
        registry.Register(new ThrowingRenderer());
        var renderer = new TemplateRenderer(registry);

        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("会崩的文本", 5, 5, 30, 10));
        template.Elements.Add(Fixture.Rect(5, 30, 20, 10, fill: SKColors.Black));

        var report = new RenderReport();
        var bitmap = new SKBitmap(new SKImageInfo(80 * 150 / 25, 60 * 150 / 25, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (bitmap)
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            renderer.Render(template, canvas, new RenderContext(new RenderOptions { Dpi = 150 }, report));
        }

        var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.ElementSkipped);
        issue.ElementId.Should().Be("t");
        issue.Message.Should().Contain("模拟渲染器内部崩溃");
    }

    [Fact]
    public void 同名类型后注册的渲染器覆盖内置实现()
    {
        var registry = ElementRendererRegistry.CreateDefault();
        registry.Register(new SquareAsBarRenderer());
        var renderer = new TemplateRenderer(registry);

        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Rect(10, 10, 40, 40, fill: SKColors.Black));

        var bitmap = new SKBitmap(new SKImageInfo(80 * 150 / 25, 60 * 150 / 25, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (bitmap)
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            renderer.Render(template, canvas, new RenderContext(new RenderOptions { Dpi = 150 }, new RenderReport()));

            var box = Ink.BoundingBoxMm(bitmap, 150)!.Value;
            box.Width.Should().BeApproximately(10, 1.5, "替换渲染器只画 10mm 宽的横条");
            box.Height.Should().BeApproximately(2, 1.0);
        }
    }

    [Fact]
    public void 同层元素按数组顺序绘制_高ZIndex压在上面()
    {
        // 数组里先画的红框在下，后画的蓝框在上；两者同 zIndex
        var template = Fixture.Page(60, 40);
        template.Elements.Add(new RectangleElement { Id = "a", XMm = 5, YMm = 5, WidthMm = 20, HeightMm = 15, Rectangle = new ShapeOptions { FillColor = SKColors.Red, StrokeColor = null } });
        template.Elements.Add(new RectangleElement { Id = "b", XMm = 10, YMm = 8, WidthMm = 20, HeightMm = 15, Rectangle = new ShapeOptions { FillColor = SKColors.Blue, StrokeColor = null } });

        var bitmap = Fixture.Render(template, 150);
        using (bitmap)
        {
            bitmap.GetPixel(Px(15), Px(12)).Blue.Should().BeGreaterThan(200, "重叠区应显示后绘制的蓝框");

            // zIndex 升序绘制，大值压小值：把数组首位的红框抬到 5，它反而盖住蓝框
            template.Elements[0].ZIndex = 5;
            var swapped = Fixture.Render(template, 150);
            using (swapped)
            {
                swapped.GetPixel(Px(15), Px(12)).Red.Should().BeGreaterThan(200);
            }
        }
    }

    [Fact]
    public void 样式库重复id渲染不抛且后定义生效()
    {
        // JSON 面板手改/外部导入的模板可能带两条同 id 样式：ToDictionary 一炸就带走整个导出，
        // 违背"渲染不抛"。改为索引器循环后重复取最后一条，缺样式引用的静默口径不变。
        var template = StyleTemplate("st-1",
            new NamedStyle { Id = "st-1", TextColor = SKColors.Red },
            new NamedStyle { Id = "st-1", TextColor = new SKColor(0, 128, 0) });

        var report = new RenderReport();
        var bitmap = Fixture.Render(template, 150, report: report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            var page = new SKRect(0, 0, 60, 40);
            Ink.ColorCountInMm(bitmap, 150, page, new SKColor(0, 128, 0), tolerance: 90)
                .Should().BeGreaterThan(20, "同 id 样式后定义覆盖先定义");
            Ink.ColorCountInMm(bitmap, 150, page, SKColors.Red, tolerance: 90)
                .Should().Be(0, "被覆盖的第一条不该再上色");
        }
    }

    [Fact]
    public void 样式库null与空白id被跳过且不抛异常()
    {
        var template = StyleTemplate("st-ok",
            new NamedStyle { Id = null!, TextColor = SKColors.Red },
            new NamedStyle { Id = "   ", TextColor = SKColors.Red },
            new NamedStyle { Id = "st-ok", TextColor = SKColors.Blue });

        var report = new RenderReport();
        var bitmap = Fixture.Render(template, 150, report: report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            var page = new SKRect(0, 0, 60, 40);
            Ink.ColorCountInMm(bitmap, 150, page, SKColors.Blue, tolerance: 90)
                .Should().BeGreaterThan(20, "脏 id 被跳过后，正常样式照样查得到");
            Ink.ColorCountInMm(bitmap, 150, page, SKColors.Red, tolerance: 90)
                .Should().Be(0, "null/空白 id 不进表，不许抢走字色");
        }
    }

    [Fact]
    public void 单样式正常样式库照常生效_正向对照()
    {
        // 正向对照：干净的单样式库必须上色成功，且去掉样式库的孪生模板出不了红——
        // 上面几条"红/绿像素计数"判据两头都钉死，才不是虚设。
        var styled = StyleTemplate("st-1", new NamedStyle { Id = "st-1", TextColor = SKColors.Red });
        var plain = StyleTemplate("st-1");

        var a = Fixture.Render(styled, 150);
        using (a)
        {
            Ink.ColorCountInMm(a, 150, new SKRect(0, 0, 60, 40), SKColors.Red, tolerance: 90)
                .Should().BeGreaterThan(20, "正常库样式必须生效");
        }

        var b = Fixture.Render(plain, 150);
        using (b)
        {
            Ink.ColorCountInMm(b, 150, new SKRect(0, 0, 60, 40), SKColors.Red, tolerance: 90)
                .Should().Be(0, "没挂样式库就没有红");
            Ink.DarkPixelCountInMm(b, 150, new SKRect(0, 0, 60, 40))
                .Should().BeGreaterThan(20, "对照只是不上色，文字照常绘制");
        }
    }

    /// <summary>一页一样式的取证模板：文本元素按 <c>styleId</c> 引用库中样式，样式条目由调用方给。</summary>
    static PrintTemplate StyleTemplate(string styleId, params NamedStyle[] styles)
    {
        var template = Fixture.Page(60, 40);
        foreach (var s in styles)
            template.Styles.Add(s);
        template.Elements.Add(new TextElement
        {
            Id = "t",
            XMm = 2,
            YMm = 8,
            WidthMm = 56,
            HeightMm = 14,
            Text = new TextOptions
            {
                Content = "样式覆盖取证文字",
                StyleId = styleId,
                FontFamily = "Microsoft YaHei",
                FontSizePt = 14,
            },
        });
        return template;
    }

    static int Px(double mm) => (int)Math.Round(mm * 150 / 25.4);

    [Fact]
    public void 超出页面的元素记告警但仍绘制()
    {
        var template = Fixture.Page(50, 40);
        template.Elements.Add(Fixture.Rect(45, 35, 20, 20, fill: SKColors.Black));

        var report = new RenderReport();
        var bitmap = Fixture.Render(template, 150, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.ElementOutOfBounds).ElementId.Should().Be("r");
            Ink.DarkPixelCountInMm(bitmap, 150, new SKRect(45, 35, 50, 40)).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void 横向纸张交换宽高_连续纸按内容自适应页高()
    {
        var landscape = Fixture.Page(80, 50);
        landscape.Paper.Orientation = PaperOrientation.Landscape;
        landscape.Elements.Add(Fixture.Text("横", 2, 2, 10, 10));
        TemplateRenderer.GetPageSizeMm(landscape, new RenderOptions { Dpi = 96 }).Should().Be(new SKSize(50, 80));

        var continuous = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 58, HeightMm = 0 },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };
        continuous.Elements.Add(Fixture.Rect(5, 10, 40, 30, fill: SKColors.Black));

        // 设计口径（docs/02-architecture.md「连续纸：引擎按最底部元素 + 余量定高」）：
        // 页高 = max(内容底边, 下留白) + 2mm 送纸余量 = max(40, 5) + 2
        TemplateRenderer.ResolvePageHeightMm(continuous, new RenderOptions()).Should().BeApproximately(42, 0.01);

        var result = TemplateRenderer.Default.RenderToBitmap(continuous, new RenderOptions { Dpi = 100 });
        using (result.Bitmap)
        {
            result.Bitmap.Height.Should().Be((int)Math.Ceiling(42 * 100 / 25.4));
        }
    }

    [Fact]
    public void 空模板渲染出纯底色的合法页面()
    {
        var template = Fixture.Page(30, 20);
        var bitmap = Fixture.Render(template, 96);
        using (bitmap)
        {
            Ink.BoundingBoxMm(bitmap, 96).Should().BeNull();
            bitmap.GetPixel(5, 5).Should().Be(SKColors.White);
        }
    }
}
