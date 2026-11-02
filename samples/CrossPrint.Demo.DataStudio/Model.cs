using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CrossPrint.Core;

namespace CrossPrint.Demo.DataStudio;

/// <summary>
/// 明细行：引擎按反射绑定 <c>lines.name / lines.qty / lines.amount</c> 等路径，
/// UI 端实现 INPC 是为了让"数量/单价"改动后金额单元格与整行底色（金额分档）即时刷新。
/// </summary>
public sealed class UiInvoiceLine : INotifyPropertyChanged
{
    string name = "";
    string spec = "";
    int qty;
    decimal unitPrice;
    decimal amount;

    public string Name { get => name; set => Set(ref name, value); }
    public string Spec { get => spec; set => Set(ref spec, value); }
    public int Qty { get => qty; set => Set(ref qty, value); }
    public decimal UnitPrice { get => unitPrice; set => Set(ref unitPrice, value); }
    public decimal Amount { get => amount; set => Set(ref amount, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>发票抬头主体：模板用 <c>buyer.name / seller.taxId</c> 等点分路径绑定，打印时反射读取。</summary>
public sealed class UiCompany
{
    public string Name { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string Address { get; set; } = "";
    public string Bank { get; set; } = "";
}

/// <summary>
/// 一张发票的全部数据，直接作为引擎绑定源（可变 POCO，无需 DTO 转换）。
/// 日期与金额用字符串承载，方便在 UI 里随手改成任意值去验证渲染。
/// </summary>
public sealed class UiInvoiceData : INotifyPropertyChanged
{
    public string InvoiceNo { get; set; } = "";
    public string InvoiceDate { get; set; } = "";
    public UiCompany Buyer { get; set; } = new();
    public UiCompany Seller { get; set; } = new();
    public ObservableCollection<UiInvoiceLine> Lines { get; set; } = [];

    string totalAmount = "";
    public string TotalAmount { get => totalAmount; set => Set(ref totalAmount, value); }

    public string AmountInWords { get; set; } = "";
    public string Payee { get; set; } = "";
    public string Checker { get; set; } = "";
    public string Clerk { get; set; } = "";

    /// <summary>合计恒等于明细之和；增删行、改金额后由界面调用。</summary>
    public void RefreshTotal() => TotalAmount = Lines.Sum(l => l.Amount).ToString("0.00");

    /// <summary>
    /// 初始演示数据：固定种子的 520 行对账单（金额 8~192，覆盖行色三档），
    /// 引擎表格跨页开启后约切 22 页。
    /// </summary>
    public static UiInvoiceData CreateDemo(int lineCount)
    {
        var rng = new Random(20261031);
        var lines = new ObservableCollection<UiInvoiceLine>();
        for (var i = 1; i <= lineCount; i++)
        {
            var qty = rng.Next(1, 5);
            var unit = Math.Round((decimal)(rng.NextDouble() * 40 + 8), 2);
            lines.Add(new UiInvoiceLine
            {
                Name = $"{Goods[i % Goods.Length]}#{i:D3}",
                Spec = Specs[i % Specs.Length],
                Qty = qty,
                UnitPrice = unit,
                Amount = Math.Round(unit * qty, 2),
            });
        }

        var data = new UiInvoiceData
        {
            InvoiceNo = $"INV-20261031-{lineCount:D4}",
            InvoiceDate = "2026-10-31",
            Buyer = new UiCompany
            {
                Name = "拾光商贸有限公司",
                TaxId = "91510100MA6TEST10X",
                Address = "成都市高新区天府大道 1 号 028-88886666",
                Bank = "工行高新支行 4402 0100 0999 888",
            },
            Seller = new UiCompany
            {
                Name = "拾光咖啡供应链有限公司",
                TaxId = "91510100MA6TEST20Y",
                Address = "成都市高新区万象城 L3-12 028-88887777",
                Bank = "建行城南支行 5100 1999 2222 333",
            },
            Lines = lines,
            AmountInWords = "贰万捌仟陆佰肆拾贰元柒角伍分",
            Payee = "王五",
            Checker = "李四",
            Clerk = "张三",
        };
        data.RefreshTotal();
        return data;
    }

    /// <summary>批量加行用的商品/规格池：8 种循环，与 docs/samples 的对账单样张同源。</summary>
    internal static readonly string[] Goods =
    [
        "咖啡豆（云南保山日晒）", "咖啡豆（埃塞俄比亚水洗）", "挂耳咖啡礼盒", "冷萃液（无糖）",
        "燕麦奶 1L", "可颂（冷冻生胚）", "曲奇礼盒", "马克杯 350ml",
    ];
    internal static readonly string[] Specs =
    [
        "250g/袋", "250g/袋", "10 片/盒", "18 袋/箱", "1L/盒", "20 个/袋", "8 块/盒", "350ml/只",
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 行底色与 docs/samples 对账单样张同一套阈值：金额 &gt;90 金 / &gt;60 蓝 / &gt;50 灰，其余用默认（不设值）。
/// </summary>
public sealed class AmountToBrushConverter : IValueConverter
{
    static readonly SolidColorBrush Gray = Freeze("#D9D9D9");
    static readonly SolidColorBrush Blue = Freeze("#B7D3F2");
    static readonly SolidColorBrush Gold = Freeze("#F2D06B");

    static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        decimal amount when amount > 90 => Gold,
        decimal amount when amount > 60 => Blue,
        decimal amount when amount > 50 => Gray,
        decimal => DependencyProperty.UnsetValue,
        _ => Binding.DoNothing,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>模板下拉项：名字即 docs/samples 里的模板文件名（去后缀）。</summary>
public sealed record TemplateItem(string Name, PrintTemplate Template)
{
    public override string ToString() => Name;
}
