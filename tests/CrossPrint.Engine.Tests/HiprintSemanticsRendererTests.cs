using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// hiprint 语义落到渲染层的取证。每条用例都配一个"机制关掉"的对照（旋转 0°、只画上边 vs 四边、
/// 内边距 0、字间距 0、省略号关、左对齐、标题关、圆角 0、显式行源清空、汇总 None、无多行表头），
/// 两边必须给出不同像素——否则断言只是在测默认行为，钉不住新机制。
/// </summary>
public class HiprintSemanticsRendererTests
{
    const double Dpi = 300;

    [Fact]
    public void 旋转九十度把墨迹包围盒宽高互换且中心不动()
    {
        var element = new RectangleElement
        {
            Id = "r",
            XMm = 20,
            YMm = 30,
            WidthMm = 40,
            HeightMm = 10,
            Rectangle = new ShapeOptions { FillColor = SKColors.Black },
        };

        using var plain = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 100);
        var before = Ink.BoundingBoxMm(plain, Dpi)!.Value;

        element.RotationDegrees = 90;
        using var rotated = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 100);
        var after = Ink.BoundingBoxMm(rotated, Dpi)!.Value;

        before.Width.Should().BeApproximately(40, 0.7, "不旋转时就是 40mm 宽");
        before.Height.Should().BeApproximately(10, 0.7);

        after.Width.Should().BeApproximately(10, 0.7, "旋转 90° 后宽度应换成原高度");
        after.Height.Should().BeApproximately(40, 0.7, "旋转 90° 后高度应换成原宽度");
        (after.Left + after.Right).Should().BeApproximately(before.Left + before.Right, 1.0, "绕中心旋转，横向中心不变");
        (after.Top + after.Bottom).Should().BeApproximately(before.Top + before.Bottom, 1.0, "绕中心旋转，纵向中心不变");
    }

    [Fact]
    public void 只选上边时其余三边没有一像素边框()
    {
        var element = Box(BorderSide.Top);

        using var top = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 100);
        Ink.DarkPixelCountInMm(top, Dpi, new SKRect(12, 9, 38, 11.4f)).Should().BeGreaterThan(0, "上边必须画出来");
        Ink.DarkPixelCountInMm(top, Dpi, new SKRect(12, 28.6f, 38, 30.6f)).Should().Be(0, "下边窗口必须完全留白");
        Ink.DarkPixelCountInMm(top, Dpi, new SKRect(9, 13, 11.4f, 27)).Should().Be(0, "左边窗口必须完全留白");

        element.BorderSides = BorderSide.All;
        using var all = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 100);
        Ink.DarkPixelCountInMm(all, Dpi, new SKRect(12, 28.6f, 38, 30.6f)).Should().BeGreaterThan(0, "四边全画时下边必须出现");
        Ink.DarkPixelCountInMm(all, Dpi, new SKRect(9, 13, 11.4f, 27)).Should().BeGreaterThan(0, "四边全画时左边必须出现");
    }

    [Fact]
    public void 内边距把文字整体推进内容盒()
    {
        var element = Text("XM", 10, 10, 60, 20);

        using var plain = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var before = Ink.BoundingBoxMm(plain, Dpi)!.Value;

        element.Text.PaddingLeftMm = 10;
        element.Text.PaddingTopMm = 6;
        using var padded = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var after = Ink.BoundingBoxMm(padded, Dpi)!.Value;

        (after.Left - before.Left).Should().BeApproximately(10, 0.8, "左内边距 10mm 必须整体右移 10mm");
        (after.Top - before.Top).Should().BeApproximately(6, 0.8, "上内边距 6mm 必须整体下移 6mm");
        (after.Right - before.Right).Should().BeApproximately(10, 0.8, "右边界同样右移，文字本身不应被压缩");
    }

    [Fact]
    public void 字间距按字缝累加而不是给整串加尾巴()
    {
        var element = Text("ABCDEFGHIJ", 10, 10, 80, 20);
        element.Text.Wrap = false;

        using var normal = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var before = Ink.BoundingBoxMm(normal, Dpi)!.Value;

        element.Text.LetterSpacingPt = 3;
        using var spaced = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var after = Ink.BoundingBoxMm(spaced, Dpi)!.Value;

        // 3pt = 1.058mm，10 个字有 9 个字缝 → 约 9.5mm
        (after.Width - before.Width).Should().BeApproximately(9.5, 1.2, "10 个字之间只有 9 个字缝");
        after.Left.Should().BeApproximately(before.Left, 0.5, "字间距从第一个字之后开始，左端不动");
    }

    [Fact]
    public void 不换行加省略号时末字不再被元素边界切断()
    {
        var element = Text("WWWWWWWWWW", 10, 10, 5, 10);
        element.Text.FontSizePt = 12;
        element.Text.Wrap = false;

        using var clipped = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40);
        var clippedBox = Ink.BoundingBoxMm(clipped, Dpi)!.Value;
        clippedBox.Right.Should().BeGreaterThan(14.4, "关掉省略号时文字顶到元素右边被硬切");

        element.Text.Ellipsis = true;
        using var truncated = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 40);
        var truncatedBox = Ink.BoundingBoxMm(truncated, Dpi)!.Value;
        truncatedBox.Right.Should().BeLessThan(14.3, "截断后的串（含省略号）必须真正放得下，不能还贴着边");
        truncatedBox.Right.Should().BeGreaterThan(10.5, "省略号本身得画出来，不能整行消失");
    }

    [Fact]
    public void 两端对齐把非末行的右端推到内容盒右边()
    {
        var element = Text("AA BB CC DD\nEE", 10, 10, 80, 20);
        element.Text.HorizontalAlign = HorizontalAlign.Left;

        using var left = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var leftBox = Ink.BoundingBoxInMm(left, Dpi, new SKRect(10, 10, 90, 19))!.Value;

        element.Text.HorizontalAlign = HorizontalAlign.Justify;
        using var justify = Fixture.RenderElement(element, Dpi, pageWidthMm: 100, pageHeightMm: 60);
        var justifyBox = Ink.BoundingBoxInMm(justify, Dpi, new SKRect(10, 10, 90, 19))!.Value;

        leftBox.Right.Should().BeLessThan(50, "左对齐时短行只占内容盒左边一小截");
        justifyBox.Right.Should().BeGreaterThan(88, "两端对齐必须把字缝撑到右边");
        justifyBox.Left.Should().BeApproximately(10.2, 0.8, "两端对齐从左端起笔");
    }

    [Fact]
    public void 二维码标题占用码下方一行且不再染指码区()
    {
        var element = new QrCodeElement
        {
            Id = "qr",
            XMm = 10,
            YMm = 10,
            WidthMm = 30,
            HeightMm = 40,
            QrCode = new QrCodeOptions
            {
                Content = "CAPTION",
                QuietZoneMm = 1,
                Caption = "编号",
                CaptionFontSizePt = 10.5,
            },
        };

        using var noCaption = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 70);
        Ink.DarkPixelCountInMm(noCaption, Dpi, new SKRect(11, 46, 39, 49.6f)).Should().Be(0, "标题关掉时元素下半部应留白");
        Ink.DarkPixelCountInMm(noCaption, Dpi, new SKRect(11, 15, 39, 45)).Should().BeGreaterThan(0, "码本身照画");

        element.QrCode.ShowCaption = true;
        using var withCaption = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 70);
        Ink.DarkPixelCountInMm(withCaption, Dpi, new SKRect(11, 46, 39, 49.6f)).Should().BeGreaterThan(0, "标题必须画在码下方");
        Ink.DarkPixelCountInMm(withCaption, Dpi, new SKRect(11, 15, 39, 42)).Should().BeGreaterThan(0, "扣掉标题后码区仍在");
    }

    [Fact]
    public void 图片圆角把四角削成纸白而中心仍是图()
    {
        var element = new ImageElement
        {
            Id = "img",
            XMm = 10,
            YMm = 10,
            WidthMm = 30,
            HeightMm = 30,
            Image = new ImageOptions
            {
                SourceType = ImageSourceType.EmbeddedBase64,
                Data = Convert.ToBase64String(PngBytes(new SKColor(30, 120, 220), 60, 60)),
                FitMode = ImageFitMode.Fill,
            },
        };

        using var square = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 60);
        Ink.ColorCountInMm(square, Dpi, new SKRect(10.3f, 10.3f, 11.5f, 11.5f), new SKColor(30, 120, 220))
            .Should().BeGreaterThan(0, "圆角 0 时左上角必须被图占满");

        element.Image.CornerRadiusMm = 8;
        using var rounded = Fixture.RenderElement(element, Dpi, pageWidthMm: 60, pageHeightMm: 60);
        Ink.ColorCountInMm(rounded, Dpi, new SKRect(10.3f, 10.3f, 11.5f, 11.5f), new SKColor(30, 120, 220))
            .Should().Be(0, "圆角 8mm 时左上角必须被削掉");
        Ink.ColorCountInMm(rounded, Dpi, new SKRect(22, 22, 28, 28), new SKColor(30, 120, 220))
            .Should().BeGreaterThan(0, "中心仍应是图");
    }

    [Fact]
    public void 显式行源让首段不同的列也能列出数据行()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Field = "lines";
        element.Table.RowHeightMm = 10;
        element.Table.Columns[0].Field = "name";
        element.Table.Columns[1].Field = "amount";
        element.Table.Columns[2].Field = "name";

        var data = new { lines = new[] { new { name = "A", amount = 10 }, new { name = "B", amount = 20 } } };

        using var withField = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.InkBandCount(withField, 2).Should().Be(2, "显式行源给了 2 条 → 2 个文本行带");

        element.Table.Field = string.Empty;
        using var inferred = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.InkBandCount(inferred, 2).Should().Be(0, "列首段 name/amount 不一致，推断失败时宁可一行都不画");
    }

    [Fact]
    public void 序号列打印行号而不是空字段()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Field = "lines";
        element.Table.RowHeightMm = 10;
        element.Table.Columns[0].Sequence = true;
        element.Table.Columns[0].Field = string.Empty;
        element.Table.Columns[1].Field = "name";
        element.Table.Columns[2].Field = "name";

        var data = new { lines = new[] { new { name = "A" }, new { name = "B" } } };

        using var numbered = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.DarkPixelCountInMm(numbered, Dpi, new SKRect(6, 6, 28, 14)).Should().BeGreaterThan(0, "序号列第一格应有数字");
        Ink.DarkPixelCountInMm(numbered, Dpi, new SKRect(6, 16, 28, 24)).Should().BeGreaterThan(0, "序号列第二格应有数字");

        element.Table.Columns[0].Sequence = false;
        using var blank = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.DarkPixelCountInMm(blank, Dpi, new SKRect(6, 6, 28, 14)).Should().Be(0, "关掉序号且字段为空时第一列必须留白");
    }

    [Fact]
    public void 汇总行只在配置汇总方式后多出一行()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Field = "lines";
        element.Table.RowHeightMm = 10;
        element.Table.Columns[0].Field = "name";
        element.Table.Columns[1].Field = "amount";
        element.Table.Columns[2].Field = "name";

        var data = new { lines = new[] { new { name = "A", amount = 10.5 }, new { name = "B", amount = 2.25 } } };

        using var plain = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.InkBandCount(plain, 2).Should().Be(2, "没有汇总时只有两条数据行");

        element.Table.Columns[1].Summary = TableSummary.Sum;
        element.Table.Columns[1].SummaryLabel = "合计：";
        element.Table.Columns[1].SummaryDecimals = 1;

        using var summarized = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60, data: data);
        Ink.InkBandCount(summarized, 2).Should().Be(3, "汇总行追加在数据行之后");
        Ink.DarkPixelCountInMm(summarized, Dpi, new SKRect(6, 26, 112, 34)).Should().BeGreaterThan(0, "汇总行必须落在第三行带");
    }

    [Fact]
    public void 多行表头的合并单元格内不画竖线()
    {
        var element = Table(showHeader: true, grid: true);
        element.Table.RowHeightMm = 8;
        element.Table.HeaderRowHeightMm = 8;
        element.Table.HeaderFillColor = null;
        element.Table.ShowBorder = false;

        // 标题留空：这一条只量网格线，字形笔画会污染竖线窗口。
        element.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 0, Title = string.Empty, ColSpan = 2 });
        element.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 2, Title = string.Empty, RowSpan = 2 });
        element.Table.HeaderRows.Add(new TableHeaderCell { Row = 1, Column = 0, Title = "A" });
        element.Table.HeaderRows.Add(new TableHeaderCell { Row = 1, Column = 1, Title = "B" });

        using var merged = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60);
        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(34.7f, 6, 35.3f, 12))
            .Should().Be(0, "第一表头行被 colspan 合并，这一段的列线必须缺席");
        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(34.7f, 14, 35.3f, 20))
            .Should().BeGreaterThan(0, "第二表头行恢复了列边界，列线必须出现");

        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(64.7f, 6, 65.3f, 12))
            .Should().BeGreaterThan(0, "rowspan 单元格的左边界仍是真边界");
        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(64.7f, 14, 65.3f, 20))
            .Should().BeGreaterThan(0, "列线贯穿两行表头");

        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(66, 12.7f, 104, 13.3f))
            .Should().Be(0, "rowspan 单元格内部不能被横向行线穿过");
        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(6, 12.7f, 30, 13.3f))
            .Should().BeGreaterThan(0, "未合并的列仍需画出第二表头行的分隔线");
        Ink.DarkPixelCountInMm(merged, Dpi, new SKRect(40, 12.7f, 60, 13.3f))
            .Should().BeGreaterThan(0, "colspan 只合并了第一行，第二行的列分隔线不能消失");

        element.Table.HeaderRows.Clear();
        using var single = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 60);
        Ink.DarkPixelCountInMm(single, Dpi, new SKRect(34.7f, 6, 35.3f, 12))
            .Should().BeGreaterThan(0, "退回单行表头后列线在表头第一行就出现");
    }

    static TextElement Text(string content, double x, double y, double w, double h)
        => new()
        {
            Id = "t",
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Text = new TextOptions
            {
                Content = content,
                FontFamily = "Microsoft YaHei",
                FontSizePt = 12,
                VerticalAlign = VerticalAlign.Top,
            },
        };

    static RectangleElement Box(BorderSide sides)
        => new()
        {
            Id = "b",
            XMm = 10,
            YMm = 10,
            WidthMm = 30,
            HeightMm = 20,
            BorderColor = SKColors.Black,
            BorderWidthMm = 0.6,
            BorderSides = sides,
            Rectangle = new ShapeOptions { StrokeColor = null },
        };

    static TableElement Table(bool showHeader, bool grid)
        => new()
        {
            Id = "tbl",
            XMm = 5,
            YMm = 5,
            WidthMm = 100,
            HeightMm = 50,
            Table = new TableOptions
            {
                ShowHeader = showHeader,
                GridColor = grid ? SKColors.Black : null,
                GridWidthMm = 0.5,
                FontSizePt = 9,
                RowHeightMm = -1,
                CellPaddingMm = 1,
                HeaderFillColor = null,
                StripeColor = null,
                ShowBorder = grid,
                Columns =
                {
                    new TableColumn { Title = "一", WidthMm = 30 },
                    new TableColumn { Title = "二", WidthMm = 30 },
                    new TableColumn { Title = "三", WidthMm = 40 },
                },
            },
        };

    static byte[] PngBytes(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
