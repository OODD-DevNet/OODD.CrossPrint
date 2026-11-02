using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 数据驱动的属性面板：按选中元素的描述符动态生成表单，提交统一走 Kernel.SetSelectedProperty。
/// </summary>
public sealed class PropertyPanel : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 6, Margin = new Thickness(8) };
    readonly TextBlock _message = new()
    {
        Foreground = Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 4, 8, 0),
        IsVisible = false,
    };

    EditorKernel _kernel;
    bool _rebuilding;
    string _signature = string.Empty;

    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;

        var scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel();
        DockPanel.SetDock(_message, Dock.Top);
        root.Children.Add(_message);
        root.Children.Add(scroll);
        Content = root;

        _kernel.ViewChanged += OnKernelViewChanged;
        Rebuild();
    }

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
        // 拖拽期间 ViewChanged 每帧都发；只有选中元素/描述符集合真的变了才重建，否则编辑框会被反复销毁。
        if (_rebuilding)
            return;

        var properties = _kernel.SelectedProperties;
        var signature = properties is null ? string.Empty : BuildSignature(properties);
        if (signature != _signature)
            Rebuild();
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
                    Foreground = Brushes.Gray,
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
                FontWeight = FontWeight.Bold,
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
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.SteelBlue,
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
    Control BuildActionBar(ElementProperties properties)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var action in properties.Actions)
        {
            var button = new Button
            {
                Content = action.DisplayName,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 2),
            };
            button.Click += (_, _) => RunAction(action);
            panel.Children.Add(button);
        }

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

        Rebuild();
    }

    Control BuildRow(PaperElement element, PropertyDescriptor descriptor)
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
            ColumnDefinitions = new ColumnDefinitions("132,*"),
            RowDefinitions = new RowDefinitions("Auto"),
        };
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    Control BuildEditor(PaperElement element, PropertyDescriptor descriptor)
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
                check.IsCheckedChanged += (_, _) =>
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
                    SelectedItem = descriptor.GetValue(element)?.ToString(),
                    IsEnabled = !descriptor.ReadOnly,
                };
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
                if (e.Key != Key.Enter)
                    return;

                e.Handled = true;
                TryCommit(element.Id, descriptor, parse, box.Text);
            };
        }

        return box;
    }

    void TryCommit(string elementId, PropertyDescriptor descriptor, Func<string, object?> parse, string? text)
    {
        object? value;
        try
        {
            value = parse(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
            Rebuild();
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
            ShowMessage(ex.Message);
        }

        Rebuild();
    }

    void ShowMessage(string? message)
    {
        _message.Text = message ?? string.Empty;
        _message.IsVisible = message is not null;
    }

    static string BuildSignature(ElementProperties properties)
        => properties.Element.Id + "|" + string.Join("|", properties.Descriptors.Select(d => d.Key));

    static object? ParseNumber(PropertyDescriptor descriptor, string text)
    {
        var trimmed = text.Trim();

        if (descriptor.Kind == PropertyKind.Integer)
        {
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                throw new FormatException($"“{text}”不是整数。");

            var clamped = (double)integer;
            if (descriptor.Min is { } min)
                clamped = Math.Max(clamped, min);
            if (descriptor.Max is { } max)
                clamped = Math.Min(clamped, max);
            return (int)clamped;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
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
            return null; // 可空颜色字段的“清空”语义

        if (!SKColor.TryParse(trimmed, out var color))
            throw new FormatException("颜色需形如 #RRGGBB 或 #AARRGGBB。");

        return color;
    }

    static object? ParseDash(string text)
    {
        var parts = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
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
        double[] dash => string.Join(",", dash.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))),
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
