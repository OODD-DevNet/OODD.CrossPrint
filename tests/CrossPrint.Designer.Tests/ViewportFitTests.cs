using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 整页适配的居中判据。曾经写成"按去掉 padding 的可用区居中"，结果整页稳定偏到左上留白里
/// （Pan 是从视口原点量起的，可用区的原点本身已经偏离了 padding），这条测试就是钉住这一点。
/// </summary>
public class ViewportFitTests
{
    [Theory]
    [InlineData(96.0)]
    [InlineData(120.0)]
    public void 整页适配把纸心放在视口中心(double dpi)
    {
        var viewport = new Viewport { Dpi = dpi };
        viewport.FitToPage(210, 297, 800, 600, dpi);

        // 纸心（105,148.5mm）换回宿主像素必须落在视口正中。宿主 px 与 DPI 无关地落在 (400,300)。
        // 容差 1e-3 px：Pan 是 float（Point2D 装 double，但平移量按视口 px 的 float 精度存），
        // 实测两端都只差 4.6e-6 px，这里留三个数量级的余量而不是把容差压到浮点极限。
        var center = viewport.ToHostPx(105, 148.5, dpi);
        center.X.Should().BeApproximately(400, 1e-3, "dpi={0} 时纸心横坐标（实测 {1}）", dpi, center.X);
        center.Y.Should().BeApproximately(300, 1e-3, "dpi={0} 时纸心纵坐标（实测 {1}）", dpi, center.Y);
    }

    [Fact]
    public void 整页适配后纸不越出带留白的可用区()
    {
        var viewport = new Viewport();
        viewport.FitToPage(210, 297, 800, 600, 96);

        var topLeft = viewport.ToHostPx(0, 0, 96);
        var bottomRight = viewport.ToHostPx(210, 297, 96);

        topLeft.X.Should().BeGreaterThanOrEqualTo(24 - 1e-6);
        topLeft.Y.Should().BeGreaterThanOrEqualTo(24 - 1e-6);
        bottomRight.X.Should().BeLessThanOrEqualTo(800 - 24 + 1e-6);
        bottomRight.Y.Should().BeLessThanOrEqualTo(600 - 24 + 1e-6);
    }
}
