using System.Net;
using System.Net.Sockets;
using System.Text;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 图片来源获取层取证：分类、data URI 两种形态、base64 严格性、网络下载与缓存。
/// 网络部分用本地裸 TCP 假装一个 HTTP 服务，不依赖外网也不绑 UrlAcl，
/// 这样"外链图片能不能打"在离线环境里也是可验证的。
/// </summary>
public class ImageAcquisitionTests
{
    [Theory]
    [InlineData(null, ImageLocatorKind.Empty)]
    [InlineData("   ", ImageLocatorKind.Empty)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", ImageLocatorKind.DataUri)]
    [InlineData("DATA:image/svg+xml;base64,PHN2Zz4=", ImageLocatorKind.DataUri)]
    [InlineData("http://example.com/a.png", ImageLocatorKind.Remote)]
    [InlineData("https://t7.baidu.com/it/u=1,2&fm=21", ImageLocatorKind.Remote)]
    [InlineData(@"C:\temp\logo.png", ImageLocatorKind.Other)]
    [InlineData("/var/lib/logo.png", ImageLocatorKind.Other)]
    [InlineData("biz-key-2024", ImageLocatorKind.Other)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'></svg>", ImageLocatorKind.SvgText)]
    [InlineData("  <?xml version='1.0'?><svg xmlns='http://www.w3.org/2000/svg'></svg>", ImageLocatorKind.SvgText)]
    public void 定位符分类(string? locator, ImageLocatorKind expected)
        => ImageAcquisition.Classify(locator).Should().Be(expected);

    [Fact]
    public void 裸_base64_与_data_URI_都被识别为可内嵌()
    {
        var png = PngBytes();
        var bare = Convert.ToBase64String(png);

        ImageAcquisition.Classify(bare).Should().Be(ImageLocatorKind.Base64);
        ImageAcquisition.Classify("data:image/png;base64," + bare).Should().Be(ImageLocatorKind.DataUri);
    }

    [Fact]
    public void data_URI_的_base64_载荷解出原始字节()
    {
        var png = PngBytes();
        var bytes = ImageAcquisition.TryParseDataUri("data:image/png;base64," + Convert.ToBase64String(png), out var error);

        error.Should().BeNull();
        bytes.Should().Equal(png);
    }

    [Fact]
    public void 百分号编码的_SVG_data_URI_也能解()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>";
        var uri = "data:image/svg+xml," + Uri.EscapeDataString(svg);

        var bytes = ImageAcquisition.TryParseDataUri(uri, out var error);

        error.Should().BeNull();
        Encoding.UTF8.GetString(bytes!).Should().Be(svg);
    }

    [Theory]
    [InlineData("https://cdn.example.com/logo.svg", true)]
    [InlineData("https://cdn.example.com/logo.svg?v=2", true)]
    [InlineData("https://cdn.example.com/logo.png", false)]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=", true)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", false)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'></svg>", true)]
    [InlineData("biz-key-2024", false)]
    public void SVG_定位符嗅探(string locator, bool expected)
        => ImageAcquisition.LooksLikeSvgLocator(locator).Should().Be(expected);

