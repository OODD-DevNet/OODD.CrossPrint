using CrossPrint.Core;
using CrossPrint.Engine;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// <see cref="PrintSettings"/> 的每个字段都得被实现真的读走——本仓不许有"设了没反应"的旋钮。
/// 这里不测"属性存在"，测的是产物字节随设置变化：轮廓模式不再内嵌字库、
/// 换字体解析器后体积骤降、给了图片解析器就不再记图片缺失。
/// </summary>
public sealed class PrintSettingsTests : IDisposable
{
    readonly Workspace _ws = new();
    readonly PdfFilePrintTarget _target = new();

    [Fact]
    public async Task 渲染选项被读取_轮廓模式产物不再内嵌字体()
    {
        var template = Fixture.Page(100, 50);
        template.Elements.Add(Fixture.Text("跨境面单归档", 8, 8, 80, 12, Fixture.CjkFont));

        var vectorPath = _ws.PathOf("options-vector.pdf");
        var outlinePath = _ws.PathOf("options-outline.pdf");

        var vector = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(vectorPath));
        var outline = await _target.PrintAsync(
            new PrintDocument(template),
            new PrintSettings(outlinePath) { Options = new RenderOptions { PdfTextAsOutlines = true } });

        vector.Success.Should().BeTrue(vector.Message ?? "成功");
        outline.Success.Should().BeTrue(outline.Message ?? "成功");

        var vectorBytes = File.ReadAllBytes(vectorPath);
        var outlineBytes = File.ReadAllBytes(outlinePath);
        Pdf.Count(vectorBytes, "/FontFile2").Should().Be(1, "对照：默认选项确实内嵌了字库");
        Pdf.Count(outlineBytes, "/FontFile2").Should().Be(0, "Options 传到了引擎，否则这里还是 1 份");
        outlineBytes.LongLength.Should().BeLessThan(vectorBytes.LongLength,
            $"轮廓模式只带用到的字形（实测矢量 {vectorBytes.LongLength / 1024}KB vs 轮廓 {outlineBytes.LongLength / 1024}KB）");
    }

    [Fact]
    public async Task 字体解析器被读取_换成西文字体后产物体积骤降()
    {
        var template = Fixture.Page(100, 50);
        template.Elements.Add(Fixture.Text("跨境面单", 8, 8, 80, 12, Fixture.CjkFont));

        var systemPath = _ws.PathOf("resolver-system.pdf");
        var latinPath = _ws.PathOf("resolver-latin.pdf");

        var system = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(systemPath));
        var latin = await _target.PrintAsync(
            new PrintDocument(template),
            new PrintSettings(latinPath) { FontResolver = new LatinOnlyFontResolver() });

        system.Success.Should().BeTrue(system.Message ?? "成功");
        latin.Success.Should().BeTrue(latin.Message ?? "成功");

        var latinBytes = File.ReadAllBytes(latinPath);
        Pdf.Count(latinBytes, "/FontFile2").Should().Be(1, "自定义解析器照样内嵌，只是换成小字库");

        // 绝对体积要拿"这台机器装了雅黑"当前提，否则对照物本身就不成立
        if (DefaultFontResolver.Shared.FamilyExists(Fixture.CjkFont))
        {
            var systemBytes = File.ReadAllBytes(systemPath);
            latinBytes.LongLength.Should().BeLessThan(systemBytes.LongLength / 4,
                $"字体来源归解析器管（实测系统雅黑 {systemBytes.LongLength / 1024}KB vs 指定 Arial {latinBytes.LongLength / 1024}KB）");
        }
    }

    [Fact]
    public async Task 图片解析器被读取_给了宿主解析器就不再记图片缺失()
    {
        var template = Fixture.Page(80, 50);
        template.Elements.Add(new ImageElement
        {
            Id = "img",
            XMm = 6,
            YMm = 6,
            WidthMm = 40,
            HeightMm = 20,
            Image = new ImageOptions { SourceType = ImageSourceType.ResolverKey, ResolverKey = "logo" },
        });

        var withoutPath = _ws.PathOf("images-none.pdf");
        var withPath = _ws.PathOf("images-resolver.pdf");
        var without = new RenderReport();
        var with = new RenderReport();

        var missing = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(withoutPath), without, default);
        var resolved = await _target.PrintAsync(
            new PrintDocument(template),
            new PrintSettings(withPath) { Images = new FixedImageResolver(PngHelper.RedSquare()) },
            with,
            default);

        missing.Success.Should().BeTrue(missing.Message ?? "成功");
        resolved.Success.Should().BeTrue(resolved.Message ?? "成功");

        without.Issues.Should().Contain(i => i.Kind == RenderIssueKind.ImageMissing, "对照：Images=null 时资源键取不到图");
        with.Issues.Where(i => i.Kind is RenderIssueKind.ImageMissing or RenderIssueKind.ImageDecodeFailed)
            .Should().BeEmpty("Images 传给了引擎，资源键就该解析成功");
    }

    [Fact]
    public async Task 渲染器实例被读取_复用同一实例的批量作业照常出页()
    {
        var template = Fixture.Page(100, 60);
        template.Elements.Add(Fixture.Text("{{no}}", 8, 8, 60, 10));
        var docs = Enumerable.Range(1, 3).Select(i => new PrintDocument(template, new { no = $"X{i}" })).ToList();
        var output = _ws.PathOf("renderer.pdf");

        var result = await _target.PrintBatchAsync(
            docs,
            new PrintSettings(output) { Renderer = TemplateRenderer.Default },
            default);

        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(3);
        Pdf.PageCount(File.ReadAllBytes(output)).Should().Be(3);
    }

    /// <summary>只认 Arial 的解析器：用来证明 <see cref="PrintSettings.FontResolver"/> 不是摆设。</summary>
    sealed class LatinOnlyFontResolver : IFontResolver
    {
        public SKTypeface Resolve(string fontFamily, bool bold, bool italic, char probeChar = 'A')
            => SKTypeface.FromFamilyName("Arial", bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
    }

    /// <summary>固定字节流的图片解析器。</summary>
    sealed class FixedImageResolver(byte[] payload) : IImageResolver
    {
        public byte[]? Resolve(string key) => key == "logo" ? payload : null;
    }

    /// <summary>造一张纯色 PNG。</summary>
    static class PngHelper
    {
        public static byte[] RedSquare()
        {
            using var bitmap = new SKBitmap(24, 24);
            using (var canvas = new SKCanvas(bitmap))
                canvas.Clear(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    /// <summary>删掉本条测试的工作区。</summary>
    public void Dispose() => _ws.Dispose();
}
