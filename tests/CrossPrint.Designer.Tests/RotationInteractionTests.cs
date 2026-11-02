using CrossPrint.Core;
using CrossPrint.Engine;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 旋转与退化盒的编辑内核测试：命中框（<see cref="ElementHitTester.HitRectMm"/>）与叠加层选中框
/// 必须落在"旋转后真正画出来"的那块纸上，而不是原始水平盒上——否则蓝色框压在空白纸、
/// 点可见墨迹选不中、点陈旧框内却选中，拖拽/缩放轴向也指错方向。
/// </summary>
/// <remarks>
/// 旋转绕元素中心（<c>TemplateRenderer</c> 的 <c>RotateDegrees(rot, XMm+WidthMm/2, YMm+HeightMm/2)</c>），
/// 命中框取旋转后盒的轴对齐外接盒（AABB）。所有期望值都按算式独立写出，不靠被测函数反推。
/// 像素断言（<c>叠加层选中框与引擎墨迹重合</c>）带正对照，先证墨迹确实在旋转后的竖条上，再证框与它重合。
/// </remarks>
public class RotationInteractionTests
{
    // 96 / 25.4：1mm 的视口 px（zoom=1、dpi=96），这里按算式重写一遍，避免用被测函数推期望值。
    const double MmPx = 96.0 / 25.4;

    // ---------------------------------------------------------------- DEFECT 1：旋转命中框

    [Fact]
    public void 旋转90度的宽文本命中旋转后的竖条_而非原始横条()
    {
        var template = KernelFixture.Page();
        var text = new TextElement { XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 10, RotationDegrees = 90 };
        template.Add(text);

        // 中心 = (10+20, 10+5) = (30,15)。绕中心转 90° 后的 AABB：
        //   宽 = |40·cos90| + |10·sin90| = 0 + 10 = 10；高 = |40·sin90| + |10·cos90| = 40 + 0 = 40。
        //   → 竖条 x∈[25,35]、y∈[-5,35]。
        // 点在旋转后竖条里、但在原始横条（x∈[10,50]、y∈[10,20]）之外 → 应命中。
        ElementHitTester.HitTest(template.Elements, 28, 25).Should().Be(text, "旋转后的墨迹就在 (28,25)");

        // 点在原始横条里、但旋转后的墨迹已不再覆盖 → 应返回 null。
        // 正是这条反向断言让这个用例有意义：陈旧的水平框会把 (15,15) 误判成命中。
        ElementHitTester.HitTest(template.Elements, 15, 15).Should().BeNull("(15,15) 在原始盒内但旋转后 x∈[10,25) 已无墨迹");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(360.0)]
    public void 零度与三百六十度保持原始精确命中盒(double rotation)
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        rect.RotationDegrees = rotation;

