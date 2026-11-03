using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 复杂脚本（阿拉伯文 RTL）渲染取证：shaping 是否真的发生、glyph 序列是否按视觉顺序落到请求坐标、
/// 字间距是否按字形数计、GC 后字体数据是否还有效。
///
/// 期望值全部实测，并锚在**内嵌的 OFL 字体** Noto Naskh Arabic（<c>Assets/</c>，只在测试程序集里，产品不打包字体）：
/// 系统里能画阿拉伯文的字体随机器变（本机 Segoe UI，CI 的 ubuntu 可能只有不含阿拉伯 GSUB 的 DejaVu），
/// 拿系统字体写"连字必须发生"，换一台机器就悄悄变成空断言。
///
/// 每条断言都做过反证——把对应机制临时改坏，只有该炸的那几条 FAIL：
/// 去掉 <c>GuessSegmentProperties</c> → 2 炸；<c>Blob.FromStream</c> → GC 那条炸；
/// 绘制侧再反转一次 glyph 序列 → 4 炸；<c>DrawText</c> 传 (x, baseline) 二次平移 → 6 炸；
/// cursor 少加 <c>spacing</c> → 字间距那条炸；复杂度按实例判定 → 样式池那条炸；
/// <c>AlignedX</c> 去掉 RTL 左右互换 → 元素对齐那条炸；轮廓路径按 <c>x</c> 而非 <c>cursor</c> 平移 → 同点位那条炸。
/// </summary>
public sealed class ComplexScriptRenderingTests : IDisposable
{
    const double Dpi = 300;
    const double SizePt = 24;
    const double X = 20;
    const double Baseline = 40;
    const string Family = "Noto Naskh Arabic";

