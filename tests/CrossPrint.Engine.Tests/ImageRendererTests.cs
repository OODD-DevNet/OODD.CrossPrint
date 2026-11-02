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

    sealed class CountingResolver(byte[] payload) : IImageResolver
    {
        public int Calls { get; private set; }

        public byte[]? Resolve(string key)
        {
            Calls++;
            return payload;
        }
    }
}