        // 0°/360° 的正弦 ~1e-16，绕中心不改变外接盒；须精确等于原盒，不能被三角噪声漂出 1e-6。
        ElementHitTester.HitRectMm(rect).Should().Be(SKRect.Create(10, 10, 20, 15));
    }

    [Fact]
    public void 四十五度的命中盒等于旋转外接盒公式()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 40, 20);
        rect.RotationDegrees = 45;

        // 独立按公式算期望（double）：中心 (30,20)，cos=sin=√2/2≈0.70710678。
        //   宽 = |40·c| + |20·s| = 0.70710678 × 60 = 42.426407；高同 = 42.426407。
        //   左 = 30 − 42.426407/2 = 8.786797；顶 = 20 − 42.426407/2 = −1.213203。
        var rad = 45 * Math.PI / 180;
        var c = Math.Abs(Math.Cos(rad));
        var s = Math.Abs(Math.Sin(rad));
        var center = new Point2D(30, 20);
        var expectW = 40 * c + 20 * s;
        var expectH = 40 * s + 20 * c;
        var expectLeft = center.X - expectW / 2;
        var expectTop = center.Y - expectH / 2;

        var hit = ElementHitTester.HitRectMm(rect);
        ((double)hit.Left).Should().BeApproximately(expectLeft, 1e-3);
        ((double)hit.Top).Should().BeApproximately(expectTop, 1e-3);
        ((double)hit.Width).Should().BeApproximately(expectW, 1e-3);
        ((double)hit.Height).Should().BeApproximately(expectH, 1e-3);
    }

    [Fact]
    public void 旋转线段的命中带仍可命中()
    {
        var template = KernelFixture.Page();
        // 水平线 (20,30) 长 50、描边 0.3：命中带 half = max(0.3, 0.5)/2 = 0.25 → 带 (19.75, 29.75, 50.5, 0.5)。
        var line = KernelFixture.AddLine(template, LineDirection.Horizontal, 20, 30, 50, strokeWidthMm: 0.3);
        line.RotationDegrees = 90;

        // 绕中心 (45,30) 转 90°：带宽 = 50.5、厚 = 0.5 → 外接盒 x∈[44.75,45.25]、y∈[4.75,55.25]（一条竖线带）。
        ElementHitTester.HitTest(template.Elements, 45, 45).Should().Be(line, "旋转后竖条中部 (45,45) 必须命中");
        ElementHitTester.HitTest(template.Elements, 45, 5).Should().Be(line, "y=5 落在 [4.75,55.25] 内");
        ElementHitTester.HitTest(template.Elements, 45, 3).Should().BeNull("y=3 在竖条带之外");
        ElementHitTester.HitTest(template.Elements, 30, 30).Should().BeNull("原水平线左段 x=30 旋转后不在竖条带里");
    }

    [Fact]
    public void 叠加层选中框与引擎画出的旋转墨迹重合_所见即所点()
    {
        var template = KernelFixture.Page();
        var rect = new RectangleElement { XMm = 20, YMm = 25, WidthMm = 40, HeightMm = 10, RotationDegrees = 90 };
        rect.Rectangle.FillColor = SKColors.Black;
        rect.Rectangle.StrokeColor = null;
        template.Add(rect);

        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        var overlay = new EditorRenderer
        {
            Options = new EditorRendererOptions { ShowGrid = false, ShowRulers = false, ShowMarginGuides = false },
        };

        using var bitmap = Surface.White(800, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            overlay.RenderBackdrop(kernel, canvas);
            EditorRenderer.PushCamera(canvas, kernel);
            TemplateRenderer.Default.Render(
                kernel.Template,
                canvas,
                new RenderContext(new RenderOptions { Dpi = kernel.Viewport.EffectiveRenderDpi }, new RenderReport()));
            canvas.Restore();
            overlay.RenderForeground(kernel, canvas);
        }

        // 正对照：引擎按中心旋转 90°，黑色墨迹必须落在旋转后的竖条 (35,10,10,40)，而不是原始横条。
        var rotatedMm = SKRect.Create(35, 10, 10, 40);
        var rotatedHost = kernel.Viewport.ToHostRect(rotatedMm, KernelFixture.Dpi96);
        var ink = Surface.InkBox(bitmap, Surface.IsDark);
        ink.Should().NotBeNull("黑色实心矩形必须被画出来，否则墨迹正对照无从谈起");
        ((double)ink!.Value.Left).Should().BeApproximately(rotatedHost.Left, 3, "旋转墨迹左边（实测 {0}，期望 {1}）", ink.Value.Left, rotatedHost.Left);
        ((double)ink.Value.Top).Should().BeApproximately(rotatedHost.Top, 3);
        ((double)ink.Value.Width).Should().BeApproximately(rotatedHost.Width, 3);
        ((double)ink.Value.Height).Should().BeApproximately(rotatedHost.Height, 3);

        // 命中盒必须取自旋转后的外接盒——与"点可见墨迹即命中"同源。
        ElementHitTester.HitRectMm(rect).Should().Be(rotatedMm, "命中盒必须等于旋转后的外接盒");

        // 叠加层选中框（accent）外接盒 ≈ 命中盒的宿主矩形：让"所见"与"所点"不能再分家。
        var frame = Surface.InkBox(bitmap, Surface.IsAccent);
        frame.Should().NotBeNull("选中框与手柄必须画出来（正对照）");
        // 手柄在四角各向外探出约半柄（4px），给 8px 容差；原始横条与竖条宽差 ~113px，绝不会被容差掩盖。
        ((double)frame!.Value.Left).Should().BeApproximately(rotatedHost.Left, 8, "叠加层框左边必须压在旋转后的命中框上");
        ((double)frame.Value.Top).Should().BeApproximately(rotatedHost.Top, 8);
        ((double)frame.Value.Right).Should().BeApproximately(rotatedHost.Right, 8);
        ((double)frame.Value.Bottom).Should().BeApproximately(rotatedHost.Bottom, 8);
    }

    // ---------------------------------------------------------------- DEFECT 4：退化盒可选

    [Fact]
    public void 零高元素在其中心线上可选中()
    {
        var template = KernelFixture.Page();
        var flat = KernelFixture.AddRect(template, 10, 30, 40, 0);

        // 命中带 = MinDegenerateHitMm(0.5) 以 YMm=30 为中心展开 → y∈[29.75,30.25]。
        // 96 DPI 下 0.5mm ≈ 1.89 设备 px，跨 2 px 足够点中；且只在退化维展开，绝不侵入正常盒。
        ElementHitTester.HitTest(template.Elements, 30, 29.9).Should().Be(flat, "紧贴中心线上方仍应命中（陈旧零高盒只匹配 y=30 精确坐标）");
        ElementHitTester.HitTest(template.Elements, 30, 30.1).Should().Be(flat);
        ElementHitTester.HitTest(template.Elements, 30, 29.6).Should().BeNull("超出 0.25mm 半带宽即应落空");
    }

    [Fact]
    public void 零宽元素在其中心线上可选中()
    {
        var template = KernelFixture.Page();
        var upright = KernelFixture.AddRect(template, 60, 10, 0, 40);

        // 零宽 → x∈[59.75,60.25] 的竖带。
        ElementHitTester.HitTest(template.Elements, 60.1, 20).Should().Be(upright, "紧贴中心竖线仍应命中");
        ElementHitTester.HitTest(template.Elements, 59.9, 20).Should().Be(upright);
        ElementHitTester.HitTest(template.Elements, 60.4, 20).Should().BeNull();
    }

    [Fact]
    public void 零高元素的抓取带不吞掉近邻元素()
    {
        var template = KernelFixture.Page();
        var flat = KernelFixture.AddRect(template, 10, 100, 40, 0);      // 零高，带 y∈[99.75,100.25]
        var below = KernelFixture.AddRect(template, 10, 103, 40, 10);     // 正常盒 y∈[103,113]

        ElementHitTester.HitTest(template.Elements, 30, 100).Should().Be(flat);
        ElementHitTester.HitTest(template.Elements, 30, 103.5).Should().Be(below, "近邻在带之外，不得被抢走");
        ElementHitTester.HitTest(template.Elements, 30, 102).Should().BeNull("两者之间的空隙不该被带填满");
    }

    [Fact]
    public void 正常高度元素的命中盒不被抓取带撑大_正对照()
    {
        var template = KernelFixture.Page();
        var normal = KernelFixture.AddRect(template, 10, 10, 20, 10);

        // 正对照：抓取带只作用于退化维，正常 10mm 高盒必须保持精确盒，否则带会吞掉真实边界。
        ElementHitTester.HitRectMm(normal).Should().Be(SKRect.Create(10, 10, 20, 10));
        ElementHitTester.HitTest(template.Elements, 20, 9.6).Should().BeNull("顶边外 0.4mm 不得被撑大命中");
        ElementHitTester.HitTest(template.Elements, 20, 20.4).Should().BeNull("底边外 0.4mm 不得被撑大命中");
    }
}