    static string Run(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    /// <summary>السلام：ل 紧邻字母必须换词形式，cmap 直译给的全是独立形式。</summary>
    static readonly string Lig = Run(0x0627, 0x0644, 0x0633, 0x0627, 0x0644, 0x0645);

    /// <summary>م م م م ا：逻辑末尾是高瘦的 alef，视觉顺序下必须落在最左。</summary>
    static readonly string AlefAtEnd = Run(0x0645, 0x0645, 0x0645, 0x0645, 0x0627);

    /// <summary>ا م م م م：逻辑首位是 alef，视觉顺序下必须落在最右。</summary>
    static readonly string AlefAtStart = Run(0x0627, 0x0645, 0x0645, 0x0645, 0x0645);

    readonly SKTypeface _face;
    readonly IFontResolver _resolver;
    readonly RenderContext _ctx;
    readonly TextStyle _style;
    readonly TextStyle _outlineStyle;

    public ComplexScriptRenderingTests()
    {
        var bytes = EmbeddedBytes("NotoNaskhArabic-VF.ttf");
        _face = SKTypeface.FromStream(new MemoryStream(bytes));
        _resolver = new FixedResolver(_face);
        _ctx = new RenderContext(new RenderOptions(), new RenderReport(), _resolver);
        _style = new TextStyle(_ctx, Family, SizePt, false, false, Lig, "t");
        _outlineStyle = new TextStyle(
            new RenderContext(new RenderOptions { PdfTextAsOutlines = true }, new RenderReport(), _resolver),
            Family, SizePt, false, false, Lig, "t");
    }

    public void Dispose()
    {
        _style.Dispose();
        _outlineStyle.Dispose();
        _face.Dispose();
    }

    [Fact]
    public void GSUB替换必须真的发生_shape结果不能等同cmap直译()
    {
        // 反证：HarfBuzzShaper.Shape 里去掉 buf.GuessSegmentProperties() 后，缓冲 script 仍是 INVALID，
        // hb_shape_full 一条 GSUB 特性都不套，实测字形序列退回 cmap 直译、宽度也从 17.831mm 涨回 26.822mm——
        // 那时"复杂脚本支持"只剩名字。
        var shaped = HarfBuzzShaper.Shape(Lig, _style.Typeface, _style.SizeMm, rtl: true);

        shaped.Should().NotBeNull("拿不到原生 HarfBuzz 时 Shape 返回 null 并静默降级，下面所有断言都会变成空断言，必须由这条先炸");
        shaped!.Glyphs.Should().HaveCount(Lig.Length);

        using var font = new SKFont(_style.Typeface, (float)_style.SizeMm);
        var cmap = new ushort[Lig.Length];
        font.GetGlyphs(Lig.AsSpan(), cmap);

        shaped.Glyphs.Select(g => g.GlyphId).Should().NotBeEquivalentTo(cmap,
            "阿拉伯文的词形式替换必须改字形 id（实测 shape 得 75,70,9,34,70,8，cmap 直译得 8,66,32,8,66,72）");

        _style.Measure(Lig).Should().BeLessThan(RawSkiaWidth(Lig),
            "连字与词形式替换后的步进必须窄于逐字符 cmap 步进之和（实测 17.831mm vs 26.822mm）");
    }

    [Fact]
    public void 复杂脚本必须落在请求的x与基线上()
    {
        // 反证：SKCanvas.DrawText(SKTextBlob, x, y, paint) 的 (x,y) 是对 blob 的**额外平移**，
        // 而 AddPositionedRun 已经写了绝对坐标。旧写法把 (20,40) 传进去，实测墨迹落在 (40.57, 73.73)mm。
        var ink = InkOf(_style, Lig);

        ink.Left.Should().BeGreaterThanOrEqualTo(X, "起笔不能落在请求 x 的左边");
        ink.Left.Should().BeLessThan(X + 1.2, "起笔侧边距只会把墨迹往右推一点点（实测 20.574mm）");
        ink.Right.Should().BeLessThanOrEqualTo(X + _style.Measure(Lig), "整行墨迹不能越过请求宽度推出的右界");
        ink.Right.Should().BeGreaterThanOrEqualTo(X + _style.Measure(Lig) - 1.5);
        ink.Top.Should().BeLessThan(Baseline, "阿拉伯文主体在基线以上");
        ink.Bottom.Should().BeGreaterThan(Baseline, "阿拉伯词尾有下沉笔画");
        ink.Bottom.Should().BeLessThan(Baseline + _style.SizeMm * 0.45,
            "下沉不超过半个字号，整行不会掉到基线以下太远（实测 42.503mm，字号 8.467mm）");
    }

    [Fact]
    public void RTL的glyph序列已是视觉从左到右_不得再反转一次()
    {
        // HarfBuzz 对反向段落自己做了缓冲反转（实测 "السلام" 的 cluster 依次 5,4,3,2,1,0）。
        // 反证：绘制时再按 n-1-i 取一次，整行镜像——高瘦的 alef 从最左跑到最右，
        // 下面两条断言同时不成立（实测镜像后 AlefAtEnd 上缘变成 37.08/34.38）。
        var end = HalfTops(_style, AlefAtEnd);
        end.LeftTop.Should().BeLessThan(end.RightTop - 1,
            "逻辑末尾的 alef 必须画在最左，左半区墨迹上缘更高（实测 34.375 vs 37.084mm）");

        var start = HalfTops(_style, AlefAtStart);
        start.RightTop.Should().BeLessThan(start.LeftTop - 1,
            "逻辑首位的 alef 必须画在最右，右半区墨迹上缘更高（实测 34.290 vs 37.084mm）");
    }

    [Fact]
    public void 轮廓模式与文本模式必须同点位()
    {
        // PdfTextAsOutlines 是给归档用的矢量轮廓导出，排版口径必须与栅格一致。
        // 反证：旧轮廓分支同样把 (x, baseline) 二次平移，墨迹整行飞出请求位置。
        var text = InkOf(_style, Lig);
        var outline = InkOf(_outlineStyle, Lig);

        outline.Left.Should().BeApproximately(text.Left, 0.3);
        outline.Right.Should().BeApproximately(text.Right, 0.3);
        outline.Top.Should().BeApproximately(text.Top, 0.3);
        outline.Bottom.Should().BeApproximately(text.Bottom, 0.3);

        var outlineEnd = HalfTops(_outlineStyle, AlefAtEnd);
        outlineEnd.LeftTop.Should().BeLessThan(outlineEnd.RightTop - 1, "轮廓模式也要保持 RTL 的视觉顺序");
    }

    [Fact]
    public void 字间距按字形数推进_度量与墨迹必须同步变宽()
    {
        // 连字把多个字符合成一个步进单元，间距若按字符数计，Measure 与 DrawLine 会分家：
        // 表格列宽按 Measure 折行，绘制却按另一套宽度溢出格子。
        const double spacing = 1.5;
        var gaps = HarfBuzzShaper.Shape(Lig, _style.Typeface, _style.SizeMm, true)!.Glyphs.Count - 1;

        _style.LetterSpacingMm = 0;
        var plainMeasure = _style.Measure(Lig);
        var plainWidth = InkOf(_style, Lig).Width;

        _style.LetterSpacingMm = spacing;
        var spacedMeasure = _style.Measure(Lig);
        var spacedWidth = InkOf(_style, Lig).Width;

        (spacedMeasure - plainMeasure).Should().BeApproximately(spacing * gaps, 0.01,
            "Measure 的间距按字形间隙计（实测 6 字形 → 5 个间隙）");
        (spacedWidth - plainWidth).Should().BeApproximately(spacing * gaps, 0.3,
            "绘制侧的 cursor 也要按字形间隙推进（实测墨迹宽度 16.764mm → 24.299mm）");
    }

    [Fact]
    public void 样式池实例按行判定脚本_拉丁探针构造的样式也要shape阿拉伯行()
    {
        // TextStyle 实例会被表式样式池复用，构造探针只是首个字符；复杂度若按实例判定，
        // 探针是拉丁的实例会把整段的阿拉伯行当西文画（不 shaping、不反转）。
        using var mixed = new TextStyle(_ctx, Family, SizePt, false, false, "1234\n" + Lig, "t");

        mixed.Measure(Lig).Should().BeApproximately(_style.Measure(Lig), 0.01, "同一行文本的度量不能因样式实例的探针而变");
        mixed.Measure(Lig).Should().BeLessThan(RawSkiaWidth(Lig, mixed.Typeface, mixed.SizeMm));
        var mixedTops = HalfTops(mixed, AlefAtEnd);
        mixedTops.LeftTop.Should().BeLessThan(mixedTops.RightTop - 1, "拉丁探针构造的样式，阿拉伯行仍须按视觉顺序绘制");
    }

    [Fact]
    public void GC压缩后shape结果不得静默漂移()
    {
        // 反证：HarfBuzzSharp 的 Blob.FromStream 把 blob 指到一个 fixed 块里的托管数组，出了 fixed 就没人钉着。
        // 旧实现实测到第 2~3 轮 Full GC 后，同一 Face 的 shape 结果静默退回 cmap 直译——不抛异常，只是画错。
        // 现在用 GCHandle 钉住 + MemoryMode.Duplicate 让 HarfBuzz 自己 malloc 拷一份。
        var first = HarfBuzzShaper.Shape(Lig, _style.Typeface, _style.SizeMm, true)!.Glyphs.Select(g => g.GlyphId).ToArray();
        var firstAdvance = _style.Measure(Lig);

        for (var round = 0; round < 4; round++)
        {
            var junk = Enumerable.Range(0, 8).Select(_ => new byte[3_000_000]).ToArray();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.KeepAlive(junk);

            HarfBuzzShaper.Shape(Lig, _style.Typeface, _style.SizeMm, true)!.Glyphs
                .Select(g => g.GlyphId).Should().Equal(first, $"第 {round} 轮 Full GC 后字形序列变了：字体缓冲已失效");
            _style.Measure(Lig).Should().BeApproximately(firstAdvance, 0.001, $"第 {round} 轮 Full GC 后步进变了");
        }
    }

    [Fact]
    public void RTL元素的Left对齐贴的是右边缘()
    {
        // AlignedX 的方向语义也必须是逐串判定：元素框里同一份样式要能给阿拉伯行和拉丁行各自归位。
        var arabic = ElementInk(AlefAtEnd, HorizontalAlign.Left);
        arabic.Right.Should().BeGreaterThan(68, "阿拉伯文 Left = 贴右边缘（盒子 10..70mm）");
        arabic.Left.Should().BeGreaterThan(45);

        var latin = ElementInk("MMMM", HorizontalAlign.Left);
        latin.Left.Should().BeLessThan(12, "拉丁文 Left 仍贴左边缘");
        latin.Right.Should().BeLessThan(60, "拉丁行不该被 RTL 语义推到右边");
    }

    double RawSkiaWidth(string text) => RawSkiaWidth(text, _style.Typeface, _style.SizeMm);

    static double RawSkiaWidth(string text, SKTypeface typeface, double sizeMm)
    {
        using var paint = new SKPaint { Typeface = typeface, TextSize = (float)sizeMm };
        return paint.MeasureText(text);
    }

    /// <summary>这一行的墨迹边界（mm）。零墨直接抛——没有墨的取证窗口会让下面所有断言变成空断言。</summary>
    Ink.RectMm InkOf(TextStyle style, string text)
    {
        using var bmp = RenderLine(style, text, X, Baseline);
        return Ink.BoundingBoxMm(bmp, Dpi)
            ?? throw new InvalidOperationException($"「{text}」整行零墨，取证窗口无效");
    }

    /// <summary>
    /// 把这一行的墨迹左右缘取出来，在中点竖切两半，分别返回两半里最深的墨迹上缘（mm）。
    /// 只依赖几何，不引用 shape 的字形顺序——否则"顺序对不对"就成了自证。
    /// </summary>
    static (double LeftTop, double RightTop) HalfTops(TextStyle style, string text)
    {
        using var bmp = RenderLine(style, text, X, Baseline);
        var mask = Ink.DarkMask(bmp);

        int minX = int.MaxValue, maxX = -1;
        for (var x = 0; x < mask.GetLength(0); x++)
        {
            var has = false;
            for (var y = 0; y < mask.GetLength(1); y++)
                if (mask[x, y]) { has = true; break; }
            if (!has) continue;
            minX = Math.Min(minX, x);
            maxX = x;
        }

        maxX.Should().BeGreaterThan(minX, "整行没有墨迹");
        var mid = (minX + maxX) / 2;

        return (TopInColumns(mask, minX, mid), TopInColumns(mask, mid + 1, maxX));
    }

    static double TopInColumns(bool[,] mask, int fromPx, int toPx)
    {
        var min = int.MaxValue;
        for (var x = fromPx; x <= toPx; x++)
            for (var y = 0; y < mask.GetLength(1); y++)
                if (mask[x, y]) { min = Math.Min(min, y); break; }

        min.Should().NotBe(int.MaxValue, $"第 {fromPx}~{toPx} 列里没有墨迹");
        return Ink.PxToMm(min, Dpi);
    }

    static SKBitmap RenderLine(TextStyle style, string text, double x, double baseline)
    {
        var bmp = new SKBitmap(new SKImageInfo((int)(80 * Dpi / 25.4), (int)(60 * Dpi / 25.4), SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale((float)(Dpi / 25.4));
            style.DrawLine(canvas, text, x, baseline, SKColors.Black);
        }

        return bmp;
    }

    Ink.RectMm ElementInk(string content, HorizontalAlign align)
    {
        var element = Fixture.Text(content, 10, 10, 60, 20);
        element.Text.FontFamily = Family;
        element.Text.FontSizePt = SizePt;
        element.Text.Wrap = false;
        element.Text.HorizontalAlign = align;

        var template = Fixture.Page(90, 45);
        template.Elements.Add(element);

        var report = new RenderReport();
        var bitmap = new SKBitmap(new SKImageInfo((int)(90 * Dpi / 25.4), (int)(45 * Dpi / 25.4), SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            TemplateRenderer.Default.Render(template, canvas, new RenderContext(new RenderOptions { Dpi = Dpi }, report, _resolver));
        }

        Fixture.NoDrawingFaults(report);
        report.Issues.Should().NotContain(i => i.Kind == RenderIssueKind.FontFallback, "解析器就是那款内嵌字体，报回退说明字体没接上");

        var window = new SKRect(9, 9, 71, 32);
        var ink = Ink.BoundingBoxInMm(bitmap, Dpi, window);
        bitmap.Dispose();

        return ink ?? throw new InvalidOperationException($"窗口 {window} 内没有墨迹，{content} 根本没画出来");
    }

    static byte[] EmbeddedBytes(string suffix)
    {
        var asm = typeof(ComplexScriptRenderingTests).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    sealed class FixedResolver(SKTypeface typeface) : IFontResolver
    {
        public SKTypeface Resolve(string fontFamily, bool bold, bool italic, char probeChar = 'A') => typeface;

        public bool FamilyExists(string fontFamily) => true;
    }
}
