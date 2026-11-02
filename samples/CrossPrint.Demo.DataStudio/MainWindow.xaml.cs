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
}
