using System;
using System.Collections.Generic;
using System.Globalization;
using CrossPrint.Core;
using CrossPrint.Designer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;

namespace CrossPrint.Samples.Uno;

/// <summary>
/// 数据驱动的属性面板：完全按 <see cref="ElementProperties.Descriptors"/> 生成表单，
/// 不写死任何字段名，八种元素类型共用这一份代码。
/// </summary>
/// <remarks>
/// <para>
/// 刷新纪律与 WPF 示例一致：选中集合变化才重建控件；同一选中下（拖拽、缩放、层次调整）只回填值，
/// 且<b>填充中或已获得焦点的编辑器不回填</b>（WinUI 没有 WPF 的 <c>IsKeyboardFocusWithin</c>，
/// 用 <see cref="UIElement.FocusState"/> 判断即可），这样正在输入的框不会被拆掉或覆盖。
/// </para>
/// <para>
/// 属性写回只走 <see cref="EditorKernel.SetSelectedProperty(string, object?)"/>，异常就地显示。
/// 多行文本不回车上提交（回车用于换行），失焦时提交——Uno 的键盘事件不带修饰键，
/// 因此不提供 WPF 示例里的 Ctrl+Enter 提交。
/// </para>
/// </remarks>
internal sealed class PropertyPanel : UserControl
{
    EditorKernel _kernel;
    readonly StackPanel _stack = new();
    readonly TextBlock _header = new();
    readonly TextBlock _error = new();
    readonly List<PropertyRow> _rows = new();

    bool _building;
    bool _refreshing;
    string _selectionKey = string.Empty;

    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;

        _header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _header.Margin = new Thickness(10, 10, 10, 2);

