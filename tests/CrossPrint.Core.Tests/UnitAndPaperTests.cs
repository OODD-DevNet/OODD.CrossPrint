using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>单位换算必须精确到 0.01，且不存在任何隐式像素存储。</summary>
public class UnitConverterTests
{
    [Theory]
    [InlineData(25.4, 96, 96)]
    [InlineData(10, 96, 37.7953)]
    [InlineData(210, 300, 2480.315)]
    [InlineData(0, 96, 0)]
    [InlineData(1, 72, 2.8346)]
    public void Mm_to_pixels(double mm, double dpi, double expectedPx)
        => UnitConverter.MmToPx(mm, dpi).Should().BeApproximately(expectedPx, 0.01);

    [Theory]
    [InlineData(72, 96, 96)]
    [InlineData(12, 300, 50)]
    [InlineData(10.5, 96, 14)]
    public void Points_to_pixels(double pt, double dpi, double expectedPx)
        => UnitConverter.PtToPx(pt, dpi).Should().BeApproximately(expectedPx, 0.01);

    [Theory]
    [InlineData(25.4, 72)]
    [InlineData(1, 2.8346)]
    [InlineData(210, 595.2756)]
    [InlineData(297, 841.8898)]
    [InlineData(0, 0)]
    public void Mm_to_pdf_points(double mm, double expectedPt)
        => UnitConverter.MmToPt(mm).Should().BeApproximately(expectedPt, 0.001);

    [Fact]
    public void A4_maps_to_the_standard_pdf_media_box()
    {
        // PDF 阅读器里量 A4 必须是 595.28 x 841.89 pt，否则 mm 建模就是错的
        UnitConverter.MmToPt(210).Should().BeApproximately(595.28, 0.01);
        UnitConverter.MmToPt(297).Should().BeApproximately(841.89, 0.01);
    }

    [Fact]
    public void Pixel_and_millimetre_round_trip_is_stable()
    {
        const double dpi = 300;
        for (var mm = 0.0; mm <= 300; mm += 7.3)
            UnitConverter.PxToMm(UnitConverter.MmToPx(mm, dpi), dpi).Should().BeApproximately(mm, 1e-9);
    }

    [Fact]
    public void One_inch_is_twenty_five_point_four_mm_and_seventy_two_points()
    {
        UnitConverter.MmPerInch.Should().Be(25.4);
        UnitConverter.PtPerInch.Should().Be(72);
        UnitConverter.MmToPx(25.4, 300).Should().Be(300);
        UnitConverter.PtToPx(72, 300).Should().Be(300);
    }

    [Fact]
    public void A4_page_is_about_210_by_297_mm()
    {
        UnitConverter.MmToPx(210, 300).Should().BeApproximately(2480.3, 0.1);
        UnitConverter.MmToPx(297, 300).Should().BeApproximately(3507.9, 0.1);
    }

    [Fact]
    public void RenderContext_font_size_is_expressed_in_mm_space()
    {
        // 引擎对 canvas 施加 Scale(UnitsPerMm) 后，字号必须以 mm 计，否则会被 CTM 二次放大
        var ctx = new RenderContext(new RenderOptions { Dpi = 300 }, new RenderReport());
        ctx.PtToUnits(72).Should().BeApproximately(25.4, 1e-9);
        ctx.UnitsPerMm.Should().BeApproximately(300 / 25.4, 1e-9);
    }

    [Fact]
    public void Options_derivation_keeps_other_fields()
    {
        var o = new RenderOptions { Dpi = 96, PdfRasterDpi = 600 };
        o.WithDpi(300).Dpi.Should().Be(300);
        o.WithDpi(300).PdfRasterDpi.Should().Be(600);
        o.WithDpi(300).PageBackgroundColor.Should().Be(o.PageBackgroundColor);
    }

    /// <summary>
    /// <c>UnitsPerMm</c> 直接除以 Dpi：0/NaN/Infinity 漏进渲染期只会把整页换算成 0 或 NaN 尺寸，
    /// 产物变成零墨或空页。所以闸在构造选项时就落下，<c>with</c>（WithDpi）也要走同一道闸。
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Dpi_在入口拒掉非有限正数(double dpi)
    {
        var create = () => new RenderOptions { Dpi = dpi };
        create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Dpi");

        var opts = new RenderOptions { Dpi = 96 };
        var derived = () => opts.WithDpi(dpi);
        derived.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Dpi");
    }

