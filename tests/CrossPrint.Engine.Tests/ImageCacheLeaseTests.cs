using System.Text.RegularExpressions;
using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 图片缓存的租约纪律取证。核心事实是先探针实测出来的（SkiaSharp 2.88.9）：
/// 动一个已 Dispose 的 SKImage 不是能 catch 的 ObjectDisposedException，而是 AccessViolationException，
/// 打印服务当场没了——所以"淘汰即释放"必须由租约兜住，而不是靠渲染器运气好。
/// 断言顺序固定：先看账（计数），再看墨（画图）；机制被拆掉时账先报错，用例才不会把整个测试宿主带走。
/// </summary>
public class ImageCacheLeaseTests
{
    const double Dpi = 300;
    static readonly SKColor HeldRed = new(210, 30, 30);

    /// <summary>
    /// 手里有租约时超容量淘汰：条目只退役、不释放，图还能用、还画得出墨。
    /// 这就是并发批量导出里"另一个线程刚把这张图拿到手还没画完"的确定性版本。
    /// </summary>
    [Fact]
    public void 持租约超容量淘汰后图片仍可用并画得出墨()
    {
        var cache = new ImageCache();
        var held = cache.GetOrAdd("in-flight", () => MakeImage(HeldRed, 24, 12));
        held.Should().NotBeNull();

        // 挤爆缓存：容量 32，再塞 32 张不同的，最早那条必然被淘汰
        for (var i = 1; i <= ImageCache.Capacity; i++)
        {
            using var other = cache.GetOrAdd("churn-" + i, () => MakeImage(SKColors.Blue));
            other.Should().NotBeNull();
        }

        cache.Count.Should().Be(ImageCache.Capacity, "索引上限是硬约束，超出的条目要摘出去");
        cache.PendingReleaseCount.Should().Be(1, "被淘汰但还欠着租约的条目只退役，不许释放");

        // 被持有的那张图必须还是完整的：尺寸读得到、画得出墨（机制拆掉时这里会是原生层 use-after-free）
        PaintAndCount(held!.Image, 24, 12).Should().BeGreaterThan(0, "租约在手期间这张图不许被放掉");
        held.Dispose();
    }

    /// <summary>归还最后一个租约，退役条目才被释放——账目回落，缓存不欠着东西。</summary>
    [Fact]
    public void 归还最后一个租约后才释放退役条目()
    {
        var cache = new ImageCache();
        var held = cache.GetOrAdd("in-flight", () => MakeImage(HeldRed, 24, 12))!;
        var second = cache.GetOrAdd("in-flight", () => MakeImage(HeldRed, 24, 12))!;

        for (var i = 1; i <= ImageCache.Capacity; i++)
        {
            using var other = cache.GetOrAdd("churn-" + i, () => MakeImage(SKColors.Blue));
        }

        // 两个持有者：还掉一个仍然欠着，还掉最后一个才结清
        cache.PendingReleaseCount.Should().Be(1);
        held.Dispose();
        cache.PendingReleaseCount.Should().Be(1, "还有第二个租约没归还");
        PaintAndCount(second.Image, 24, 12).Should().BeGreaterThan(0);
        second.Dispose();
        cache.PendingReleaseCount.Should().Be(0, "最后一个租约归还后条目才真正释放");
    }

    /// <summary>Clear() 也不放掉仍在租约里的图：清空是"摘账"，不是"当场释放"。</summary>
    [Fact]
    public void 清空缓存不放掉仍在租约里的图片()
    {
        var cache = new ImageCache();
        var held = cache.GetOrAdd("busy", () => MakeImage(HeldRed, 24, 12))!;
        cache.GetOrAdd("idle", () => MakeImage(SKColors.Blue, 8, 8))!.Dispose();

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.PendingReleaseCount.Should().Be(1, "Clear 遇到在用的条目只能记账待还");
        PaintAndCount(held.Image, 24, 12).Should().BeGreaterThan(0);
        held.Dispose();
        cache.PendingReleaseCount.Should().Be(0);
    }

    /// <summary>
    /// 整页渲染超过容量的不同图片：第一张与最后一张都要有墨（渲染路径本身不受淘汰影响），
    /// 且整页零绘制降级告警。
    /// </summary>
    [Fact]
    public void 超过容量的整页渲染每张图都有墨()
    {
        var count = ImageCache.Capacity + 8;
        var (bitmap, colors, report) = RenderGrid(count);
        using (bitmap)
        {
            // 实测：40 张不同图片整页渲染，零告警（超容量淘汰发生在渲染途中，不该留下任何降级记录）
            report.Issues.Should().BeEmpty();

            Ink.ColorCountInMm(bitmap, Dpi, WindowOf(0), colors[0], 20).Should().BeGreaterThan(0, "超容量淘汰不该让第一张图消失");
            Ink.ColorCountInMm(bitmap, Dpi, WindowOf(count - 1), colors[count - 1], 20).Should().BeGreaterThan(0, "最后一张同样要画出来");
            ImageCache.Shared.Count.Should().BeLessThanOrEqualTo(ImageCache.Capacity, "索引上限在真实渲染路径上也要守住");
        }
    }

