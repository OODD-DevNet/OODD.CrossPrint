using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CrossPrint.Core;
using CrossPrint.Engine;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 模板库弹窗：左侧分类菜单 + 右侧模板卡片（真实渲染缩略图）。
/// 点击卡片把模板套用到设计器（LoadTemplate + BindKernel，由宿主回调完成）。
/// </summary>
/// <summary>
/// 模板库弹窗：左侧分类菜单 + 右侧模板卡片（真实渲染缩略图）。
/// 点击卡片把模板套用到设计器（LoadTemplate + BindKernel，由宿主回调完成）。
/// 行业模板构造器在 <see cref="TemplateLibraryDialog"/> 的 partial 部分（TemplateLibraryDialog.Catalog.cs）。
/// </summary>
internal static partial class TemplateLibraryDialog
{
    /// <summary>一条模板条目：显示名 / 分类 / 惰性构造（每次点卡片给设计器一份新模板）。</summary>
    sealed record Entry(string Name, string Category, Func<PrintTemplate> Create, string Description);

    static readonly string[] Categories =
    [
        "全部", "报表单据", "小票票据", "标签卡券", "财税金融", "教育答题",
        "医疗健康", "餐饮零售", "食品包装", "物流仓储", "人事行政", "工业制造",
    ];

    /// <summary>弹出模板库窗口。applyTemplate(template, note) 由宿主负责 LoadTemplate + 重接面板。</summary>
    /// <summary>弹出模板库。useTemplate(template, note) 返回是否已应用（false = 用户取消/主视图拦截）。</summary>
    public static Task Show(Visual anchor, Func<PrintTemplate, string, Task<bool>> useTemplate)
    {
        var entries = BuildEntries();

        var categoryList = new ListBox
        {
            Width = 132,
            Margin = new Thickness(8, 8, 0, 8),
        };
        foreach (var category in Categories)
            categoryList.Items.Add(new ListBoxItem { Content = category, Tag = category });
        categoryList.SelectedIndex = 0;

        var cardsHost = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 10, 10, 10),
        };

        void ShowCards(string? category)
        {
            cardsHost.Children.Clear();
            foreach (var entry in entries)
            {
                if (category is not (null or "全部") && entry.Category != category)
                    continue;
                cardsHost.Children.Add(BuildCard(entry, useTemplate));
            }
        }

        categoryList.SelectionChanged += (_, _) =>
            ShowCards((categoryList.SelectedItem as ListBoxItem)?.Tag as string);
        ShowCards("全部");

        var root = new DockPanel
        {
            LastChildFill = true,
        };
        var leftBorder = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = categoryList,
        };
        DockPanel.SetDock(leftBorder, Dock.Left);
        root.Children.Add(leftBorder);
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = cardsHost,
        });

        return ModalDialog.ShowAsync(anchor, "模板库", root, 820, 600, 640, 480);
    }

    /// <summary>内置模板清单：两份样例资产 + 三份程序化标签 + 行业模板库（见 Catalog partial）。</summary>
    static List<Entry> BuildEntries()
    {
        return
        [
            new Entry("厨房订单（A4）", "报表单据", LoadSample("kitchen-sink.template.json"),
                "A4 综合样例：文本 / 表格 / 条码 / 二维码 / 线段，覆盖全部元素类型。"),
            new Entry("80mm 小票", "小票票据", LoadSample("complex-receipt.template.json"),
                "80mm 连续纸小票：订单头 + 明细表格 + 汇总区，适合热敏打印。"),
            new Entry("快递面单（100×150）", "标签卡券", BuildShippingLabel,
                "100×150mm 面单：条码单号、收件人、地址与二维码。"),
            new Entry("商品价签（50×30）", "标签卡券", BuildPriceTag,
                "50×30mm 价签：品名 + 价格 + 条码，适合货架标价。"),
            new Entry("条码标签（100×30 连续纸）", "标签卡券", BuildBarcodeTag,
                "100mm 连续纸条码标签：条码 + 名称，适合批量贴标。"),
            .. BuildIndustryEntries(),
        ];
    }

    /// <summary>从样例资产构造模板；资产缺失或解析失败回退空白 A4（弹窗仍可用）。</summary>
    static Func<PrintTemplate> LoadSample(string name)
    {
        return () =>
        {
            try
            {
                var text = SampleAssets.Read(name);
                if (text is not null)
                    return TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant).Template;
            }
            catch
            {
                // 资产不可用时回退默认模板。
            }
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        };
    }

    /// <summary>缩略图/设计态共用的演示数据：覆盖全部占位符字段，避免卡片上出现裸 {"{"}字段{"}"}。</summary>
    static Dictionary<string, string> DemoData { get; } = new()
    {
        ["title"] = "厨房订单",
        ["orderNo"] = "ORD-20261030-0001",
        ["sku"] = "SKU-88-A",
        ["name"] = "张小明",
        ["phone"] = "138****6688",
        ["address"] = "上海市浦东新区张江高科技园区博云路 2 号 301 室",
        ["price"] = "128.00",
        ["qty"] = "2",
        ["date"] = "2026-10-31 15:42",
        ["barcode"] = "8801234567890",
    };

    static Func<PrintTemplate> BuildShippingLabel => () =>
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { WidthMm = 100, HeightMm = 150 },
            DesignData = new(DemoData),
        };
        Add(template, Rect(2, 2, 96, 146));
        Add(template, Text("快递面单", 8, 6, 60, 10, 16, bold: true));
        Add(template, Line(8, 18, 84));
        Add(template, Barcode("{{barcode}}", 8, 21, 84, 16));
        Add(template, Text("收件人", 8, 40, 20, 6, 10, bold: true));
        Add(template, Text("{{name}}    {{phone}}", 28, 40, 64, 6, 10, binding: "name"));
        Add(template, Text("地　址", 8, 49, 20, 6, 10, bold: true));
        Add(template, Text("{{address}}", 28, 49, 64, 14, 10, binding: "address"));
        Add(template, Line(8, 66, 84));
        Add(template, Text("订单号", 8, 70, 20, 6, 10, bold: true));
        Add(template, Text("{{orderNo}}", 28, 70, 40, 6, 10, binding: "orderNo"));
        Add(template, Text("货物明细", 8, 80, 30, 6, 10, bold: true));
        Add(template, Text("{{sku}} × {{qty}}", 8, 88, 56, 6, 10, binding: "sku"));
        Add(template, Qr("{{orderNo}}", 66, 78, 26, 26, "orderNo"));
        Add(template, Text("签收栏：____________", 8, 118, 60, 6, 9));
        Add(template, Text("{{date}}", 66, 138, 26, 6, 9, binding: "date", align: HorizontalAlign.Right));
        return template;
    };

    static Func<PrintTemplate> BuildPriceTag => () =>
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { WidthMm = 50, HeightMm = 30 },
            DesignData = new(DemoData),
        };
        Add(template, Text("{{name}}", 2, 1.5, 46, 6, 10, bold: true, binding: "name"));
        Add(template, Text("￥{{price}}", 2, 9, 24, 8, 13, bold: true, binding: "price"));
        Add(template, Barcode("{{barcode}}", 27, 9, 21, 13, "barcode"));
        Add(template, Text("规格 {{sku}}", 2, 18, 46, 5, 7, binding: "sku"));
        Add(template, Text("{{date}}", 2, 24, 46, 4.5, 7, binding: "date"));
        return template;
    };

    static Func<PrintTemplate> BuildBarcodeTag => () =>
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { WidthMm = 100, HeightMm = 0 }, // 高度 0 = 连续纸，长度自适应
            DesignData = new(DemoData),
        };
        Add(template, Barcode("{{barcode}}", 5, 4, 90, 16, "barcode"));
        Add(template, Text("{{name}}", 5, 22, 90, 6, 9, binding: "name"));
        return template;
    };

    // ── 卡片 ─────────────────────────────────────────────────────────────────

    static Control BuildCard(Entry entry, Func<PrintTemplate, string, Task<bool>> applyTemplate)
    {
        var thumb = new Image
        {
            Height = 150,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbHost = new Border
        {
            Height = 156,
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6)),
            CornerRadius = new CornerRadius(5),
            Child = thumb,
        };

        // 点击卡片直接弹预览页（使用与否在预览页里决定，防误点覆盖当前模板）。
        var cardInner = new Grid();
        var cardText = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 10),
            Spacing = 5,
        };
        cardText.Children.Add(thumbHost);
        cardText.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        cardText.Children.Add(new TextBlock
        {
            Text = entry.Category + " · 点击预览",
            FontSize = 11,
            Foreground = Brushes.DimGray,
        });
        cardInner.Children.Add(cardText);

        var card = new Button
        {
            Width = 196,
            Margin = new Thickness(5),
            Padding = new Thickness(0),
            Content = cardInner,
        };
        ToolTip.SetTip(card, entry.Description);

        card.Click += (_, _) => _ = TemplatePreviewDialog.ShowAsync(
            card, entry.Name, entry.Category, entry.Description, entry.Create, applyTemplate);

        // 异步出缩略图：渲染有 Jint/图片解析等成本，别卡在弹窗打开这一帧。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                thumb.Source = await Task.Run(() => RenderThumbnail(entry.Create()));
            }
            catch
            {
                // 缩略图失败就留灰底占位，不影响点击套用。
            }
        });
        return card;
    }

    /// <summary>用真实渲染引擎出模板缩略图：固定 96 DPI 渲染（低 DPI 文字会缩没了），显示时由 Image 等比缩放。</summary>
    static Bitmap RenderThumbnail(PrintTemplate template)
    {
        var paper = template.Paper;
        var wMm = paper.PageWidthMm;
        var hMm = paper.PageHeightMm;

        // 连续纸按内容自适应估个高度，缩略图才有形状。
        if (hMm <= 0)
        {
            var maxY = 20.0;
            foreach (var e in template.Elements)
                maxY = Math.Max(maxY, e.YMm + e.HeightMm);
            hMm = Math.Min(300, maxY + 8);
        }

        const double dpi = 96;
        var pxW = Math.Max(1, (int)Math.Round(UnitConverter.MmToPx(wMm, dpi)));
        var pxH = Math.Max(1, (int)Math.Round(UnitConverter.MmToPx(hMm, dpi)));

        using var bitmap = new SKBitmap(pxW, pxH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        TemplateRenderer.Default.Render(template, canvas, new CrossPrint.Core.RenderOptions { Dpi = dpi }, ThumbData(template));
        canvas.Flush();

        // Bitmap 构造会拷贝像素，SKBitmap 用完即释放。
        return new Bitmap(
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            global::Avalonia.Platform.AlphaFormat.Premul,
            bitmap.GetPixels(),
            new PixelSize(pxW, pxH),
            new Vector(dpi, dpi),
            bitmap.RowBytes);
    }

    /// <summary>缩略图绑定源：演示数据打底、模板自带 DesignData 覆盖。引擎不会自动合并 DesignData，缩略图得自己喂。</summary>
    static Dictionary<string, object?> ThumbData(PrintTemplate template)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in DemoData)
            dict[kv.Key] = kv.Value;
        foreach (var kv in template.DesignData)
            dict[kv.Key] = kv.Value;
        return dict;
    }

    // ── 程序化造模板的小助手 ─────────────────────────────────────────────────

    static int _seq;

    static void Add(PrintTemplate template, PaperElement element)
    {
        element.Id = $"{element.TypeKey}-{++_seq}";
        template.Elements.Add(element);
    }

    static TextElement Text(string content, double x, double y, double w, double h,
        double pt, bool bold = false, string? binding = null, HorizontalAlign align = HorizontalAlign.Left) =>
        new()
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Binding = binding,
            Text = new TextOptions
            {
                Content = content,
                FontSizePt = pt,
                Bold = bold,
                HorizontalAlign = align,
                Wrap = true,
            },
        };

    static LineElement Line(double x, double y, double lengthMm) =>
        new() { XMm = x, YMm = y, WidthMm = lengthMm, HeightMm = 0 };

    static RectangleElement Rect(double x, double y, double w, double h) =>
        new() { XMm = x, YMm = y, WidthMm = w, HeightMm = h };

    static BarcodeElement Barcode(string content, double x, double y, double w, double h, string? binding = null) =>
        new()
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Binding = binding,
            Barcode = new BarcodeOptions { Content = content },
        };

    static QrCodeElement Qr(string content, double x, double y, double w, double h, string? binding = null) =>
        new()
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Binding = binding,
            QrCode = new QrCodeOptions { Content = content },
        };
}
