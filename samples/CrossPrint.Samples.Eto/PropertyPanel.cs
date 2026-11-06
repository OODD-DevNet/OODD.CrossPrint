using System.Globalization;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.I18n;
using Eto.Drawing;
using Eto.Forms;
using SkiaSharp;

namespace CrossPrint.Samples.Eto;

/// <summary>
/// 数据驱动的属性面板：完全按 <see cref="ElementProperties.Descriptors"/> 生成表单，
/// 不写死任何字段名，八种元素类型共用这一份代码。
/// </summary>
/// <remarks>
/// 布局是两列表格：标签列固定 <see cref="LabelColumnWidth"/> 宽，编辑器列拉伸填满剩余宽度，
/// 整张表放在 <see cref="Scrollable"/> 里，窄窗口下也能滚动到所有编辑器。
/// 刷新纪律：选中集合变化才重建控件；同一选中下（拖拽、缩放、层次调整、属性提交）只回填值，
/// 避免把正在输入的控件拆掉。属性写回只走 <see cref="EditorKernel.SetSelectedProperty(string, object?)"/>，
/// 解析/写入失败就地显示在错误标签里，不弹框、不抛出。
/// </remarks>
internal sealed class PropertyPanel : Panel
{
    const int LabelColumnWidth = 132;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    EditorKernel _kernel;
    readonly Scrollable _scroll = new() { Border = BorderType.None, ExpandContentWidth = true };
    // 动态属性行放在 _rowsHost 里整表替换；页面级设置（水印）放在并列的 _emptyStateHost 里，
    // 永不被 SwapTable 的 Dispose 循环碰到——持久控件被销毁正是空白区铁律要防的事故。
    readonly Panel _rowsHost = new();
    readonly Panel _emptyStateHost = new() { Visible = false };
    Control? _emptyStateControl;
    readonly List<TableRow> _tableRows = new();
    readonly Label _header = new();
    readonly Label _error = new() { TextColor = Colors.Firebrick, Wrap = WrapMode.Word };
    readonly List<PropertyRow> _rows = new();

    bool _building;
    bool _refreshing;
    bool _rebuildQueued;
    string _selectionKey = string.Empty;

    /// <summary>
    /// 无选中元素时空白区的附加内容构造器（页面级设置：水印等）。
    /// 必须返回懒构建的持久实例，绝不能每次 Rebuild 新建控件树。
    /// </summary>
    public Func<Control>? EmptyStateContentBuilder { get; set; }

    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;
        Padding = new Padding(8, 6, 8, 8);

        _header.Text = Localizer.T("PropertyGrid.Section");
        _error.Visible = false;

        // 滚动区内的两行：动态行宿主 + 持久空白区宿主；两行都在构造期一次性建好，
        // 之后只换 _rowsHost.Content，不再整块替换 _scroll.Content。
        _scroll.Content = new TableLayout(
            new TableRow(new TableCell(_rowsHost) { ScaleWidth = true }),
            new TableRow(new TableCell(_emptyStateHost) { ScaleWidth = true }))
        {
            Spacing = new Size(0, 4),
        };

        var layout = new TableLayout { Spacing = new Size(0, 4) };
        layout.Rows.Add(new TableRow(new TableCell(_header) { ScaleWidth = true }));
        layout.Rows.Add(new TableRow(new TableCell(_error) { ScaleWidth = true }));
        layout.Rows.Add(new TableRow(new TableCell(_scroll) { ScaleWidth = true }) { ScaleHeight = true });
        Content = layout;

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

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _kernel.ViewChanged -= OnViewChanged;

