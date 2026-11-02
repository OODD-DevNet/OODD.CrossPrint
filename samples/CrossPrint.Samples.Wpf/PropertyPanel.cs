using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.Wpf;

/// <summary>
/// 数据驱动的属性面板：完全按 <see cref="ElementProperties.Descriptors"/> 生成表单，
/// 不写死任何字段名，八种元素类型共用这一份代码。
/// </summary>
/// <remarks>
/// 刷新纪律：选中集合变化才重建控件；同一选中下（拖拽、缩放、层次调整）只回填值，
/// 避免每次 <see cref="EditorKernel.ViewChanged"/> 都把正在输入的控件拆掉。
/// 属性写回只走 <see cref="EditorKernel.SetSelectedProperty(string, object?)"/>，异常就地显示。
/// </remarks>
internal sealed class PropertyPanel : UserControl
{
    const double LabelColumnWidth = 128;

    EditorKernel _kernel;
    readonly Grid _form = new();
    readonly TextBlock _header = new();
    readonly TextBlock _error = new();
    readonly List<PropertyRow> _rows = new();
    readonly Dictionary<string, Control> _editors = new(StringComparer.Ordinal);

    bool _building;
    bool _rebuildQueued;
    string _selectionKey = string.Empty;

    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;

        // 固定标签列 + 星号编辑器列：选中元素后属性编辑器必须完整可见、随面板宽度拉伸。
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _header.FontWeight = FontWeights.SemiBold;
        _header.Margin = new Thickness(10, 10, 10, 2);

        _error.Foreground = Brushes.Firebrick;
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
            Content = _form,
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(scroll);
        Content = root;

        Background = Brushes.White;
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

        // 同一选中：拖拽/缩放/层级变化只回填值，不重建（否则正在输入的框会被拆掉）。
        if (_building)
            return;