    /// <summary>正向对照：同样的构造、同样的取证窗口，不超容量时也必须有墨——否则上面那条"有墨"什么都证明不了。</summary>
    [Fact]
    public void 不超容量的正向对照同样有墨()
    {
        var count = ImageCache.Capacity - 8;
        var (bitmap, colors, report) = RenderGrid(count);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.ColorCountInMm(bitmap, Dpi, WindowOf(0), colors[0], 20).Should().BeGreaterThan(0);
            Ink.ColorCountInMm(bitmap, Dpi, WindowOf(count - 1), colors[count - 1], 20).Should().BeGreaterThan(0);
        }
    }

    /// <summary>判别力对照：同一张位图上，纸白处的窗口必须是 0 墨，否则"窗口里有墨"可能只是阈值太宽。</summary>
    [Fact]
    public void 取证窗口错位时没有墨()
    {
        var count = ImageCache.Capacity + 8;
        var (bitmap, colors, _) = RenderGrid(count);
        using (bitmap)
        {
            // 元素只铺到 y≈45mm，下面这片是纸白
            Ink.ColorCountInMm(bitmap, Dpi, new SKRect(2, 100, 12, 112), colors[0], 20).Should().Be(0, "空白处必须干净，窗口才有鉴别力");
            // 同一格挪到隔壁元素的位置，也不该撞上第一张的颜色
            Ink.ColorCountInMm(bitmap, Dpi, WindowOf(11), colors[0], 0).Should().Be(0, "严格同色时另一张图不能冒充第一张");
        }
    }

    /// <summary>
    /// 批量 PDF：页数超过缓存容量（淘汰必然发生在文档还开着的时候），一页一图都不许丢。
    /// 口径：数 PDF 里的图片 XObject 个数与页数对齐（实测 45 页 → 45 个）。
    /// </summary>
    [Fact]
    public void 批量PDF页数超过缓存容量时一页一图都不丢()
    {
        var pages = ImageCache.Capacity + 13;
        var templates = new List<PrintTemplate>();
        var colors = new SKColor[pages];
        for (var i = 0; i < pages; i++)
        {
            colors[i] = ColorOf(i);
            var t = Fixture.Page(40, 30);
            t.Elements.Add(new ImageElement
            {
                Id = "img" + i,
                XMm = 5,
                YMm = 5,
                WidthMm = 20,
                HeightMm = 10,
                Image = new ImageOptions
                {
                    SourceType = ImageSourceType.EmbeddedBase64,
                    Data = Convert.ToBase64String(PngBytes(colors[i], 24, 12)),
                    FitMode = ImageFitMode.Fill,
                },
            });
            templates.Add(t);
        }

        using var stream = new MemoryStream();
        var (report, pageCount) = BatchPdfExporter.ExportPages(templates, stream);

        pageCount.Should().Be(pages);
        Fixture.NoDrawingFaults(report);
        var pdf = stream.ToArray();
        PdfProbe.PageCount(pdf).Should().Be(pages);
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Subtype\s*/Image").Count.Should().Be(pages, "淘汰过的条目仍被页面租约钉住，文档关闭时一张都不能少");
    }

    /// <summary>
    /// 实测口径（渲染规范 §8.1 就是照这条写的）：录进 picture 的绘制不靠托管引用活着——
    /// 录制对象自己持有原生引用，所以即使渲染路径上没有袋、租约已归还、缓存随后被挤爆把包装放掉，
    /// 回放照样画得出墨。租约纪律不是为这条活着的（它防的是并发线程直接用同一个引用），
    /// 但这条必须钉住：它决定了"袋什么时候可以归还"。
    /// </summary>
    [Fact]
    public void 录进picture的图片在缓存被挤爆后仍能回放()
    {
        var count = ImageCache.Capacity + 8;
        var template = Fixture.Page(120, 120);
        var colors = new SKColor[count];
        for (var i = 0; i < count; i++)
        {
            colors[i] = ColorOf(i);
            template.Elements.Add(new ImageElement
            {
                Id = "img" + i,
                XMm = 2 + (i % 10) * 11,
                YMm = 2 + (i / 10) * 11,
                WidthMm = 10,
                HeightMm = 10,
                Image = new ImageOptions
                {
                    SourceType = ImageSourceType.EmbeddedBase64,
                    Data = Convert.ToBase64String(PngBytes(colors[i], 16, 16)),
                    FitMode = ImageFitMode.Fill,
                },
            });
        }

        // 故意不挂袋：宿主直接调渲染入口时的形态，每个元素的租约画完就还了
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, 1420, 1420));
        var report = new RenderReport();
        TemplateRenderer.Default.Render(template, canvas, new RenderContext(new RenderOptions { Dpi = Dpi }, report));
        using var picture = recorder.EndRecording();

        // 录制完成后把进程级缓存挤爆：这些条目的托管包装此刻已被释放
        for (var i = 0; i < ImageCache.Capacity * 2; i++)
        {
            using var churn = ImageCache.Shared.GetOrAdd("replay-churn-" + Guid.NewGuid().ToString("N"), () => MakeImage(SKColors.Blue, 8, 8));
        }

        using var bitmap = new SKBitmap(1420, 1420, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var target = new SKCanvas(bitmap))
        {
            target.Clear(SKColors.White);
            target.DrawPicture(picture);
            target.Flush();
        }

        report.Issues.Should().BeEmpty();
        Ink.ColorCountInMm(bitmap, Dpi, WindowOf(0), colors[0], 20).Should().BeGreaterThan(0, "录制自己持有原生引用，回放不该是空的");
        Ink.ColorCountInMm(bitmap, Dpi, WindowOf(count - 1), colors[count - 1], 20).Should().BeGreaterThan(0);
        Ink.ColorCountInMm(bitmap, Dpi, new SKRect(2, 100, 12, 112), colors[0], 20).Should().Be(0, "空白处仍应是空白");
    }

    /// <summary>
    /// 两个线程渲染互不相交的图片集，总量超过缓存容量：交织用事件钉死（不靠定时器），
    /// A 录完一页并持有租约时 B 才翻搅缓存，B 翻搅完 A 才回放。
    /// 诚实说明：把租约机制拆掉，这条不会变红——实测录制对象自己持有原生引用，回放照样有墨；
    /// 能变红的是上面"直接持有引用"的用例。这条证明的是多线程下账目不烂、双放不崩、上限守得住。
    /// </summary>
    [Fact]
    public void 两个线程共用缓存且总量超容时各画各的()
    {
        var keysA = Enumerable.Range(0, 10).Select(i => "conc-A-" + Guid.NewGuid().ToString("N")).ToArray();
        var keysB = Enumerable.Range(0, ImageCache.Capacity + 10).Select(i => "conc-B-" + Guid.NewGuid().ToString("N")).ToArray();

        var leasesHeld = new ManualResetEventSlim(false);
        var churnDone = new ManualResetEventSlim(false);
        SKBitmap? replay = null;
        SKBitmap? bOut = null;
        var aIssues = new RenderReport();
        var bIssues = new RenderReport();

        var threadA = new Thread(() =>
        {
            var context = new RenderContext(new RenderOptions { Dpi = Dpi }, aIssues, images: new FixedResolver(keysA, HeldRed));
            using var bag = ImageLeaseBag.Open(context);
            using var recorder = new SKPictureRecorder();
            // 录制边界要盖住整页（120mm @300dpi ≈ 1417px），否则右侧的取证窗口落在剪辑外，"没墨"是裁掉的不是丢的
            var canvas = recorder.BeginRecording(new SKRect(0, 0, 1420, 1420));
            TemplateRenderer.Default.Render(GridTemplate(keysA), canvas, context);
            using var picture = recorder.EndRecording();
            leasesHeld.Set();
            // 等 B 把缓存挤爆之后再回放：此时 A 这次渲染取过的图全部还在租约里
            churnDone.Wait();
            var bitmap = new SKBitmap(1420, 1420, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var target = new SKCanvas(bitmap))
            {
                target.Clear(SKColors.White);
                target.DrawPicture(picture);
                target.Flush();
            }

            replay = bitmap;
        });

        var threadB = new Thread(() =>
        {
            leasesHeld.Wait();
            // B 不挂袋：即时绘制，画完即还，翻搅会把 A 仍持有的条目挤出去
            bOut = Fixture.Render(GridTemplate(keysB), Dpi, report: bIssues, images: new FixedResolver(keysB, new SKColor(30, 30, 200)));
            churnDone.Set();
        });

        threadA.Start();
        threadB.Start();
        threadA.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
        threadB.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();

        var aReplay = replay!;
        var bBitmap = bOut!;
        using (aReplay)
        using (bBitmap)
        {
            Fixture.NoDrawingFaults(aIssues);
            Fixture.NoDrawingFaults(bIssues);
            // A 的第一格与右侧一格（同一页里的两个不同资源键）
            Ink.ColorCountInMm(aReplay, Dpi, WindowOf(0), HeldRed, 20).Should().BeGreaterThan(0, "并发翻搅不该抹掉 A 录制里的图");
            Ink.ColorCountInMm(aReplay, Dpi, WindowOf(5), HeldRed, 20).Should().BeGreaterThan(0);
            Ink.ColorCountInMm(bBitmap, Dpi, WindowOf(0), new SKColor(30, 30, 200), 20).Should().BeGreaterThan(0, "B 自己的整页也该有墨");
        }

        ImageCache.Shared.Count.Should().BeLessThanOrEqualTo(ImageCache.Capacity, "并发下索引上限也要守住");
    }

    /// <summary>把图片画进一张 300dpi 的小位图并数墨，作为"这张 SKImage 还活着"的取证。</summary>
    static int PaintAndCount(SKImage image, double widthMm, double heightMm)
    {
        var px = (int)Math.Ceiling(widthMm * Dpi / 25.4);
        var py = (int)Math.Ceiling(heightMm * Dpi / 25.4);
        using var bitmap = new SKBitmap(px, py, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, (float)widthMm, (float)heightMm));
            canvas.Flush();
        }

        return Ink.ColorCountInMm(bitmap, Dpi, new SKRect(1, 1, (float)widthMm - 1, (float)heightMm - 1), HeldRed, 20);
    }

    /// <summary>渲染 n 张互不相同的内嵌图片，按 10×10mm 网格铺在 120×120mm 页面上。</summary>
    static (SKBitmap Bitmap, SKColor[] Colors, RenderReport Report) RenderGrid(int n)
    {
        var template = Fixture.Page(120, 120);
        var colors = new SKColor[n];
        for (var i = 0; i < n; i++)
        {
            colors[i] = ColorOf(i);
            template.Elements.Add(new ImageElement
            {
                Id = "img" + i,
                XMm = 2 + (i % 10) * 11,
                YMm = 2 + (i / 10) * 11,
                WidthMm = 10,
                HeightMm = 10,
                Image = new ImageOptions
                {
                    SourceType = ImageSourceType.EmbeddedBase64,
                    Data = Convert.ToBase64String(PngBytes(colors[i], 16, 16)),
                    FitMode = ImageFitMode.Fill,
                },
            });
        }

        var report = new RenderReport();
        return (Fixture.Render(template, Dpi, report: report), colors, report);
    }

    /// <summary>同网格布局，但图片来源换成资源键 + 宿主解析器（走 key: 那条缓存分支）。键由调用方给，保证与解析器对得上。</summary>
    static PrintTemplate GridTemplate(string[] keys)
    {
        var template = Fixture.Page(120, 120);
        for (var i = 0; i < keys.Length; i++)
        {
            template.Elements.Add(new ImageElement
            {
                Id = "img" + i,
                XMm = 2 + (i % 10) * 11,
                YMm = 2 + (i / 10) * 11,
                WidthMm = 10,
                HeightMm = 10,
                Image = new ImageOptions
                {
                    SourceType = ImageSourceType.ResolverKey,
                    ResolverKey = keys[i],
                    FitMode = ImageFitMode.Fill,
                },
            });
        }

        return template;
    }

    /// <summary>第 i 格的取证窗口（元素内缩 0.5mm，避开边缘抗锯齿）。</summary>
    static SKRect WindowOf(int i)
    {
        var x = 2 + (i % 10) * 11;
        var y = 2 + (i / 10) * 11;
        return new SKRect(x + 0.5f, y + 0.5f, x + 9.5f, y + 9.5f);
    }

    /// <summary>逐张可区分的颜色：通道差 5，容差 20 下靠"同色"取证的窗口只可能命中自己那一格。</summary>
    static SKColor ColorOf(int i) => new((byte)Math.Min(250, 20 + i * 5), 20, 20);

    static SKImage MakeImage(SKColor color, int w = 24, int h = 12)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        return SKImage.FromBitmap(bitmap);
    }

    static byte[] PngBytes(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>每个键回一张纯色 PNG 的解析器（纯色一致，便于按颜色数墨）。</summary>
    sealed class FixedResolver(string[] keys, SKColor color) : IImageResolver
    {
        readonly Dictionary<string, byte[]> _payloads = keys.ToDictionary(k => k, _ => PngBytes(color, 16, 16), StringComparer.Ordinal);

        public byte[]? Resolve(string key) => _payloads.TryGetValue(key, out var p) ? p : null;
    }
}