    [Theory]
    [InlineData(72d)]
    [InlineData(96d)]
    [InlineData(300d)]
    public void Dpi_合法值原样生效(double dpi)
    {
        var opts = new RenderOptions { Dpi = dpi };
        opts.Dpi.Should().Be(dpi);
        opts.UnitsPerMm.Should().BeApproximately(dpi / 25.4, 1e-9);
    }

    /// <summary>PDF 的 MediaBox 量化公式同一分母，0/NaN 会在 Skia 里除出无效页框。</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-2540d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PdfRasterDpi_在入口拒掉非有限正数(double dpi)
    {
        var create = () => new RenderOptions { PdfRasterDpi = dpi };
        create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("PdfRasterDpi");
    }
}

/// <summary>纸张与留白：方向交换、连续纸自适应、预设表。</summary>
public class PaperSpecTests
{
    [Fact]
    public void Landscape_swaps_width_and_height()
    {
        var p = new PaperSpec { WidthMm = 210, HeightMm = 297, Orientation = PaperOrientation.Landscape };
        p.PageWidthMm.Should().Be(297);
        p.PageHeightMm.Should().Be(210);
    }

    [Theory]
    [InlineData(PaperTypePresets.Receipt80, PaperOrientation.Portrait, 80, 0)]
    [InlineData(PaperTypePresets.Receipt80, PaperOrientation.Landscape, 80, 0)]
    [InlineData(PaperTypePresets.A4, PaperOrientation.Portrait, 210, 297)]
    [InlineData(PaperTypePresets.A4, PaperOrientation.Landscape, 297, 210)]
    public void Orientation_swap_skips_continuous_paper(string preset, PaperOrientation orientation, double width, double height)
    {
        var p = PaperSpec.FromPreset(preset);
        p.Orientation = orientation;

        p.PageWidthMm.Should().Be(width, "连续纸的 0 是自适应长度而不是可旋转的维度，旋转后仍要有纸宽");
        p.PageHeightMm.Should().Be(height);
        p.IsContinuous.Should().Be(preset == PaperTypePresets.Receipt80);
    }

    [Theory]
    [InlineData(PaperTypePresets.A3, 297, 420)]
    [InlineData(PaperTypePresets.A4, 210, 297)]
    [InlineData(PaperTypePresets.A5, 148, 210)]
    [InlineData(PaperTypePresets.B5, 176, 250)]
    [InlineData(PaperTypePresets.Receipt80, 80, 0)]
    [InlineData(PaperTypePresets.Receipt58, 58, 0)]
    [InlineData(PaperTypePresets.Label100x150, 100, 150)]
    public void Presets_carry_expected_sizes(string preset, double w, double h)
    {
        PaperTypePresets.TryGet(preset, out var size).Should().BeTrue();
        size.WidthMm.Should().Be(w);
        size.HeightMm.Should().Be(h);
    }

    [Fact]
    public void Receipt_presets_are_continuous_paper()
    {
        PaperSpec.FromPreset(PaperTypePresets.Receipt80).IsContinuous.Should().BeTrue();
        PaperSpec.FromPreset(PaperTypePresets.A4).IsContinuous.Should().BeFalse();
    }

    [Fact]
    public void From_preset_rejects_unknown_name()
    {
        Action act = () => PaperSpec.FromPreset("A99");
        act.Should().Throw<ArgumentException>().WithMessage("*A99*");
    }

    [Fact]
    public void Continuous_page_height_follows_lowest_element()
    {
        var t = new PrintTemplate { Paper = PaperSpec.FromPreset(PaperTypePresets.Receipt80), Margins = new ThicknessMm(5, 5, 5, 5) };
        t.Add(new TextElement { YMm = 10, HeightMm = 8 });
        t.Add(new TextElement { YMm = 40, HeightMm = 20 });

        t.ResolvePageHeightMm().Should().Be(62); // 最低元素底边 60 + 余量 2
    }

