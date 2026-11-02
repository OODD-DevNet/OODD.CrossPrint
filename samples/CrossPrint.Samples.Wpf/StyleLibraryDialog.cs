using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.Wpf;

/// <summary>
/// 模板样式库编辑对话框：左侧样式清单（增删），右侧选中样式的字段表单。
/// 所有变更都走 <see cref="EditorKernel"/> 的命令体系（AddStyle/RemoveStyle/UpdateStyle），
/// 撤销栈与画布刷新与普通编辑同源；删除有引用的样式由内核拦截并把引用者显示出来。
/// 可空字段一律"留空 = 不设置"：样式只补内联没设置的字段（CSS 同款覆盖口径）。
/// </summary>
internal sealed class StyleLibraryDialog : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const string Unset = "（不设置）";

    readonly EditorKernel _kernel;
    readonly ListBox _list = new();
    readonly Button _deleteButton = new() { Content = "删除" };
    readonly Grid _form = new();
    readonly TextBlock _usage = new();
    readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    // 全部字段控件（重建时换掉；字典键只用于焦点恢复，这里不需要）。
    readonly Dictionary<string, FrameworkElement> _fields = new(StringComparer.Ordinal);
    bool _loading;

    public StyleLibraryDialog(EditorKernel kernel)
    {
        _kernel = kernel;

        Title = "模板样式库";
        Width = 680;
        Height = 560;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 左列：清单与增删；右列：选中样式的表单。
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        buttons.Children.Add(new Button { Content = "新增", Padding = new Thickness(10, 2, 10, 2) });
        ((Button)buttons.Children[0]).Click += (_, _) => AddStyle();
        _deleteButton.Padding = new Thickness(10, 2, 10, 2);
        _deleteButton.Margin = new Thickness(6, 0, 0, 0);
        _deleteButton.Click += (_, _) => RemoveSelected();
        buttons.Children.Add(_deleteButton);
        DockPanel.SetDock(buttons, Dock.Top);

        _list.DisplayMemberPath = nameof(StyleItem.Display);
        _list.SelectionChanged += (_, _) => LoadSelected();
        left.Children.Add(buttons);
        left.Children.Add(_list);

        BuildForm();
        _usage.Foreground = Brushes.DimGray;
        _usage.TextWrapping = TextWrapping.Wrap;
        _usage.Margin = new Thickness(0, 8, 0, 0);
        _error.Margin = new Thickness(0, 4, 0, 0);

        var right = new DockPanel { LastChildFill = true };
        var formScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new DockPanel { LastChildFill = true, Children = { _form } },
        };
        DockPanel.SetDock(_usage, Dock.Bottom);
        right.Children.Add(_usage);
        right.Children.Add(formScroll);

        var rootGrid = new Grid { Margin = new Thickness(14) };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootGrid.Children.Add(left);
        Grid.SetColumn(left, 0);
        rootGrid.Children.Add(right);
        Grid.SetColumn(right, 1);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(_error);
        root.Children.Add(rootGrid);
        Content = root;

        ReloadList(keepId: null);
    }

    /// <summary>下拉框的条目（Name + id，模板内展示与反查都用它）。</summary>
    sealed record StyleItem(string Id, string Name)
    {
        public string Display => $"{Name}（{Id}）";
    }

    // ---------------- 清单管理 ----------------

    void AddStyle()
    {
        Run(() =>
        {
            var style = _kernel.AddStyle();
            ReloadList(keepId: style.Id);
        });
    }

    void RemoveSelected()
    {
        if (_list.SelectedItem is not StyleItem item)
            return;

        Run(() =>
        {
            _kernel.RemoveStyle(item.Id);
            ReloadList(keepId: null);
        });
    }

    void ReloadList(string? keepId)
    {
        _loading = true;
        try
        {
            _list.ItemsSource = _kernel.Template.Styles
                .Select(s => new StyleItem(s.Id, s.Name))
                .ToList();
            _list.SelectedItem = keepId is null
                ? _list.Items.Count > 0 ? _list.Items[0] : null
                : _list.Items.OfType<StyleItem>().FirstOrDefault(i => i.Id == keepId);
        }
        finally
        {
            _loading = false;
        }

        LoadSelected();
    }

    // ---------------- 表单 ----------------

    void BuildForm()
    {
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        Row(row++, Label("名称"), TextField("name"), Label("字体"), TextField("fontFamily"));
        Row(row++, Label("字号 (pt)"), NumberField("fontSizePt"), Label("字间距 (pt)"), NumberField("letterSpacingPt"));
        Row(row++, Label("加粗"), TriState("bold"), Label("倾斜"), TriState("italic"));
        Row(row++, Label("下划线"), TriState("underline"), Label("删除线"), TriState("strikethrough"));
        Row(row++, Label("行距倍数"), NumberField("lineSpacing"), Label("字色"), ColorField("textColor"));
        Row(row++, Label("底色"), ColorField("backgroundColor"), Label("边框色"), ColorField("borderColor"));
        Row(row++, Label("边框宽 (mm)"), NumberField("borderWidthMm"), Label("边框虚线 (mm)"), DashField("borderDashPatternMm"));
        Row(row++, Label("水平对齐"), EnumField<HorizontalAlign>("horizontalAlign"), Label("垂直对齐"), EnumField<VerticalAlign>("verticalAlign"));
        Row(row++, Label("内边距左 (mm)"), NumberField("paddingLeftMm"), Label("内边距右 (mm)"), NumberField("paddingRightMm"));
        Row(row++, Label("内边距上 (mm)"), NumberField("paddingTopMm"), Label("内边距下 (mm)"), NumberField("paddingBottomMm"));

        var hint = new TextBlock
        {
            Text = "所有字段留空（或「不设置」）表示不覆盖：元素内联已设置的值优先，样式只补内联没设置的字段。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Grid.SetRow(hint, row);
        Grid.SetColumn(hint, 0);
        Grid.SetColumnSpan(hint, 4);
        _form.Children.Add(hint);
    }

    static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brushes.DimGray,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 3, 6, 3),
    };

    /// <summary>把"标签 + 控件"×2 铺进网格的一行（两列字段并排，压住对话框高度）。</summary>
    void Row(int row, UIElement l1, UIElement e1, UIElement l2, UIElement e2)
    {
        while (_form.RowDefinitions.Count <= row)
            _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var items = new[] { (l1, 0), (e1, 1), (l2, 2), (e2, 3) };
        foreach (var (element, column) in items)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            _form.Children.Add(element);
        }
    }

    FrameworkElement TextField(string key) => Bind(new TextBox { Margin = new Thickness(0, 3, 12, 3) }, key,
        (box, style) => box.Text = style.Value(key) as string ?? string.Empty,
        (box, style, key) => style.Set(key, box.Text.Length == 0 ? null : box.Text));

    FrameworkElement NumberField(string key) => Bind(new TextBox { Margin = new Thickness(0, 3, 12, 3) }, key,
        (box, style) => box.Text = style.Value(key) is double d ? d.ToString("0.###", Inv) : string.Empty,
        (box, style, key) => style.Set(key, ParseDouble(box.Text)));

    FrameworkElement ColorField(string key) => Bind(new TextBox { Margin = new Thickness(0, 3, 12, 3), ToolTip = "#RRGGBB；留空不设置" }, key,
        (box, style) => box.Text = style.Value(key) is SKColor c ? SkColorJsonConverter.ToHex(c) : string.Empty,
        (box, style, key) => style.Set(key, ParseColor(box.Text)));

    FrameworkElement DashField(string key) => Bind(new TextBox { Margin = new Thickness(0, 3, 12, 3), ToolTip = "逗号分隔段长（mm），如 1.5,1；留空实线" }, key,
        (box, style) => box.Text = style.Value(key) is double[] a && a.Length > 0
            ? string.Join(",", Array.ConvertAll(a, v => v.ToString("0.###", Inv)))
            : string.Empty,
        (box, style, key) => style.Set(key, ParseDash(box.Text)));

    FrameworkElement TriState(string key)
    {
        var check = new CheckBox
        {
            IsThreeState = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 12, 3),
            ToolTip = "勾选 = 开；不勾选 = 关；半选（方块）= 不设置",
        };
        return Bind(check, key,
            (c, style) => c.IsChecked = style.Value(key) is bool b ? b : null,
            (c, style, key) => style.Set(key, c.IsChecked));
    }

    FrameworkElement EnumField<T>(string key) where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        var combo = new ComboBox
        {
            ItemsSource = new[] { Unset }.Concat(names).ToArray(),
            Margin = new Thickness(0, 3, 12, 3),
        };
        return Bind(combo, key,
            (c, style) => c.SelectedItem = style.Value(key) is { } v ? v.ToString() : Unset,
            (c, style, key) => style.Set(key, c.SelectedItem is string s && s != Unset ? Enum.Parse<T>(s) : null));
    }

    /// <summary>把控件与样式字段接上：加载时回填，变更时经 UpdateStyle 提交（走撤销栈）。</summary>
    FrameworkElement Bind<T>(T control, string key, Action<T, StyleProxy> load, Action<T, StyleProxy, string> commit)
        where T : FrameworkElement
    {
        _fields[key] = control;
        void Commit()
        {
            if (_loading || _list.SelectedItem is not StyleItem item)
                return;

            Run(() =>
            {
                var proxy = new StyleProxy(_kernel.Template.FindStyle(item.Id)!);
                commit(control, proxy, key);
                _kernel.UpdateStyle(item.Id, $"修改样式 {FieldLabel(key)}", proxy.Apply);
            });
        }

        switch (control)
        {
            case TextBox box:
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Commit();
                        e.Handled = true;
                    }
                };
                break;
            case CheckBox check:
                check.Click += (_, _) => Commit();
                break;
            case ComboBox combo:
                combo.SelectionChanged += (_, _) => { if (!_loading) Commit(); };
                break;
        }

        return control;
    }

    static string FieldLabel(string key) => key switch
    {
        "name" => "名称",
        "fontFamily" => "字体",
        "fontSizePt" => "字号",
        "letterSpacingPt" => "字间距",
        "bold" => "加粗",
        "italic" => "倾斜",
        "underline" => "下划线",
        "strikethrough" => "删除线",
        "lineSpacing" => "行距",
        "textColor" => "字色",
        "backgroundColor" => "底色",
        "borderColor" => "边框色",
        "borderWidthMm" => "边框宽",
        "borderDashPatternMm" => "边框虚线",
        "horizontalAlign" => "水平对齐",
        "verticalAlign" => "垂直对齐",
        "paddingLeftMm" => "内边距左",
        "paddingRightMm" => "内边距右",
        "paddingTopMm" => "内边距上",
        "paddingBottomMm" => "内边距下",
        _ => key,
    };

    void LoadSelected()
    {
        _loading = true;
        try
        {
            if (_list.SelectedItem is not StyleItem item)
            {
                _form.IsEnabled = false;
                _usage.Text = string.Empty;
                return;
            }

            var style = _kernel.Template.FindStyle(item.Id);
            if (style is null)
            {
                _form.IsEnabled = false;
                return;
            }

            _form.IsEnabled = true;

            var proxy = new StyleProxy(style);
            foreach (var pair in _fields)
            {
                switch (pair.Value)
                {
                    case TextBox box when pair.Key == "name" || pair.Key == "fontFamily":
                        if (!box.IsKeyboardFocusWithin)
                            box.Text = proxy.Value(pair.Key) as string ?? string.Empty;
                        break;
                    case TextBox box:
                        var text = pair.Key switch
                        {
                            "fontSizePt" or "letterSpacingPt" or "lineSpacing" or "borderWidthMm"
                                or "paddingLeftMm" or "paddingRightMm" or "paddingTopMm" or "paddingBottomMm"
                                => proxy.Value(pair.Key) is double d ? d.ToString("0.###", Inv) : string.Empty,
                            "textColor" or "backgroundColor" or "borderColor"
                                => proxy.Value(pair.Key) is SKColor c ? SkColorJsonConverter.ToHex(c) : string.Empty,
                            _ => proxy.Value(pair.Key) is double[] a && a.Length > 0
                                ? string.Join(",", Array.ConvertAll(a, v => v.ToString("0.###", Inv)))
                                : string.Empty,
                        };
                        if (!box.IsKeyboardFocusWithin)
                            box.Text = text;
                        break;
                    case CheckBox check:
                        check.IsChecked = proxy.Value(pair.Key) is bool b ? b : null;
                        break;
                    case ComboBox combo:
                        combo.SelectedItem = proxy.Value(pair.Key) is { } v ? v.ToString() : Unset;
                        break;
                }
            }

            var usage = _kernel.GetStyleUsage(item.Id);
            _usage.Text = usage.Count == 0
                ? "当前没有被任何元素/表格引用。"
                : $"被 {usage.Count} 处引用：{string.Join("、", usage.Take(6).Select(u => $"{u.ElementId}·{u.Location}"))}{(usage.Count > 6 ? "…" : string.Empty)}";
            _deleteButton.IsEnabled = usage.Count == 0;
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------------- 提交与解析 ----------------

    void Run(Action action)
    {
        try
        {
            action();
            _error.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
    }

    static double? ParseDouble(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        if (!double.TryParse(trimmed, NumberStyles.Float, Inv, out var value))
            throw new FormatException($"“{text}”不是合法数字（请用小数点，如 12.5）。");

        return value;
    }

    static SKColor? ParseColor(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        if (!SKColor.TryParse(trimmed, out var color))
            throw new FormatException("颜色格式应为 #RRGGBB 或 #AARRGGBB。");

        return color;
    }

    static double[]? ParseDash(string text)
    {
        var parts = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out values[i]))
                throw new FormatException($"“{parts[i]}”不是合法段长（用逗号分隔，如 1.5,1）。");
        }

        return values;
    }

    /// <summary>
    /// UpdateStyle 的 mutate 前把字段先写进代理：内核在 Apply 前后各自快照，
    /// 所以字段收集与提交必须一次完成——直接 mutate 模板里的活对象也行，代理只是把
    /// "控件 → 可空值"的转换集中在一处。
    /// </summary>
    sealed class StyleProxy(NamedStyle style)
    {
        readonly List<Action<NamedStyle>> _changes = [];
        readonly NamedStyle _style = style;

        /// <summary>读当前模板对象的字段值（控件回填用）。</summary>
        public object? Value(string key) => key switch
        {
            "name" => _style.Name,
            "fontFamily" => _style.FontFamily,
            "fontSizePt" => _style.FontSizePt,
            "letterSpacingPt" => _style.LetterSpacingPt,
            "bold" => _style.Bold,
            "italic" => _style.Italic,
            "underline" => _style.Underline,
            "strikethrough" => _style.Strikethrough,
            "lineSpacing" => _style.LineSpacing,
            "textColor" => _style.TextColor,
            "backgroundColor" => _style.BackgroundColor,
            "borderColor" => _style.BorderColor,
            "borderWidthMm" => _style.BorderWidthMm,
            "borderDashPatternMm" => _style.BorderDashPatternMm,
            "horizontalAlign" => _style.HorizontalAlign,
            "verticalAlign" => _style.VerticalAlign,
            "paddingLeftMm" => _style.PaddingLeftMm,
            "paddingRightMm" => _style.PaddingRightMm,
            "paddingTopMm" => _style.PaddingTopMm,
            "paddingBottomMm" => _style.PaddingBottomMm,
            _ => null,
        };

        /// <summary>收集一条变更（此刻先算好目标值，Apply 时统一写入快照对象）。</summary>
        public void Set(string key, object? value)
        {
            _changes.Add(style => Apply(style, key, value));
        }

        /// <summary>UpdateStyle 的 mutate：把收集到的变更全部写入。</summary>
        public void Apply(NamedStyle target)
        {
            foreach (var change in _changes)
                change(target);
        }

        static void Apply(NamedStyle style, string key, object? value)
        {
            switch (key)
            {
                case "name":
                    style.Name = value as string is { } name && name.Length > 0 ? name : "未命名样式";
                    break;
                case "fontFamily": style.FontFamily = (string?)value; break;
                case "fontSizePt": style.FontSizePt = (double?)value; break;
                case "letterSpacingPt": style.LetterSpacingPt = (double?)value; break;
                case "bold": style.Bold = (bool?)value; break;
                case "italic": style.Italic = (bool?)value; break;
                case "underline": style.Underline = (bool?)value; break;
                case "strikethrough": style.Strikethrough = (bool?)value; break;
                case "lineSpacing": style.LineSpacing = (double?)value; break;
                case "textColor": style.TextColor = (SKColor?)value; break;
                case "backgroundColor": style.BackgroundColor = (SKColor?)value; break;
                case "borderColor": style.BorderColor = (SKColor?)value; break;
                case "borderWidthMm": style.BorderWidthMm = (double?)value; break;
                case "borderDashPatternMm": style.BorderDashPatternMm = (double[]?)value; break;
                case "horizontalAlign": style.HorizontalAlign = (HorizontalAlign?)value; break;
                case "verticalAlign": style.VerticalAlign = (VerticalAlign?)value; break;
                case "paddingLeftMm": style.PaddingLeftMm = (double?)value; break;
                case "paddingRightMm": style.PaddingRightMm = (double?)value; break;
                case "paddingTopMm": style.PaddingTopMm = (double?)value; break;
                case "paddingBottomMm": style.PaddingBottomMm = (double?)value; break;
            }
        }
    }
}