    [Fact]
    public void 裸_base64_SVG_靠头部魔数识别()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>";
        var bare = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        ImageAcquisition.LooksLikeSvgLocator(bare).Should().BeTrue();
        ImageAcquisition.Classify(bare).Should().Be(ImageLocatorKind.Base64);
    }

    /// <summary>
    /// 用户实报：属性面板「内嵌图片」直接粘 &lt;svg…&gt; 源码。旧实现把这种值归为 Other，
    /// 渲染器再试裸 base64 解码失败 → "图片数据不是合法 Base64" → 占位框。
    /// </summary>
    [Fact]
    public void 裸_SVG_源码本身就是可解码的图片载荷()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect width='1' height='1' fill='#ff6b6b'/></svg>";

        ImageAcquisition.Classify(svg).Should().Be(ImageLocatorKind.SvgText);
        ImageAcquisition.LooksLikeSvgLocator(svg).Should().BeTrue("按尺寸光栅化，缓存键必须带目标像素尺寸");

        var bytes = ImageAcquisition.Acquire(svg, allowRemote: false, timeoutMs: 1000, out var error);
        error.Should().BeNull();
        Encoding.UTF8.GetString(bytes!).Should().Be(svg);

        using var image = ImageAcquisition.Decode(bytes!, 32, 32, out var decodeError);
        decodeError.Should().BeNull();
        image.Should().NotBeNull();
        image!.Width.Should().Be(32);
    }

    [Fact]
    public void 带_BOM_的_SVG_字节也能解码()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect width='1' height='1' fill='#1a535c'/></svg>";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(svg)).ToArray();

        using var image = ImageAcquisition.Decode(bytes, 16, 16, out var error);
        error.Should().BeNull();
        image.Should().NotBeNull();
    }

    [Fact]
    public void 关掉网络图片后外链直接失败并给出可读原因()
    {
        var bytes = ImageAcquisition.Acquire("https://example.com/a.png", allowRemote: false, timeoutMs: 1000, out var error);

        bytes.Should().BeNull();
        error.Should().Contain("AllowRemoteImages");
    }

    [Fact]
    public void 非法_base64_不抛异常只给原因()
    {
        // 33 个 'x'：长度 ≥32 会通过 LooksLikeBase64 的形态判定，但 33 不是 4 的倍数，
        // Convert.FromBase64String 会真正失败——这是"看着像 base64 但其实非法"的临界形态。
        var bytes = ImageAcquisition.Acquire(new string('x', 33), allowRemote: true, timeoutMs: 1000, out var error);

        bytes.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SVG_字节走矢量光栅并按目标像素出图()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect width='1' height='1' fill='#ff0000'/></svg>";
        var bytes = Encoding.UTF8.GetBytes(svg);

        using var image = ImageAcquisition.Decode(bytes, 40, 20, out var error);

        error.Should().BeNull();
        image!.Width.Should().Be(40);
        image.Height.Should().Be(20);
    }

    [Fact]
    public void 位图字节仍走_Skia_解码()
    {
        var png = PngBytes(width: 30, height: 12);

        using var image = ImageAcquisition.Decode(png, 100, 100, out var error);

        error.Should().BeNull();
        image!.Width.Should().Be(30, "位图不该被拉伸到目标像素尺寸");
        image.Height.Should().Be(12);
    }

    [Fact]
    public void 既不是位图也不是_SVG_时给原因()
    {
        using var image = ImageAcquisition.Decode(Encoding.UTF8.GetBytes("not an image at all"), 10, 10, out var error);

        image.Should().BeNull();
        error.Should().Contain("图片");
    }

    [Fact]
    public void 外链下载成功并按_URL_命中缓存()
    {
        var png = PngBytes(width: 8, height: 8);
        var (url, stop) = ServeOnce(png);

        try
        {
            var first = ImageAcquisition.FetchRemote(url, 5000, out var error);
            error.Should().BeNull();
            first.Should().Equal(png);

            // 服务已经停了：第二次还能拿到数据就说明走的是字节缓存而不是重新下载
            stop();

            var second = ImageAcquisition.FetchRemote(url, 5000, out var cachedError);
            cachedError.Should().BeNull();
            second.Should().Equal(png);
        }
        finally
        {
            stop();
        }
    }

    /// <summary>
    /// 起一个只接受一次连接的裸 TCP HTTP 服务（不绑 URL ACL，离线可跑），
    /// 返回 URL 与"停服"动作。
    /// </summary>
    static (string Url, Action Stop) ServeOnce(byte[] payload)
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;

        var accepting = true;
        _ = Task.Run(() =>
        {
            try
            {
                using var client = tcp.AcceptTcpClient();
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;

                // 读到请求头结束（不关心内容，只要让客户端把请求发完）
                var buffer = new byte[4096];
                var seen = new StringBuilder();
                while (seen.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var header = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                var headBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // 客户端提前断开属正常（缓存命中时不会再连）
            }
            finally
            {
                try
                {
                    tcp.Stop();
                }
                catch (Exception)
                {
                    // 幂等停服
                }
            }
        });

        void StopOnce()
        {
            if (!accepting)
                return;
            accepting = false;
            try
            {
                tcp.Stop();
            }
            catch (Exception)
            {
                // 幂等停服
            }
        }

        return ($"http://127.0.0.1:{port}/logo.png", StopOnce);
    }

    static byte[] PngBytes(int width = 4, int height = 4)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(new SKColor(10, 20, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
