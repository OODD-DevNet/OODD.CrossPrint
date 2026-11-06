using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CrossPrint.Core;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 自定义纸张对话框：纸宽、纸高都必须填大于 0 的数字。
/// 「是否连续纸」是纸张属性里的独立开关（不在对话框里）；勾选连续纸时纸高作为设计画布高度。
/// 单位一律 mm，与模板存储口径一致；非法值留在对话框里提示，不会把半截状态写进模板。
/// 是 UserControl 而非 Window：由 <see cref="ModalDialog"/> 决定桌面包窗口还是浏览器挂 overlay
/// （浏览器单视图没有 WindowingPlatform，Window 一实例化就抛）。
/// </summary>
public sealed class PaperSizeDialog : UserControl
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>连续纸模式下纸高留空/0 的兜底设计高度（mm），与 EditorKernel 默认一致。</summary>
    public const double DefaultContinuousDesignHeightMm = 297;

    readonly TextBox _width = new();
    readonly TextBox _height = new();
    readonly TextBlock _error = new()
    {
        Foreground = Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };

    /// <summary>为当前纸张创建对话框，预填现有尺寸。</summary>
    /// <param name="current">当前纸张，用于预填。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);

        _width.Text = current.WidthMm.ToString("0.##", Inv);
        // 设计高度：连续纸模板的纸高是设计高度，原样预填；0（旧隐式表达）落默认。
        _height.Text = (current.HeightMm > 0 ? current.HeightMm : DefaultContinuousDesignHeightMm)
            .ToString("0.##", Inv);
        // 连续纸开关不在对话框里（用户钦定：右栏纸张属性下的独立开关才是唯一入口），
        // 纸高一律必填；连续纸时纸高作为设计画布高度。

        var orientation = new TextBlock
        {
            Text = "方向沿用当前设置（连续纸固定纵向）",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
        };

        var hint = new TextBlock
        {
            // 连续纸由纸张属性里的独立开关控制（不在对话框里）；连续纸时此处的纸高是设计画布高度。
            Text = "连续纸由纸张属性里的开关控制；勾选连续纸时，纸高作为设计画布高度。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
        };

        var form = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(14, 12, 14, 8),
        };
        form.Children.Add(Field("纸宽（mm）", _width));
        form.Children.Add(Field("纸高（mm）", _height));
        form.Children.Add(hint);
        form.Children.Add(orientation);
        form.Children.Add(_error);

        var ok = new Button
        {
            Content = "确定",
            MinWidth = 76,
        };
        ok.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 76,
        };
        cancel.Click += (_, _) => ModalDialog.CloseHost(this);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(14, 0, 14, 12),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(form);
        Content = root;
    }

    /// <summary>用户是否点了“确定”并通过校验；取消或直接关闭窗口时为 <c>false</c>。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>纸宽（mm），仅在 <see cref="Confirmed"/> 为 <c>true</c> 后有效。</summary>
    public double WidthMm { get; private set; }

    /// <summary>纸高（mm）；留空/0 表示连续纸（内核落默认设计高度 297 作画布基准）。</summary>
    public double HeightMm { get; private set; }

    static Grid Field(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("96,*"),
            RowDefinitions = new RowDefinitions("Auto"),
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    void Commit()
    {
        var widthText = _width.Text?.Trim() ?? string.Empty;
        if (!double.TryParse(widthText, NumberStyles.Float, Inv, out var width)
            || !double.IsFinite(width)
            || width <= 0)
        {
            Warn("纸宽必须是大于 0 的数字（mm）。", _width);
            return;
        }

        // 纸高必填：连续纸/固定纸高由纸张属性里的独立开关决定，不再用"留空"表达（用户没法设计）。
        // 连续纸时这里的纸高是设计画布高度；固定纸高时就是分页纸高。
        var heightText = _height.Text?.Trim() ?? string.Empty;
        if (!double.TryParse(heightText, NumberStyles.Float, Inv, out var height)
            || !double.IsFinite(height)
            || height <= 0)
        {
            Warn("纸高必须是大于 0 的数字（mm）。", _height);
            return;
        }

        WidthMm = width;
        HeightMm = height;
        Confirmed = true;
        ModalDialog.CloseHost(this);
    }

    void Warn(string message, TextBox focus)
    {
        _error.Text = message;
        _error.IsVisible = true;
        focus.Focus();
        focus.SelectAll();
    }
}
