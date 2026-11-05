using System.Diagnostics;
using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Printing;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// 大数据量压测（用户验收口径）：连续 20 轮、每轮一条 10 万明细行的打印作业，
/// 要求：作业全部成功、无崩溃卡死；内存不随轮数增长（无泄漏）；
/// 每轮结束（强制 GC 后）的托管内存与进程工作集都不得超过 <see cref="MaxManagedBytes"/> / <see cref="MaxWorkingSetBytes"/>。
/// 用高纸（A4 宽 × 1200mm 高，单页 ~190 行）压低页数让 20 轮能在可接受时间跑完，
/// 分页/绑定/渲染/写盘路径与 A4 完全同一条管线，稳定性语义不变。
/// 运行时间约 1-3 分钟，标记 Category=Stress 便于按需筛选。
/// </summary>
[Trait("Category", "Stress")]
public sealed class BigDataStressTests : IDisposable
{
    const int RowCount = 100_000;
    const int Rounds = 20;

    /// <summary>每轮结束后（GC 后）托管内存上限：50MB。数据行本身随轮次新建，实测基线 ~40MB 上下。</summary>
    const long MaxManagedBytes = 50 * 1024 * 1024;

    /// <summary>每轮结束后（GC 后）进程工作集上限：给托管堆 + Skia 原生留出余量（测试宿主基线 ~80MB）。</summary>
    const long MaxWorkingSetBytes = 160 * 1024 * 1024;

    /// <summary>第 1 轮与第 20 轮之间允许的最大增长（泄漏判据）。</summary>
    const long MaxGrowthBytes = 20 * 1024 * 1024;

    readonly string _workDir = Path.Combine(Path.GetTempPath(), $"crossprint-stress-{Guid.NewGuid():N}");

    public BigDataStressTests() => Directory.CreateDirectory(_workDir);

    [Fact]
    public async Task 连续20轮每轮10万行_全部成功且内存不涨()
    {
        var template = TallInvoice();
        var data = Payload(RowCount);
        var target = new PdfFilePrintTarget();
        var options = new PrintSettings(Path.Combine(_workDir, "stress.pdf"))
        {
            Options = new RenderOptions { AllowTablePagination = true, PdfTextAsOutlines = true },
            Overwrite = true,
        };
        var process = Process.GetCurrentProcess();

        long managedBase = ManagedBytes();
        long workingSetFirst = 0;
        long managedMax = 0;
        long workingSetMax = 0;
        var durations = new List<TimeSpan>();

        for (var round = 1; round <= Rounds; round++)
        {
            var watch = Stopwatch.StartNew();
            var result = await target.PrintAsync(new PrintDocument(template, data), options, default);
            watch.Stop();
            durations.Add(watch.Elapsed);

            result.Success.Should().BeTrue($"第 {round} 轮打印失败：{result.Message}");
            result.PrintedPages.Should().BeGreaterThan(1, "10 万行必须真实分页");
            File.Exists(options.OutputPath).Should().BeTrue();
            File.Delete(options.OutputPath);

            // 强制回收后量内存：泄漏（跨轮残留）会在这里持续增长
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();

            var managed = ManagedBytes() - managedBase;
            var workingSet = process.WorkingSet64;
            managedMax = Math.Max(managedMax, managed);
            workingSetMax = Math.Max(workingSetMax, workingSet);
            if (round == 1)
                workingSetFirst = workingSet;
        }

        process.Refresh();

        // 诊断信息落在断言消息里：CI 红了不用复跑也能看到量级
        var report = $"\n每轮耗时(ms)：{string.Join(",", durations.Select(d => (int)d.TotalMilliseconds))}\n"
                     + $"托管内存增量峰值：{managedMax / 1024.0 / 1024:F1} MB（上限 50MB）\n"
                     + $"工作集峰值：{workingSetMax / 1024.0 / 1024:F1} MB\n"
                     + $"第 1 轮工作集：{workingSetFirst / 1024.0 / 1024:F1} MB，"
                     + $"第 20 轮工作集：{process.WorkingSet64 / 1024.0 / 1024:F1} MB";

        var leakGrowth = process.WorkingSet64 - workingSetFirst;
        leakGrowth.Should().BeLessThanOrEqualTo(MaxGrowthBytes, "20 轮之间工作集不得持续增长（泄漏）" + report);
        workingSetMax.Should().BeLessThanOrEqualTo(MaxWorkingSetBytes, "工作集峰值不得失控" + report);
        managedMax.Should().BeLessThanOrEqualTo(MaxManagedBytes, "每轮 GC 后托管内存不得超过 50MB（超过 = 轮间残留未回收）" + report);
    }

    static long ManagedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── 造数 ──

    /// <summary>绑定源：模板列字段是 <c>rows.*</c> 路径。</summary>
    static object Payload(int count)
        => new { rows = Enumerable.Range(1, count)
            .Select(i => new InvoiceRow { Name = $"商品#{i:D6}", Qty = i % 4 + 1, Amount = i * 1.37m })
            .ToList() };

    /// <summary>A4 宽 × 1200mm 高：表格单页装 ~190 行，10 万行 ≈ 530 页/轮，20 轮 ≈ 1 万页，管线同 A4。</summary>
    static PrintTemplate TallInvoice()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 210, HeightMm = 1200 },
        };
        var table = new TableElement
        {
            Id = "stress-table",
            XMm = 10,
            YMm = 12,
            WidthMm = 190,
            HeightMm = 1170,
            Table = new TableOptions
            {
                Columns =
                {
                    new TableColumn { Title = "货物名称", WidthMm = 90, Field = "rows.name" },
                    new TableColumn { Title = "数量", WidthMm = 40, Field = "rows.qty" },
                    new TableColumn { Title = "金额", WidthMm = -1, Field = "rows.amount", Summary = TableSummary.Sum, SummaryLabel = "本页小计：" },
                },
                FontFamily = "Microsoft YaHei",
                FontSizePt = 9,
                RowHeightMm = 6,
            },
        };
        template.Elements.Add(table);
        return template;
    }

    sealed class InvoiceRow
    {
        public string Name { get; init; } = "";
        public int Qty { get; init; }
        public decimal Amount { get; init; }
    }
}
