using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 样式库弹窗：模板级命名样式的增删与全量字段编辑（字体/段落/对齐/内边距/边框五组）。
/// 全部变更走内核命令（AddStyle/RemoveStyle/UpdateStyle），可撤销；删除仍被引用的样式由内核拦截并提示引用者。
/// </summary>
public static class StyleLibraryDialog
{
    public static Task Show(Visual anchor, EditorKernel kernel)
    {
        string? currentId = null;
        var syncing = false;

        var message = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 4),
            IsVisible = false,
        };

        var list = new ListBox { MinWidth = 190, Margin = new Thickness(8, 8, 4, 8) };

        // ---- 文本 ----
        var name = new TextBox();
        var fontFamily = new TextBox();
        var fontSize = new TextBox();
        var bold = new CheckBox { Content = "加粗" };
        var italic = new CheckBox { Content = "倾斜" };
        var underline = new CheckBox { Content = "下划线" };
        var strikethrough = new CheckBox { Content = "删除线" };
        var overline = new CheckBox { Content = "上划线" };
        var textColor = new TextBox { Watermark = "留空不设置" };
        var backgroundColor = new TextBox { Watermark = "留空不设置" };

        // ---- 段落 ----
        var lineSpacing = new TextBox { Watermark = "倍数" };
        var letterSpacing = new TextBox { Watermark = "磅" };
        var indent = new TextBox { Watermark = "mm" };
        var firstLineIndent = new TextBox { Watermark = "mm" };

        // ---- 对齐 ----
        var hAlign = new ComboBox { MinWidth = 140, ItemsSource = Enum.GetNames(typeof(HorizontalAlign)) };
        var vAlign = new ComboBox { MinWidth = 140, ItemsSource = Enum.GetNames(typeof(VerticalAlign)) };

        // ---- 内边距 ----
        var padLeft = new TextBox { Watermark = "mm" };
        var padTop = new TextBox { Watermark = "mm" };
        var padRight = new TextBox { Watermark = "mm" };
        var padBottom = new TextBox { Watermark = "mm" };

        // ---- 边框 ----
        var borderColor = new TextBox { Watermark = "留空不设置" };
        var borderWidth = new TextBox { Watermark = "mm" };
        var borderDash = new TextBox { Watermark = "如 2,2（留空实线）" };

        void ShowMessage(string? text)
        {
            message.Text = text ?? string.Empty;
            message.IsVisible = text is not null;
        }

        void RefreshList()
        {
            syncing = true;
            try
            {
                list.Items.Clear();
                foreach (var style in kernel.Template.Styles)
                {
                    var item = new ListBoxItem
                    {
                        Content = $"{style.Name}（{style.Id}）",
                        Tag = style.Id,
                        Padding = new Thickness(6, 2),
                    };
                    if (style.Id == currentId)
                        item.IsSelected = true;
                    list.Items.Add(item);
                }
            }
            finally
            {
                syncing = false;
            }
        }

        NamedStyle? Current() => currentId is null ? null : kernel.Template.FindStyle(currentId);

        void LoadFields()
        {
            // 程序化赋值也会同步触发 IsCheckedChanged / SelectionChanged（布尔开关拦不住），
            // 不加守卫的话每次切换样式都会把全部字段"真实提交"一遍：逐字段推撤销命令、
            // 全模板 JSON 重序列化、并在列表 SelectionChanged 处理中途整表重建——
            // 真机上两个样式间切换直接卡死（2026-11-04 用户反馈），这里与 RefreshList 同守卫。
            syncing = true;
            try
            {
                var s = Current();
                name.Text = s?.Name ?? string.Empty;
                fontFamily.Text = s?.FontFamily ?? string.Empty;
                fontSize.Text = NumText(s?.FontSizePt);
                bold.IsChecked = s?.Bold;
                italic.IsChecked = s?.Italic;
                underline.IsChecked = s?.Underline;
                strikethrough.IsChecked = s?.Strikethrough;
                overline.IsChecked = s?.Overline;
                textColor.Text = s?.TextColor is { } tc ? ToHex(tc) : string.Empty;
                backgroundColor.Text = s?.BackgroundColor is { } bc ? ToHex(bc) : string.Empty;
                lineSpacing.Text = NumText(s?.LineSpacing);
                letterSpacing.Text = NumText(s?.LetterSpacingPt);
                indent.Text = NumText(s?.IndentMm);
                firstLineIndent.Text = NumText(s?.FirstLineIndentMm);
                hAlign.SelectedItem = s?.HorizontalAlign?.ToString();
                vAlign.SelectedItem = s?.VerticalAlign?.ToString();
                padLeft.Text = NumText(s?.PaddingLeftMm);
                padTop.Text = NumText(s?.PaddingTopMm);
                padRight.Text = NumText(s?.PaddingRightMm);
                padBottom.Text = NumText(s?.PaddingBottomMm);
                borderColor.Text = s?.BorderColor is { } bc2 ? ToHex(bc2) : string.Empty;
                borderWidth.Text = NumText(s?.BorderWidthMm);
                borderDash.Text = s?.BorderDashPatternMm is { } dash
                    ? string.Join(",", dash.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))
                    : string.Empty;
            }
            finally
            {
                syncing = false;
            }
        }

        /// <summary>提交一次样式修改；只刷新左侧清单（名字可能变了），右侧字段保持用户输入，避免重建打断连续编辑。</summary>
        void Commit(string label, Action<NamedStyle> mutate)
        {
            if (currentId is null)
                return;

            try
            {
                kernel.UpdateStyle(currentId, label, mutate);
                ShowMessage(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }

            RefreshList();

            // 对齐下拉提交后从活样式回读：只刷清单时右侧选中项停在旧值，
            // 与 UpdateStyle 写进去的值分叉，看起来就是"选了没变化"。
            // 文本/数值字段不动（会打断连续输入），只回读两个枚举下拉。
            var style = Current();
            syncing = true;
            try
            {
                hAlign.SelectedItem = style?.HorizontalAlign?.ToString();
                vAlign.SelectedItem = style?.VerticalAlign?.ToString();
            }
            finally
            {
                syncing = false;
            }
        }

        /// <summary>数值字段提交：空串清为 null（不设置），非法数字提示且不提交。</summary>
        void CommitNumber(string fieldName, TextBox box, Action<NamedStyle, double?> apply)
        {
            try
            {
                var value = ParseDouble(box.Text ?? string.Empty);
                Commit($"修改样式 {fieldName}", s => apply(s, value));
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        }

        void CommitColor(string fieldName, TextBox box, Action<NamedStyle, SKColor?> apply)
        {
            try
            {
                var color = ParseColor(box.Text ?? string.Empty);
                Commit($"修改样式 {fieldName}", s => apply(s, color));
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        }

        /// <summary>
        /// 给「留空不设置」的 hex 颜色框加色块取色按钮（字色/底色/边框色共用）：
        /// Button.Flyout 弹取色器（自带锚点与轻触关闭），拖选实时回填 hex，关面板才提交；
        /// hex 为空 = 不设置，色块显示中性灰格子而不是误导性的白色。
        /// </summary>
        Control WithColorSwatch(string fieldName, TextBox box, Action<NamedStyle, SKColor?> apply)
        {
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                BorderBrush = Brushes.LightGray,
                Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)), // 未设置 = 中性格子
            };
            ToolTip.SetTip(swatch, "点击打开取色面板；清空文本框表示不设置");

            // ColorView 是取色面板本体（无 ColorPicker 的「色块+下拉」外壳），点色块一次直达取色。
            var picker = new global::Avalonia.Controls.ColorView { IsAlphaEnabled = false, Color = Colors.White };
            var flyout = new Flyout { Placement = PlacementMode.Bottom, Content = picker };
            swatch.Flyout = flyout;

            picker.ColorChanged += (_, e) =>
            {
                swatch.Background = new SolidColorBrush(e.NewColor);
                box.Text = ToHex(new SKColor(e.NewColor.R, e.NewColor.G, e.NewColor.B));
            };
            // 关面板才提交（与 hex 手填同通道），拖色不会把撤销栈灌满。
            flyout.Closed += (_, _) => CommitColor(fieldName, box, apply);

            // hex 框任何变化都同步色块底色（含 LoadFields 的程序化回填）。
            void SyncSwatch()
            {
                swatch.Background = ParseColor(box.Text ?? string.Empty) is { } color
                    ? new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue))
                    : new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            }
            box.TextChanged += (_, _) => SyncSwatch();
            SyncSwatch();

            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            grid.Children.Add(box);
            Grid.SetColumn(swatch, 1);
            grid.Children.Add(swatch);
            return grid;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (syncing)
                return;

            currentId = (list.SelectedItem as ListBoxItem)?.Tag as string;
            LoadFields();
        };

        name.LostFocus += (_, _) => Commit("名称", s => s.Name = (name.Text ?? string.Empty).Trim());
        fontFamily.LostFocus += (_, _) => Commit("字体", s => s.FontFamily = NullIfEmpty(fontFamily.Text));
        fontSize.LostFocus += (_, _) => CommitNumber("字号", fontSize, (s, v) => s.FontSizePt = v);
        bold.IsCheckedChanged += (_, _) => { if (!syncing) Commit("加粗", s => s.Bold = bold.IsChecked); };
        italic.IsCheckedChanged += (_, _) => { if (!syncing) Commit("倾斜", s => s.Italic = italic.IsChecked); };
        underline.IsCheckedChanged += (_, _) => { if (!syncing) Commit("下划线", s => s.Underline = underline.IsChecked); };
        strikethrough.IsCheckedChanged += (_, _) => { if (!syncing) Commit("删除线", s => s.Strikethrough = strikethrough.IsChecked); };
        overline.IsCheckedChanged += (_, _) => { if (!syncing) Commit("上划线", s => s.Overline = overline.IsChecked); };
        textColor.LostFocus += (_, _) => CommitColor("字色", textColor, (s, v) => s.TextColor = v);
        backgroundColor.LostFocus += (_, _) => CommitColor("底色", backgroundColor, (s, v) => s.BackgroundColor = v);
        lineSpacing.LostFocus += (_, _) => CommitNumber("行距", lineSpacing, (s, v) => s.LineSpacing = v);
        letterSpacing.LostFocus += (_, _) => CommitNumber("字间距", letterSpacing, (s, v) => s.LetterSpacingPt = v);
        indent.LostFocus += (_, _) => CommitNumber("左缩进", indent, (s, v) => s.IndentMm = v);
        firstLineIndent.LostFocus += (_, _) => CommitNumber("首行缩进", firstLineIndent, (s, v) => s.FirstLineIndentMm = v);
        hAlign.SelectionChanged += (_, _) =>
        {
            if (!syncing && hAlign.SelectedItem is string ht)
                Commit("水平对齐", s => s.HorizontalAlign = Enum.Parse<HorizontalAlign>(ht));
        };
        vAlign.SelectionChanged += (_, _) =>
        {
            if (!syncing && vAlign.SelectedItem is string vt)
                Commit("垂直对齐", s => s.VerticalAlign = Enum.Parse<VerticalAlign>(vt));
        };
        padLeft.LostFocus += (_, _) => CommitNumber("内边距·左", padLeft, (s, v) => s.PaddingLeftMm = v);
        padTop.LostFocus += (_, _) => CommitNumber("内边距·上", padTop, (s, v) => s.PaddingTopMm = v);
        padRight.LostFocus += (_, _) => CommitNumber("内边距·右", padRight, (s, v) => s.PaddingRightMm = v);
        padBottom.LostFocus += (_, _) => CommitNumber("内边距·下", padBottom, (s, v) => s.PaddingBottomMm = v);
        borderColor.LostFocus += (_, _) => CommitColor("边框色", borderColor, (s, v) => s.BorderColor = v);
        borderWidth.LostFocus += (_, _) => CommitNumber("边框宽", borderWidth, (s, v) => s.BorderWidthMm = v);
        borderDash.LostFocus += (_, _) =>
        {
            try
            {
                var dash = ParseDash(borderDash.Text ?? string.Empty);
                Commit("边框虚线", s => s.BorderDashPatternMm = dash);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        };

        var add = new Button { Content = "新增", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0, 4, 4) };
        add.Click += (_, _) =>
        {
            try
            {
                var style = kernel.AddStyle();
                currentId = style.Id;
                ShowMessage(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }

            RefreshList();
            LoadFields();
        };

        var remove = new Button { Content = "删除", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0, 4, 8) };
        remove.Click += (_, _) =>
        {
            if (currentId is null)
                return;

            try
            {
                kernel.RemoveStyle(currentId);
                currentId = null;
                ShowMessage(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }

            RefreshList();
            LoadFields();
        };

        var left = new DockPanel();
        DockPanel.SetDock(add, Dock.Bottom);
        DockPanel.SetDock(remove, Dock.Bottom);
        left.Children.Add(add);
        left.Children.Add(remove);
        left.Children.Add(list);

        var fields = new StackPanel { Spacing = 6, Margin = new Thickness(4, 8, 8, 8) };
        fields.Children.Add(Group("文本"));
        fields.Children.Add(Row("名称", name));
        fields.Children.Add(Row("字体", fontFamily));
        fields.Children.Add(Row("字号 (pt)", fontSize));
        fields.Children.Add(Checks(bold, italic, underline, strikethrough, overline));
        fields.Children.Add(Row("字色", WithColorSwatch("字色", textColor, (s, v) => s.TextColor = v)));
        fields.Children.Add(Row("底色", WithColorSwatch("底色", backgroundColor, (s, v) => s.BackgroundColor = v)));
        fields.Children.Add(Group("段落"));
        fields.Children.Add(Row("行距", lineSpacing));
        fields.Children.Add(Row("字间距 (pt)", letterSpacing));
        fields.Children.Add(Row("左缩进 (mm)", indent));
        fields.Children.Add(Row("首行缩进 (mm)", firstLineIndent));
        fields.Children.Add(Group("对齐"));
        fields.Children.Add(Row("水平对齐", hAlign));
        fields.Children.Add(Row("垂直对齐", vAlign));
        fields.Children.Add(Group("内边距 (mm)"));
        fields.Children.Add(Row("左", padLeft));
        fields.Children.Add(Row("上", padTop));
        fields.Children.Add(Row("右", padRight));
        fields.Children.Add(Row("下", padBottom));
        fields.Children.Add(Group("边框"));
        fields.Children.Add(Row("边框色", WithColorSwatch("边框色", borderColor, (s, v) => s.BorderColor = v)));
        fields.Children.Add(Row("边框宽 (mm)", borderWidth));
        fields.Children.Add(Row("虚线段 (mm)", borderDash));

        var right = new ScrollViewer
        {
            Content = fields,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(message, Dock.Bottom);
        root.Children.Add(message);
        root.Children.Add(grid);

        RefreshList();
        LoadFields();

        return ModalDialog.ShowAsync(anchor, "样式库", root, 760, 680);
    }

    static TextBlock Group(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.SteelBlue,
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>标签 + 编辑器一行。必须用 Grid「96,*」而不是水平 StackPanel——
    /// 水平 StackPanel 把子项 arrange 成内容宽，<see cref="HorizontalAlignment.Stretch"/> 无效，
    /// 全部文本框会塌成 Watermark 那么窄（实测踩过）。</summary>
    static Control Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
        if (label.Length > 0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    static StackPanel Checks(params CheckBox[] checks)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(102, 0, 0, 0) };
        foreach (var check in checks)
            panel.Children.Add(check);
        return panel;
    }

    static string NumText(double? value) =>
        value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    static SKColor? ParseColor(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        return SKColor.TryParse(trimmed, out var color)
            ? color
            : throw new FormatException($"颜色 \"{text}\" 无法解析，请用 #RRGGBB。");
    }

    static double? ParseDouble(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"\"{text}\" 不是数字。");
    }

    static double[]? ParseDash(string text)
    {
        var parts = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw new FormatException($"虚线段 \"{parts[i]}\" 不是数字，请用逗号分隔，如 2,2。");
        }

        return values;
    }

    static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
