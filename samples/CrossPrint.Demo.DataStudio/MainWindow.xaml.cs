using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CrossPrint.Core;
using CrossPrint.Printing;
using Microsoft.Win32;

namespace CrossPrint.Demo.DataStudio;

/// <summary>
/// 数据工作台主窗口：UI 里随便改表头、增删改明细、批量加行，
/// 「打印 PDF」走 PdfFilePrintTarget + AllowTablePagination——表格放不下时由引擎自动跨页，
/// 页脚页码（第 x 页 / 共 y 页）与本页小计由分页上下文注入，不再需要 demo 层切数据。
/// </summary>
public partial class MainWindow : Window
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "PrintOut");

    readonly PdfFilePrintTarget printTarget = new();
    readonly Random rng = new(20261031);
    int autoRowNo;

    public MainWindow()
    {
        InitializeComponent();
        TemplateCombo.ItemsSource = new[]
        {
            "invoice-a4-500row", "invoice-a4", "complex-receipt", "kitchen-sink", "label-100x150",
            "receipt-long", "hotel-folio", "sticker-50", "edge-case-a6", "pick-list-a5",
        }
        .Select(n => new TemplateItem(n, LoadTemplate(n)))
        .ToList();
        TemplateCombo.SelectedIndex = 0;

        Attach(UiInvoiceData.CreateDemo(520));
    }

    UiInvoiceData Data => (UiInvoiceData)DataContext;

    static PrintTemplate LoadTemplate(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".template.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到样例模板 {name}（csproj 的 None Include 应把它复制到输出目录）。", path);

        return TemplateJson.Load(File.ReadAllText(path)).Template;
    }

    void Attach(UiInvoiceData data)
    {
        data.Lines.CollectionChanged += (_, _) => RefreshStatus();
        DataContext = data;
        RefreshStatus();
    }

    void RefreshStatus() =>
        StatusText.Text = $"明细 {Data.Lines.Count} 行 · 合计 {Data.Lines.Sum(l => l.Amount):0.00}";

    // ── 明细行操作 ──

    void OnAddRow(object sender, RoutedEventArgs e)
    {
        Data.Lines.Add(MakeRandomLine());
        Data.RefreshTotal();
    }

    void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        foreach (var line in LinesGrid.SelectedItems.Cast<UiInvoiceLine>().ToArray())
            Data.Lines.Remove(line);
        Data.RefreshTotal();
    }

    void OnAddBatch(object sender, RoutedEventArgs e)
    {
        var count = Math.Clamp(int.TryParse(BatchCountBox.Text, out var n) ? n : 50, 1, 5000);
        for (var i = 0; i < count; i++)
            Data.Lines.Add(MakeRandomLine());
        Data.RefreshTotal();
        StatusText.Text = $"已批量加入 {count} 行";
    }

    /// <summary>随机商品行：单价 8~48 × 数量 1~4 → 金额 8~192，覆盖行色三档。</summary>
    UiInvoiceLine MakeRandomLine()
    {
        var qty = rng.Next(1, 5);
        var unit = Math.Round((decimal)(rng.NextDouble() * 40 + 8), 2);
        return new UiInvoiceLine
        {
            Name = $"{UiInvoiceData.Goods[autoRowNo % UiInvoiceData.Goods.Length]}#+{autoRowNo:D3}",
            Spec = UiInvoiceData.Specs[autoRowNo % UiInvoiceData.Specs.Length],
            Qty = qty,
            UnitPrice = unit,
            Amount = Math.Round(unit * qty, 2),
        };
    }

    /// <summary>单元格编辑提交后：勾了自动算金额就重算本行 Amount，然后合计恒等于明细之和。</summary>
    void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (e.Row.Item is not UiInvoiceLine line)
                return;

            var isQtyOrUnit = e.Column.Header as string is "数量" or "单价";
            if (AutoCalcCheck.IsChecked == true && isQtyOrUnit)
                line.Amount = Math.Round(line.Qty * line.UnitPrice, 2);
            Data.RefreshTotal();
        });
    }

    // ── JSON 导入导出 ──

    void OnExportJson(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON 数据|*.json", FileName = "invoice-data.json" };
        if (dlg.ShowDialog(this) != true)
            return;

        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(Data, JsonOpts));
        StatusText.Text = $"已保存 {dlg.FileName}";
    }

    void OnImportJson(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON 数据|*.json" };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<UiInvoiceData>(File.ReadAllText(dlg.FileName), JsonOpts);
            if (loaded is null)
                return;

            Attach(loaded);
            StatusText.Text = $"已读取 {Path.GetFileName(dlg.FileName)}（{loaded.Lines.Count} 行）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取失败：{ex.Message}", "JSON 导入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 打印 ──

    async void OnPrintPdf(object sender, RoutedEventArgs e)
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var template = ((TemplateItem)TemplateCombo.SelectedItem).Template;
        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, $"datastudio-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");

        var report = new RenderReport();
        var settings = new PrintSettings(path)
        {
            // 文字转轮廓控制体积（引擎不子集化字库）；表格跨页交给引擎分页器
            Options = new RenderOptions { PdfTextAsOutlines = true, AllowTablePagination = true },
        };

        PrintButton.IsEnabled = false;
        StatusText.Text = $"正在打印 {Data.Lines.Count} 行 …";
        try
        {
            var result = await printTarget.PrintAsync(new PrintDocument(template, Data), settings, report, default);
            IssuesList.ItemsSource = report.Issues
                .Select(i => $"{i.Kind} [{i.ElementId ?? "-"}] {i.Message}")
                .ToList();

            if (result.Success)
            {
                var kb = new FileInfo(path).Length / 1024.0;
                StatusText.Text = $"已生成 {Path.GetFileName(path)}：{result.PrintedPages} 页 / {kb:0.0} KB，正在打开 …";
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                StatusText.Text = result.Error is { } err
                    ? $"打印失败：{result.Message}（{err.GetType().Name}: {err.Message}）"
                    : $"打印失败：{result.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打印异常：{ex.Message}";
        }
        finally
        {
            PrintButton.IsEnabled = true;
        }
    }

    void OnOpenOutDir(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(OutputDir);
        Process.Start(new ProcessStartInfo(OutputDir) { UseShellExecute = true });
    }

    // ── 连续打印压测：20 轮 × 每轮 10 万条，不中断连打 ──

    /// <summary>压测轮数与每轮行数（用户验收口径，不做成可调参数——口径固定才能横向对比）。</summary>
    const int StressRounds = 20;
    const int StressRowCount = 100_000;

    /// <summary>验收线：每轮结束（强制 GC 后）托管内存不得超过 50MB，超过即视为轮间残留。</summary>
    const long StressManagedLimitBytes = 50 * 1024 * 1024;

    CancellationTokenSource? stressCts;

    async void OnStartStress(object sender, RoutedEventArgs e)
    {
        if (stressCts is not null)
            return;

        var dir = Path.Combine(OutputDir, "stress");
        Directory.CreateDirectory(dir);
        var template = StressTemplate();
        var data = StressPayload(StressRowCount);
        var path = Path.Combine(dir, "stress.pdf");
        var settings = new PrintSettings(path)
        {
            Options = new RenderOptions { PdfTextAsOutlines = true, AllowTablePagination = true },
            Overwrite = true,
        };
        var process = Process.GetCurrentProcess();
        var overall = Stopwatch.StartNew();

        // 基线在数据建好之后量：数据本身常驻，压的就是"打印链路不新增残留"
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBase = GC.GetTotalMemory(forceFullCollection: true);
        process.Refresh();
        var workingSetFirst = process.WorkingSet64;

        PrintButton.IsEnabled = false;
        StressButton.IsEnabled = false;
        StressStopButton.IsEnabled = true;
        stressCts = new CancellationTokenSource();

        var durations = new List<TimeSpan>();
        long managedMax = 0;
        long workingSetMax = 0;
        try
        {
            for (var round = 1; round <= StressRounds; round++)
            {
                stressCts.Token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();

                // 注意契约：PrintAsync 永不把作业失败折成异常，取消才会抛 OperationCanceledException
                var result = await printTarget.PrintAsync(new PrintDocument(template, data), settings, null, stressCts.Token);
                watch.Stop();
                durations.Add(watch.Elapsed);

                if (!result.Success)
                {
                    StatusText.Text = $"压测第 {round} 轮失败：{result.Message}（{result.Error?.GetType().Name}: {result.Error?.Message}）";
                    return;
                }

                // 压测关心的是管线稳定性不是产物，每轮出完即删，避免 20 份几百页 PDF 占盘
                File.Delete(path);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                process.Refresh();
                var managed = GC.GetTotalMemory(forceFullCollection: false) - managedBase;
                var workingSet = process.WorkingSet64;
                managedMax = Math.Max(managedMax, managed);
                workingSetMax = Math.Max(workingSetMax, workingSet);
                var verdict = managed <= StressManagedLimitBytes ? "OK" : "超 50MB！";
                StatusText.Text = $"压测 {round}/{StressRounds} 轮成功：{result.PrintedPages} 页 · {watch.Elapsed.TotalSeconds:0} s · "
                                  + $"托管内存 {managed / 1024.0 / 1024:0.#} MB [{verdict}] · 工作集 {workingSet / 1024.0 / 1024:0} MB";
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            var growth = process.WorkingSet64 - workingSetFirst;
            StatusText.Text = $"压测完成：{StressRounds} 轮 × {StressRowCount:N0} 行全部成功，总耗时 {overall.Elapsed.TotalMinutes:0.#} 分钟；"
                              + $"单轮最长 {durations.Max().TotalSeconds:0} s；托管内存峰值 {managedMax / 1024.0 / 1024:0.#} MB（上限 50）；"
                              + $"工作集峰值 {workingSetMax / 1024.0 / 1024:0} MB，首末轮差 {growth / 1024.0 / 1024:0.#} MB（增长明显＝有泄漏）";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"压测已停止（完成 {durations.Count}/{StressRounds} 轮，总耗时 {overall.Elapsed.TotalMinutes:0.#} 分钟）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"压测异常（第 {durations.Count + 1} 轮）：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            stressCts.Dispose();
            stressCts = null;
            StressStopButton.IsEnabled = false;
            StressButton.IsEnabled = true;
            PrintButton.IsEnabled = true;
        }
    }

    void OnStopStress(object sender, RoutedEventArgs e) => stressCts?.Cancel();

    /// <summary>
    /// 压测专用模板：A4 宽 × 1200mm 高（单页约 190 行，10 万行 ≈ 530 页/轮），20 轮约 28 分钟能跑完。
    /// 分页/绑定/渲染/写盘与真实 A4 发票完全同一条管线，稳定性语义不变；刻意带「本页小计」汇总列
    /// 把尾行求值也压进每页渲染。压测不依赖左侧数据——用独立载荷，避免 10 万行撑爆 DataGrid。
    /// </summary>
    static PrintTemplate StressTemplate()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 210, HeightMm = 1200 },
        };
        template.Elements.Add(new TableElement
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
                    new TableColumn { Title = "货物名称", WidthMm = 80, Field = "rows.name" },
                    new TableColumn { Title = "规格", WidthMm = 40, Field = "rows.spec" },
                    new TableColumn { Title = "数量", WidthMm = 30, Field = "rows.qty" },
                    new TableColumn { Title = "金额", WidthMm = -1, Field = "rows.amount", Summary = TableSummary.Sum, SummaryLabel = "本页小计：" },
                },
                FontFamily = "Microsoft YaHei",
                FontSizePt = 9,
                RowHeightMm = 6,
            },
        });
        return template;
    }

    /// <summary>压测载荷：固定种子随机行，与 BigDataStressTests 同口径（引擎按反射绑定 rows.* 路径）。</summary>
    static object StressPayload(int count)
        => new
        {
            rows = Enumerable.Range(1, count)
                .Select(i => new StressRow
                {
                    Name = $"商品#{i:D6}",
                    Spec = $"规格-{i % 97:D2}",
                    Qty = i % 4 + 1,
                    Amount = i * 1.37m,
                })
                .ToList(),
        };

    sealed class StressRow
    {
        public string Name { get; init; } = "";
        public string Spec { get; init; } = "";
        public int Qty { get; init; }
        public decimal Amount { get; init; }
    }
}
