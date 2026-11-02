using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 元素公共外观（背景/边框/不透明度）与文字装饰的取证测试。
/// 这些字段走 <see cref="TemplateRenderer"/> 的公共路径，八种元素都应生效，
/// 所以断言只看像素，不依赖具体元素的渲染器。
/// </summary>
public class ElementBoxStyleTests
{
    const double Dpi = 150;

    [Fact]
    public void 背景色与边框色真的画出像素_留空时零墨()
    {
        var decorated = Fixture.Rect(20, 20, 30, 20);
        decorated.BackgroundColor = SKColors.Red;
        decorated.BorderColor = SKColors.Blue;
        decorated.BorderWidthMm = 1;

        var plain = Fixture.Rect(20, 20, 30, 20);

        var withStyle = Fixture.RenderElement(decorated, Dpi);
        var withoutStyle = Fixture.RenderElement(plain, Dpi);
        using (withStyle)
        using (withoutStyle)
        {
            Ink.ColorCountInMm(withStyle, Dpi, new SKRect(25, 25, 45, 35), new SKColor(255, 0, 0))
                .Should().BeGreaterThan(1000, "背景色应铺满元素内部");

            Ink.ColorCountInMm(withStyle, Dpi, new SKRect(19.2f, 25, 20.2f, 35), new SKColor(0, 0, 255))
                .Should().BeGreaterThan(10, "1mm 蓝色边框应真的落在左边沿");

            Ink.ColorCountInMm(withoutStyle, Dpi, new SKRect(19, 19, 51, 41), new SKColor(255, 0, 0))
                .Should().Be(0, "未设置背景色时不得凭空出现底色");
            Ink.ColorCountInMm(withoutStyle, Dpi, new SKRect(19, 19, 51, 41), new SKColor(0, 0, 255))
                .Should().Be(0, "未设置边框色时不得凭空出现边框");
        }
    }

    [Fact]
    public void 不透明度把黑色按比例混到纸白上()
    {
        var element = Fixture.Rect(10, 10, 20, 20, fill: SKColors.Black);
        element.Opacity = 0.5;

        var half = Fixture.RenderElement(element, Dpi);
        using (half)
        {
            var pixel = half.GetPixel(Ink.MmToPx(20, Dpi), Ink.MmToPx(20, Dpi));
            pixel.Red.Should().BeInRange(118, 138, "50% 黑叠在白纸上应得到中灰，差太远说明合成层没有生效");
        }

        element.Opacity = 1.0;
        var opaque = Fixture.RenderElement(element, Dpi);
        using (opaque)
        {
            opaque.GetPixel(Ink.MmToPx(20, Dpi), Ink.MmToPx(20, Dpi)).Red
                .Should().BeLessThan(16, "不透明度为 1 时必须是纯黑");
        }
    }

    [Fact]
    public void 下划线与删除线增加墨量而默认不画()
    {
        static TextElement Build(bool underline, bool strike)
        {
            var element = Fixture.Text("MMMM", 10, 10, 80, 30);
            element.Text.FontSizePt = 24;
            element.Text.Wrap = false;
            element.Text.Underline = underline;
            element.Text.Strikethrough = strike;
            return element;
        }

        var window = new SKRect(5, 5, 95, 45);
        var plain = Fixture.RenderElement(Build(underline: false, strike: false), Dpi);
        var underlined = Fixture.RenderElement(Build(underline: true, strike: false), Dpi);
        var struck = Fixture.RenderElement(Build(underline: false, strike: true), Dpi);
        using (plain)
        using (underlined)
        using (struck)
        {
            var plainInk = Ink.DarkPixelCountInMm(plain, Dpi, window);
            var underInk = Ink.DarkPixelCountInMm(underlined, Dpi, window);
            var strikeInk = Ink.DarkPixelCountInMm(struck, Dpi, window);

            plainInk.Should().BeGreaterThan(0, "正对照：字母本身必须画出墨");
            underInk.Should().BeGreaterThan(plainInk + 200, "下划线应新增一条贯穿文本宽度的墨线");
            strikeInk.Should().BeGreaterThan(plainInk + 200, "删除线应新增一条贯穿文本宽度的墨线");
        }
    }

    [Fact]
    public void 克隆会带上公共外观字段()
    {
        var source = Fixture.Rect(10, 10, 20, 20);
        source.BackgroundColor = SKColors.Red;
        source.BorderColor = SKColors.Blue;
        source.BorderWidthMm = 1.5;
        source.BorderDashPatternMm = [2, 1];
        source.Opacity = 0.4;

        var clone = (RectangleElement)source.CloneDeep();

        clone.BackgroundColor.Should().Be(SKColors.Red);
        clone.BorderColor.Should().Be(SKColors.Blue);
        clone.BorderWidthMm.Should().Be(1.5);
        clone.BorderDashPatternMm.Should().Equal(2, 1);
        clone.Opacity.Should().Be(0.4);
        clone.BorderDashPatternMm.Should().NotBeSameAs(source.BorderDashPatternMm, "虚线数组必须深拷贝，否则副本改段长会连带改到源元素");
    }
}
