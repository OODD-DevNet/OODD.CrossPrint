using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// SVG 光栅器取证。断言一律落在"某个像素是不是这个颜色"上：
/// 矢量转位图的正确性最终只有像素能证明，比"没抛异常"有意义得多。
/// </summary>
public class SvgRasterizerTests
{
    /// <summary>
    /// 用户实际反馈的那张图：Windows 徽标四个方块的 SVG（base64 形态）。
    /// viewBox 2×2，四个 1×1 方块正好占一个象限。
    /// </summary>
    const string WindowsLogoSvgBase64 =
        "PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyIDIiPjxyZWN0IHdpZHRoPSIxIiBoZWlnaHQ9IjEiIGZpbGw9IiNmMjUwMjIiLz48cmVjdCB4PSIxIiB3aWR0aD0iMSIgaGVpZ2h0PSIxIiBmaWxsPSIjN2ZiYTAwIi8+PHJlY3QgeT0iMSIgd2lkdGg9IjEiIGhlaWdodD0iMSIgZmlsbD0iIzAwYTRlZiIvPjxyZWN0IHg9IjEiIHk9IjEiIHdpZHRoPSIxIiBoZWlnaHQ9IjEiIGZpbGw9IiNmZmI5MDAiLz48L3N2Zz4=";

    static readonly SKColor Orange = new(0xF2, 0x50, 0x22);
    static readonly SKColor Green = new(0x7F, 0xBA, 0x00);
    static readonly SKColor Blue = new(0x00, 0xA4, 0xEF);
    static readonly SKColor Yellow = new(0xFF, 0xB9, 0x00);

    [Fact]
    public void 四色方块图标按象限落色()
    {
        var svg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(WindowsLogoSvgBase64));

        using var bitmap = Rasterize(svg, 64, 64, out var error);
        error.Should().BeNull();

        // 每个象限取中心点，避开边缘抗锯齿
        ColorAt(bitmap, 16, 16).Should().Be(Orange);
        ColorAt(bitmap, 48, 16).Should().Be(Green);
        ColorAt(bitmap, 16, 48).Should().Be(Blue);
        ColorAt(bitmap, 48, 48).Should().Be(Yellow);
    }

    [Fact]
    public void 非等比目标尺寸下按默认_meet_规则居中留白()
    {
        // 1:1 的内容放进 100×50：meet 取 min(100/1, 50/1)=50，横向居中 → x∈[25,75]
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect width='1' height='1' fill='#ff0000'/></svg>";

        using var bitmap = Rasterize(svg, 100, 50, out var error);
        error.Should().BeNull();

        ColorAt(bitmap, 5, 25).Alpha.Should().Be(0, "左侧为 meet 留白");
        ColorAt(bitmap, 95, 25).Alpha.Should().Be(0, "右侧为 meet 留白");
        ColorAt(bitmap, 50, 25).Should().Be(new SKColor(255, 0, 0));
    }

    [Fact]
    public void 路径与相对指令闭合三角形只填内部()
    {
        // 相对/绝对混用 + Z 闭合：左上三角
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>"
                           + "<path d='M0 0 L10 0 L10 10 Z' fill='#00ff00'/></svg>";

        using var bitmap = Rasterize(svg, 100, 100, out var error);
        error.Should().BeNull();

        ColorAt(bitmap, 50, 20).Should().Be(new SKColor(0, 255, 0), "三角形内部应为绿色");
        ColorAt(bitmap, 20, 80).Alpha.Should().Be(0, "三角形外部应为留白");
    }

    [Fact]
    public void transform_列表按_SVG_语义从右往左生效()
    {
        // translate(10,0) scale(2)：先缩放再平移 → 1 单位方块变成 (10,0)-(12,2)
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'>"
                           + "<rect width='1' height='1' fill='#0000ff' transform='translate(10,0) scale(2)'/></svg>";

        using var bitmap = Rasterize(svg, 200, 200, out var error);
        error.Should().BeNull();

        // 视口 20 单位 → 200px，1 单位 = 10px。(10..12, 0..2) → (100..120, 0..20)
        ColorAt(bitmap, 110, 10).Should().Be(new SKColor(0, 0, 255));
        ColorAt(bitmap, 30, 10).Alpha.Should().Be(0, "平移生效后原点附近应为空");
    }

    [Fact]
    public void 非法_XML_返回空并带原因()
    {
        var image = SvgRasterizer.Rasterize("<svg><rect", 32, 32, out var error);

        image.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 根元素不是_svg_返回空并带原因()
    {
        var image = SvgRasterizer.Rasterize("<html><body/></html>", 32, 32, out var error);

        image.Should().BeNull();
        error.Should().Contain("svg");
    }

    [Fact]
    public void use_自引用不死循环()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>"
                           + "<g id='loop'><use href='#loop'/></g><use href='#loop'/>"
                           + "<rect width='1' height='1' fill='#123456'/></svg>";

        using var bitmap = Rasterize(svg, 64, 64, out var error);
        error.Should().BeNull();
        ColorAt(bitmap, 2, 2).Should().Be(new SKColor(0x12, 0x34, 0x56), "递归被深度上限挡住，后面的元素仍要画出来");
    }

    static SKBitmap Rasterize(string svg, int width, int height, out string? error)
    {
        var image = SvgRasterizer.Rasterize(svg, width, height, out error);
        image.Should().NotBeNull(error ?? string.Empty);

        var bitmap = new SKBitmap(new SKImageInfo(image!.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0).Should().BeTrue();
        image.Dispose();
        return bitmap;
    }

    static SKColor ColorAt(SKBitmap bitmap, int x, int y)
    {
        bitmap.Width.Should().BeGreaterThan(x);
        bitmap.Height.Should().BeGreaterThan(y);
        return bitmap.GetPixel(x, y);
    }
}
