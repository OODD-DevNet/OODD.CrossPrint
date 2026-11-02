using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// EAN/UPC 护栏条：ISO/IEC 15420 要求起始/中间/终止护栏比数据条长。
/// 期望值不是照规范抄的——先量 ZXing 编码出来的模块总数与护栏段比特（95/67/51，101/01010/010101），
/// 再量渲染位图里护栏列与数据列的墨迹高度。
/// </summary>
public class BarcodeGuardBarTests
{
    /// <summary>护栏区间（模块下标，左闭右开），与渲染器同一份口径；模块总数就是实测的编码长度。</summary>
    static IReadOnlyList<(int Start, int Length)> Guards(int modules) => modules switch
    {
        95 => [(0, 3), (45, 5), (92, 3)],
        67 => [(0, 3), (31, 5), (64, 3)],
        51 => [(0, 3), (45, 6)],
        _ => [],
    };

    [Theory]
    [InlineData(BarcodeSymbology.Ean13, "4006381333931", 95)]
    [InlineData(BarcodeSymbology.UpcA, "036000291452", 95)]
    [InlineData(BarcodeSymbology.Ean8, "96385074", 67)]
    [InlineData(BarcodeSymbology.UpcE, "01234565", 51)]
    public void 护栏区间与ZXing模块序列一致(BarcodeSymbology symbology, string content, int modules)
    {
        var pattern = BarcodeRenderer.NewWriter(symbology).encode(content);
        pattern.Should().HaveCount(modules, "模块总数决定护栏下标，实测 ZXing 的编码长度");

        var guards = Guards(modules);
        guards[0].Should().Be((0, 3));
        guards[^1].Start.Should().Be(modules - guards[^1].Length, "终止护栏紧贴最后一个模块");

        Bits(pattern, 0, 3).Should().Be("101", "起始护栏实测为 101");
        Bits(pattern, guards[^1].Start, guards[^1].Length)
            .Should().Be(guards[^1].Length == 6 ? "010101" : "101", "终止护栏实测：EAN 系 101，UPC-E 的 6 模块 010101");

        // 中段/尾部护栏是 0/1 交替：EAN-13/EAN-8 的 5 模块中段实测 01010，UPC-E 的 6 模块终止护栏实测 010101
        var expectedMiddle = guards[1].Length == 6 ? "010101" : "01010";
        Bits(pattern, guards[1].Start, guards[1].Length).Should().Be(expectedMiddle);
    }

    [Theory]
    [InlineData(BarcodeSymbology.Ean13, "4006381333931")]
    [InlineData(BarcodeSymbology.Ean8, "96385074")]
    public void 护栏条比数据条长五个模块(BarcodeSymbology symbology, string content)
    {
        var pattern = BarcodeRenderer.NewWriter(symbology).encode(content);

        // 元素宽 = 模块数（每模块 1mm），高 20mm，关可读文本：条区 20mm、护栏增量 min(5mm, 25%)=5mm
        var element = new BarcodeElement
        {
            Id = "bc",
            XMm = 5,
            YMm = 5,
            WidthMm = pattern.Length,
            HeightMm = 20,
            Barcode = new BarcodeOptions
            {
                Content = content,
                Symbology = symbology,
                ShowText = false,
                QuietZoneMm = 0,
            },
        };

        using var bitmap = Fixture.RenderElement(element, dpi: 254, pageWidthMm: pattern.Length + 10, pageHeightMm: 40);

        var guards = Guards(pattern.Length);
        bool InGuard(int i) => guards.Any(g => i >= g.Start && i < g.Start + g.Length);
        var guardModule = Enumerable.Range(0, pattern.Length).First(i => pattern[i] && InGuard(i));
        var dataModule = Enumerable.Range(0, pattern.Length).First(i => pattern[i] && !InGuard(i));

        var guard = Column(element, bitmap, guardModule);
        var data = Column(element, bitmap, dataModule);

        guard.StartMm.Should().BeApproximately(5, 0.3, "上下沿对齐，条从元素顶部开始");
        data.StartMm.Should().BeApproximately(5, 0.3);
        guard.LengthMm.Should().BeApproximately(20, 0.3, "护栏占满条区");
        data.LengthMm.Should().BeApproximately(15, 0.3, "数据条比护栏短 5 个模块（1mm/模块）");
    }

    /// <summary>正对照：没有护栏的码制所有条同高——证明探针能区分"长条/短条"，不是恒绿。</summary>
    [Fact]
    public void 无护栏的码制所有条同高()
    {
        const string content = "ABC-123";
        var pattern = BarcodeRenderer.NewWriter(BarcodeSymbology.Code128).encode(content);
        Guards(pattern.Length).Should().BeEmpty("Code128 没有独立护栏区间");

        var element = new BarcodeElement
        {
            Id = "bc",
            XMm = 5,
            YMm = 5,
            WidthMm = pattern.Length,
            HeightMm = 20,
            Barcode = new BarcodeOptions
            {
                Content = content,
                Symbology = BarcodeSymbology.Code128,
                ShowText = false,
                QuietZoneMm = 0,
            },
        };

        using var bitmap = Fixture.RenderElement(element, dpi: 254, pageWidthMm: pattern.Length + 10, pageHeightMm: 40);
        var first = Enumerable.Range(0, pattern.Length).First(i => pattern[i]);
        var second = Enumerable.Range(first + 1, pattern.Length - first - 1).First(i => pattern[i]);

        Column(element, bitmap, first).LengthMm.Should().BeApproximately(20, 0.3);
        Column(element, bitmap, second).LengthMm.Should().BeApproximately(20, 0.3);
    }

    static (double StartMm, double LengthMm) Column(BarcodeElement element, SkiaSharp.SKBitmap bitmap, int module)
    {
        // 每模块 1mm，取模块中心列（避开相邻模块边界）
        var xPx = Ink.MmToPx(element.XMm + module + 0.5, 254);
        var run = Ink.DarkRunsInColumn(bitmap, 254, xPx).OrderByDescending(r => r.LengthMm).FirstOrDefault();
        return (run.StartMm, run.LengthMm);
    }

    static string Bits(bool[] pattern, int start, int count)
        => string.Concat(Enumerable.Range(start, count).Select(i => pattern[i] ? '1' : '0'));
}
