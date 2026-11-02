using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.OneD;
using ZXing.QrCode;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 码制渲染器取证。三档主张，从弱到强：
/// ① 能被 ZXing 自己的读码器解回原内容（"可扫描"的最低实证）；
/// ② 逐模块比对：栅格化的结果按自然网格采样，必须与 <c>BitMatrix</c> 完全一致（证明没有重采样/错位）；
/// ③ 条宽严格等于模块序列的整数倍（证明没被 ZXing 位图的 uneven padding 污染）。
/// 期望值全部来自探针实测（§17/§18/§19/§20），不是推算。
/// </summary>
public class CodeRendererTests
{
    [Fact]
    public void 二维码渲染后能被读码器解回原内容()
    {
        const string content = "https://crossprint.dev/CP-2026";
        var element = Qr(content, QrErrorCorrection.M, 40, 2);

        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 60, pageHeightMm: 60);
        using (bitmap)
        {
            DecodeQr(bitmap).Text.Should().Be(content, "渲染出的二维码必须是真能扫的，不是像二维码的方块");
        }
    }

    [Fact]
    public void 中文内容按UTF8编码后仍能解回()
    {
        const string content = "面单号:CP-0001";
        var element = Qr(content, QrErrorCorrection.M, 45, 2);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 60, pageHeightMm: 60, report: report);
        using (bitmap)
        {
            report.Issues.Should().BeEmpty();
            DecodeQr(bitmap).Text.Should().Be(content, "跨水面单的内容就是中文，UTF-8 hint 必须真的进到符号里");
        }
    }

    [Theory]
    [InlineData(QrErrorCorrection.L, "L")]
    [InlineData(QrErrorCorrection.M, "M")]
    [InlineData(QrErrorCorrection.Q, "Q")]
    [InlineData(QrErrorCorrection.H, "H")]
    public void 纠错等级真正写进符号(QrErrorCorrection level, string expected)
    {
        // 探针 §20：解码元数据 ERROR_CORRECTION_LEVEL 是字符串 "L"/"M"/"Q"/"H"
        var element = Qr("CP", level, 30, 1);
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 50, pageHeightMm: 50);
        using (bitmap)
        {
            DecodeQr(bitmap).EcLevel.Should().Be(expected, "枚举映射写错不会让渲染失败，只有解码器能揭穿");
        }
    }

    [Fact]
    public void 栅格逐模块等于ZXing的自然网格()
    {
        const string content = "CP-PROBE-2026";
        const double dpi = 300;
        var element = Qr(content, QrErrorCorrection.H, 40, 2);

        var grid = new QRCodeWriter().encode(
            content,
            BarcodeFormat.QR_CODE,
            1,
            1,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 0,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
            });

        var bitmap = Fixture.RenderElement(element, dpi, pageWidthMm: 60, pageHeightMm: 60);
        using (bitmap)
        {
            var mask = Ink.DarkMask(bitmap);
            var box = Ink.BoundingBoxInMm(bitmap, dpi, new SKRect(0, 0, 60, 60))!.Value;

            // 40mm 元素减两侧 2mm 静区：图案应正好占满 36mm
            box.Left.Should().BeApproximately(7, 0.35);
            box.Width.Should().BeApproximately(36, 0.35);

            var moduleMm = box.Width / grid.Width;
            var mismatches = new List<string>();
            for (var y = 0; y < grid.Height && mismatches.Count < 5; y++)
            {
                for (var x = 0; x < grid.Width && mismatches.Count < 5; x++)
                {
                    // 取模块中心采样，离边界最远，抗锯齿不会干扰判定
                    var px = Ink.MmToPx(box.Left + (x + 0.5) * moduleMm, dpi);
                    var py = Ink.MmToPx(box.Top + (y + 0.5) * moduleMm, dpi);
                    if (mask[px, py] == grid[x, y])
                        continue;

                    mismatches.Add($"模块({x},{y}) 期望 {(grid[x, y] ? "黑" : "白")}，实际 {(mask[px, py] ? "黑" : "白")}");
                }
            }

            mismatches.Should().BeEmpty("逐模块一致才证明我们画的是矢量网格，不是把位图缩过来：" + string.Join("; ", mismatches));
        }
    }

    [Fact]
    public void 静区是真正的留白()
    {
        var element = Qr("A", QrErrorCorrection.M, 40, 3);
        element.QrCode.BackgroundColor = null;
        element.XMm = 10;
        element.YMm = 10;

        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 80, pageHeightMm: 80);
        using (bitmap)
        {
            var box = Ink.BoundingBoxInMm(bitmap, 300, new SKRect(0, 0, 80, 80))!.Value;

            // 21 模块 × (40-6)/21 mm，加 3mm 静区后正好铺满 40mm，所以图案左沿落在 XMm+3
            box.Left.Should().BeApproximately(13, 0.35, "扫描枪靠静区判定符号起止，留白必须是空的");
            box.Top.Should().BeApproximately(13, 0.35);
            Ink.DarkPixelCountInMm(bitmap, 300, new SKRect(10, 10, 12.9f, 40)).Should().Be(0, "静区内不能有墨");
        }
    }

    [Fact]
    public void 内容超容量时降级为告警而不是抛异常()
    {
        var element = Qr(new string('x', 5000), QrErrorCorrection.H, 30, 1);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 150, report: report);
        using (bitmap)
        {
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.QrCodeFailed);
            issue.ElementId.Should().Be("q");
            issue.Message.Should().Contain("无法编码");
            Ink.BoundingBoxInMm(bitmap, 150, new SKRect(0, 0, 100, 100)).Should().BeNull("降级后整页不该有墨");
        }
    }

    [Fact]
    public void 元素放不下时记二维码失败并给出原因()
    {
        // 探针 §20 之外另测：内容 "HELLO" 在 M 级是版本 1 = 21 模块
        var element = Qr("HELLO", QrErrorCorrection.M, 6, 3);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 150, report: report);
        using (bitmap)
        {
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.QrCodeFailed);
            issue.Message.Should().Contain("放不下").And.Contain("21");
            Ink.BoundingBoxInMm(bitmap, 150, new SKRect(0, 0, 100, 100)).Should().BeNull();
        }
    }

    [Fact]
    public void 一维码渲染后能被读码器解回原内容()
    {
        var element = Barcode("ABC-123", BarcodeSymbology.Code128, 5, 10, 90, 20, quietMm: 5, showText: false);

        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 100, pageHeightMm: 50);
        using (bitmap)
        {
            Decode1D(bitmap).Should().Be("ABC-123");
        }
    }

    /// <summary>
    /// hiprint 的每种条码类型都必须真的能扫。期望文本是 ZXing 解码器对同一内容的实测回读值
    /// （EAN/UPC 会带上校验位，Codabar 会带回起止符），不是照抄输入。
    /// </summary>
    [Theory]
    [InlineData(BarcodeSymbology.Code39, "ABC123", "ABC123")]
    [InlineData(BarcodeSymbology.Code93, "ABC123", "ABC123")]
    [InlineData(BarcodeSymbology.Codabar, "A123456B", "123456")]
    [InlineData(BarcodeSymbology.Itf, "123456", "123456")]
    [InlineData(BarcodeSymbology.Ean13, "5901234123457", "5901234123457")]
    [InlineData(BarcodeSymbology.Ean8, "96385074", "96385074")]
    [InlineData(BarcodeSymbology.UpcA, "036000291452", "036000291452")]
    [InlineData(BarcodeSymbology.UpcE, "01234565", "01234565")]
    public void 每种一维码制渲染后都能被读回(BarcodeSymbology symbology, string content, string expected)
    {
        var element = Barcode(content, symbology, 5, 10, 90, 25, quietMm: 6, showText: false);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 100, pageHeightMm: 55, report: report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Decode1D(bitmap).Should().Be(expected);
        }
    }

    [Fact]
    public void MSI码渲染后能被MSI读码器解回()
    {
        // MultiFormatReader 不注册 MSI（ZXing 的多格式一维读码表里没有它），只能直接调 MSIReader.decodeRow。
        var element = Barcode("1234567", BarcodeSymbology.Msi, 5, 10, 90, 25, quietMm: 6, showText: false);

        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 100, pageHeightMm: 55);
        using (bitmap)
        {
            var y = Ink.MmToPx(22, 300);
            var result = new MSIReader().decodeRow(y, RowAt(bitmap, y), new Dictionary<DecodeHintType, object>());
            result.Should().NotBeNull("MSI 必须能被 MSIReader 读回");
            result.Text.Should().Be("1234567");
        }
    }

    [Fact]
    public void 条宽严格等于模块序列的整数倍()
    {
        const string content = "ABC-123";
        const double widthMm = 100;
        const double quietMm = 0;
        const double dpi = 300;

        // 探针 §18 实测 Code128 "ABC-123" = 112 模块。若上游改了编码，这条必须先把差异暴露出来。
        var pattern = new Code128Writer().encode(content);
        pattern.Length.Should().Be(112);

        var moduleMm = (widthMm - 2 * quietMm) / pattern.Length;
        var expected = DarkRuns(pattern).Select(r => r.Length * moduleMm).ToList();

        var element = Barcode(content, BarcodeSymbology.Code128, 5, 10, widthMm, 15, quietMm: quietMm, showText: false);

        // 页面要装得下 5..105，否则元素被页边切掉，行程数不全
        var bitmap = Fixture.RenderElement(element, dpi, pageWidthMm: 120, pageHeightMm: 40);
        using (bitmap)
        {
            var runs = Ink.DarkRunsOnRow(bitmap, dpi, Ink.MmToPx(15, dpi));

            runs.Should().HaveCount(expected.Count, "黑条段数必须等于模块序列的黑段数");
            runs.Select((r, i) => new
            {
                Index = i,
                StartError = Math.Abs(r.StartMm - (5 + quietMm + DarkRuns(pattern)[i].Start * moduleMm)),
                LengthError = Math.Abs(r.LengthMm - expected[i]),
            }).Where(x => x.StartError > 0.25 || x.LengthError > 0.25)
              .Should().BeEmpty("每段的起点与宽度都必须落在模块网格上（容差 0.25mm）");
        }
    }

    [Fact]
    public void 静区把整组条往里推()
    {
        const string content = "ABC-123";
        const double widthMm = 80;
        const double quietMm = 6;
        const double dpi = 300;

        var pattern = new Code128Writer().encode(content);
        var moduleMm = (widthMm - 2 * quietMm) / pattern.Length;
        var firstDark = Array.FindIndex(pattern, b => b);
        var lastDark = Array.FindLastIndex(pattern, b => b);

        var element = Barcode(content, BarcodeSymbology.Code128, 10, 10, widthMm, 15, quietMm: quietMm, showText: false);
        var bitmap = Fixture.RenderElement(element, dpi, pageWidthMm: 120, pageHeightMm: 40);
        using (bitmap)
        {
            var box = Ink.BoundingBoxInMm(bitmap, dpi, new SKRect(0, 0, 120, 40))!.Value;

            // Code128 的停止字符尾部是白模块，所以条组右沿不等于元素右沿
            box.Left.Should().BeApproximately(10 + quietMm + firstDark * moduleMm, 0.35);
            box.Right.Should().BeApproximately(10 + quietMm + (lastDark + 1) * moduleMm, 0.35);
            Ink.DarkPixelCountInMm(bitmap, dpi, new SKRect(10, 10, 15.8f, 25)).Should().Be(0, "静区内不能有黑条");
        }
    }

    [Fact]
    public void 非法内容按码制校验失败并降级()
    {
        var element = Barcode("中文", BarcodeSymbology.Ean13, 5, 5, 60, 15);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 150, report: report);
        using (bitmap)
        {
            // 探针 §18：EAN13 对非数字内容抛 ArgumentException，消息里带请求长度
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.BarcodeFailed);
            issue.Message.Should().Contain("Ean13").And.Contain("中文");
            Ink.BoundingBoxInMm(bitmap, 150, new SKRect(0, 0, 100, 100)).Should().BeNull();
        }
    }

    [Fact]
    public void 条码绑定为空时画占位框而不是一截黑砖()
    {
        // 用户预览里"几块巨大黑块"的根因：Code128 encode("") 会返回一段 24 模块的合法图案，
        // 拉到 90mm 宽就是几块黑砖。这条把"空内容必须走占位框"钉死。
        var element = Barcode("{{barcode}}", BarcodeSymbology.Code128, 5, 5, 90, 20, quietMm: 0, showText: false);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 100, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.BarcodeFailed);
            issue.Message.Should().Contain("内容为空").And.Contain("{{barcode}}").And.Contain("占位框");

            var window = new SKRect(5, 5, 95, 25);
            Ink.DarkPixelCountInMm(bitmap, 300, window).Should().Be(0,
                "旧实现会把空内容的 24 模块图案画成黑砖；占位框只有浅灰虚线和中灰提示文字");
            Ink.NonWhitePixelCountInMm(bitmap, 300, window).Should().BeGreaterThan(0,
                "占位框必须真的可见，否则用户只看到一片空白，还是不知道缺数据");
        }
    }

    [Fact]
    public void 二维码内容为空时画占位框而不是留白()
    {
        var element = Qr(string.Empty, QrErrorCorrection.M, 30, 1);

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 50, pageHeightMm: 50, report: report);
        using (bitmap)
        {
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.QrCodeFailed);
            issue.Message.Should().Contain("内容为空").And.Contain("占位框");

            var window = new SKRect(5, 5, 35, 35);
            Ink.DarkPixelCountInMm(bitmap, 300, window).Should().Be(0, "占位框不能画成黑块");
            Ink.NonWhitePixelCountInMm(bitmap, 300, window).Should().BeGreaterThan(0,
                "旧实现对空内容直接 return，页面上什么都没有；占位框要有可见的浅灰提示");
        }
    }

    [Fact]
    public void 可读文本从条高里扣出一行()
    {
        var withText = Barcode("ABC-123", BarcodeSymbology.Code128, 10, 10, 60, 24, quietMm: 3, showText: true);
        withText.Barcode.FontSizePt = 8;
        var withoutText = Barcode("ABC-123", BarcodeSymbology.Code128, 10, 10, 60, 24, quietMm: 3, showText: false);

        var a = Fixture.RenderElement(withText, 300, pageWidthMm: 80, pageHeightMm: 50);
        var b = Fixture.RenderElement(withoutText, 300, pageWidthMm: 80, pageHeightMm: 50);
        using (a)
        using (b)
        {
            // 不能拿整页墨迹包围盒判定：带文本那张的墨迹下沿是**文本**的下沿，不是条的下沿。
            // 竖切第一根条（x=13.5mm 落在首条 2 模块宽里，居中文本从 ~32.5mm 才开始，不干扰这列）。
            var colA = Ink.DarkRunsInColumn(a, 300, Ink.MmToPx(13.5, 300));
            var colB = Ink.DarkRunsInColumn(b, 300, Ink.MmToPx(13.5, 300));
            colA.Should().ContainSingle("这一列上条是连续一段，文本不在其上");
            colB.Should().ContainSingle();

            colB[0].StartMm.Should().BeApproximately(10, 0.4);
            colB[0].LengthMm.Should().BeApproximately(24, 0.5, "无文本时条占满元素高");

            var textLineMm = UnitConverter.PtToMm(8) * 1.2;
            colA[0].LengthMm.Should().BeApproximately(24 - textLineMm, 0.5, $"可读文本要从条高里扣出一行行高（{textLineMm:0.##}mm）");

            // 条结束之后的带里仍有墨——那只能是文本
            Ink.DarkPixelCountInMm(a, 300, new SKRect(11, 30.9f, 69, 34)).Should().BeGreaterThan(0, "可读文本必须真的画出来");

            // 中部扫描线两者段数一致：文本模式没有改动条宽布局
            var row = Ink.MmToPx(20, 300);
            Ink.DarkRunsOnRow(a, 300, row).Should().HaveCount(Ink.DarkRunsOnRow(b, 300, row).Count);
        }
    }

    [Fact]
    public void 反相配色下底色铺满整个元素框()
    {
        var element = Barcode("REV-1", BarcodeSymbology.Code128, 5, 10, 80, 18, quietMm: 4, showText: false);
        element.Barcode.BarColor = SKColors.White;
        element.Barcode.BackgroundColor = SKColors.Black;

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, 300, pageWidthMm: 100, pageHeightMm: 40, report: report);
        using (bitmap)
        {
            report.Issues.Should().BeEmpty();
            var box = Ink.BoundingBoxInMm(bitmap, 300, new SKRect(0, 0, 100, 40))!.Value;
            box.Left.Should().BeApproximately(5, 0.35);
            box.Width.Should().BeApproximately(80, 0.6);
            box.Height.Should().BeApproximately(18, 0.6);
        }
    }

    static QrCodeElement Qr(string content, QrErrorCorrection level, double sideMm, double quietMm)
        => new()
        {
            Id = "q",
            XMm = 5,
            YMm = 5,
            WidthMm = sideMm,
            HeightMm = sideMm,
            QrCode = new QrCodeOptions { Content = content, ErrorCorrection = level, QuietZoneMm = quietMm },
        };

    static BarcodeElement Barcode(string content, BarcodeSymbology symbology, double x, double y, double w, double h, double quietMm = 2.5, bool showText = true)
        => new()
        {
            Id = "b",
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Barcode = new BarcodeOptions { Content = content, Symbology = symbology, QuietZoneMm = quietMm, ShowText = showText },
        };

    readonly record struct Run(int Start, int Length);

    /// <summary>模块序列里的黑段（起始下标 + 模块数）。</summary>
    static List<Run> DarkRuns(bool[] pattern)
    {
        var runs = new List<Run>();
        var start = -1;
        for (var i = 0; i <= pattern.Length; i++)
        {
            var dark = i < pattern.Length && pattern[i];
            if (dark)
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                runs.Add(new Run(start, i - start));
                start = -1;
            }
        }

        return runs;
    }

    record Decoded(string? Text, string? EcLevel);

    /// <summary>
    /// 把位图交给 ZXing 自己的读码器反解。
    /// 实测（探针 §19）：1 字节/像素的亮度缓冲配 Gray8 口径可用，
    /// 3 参数 RGB 构造在同样数据下抛 NullReferenceException，所以不走那条重载。
    /// </summary>
    static Decoded DecodeQr(SKBitmap bitmap)
    {
        // 探针 §21：本版本的 QRCodeReader 读不到时**返回 null 而不是抛 NotFoundException**，
        // 不先判空就会拿着一句没信息量的 NullReferenceException 去找渲染 bug。
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(LumaSource(bitmap))));
        result.Should().NotBeNull("渲染结果必须能被 ZXing 自己的读码器定位并解出");

        string? ec = null;
        if (result.ResultMetadata is not null &&
            result.ResultMetadata.TryGetValue(ResultMetadataType.ERROR_CORRECTION_LEVEL, out var v))
        {
            ec = v?.ToString();
        }

        return new Decoded(result.Text, ec);
    }

    static string? Decode1D(SKBitmap bitmap)
    {
        // 探针 §16：Code128Reader 本身只暴露 decodeRow，跨平台的入口是 MultiFormatReader
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true };
        var result = new MultiFormatReader()
            .decode(new BinaryBitmap(new HybridBinarizer(LumaSource(bitmap))), hints);
        result.Should().NotBeNull("一维码必须能被读回，判空理由同 DecodeQr（探针 §21）");
        return result.Text;
    }

    static RGBLuminanceSource LumaSource(SKBitmap bitmap)
        => new(Luma(bitmap), bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.Gray8);

    /// <summary>把一行像素二值化成 ZXing 的 BitArray（黑=true）。</summary>
    static BitArray RowAt(SKBitmap bitmap, int y)
    {
        var row = new BitArray(bitmap.Width);
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            row[x] = (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000 < 128;
        }

        return row;
    }

    static byte[] Luma(SKBitmap bitmap)
    {
        var luma = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                luma[y * bitmap.Width + x] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            }
        }

        return luma;
    }
}
