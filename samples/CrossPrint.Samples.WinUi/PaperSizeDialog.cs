using System;
using System.Globalization;
using CrossPrint.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 自定义纸张对话框：纸宽必填，纸高留空表示连续纸（长度随内容自适应）。
/// 单位一律 mm，与模板存储口径一致；校验在 <see cref="CrossPrint.Designer.EditorKernel"/> 之前完成，
/// 非法值留在对话框里改，不会把半截状态写进模板。
/// </summary>
public sealed class PaperSizeDialog : ContentDialog
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly TextBox _width;
    readonly TextBox _height;
    readonly TextBlock _error;

    /// <summary>为当前纸张创建对话框，预填现有尺寸。</summary>
    /// <param name="current">当前纸张，用于预填。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Title = "自定义纸张";
        PrimaryButtonText = "确定";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        _width = new TextBox
        {
            Header = "纸宽（mm）",
            Text = current.WidthMm.ToString("0.##", Inv),
        };
        _height = new TextBox
        {
            Header = "纸高（mm）",
            PlaceholderText = "留空表示连续纸",
            Text = current.HeightMm > 0 ? current.HeightMm.ToString("0.##", Inv) : string.Empty,
        };
        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var hint = new TextBlock
        {
            Text = "纸高留空表示连续纸（长度随内容自适应）。",
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var orientation = new TextBlock
        {
            Text = current.IsContinuous ? "方向：连续纸固定纵向" : "方向沿用当前设置",
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };

        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(_width);
        panel.Children.Add(_height);
        panel.Children.Add(hint);
        panel.Children.Add(orientation);
        panel.Children.Add(_error);
        Content = panel;

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    /// <summary>纸宽（mm），仅在对话框结果为 <see cref="ContentDialogResult.Primary"/> 后有效。</summary>
    public double WidthMm { get; private set; }

    /// <summary>纸高（mm）；0 表示连续纸。</summary>
    public double HeightMm { get; private set; }

    void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 先假定不通过；只有校验全部成功才让对话框关闭。
        args.Cancel = true;

        var widthText = (_width.Text ?? string.Empty).Trim();
        if (!double.TryParse(widthText, NumberStyles.Float, Inv, out var width)
            || !double.IsFinite(width) || width <= 0)
        {
            ShowError("纸宽必须是大于 0 的数字（mm）。");
            _width.Focus(FocusState.Programmatic);
            return;
        }

        var heightText = (_height.Text ?? string.Empty).Trim();
        var height = 0.0;
        if (heightText.Length > 0
            && (!double.TryParse(heightText, NumberStyles.Float, Inv, out height)
                || !double.IsFinite(height) || height < 0))
        {
            ShowError("纸高必须是 0 或更大的数字（mm），留空表示连续纸。");
            _height.Focus(FocusState.Programmatic);
            return;
        }

        WidthMm = width;
        HeightMm = height;
        args.Cancel = false;
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }
}