        _error.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick);
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Margin = new Thickness(10, 2, 10, 6);
        _error.Visibility = Visibility.Collapsed;

        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_error);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _stack,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(scroll, 1);
        root.Children.Add(top);
        root.Children.Add(scroll);

        Content = root;
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);

        // 面板自己填满侧栏：编辑器列按星号拉伸，行不会因为对齐方式被压扁。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _kernel.ViewChanged += OnViewChanged;
        Rebuild();
    }

    /// <summary>切换到另一个内核（宿主换模板后调用）。</summary>
    public void Attach(EditorKernel kernel)
    {
        _kernel.ViewChanged -= OnViewChanged;
        _kernel = kernel;
        _kernel.ViewChanged += OnViewChanged;
        _selectionKey = string.Empty;
        Rebuild();
    }

    string SelectionKey() => string.Join("|", _kernel.Selection.Ids);

    void OnViewChanged()
    {
        var key = SelectionKey();
        if (!string.Equals(key, _selectionKey, StringComparison.Ordinal))
        {
            _error.Text = string.Empty;
            _error.Visibility = Visibility.Collapsed;
            Rebuild();
            return;
        }

        // 同一选中：只回填值，不重建。
        if (_building)
            return;

        _refreshing = true;
        try
        {
            foreach (var row in _rows)
                row.Refresh();
        }
        finally
        {
            _refreshing = false;
        }
    }

    void Rebuild()
    {
        if (_building)
            return;

        _building = true;
        try
        {
            _rows.Clear();
            _stack.Children.Clear();
            _selectionKey = SelectionKey();

            var properties = _kernel.SelectedProperties;
            if (properties is null)
            {
                _header.Text = _kernel.Selection.IsEmpty
                    ? "属性"
                    : $"属性（已选中 {_kernel.Selection.Ids.Count} 个）";
                _stack.Children.Add(new TextBlock
                {
                    Text = _kernel.Selection.IsEmpty
                        ? "点击画布元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。"
                        : "已选中多个元素：点击单个元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10, 4, 10, 10),
                });
                return;
            }

            _header.Text = $"{TypeName(properties.Element.TypeKey)} · {properties.Element.Id}";
            if (properties.Actions.Count > 0)
                AddActionBar(properties);

            string? group = null;
            foreach (var descriptor in properties.Descriptors)
            {
                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    group = descriptor.Group;
                    _stack.Children.Add(new TextBlock
                    {
                        Text = group,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue),
                        Margin = new Thickness(10, 10, 10, 2),
                    });
                }

                AddRow(properties.Element, descriptor);
            }
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>结构动作按钮行（表格列的增删）：点击交给内核，成败都重建面板（动作清单是快照）。</summary>
    /// <remarks>
    /// 横向 StackPanel 不换行，而外层 ScrollViewer 的横向滚动是 <c>Disabled</c>：按钮一排超过面板宽度后，
    /// 右边的"删除列N"被裁掉又滚不出来，列数稍多的表格在界面上就删不了列。改用会换行的
    /// <see cref="VariableSizedWrapGrid"/>；它在 <c>ItemWidth=NaN</c>（默认）时按第一个子项定格宽，
    /// 第一个按钮"追加列"标签最短会把长标签裁掉，所以先量出最宽的按钮再写回 ItemWidth。
    /// </remarks>
    void AddActionBar(ElementProperties properties)
    {
        var panel = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 2, 10, 4),
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
        _stack.Children.Add(panel);
    }

    void RunAction(ElementAction action)
    {
        try
        {
            _kernel.RunElementAction(action.Key);
            _error.Text = string.Empty;
            _error.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }

        Rebuild();
    }

    void AddRow(PaperElement element, PropertyDescriptor descriptor)
    {
        Control editor;
        Action refresh;

        switch (descriptor.Kind)
        {
            case PropertyKind.Text:
            case PropertyKind.MultilineText:
            {
                var multiline = descriptor.Kind == PropertyKind.MultilineText;
                var box = new TextBox
                {
                    Text = FormatText(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                    AcceptsReturn = multiline,
                    TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = multiline ? 56 : 0,
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, text => text);
                    if (!multiline)
                    {
                        box.KeyDown += (_, e) =>
                        {
                            if (e.Key != VirtualKey.Enter)
                                return;

                            CommitTextBox(box, descriptor, text => text);
                            e.Handled = true;
                        };
                    }
                }

                refresh = () =>
                {
                    if (box.FocusState != FocusState.Unfocused)
                        return;

                    var text = FormatText(descriptor.GetValue(element));
                    box.Text = text;
                    box.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.Number:
            case PropertyKind.Integer:
            {
                var box = new TextBox
                {
                    Text = FormatNumber(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, text => ParseNumber(text, descriptor));
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key != VirtualKey.Enter)
                            return;

                        CommitTextBox(box, descriptor, text => ParseNumber(text, descriptor));
                        e.Handled = true;
                    };
                }

                refresh = () =>
                {
                    if (box.FocusState != FocusState.Unfocused)
                        return;

                    var text = FormatNumber(descriptor.GetValue(element));
                    box.Text = text;
                    box.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.Boolean:
            {
                var check = new CheckBox
                {
                    IsChecked = descriptor.GetValue(element) is bool b && b,
                    IsEnabled = !descriptor.ReadOnly,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                if (!descriptor.ReadOnly)
                {
                    check.Checked += (_, _) =>
                    {
                        if (!_building && !_refreshing)
                            CommitValue(descriptor, true);
                    };
                    check.Unchecked += (_, _) =>
                    {
                        if (!_building && !_refreshing)
                            CommitValue(descriptor, false);
                    };
                }

                refresh = () =>
                {
                    if (check.FocusState != FocusState.Unfocused)
                        return;

                    check.IsChecked = descriptor.GetValue(element) is bool b && b;
                };

                editor = check;
                break;
            }

            case PropertyKind.Enum:
            {
                var combo = new ComboBox
                {
                    ItemsSource = descriptor.Options ?? Array.Empty<string>(),
                    SelectedItem = descriptor.GetValue(element)?.ToString(),
                    IsEnabled = !descriptor.ReadOnly,
                };

                if (!descriptor.ReadOnly)
                {
                    // 订阅放在赋值之后：初始化选中项不算用户提交。
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_building || _refreshing || combo.SelectedItem is not string name)
                            return;

                        CommitValue(descriptor, name);
                    };
                }

                refresh = () =>
                {
                    if (combo.FocusState != FocusState.Unfocused)
                        return;

                    combo.SelectedItem = descriptor.GetValue(element)?.ToString();
                };

                editor = combo;
                break;
            }

            case PropertyKind.Color:
            {
                var box = new TextBox
                {
                    Text = FormatColor(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, ParseColor);
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key != VirtualKey.Enter)
                            return;

                        CommitTextBox(box, descriptor, ParseColor);
                        e.Handled = true;
                    };
                }

                refresh = () =>
                {
                    if (box.FocusState != FocusState.Unfocused)
                        return;

                    var text = FormatColor(descriptor.GetValue(element));
                    box.Text = text;
                    box.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.DashPattern:
            {
                var box = new TextBox
                {
                    Text = FormatDash(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, ParseDash);
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key != VirtualKey.Enter)
                            return;

                        CommitTextBox(box, descriptor, ParseDash);
                        e.Handled = true;
                    };
                }

                refresh = () =>
                {
                    if (box.FocusState != FocusState.Unfocused)
                        return;

                    var text = FormatDash(descriptor.GetValue(element));
                    box.Text = text;
                    box.Tag = text;
                };

                editor = box;
                break;
            }

            default:
                return;
        }

        _rows.Add(new PropertyRow(refresh));
        _stack.Children.Add(MakeRow(descriptor, editor));
    }

    void CommitTextBox(TextBox box, PropertyDescriptor descriptor, Func<string, object?> convert)
    {
        if (_building || _refreshing)
            return;

        // 文本没改就不提交：避免把显示用的舍入值写回模型。
        if (Equals(box.Tag as string, box.Text))
            return;

        object? value;
        try
        {
            value = convert(box.Text);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        CommitValue(descriptor, value);
    }

    void CommitValue(PropertyDescriptor descriptor, object? value)
    {
        try
        {
            _kernel.SetSelectedProperty(descriptor.Key, value);
            _error.Text = string.Empty;
            _error.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    void ShowError(Exception ex)
    {
        _error.Text = ex.Message;
        _error.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 两列行：标签列固定 128（约 120–140），编辑器列按星号拉伸填满剩余宽度。
    /// 之前标签压在编辑器上方的单列布局在窄侧栏里会显得编辑器"消失"，这里改成表格式对齐。
    /// </summary>
    static Grid MakeRow(PropertyDescriptor descriptor, Control editor)
    {
        var label = new TextBlock
        {
            Text = descriptor.DisplayName,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.MinWidth = 0;

        var row = new Grid { Margin = new Thickness(10, 0, 10, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(editor, 1);
        row.Children.Add(label);
        row.Children.Add(editor);
        return row;
    }

    static string TypeName(string typeKey) => typeKey switch
    {
        ElementTypeKeys.Text => "文字",
        ElementTypeKeys.Line => "线段",
        ElementTypeKeys.Rectangle => "矩形",
        ElementTypeKeys.Ellipse => "椭圆",
        ElementTypeKeys.Image => "图片",
        ElementTypeKeys.QrCode => "二维码",
        ElementTypeKeys.Barcode => "一维码",
        ElementTypeKeys.Table => "表格",
        _ => typeKey,
    };

    static string FormatText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    static string FormatNumber(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.######", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    static string FormatColor(object? value)
        => value is SKColor color ? SkColorJsonConverter.ToHex(color) : string.Empty;

    static string FormatDash(object? value)
        => value is double[] segments && segments.Length > 0
            ? string.Join(",", Array.ConvertAll(segments, v => v.ToString("0.###", CultureInfo.InvariantCulture)))
            : string.Empty;

    static object? ParseNumber(string text, PropertyDescriptor descriptor)
    {
        var trimmed = text.Trim();
        // 数值描述符可能可空（如表格列字号"留空继承表体"）；空串交给内核去区分可空/不可空。
        if (trimmed.Length == 0)
            return null;

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"“{text}”不是合法数字（请用小数点，如 12.5）。");

        // Min/Max 只是编辑器提示，内核不夹紧；面板按提示夹一次再写回。
        if (descriptor.Min is { } min && value < min)
            value = min;
        if (descriptor.Max is { } max && value > max)
            value = max;

        return descriptor.Kind == PropertyKind.Integer
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : value;
    }

    static object? ParseColor(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        if (!SKColor.TryParse(trimmed, out var color))
            throw new FormatException("颜色格式应为 #RRGGBB 或 #AARRGGBB。");

        return color;
    }

    static object? ParseDash(string text)
    {
        var parts = text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw new FormatException($"“{parts[i]}”不是合法段长（用逗号分隔，如 1.5,1）。");
        }

        return values;
    }

    sealed class PropertyRow(Action refresh)
    {
        public Action Refresh { get; } = refresh;
    }
}
