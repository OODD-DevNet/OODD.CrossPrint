using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.I18n;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 数据驱动的属性面板：按选中元素的描述符动态生成表单，提交统一走 Kernel.SetSelectedProperty。
/// </summary>
public sealed class PropertyPanel : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 6, Margin = new Thickness(8) };

    /// <summary>属性行根容器（headless 测试按标签定位行用）。</summary>
    public global::Avalonia.Controls.Controls Rows => _rows.Children;
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

    /// <summary>
    /// 无选中元素时空白区的附加内容构造器（如「纸张 + 水印」设置区）。
    /// 每次 Rebuild 空白态时调用一次；返回 null/不设置则只显示提示文案。
    /// </summary>
    public Func<Control>? EmptyStateContentBuilder { get; set; }

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

    /// <summary>公开强制重建入口：直接改模型（Selection.Select/元素字段等绕过命令的写入）后，宿主或测试可显式刷新（签名比对会跳过"选中未变"的重建）。</summary>
    public void Refresh() => Rebuild();

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
                    Text = Localizer.T("Property.Hint.Empty"),
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                if (EmptyStateContentBuilder?.Invoke() is { } extra)
                    _rows.Children.Add(extra);
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
            var columnsHead = ElementTypeKeys.Table + ".columns[";
            foreach (var descriptor in properties.Descriptors)
            {
                if (descriptor.Hidden)
                    continue;

                // 逐列展开的字段（table.columns[i].*）不再平铺：改由下面的列列表统一管理。
                if (descriptor.Key.StartsWith(columnsHead, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    group = descriptor.Group;
                    _rows.Children.Add(new TextBlock
                    {
                        Text = PropertyText.Group(group),
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.SteelBlue,
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                }

                _rows.Children.Add(BuildRow(properties.Element, descriptor));
            }

            if (properties.Element is TableElement table)
                _rows.Children.Add(BuildColumnsSection(properties, table));
        }
        finally
        {
            _rebuilding = false;
        }
    }

    static string DescribeElement(PaperElement element) => PropertyText.ElementType(element.TypeKey, element switch
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
    });

    /// <summary>结构动作按钮行：点击交给内核，成败都重建面板（动作清单是快照）。列操作收进列列表右键菜单，这里不再铺按钮。</summary>
    Control BuildActionBar(ElementProperties properties)
    {
        var columnsPrefix = ElementTypeKeys.Table + ".columns[";
        var appendColumnKey = ElementTypeKeys.Table + ".appendColumn";
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var action in properties.Actions)
        {
            if (action.Key == appendColumnKey || action.Key.StartsWith(columnsPrefix, StringComparison.Ordinal))
                continue;

            var button = new Button
            {
                Content = PropertyText.Action(action.DisplayName),
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
            ShowMessage(Localizer.T("Property.Error.Action", PropertyText.Action(action.DisplayName), ex.Message));
        }

        Rebuild();
    }

    Control BuildRow(PaperElement element, PropertyDescriptor descriptor)
    {
        // 标签只留主干（「（/(/：/:」前的部分），完整说明放进右侧 ⓘ 的悬停提示，行行干净。
        var full = PropertyText.Label(descriptor.DisplayName);
        var shortLabel = ShortLabel(full);

        var labelBlock = new TextBlock
        {
            Text = shortLabel,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var labelCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = descriptor.Kind == PropertyKind.MultilineText
                ? VerticalAlignment.Top
                : VerticalAlignment.Center,
        };
        labelCell.Children.Add(labelBlock);
        if (!string.Equals(shortLabel, full, StringComparison.Ordinal))
        {
            var tip = new TextBlock
            {
                Text = "ⓘ",
                FontSize = 13,
                Foreground = Brushes.SteelBlue,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            ToolTip.SetTip(tip, full);
            labelCell.Children.Add(tip);
        }

        var editor = BuildEditor(element, descriptor);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("132,*"),
            RowDefinitions = new RowDefinitions("Auto"),
        };
        grid.Children.Add(labelCell);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>取「（/(/：/:」之前的部分作为短标签；无分隔符原样返回。</summary>
    static string ShortLabel(string label)
    {
        var cut = label.IndexOfAny(['（', '(', '：', ':']);
        return cut > 0 ? label[..cut] : label;
    }

    Control BuildEditor(PaperElement element, PropertyDescriptor descriptor)
    {
        // 「测试数据」行（String/多行共用）显示画布当前生效值（元素测试数据 > 模板设计数据/宿主演示数据），
        // 与画布所见一致；编辑即固化为元素测试数据，清空则回落显示数据源的值。
        var initial = FormatValue(descriptor.GetValue(element));
        if (descriptor.Key == "testData" && string.IsNullOrEmpty(initial))
        {
            var effective = EffectiveBoundValue(element, CanvasDataProvider);
            if (!string.IsNullOrEmpty(effective))
                initial = effective;
        }

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
                // 值语义仍是枚举 CLR 名（Options 原样、SelectedItem 原样），
                // 显示用 ItemTemplate 翻译——中文界面形状种类显示"等腰三角形"而不是"Triangle"。
                var combo = new ComboBox
                {
                    ItemsSource = descriptor.Options,
                    SelectedItem = descriptor.GetValue(element)?.ToString(),
                    IsEnabled = !descriptor.ReadOnly,
                    ItemTemplate = new FuncDataTemplate<string>(
                        (name, _) => new TextBlock { Text = PropertyText.Option(descriptor.Key, name ?? string.Empty) },
                        true),
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
                return BuildColorEditor(element, descriptor);

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
                    initial,
                    text => text,
                    commitOnEnter: false);

            case PropertyKind.StyleRef:
                return BuildStyleRefEditor(element, descriptor);

            default:
            {
                return CreateTextBox(
                    element,
                    descriptor,
                    initial,
                    text => text,
                    commitOnEnter: true);
            }
        }
    }

    /// <summary>
    /// 画布当前生效数据的提供者（元素测试数据 > 模板设计数据 > 宿主演示数据的合并结果）。
    /// 由宿主（MainWindow）注入；null = 拿不到，「测试数据」行只显示元素自身的测试数据。
    /// </summary>
    public Func<object?>? CanvasDataProvider { get; set; }

    /// <summary>
    /// 「测试数据」行的画布生效值：解析元素的绑定路径（或内容里的首个 <c>{{字段}}</c>），
    /// 复用引擎同一个 BindText 解析器——保证与画布渲染逐字一致。
    /// </summary>
    static string? EffectiveBoundValue(PaperElement element, Func<object?>? dataProvider)
    {
        if (dataProvider is null)
            return null;

        var path = element.Binding;
        if (string.IsNullOrWhiteSpace(path))
            path = FirstPlaceholder(element);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var expression = path.Contains("{{", StringComparison.Ordinal)
            ? path
            : "{{" + path.Trim() + "}}";
        var value = CrossPrint.Engine.DataBinder.BindText(
            expression, dataProvider(), new RenderReport(), element.Id);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>元素内容里第一个 <c>{{字段}}</c> 的路径；无占位符返回 null。</summary>
    static string? FirstPlaceholder(PaperElement element) => element.TypeKey switch
    {
        ElementTypeKeys.Text => FirstMatch(((TextElement)element).Text.Content),
        ElementTypeKeys.QrCode => FirstMatch(((QrCodeElement)element).QrCode.Content),
        ElementTypeKeys.Barcode => FirstMatch(((BarcodeElement)element).Barcode.Content),
        ElementTypeKeys.Shape => null,
        _ => null,
    };

    static string? FirstMatch(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var match = PlaceholderPattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    static readonly Regex PlaceholderPattern = new(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// 颜色编辑器：hex 文本框 + 色块按钮，点按钮直接弹出取色面板本体（ColorView，无 ColorPicker 的下拉外壳）。
    /// 面板里拖选实时更新文本框与色块，关闭面板时才提交（拖一次颜色不会灌满撤销栈）。
    /// </summary>
    Control BuildColorEditor(PaperElement element, PropertyDescriptor descriptor)
    {
        var box = CreateTextBox(
            element,
            descriptor,
            FormatValue(descriptor.GetValue(element)),
            ParseColor,
            commitOnEnter: true);

        if (descriptor.ReadOnly)
            return box;

        var initial = descriptor.GetValue(element) is SKColor color ? color : (SKColor?)null;

        var swatch = new Button
        {
            Width = 32,
            MinHeight = 0,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.LightGray,
            Background = new SolidColorBrush(initial is { } c ? Color.FromRgb(c.Red, c.Green, c.Blue) : Colors.White),
        };
        ToolTip.SetTip(swatch, "点击打开取色面板");

        var picker = new global::Avalonia.Controls.ColorView
        {
            IsAlphaEnabled = true,
            Color = initial is { } c2 ? Color.FromRgb(c2.Red, c2.Green, c2.Blue) : Colors.White,
        };
        // 用 Button.Flyout 而不是裸 Popup：Flyout 由按钮自带锚点、自动开合与轻触关闭；
        // 裸 Popup（哪怕补了 PlacementTarget）在部分宿主路径下依旧静默不显示（真机实测 2026-11-04）。
        // ColorView 是面板本体（无 ColorPicker 的「色块+下拉」外壳），点色块一次直达取色。
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = picker,
        };
        swatch.Flyout = flyout;

        picker.ColorChanged += (_, e) =>
        {
            swatch.Background = new SolidColorBrush(e.NewColor);
            box.Text = e.NewColor.A == 255
                ? $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"
                : $"#{e.NewColor.A:X2}{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        };
        // 关面板才提交：拖色时 Text 一直在变，逐帧提交会把撤销栈灌满。
        flyout.Closed += (_, _) => TryCommit(element.Id, descriptor, ParseColor, box.Text);

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        grid.Children.Add(box);
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(swatch);
        return grid;
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
            // 单行框与 ComboBox 等高（Fluent ≈32），多行另说；行行对齐才不显乱。
            MinHeight = multiline ? 72 : 32,
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
            // 解析失败不重建：重建会把用户刚输的文本冲掉，留在框里改才对。
            ShowMessage(ex.Message);
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

        // 提交成功后不再整块重建：重建会销毁全部编辑器（焦点丢失、下一个要点到的控件被换掉），
        // 表现为"改一个属性面板就不能用了，要重选元素才恢复"。值与描述符集合都没变，控件保持原样。
    }

    void ShowMessage(string? message)
    {
        _message.Text = message ?? string.Empty;
        _message.IsVisible = message is not null;
    }

    /// <summary>样式引用编辑器：下拉枚举模板样式库（按显示名展示、按 id 提交），首项"（不引用）"提交 null。</summary>
    Control BuildStyleRefEditor(PaperElement element, PropertyDescriptor descriptor)
    {
        var current = descriptor.GetValue(element) as string;
        var items = new List<StyleItem> { new(null, "（不引用）") };
        foreach (var style in _kernel.Template.Styles)
            items.Add(new StyleItem(style.Id, $"{style.Name}（{style.Id}）"));

        var combo = new ComboBox
        {
            ItemsSource = items,
            IsEnabled = !descriptor.ReadOnly,
            SelectedItem = items.FirstOrDefault(item => string.Equals(item.Id, current, StringComparison.Ordinal))
                ?? items[0],
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || combo.SelectedItem is not StyleItem picked)
                return;

            Commit(element.Id, descriptor, picked.Id);
        };
        return combo;
    }

    sealed record StyleItem(string? Id, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>表格列列表：一行一列，右键菜单插入/移动/删除/编辑，双击进编辑弹窗。</summary>
    Control BuildColumnsSection(ElementProperties properties, TableElement table)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "列（右键：插入/移动/删除/编辑，双击编辑）",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.SteelBlue,
        });

        var count = table.Table.Columns.Count;
        var list = new ListBox { MaxHeight = 220 };
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var column = table.Table.Columns[index];
            var widthText = column.WidthMm < 0 ? "均分" : $"{column.WidthMm:0.##}mm";
            var fieldText = string.IsNullOrEmpty(column.Field) ? string.Empty : $" · {column.Field}";
            var item = new ListBoxItem
            {
                Content = $"列{index + 1} · {column.Title} · {widthText}{fieldText}",
                Padding = new Thickness(6, 2),
                ContextMenu = BuildColumnMenu(properties, index, count),
            };
            item.DoubleTapped += (_, _) => OpenColumnEditor(properties, index);
            list.Items.Add(item);
        }

        panel.Children.Add(list);
        return panel;
    }

    ContextMenu BuildColumnMenu(ElementProperties properties, int index, int count)
    {
        var key = ElementTypeKeys.Table + ".";
        var menu = new ContextMenu();

        void Item(string header, bool enabled, Action click)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        Item("在上方插入", true, () => RunColumnAction($"{key}columns[{index}].insertBefore"));
        Item("在下方插入", true, () => RunColumnAction(
            index + 1 < count ? $"{key}columns[{index + 1}].insertBefore" : $"{key}appendColumn"));
        Item("上移", index > 0, () => RunColumnAction($"{key}columns[{index}].moveUp"));
        Item("下移", index < count - 1, () => RunColumnAction($"{key}columns[{index}].moveDown"));
        menu.Items.Add(new Separator());
        Item("删除", true, () => RunColumnAction($"{key}columns[{index}].remove"));
        Item("编辑…", true, () => OpenColumnEditor(properties, index));
        return menu;
    }

    void RunColumnAction(string actionKey)
    {
        try
        {
            _kernel.RunElementAction(actionKey);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }

        Rebuild();
    }

    /// <summary>列编辑弹窗：该列全部字段集中在一个模态窗里编辑，关窗后主面板刷新列列表。</summary>
    void OpenColumnEditor(ElementProperties properties, int index)
    {
        var head = ElementTypeKeys.Table + ".columns[" + index + "].";
        var descriptors = properties.Descriptors
            .Where(d => d.Key.StartsWith(head, StringComparison.Ordinal) && !d.Hidden)
            .ToList();
        if (descriptors.Count == 0)
            return;

        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        foreach (var descriptor in descriptors)
            rows.Children.Add(BuildRow(properties.Element, descriptor));

        var close = new Button
        {
            Content = "关闭",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        close.Click += (_, _) => ModalDialog.CloseHost(close);

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        _ = ModalDialog.ShowAsync(this, $"编辑列{index + 1}", root, 560, 620, onClosed: Rebuild);
    }

    static string BuildSignature(ElementProperties properties)
        => properties.Element.Id + "|" + string.Join("|", properties.Descriptors.Select(d => d.Key));

    static object? ParseNumber(PropertyDescriptor descriptor, string text)
    {
        var trimmed = text.Trim();

        if (descriptor.Kind == PropertyKind.Integer)
        {
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                throw new FormatException(Localizer.T("Property.Error.Integer", text));

            var clamped = (double)integer;
            if (descriptor.Min is { } min)
                clamped = Math.Max(clamped, min);
            if (descriptor.Max is { } max)
                clamped = Math.Min(clamped, max);
            return (int)clamped;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException(Localizer.T("Property.Error.Number", text));

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
            throw new FormatException(Localizer.T("Property.Error.Color"));

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
                throw new FormatException(Localizer.T("Property.Error.Dash", parts[i]));
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
