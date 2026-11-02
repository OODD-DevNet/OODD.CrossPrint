using System.IO;
using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// <see cref="EditorCanvas"/> 是六个宿主共用的唯一编排入口，所以这里断言的正是宿主拿不到
/// 编译期保护的那部分：谁先画、DPI 用哪个、数据有没有传下去。
/// </summary>
public sealed class EditorCanvasTests
{
    static SKCanvas NewCanvas(SKBitmap bitmap) => new(bitmap);

    [Fact]
    public void Draw_把本次布局尺寸与DPI写进内核_有效DPI等于DPI乘缩放()
    {
        var kernel = KernelFixture.Kernel();
        kernel.ViewWidthHostPx = 1;
        kernel.ViewHeightHostPx = 1;
        kernel.Viewport.Zoom = 2.0;

        using var bitmap = Surface.White(640, 480);
        using var canvas = NewCanvas(bitmap);

        EditorCanvas.Draw(canvas, kernel, new EditorRenderer(), 640, 480, 144.0);

        kernel.ViewWidthHostPx.Should().Be(640);
        kernel.ViewHeightHostPx.Should().Be(480);
        kernel.Viewport.Dpi.Should().Be(144.0);
        // 引擎入口 canvas.Scale(Dpi/25.4)，所以只有 dpi×zoom 才能把 mm 直接落到设备 px。
        kernel.Viewport.EffectiveRenderDpi.Should().Be(288.0);
    }

