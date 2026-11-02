using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 表格渲染取证。表格不对外暴露布局结果，所以一切从像素反推：
/// 横线位置用垂直扫描线量（列宽、行高），行数用行带数量，越界用邻列窗口内墨迹必须为零。
/// </summary>
public class TableRendererTests
{
    const double Dpi = 300;
    const double Xm = 5;
    const double Ym = 5;

    static readonly object Data = new
    {
        orderNo = "CP-2026",
        items = new[] { new { sku = "SKU-A", qty = 2 }, new { sku = "SKU-BB", qty = 17 } },
        parcels = new[] { new { name = "P1" } },
    };

    [Fact]
    public void 列宽由显式毫米与均分剩余共同决定()
    {
        // 元素 100mm：显式 30mm + 两个 -1 均分剩余 70 → 分界应落在 5 / 35 / 70 / 105
        var element = Table(showHeader: false, grid: true);
        element.Table.Columns[1].WidthMm = -1;
        element.Table.Columns[2].WidthMm = -1;
        element.Table.Rows.Add(EmptyCells(3));

        var bitmap = Render(element);
        using (bitmap)
        {
            var centers = Ink.DarkRunsOnRow(bitmap, Dpi, Ink.MmToPx(10, Dpi))
                .Select(r => r.StartMm + r.LengthMm / 2)
                .ToList();

            var expected = new[] { 5d, 35d, 70d, 105d };
            centers.Should().HaveCount(4, "三条列分界线加最右侧边线");
            for (var i = 0; i < expected.Length; i++)
                centers[i].Should().BeApproximately(expected[i], 0.4, $"第 {i} 条竖线");
        }
    }

    [Fact]
    public void 固定行高时横线严格等距()
    {
        var element = Table(showHeader: false, grid: true);
        element.Table.RowHeightMm = 8;
        for (var i = 0; i < 3; i++)
            element.Table.Rows.Add(EmptyCells(3));

        var bitmap = Render(element);
        using (bitmap)
        {
            // x=52mm 落在第二列中间，避开了所有竖线，扫到的黑段即横线
            var tops = Ink.DarkRunsInColumn(bitmap, Dpi, Ink.MmToPx(52, Dpi))
                .Select(r => r.StartMm + r.LengthMm / 2)
                .ToList();

            tops.Should().HaveCount(4, "3 行 → 3 条行顶线 + 1 条底线");
            tops[0].Should().BeApproximately(Ym, 0.4);
            for (var i = 1; i < tops.Count; i++)
                (tops[i] - tops[i - 1]).Should().BeApproximately(8, 0.35, $"第 {i} 条横线的间距");

            tops[^1].Should().BeApproximately(Ym + 24, 0.4, "表格底边按行高算，不铺满元素高");
        }
    }