        foreach (var row in _rows)
            row.Refresh();
    }

    void Rebuild()
    {
        if (_building)
            return;

        // 重建会换掉全部控件；记住当前焦点属于哪个属性，重建完再还回去（提交后重建不丢焦点）。
        string? focusedKey = null;
        foreach (var row in _rows)
        {
            if (row.Editor.IsKeyboardFocusWithin)
            {
                focusedKey = row.Descriptor.Key;
                break;
            }
        }

        _building = true;
        try
        {
            _rows.Clear();
            _editors.Clear();
            _form.Children.Clear();
            _form.RowDefinitions.Clear();
            _selectionKey = SelectionKey();

            var properties = _kernel.SelectedProperties;
            if (properties is null)
            {
                _header.Text = _kernel.Selection.IsEmpty
                    ? "属性"
                    : $"属性（已选中 {_kernel.Selection.Ids.Count} 个）";

                _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var hint = new TextBlock
                {
                    Text = _kernel.Selection.IsEmpty
                        ? "点击画布元素编辑属性；右键画布打开菜单；纸张大小在纸张栏设置。"
                        : "属性面板仅支持单选；请在画布上只选中一个元素。",
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10, 4, 10, 10),
                };
                Grid.SetRow(hint, 0);
                Grid.SetColumn(hint, 0);
                Grid.SetColumnSpan(hint, 2);
                _form.Children.Add(hint);
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
                    AddGroupHeader(group);
                }

                AddRow(properties.Element, descriptor);
            }
        }
        finally
        {
            _building = false;
        }

        if (focusedKey is not null && _editors.TryGetValue(focusedKey, out var editor) && editor.IsVisible)
            editor.Focus();
    }

    /// <summary>结构动作按钮行（表格列的增删）：点击交给内核，成败都重建面板（动作清单是快照）。</summary>
    void AddActionBar(ElementProperties properties)
    {
        var panel = new WrapPanel { Margin = new Thickness(10, 2, 10, 4) };
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

        var rowIndex = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(panel, rowIndex);
        Grid.SetColumn(panel, 0);
        Grid.SetColumnSpan(panel, 2);
        _form.Children.Add(panel);
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
        finally
        {
            ScheduleRebuild();
        }
    }

    void AddGroupHeader(string text)
    {
        var rowIndex = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.SteelBlue,
            Margin = new Thickness(10, 10, 10, 2),
        };
        Grid.SetRow(header, rowIndex);
        Grid.SetColumn(header, 0);
        Grid.SetColumnSpan(header, 2);
        _form.Children.Add(header);
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
                    MaxHeight = multiline ? 160 : double.PositiveInfinity,
                    VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, text => text);
                    box.KeyDown += (_, e) =>
                    {
                        // 多行框的回车用于换行；Ctrl+Enter 才提交（表格行/正文都要能输入换行）。
                        if (e.Key == Key.Enter && (!multiline || (Keyboard.Modifiers & ModifierKeys.Control) != 0))
                        {
                            CommitTextBox(box, descriptor, text => text);
                            e.Handled = true;
                        }
                    };
                }

                refresh = () =>
                {
                    if (box.IsKeyboardFocusWithin)
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
                        if (e.Key == Key.Enter)
                        {
                            CommitTextBox(box, descriptor, text => ParseNumber(text, descriptor));
                            e.Handled = true;
                        }
                    };
                }

                refresh = () =>
                {
                    if (box.IsKeyboardFocusWithin)
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
                        if (!_building)
                            CommitValue(descriptor, true);
                    };
                    check.Unchecked += (_, _) =>
                    {
                        if (!_building)
                            CommitValue(descriptor, false);
                    };
                }

                refresh = () =>
                {
                    if (!check.IsKeyboardFocusWithin)
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
                        if (_building || combo.SelectedItem is not string name)
                            return;

                        CommitValue(descriptor, name);
                    };
                }

                refresh = () =>
                {
                    if (!combo.IsKeyboardFocusWithin)
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
                    ToolTip = "#RRGGBB 或 #AARRGGBB；留空表示无颜色",
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, ParseColor);
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            CommitTextBox(box, descriptor, ParseColor);
                            e.Handled = true;
                        }
                    };
                }

                refresh = () =>
                {
                    if (box.IsKeyboardFocusWithin)
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
                    ToolTip = "逗号分隔的段长（mm），如 1.5,1；留空表示实线",
                };
                box.Tag = box.Text;

                if (!descriptor.ReadOnly)
                {
                    box.LostFocus += (_, _) => CommitTextBox(box, descriptor, ParseDash);
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            CommitTextBox(box, descriptor, ParseDash);
                            e.Handled = true;
                        }
                    };
                }

                refresh = () =>
                {
                    if (box.IsKeyboardFocusWithin)
                        return;

                    var text = FormatDash(descriptor.GetValue(element));
                    box.Text = text;
                    box.Tag = text;
                };

                editor = box;
                break;
            }

            case PropertyKind.StyleRef:
            {
                const string none = "（不引用）";
                var items = new List<string> { none };
                var idByDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var style in _kernel.Template.Styles)
                {
                    var display = $"{style.Name}（{style.Id}）";
                    if (idByDisplay.TryAdd(display, style.Id))
                        items.Add(display);
                }

                var combo = new ComboBox
                {
                    ItemsSource = items,
                    IsEnabled = !descriptor.ReadOnly,
                    ToolTip = "引用模板样式库；样式作默认值打底，本元素内联已设置的值优先。",
                };

                string DisplayOf(string? id) => string.IsNullOrEmpty(id)
                    ? none
                    : _kernel.Template.Styles.FirstOrDefault(s => s.Id == id) is { } found
                        ? $"{found.Name}（{found.Id}）"
                        : id;
                combo.SelectedItem = DisplayOf(descriptor.GetValue(element) as string);

                if (!descriptor.ReadOnly)
                {
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_building || combo.SelectedItem is not string display)
                            return;

                        CommitValue(descriptor, display == none ? null : idByDisplay.GetValueOrDefault(display, display));
                    };
                }

                // 下拉旁的「…」直达样式库管理：库里没样式时这是最短路径。
                var manageButton = new Button
                {
                    Content = "…",
                    Width = 24,
                    Padding = new Thickness(0),
                    ToolTip = "管理模板样式库（新增/编辑/删除样式）",
                };
                manageButton.Click += (_, _) =>
                {
                    var owner = Window.GetWindow(this);
                    var dialog = new StyleLibraryDialog(_kernel);
                    if (owner is not null)
                        dialog.Owner = owner;

                    dialog.ShowDialog();
                    Rebuild();
                };

                refresh = () =>
                {
                    if (!combo.IsKeyboardFocusWithin)
                        combo.SelectedItem = DisplayOf(descriptor.GetValue(element) as string);
                };

                editor = new ContentControl
                {
                    Content = new DockPanel { LastChildFill = true, Children = { manageButton, combo } },
                    IsTabStop = false,
                    Focusable = false,
                };
                DockPanel.SetDock(manageButton, Dock.Right);
                break;
            }

            default:
                return;
        }

        editor.ToolTip ??= descriptor.Key;
        _editors[descriptor.Key] = editor;
        _rows.Add(new PropertyRow(descriptor, editor, refresh));

        var rowIndex = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = descriptor.DisplayName,
            FontSize = 11,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 4, 8, 4),
            ToolTip = descriptor.Key + (descriptor.ReadOnly ? "（只读）" : string.Empty),
        };
        Grid.SetRow(label, rowIndex);
        Grid.SetColumn(label, 0);
        _form.Children.Add(label);

        editor.Margin = new Thickness(0, 3, 10, 3);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = descriptor.Kind == PropertyKind.MultilineText
            ? VerticalAlignment.Stretch
            : VerticalAlignment.Center;
        Grid.SetRow(editor, rowIndex);
        Grid.SetColumn(editor, 1);
        _form.Children.Add(editor);
    }

    void CommitTextBox(TextBox box, PropertyDescriptor descriptor, Func<string, object?> convert)
    {
        if (_building)
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
        finally
        {
            // 提交后重建，显示规范化后的值（夹紧、枚举转名、颜色缩写）；延迟到布局空闲，避免在
            // LostFocus 里拆掉刚拿到焦点的控件。
            ScheduleRebuild();
        }
    }

    void ShowError(Exception ex)
    {
        _error.Text = ex.Message;
        _error.Visibility = Visibility.Visible;
    }

    void ScheduleRebuild()
    {
        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }));
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

    sealed class PropertyRow(PropertyDescriptor descriptor, Control editor, Action refresh)
    {
        public PropertyDescriptor Descriptor { get; } = descriptor;

        public Control Editor { get; } = editor;

        public Action Refresh { get; } = refresh;
    }
}