    [Fact]
    public void Draw_文档与选中层两遍都真的落到像素上()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 20, 30, 40, 25);
        rect.Rectangle.FillColor = SKColors.Black;

        var kernel = new EditorKernel(template);
        kernel.Viewport.Reset();
        kernel.Selection.Select(rect.Id);

        using var bitmap = Surface.White(800, 600);
        using var canvas = NewCanvas(bitmap);

        EditorCanvas.Draw(canvas, kernel, new EditorRenderer(), 800, 600, KernelFixture.Dpi96);

        // 文档：矩形内部是黑的（1mm = 96/25.4 px，40mm 处 = 151px）。
        Surface.AnyIn(bitmap, 40 * Viewport.MmToBasePx, 42.5 * Viewport.MmToBasePx, 2, Surface.IsDark)
            .Should().BeTrue("文档内容没有落在画布上");

        // 叠加层：选中框描边在左边缘、避开上下两个边中手柄（手柄会盖住描边）。
        Surface.AnyIn(bitmap, 20 * Viewport.MmToBasePx, 37 * Viewport.MmToBasePx, 2, Surface.IsAccent)
            .Should().BeTrue("选中框没有画出来");
    }

    [Fact]
    public void Draw_叠加层为null时只画文档_预览路径没有选中框()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 20, 30, 40, 25);
        rect.Rectangle.FillColor = SKColors.Black;

        var kernel = new EditorKernel(template);
        kernel.Viewport.Reset();
        kernel.Selection.Select(rect.Id);

        using var bitmap = Surface.White(800, 600);
        using var canvas = NewCanvas(bitmap);

        EditorCanvas.Draw(canvas, kernel, overlay: null, 800, 600, KernelFixture.Dpi96);

        Surface.AnyIn(bitmap, 40 * Viewport.MmToBasePx, 42.5 * Viewport.MmToBasePx, 2, Surface.IsDark)
            .Should().BeTrue("没有叠加层时文档仍必须画出来");
        Surface.AnyIn(bitmap, 20 * Viewport.MmToBasePx, 37 * Viewport.MmToBasePx, 2, Surface.IsAccent)
            .Should().BeFalse("预览路径不应出现选中框");
    }

    [Fact]
    public void Draw_把数据源送到绑定器_传数据后不再有缺字段告警()
    {
        var template = KernelFixture.Page();
        template.Add(new TextElement
        {
            XMm = 10,
            YMm = 10,
            WidthMm = 120,
            HeightMm = 12,
            Text = new TextOptions { Content = "{{title}}", FontSizePt = 14 },
        });

        var kernel = new EditorKernel(template);

        using var bitmap = Surface.White(800, 600);
        using var canvas = NewCanvas(bitmap);

        var withoutData = EditorCanvas.Draw(canvas, kernel, overlay: null, 800, 600, KernelFixture.Dpi96);
        withoutData.Issues.Should().Contain(i => i.Kind == RenderIssueKind.BindingMissingField,
            "设计态不传数据时占位符应记为缺字段");

        var withData = EditorCanvas.Draw(canvas, kernel, overlay: null, 800, 600, KernelFixture.Dpi96,
            data: new { title = "厨房订单" });
        withData.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.BindingMissingField,
            "数据已经送进 Draw，绑定器却没看到它");
    }

    [Fact]
    public void Draw_设计数据兜底_没有宿主数据时条码仍按样例值画出()
    {
        var template = KernelFixture.Page();
        template.DesignData["barcode"] = "8801234567890";
        template.Add(new BarcodeElement
        {
            XMm = 10,
            YMm = 10,
            WidthMm = 80,
            HeightMm = 18,
            Barcode = new BarcodeOptions
            {
                Content = "{{barcode}}",
                Symbology = BarcodeSymbology.Code128,
                QuietZoneMm = 0,
                ShowText = false,
            },
        });

        var kernel = new EditorKernel(template);

        using var bitmap = Surface.White(800, 600);
        using var canvas = NewCanvas(bitmap);

        var report = EditorCanvas.Draw(canvas, kernel, overlay: null, 800, 600, KernelFixture.Dpi96);

        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.BarcodeFailed,
            "模板里带着设计数据（hiprint testData），条码不该退化成“缺少数据”占位框");
        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.BindingMissingField,
            "设计数据兜底后 barcode 不该再被记成缺字段");

        var pxPerMm = Viewport.MmToBasePx;
        Surface.InkBox(bitmap, SKRect.Create(
                (float)(10 * pxPerMm), (float)(10 * pxPerMm),
                (float)(80 * pxPerMm), (float)(18 * pxPerMm)), Surface.IsDark)
            .Should().NotBeNull("去掉 EditorCanvas 的设计数据兜底，条码绑定成空串后只剩浅灰占位框，这条必红");
    }

    [Fact]
    public void 无头渲染冒烟_两种DPI都出墨并写出可解码的PNG产物()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 20, 30, 40, 25);
        rect.Rectangle.FillColor = SKColors.Black;
        template.Add(new TextElement
        {
            XMm = 20,
            YMm = 70,
            WidthMm = 120,
            HeightMm = 12,
            Text = new TextOptions { Content = "CrossPrint 无头冒烟", FontSizePt = 14 },
        });

        var artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);

        foreach (var dpi in new[] { KernelFixture.Dpi96, 144.0 })
        {
            // 整页 + 余量：210×297mm 在 144 DPI 下是 1191×1684 设备 px。
            var width = (int)Math.Ceiling(210 * dpi / 25.4) + 20;
            var height = (int)Math.Ceiling(297 * dpi / 25.4) + 20;
            var kernel = new EditorKernel(template);
            kernel.Viewport.Reset();

            using var bitmap = Surface.White(width, height);
            using (var canvas = NewCanvas(bitmap))
                EditorCanvas.Draw(canvas, kernel, overlay: null, width, height, dpi);

            // 墨迹位置按 dpi 独立算出来，不借用被测的换算函数。
            var pxPerMm = dpi / 25.4;
            Surface.AnyIn(bitmap, 40 * pxPerMm, 42.5 * pxPerMm, 2, Surface.IsDark)
                .Should().BeTrue($"{dpi} DPI 下矩形没有落到像素上");
            Surface.InkBox(bitmap, SKRect.Create(
                    (float)(20 * pxPerMm), (float)(70 * pxPerMm),
                    (float)(120 * pxPerMm), (float)(12 * pxPerMm)), Surface.IsDark)
                .Should().NotBeNull($"{dpi} DPI 下文字没有落到像素上");

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = data.ToArray();
            bytes.Length.Should().BeGreaterThan(2000, $"{dpi} DPI 的 PNG 不该是空壳");
            bytes.Take(8).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                "产物必须是 PNG 签名");

            var path = Path.Combine(artifacts, $"editor-canvas-{dpi:0}.png");
            File.WriteAllBytes(path, bytes);

            // 产物"生成了"还要能读回来，尺寸对得上才算数。
            using var decoded = SKBitmap.Decode(File.ReadAllBytes(path));
            decoded.Should().NotBeNull("写出的 PNG 必须能被解码");
            decoded.Width.Should().Be(width);
            decoded.Height.Should().Be(height);
        }

        // 正对照：空模板在同样的管道下必须一个墨点都没有，否则上面的断言无法区分"画了"和"底噪"。
        using var blank = Surface.White(900, 1200);
        using (var canvas = NewCanvas(blank))
            EditorCanvas.Draw(canvas, new EditorKernel(KernelFixture.Page()), overlay: null, 900, 1200, KernelFixture.Dpi96);
        Surface.InkBox(blank, Surface.IsDark).Should().BeNull("空白模板不该有墨迹");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-96.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法DPI在边界上拒绝_不把视口换算写坏(double dpi)
    {
        // 视口每次换算都要除以 DPI，0/NaN/Infinity 一旦进去，拖拽出的 mm 会顺着命令写进模型并被 JSON 持久化。
        var viewport = new Viewport();
        Action set = () => viewport.Dpi = dpi;
        set.Should().Throw<ArgumentOutOfRangeException>();
        viewport.Dpi.Should().Be(KernelFixture.Dpi96, "拒绝就要原样不动，不能留下半个值");

        var kernel = KernelFixture.Kernel();
        using var bitmap = Surface.White(320, 240);
        using var canvas = NewCanvas(bitmap);
        var act = () => EditorCanvas.Draw(canvas, kernel, overlay: null, 320, 240, dpi);
        act.Should().Throw<ArgumentOutOfRangeException>();
        kernel.Viewport.Dpi.Should().Be(KernelFixture.Dpi96, "Draw 是宿主写 DPI 的唯一入口，这里拦住才谈得上兜底");
    }

    [Fact]
    public void 合法DPI照写不误()
    {
        var kernel = KernelFixture.Kernel();
        using var bitmap = Surface.White(320, 240);
        using var canvas = NewCanvas(bitmap);

        EditorCanvas.Draw(canvas, kernel, overlay: null, 320, 240, 144.0);

        kernel.Viewport.Dpi.Should().Be(144.0);
    }
}
