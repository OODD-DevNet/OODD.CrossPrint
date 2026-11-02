using System;
using System.Collections.Generic;
using System.Globalization;
using CrossPrint.Core;
using CrossPrint.Designer;
using Microsoft.Maui.Controls;
// FlexDirection / FlexWrap 不在 Controls 命名空间里：MAUI 8 把 Flex 布局引擎的枚举放在
// Microsoft.Maui.Layouts（漏了这条 using，下面 FlexLayout.Direction/Wrap 的赋值编不过）。
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Graphics;
using SkiaSharp;

namespace CrossPrint.Samples.Maui;

/// <summary>
/// 数据驱动的属性面板：完全按 <see cref="ElementProperties.Descriptors"/> 生成表单，
/// 不写死任何字段名，八种元素类型共用这一份代码。
/// </summary>
/// <remarks>
/// <para>
/// 刷新纪律与其它宿主一致：选中集合变化才重建控件；同一选中下（拖拽、缩放、层次调整）只回填值，
/// 且<b>填充中或已获得焦点的编辑器不回填</b>（用 <see cref="VisualElement.IsFocused"/> 判断），
/// 正在输入的框不会被拆掉或覆盖。
/// </para>
/// <para>
/// 属性写回只走 <see cref="EditorKernel.SetSelectedProperty(string, object?)"/>，异常就地显示。
/// 多行文本不回车上提交（回车用于换行），失焦时提交；单行编辑器回车即提交。
/// </para>
/// </remarks>
internal sealed class PropertyPanel : ContentView
{
    EditorKernel _kernel;
    readonly VerticalStackLayout _stack = new();
    readonly Label _header = new();
    readonly Label _error = new();
    readonly List<PropertyRow> _rows = new();

    bool _building;
    bool _refreshing;
    string _selectionKey = string.Empty;

    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;

        _header.FontAttributes = FontAttributes.Bold;
        // 右侧栏收窄后竖向空间更紧，标题与分组的上边距相应压缩。
        _header.Margin = new Thickness(10, 8, 10, 2);

        _error.TextColor = Colors.Firebrick;
        _error.LineBreakMode = LineBreakMode.WordWrap;
        _error.Margin = new Thickness(10, 2, 10, 6);
        _error.IsVisible = false;

        var top = new VerticalStackLayout { _header, _error };

        var scroll = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            Content = _stack,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(scroll, 1);
        root.Add(top);
        root.Add(scroll);

        Content = root;
        Background = new SolidColorBrush(Colors.White);

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
            _error.IsVisible = false;
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
            _stack.Clear();
            _selectionKey = SelectionKey();

