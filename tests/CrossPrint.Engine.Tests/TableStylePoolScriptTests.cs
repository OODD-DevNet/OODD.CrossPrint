using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 表体样式池的脚本分桶取证。字体族缺失时 TextStyle 按探针字符回退选字体，
/// 缓存键若不含脚本信息，"谁先被测量"就决定整张表落到哪套字体——CJK 格会被 ASCII 格
/// 劫持到没有汉字字形的西文回退字体上，折行数与行高全按豆腐块的窄步进算。
/// 一切从 <see cref="TableRenderer.LayoutBody"/> 的实测行高/折行数反推。
/// </summary>
public class TableStylePoolScriptTests
{
    /// <summary>任何机器上都不存在的族名：强制走"按探针字符回退选字体"的路径。</summary>
    const string MissingFamily = "CrossPrint Missing Font ZX-9";

    /// <summary>40 个互不重复的汉字（CJK 基本区）：CJK 全角步进下必超 28mm 内宽，西文豆腐块步进下折得更少。</summary>
    static readonly string Cjk40 = string.Concat(Enumerable.Range(0x4E00, 40).Select(cp => char.ConvertFromUtf32(cp)));

    static TableOptions Opts(double? secondColumnPt = null)
    {
        var o = new TableOptions { FontFamily = MissingFamily };
        o.Columns.Add(new TableColumn { Title = "名称", WidthMm = 30 });
        if (secondColumnPt is { } pt)
            o.Columns.Add(new TableColumn { Title = "Name", WidthMm = 30, FontSizePt = pt });
        return o;
    }

    static (double[] RowHeights, int[] FirstColumnLineCounts, RenderReport Report) Layout(TableOptions o, IReadOnlyList<string> column0, IReadOnlyList<string>? column1 = null)
    {
        var rows = Enumerable.Range(0, column0.Count)
            .Select(r => column1 is null ? new List<string> { column0[r] } : new List<string> { column0[r], column1[r] })
            .ToList();

        var report = new RenderReport();
        var context = new RenderContext(new RenderOptions(), report);
        var lefts = column1 is null ? new[] { 0d } : new[] { 0d, 30d };
        var widths = column1 is null ? new[] { 30d } : new[] { 30d, 30d };
        var aligns = column1 is null ? new[] { HorizontalAlign.Left } : new[] { HorizontalAlign.Left, HorizontalAlign.Left };

        using var pool = new TableRenderer.TableStylePool(context);
        var body = TableRenderer.LayoutBody(o, rows, 0, null, lefts, widths, pool, "t", aligns);

        // 每行取第 0 列格子的视觉行数——行高由它驱动
        var counts = new int[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var top = body.RowTops[r];
            counts[r] = body.Cells.First(c => c.X == 0 && Math.Abs(c.Top - top) < 1e-9).Lines.Count;
        }

        return (body.RowHeights, counts, report);
    }

    [Fact]
    public void 回退按探针分叉是前提_纯CJK表必须比纯ASCII表折更多行()
    {
        // 正向对照：若这台机器上 ASCII 探针与 CJK 探针回退到同一套字体，后面的顺序测试就没有意义——先量出"确实分叉"
        var ascii = Layout(Opts(), new[] { "ABCDabcd0123" });
        var cjk = Layout(Opts(), new[] { Cjk40 });

        ascii.Report.Issues.Should().Contain(i => i.Kind == RenderIssueKind.FontFallback, "族名不存在必然触发回退告警");
        cjk.FirstColumnLineCounts[0].Should().BeGreaterThan(ascii.FirstColumnLineCounts[0],
            "CJK 字体按全角步进折行，西文回退字体把汉字当豆腐块窄步进——两套度量的行数必须不同");
        cjk.RowHeights[0].Should().BeGreaterThan(ascii.RowHeights[0]);
    }

    [Fact]
    public void ASCII格先测量不再劫持CJK行的度量()
    {
        // 同一组内容，只交换先后测量顺序：CJK 行的行高与折行数必须一致。
        // 修复前实测：ASCII 先测 → 缓存落到西文回退字体 → CJK 行按豆腐块窄步进只折一半的行。
        var asciiFirst = Layout(Opts(), new[] { "ABCD0123", Cjk40 });
        var cjkFirst = Layout(Opts(), new[] { Cjk40, "ABCD0123" });

        // 变体 A 的 CJK 行在第 2 行，变体 B 在第 1 行
        cjkFirst.RowHeights[0].Should().Be(asciiFirst.RowHeights[1], "CJK 行高不能因测量先后而变");
        cjkFirst.FirstColumnLineCounts[0].Should().Be(asciiFirst.FirstColumnLineCounts[1], "CJK 行折行数不能因测量先后而变");
    }

    [Fact]
    public void 混合脚本多文本单元格的样式构造数受字体设置乘脚本桶数约束()
    {
        // 构造数是可诚实观测的：族名缺失时每构造一个 TextStyle 记一条 FontFallback，缓存命中不再构造——告警条数即缓存条目数。
        // 40 个互不重复的文本、2 种字号（列）、3 个脚本桶 → 上限 (列字号数 × 命中桶数)，与文本种数无关。
        var o = Opts(secondColumnPt: 11);
        var column0 = Enumerable.Range(0, 10).SelectMany(i => new[] { $"LAT-{i}-unique-text", $"汉字{i}测试内容不重复" }).ToArray();
        var column1 = Enumerable.Range(0, 20).Select(i => $"عربي-{i}-متن-فريد").ToArray();

        var a = Layout(o, column0, column1);
        // 反向测量（同一批行从尾到头），构造数与行高集合都不许变
        var b = Layout(o, column0.Reverse().ToArray(), column1.Reverse().ToArray());

        // 列1 命中拉丁+CJK 两桶，列2 命中阿拉伯语桶一桶：共 3 条，而不是 20+20 种文本各一条
        var fallbacks = a.Report.Issues.Count(i => i.Kind == RenderIssueKind.FontFallback);
        fallbacks.Should().Be(3, "每（字体设置 × 脚本桶）只构造一次，缓存数与文本种数无关");
        b.Report.Issues.Count(i => i.Kind == RenderIssueKind.FontFallback).Should().Be(fallbacks, "构造数不能随先后顺序变");
        b.RowHeights.Should().Equal(a.RowHeights.Reverse().ToArray(), "行高逐行对应，只随内容不随测量顺序");
    }
}
