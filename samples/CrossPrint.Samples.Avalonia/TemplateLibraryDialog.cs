using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 模板库对话框（UI 外壳）：分类列表 + 模板卡片网格，点击卡片打开 <see cref="TemplatePreviewDialog"/>。
/// 模板条目与程序化构造器已下沉到 <see cref="BuiltinTemplateCatalog"/>（共享层，所有宿主共用同一份）。
/// </summary>
internal static class TemplateLibraryDialog
{
    public static async Task Show(Control owner, Func<PrintTemplate, string, Task<bool>> applyTemplate)
    {
        var entries = BuiltinTemplateCatalog.BuildEntries();
        var byCategory = new Dictionary<string, List<BuiltinTemplateCatalog.Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byCategory.TryGetValue(entry.Category, out var list))
                byCategory[entry.Category] = list = new List<BuiltinTemplateCatalog.Entry>();
            list.Add(entry);
        }

        var categoryList = new ListBox
        {
            Width = 150,
            Margin = new Thickness(8),
            ItemsSource = BuiltinTemplateCatalog.Categories,
        };

        var cards = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        var scroll = new ScrollViewer { Content = cards, Margin = new Thickness(8) };

        async Task ShowCards(string category)
        {
            cards.Children.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            foreach (var entry in list)
                cards.Children.Add(await BuildCard(entry, applyTemplate));
        }

        categoryList.SelectionChanged += async (_, _) =>
        {
            if (categoryList.SelectedItem is string category)
                await ShowCards(category);
        };

        await ShowCards("全部");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,1*"),
            Margin = new Thickness(8),
        };
        grid.Children.Add(categoryList);
        Grid.SetColumn(categoryList, 0);
        grid.Children.Add(scroll);
        Grid.SetColumn(scroll, 1);

        var panel = new DockPanel();
        var hint = new TextBlock
        {
            Text = "点击模板卡片可预览并应用；缩略图即用真实引擎渲染。",
            Margin = new Thickness(4),
            Foreground = Brushes.Gray,
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        panel.Children.Add(grid);

        await ModalDialog.ShowAsync(owner, "模板库", panel, 1080, 720);
    }

    static async Task<Control> BuildCard(BuiltinTemplateCatalog.Entry entry, Func<PrintTemplate, string, Task<bool>> applyTemplate)
    {
        var border = new Border
        {
            Width = 220,
            Height = 150,
            Margin = new Thickness(6),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var title = new TextBlock
        {
            Text = entry.Name,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(title, Dock.Top);

        var thumb = new Image
        {
            Width = 200,
            Height = 96,
            Stretch = Stretch.Uniform,
        };
        DockPanel.SetDock(thumb, Dock.Top);

        var desc = new TextBlock
        {
            Text = entry.Description,
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        DockPanel.SetDock(desc, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(title);
        dock.Children.Add(thumb);
        dock.Children.Add(desc);
        border.Child = dock;

        thumb.Source = await Task.Run(() => ToBitmap(BuiltinTemplateCatalog.RenderThumbnail(entry.Create())));

        border.PointerPressed += async (_, _) =>
            await TemplatePreviewDialog.ShowAsync(border, entry.Name, entry.Category, entry.Description, entry.Create, applyTemplate);

        return border;
    }

    /// <summary>把共享层渲染出的 SKBitmap 经 PNG 中转拷贝成自包含的 Avalonia 位图，避免引用即将被回收的像素内存。</summary>
    static Bitmap ToBitmap(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        encoded.SaveTo(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }
}
