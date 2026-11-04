using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 图片渲染取证。四种 fit 的差别全在"红色块落在哪几个毫米区间"，
/// 所以断言一律用窗口内的颜色像素数，而不是比哈希——哈希会让平台差异变成假失败。
/// </summary>
public class ImageRendererTests
{
    static readonly SKColor Red = new(220, 30, 30);
    const double Dpi = 300;

    /// <summary>缓存是进程级共享的，每个测试用唯一键，避免互相污染。</summary>
    readonly string _uniqueKey = "logo-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(ImageFitMode.Contain, false)]
    [InlineData(ImageFitMode.Fill, true)]
    [InlineData(ImageFitMode.Cover, true)]
    public void 等比与非等比适配决定红色块覆盖范围(ImageFitMode mode, bool fillsCorners)
    {
        // 源图 60×20（3:1），元素 60×40：Contain 会上下留白，Fill/Cover 铺满
        var element = Image(Red, 60, 20, mode);
        element.XMm = 10;
        element.YMm = 10;
        element.WidthMm = 60;
        element.HeightMm = 40;

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 80, pageHeightMm: 60, report: report);
        using (bitmap)
        {
            report.Issues.Should().BeEmpty();

            var corners = new[] { new SKRect(10.5f, 10.5f, 14f, 14f), new SKRect(66f, 46f, 69.5f, 49.5f) };
            var cornerInk = corners.Sum(c => Ink.ColorCountInMm(bitmap, Dpi, c, Red));

            if (fillsCorners)
                cornerInk.Should().BeGreaterThan(0, $"{mode} 必须把元素框铺满，角落也该有图");
            else
                cornerInk.Should().Be(0, $"{mode} 必须留白，不能顶到角落");

            // 中心永远有图
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(36, 26, 44, 34), Red).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Contain按短边受限并居中()
    {
        var element = Image(Red, 60, 20, ImageFitMode.Contain);
        element.XMm = 10;
        element.YMm = 10;
        element.WidthMm = 60;
        element.HeightMm = 40;

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 80, pageHeightMm: 60);
        using (bitmap)
        {
            // scale = min(60/60, 40/20) = 1 → 60×20mm，垂直居中在 40mm 框里 → y 20..40
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(10.5f, 20.5f, 69.5f, 39.5f), Red).Should().BeGreaterThan(0);
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(10.5f, 11f, 69.5f, 19.5f), Red).Should().Be(0, "上方留白必须是纸白");
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(10.5f, 40.5f, 69.5f, 49.5f), Red).Should().Be(0, "下方留白必须是纸白");
        }
    }

    [Fact]
    public void None按96dpi换算成毫米后不缩放()
    {
        var element = Image(Red, 96, 48, ImageFitMode.None);
        element.XMm = 10;
        element.YMm = 10;
        element.WidthMm = 60;
        element.HeightMm = 40;

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 80, pageHeightMm: 60);
        using (bitmap)
        {
            // 96×48px @96dpi = 25.4×12.7mm，居中于 (40,30) → x 27.3..52.7，y 23.65..36.35
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(28, 24.5f, 52, 35.5f), Red).Should().BeGreaterThan(0);
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(11, 11, 26.5f, 49), Red).Should().Be(0, "None 不该把图放大到元素框");
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(53.5f, 11, 69, 49), Red).Should().Be(0);
        }
    }

    [Fact]
    public void 非法Base64记解码失败并画占位框()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { SourceType = ImageSourceType.EmbeddedBase64, Data = "这不是合法的 base64 !!!" },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.ImageDecodeFailed).ElementId.Should().Be("img");
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(10, 10, 50, 30), Red).Should().Be(0, "解码失败不该有图");
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(10, 10, 50, 30)).Should().BeGreaterThan(0, "必须留下看得见的占位框");
        }
    }

    [Fact]
    public void 数据为空记图片缺失()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { SourceType = ImageSourceType.EmbeddedBase64, Data = " " },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.ImageMissing).Message.Should().Contain("data");
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(10, 10, 50, 30)).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void 能解码成位图但不是图片时记解码失败()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { Data = "AAAA" },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            // "AAAA" 是合法 base64（3 字节），但不是图片：FromEncodedData 返回 null，必须同样报出来
            report.Issues.Single(i => i.Kind == RenderIssueKind.ImageDecodeFailed);
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(10, 10, 50, 30)).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void 宿主没给解析器时资源键降级为缺失()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { SourceType = ImageSourceType.ResolverKey, ResolverKey = _uniqueKey },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            report.Issues.Single(i => i.Kind == RenderIssueKind.ImageMissing).Message.Should().Contain("IImageResolver");
        }
    }

    [Fact]
    public void 资源键由宿主解析且同一键只解析一次()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { SourceType = ImageSourceType.ResolverKey, ResolverKey = _uniqueKey, FitMode = ImageFitMode.Fill },
        };

        var resolver = new CountingResolver(PngBytes(Red, 40, 20));
        var first = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, images: resolver);
        var second = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, images: resolver);
        using (first)
        using (second)
        {
            Ink.ColorCountInMm(first, Dpi, new SKRect(10.5f, 10.5f, 49.5f, 29.5f), Red).Should().BeGreaterThan(0);
            Ink.ColorCountInMm(second, Dpi, new SKRect(10.5f, 10.5f, 49.5f, 29.5f), Red).Should().BeGreaterThan(0);
            resolver.Calls.Should().Be(1, "解码缓存按 SHA/键命中，第二页不该再问宿主取一次");
        }
    }

    [Fact]
    public void 图片坏数据不连累其他元素绘制()
    {
        var page = Fixture.Page(80, 60);
        page.Elements.Add(new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { Data = "AAAA" },
        });
        page.Elements.Add(Fixture.Rect(10, 35, 40, 15, fill: SKColors.Black));

        var report = new RenderReport();
        var bitmap = Fixture.Render(page, Dpi, report: report);
        using (bitmap)
        {
            report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.ImageDecodeFailed);
            Ink.DarkPixelCountInMm(bitmap, Dpi, new SKRect(10, 35, 50, 50)).Should().BeGreaterThan(0, "坏图不能连累后面的矩形");
        }
    }

    /// <summary>
    /// 用户实报的那种图：内嵌 SVG（Windows 徽标四色方块）。
    /// 以前 <c>FromEncodedData</c> 解不了矢量，页面上只剩占位框；现在必须四色都真画出来。
    /// </summary>
    [Fact]
    public void 内嵌_SVG_的_data_URI_四个方块都画出来()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.EmbeddedBase64,
                Data = "data:image/svg+xml;base64," + WindowsLogoSvg,
                FitMode = ImageFitMode.Fill,
            },
        };

        var report = new RenderReport();
        using var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);

        report.Issues.Should().BeEmpty();

        // 元素框 (10,10)-(50,30)，四个象限各取中心区域
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(12, 12, 28, 19), new SKColor(0xF2, 0x50, 0x22)).Should().BeGreaterThan(0, "左上橙");
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(32, 12, 48, 19), new SKColor(0x7F, 0xBA, 0x00)).Should().BeGreaterThan(0, "右上绿");
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(12, 21, 28, 28), new SKColor(0x00, 0xA4, 0xEF)).Should().BeGreaterThan(0, "左下蓝");
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(32, 21, 48, 28), new SKColor(0xFF, 0xB9, 0x00)).Should().BeGreaterThan(0, "右下黄");
    }

    /// <summary>
    /// 历史模板把外链/内嵌图错存成 ResolverKey（用户截图里 SourceType=ResolverKey + https:// 就是这个），
    /// 宿主又没配解析器。现在引擎在键本身是定位符时兜底自取，而不是直接给空框。
    /// </summary>
    [Fact]
    public void 资源键里存的是_data_URI_时引擎兜底自绘()
    {
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(PngBytes(Red, 40, 20));
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.ResolverKey,
                ResolverKey = dataUri,
                FitMode = ImageFitMode.Fill,
            },
        };

        var report = new RenderReport();
        using var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);

        report.Issues.Should().BeEmpty("定位符形态的键不该再报「缺解析器」");
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(15, 15, 45, 25), Red).Should().BeGreaterThan(0);
    }

    /// <summary>外链在没有宿主解析器且显式禁用网络时：画占位框，并留下一条能读懂的原因。</summary>
    [Fact]
    public void 外链在禁用网络时降级为占位框并说明原因()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.ResolverKey,
                ResolverKey = "https://example.com/logo-" + _uniqueKey + ".png",
            },
        };

        var report = new RenderReport();
        using var bitmap = Fixture.RenderElement(
            element,
            Dpi,
            pageWidthMm: 60,
            pageHeightMm: 40,
            report: report,
            options: new RenderOptions { AllowRemoteImages = false });

        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.ImageMissing)
            .Which.Message.Should().Contain("AllowRemoteImages");
        Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(10, 10, 50, 30)).Should().BeGreaterThan(0, "占位框要画出来，设计者才看得出这里空了");
    }

    /// <summary>Url 类型接受 data URI（同一字段既能写内嵌也能写外链，方便表单编辑）。</summary>
    [Fact]
    public void Url_类型写_data_URI_也能渲染()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.Url,
                Url = "data:image/png;base64," + Convert.ToBase64String(PngBytes(Red, 40, 20)),
                FitMode = ImageFitMode.Fill,
            },
        };

        var report = new RenderReport();
        using var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40, report: report);

        report.Issues.Should().BeEmpty();
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(15, 15, 45, 25), Red).Should().BeGreaterThan(0);
    }

    /// <summary>四色方块 SVG 的 base64 载荷（与 SvgRasterizerTests 同源）。</summary>
    const string WindowsLogoSvg =
        "PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyIDIiPjxyZWN0IHdpZHRoPSIxIiBoZWlnaHQ9IjEiIGZpbGw9IiNmMjUwMjIiLz48cmVjdCB4PSIxIiB3aWR0aD0iMSIgaGVpZ2h0PSIxIiBmaWxsPSIjN2ZiYTAwIi8+PHJlY3QgeT0iMSIgd2lkdGg9IjEiIGhlaWdodD0iMSIgZmlsbD0iIzAwYTRlZiIvPjxyZWN0IHg9IjEiIHk9IjEiIHdpZHRoPSIxIiBoZWlnaHQ9IjEiIGZpbGw9IiNmZmI5MDAiLz48L3N2Zz4=";

    static ImageElement Image(SKColor color, int srcW, int srcH, ImageFitMode mode)
        => new()
        {
            Id = "img",
            WidthMm = 10,
            HeightMm = 10,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.EmbeddedBase64,
                Data = Convert.ToBase64String(PngBytes(color, srcW, srcH)),
                FitMode = mode,
            },
        };

    /// <summary>现场生成纯色 PNG，测试不依赖仓库里的二进制样例。</summary>
    static byte[] PngBytes(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>字段绑定测试用的样例图：纯色块 PNG（<see cref="PngBytes"/> 的语义别名）。</summary>
    static byte[] SamplePng(int width, int height, SKColor color) => PngBytes(color, width, height);

    sealed class CountingResolver(byte[] payload) : IImageResolver
    {
        public int Calls { get; private set; }

        public byte[]? Resolve(string key)
        {
            Calls++;
            return payload;
        }
    }

    [Fact]
    public void 图片字段绑定_render_用数据里的图源()
    {
        var element = new ImageElement
        {
            XMm = 5, YMm = 5, WidthMm = 40, HeightMm = 30,
            Image = new ImageOptions { Field = "photo" },
        };

        var resolver = new CountingResolver(SamplePng(40, 30, Red));
        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(
            element, Dpi, pageWidthMm: 60, pageHeightMm: 50, report: report,
            data: new { photo = "resolver-key" }, images: resolver);

        using (bitmap)
        {
            report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.ImageMissing);
            resolver.Calls.Should().BeGreaterThan(0, "字段值应作为定位符交给图片解析器");
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(5, 5, 45, 35), Red).Should().BeGreaterThan(0);
        }
    }
}
