using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CrossPrint.Core;
using CrossPrint.Engine;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 模板库「预览」专用页面：左整页预览图 + 右元信息（分类/纸张/说明/可用变量）+ 底部「使用此模板」。
/// 与工具栏的「预览」（预览当前编辑中的模板）是两套独立实现——这里只看库里的模板，
/// 不读当前内核状态，也不弹打印参数；代码不与 OpenPreview 共用，改这边不影响那边。
/// </summary>
internal static class TemplatePreviewDialog
{
    /// <summary>弹出预览页。useTemplate(template, note) 返回是否已应用（false = 用户在主视图取消），成功后关闭本页。</summary>
    public static async Task ShowAsync(
        Visual anchor,
        string name,
        string category,
        string? description,
        Func<PrintTemplate> create,
        Func<PrintTemplate, string, Task<bool>> useTemplate)
    {
        try
        {
            var template = create();

            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 先弹窗后出图：渲染在后台线程做，页面立即可见。
            var page = BuildPage(name, category, description, template, image, create, useTemplate);
            _ = ModalDialog.ShowAsync(anchor, "预览 — " + name, page, 860, 640, 480, 420);

            image.Source = await Task.Run(() => RenderPage(template));
        }
        catch (Exception ex)
        {
            await ModalDialog.ConfirmAsync(anchor, "预览失败", "渲染模板预览时出错：" + ex.Message);
        }
    }

    static Control BuildPage(
        string name,
        string category,
        string? description,
        PrintTemplate template,
        Image image,
        Func<PrintTemplate> create,
        Func<PrintTemplate, string, Task<bool>> useTemplate)
    {
        // 右侧信息列。
        var info = new StackPanel { Margin = new Thickness(4, 0, 0, 0), Spacing = 10 };

        info.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var paper = template.Paper;
        info.Children.Add(Row("分类", category));
        info.Children.Add(Row("纸张", paper.HeightMm > 0
            ? $"{paper.PageWidthMm:0.#} × {paper.PageHeightMm:0.#} mm"
            : $"{paper.PageWidthMm:0.#} mm 连续纸"));
        info.Children.Add(Row("元素", $"{template.Elements.Count} 个"));

        if (!string.IsNullOrWhiteSpace(description))
        {
            info.Children.Add(new TextBlock
            {
                Text = "模板说明",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            info.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var variables = CollectVariables(template);
        if (variables.Count > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = "可用变量",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var variable in variables)
            {
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEF, 0xFB)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(7, 2),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock
                    {
                        Text = variable,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),
                    },
                });
            }
            info.Children.Add(chips);
        }

        // 底部大按钮：走宿主的使用流程（含未保存改动确认），确认通过才关本页。
        var useButton = new Button
        {
            Content = "使用此模板",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        useButton.Click += async (_, _) =>
        {
            var applied = await useTemplate(create(), $"已应用模板「{name}」。");
            if (applied)
                ModalDialog.CloseHost(useButton);
        };

        var rightColumn = new DockPanel { LastChildFill = true };
        var buttonHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttonHost.Children.Add(useButton);
        DockPanel.SetDock(buttonHost, Dock.Bottom);
        rightColumn.Children.Add(buttonHost);
        rightColumn.Children.Add(new ScrollViewer { Content = info });

        // 左预览区：浅灰底衬白纸，整页大图。
        var previewArea = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12),
            Child = image,
        };

        // 布局：右栏固定 280 宽（信息列不再被挤压竖排），左侧预览随窗口自适应伸缩。
        var root = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,280"),
        };
        Grid.SetColumn(previewArea, 0);
        root.Children.Add(previewArea);
        Grid.SetColumn(rightColumn, 1);
        rightColumn.Margin = new Thickness(12, 0, 0, 0);
        root.Children.Add(rightColumn);
        return root;
    }

    static Control Row(string label, string value) => new Grid
    {
        ColumnDefinitions = ColumnDefinitions.Parse("64,*"),
        Children =
        {
            new TextBlock { Text = label, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Top },
            new TextBlock { [Grid.ColumnProperty] = 1, Text = value, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
        },
    };

    /// <summary>模板里出现的绑定字段（{{占位}} 与 Binding 声明都算），按出现顺序去重。</summary>
    static List<string> CollectVariables(PrintTemplate template)
    {
        var found = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            var key = raw.Trim();
            if (!found.Contains(key))
                found.Add(key);
        }

        foreach (var element in template.Elements)
        {
            Add(element.Binding);
            if (element is TextElement text)
                Add(FirstPlaceholder(text.Text.Content));
            else if (element is BarcodeElement barcode)
                Add(FirstPlaceholder(barcode.Barcode.Content));
            else if (element is QrCodeElement qr)
                Add(FirstPlaceholder(qr.QrCode.Content));
        }
        return found;
    }

    static string? FirstPlaceholder(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        var start = content.IndexOf("{{", StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = content.IndexOf("}}", start + 2, StringComparison.Ordinal);
        if (end < 0)
            return null;
        return content[(start + 2)..end].Trim();
    }

    // ---- 整页渲染（本页自有实现，96 DPI，与缩略图/主预览互不复用）----

    /// <summary>预览页演示数据兜底：模板自带 DesignData 缺字段时补位，避免出现裸 {"{"}字段{"}"}。</summary>
    static Dictionary<string, string> FallbackData { get; } = new()
    {
        ["title"] = "示例标题",
        ["orderNo"] = "ORD-20261104-0001",
        ["sku"] = "SKU-01-A",
        ["name"] = "张三",
        ["phone"] = "138****6688",
        ["address"] = "示例地址：某某市某某区某某路 1 号",
        ["price"] = "68.00",
        ["qty"] = "2",
        ["date"] = "2026-11-04 10:00",
        ["barcode"] = "6901234567890",
    };

    static Bitmap RenderPage(PrintTemplate template)
    {
        var paper = template.Paper;
        var wMm = paper.PageWidthMm;
        var hMm = paper.PageHeightMm;

        // 连续纸按内容自适应估个高度，预览才有形状。
        if (hMm <= 0)
        {
            var maxY = 20.0;
            foreach (var e in template.Elements)
                maxY = Math.Max(maxY, e.YMm + e.HeightMm);
            hMm = Math.Min(400, maxY + 8);
        }

        const double dpi = 96;
        var pxW = Math.Max(1, (int)Math.Round(UnitConverter.MmToPx(wMm, dpi)));
        var pxH = Math.Max(1, (int)Math.Round(UnitConverter.MmToPx(hMm, dpi)));

        using var bitmap = new SKBitmap(pxW, pxH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        TemplateRenderer.Default.Render(template, canvas, new CrossPrint.Core.RenderOptions { Dpi = dpi }, PreviewData(template));
        canvas.Flush();

        return new Bitmap(
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            global::Avalonia.Platform.AlphaFormat.Premul,
            bitmap.GetPixels(),
            new PixelSize(pxW, pxH),
            new Vector(dpi, dpi),
            bitmap.RowBytes);
    }

    /// <summary>预览数据：演示数据打底、模板自带 DesignData 覆盖（渲染器不会自动合并 DesignData）。</summary>
    static Dictionary<string, object?> PreviewData(PrintTemplate template)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in FallbackData)
            dict[kv.Key] = kv.Value;
        foreach (var kv in template.DesignData)
            dict[kv.Key] = kv.Value;
        return dict;
    }
}
