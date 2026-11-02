using CrossPrint.Core;
using CrossPrint.Engine;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 叠加层的无头验证。判据分三类：
/// <list type="bullet">
/// <item><description><b>位置</b>：页框、手柄、网格必须落在 <see cref="Viewport.ToHostPx"/> 算出的宿主像素上。</description></item>
/// <item><description><b>区分度</b>：每个开关关掉后对应像素必须变回纸白（否则"开着"等于没验）。</description></item>
/// <item><description><b>同源</b>：相机 + 引擎画出来的内容与视口换算的框必须重合——这是"预览即导出"的像素级判据。</description></item>
/// </list>
/// </summary>
public class EditorRendererTests
{
    const double Dpi = 96;

    // 100×60mm 小纸：96 DPI 下 377.95×226.77 宿主 px，800×600 的位面放得下还有余量。
    static PrintTemplate Paper(double marginMm = 0)
        => new()
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 100, HeightMm = 60 },
            Margins = new ThicknessMm(marginMm, marginMm, marginMm, marginMm),
        };

    static EditorKernel Kernel(double marginMm = 0)
    {
        var kernel = new EditorKernel(Paper(marginMm));
        kernel.ViewWidthHostPx = 800;
        kernel.ViewHeightHostPx = 600;
        return kernel;
    }

    static EditorRenderer Overlay(EditorRendererOptions options) => new() { Options = options };

    static EditorRendererOptions Minimal() => new()
    {
        ShowGrid = false,
        ShowRulers = false,
        ShowMarginGuides = false,
    };

    [Theory]
    [InlineData(96.0)]
    [InlineData(144.0)]
    public void 引擎内容与视口换算落在同一处_这是同源预览的像素判据(double dpi)
    {
        var kernel = Kernel();
        kernel.Viewport.Dpi = dpi;
        var rect = KernelFixture.AddRect(kernel.Template, 20, 30, 40, 25);
        rect.Rectangle.FillColor = SKColors.Black;
        rect.Rectangle.StrokeColor = null;
        kernel.Viewport.Zoom = 1.7;
        kernel.Viewport.Pan = new Point2D(37, -11);

        using var bitmap = Surface.White(1600, 1200);
        using (var canvas = new SKCanvas(bitmap))
        {
            EditorRenderer.PushCamera(canvas, kernel);
            TemplateRenderer.Default.Render(
                kernel.Template,
                canvas,
                new RenderContext(new RenderOptions { Dpi = kernel.Viewport.EffectiveRenderDpi }, new RenderReport()));
            canvas.Restore();
        }

        var expected = kernel.Viewport.ToHostRect(SKRect.Create(20, 30, 40, 25), kernel.Viewport.Dpi);
        var actual = Surface.InkBox(bitmap, Surface.IsDark);
        actual.Should().NotBeNull("黑色实心矩形必须画出来，否则下面的位置断言没有对象（正对照）");

        // 期望值直接来自视口换算：zoom=1.7、Pan=(37,-11) 时 mm→宿主 px 的那条链。
        // SKRect 是 float，FluentAssertions 6.x 不给 float 提供 BeApproximately，统一转 double 再比。
        var ink = actual.GetValueOrDefault();
        ((double)ink.Left).Should().BeApproximately(expected.Left, 2.0, "左边（实测 {0}，视口换算 {1}）", ink.Left, expected.Left);
        ((double)ink.Top).Should().BeApproximately(expected.Top, 2.0, "上边（实测 {0}，视口换算 {1}）", ink.Top, expected.Top);
        ((double)ink.Width).Should().BeApproximately(expected.Width, 2.0, "宽度（实测 {0}，视口换算 {1}）", ink.Width, expected.Width);
        ((double)ink.Height).Should().BeApproximately(expected.Height, 2.0, "高度（实测 {0}，视口换算 {1}）", ink.Height, expected.Height);
    }

    [Fact]
    public void 页纸铺在换算位置_页外是工作区底色()
    {
        var kernel = Kernel();
        kernel.Viewport.Pan = new Point2D(50, 40);

        using var bitmap = Surface.White(800, 600);
        using (var canvas = new SKCanvas(bitmap))
            Overlay(Minimal()).RenderBackdrop(kernel, canvas);

        var page = EditorRenderer.PageRectHost(kernel);
        ((double)page.Left).Should().BeApproximately(50, 0.01);
        ((double)page.Top).Should().BeApproximately(40, 0.01);
        ((double)page.Width).Should().BeApproximately(100 * Viewport.MmToBasePx, 0.01);
        ((double)page.Height).Should().BeApproximately(60 * Viewport.MmToBasePx, 0.01);

        Surface.Pixel(bitmap, (int)(page.Left + page.Width / 2), (int)(page.Top + page.Height / 2))
            .Should().Be(SKColors.White, "纸面必须是纸白，文档会画在它上面");

        Surface.Pixel(bitmap, (int)page.Right + 5, 300)
            .Should().NotBe(SKColors.White, "页外必须是工作区底色，否则看不出纸的边界");

        Surface.AnyIn(bitmap, page.Left + 20, page.Top, 1, c => c.Red < 150)
            .Should().BeTrue("页框线要画在纸边上（工作区底色亮度 0xB4=180，页框 0x77=119）");
    }

    [Fact]
    public void 网格开关有区分度()
    {
        var kernel = Kernel();
        kernel.Viewport.Pan = new Point2D(50, 40);

        // 5mm 网格线：96 DPI 下距纸左边 5 × 3.7795 = 18.90 宿主 px，即 x≈68.9。
        var gridX = kernel.Viewport.ToHostPx(5, 0, Dpi).X;

        using var on = Surface.White(800, 600);
        using (var canvas = new SKCanvas(on))
            Overlay(new EditorRendererOptions { ShowGrid = true, ShowRulers = false, ShowMarginGuides = false, GridStepMm = 5 })
                .RenderBackdrop(kernel, canvas);

        using var off = Surface.White(800, 600);
        using (var canvas = new SKCanvas(off))
            Overlay(Minimal()).RenderBackdrop(kernel, canvas);

        Surface.AnyIn(on, gridX, 160, 1, Surface.NotWhite)
            .Should().BeTrue("网格开着时 5mm 线上必须有像素（实测 x={0}）", gridX);
        Surface.AnyIn(off, gridX, 160, 1, Surface.NotWhite)
            .Should().BeFalse("网格关掉后同一位置必须回到纸白，否则这个开关是假功能");
    }

    [Fact]
    public void 八个手柄都画在锚点上_且尺寸不随缩放变()
    {
        var kernel = Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);
        kernel.Selection.Select(rect.Id);
        kernel.Viewport.Pan = new Point2D(50, 40);

        var box = ElementHitTester.UnionBox(kernel.SelectedElements);
        var anchors = ElementHitTester.HandleAnchorsMm(box);
        anchors.Should().HaveCount(8);

        var sizes = new List<double>();
        foreach (var zoom in new[] { 1.0, 2.5 })
        {
            kernel.Viewport.Zoom = zoom;

            using var bitmap = Surface.White(800, 600);
            using (var canvas = new SKCanvas(bitmap))
                Overlay(Minimal()).RenderForeground(kernel, canvas);

            foreach (var (handle, xMm, yMm) in anchors)
            {
                var anchor = kernel.Viewport.ToHostPx(xMm, yMm, Dpi);

                // 手柄是 8×8 的白底蓝框，锚点在正中：±6 的窗口一定能扫到描边。
                Surface.AnyIn(bitmap, anchor.X, anchor.Y, 6, Surface.IsAccent)
                    .Should().BeTrue("zoom={0} 时手柄 {1} 必须画在锚点 ({2:0.0},{3:0.0}) 上", zoom, handle, anchor.X, anchor.Y);

                // 只量"朝盒外"的那一象限：选中框的边线也在锚点上，会把整个窗口拉成长条。
                var outward = SKRect.Create((float)(anchor.X - 6), (float)(anchor.Y - 6), 7, 7);
                var ink = Surface.InkBox(bitmap, outward, Surface.IsAccent);
                ink.Should().NotBeNull();
                sizes.Add(ink!.Value.Width);
            }
        }

        // 四个角 + 四条边，两档缩放各量一次：设备像素尺寸不随 zoom 变（缩放变的是 mm 间距）。
        sizes.Take(8).Max().Should().BeApproximately(sizes.Skip(8).Max(), 1.0,
            "zoom=1 与 zoom=2.5 的手柄描边宽度必须一致（实测 {0} vs {1}）", sizes.Take(8).Max(), sizes.Skip(8).Max());
    }

    [Fact]
    public void 放置幽灵出现与消失都有像素证据()
    {
        var kernel = Kernel();
        kernel.Viewport.Pan = new Point2D(50, 40);

        kernel.BeginPlacing(ElementTypeKeys.Text, KernelFixture.At(kernel, 30, 20)).Should().BeTrue();
        var ghost = kernel.PlacingGhostMm;
        ghost.Should().NotBeNull();

        using var shown = Surface.White(800, 600);
        using (var canvas = new SKCanvas(shown))
            Overlay(Minimal()).RenderForeground(kernel, canvas);

        var center = kernel.Viewport.ToHostPx(30, 20, Dpi);
        Surface.Pixel(shown, (int)center.X, (int)center.Y)
            .Should().NotBe(SKColors.White, "放置中的幽灵框是半透明蓝，白底上会变淡但不会还是纸白");

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape, Shift: false, Control: false));
        kernel.PlacingGhostMm.Should().BeNull();

        using var gone = Surface.White(800, 600);
        using (var canvas = new SKCanvas(gone))
            Overlay(Minimal()).RenderForeground(kernel, canvas);

        Surface.Pixel(gone, (int)center.X, (int)center.Y)
            .Should().Be(SKColors.White, "Esc 取消后同一像素必须回到纸白");
    }

    [Fact]
    public void 标尺占据顶部与左侧条带_页心仍是纸白()
    {
        var kernel = Kernel();
        kernel.Viewport.Pan = new Point2D(120, 120);

        using var bitmap = Surface.White(800, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            var overlay = Overlay(new EditorRendererOptions { ShowGrid = false, ShowMarginGuides = false, ShowRulers = true });
            overlay.RenderBackdrop(kernel, canvas);
            overlay.RenderForeground(kernel, canvas);
        }

        var page = EditorRenderer.PageRectHost(kernel);
        const int band = 18;

        Surface.Pixel(bitmap, 400, band / 2)
            .Should().Be(new SKColor(0xF3, 0xF3, 0xF3), "顶部标尺条要铺满整宽");
        Surface.Pixel(bitmap, band / 2, 400)
            .Should().Be(new SKColor(0xF3, 0xF3, 0xF3), "左侧标尺条要铺满整高");
        Surface.Pixel(bitmap, (int)(page.Left + page.Width / 2), (int)(page.Top + page.Height / 2))
            .Should().Be(SKColors.White, "页心不能被标尺盖住");
        Surface.AnyIn(bitmap, page.Left, band / 2, 2, c => c.Red < 200)
            .Should().BeTrue("标尺上要有刻度（页左边 0mm 处有主刻度与数字）");
    }

    [Fact]
    public void 连续纸的页框与引擎自适应页高一致()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 80, HeightMm = 0 },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };
        KernelFixture.AddRect(template, 10, 10, 30, 20);

        var kernel = new EditorKernel(template) { ViewWidthHostPx = 800, ViewHeightHostPx = 600 };

        // 内容底边 30mm 与下留白 5mm 取大者，再 +2mm —— 这是 PrintTemplate.ResolvePageHeightMm 的判据。
        kernel.PageHeightMm.Should().BeApproximately(32, 1e-9, "连续纸页高 = max(内容底边, 下留白) + 2mm");

        var page = EditorRenderer.PageRectHost(kernel);
        ((double)page.Height).Should().BeApproximately(32 * Viewport.MmToBasePx, 0.01);
    }
}
