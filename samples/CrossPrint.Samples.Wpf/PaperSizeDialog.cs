using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrossPrint.Core;

namespace CrossPrint.Samples.Wpf;

/// <summary>
/// 自定义纸张对话框：纸宽必填且大于 0；「是否连续纸」是独立开关，纸高一律是设计高度语义——
/// 连续纸时纸高只决定设计画布多高（打印长度随内容自适应），非连续纸时纸高必须大于 0。
/// 单位一律 mm，与模板存储口径一致；非法值留在对话框内提示，不会把半截状态写进模板。
/// </summary>
internal sealed class PaperSizeDialog : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>连续纸模式下纸高留空/0 的兜底设计高度（mm），与 EditorKernel 默认一致。</summary>
    public const double DefaultContinuousDesignHeightMm = 297;

    readonly TextBox _width;
    readonly TextBox _height;
    readonly TextBlock _hint;
    readonly TextBlock _error;

    /// <summary>为当前纸张创建对话框，预填现有尺寸。</summary>
    /// <param name="current">当前纸张，用于预填宽高与方向提示。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Title = "自定义纸张";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _width = new TextBox
        {
            Text = current.WidthMm.ToString("0.##", Inv),
            MinWidth = 180,
            Margin = new Thickness(0, 3, 0, 3),
        };

        _height = new TextBox
        {
            // 连续纸时纸高是设计高度，原样预填；0（旧隐式表达）落默认避免预填成 0。
            Text = (current.HeightMm > 0 ? current.HeightMm : DefaultContinuousDesignHeightMm).ToString("0.##", Inv),
            MinWidth = 180,
            Margin = new Thickness(0, 3, 0, 3),
        };

        _hint = new TextBlock
        {
            // 连续纸开关不在对话框里（右栏纸张属性下的独立开关是唯一入口）。
            Text = "连续纸由纸张属性里的开关控制；勾选连续纸时，纸高作为设计画布高度。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2),
        };

        _error = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 7; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabel(grid, "纸宽（mm）", 0);
        AddEditor(grid, _width, 0);

        AddLabel(grid, "纸高（mm）", 1);
        AddEditor(grid, _height, 1);

        Grid.SetRow(_hint, 2);
        Grid.SetColumn(_hint, 0);
        Grid.SetColumnSpan(_hint, 2);
        grid.Children.Add(_hint);

        var orientation = new TextBlock
        {
            Text = "方向沿用当前设置（连续纸固定纵向）",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetRow(orientation, 3);
        Grid.SetColumn(orientation, 0);
        Grid.SetColumnSpan(orientation, 2);
        grid.Children.Add(orientation);

        Grid.SetRow(_error, 4);
        Grid.SetColumn(_error, 0);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            MinWidth = 76,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 76,
            Padding = new Thickness(10, 3, 10, 3),
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 5);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;

        _width.TextChanged += (_, _) => HideError();
        _height.TextChanged += (_, _) => HideError();
        Loaded += (_, _) =>
        {
            _width.Focus();
            _width.SelectAll();
        };
    }

    /// <summary>纸宽（mm），仅在对话框返回 true 后有效。</summary>
    public double WidthMm { get; private set; }

    /// <summary>纸高（mm）。连续纸时这是设计画布高度（连续纸开关在纸张属性里），固定纸高时是分页纸高。</summary>
    public double HeightMm { get; private set; }

    static void AddLabel(Grid grid, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 8, 3),
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
    }

    static void AddEditor(Grid grid, TextBox box, int row)
    {
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
    }

    void Commit()
    {
        if (!double.TryParse(_width.Text.Trim(), NumberStyles.Float, Inv, out var width)
            || !double.IsFinite(width)
            || width <= 0)
        {
            ShowError("纸宽必须是大于 0 的数字（mm）。", _width);
            return;
        }

        // 纸高必填：连续纸/固定纸高由纸张属性里的独立开关决定，不再用"留空"表达。
        var heightText = _height.Text.Trim();
        if (!double.TryParse(heightText, NumberStyles.Float, Inv, out var height)
            || !double.IsFinite(height)
            || height <= 0)
        {
            ShowError("纸高必须是大于 0 的数字（mm）。", _height);
            return;
        }

        WidthMm = width;
        HeightMm = height;
        DialogResult = true;
    }

    void ShowError(string message, TextBox focus)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        focus.Focus();
        focus.SelectAll();
    }

    void HideError()
    {
        if (_error.Visibility == Visibility.Visible)
            _error.Visibility = Visibility.Collapsed;
    }
}
