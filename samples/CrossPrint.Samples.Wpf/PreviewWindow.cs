using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrossPrint.Hosts.Wpf;

namespace CrossPrint.Samples.Wpf;

/// <summary>打印预览窗口：一个 <see cref="PreviewControl"/> 加一条只读说明栏。</summary>
internal sealed class PreviewWindow : Window
{
    public PreviewWindow(PreviewControl preview)
    {
        Title = "打印预览";
        Width = 920;
        Height = 1080;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));

        var info = new TextBlock
        {
            Text = "示例数据：title / orderNo / sku。预览与 PDF 导出共用同一条渲染路径。",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.DimGray,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 4, 6, 4),
        };

        var fit = new Button { Content = "适合窗口", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 0, 2, 0) };
        fit.Click += (_, _) => preview.FitToPage();
        var actual = new Button { Content = "100%", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 0, 2, 0) };
        actual.Click += (_, _) =>
        {
            preview.Kernel.Viewport.Reset();
            preview.InvalidateVisual();
        };

        buttons.Children.Add(fit);
        buttons.Children.Add(actual);

        var bar = new Grid { Background = Brushes.White };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(info, 0);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(info);
        bar.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(preview);
        Content = root;
    }
}
