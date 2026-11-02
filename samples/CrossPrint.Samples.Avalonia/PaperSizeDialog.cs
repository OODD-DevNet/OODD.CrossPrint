using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CrossPrint.Core;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 自定义纸张对话框：纸宽必填，纸高留空表示连续纸（长度随内容自适应）。
/// 单位一律 mm，与模板存储口径一致；非法值留在对话框里提示，不会把半截状态写进模板。
/// </summary>
public sealed class PaperSizeDialog : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

        Title = "自定义纸张";
        Width = 400;
        Height = 280;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _width.Text = current.WidthMm.ToString("0.##", Inv);
        _height.Text = current.HeightMm > 0 ? current.HeightMm.ToString("0.##", Inv) : string.Empty;

        var hint = new TextBlock
        {
            Text = "纸高留空表示连续纸（长度随内容自适应）。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
        };

        var orientation = new TextBlock
        {
            Text = current.IsContinuous ? "方向：连续纸固定纵向" : "方向沿用当前设置",
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
            IsDefault = true,
        };
        ok.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 76,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

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

    /// <summary>纸高（mm）；0 表示连续纸。</summary>
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

        var heightText = _height.Text?.Trim() ?? string.Empty;
        var height = 0.0;
        if (heightText.Length > 0
            && (!double.TryParse(heightText, NumberStyles.Float, Inv, out height)
                || !double.IsFinite(height)
                || height < 0))
        {
            Warn("纸高必须是 0 或更大的数字（mm），留空表示连续纸。", _height);
            return;
        }

        WidthMm = width;
        HeightMm = height;
        Confirmed = true;
        Close();
    }

    void Warn(string message, TextBox focus)
    {
        _error.Text = message;
        _error.IsVisible = true;
        focus.Focus();
        focus.SelectAll();
    }
}