    [Fact]
    public void 数据行数超过列数时每一行都画得出文字()
    {
        // 回归：LayoutRows 曾把行号 r 与列数比较（r < rows[r].Count），行号 ≥ 列数的行整行换成空串，
        // 三列表格从第 4 行起"只见底色不见文字"。6 行数据必须每行都有墨迹。
        var element = BoundTable();
        element.Table.RowHeightMm = 8;

        var data = new
        {
            items = Enumerable.Range(1, 6).Select(i => new { sku = $"SKU-{i}", qty = i }).ToArray(),
        };

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, data: data);
        using (bitmap)
        {
            for (var r = 0; r < 6; r++)
            {
                var center = Ink.MmToPx(Ym + r * 8 + 4, Dpi);
                var hasInk = false;
                for (var dy = -3; dy <= 3 && !hasInk; dy++)
                {
                    for (var x = Ink.MmToPx(Xm + 1, Dpi); x < Ink.MmToPx(Xm + 99, Dpi); x += 2)
                    {
                        var c = bitmap.GetPixel(x, center + dy);
                        if (c.Red < 90 && c.Green < 90 && c.Blue < 90)
                        {
                            hasInk = true;
                            break;
                        }
                    }
                }

                hasInk.Should().BeTrue($"第 {r + 1} 行必须画出文字（行数多于列数时不能变成空白行）");
            }
        }
    }

    [Fact]
    public void 数据行由列字段的公共首段展开()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 10;

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.InkBandCount(bitmap, 2).Should().Be(2, "items 有两条 → 两个文本行带");

            element.Table.Rows.Insert(0, new List<string> { "静态行", "", "" });
            var withStatic = Render(element, new RenderReport());
            using (withStatic)
            {
                // 静态行在前、数据行在后，顺序即数组顺序
                Ink.InkBandCount(withStatic, 2).Should().Be(3);
            }
        }
    }

    [Fact]
    public void 首段不一致时不把两个集合硬配对()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.RowHeightMm = 10;
        element.Table.Columns[0].Field = "items.sku";
        element.Table.Columns[1].Field = "parcels.name";
        element.Table.Rows.Add(new List<string> { "只有静态行", "", "" });

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.InkBandCount(bitmap, 2).Should().Be(1, "两列首段不同 → 只画静态行，宁可少画也不印出一张看着没错的错单");
        }
    }

    [Fact]
    public void 静态行里的占位符同样参与绑定()
    {
        var bound = SingleColumnTable("单号{{orderNo}}");
        var bitmap = Render(bound);
        using (bitmap)
        {
            var boundWidth = Ink.BoundingBoxMm(bitmap, Dpi)!.Value.Width;

            // 同一位置放 4 个汉字（明显短于 "单号CP-2026"），用来证明占位符真的被替换成长文本
            var literal = SingleColumnTable("单号单号");
            var literalBitmap = Render(literal);
            using (literalBitmap)
            {
                var literalWidth = Ink.BoundingBoxMm(literalBitmap, Dpi)!.Value.Width;
                boundWidth.Should().BeGreaterThan(literalWidth + 3, "替换后的文本更宽，说明绑定生效而不是原样印了花括号");
            }
        }
    }

    [Fact]
    public void 表格里的缺字段记绑定告警()
    {
        var element = SingleColumnTable("{{nothing.here}}");

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField && i.ElementId == "tbl");
            Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect(0, 0, 120, 80)).Should().BeNull("替换成空串后整页无墨");
        }
    }

    [Fact]
    public void 内容超出元素高记表格溢出并裁在元素内()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 8;
        element.HeightMm = 10;

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            var issue = report.Issues.Single(i => i.Kind == RenderIssueKind.TableOverflow);
            issue.Message.Should().Contain("行");
            Ink.BoundingBoxInMm(bitmap, Dpi, new SKRect((float)Xm, (float)Ym, 115, 80))!.Value.Bottom.Should().BeLessThanOrEqualTo(Ym + 10 + 0.4, "超出部分必须被元素框裁掉");
        }
    }

    [Fact]
    public void 超长内容只压垮自己那一列()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Columns[0].WidthMm = 30;
        element.Table.Columns[1].WidthMm = 70;
        element.Table.RowHeightMm = 10;
        element.Table.Rows.Add(new List<string> { new('A', 60), "", "" });

        var bitmap = Render(element);
        using (bitmap)
        {
            Ink.DarkPixelCountInMm(bitmap, Dpi, new SKRect(6, 6, 34, 14)).Should().BeGreaterThan(0, "本列必须画出内容");
            Ink.DarkPixelCountInMm(bitmap, Dpi, new SKRect(36, 6, 104, 14)).Should().Be(0, "邻列窗口内不能有一像素来自越界内容");
        }
    }

    [Fact]
    public void 表头底色只覆盖表头行()
    {
        var element = Table(showHeader: true, grid: false);
        element.Table.RowHeightMm = 10;
        element.Table.HeaderFillColor = new SKColor(225, 225, 225);
        foreach (var col in element.Table.Columns)
            col.Title = string.Empty;
        element.Table.Rows.Add(EmptyCells(3));

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(6, 6, 104, 14)).Should().BeGreaterThan(0, "表头行该有底色");
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(6, 16, 104, 24)).Should().Be(0, "数据行不该被表头底色染到");
        }
    }

    [Fact]
    public void 斑马纹只给偶数行上色()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.Table.StripeColor = new SKColor(230, 230, 230);
        // 单元格留空：字段换成不存在的路径，窗口里就只剩底色，没有文字干扰
        foreach (var col in element.Table.Columns)
            col.Field = "items.nothere";

        var bitmap = Render(element);
        using (bitmap)
        {
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(6, 6, 104, 14)).Should().Be(0, "第 1 行（索引 0）不上色");
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, new SKRect(6, 16, 104, 24)).Should().BeGreaterThan(0, "第 2 行该上色");
        }
    }

    [Fact]
    public void 测试数据按换行分行制表符分列()
    {
        // 列没有绑定字段 → 集合解析为 null → 测试数据行兜底：设计态没接数据也能看排版效果。
        var element = Table(showHeader: false, grid: false);
        element.Table.RowHeightMm = 10;
        element.TestData = "螺丝\t10\n垫片\t20";

        var report = new RenderReport();
        var bitmap = Render(element, report);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.InkBandCount(bitmap).Should().Be(2, "两行测试数据 → 两个文本行带");
        }
    }

    [Fact]
    public void 绑定到集合时测试数据不参与()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.TestData = "多余\t一行";

        var bitmap = Render(element);
        using (bitmap)
            Ink.InkBandCount(bitmap).Should().Be(2, "有真实数据行就不再叠测试数据行，测试数据只兜'没有数据'的场景");
    }

    [Fact]
    public void 静态行存在时打印不叠加测试数据行()
    {
        // 静态行（模板自带内容）已让表格非空：测试数据再叠上去只会撑爆高度、重复内容。
        // complex-receipt 的存根复核表（el-28）就是这个形态——有静态行也有测试数据。
        var element = Table(showHeader: false, grid: false);
        element.Table.RowHeightMm = 10;
        element.Table.Rows.Add(new List<string> { "出品完成", "是", "" });
        element.Table.Rows.Add(new List<string> { "器皿检查", "是", "" });
        element.TestData = "测试甲\t1\n测试乙\t2";

        var bitmap = Render(element);
        using (bitmap)
            Ink.InkBandCount(bitmap).Should().Be(2, "测试数据只兜'一行内容都没有'的场景，静态行已在时不叠加");
    }

    [Fact]
    public void 设计画布上测试数据盖过绑定的集合()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        // 绑定的集合有 2 条；用户在设计态输入了 3 行测试数据 → 画布上必须显示 3 行
        element.TestData = "测试行A\t1\n测试行B\t2\n测试行C\t3";

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, report: report, data: Data, preferTestData: true);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            Ink.InkBandCount(bitmap).Should().Be(3, "设计态显示的是用户输入的 3 行测试数据，不是绑定的 2 条数据行");
        }
    }

    [Fact]
    public void 行值超阈值整行上条件底色且后命中规则覆盖前者()
    {
        // 三行数据：55.5 只过灰档（>50，未过 60/90）；95.5 三档全过 → 最终金色（后者覆盖，不能叠成灰或蓝）；
        // 12.0 一档都不过 → 无底色。
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.Table.Columns[2].Field = "items.amount";
        element.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "50", BackgroundColor = new SKColor(217, 217, 217) });
        element.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "60", BackgroundColor = new SKColor(183, 211, 242) });
        element.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "90", BackgroundColor = new SKColor(242, 208, 107) });

        var data = new
        {
            items = new[]
            {
                new { sku = "A", qty = 1, amount = 55.5 },
                new { sku = "B", qty = 2, amount = 95.5 },
                new { sku = "C", qty = 3, amount = 12.0 },
            },
        };

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, report: report, data: data);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);

            // 行带：表格从 y=5mm 起，rowHeight 10mm → 行 0 占 5~15、行 1 占 15~25、行 2 占 25~35；
            // 取样窗口用每行顶部 0.5~2.5mm 的无文字带 + 数量列右对齐文字左侧的空白列段，只看底色。
            var gray = CountColorInMm(bitmap, new SKColor(217, 217, 217), 41, 5.5f, 58, 7.5f);
            gray.Should().BeGreaterThan(0, "55.5 > 50 → 第一行必须是灰色底");

            var gold = CountColorInMm(bitmap, new SKColor(242, 208, 107), 41, 15.5f, 58, 17.5f);
            gold.Should().BeGreaterThan(0, "95.5 > 90 → 第二行必须是金色底（规则覆盖语义）");
            CountColorInMm(bitmap, new SKColor(217, 217, 217), 41, 15.5f, 58, 17.5f).Should().Be(0, "金色行不能残留灰底——后命中规则必须覆盖先命中规则");

            var anyFill = CountColorInMm(bitmap, new SKColor(217, 217, 217), 41, 25.5f, 58, 27.5f)
                + CountColorInMm(bitmap, new SKColor(183, 211, 242), 41, 25.5f, 58, 27.5f)
                + CountColorInMm(bitmap, new SKColor(242, 208, 107), 41, 25.5f, 58, 27.5f);
            anyFill.Should().Be(0, "12.0 一档都不过 → 第三行无底色");
        }
    }

    [Fact]
    public void 未命中条件底色的行保持斑马纹()
    {
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.Table.StripeColor = new SKColor(230, 230, 230);
        element.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "50", BackgroundColor = new SKColor(242, 208, 107) });

        var data = new
        {
            items = new[]
            {
                new { sku = "A", qty = 1, amount = 5.0 },
                new { sku = "B", qty = 2, amount = 80.0 },
            },
        };

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, data: data);
        using (bitmap)
        {
            // 第一行（索引 0）：金额 5.0 未命中 → 保持无底色（斑马纹只给偶数行）
            CountColorInMm(bitmap, new SKColor(242, 208, 107), 41, 5.5f, 58, 7.5f).Should().Be(0, "未命中规则的第一行不能上规则色");
            CountColorInMm(bitmap, new SKColor(230, 230, 230), 41, 5.5f, 58, 7.5f).Should().Be(0, "斑马纹语义不变：第 1 行（索引 0）不上色");

            // 第二行（索引 1）：金额 80.0 命中规则色，且规则色替代斑马纹，两种底色不混叠
            CountColorInMm(bitmap, new SKColor(242, 208, 107), 41, 15.5f, 58, 17.5f).Should().BeGreaterThan(0, "80.0 > 50 → 第二行上规则色");
            CountColorInMm(bitmap, new SKColor(230, 230, 230), 41, 15.5f, 58, 17.5f).Should().Be(0, "命中规则的行由规则色替代斑马纹");
        }
    }

    [Fact]
    public void 条件样式限定列只染该格且可改字色()
    {
        // qty=17 命中 ≥10：第二行"数量"格上底色 + 红字；同行其他格与未命中行不受影响。
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.Table.ConditionalStyles.Add(new TableConditionalStyle
        {
            Field = "qty",
            Operator = TableConditionOperator.GreaterOrEqual,
            Value = "10",
            Column = "qty",
            BackgroundColor = new SKColor(255, 235, 156),
            TextColor = new SKColor(200, 30, 30),
        });

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, report: report, data: Data);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            CountColorInMm(bitmap, new SKColor(255, 235, 156), 36, 15.5f, 64, 17.5f).Should().BeGreaterThan(0, "qty=17 命中 ≥10 → 第二行数量格上底色");
            CountColorInMm(bitmap, new SKColor(255, 235, 156), 36, 5.5f, 64, 7.5f).Should().Be(0, "qty=2 未命中 → 第一行数量格不上底色");
            CountColorInMm(bitmap, new SKColor(255, 235, 156), 6, 15.5f, 34, 17.5f).Should().Be(0, "限定列规则只染该格，不染整行");

            // 命中格文字用条件字色渲染（数量列右对齐文字附近的行带里找红色核心像素）
            CountColorInMm(bitmap, new SKColor(200, 30, 30), 36, 15.5f, 64, 24.5f).Should().BeGreaterThan(0, "命中格文字用条件字色");
            CountColorInMm(bitmap, new SKColor(200, 30, 30), 36, 5.5f, 64, 14.5f).Should().Be(0, "未命中格保持默认黑字");
        }
    }

    [Fact]
    public void 条件样式改字号会撑高命中行()
    {
        // 自动行高下命中行用 16pt 折行测高：第二行行高必须明显大于 9pt 的第一行。
        var element = BoundGridTable();
        element.Table.ConditionalStyles.Add(new TableConditionalStyle
        {
            Field = "qty",
            Operator = TableConditionOperator.GreaterThan,
            Value = "10",
            FontSizePt = 16,
        });

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, data: Data);
        using (bitmap)
        {
            // 第三列右侧空白区（x=103mm）扫横线：[表顶, 行1底, 行2底]
            var tops = Ink.DarkRunsInColumn(bitmap, Dpi, Ink.MmToPx(103, Dpi))
                .Select(r => r.StartMm + r.LengthMm / 2)
                .ToList();

            tops.Should().HaveCount(3, "两行数据 → 表顶线 + 两条行底线");
            var row0 = tops[1] - tops[0];
            var row1 = tops[2] - tops[1];
            (row1 - row0).Should().BeGreaterThan(1.5, "命中行按 16pt 测行高，必须明显比 9pt 的第一行高");
        }
    }

    [Fact]
    public void 关键词命中片段上底色其余部分不受影响()
    {
        // "BB" 只出现在第二行（SKU-BB，名称与备注两列）：片段底色只落在片段盒内。
        var element = BoundTable();
        element.Table.RowHeightMm = 10;
        element.Table.Highlights.Add(new TableHighlightRule { Keyword = "BB", BackgroundColor = new SKColor(255, 235, 156) });

        var report = new RenderReport();
        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, report: report, data: Data);
        using (bitmap)
        {
            Fixture.NoDrawingFaults(report);
            // 行高 10mm、单行 9pt 文本垂直居中：片段底色盒约在 18.2–21.9mm，取 19–21 的带取证
            CountColorInMm(bitmap, new SKColor(255, 235, 156), 6, 19f, 104, 21f).Should().BeGreaterThan(0, "SKU-BB 中的 BB 片段要上底色");
            CountColorInMm(bitmap, new SKColor(255, 235, 156), 6, 9f, 104, 11f).Should().Be(0, "第一行没有 BB，不应出现片段底色");
        }
    }

    [Fact]
    public void 列同值合并把连续相同值的数据行并为一片()
    {
        var element = Table(showHeader: false, grid: true);
        element.Table.RowHeightMm = 8;
        element.Table.Columns[0].Field = "items.name";
        element.Table.Columns[1].Field = "items.cat";
        element.Table.Columns[1].MergeSameValue = true;
        element.Table.Columns[2].Field = "items.name";

        var data = new
        {
            items = new[]
            {
                new { name = "甲", cat = "B" },
                new { name = "乙", cat = "B" },
                new { name = "丙", cat = "A" },
            },
        };

        var bitmap = Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, data: data);
        using (bitmap)
        {
            // 第二列内部（x=63mm）扫横线：行 0/1 同值合并 → 13mm 处横线被抽掉；
            // 行 1/2 值不同 → 21mm 处横线保留。
            var tops = Ink.DarkRunsInColumn(bitmap, Dpi, Ink.MmToPx(63, Dpi))
                .Select(r => r.StartMm + r.LengthMm / 2)
                .ToList();

            tops.Should().NotContain(t => Math.Abs(t - 13) < 0.4, "同值合并的相邻两行之间不能有横线");
            tops.Should().Contain(t => Math.Abs(t - 21) < 0.45, "不同值的相邻行之间横线保留");
        }
    }

    [Fact]
    public void 静态行显式跨行合并抽掉区间内横线()
    {
        var element = Table(showHeader: false, grid: true);
        element.Table.RowHeightMm = 8;
        element.Table.Rows.Add(new List<string> { "甲", "汇总", "x" });
        element.Table.Rows.Add(new List<string> { "乙", "", "y" });
        element.Table.RowSpans.Add(new TableRowSpan { Row = 0, Column = 1, RowSpan = 2, ColSpan = 1 });

        var bitmap = Render(element);
        using (bitmap)
        {
            var tops = Ink.DarkRunsInColumn(bitmap, Dpi, Ink.MmToPx(63, Dpi))
                .Select(r => r.StartMm + r.LengthMm / 2)
                .ToList();

            tops.Should().NotContain(t => Math.Abs(t - 13) < 0.4, "RowSpan=2 的区间内不能有横线");
            tops.Should().Contain(t => Math.Abs(t - 21) < 0.45, "区间之外的横线保留");
        }
    }

    /// <summary>带网格线的三列绑定表（列宽/行高同 <see cref="Table"/>，字段同 <see cref="BoundTable"/>）。</summary>
    static TableElement BoundGridTable()
    {
        var element = Table(showHeader: false, grid: true);
        element.Table.Columns[0].Field = "items.sku";
        element.Table.Columns[1].Field = "items.qty";
        element.Table.Columns[2].Field = "items.sku";
        return element;
    }

    static int CountColorInMm(SKBitmap bitmap, SKColor color, double x0Mm, double y0Mm, double x1Mm, double y1Mm)
    {
        var count = 0;
        var x0 = Ink.MmToPx(x0Mm, Dpi);
        var y0 = Ink.MmToPx(y0Mm, Dpi);
        var x1 = Ink.MmToPx(x1Mm, Dpi);
        var y1 = Ink.MmToPx(y1Mm, Dpi);
        for (var y = y0; y < Math.Min(y1, bitmap.Height); y++)
        {
            for (var x = x0; x < Math.Min(x1, bitmap.Width); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - color.Red) <= 2 && Math.Abs(pixel.Green - color.Green) <= 2 && Math.Abs(pixel.Blue - color.Blue) <= 2)
                    count++;
            }
        }

        return count;
    }

    static List<string> EmptyCells(int count) => Enumerable.Repeat(string.Empty, count).ToList();

    static TableElement Table(bool showHeader, bool grid, double widthMm = 100)
    {
        var element = new TableElement
        {
            Id = "tbl",
            XMm = Xm,
            YMm = Ym,
            WidthMm = widthMm,
            HeightMm = 60,
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
            },
        };

        element.Table.Columns.Add(new TableColumn { Title = "名称", WidthMm = 30 });
        element.Table.Columns.Add(new TableColumn { Title = "数量", WidthMm = 30 });
        element.Table.Columns.Add(new TableColumn { Title = "备注", WidthMm = 40 });
        return element;
    }

    /// <summary>三列全部绑定到 items 的表格。</summary>
    static TableElement BoundTable()
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Columns[0].Field = "items.sku";
        element.Table.Columns[1].Field = "items.qty";
        element.Table.Columns[2].Field = "items.sku";
        return element;
    }

    /// <summary>单列表格：文本有 98mm 可放，不会折行，宽度差可以直接归因于内容长度。</summary>
    static TableElement SingleColumnTable(string cell)
    {
        var element = Table(showHeader: false, grid: false);
        element.Table.Columns.Clear();
        element.Table.Columns.Add(new TableColumn { Title = string.Empty, WidthMm = 100, Field = string.Empty });
        element.Table.RowHeightMm = 10;
        element.Table.Rows.Add(new List<string> { cell });
        return element;
    }

    [Fact]
    public void 汇总吃formatter改写前的原始值()
    {
        // 金额列 formatter 输出 ¥1,234.56 形态后，Sum 必须还能基于原始数值求和
        var element = Table(showHeader: false, grid: false);
        element.Table.RowHeightMm = 8;
        element.Table.Columns[0].Field = "items.amount";
        element.Table.Columns[0].Formatter = "function(value){ return '¥' + Number(value).toFixed(2); }";
        element.Table.Columns[0].Summary = TableSummary.Sum;
        element.Table.Columns[0].SummaryLabel = "合计：";

        var o = element.Table;
        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report,
            data: new { items = new[] { new { amount = 596.0 }, new { amount = 3200.0 } } })
        {
            Scripts = new ScriptRunner(),
        };

        var rows = TableRenderer.ResolveRows(o, context, "tbl", null, out var dataStart, out _, out var rawRows);
        rows[0][0].Should().Be("¥596.00", "显示文本由 formatter 格式化");
        rawRows[0][0].Should().Be("596", "原始值行保留绑定原值供汇总使用");

        var widths = TableRenderer.ResolveWidths(o, 100);
        using var pool = new TableRenderer.TableStylePool(context);
        var footer = TableRenderer.LayoutFooter(o, pool, rows, rawRows, dataStart, widths, "tbl");
        footer.Should().NotBeNull();
        footer!.Cells[0][0].Should().Contain("3796", "Sum 基于原始值求和（596 + 3200），不受显示格式影响");
    }

    SKBitmap Render(TableElement element, RenderReport? report = null)
        => Fixture.RenderElement(element, Dpi, pageWidthMm: 120, pageHeightMm: 80, report: report, data: Data);
}