    [Fact]
    public void Fixed_page_height_ignores_elements()
    {
        var t = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        t.Add(new TextElement { YMm = 5000, HeightMm = 10 });
        t.ResolvePageHeightMm().Should().Be(297);
    }

    [Fact]
    public void Margins_default_to_ten_mm()
    {
        var m = new ThicknessMm();
        m.Top.Should().Be(10);
        m.Left.Should().Be(10);
    }

    [Fact]
    public void Default_template_is_portrait_a4()
    {
        var t = PrintTemplate.CreateDefault();
        t.SchemaVersion.Should().Be("0.1");
        t.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
        t.Paper.PageWidthMm.Should().Be(210);
    }
}

/// <summary>字体解析必须永远返回可用字体，不得因缺失而抛。</summary>
public class FontResolverTests
{
    [Theory]
    [InlineData("Microsoft YaHei")]
    [InlineData("Totally Missing Font Name 9999")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_never_returns_null(string family)
        => DefaultFontResolver.Shared.Resolve(family, false, false).Should().NotBeNull();

    [Fact]
    public void Resolve_is_stable_for_repeated_calls()
    {
        var a = DefaultFontResolver.Shared.Resolve("Arial", true, false);
        var b = DefaultFontResolver.Shared.Resolve("Arial", true, false);
        a.Should().BeSameAs(b, "同一请求必须命中缓存，避免每帧重建 typeface");
    }

    [Fact]
    public void Bold_and_regular_can_differ()
    {
        var regular = DefaultFontResolver.Shared.Resolve("Arial", false, false);
        var bold = DefaultFontResolver.Shared.Resolve("Arial", true, false);
        regular.Should().NotBeNull();
        bold.Should().NotBeNull();
    }

    [Fact]
    public void Cjk_probe_char_resolves_a_font_that_can_draw_it()
    {
        var typeface = DefaultFontResolver.Shared.Resolve("NoSuchFontAtAll", false, false, '中');
        var metrics = typeface.GetGlyphs("中".AsSpan());
        metrics.Should().NotBeEmpty();
        metrics[0].Should().NotBe(0, "回退字体必须真正含该汉字字形，而不是落到只支持西文的字体");
    }

    [Fact]
    public void 缓存有条数上限_超出后最久未用的被淘汰()
    {
        // 不用 Shared：测试要灌几百个键，别污染共享实例的水位
        var resolver = new DefaultFontResolver();

        // 先放一个"要存活"的键，再灌满缓存；触及它使其前移（LRU 命中即前移）
        var kept = resolver.Resolve("KeptFamily", false, false, '守');
        for (var i = 0; i < DefaultFontResolver.Capacity - 1; i++)
            resolver.Resolve($"Flood-{i}", false, false);
        resolver.Resolve("KeptFamily", false, false, '守').Should().BeSameAs(kept, "未超容时命中必须拿到同一实例");

        // 再灌 20 个唯一键：挤掉的应是最早灌入的 Flood-0..19，kept 因最近被触及而存活
        for (var i = 0; i < 20; i++)
            resolver.Resolve($"Late-{i}", false, false);

        resolver.CacheCount.Should().Be(DefaultFontResolver.Capacity,
            "缓存必须钉死在上限：淘汰只丢引用，被淘汰的键下次解析重新匹配，语义不变");
        resolver.Resolve("KeptFamily", false, false, '守').Should().BeSameAs(kept, "最近用过的条目不该被淘汰");
        resolver.Resolve("Flood-0", false, false).Should().NotBeNull("被淘汰的条目重新解析仍要给出能画的字体");
    }

    [Fact]
    public void 反复解析同批键_缓存水位恒不超上限()
    {
        var resolver = new DefaultFontResolver();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < DefaultFontResolver.Capacity + 30; i++)
                resolver.Resolve($"Round-{round}-Key-{i}", false, false);
        }

        // 每轮键集不同，总量远超容量：混跑后仍不得突破上限（键集相同的重复解析只会命中，不新增）
        resolver.CacheCount.Should().Be(DefaultFontResolver.Capacity);
    }
}
