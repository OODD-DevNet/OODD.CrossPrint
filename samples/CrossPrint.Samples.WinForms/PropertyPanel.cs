using System.Globalization;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.WinForms;

/// <summary>
/// 数据驱动的属性面板：遍历 <see cref="ElementProperties.Descriptors"/> 生成表单，仅服务单选。
/// </summary>
public sealed class PropertyPanel : UserControl
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    EditorKernel _kernel;
    readonly TableLayoutPanel _table;
    bool _rebuilding;
    string _selectionSignature = string.Empty;

    /// <summary>为指定内核创建属性面板。</summary>
    public PropertyPanel(EditorKernel kernel)
    {
        _kernel = kernel;
        AutoScroll = true;
        Padding = new Padding(8, 4, 8, 8);

        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 0,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);

        _kernel.ViewChanged += OnKernelViewChanged;
        _selectionSignature = Signature();
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
        _selectionSignature = Signature();
        Rebuild();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _kernel.ViewChanged -= OnKernelViewChanged;

        base.Dispose(disposing);
    }

    void OnKernelViewChanged()
    {
        // 拖拽期间 ViewChanged 会高频触发；只有选中集合真的变了才重排面板。
        var signature = Signature();
        if (signature == _selectionSignature)
            return;

        _selectionSignature = signature;
        ScheduleRebuild();
    }

    string Signature() => string.Join("|", _kernel.Selection.Ids);

    void ScheduleRebuild()
    {
        if (IsDisposed)
            return;

        if (IsHandleCreated)
        {
            try
            {
                BeginInvoke(new Action(Rebuild));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，句柄已不可用；无需再刷新。
            }
        }
        else
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        if (_rebuilding || IsDisposed)
            return;

        // 提交后重建会销毁当前编辑控件；记住描述符键，重建完把焦点还给同一格。
        string? focusedKey = null;
        foreach (Control control in _table.Controls)
        {
            if (control.Focused)
            {
                focusedKey = control.Tag as string;
                break;
            }
        }

        _rebuilding = true;
        try
        {
            _table.SuspendLayout();
            foreach (var control in _table.Controls.Cast<Control>().ToArray())
                control.Dispose();
            _table.Controls.Clear();
            _table.RowStyles.Clear();
            _table.RowCount = 0;

            var properties = _kernel.SelectedProperties;
            if (properties is null)
            {
                _table.RowCount = 1;
                _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var hint = new Label
                {
                    Text = "未选中元素。点击画布上的元素编辑属性；右键画布可打开菜单；纸张大小在左侧「纸张」栏设置。",
                    AutoSize = true,
                    // 右栏默认 310px，去掉内边距后提示文本按 ~290 宽换行，不会被面板裁掉。
                    MaximumSize = new Size(290, 0),
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(0, 8, 0, 0),
                };
                _table.Controls.Add(hint, 0, 0);
                _table.SetColumnSpan(hint, 2);
                return;
            }

            AddSpanRow(new Label
            {
                Text = DescribeElement(properties.Element),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 4),
            });

            if (properties.Actions.Count > 0)
                AddSpanRow(CreateActionBar(properties));

            string? group = null;
            foreach (var descriptor in properties.Descriptors)
            {
                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    group = descriptor.Group;
                    AddSpanRow(new Label
                    {
                        Text = group,
                        AutoSize = true,
                        Font = new Font(Font, FontStyle.Bold),
                        ForeColor = Color.SteelBlue,
                        Margin = new Padding(0, 8, 0, 2),
                    });
                }

                AddRow(descriptor, properties.Element);
            }
        }
        finally
        {
            _table.ResumeLayout(true);
            _rebuilding = false;
            _selectionSignature = Signature();
        }

        if (focusedKey is not null)
        {
            foreach (Control control in _table.Controls)
            {
                if (control.Tag as string == focusedKey)
                {
                    control.Focus();
                    break;
                }
            }
        }
    }

    void AddSpanRow(Control control)
    {
        var row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(control, 0, row);
        _table.SetColumnSpan(control, 2);
    }

    /// <summary>结构动作按钮行（表格列的增删）：点击交给内核，成败都重建面板（动作清单是快照）。</summary>
    Control CreateActionBar(ElementProperties properties)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 4),
        };

        foreach (var action in properties.Actions)
        {
            var button = new Button
            {
                Text = action.DisplayName,
                AutoSize = true,
                Margin = new Padding(0, 0, 6, 2),
            };
            button.Click += (_, _) => RunAction(action);
            flow.Controls.Add(button);
        }

        return flow;
    }

    void RunAction(ElementAction action)
    {
        try
        {
            _kernel.RunElementAction(action.Key);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"动作“{action.DisplayName}”执行失败：{ex.Message}",
                "动作执行失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        ScheduleRebuild();
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

    void AddRow(PropertyDescriptor descriptor, PaperElement element)
    {
        var row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = descriptor.DisplayName,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 4),
        };
        _table.Controls.Add(label, 0, row);
        _table.Controls.Add(CreateEditor(descriptor, element), 1, row);
    }

    Control CreateEditor(PropertyDescriptor descriptor, PaperElement element)
    {
        var value = descriptor.GetValue(element);

        if (descriptor.Kind == PropertyKind.Boolean)
        {
            var check = new CheckBox
            {
                Checked = value is bool flag && flag,
                AutoSize = true,
                Enabled = !descriptor.ReadOnly,
                Margin = new Padding(0, 4, 0, 4),
                Tag = descriptor.Key,
            };
            check.CheckedChanged += (_, _) =>
            {
                if (_rebuilding || descriptor.ReadOnly)
                    return;
                Apply(descriptor, check.Checked);
            };
            return check;
        }

        if (descriptor.Kind == PropertyKind.Enum)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Enabled = !descriptor.ReadOnly,
                Margin = new Padding(0, 2, 0, 2),
                Tag = descriptor.Key,
            };
            if (descriptor.Options is not null)
            {
                foreach (var option in descriptor.Options)
                    combo.Items.Add(option);
            }

            var current = value?.ToString();
            if (current is not null && combo.Items.Contains(current))
                combo.SelectedItem = current;

            combo.SelectedIndexChanged += (_, _) =>
            {
                if (_rebuilding || descriptor.ReadOnly || combo.SelectedItem is not string selected)
                    return;
                Apply(descriptor, selected);
            };
            return combo;
        }

        var multiline = descriptor.Kind == PropertyKind.MultilineText;
        var box = new TextBox
        {
            Text = Format(descriptor, value),
            Dock = DockStyle.Fill,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            AcceptsReturn = multiline,
            WordWrap = multiline,
            Height = multiline ? 64 : 23,
            ReadOnly = descriptor.ReadOnly,
            TabStop = !descriptor.ReadOnly,
            BackColor = descriptor.ReadOnly ? SystemColors.Control : SystemColors.Window,
            Margin = new Padding(0, 2, 0, 2),
            Tag = descriptor.Key,
        };

        var initial = box.Text;

        void Commit()
        {
            if (_rebuilding || descriptor.ReadOnly || box.IsDisposed || box.Text == initial)
                return;

            if (!TryRead(descriptor, box.Text, out var parsed))
            {
                ScheduleRebuild();
                return;
            }

            if (Apply(descriptor, parsed))
                initial = box.Text;
        }

        box.Leave += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            // 多行框的裸 Enter 是换行；Ctrl+Enter 才提交。
            if (e.KeyCode != Keys.Enter || (multiline && !e.Control))
                return;

            e.SuppressKeyPress = true;
            Commit();
        };

        return box;
    }

    bool TryRead(PropertyDescriptor descriptor, string text, out object? value)
    {
        value = null;
        switch (descriptor.Kind)
        {
            case PropertyKind.Number:
            {
                var text2 = text.Trim();
                if (text2.Length == 0)
                {
                    // 可空数值（如表格列字号）留空表示继承；不可空字段由内核校验并抛出。
                    value = null;
                    return true;
                }

                if (!double.TryParse(text2, NumberStyles.Float, Inv, out var number))
                {
                    ShowInputError(descriptor, "请输入有效数字（小数用 . 分隔）。");
                    return false;
                }

                if (descriptor.Min is { } min)
                    number = Math.Max(min, number);
                if (descriptor.Max is { } max)
                    number = Math.Min(max, number);

                value = number;
                return true;
            }

            case PropertyKind.Integer:
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out var integer))
                {
                    ShowInputError(descriptor, "请输入整数。");
                    return false;
                }

                if (descriptor.Min is { } min && integer < min)
                    integer = (int)Math.Ceiling(min);
                if (descriptor.Max is { } max && integer > max)
                    integer = (int)Math.Floor(max);

                value = integer;
                return true;
            }

            case PropertyKind.Color:
            {
                var colorText = text.Trim();
                if (colorText.Length == 0)
                    return true;

                if (!SKColor.TryParse(colorText, out var color))
                {
                    ShowInputError(descriptor, "颜色格式应为 #RRGGBB 或 #AARRGGBB。");
                    return false;
                }

                value = color;
                return true;
            }

            case PropertyKind.DashPattern:
            {
                var parts = text.Split(
                    [',', ';', ' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries);
                var values = new List<double>(parts.Length);
                foreach (var part in parts)
                {
                    if (!double.TryParse(part, NumberStyles.Float, Inv, out var segment))
                    {
                        ShowInputError(descriptor, "虚线段长格式应为 1.5,1（逗号分隔的数字）。");
                        return false;
                    }

                    values.Add(segment);
                }

                value = values.Count == 0 ? null : values.ToArray();
                return true;
            }

            default:
                value = text;
                return true;
        }
    }

    bool Apply(PropertyDescriptor descriptor, object? value)
    {
        try
        {
            _kernel.SetSelectedProperty(descriptor.Key, value);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"属性“{descriptor.DisplayName}”写入失败：{ex.Message}",
                "属性写入失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            ScheduleRebuild();
            return false;
        }

        ScheduleRebuild();
        return true;
    }

    void ShowInputError(PropertyDescriptor descriptor, string message)
    {
        MessageBox.Show(
            this,
            $"属性“{descriptor.DisplayName}”：{message}",
            "属性输入",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    static string Format(PropertyDescriptor descriptor, object? value) => descriptor.Kind switch
    {
        PropertyKind.Number => value is double number ? number.ToString("0.####", Inv) : string.Empty,
        PropertyKind.Integer => value is int integer ? integer.ToString(Inv) : string.Empty,
        PropertyKind.Color => value is SKColor color ? FormatColor(color) : string.Empty,
        PropertyKind.DashPattern => value is double[] dash
            ? string.Join(",", dash.Select(v => v.ToString("0.###", Inv)))
            : string.Empty,
        _ => value?.ToString() ?? string.Empty,
    };

    static string FormatColor(SKColor color) => color.Alpha == 255
        ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        : $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