        base.Dispose(disposing);
    }

    // 结构签名含描述符 Key + Hidden：表格列切换渲染方式后条件属性即时出现；
    // 不含坐标值，拖拽时不会逐帧重建面板。
    string SelectionKey() => PropertyOptions.StructureSignature(_kernel);

    void OnViewChanged()
    {
        var key = SelectionKey();
        if (!string.Equals(key, _selectionKey, StringComparison.Ordinal))
        {
            ClearError();
            Rebuild();
            return;
        }

        // 同一选中：只回填值，不重建（否则正在输入的框会被拆掉）。
        // 程序化回填会触发 SelectedIndexChanged / TextChanged，必须全程拉起 _refreshing，
        // 否则一次回填就把旧值重新提交一遍，甚至形成事件回环。
        if (_building || _refreshing)
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
            _tableRows.Clear();
            _selectionKey = SelectionKey();
            BuildRows();
        }
        finally
        {
            _building = false;
            SwapTable();
        }
    }

    void BuildRows()
    {
        if (_kernel.Selection.IsEmpty)
        {
            _header.Text = Localizer.T("PropertyGrid.Section");
            AddHint(Localizer.T("Property.Hint.Empty"));
            ShowEmptyState();
            return;
        }

        _emptyStateHost.Visible = false;
        var selected = _kernel.SelectedElements;
        if (selected.Count != 1)
        {
            _header.Text = Localizer.T("Property.Header.Multi", selected.Count);
            AddHint(Localizer.T("Property.Hint.Multi"));
            return;
        }

        var element = selected[0];
        var properties = ElementProperties.For(element);
        _header.Text = $"{TypeName(element.TypeKey)} · {element.Id}";

        if (properties.Actions.Count > 0)
            AddActionBar(properties);

        string? group = null;
        foreach (var descriptor in properties.Descriptors)
        {
            if (descriptor.Hidden)
                continue;

            if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
            {
                group = descriptor.Group;
                AddGroupHeader(group);
            }

            AddRow(element, descriptor);
        }
    }

    /// <summary>
    /// 整表替换。Eto 的 <c>TableLayoutHandler</c> 在创建时按当时的行数分配 <c>views[cols, rows]</c>，
    /// 之后再 <c>Rows.Add</c> 的控制永远不会被挂上原生控件——属性面板的行数是逐次变化的，
    /// 所以每次重建都必须把完整的行集合交给一个新的 <see cref="TableLayout"/> 构造函数。
    /// </summary>
    void SwapTable()
    {
        var previous = _rowsHost.Content as TableLayout;
        _rowsHost.Content = new TableLayout(_tableRows.ToArray()) { Spacing = new Size(8, 6) };

        if (previous is null)
            return;

        foreach (var row in previous.Rows)
        {
            foreach (var cell in row.Cells)
                cell.Control?.Dispose();
        }

        previous.Rows.Clear();
        previous.Dispose();
    }

    /// <summary>懒构建并持久复用空白区控件；只切 Visible，绝不摘树/重建/销毁。</summary>
    void ShowEmptyState()
    {
        if (EmptyStateContentBuilder is not null)
        {
            _emptyStateControl ??= EmptyStateContentBuilder();
            if (!ReferenceEquals(_emptyStateHost.Content, _emptyStateControl))
                _emptyStateHost.Content = _emptyStateControl;
        }

        _emptyStateHost.Visible = true;
    }

    void AddGroupHeader(string text)
    {
        var label = new Label
        {
            Text = PropertyText.Group(text),
            TextColor = Colors.SteelBlue,
            Font = new Font(SystemFont.Bold),
            Wrap = WrapMode.Word,
        };

        _tableRows.Add(new TableRow(new TableCell(label) { ScaleWidth = true }));
    }

    /// <summary>
    /// 结构动作按钮行（表格列的增删）：点击交给内核，成败都重建面板（动作清单是快照）。
    /// 每行两个按钮——见remarks。
    /// </summary>
    /// <remarks>
    /// Eto 的 <see cref="TableLayout"/> 按最宽的那一行定列数，而本面板的既有形状是"标签 132 + 编辑器拉伸"
    /// 两列：把八个"删除列N"排成一行，表格就变成九列，属性行的编辑器列只占第一格宽、不再拉满面板；
    /// Eto 又没有换行容器，超出面板宽度的按钮会被直接裁掉——列数稍多的表格在界面上就删不了列。
    /// 所以这里两列一行地排，奇数个时末格放空面板补齐。
    /// </remarks>
    void AddActionBar(ElementProperties properties)
    {
        for (var i = 0; i < properties.Actions.Count; i += 2)
        {
            var second = i + 1 < properties.Actions.Count
                ? (Control)CreateActionButton(properties.Actions[i + 1])
                : new Panel();
            _tableRows.Add(new TableRow(
                new TableCell(CreateActionButton(properties.Actions[i])),
                new TableCell(second)));
        }
    }

    Button CreateActionButton(ElementAction action)
    {
        var button = new Button { Text = PropertyText.Action(action.DisplayName) };
        button.Click += (_, _) => RunAction(action);
        return button;
    }

    void RunAction(ElementAction action)
    {
        try
        {
            _kernel.RunElementAction(action.Key);
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError(Localizer.T("Property.Error.Action", PropertyText.Action(action.DisplayName), ex.Message));
        }

        Rebuild();
    }

    void AddHint(string text)
    {
        var hint = new Label
        {
            Text = text,
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        _tableRows.Add(new TableRow(new TableCell(hint) { ScaleWidth = true }));
    }

    void AddRow(PaperElement element, PropertyDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case PropertyKind.Boolean:
                AddCheckRow(element, descriptor);
                break;

            case PropertyKind.Enum:
                AddEnumRow(element, descriptor);
                break;

            case PropertyKind.Text:
            case PropertyKind.MultilineText:
            case PropertyKind.Number:
            case PropertyKind.Integer:
            case PropertyKind.Color:
            case PropertyKind.DashPattern:
                AddTextRow(element, descriptor);
                break;
        }
    }

    void AddTextRow(PaperElement element, PropertyDescriptor descriptor)
    {
        var multiline = descriptor.Kind == PropertyKind.MultilineText;
        TextControl editor = multiline
            ? new TextArea
            {
                Text = FormatValue(descriptor, descriptor.GetValue(element)),
                ReadOnly = descriptor.ReadOnly,
                Wrap = true,
                Height = 64,
            }
            : new TextBox
            {
                Text = FormatValue(descriptor, descriptor.GetValue(element)),
                ReadOnly = descriptor.ReadOnly,
            };

        var last = editor.Text;

        void Commit()
        {
            if (_building || descriptor.ReadOnly)
                return;

            var text = editor.Text;
            if (string.Equals(text, last, StringComparison.Ordinal))
                return;

            if (!TryParse(descriptor, text, out var value))
                return;

            if (!Apply(descriptor, value))
                return;

            last = editor.Text;
        }

        editor.LostFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) =>
        {
            // 多行框的裸 Enter 是换行；Ctrl+Enter 才提交。
            if (e.Key != Keys.Enter || (multiline && !e.Control))
                return;

            e.Handled = true;
            Commit();
        };

        void Refresh()
        {
            if (editor.HasFocus)
                return;

            var text = FormatValue(descriptor, descriptor.GetValue(element));
            if (string.Equals(text, editor.Text, StringComparison.Ordinal))
                return;

            editor.Text = text;
            last = text;
        }

        editor.ToolTip ??= DescribeTooltip(descriptor);
        _rows.Add(new PropertyRow(descriptor, editor, Refresh));
        AddEditorRow(descriptor, editor);
    }

    void AddCheckRow(PaperElement element, PropertyDescriptor descriptor)
    {
        var check = new CheckBox
        {
            Checked = descriptor.GetValue(element) is bool flag && flag,
            Enabled = !descriptor.ReadOnly,
            ToolTip = DescribeTooltip(descriptor),
        };

        check.CheckedChanged += (_, _) =>
        {
            if (_building || descriptor.ReadOnly)
                return;

            Apply(descriptor, check.Checked == true);
        };

        void Refresh()
        {
            if (check.HasFocus)
                return;

            var value = descriptor.GetValue(element) is bool flag && flag;
            if (check.Checked == value)
                return;

            check.Checked = value;
        }

        _rows.Add(new PropertyRow(descriptor, check, Refresh));
        AddEditorRow(descriptor, check);
    }

    void AddEnumRow(PaperElement element, PropertyDescriptor descriptor)
    {
        // 候选项由描述符给出（CLR 成员名原样），面板只负责按名选中。
        // 候选项走统一口径：Flags 组合值也补成一项，避免下拉空白 / 误落第一项。
        var options = PropertyOptions.Values(descriptor, element);
        var drop = new DropDown
        {
            Enabled = !descriptor.ReadOnly,
            ToolTip = DescribeTooltip(descriptor),
        };

        foreach (var option in options)
            drop.Items.Add(option);

        drop.SelectedIndex = PropertyOptions.IndexOf(options, PropertyOptions.Current(descriptor, element));

        drop.SelectedIndexChanged += (_, _) =>
        {
            if (_building || _refreshing || descriptor.ReadOnly)
                return;

            var index = drop.SelectedIndex;
            if (index < 0 || index >= options.Count)
                return;

            Apply(descriptor, options[index]);
        };

        void Refresh()
        {
            if (drop.HasFocus)
                return;

            var index = PropertyOptions.IndexOf(options, PropertyOptions.Current(descriptor, element));
            if (drop.SelectedIndex != index)
                drop.SelectedIndex = index;
        }

        _rows.Add(new PropertyRow(descriptor, drop, Refresh));
        AddEditorRow(descriptor, drop);
    }

    void AddEditorRow(PropertyDescriptor descriptor, Control editor)
    {
        // 标签列固定宽度、编辑器列吸收剩余宽度；窗口变窄时整表可滚动，编辑器不会被挤没。
        var label = new Label
        {
            Text = PropertyText.Label(descriptor.DisplayName),
            TextColor = Colors.DimGray,
            ToolTip = DescribeTooltip(descriptor),
            Width = LabelColumnWidth,
            Wrap = WrapMode.Word,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _tableRows.Add(new TableRow(
            new TableCell(label) { ScaleWidth = false },
            new TableCell(editor) { ScaleWidth = true }));
    }

    bool Apply(PropertyDescriptor descriptor, object? value)
    {
        // 程序化回填（Refresh）触发的事件不能当成用户提交。
        if (_building || _refreshing)
            return false;

        try
        {
            _kernel.SetSelectedProperty(descriptor.Key, value);
            ClearError();
            ScheduleRebuild();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(Localizer.T("Property.Error.Apply", PropertyText.Label(descriptor.DisplayName), ex.Message));
            ScheduleRebuild();
            return false;
        }
    }

    /// <summary>提交后延迟重建：下拉从活模型回读、条件属性（Hidden 变化）即时出现。</summary>
    void ScheduleRebuild()
    {
        if (_rebuildQueued || _building)
            return;

        _rebuildQueued = true;
        var app = Application.Instance;
        if (app is null)
        {
            // headless / 无应用实例：没有 UI 队列可用，直接重建（无输入事件在派发）。
            _rebuildQueued = false;
            Rebuild();
            return;
        }

        app.AsyncInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    bool TryParse(PropertyDescriptor descriptor, string text, out object? value)
    {
        value = null;
        switch (descriptor.Kind)
        {
            case PropertyKind.MultilineText:
            case PropertyKind.Text:
                value = text;
                return true;

            case PropertyKind.Number:
            {
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                {
                    // 可空数值（如表格列字号）留空表示继承；不可空字段由内核校验并抛出。
                    return true;
                }

                if (!double.TryParse(trimmed, NumberStyles.Float, Inv, out var number))
                    return InputError(Localizer.T("Property.Error.Number", text));

                if (descriptor.Min is { } min)
                    number = Math.Max(min, number);
                if (descriptor.Max is { } max)
                    number = Math.Min(max, number);

                value = number;
                return true;
            }

            case PropertyKind.Integer:
            {
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                    return true;

                if (!int.TryParse(trimmed, NumberStyles.Integer, Inv, out var integer))
                    return InputError(Localizer.T("Property.Error.Integer", text));

                if (descriptor.Min is { } min && integer < min)
                    integer = (int)Math.Ceiling(min);
                if (descriptor.Max is { } max && integer > max)
                    integer = (int)Math.Floor(max);

                value = integer;
                return true;
            }

            case PropertyKind.Color:
            {
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                    return true;

                if (!SKColor.TryParse(trimmed, out var color))
                    return InputError(Localizer.T("Property.Error.Color"));

                value = color;
                return true;
            }

            case PropertyKind.DashPattern:
            {
                var parts = text.Split(
                    [',', ';', ' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return true;

                var segments = new double[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out segments[i]))
                        return InputError(Localizer.T("Property.Error.Dash", parts[i]));
                }

                value = segments;
                return true;
            }

            default:
                value = text;
                return true;
        }
    }

    bool InputError(string message)
    {
        ShowError(message);
        return false;
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.Visible = true;
    }

    void ClearError()
    {
        _error.Text = string.Empty;
        _error.Visible = false;
    }

    static string DescribeTooltip(PropertyDescriptor descriptor)
        => descriptor.Key + (descriptor.ReadOnly ? Localizer.T("Property.ReadOnlyTip") : string.Empty);

    static int IndexOf(IReadOnlyList<string> options, string? value)
    {
        if (value is null)
            return -1;

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    static string FormatValue(PropertyDescriptor descriptor, object? value) => descriptor.Kind switch
    {
        PropertyKind.Number => value switch
        {
            double number => number.ToString("0.######", Inv),
            float single => ((double)single).ToString("0.######", Inv),
            int integer => integer.ToString(Inv),
            long l => l.ToString(Inv),
            _ => Convert.ToString(value, Inv) ?? string.Empty,
        },
        PropertyKind.Integer => value switch
        {
            int integer => integer.ToString(Inv),
            long l => l.ToString(Inv),
            _ => Convert.ToString(value, Inv) ?? string.Empty,
        },
        PropertyKind.Color => value is SKColor color ? SkColorJsonConverter.ToHex(color) : string.Empty,
        PropertyKind.DashPattern => value is double[] segments && segments.Length > 0
            ? string.Join(",", Array.ConvertAll(segments, v => v.ToString("0.###", Inv)))
            : string.Empty,
        _ => value?.ToString() ?? string.Empty,
    };

    static string TypeName(string typeKey)
        => PropertyText.ElementType(typeKey, ChineseTypeName(typeKey));

    static string ChineseTypeName(string typeKey) => typeKey switch
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

    sealed class PropertyRow(PropertyDescriptor descriptor, Control editor, Action refresh)
    {
        public PropertyDescriptor Descriptor { get; } = descriptor;

        public Control Editor { get; } = editor;

        public Action Refresh { get; } = refresh;
    }
}