            var properties = _kernel.SelectedProperties;
            if (properties is null)
            {
                _header.Text = _kernel.Selection.IsEmpty
                    ? "属性"
                    : $"属性（已选中 {_kernel.Selection.Ids.Count} 个）";
                _stack.Add(new Label
                {
                    Text = _kernel.Selection.IsEmpty
                        ? "点击画布元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。"
                        : "已选中多个元素：点击单个元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。",
                    TextColor = Colors.Gray,
                    LineBreakMode = LineBreakMode.WordWrap,
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
                    _stack.Add(new Label
                    {
                        Text = group,
                        FontSize = 12,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.SteelBlue,
                        Margin = new Thickness(10, 8, 10, 2),
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
    /// 用会换行的 <see cref="FlexLayout"/>（<c>Wrap=Wrap</c>）而不是 <c>HorizontalStackLayout</c>：
    /// 后者永远排成一行，外层滚动区只给纵向滚动，按钮一排超过面板宽度后右边的"删除列N"直接被裁掉，
    /// 列数稍多的表格在界面上就删不了列。
    /// </remarks>
    void AddActionBar(ElementProperties properties)
    {
        var panel = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            Margin = new Thickness(10, 2, 10, 4),
        };
        foreach (var action in properties.Actions)
        {
            var button = new Button
            {
                Text = action.DisplayName,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 4),
            };
            button.Clicked += (_, _) => RunAction(action);
            panel.Children.Add(button);
        }

        _stack.Add(panel);
    }

    void RunAction(ElementAction action)
    {
        try
        {
            _kernel.RunElementAction(action.Key);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }

        Rebuild();
    }

    void AddRow(PaperElement element, PropertyDescriptor descriptor)
    {
        View editor;
        Action refresh;
        PropertyRow row = new();

        switch (descriptor.Kind)
        {
            case PropertyKind.Text:
            case PropertyKind.MultilineText:
            {
                var multiline = descriptor.Kind == PropertyKind.MultilineText;

                // MAUI 的 Editor 固定多行；单行用 Entry。两者都有 Unfocused/IsFocused。
                View box = multiline
                    ? new Editor
                    {
                        Text = FormatText(descriptor.GetValue(element)),
                        IsEnabled = !descriptor.ReadOnly,
                        MinimumHeightRequest = 56,
                    }
                    : new Entry
                    {
                        Text = FormatText(descriptor.GetValue(element)),
                        IsEnabled = !descriptor.ReadOnly,
                    };
                row.Tag = GetText(box);

                if (!descriptor.ReadOnly)
                {
                    box.Unfocused += (_, _) => CommitTextBox(box, descriptor, text => text, row);
                    if (!multiline && box is Entry entry)
                    {
                        entry.Completed += (_, _) => CommitTextBox(box, descriptor, text => text, row);
                    }
                }

                refresh = () =>
                {
                    if (box.IsFocused)
                        return;

                    var text = FormatText(descriptor.GetValue(element));
                    SetText(box, text);
                    row.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.Number:
            case PropertyKind.Integer:
            {
                var box = new Entry
                {
                    Text = FormatNumber(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                row.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.Unfocused += (_, _) => CommitTextBox(box, descriptor, text => ParseNumber(text, descriptor), row);
                    box.Completed += (_, _) => CommitTextBox(box, descriptor, text => ParseNumber(text, descriptor), row);
                }

                refresh = () =>
                {
                    if (box.IsFocused)
                        return;

                    var text = FormatNumber(descriptor.GetValue(element));
                    box.Text = text;
                    row.Tag = text;
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
                    VerticalOptions = LayoutOptions.Center,
                };

                if (!descriptor.ReadOnly)
                {
                    // MAUI 的 CheckBox 只有一个 CheckedChanged 事件，args.Value 是新状态。
                    check.CheckedChanged += (_, args) =>
                    {
                        if (!_building && !_refreshing)
                            CommitValue(descriptor, args.Value);
                    };
                }

                refresh = () =>
                {
                    if (check.IsFocused)
                        return;

                    check.IsChecked = descriptor.GetValue(element) is bool b && b;
                };

                editor = check;
                break;
            }

            case PropertyKind.Enum:
            {
                var combo = new Picker
                {
                    // MAUI 的 Picker.ItemsSource 是 IList；Options 是 IReadOnlyList，拷一份。
                    ItemsSource = descriptor.Options?.ToList() ?? new List<string>(),
                    SelectedItem = descriptor.GetValue(element)?.ToString(),
                    IsEnabled = !descriptor.ReadOnly,
                };

                if (!descriptor.ReadOnly)
                {
                    // 订阅放在赋值之后：初始化选中项不算用户提交。
                    combo.SelectedIndexChanged += (_, _) =>
                    {
                        if (_building || _refreshing || combo.SelectedItem is not string name)
                            return;

                        CommitValue(descriptor, name);
                    };
                }

                refresh = () =>
                {
                    if (combo.IsFocused)
                        return;

                    combo.SelectedItem = descriptor.GetValue(element)?.ToString();
                };

                editor = combo;
                break;
            }

            case PropertyKind.Color:
            {
                var box = new Entry
                {
                    Text = FormatColor(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                row.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.Unfocused += (_, _) => CommitTextBox(box, descriptor, ParseColor, row);
                    box.Completed += (_, _) => CommitTextBox(box, descriptor, ParseColor, row);
                }

                refresh = () =>
                {
                    if (box.IsFocused)
                        return;

                    var text = FormatColor(descriptor.GetValue(element));
                    box.Text = text;
                    row.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.DashPattern:
            {
                var box = new Entry
                {
                    Text = FormatDash(descriptor.GetValue(element)),
                    IsEnabled = !descriptor.ReadOnly,
                };
                row.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.Unfocused += (_, _) => CommitTextBox(box, descriptor, ParseDash, row);
                    box.Completed += (_, _) => CommitTextBox(box, descriptor, ParseDash, row);
                }

                refresh = () =>
                {
                    if (box.IsFocused)
                        return;

                    var text = FormatDash(descriptor.GetValue(element));
                    box.Text = text;
                    row.Tag = text;
                };

                editor = box;
                break;
            }

            default:
                return;
        }

        row.Refresh = refresh;
        _rows.Add(row);
        _stack.Add(MakeRow(descriptor, editor));
    }

    static string? GetText(VisualElement box) => box switch
    {
        Entry entry => entry.Text,
        Editor editor => editor.Text,
        _ => null,
    };

    static void SetText(VisualElement box, string text)
    {
        switch (box)
        {
            case Entry entry:
                entry.Text = text;
                break;
            case Editor editor:
                editor.Text = text;
                break;
        }
    }

    void CommitTextBox(VisualElement box, PropertyDescriptor descriptor, Func<string, object?> convert, PropertyRow row)
    {
        if (_building || _refreshing)
            return;

        var text = GetText(box) ?? string.Empty;

        // 文本没改就不提交：避免把显示用的舍入值写回模型。
        if (string.Equals(row.Tag as string, text, StringComparison.Ordinal))
            return;

        object? value;
        try
        {
            value = convert(text);
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
            _error.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    void ShowError(Exception ex)
    {
        _error.Text = ex.Message;
        _error.IsVisible = true;
    }

    /// <summary>
    /// 两列行：标签列固定 128（约 120–140），编辑器列按星号拉伸填满剩余宽度。
    /// 之前标签压在编辑器上方的单列布局在窄侧栏里会显得编辑器"消失"，这里改成表格式对齐。
    /// </summary>
    static Grid MakeRow(PropertyDescriptor descriptor, View editor)
    {
        var label = new Label
        {
            Text = descriptor.DisplayName,
            FontSize = 12,
            TextColor = Colors.DimGray,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        editor.HorizontalOptions = LayoutOptions.Fill;
        editor.VerticalOptions = LayoutOptions.Center;

        var row = new Grid { Margin = new Thickness(10, 0, 10, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(editor, 1);
        row.Add(label);
        row.Add(editor);
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

    /// <summary>一行 = 回填委托 + 提交快照（相当于 WinUI TextBox.Tag 的换行存储）。</summary>
    sealed class PropertyRow
    {
        public Action Refresh { get; set; } = static () => { };

        public object? Tag { get; set; }
    }
}
