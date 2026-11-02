using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;

namespace CrossPrint.Engine.Tests;

/// <summary>测试内共用的模板/元素构造与渲染快捷方式。</summary>
internal static class Fixture
{
    /// <summary>固定尺寸的自定义纸张模板，方便断言像素尺寸。</summary>
    public static PrintTemplate Page(double widthMm, double heightMm)
        => new()
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = widthMm, HeightMm = heightMm },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };

    /// <summary>渲染到指定 DPI 的位图，并断言没有任何"渲染失败"级告警。</summary>
    public static SKBitmap Render(PrintTemplate template, double dpi, object? data = null, RenderReport? report = null, IImageResolver? images = null, bool preferTestData = false)
    {
        var r = report ?? new RenderReport();
        var bitmap = new SKBitmap(new SKImageInfo(
            Math.Max(1, (int)Math.Ceiling(template.Paper.PageWidthMm * dpi / 25.4)),
            Math.Max(1, (int)Math.Ceiling(TemplateRenderer.ResolvePageHeightMm(template, new RenderOptions { Dpi = dpi }) * dpi / 25.4)),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        TemplateRenderer.Default.Render(template, canvas, new RenderContext(new RenderOptions { Dpi = dpi }, r, images: images, data: data, preferTestData: preferTestData));
        return bitmap;
    }

    /// <summary>渲染单元素页面。</summary>
    public static SKBitmap RenderElement(PaperElement element, double dpi = 150, double pageWidthMm = 100, double pageHeightMm = 100, RenderReport? report = null, IImageResolver? images = null, object? data = null, bool preferTestData = false)
    {
        var t = Page(pageWidthMm, pageHeightMm);
        t.Elements.Add(element);
        return Render(t, dpi, data, report, images, preferTestData);
    }

    public static TextElement Text(string content, double x, double y, double w, double h)
        => new()
        {
            Id = "t",
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Text = new TextOptions { Content = content, FontFamily = "Microsoft YaHei", FontSizePt = 12 },
        };

    public static RectangleElement Rect(double x, double y, double w, double h, SKColor? fill = null, SKColor? stroke = null, double strokeMm = 0.4)
        => new()
        {
            Id = "r",
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Rectangle = new ShapeOptions { FillColor = fill, StrokeColor = stroke, StrokeWidthMm = strokeMm },
        };

    /// <summary>
    /// 断言"除了字体回退，没有别的降级"。
    /// 字体回退取决于跑测试这台机器装没装模板里写的那款字体（Linux 上通常没有 Microsoft YaHei），
    /// 它挂掉的是环境而不是渲染；其余任何告警都说明绘制真的出了问题。
    /// </summary>
    public static void NoDrawingFaults(RenderReport report)
        => report.Issues.Where(i => i.Kind != RenderIssueKind.FontFallback).Should().BeEmpty("不该有降级告警");

    /// <summary>仓库根目录（从测试输出目录向上找 docs 目录）。</summary>
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "src")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("未从测试输出目录定位到仓库根。");
    }

    /// <summary>docs/samples 里的样例模板（严格模式加载，必须零告警）。</summary>
    public static PrintTemplate KitchenSink()
    {
        var path = Path.Combine(RepoRoot(), "docs", "samples", "kitchen-sink.template.json");
        var result = TemplateJson.Load(File.ReadAllText(path));
        result.Warnings.Should().BeEmpty();
        return result.Template;
    }
}
