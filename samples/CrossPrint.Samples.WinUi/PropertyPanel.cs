using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Designer;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 数据驱动的属性面板：遍历 <see cref="ElementProperties.Descriptors"/> 生成表单，仅服务单选。
/// 提交统一走 <see cref="EditorKernel.SetSelectedProperty"/>；解析失败只显示错误文本，不抛异常。
/// 面板只在"选中元素 / 描述符集合"变化时重建，拖拽期间的高频 ViewChanged 不会销毁编辑框。
/// </summary>
public sealed class PropertyPanel : UserControl
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    EditorKernel _kernel;
    readonly StackPanel _rows = new() { Spacing = 6, Margin = new Thickness(8) };
    readonly TextBlock _message = new()
    {
        Foreground = new SolidColorBrush(Colors.IndianRed),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 4, 8, 0),
        Visibility = Visibility.Collapsed,
    };

    bool _rebuilding;
    string _signature = string.Empty;

    /// <summary>为指定内核创建属性面板。</summary>
    /// <param name="kernel">设计器内核；面板跟随它的选中集合刷新。</param>
    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;

        // UserControl 的默认内容对齐不一定拉伸：显式撑满，属性行才能吃到面板全宽。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        var scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // ScrollViewer 默认按内容期望宽度摆放；不拉伸的话右侧编辑器列会被挤没。
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_message, 0);
        Grid.SetRow(scroll, 1);
        root.Children.Add(_message);
        root.Children.Add(scroll);
        Content = root;

        _kernel.ViewChanged += OnKernelViewChanged;
        Rebuild();
    }

    /// <summary>按当前选中重新生成表单。</summary>
    public void RefreshPanel() => Rebuild();

    /// <summary>切换到另一个内核（宿主换模板后调用）。</summary>
    public void Attach(EditorKernel kernel)
    {
        _kernel.ViewChanged -= OnKernelViewChanged;
        _kernel = kernel;
        _kernel.ViewChanged += OnKernelViewChanged;
        _signature = string.Empty;
        Rebuild();
    }

    void OnKernelViewChanged()
    {
        if (_rebuilding)
            return;

        var properties = _kernel.SelectedProperties;
        var signature = properties is null ? string.Empty : BuildSignature(properties);
        if (signature == _signature)
            return;

        ScheduleRebuild();
    }

    void ScheduleRebuild()
    {
        if (_rebuilding)
            return;

        // 在属性提交（LostFocus/KeyDown）的同一个调用栈里销毁编辑控件会打断焦点转移，延后一拍更稳。
        DispatcherQueue.TryEnqueue(Rebuild);
    }

    void Rebuild()
    {
        if (_rebuilding)
            return;

        _rebuilding = true;
        try
        {
            _rows.Children.Clear();
            var properties = _kernel.SelectedProperties;
            if (properties is null)
            {
                _signature = string.Empty;
                _rows.Children.Add(new TextBlock
                {
                    Text = "点击画布元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。",
                    Foreground = new SolidColorBrush(Colors.Gray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                return;
            }

            _signature = BuildSignature(properties);
            _rows.Children.Add(new TextBlock
            {
                Text = DescribeElement(properties.Element),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            if (properties.Actions.Count > 0)
                _rows.Children.Add(BuildActionBar(properties));

            string? group = null;
            foreach (var descriptor in properties.Descriptors)
            {
                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    group = descriptor.Group;
                    _rows.Children.Add(new TextBlock
                    {
                        Text = group,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Colors.SteelBlue),
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                }

                _rows.Children.Add(BuildRow(properties.Element, descriptor));
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    static string BuildSignature(ElementProperties properties)
        => properties.Element.Id + "|" + string.Join("|", properties.Descriptors.Select(d => d.Key));

    static string DescribeElement(PaperElement element) => element switch
    {
        TextElement => "文本元素",
        LineElement => "线条元素",
        RectangleElement => "矩形元素",
        EllipseElement => "椭圆元素",
        ImageElement => "图片元素",
        QrCodeElement => "二维码元素",
        BarcodeElement => "一维码元素",
        TableElement => "表格元素",
        _ => element.TypeKey,
    };

    /// <summary>结构动作按钮行（表格列的增删）：点击交给内核，成败都重建面板（动作清单是快照）。</summary>
    /// <remarks>
    /// 这里用 <see cref="VariableSizedWrapGrid"/> 而不是横向 StackPanel：外层滚动条横向是 <c>Disabled</c>，
    /// 不换行的容器一超过面板宽度，右边的"删除列N"就被裁掉且滚不出来——列数稍多的表格在界面上等于删不了列。
    /// 该面板在 <c>ItemWidth=NaN</c>（默认）时按**第一个**子项定格宽，而第一个按钮"追加列"是最短的标签，
    /// 长标签会被裁，所以先量出最宽的那个按钮再把它写回 ItemWidth。
    /// </remarks>
    FrameworkElement BuildActionBar(ElementProperties properties)
    {
        var panel = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 4),
        };

        double widest = 0;
        foreach (var action in properties.Actions)
        {
            var button = new Button
            {
                Content = action.DisplayName,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 4),
            };
            button.Click += (_, _) => RunAction(action);
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, button.DesiredSize.Width + button.Margin.Left + button.Margin.Right);
            panel.Children.Add(button);
        }

        panel.ItemWidth = widest;
        return panel;
    }

    void RunAction(ElementAction action)
    {
        try
        {
            _kernel.RunElementAction(action.Key);
        }
        catch (Exception ex)
        {
            ShowMessage($"动作“{action.DisplayName}”执行失败：{ex.Message}");
        }

        ScheduleRebuild();
    }

    FrameworkElement BuildRow(PaperElement element, PropertyDescriptor descriptor)
    {
        var label = new TextBlock
        {
            Text = descriptor.DisplayName,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = descriptor.Kind == PropertyKind.MultilineText
                ? VerticalAlignment.Top
                : VerticalAlignment.Center,
        };

        var editor = BuildEditor(element, descriptor);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;

        var grid = new Grid
        {
            ColumnSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // 标签列固定 132（120–140 之间），编辑器列吃掉剩余宽度；面板由外层 ScrollViewer 纵向滚动。
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    FrameworkElement BuildEditor(PaperElement element, PropertyDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case PropertyKind.Boolean:
            {
                var check = new CheckBox
                {
                    IsChecked = descriptor.GetValue(element) is true,
                    IsEnabled = !descriptor.ReadOnly,
                };
                check.Click += (_, _) =>
                {
                    if (_rebuilding)
                        return;

                    Commit(element.Id, descriptor, check.IsChecked == true);
                };
                return check;
            }

            case PropertyKind.Enum:
            {
                var combo = new ComboBox
                {
                    ItemsSource = descriptor.Options,
                    IsEnabled = !descriptor.ReadOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var current = descriptor.GetValue(element)?.ToString();
                if (current is not null)
                    combo.SelectedItem = current;

                combo.SelectionChanged += (_, _) =>
                {
                    if (_rebuilding)
                        return;

                    if (combo.SelectedItem is string name)
                        Commit(element.Id, descriptor, name);
                };
                return combo;
            }

            case PropertyKind.Number:
            case PropertyKind.Integer:
                return CreateTextBox(
                    element,
                    descriptor,
                    FormatValue(descriptor.GetValue(element)),
                    text => ParseNumber(descriptor, text),
                    commitOnEnter: true);

            case PropertyKind.Color:
                return CreateTextBox(
                    element,
                    descriptor,
                    FormatValue(descriptor.GetValue(element)),
                    ParseColor,
                    commitOnEnter: true);

            case PropertyKind.DashPattern:
                return CreateTextBox(
                    element,
                    descriptor,
                    FormatValue(descriptor.GetValue(element)),
                    ParseDash,
                    commitOnEnter: true);

            case PropertyKind.MultilineText:
                return CreateTextBox(
                    element,
                    descriptor,
                    FormatValue(descriptor.GetValue(element)),
                    text => text,
                    commitOnEnter: false);

            default:
                return CreateTextBox(
                    element,
                    descriptor,
                    FormatValue(descriptor.GetValue(element)),
                    text => text,
                    commitOnEnter: true);
        }
    }

    TextBox CreateTextBox(
        PaperElement element,
        PropertyDescriptor descriptor,
        string initial,
        Func<string, object?> parse,
        bool commitOnEnter)
    {
        var multiline = descriptor.Kind == PropertyKind.MultilineText;
        var box = new TextBox
        {
            Text = initial,
            IsReadOnly = descriptor.ReadOnly,
            IsEnabled = !descriptor.ReadOnly,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 72 : 0,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };

        box.LostFocus += (_, _) => TryCommit(element.Id, descriptor, parse, box.Text);
        if (commitOnEnter)
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter)
                    return;

                e.Handled = true;
                TryCommit(element.Id, descriptor, parse, box.Text);
            };
        }
        else
        {
            // 多行框的裸 Enter 是换行；Ctrl+Enter 才提交（与 WinForms/WPF 示例一致）。
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter || !ControlDown())
                    return;

                e.Handled = true;
                TryCommit(element.Id, descriptor, parse, box.Text);
            };
        }

        return box;
    }

    static bool ControlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    void TryCommit(string elementId, PropertyDescriptor descriptor, Func<string, object?> parse, string text)
    {
        object? value;
        try
        {
            value = parse(text);
        }
        catch (Exception ex)
        {
            ShowMessage($"属性“{descriptor.DisplayName}”：{ex.Message}");
            ScheduleRebuild();
            return;
        }

        Commit(elementId, descriptor, value);
    }

    void Commit(string elementId, PropertyDescriptor descriptor, object? value)
    {
        if (_rebuilding)
            return;

        // 选中早已换人时，LostFocus 还会从旧控件冒上来；此时写入会把值打到新元素上。
        var current = _kernel.SelectedProperties;
        if (current is null || current.Element.Id != elementId)
            return;

        try
        {
            _kernel.SetSelectedProperty(descriptor.Key, value);
            ShowMessage(null);
        }
        catch (Exception ex)
        {
            ShowMessage($"属性“{descriptor.DisplayName}”写入失败：{ex.Message}");
        }

        ScheduleRebuild();
    }

    void ShowMessage(string? message)
    {
        _message.Text = message ?? string.Empty;
        _message.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    static object? ParseNumber(PropertyDescriptor descriptor, string text)
    {
        var trimmed = text.Trim();

        if (descriptor.Kind == PropertyKind.Integer)
        {
            if (!int.TryParse(trimmed, NumberStyles.Integer, Inv, out var integer))
                throw new FormatException($"“{text}”不是整数。");

            var clamped = (double)integer;
            if (descriptor.Min is { } min)
                clamped = Math.Max(clamped, min);
            if (descriptor.Max is { } max)
                clamped = Math.Min(clamped, max);
            return (int)clamped;
        }

        // 可空数值（如表格列字号）留空表示继承；不可空字段由内核校验并报错。
        if (trimmed.Length == 0)
            return null;

        if (!double.TryParse(trimmed, NumberStyles.Float, Inv, out var number))
            throw new FormatException($"“{text}”不是数字。");

        if (descriptor.Min is { } lo && number < lo)
            number = lo;
        if (descriptor.Max is { } hi && number > hi)
            number = hi;
        return number;
    }

    static object? ParseColor(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null; // 可空颜色字段的"清空"语义

        if (!SKColor.TryParse(trimmed, out var color))
            throw new FormatException("颜色需形如 #RRGGBB 或 #AARRGGBB。");

        return color;
    }

    static object? ParseDash(string text)
    {
        var parts = text.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out values[i]))
                throw new FormatException($"“{parts[i]}”不是数字；虚线段长写成 1.5,1 这样。");
        }

        return values;
    }

    static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        SKColor color => color.Alpha == 255
            ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            : $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}",
        double[] dash => string.Join(",", dash.Select(v => v.ToString("0.###", Inv))),
        double number => number.ToString("0.###", Inv),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, Inv),
        _ => value.ToString() ?? string.Empty,
    };
}
