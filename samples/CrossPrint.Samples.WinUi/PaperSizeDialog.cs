using System;
using System.Globalization;
using CrossPrint.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 自定义纸张对话框：纸宽必填；「是否连续纸」是独立开关，纸高一律是设计高度语义——
/// 连续纸时纸高只决定设计画布多高（打印长度随内容自适应），非连续纸时纸高必须大于 0。
/// 单位一律 mm，与模板存储口径一致；校验在 <see cref="CrossPrint.Designer.EditorKernel"/> 之前完成，
/// 非法值留在对话框里改，不会把半截状态写进模板。
/// </summary>
public sealed class PaperSizeDialog : ContentDialog
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>连续纸模式下纸高留空/0 的兜底设计高度（mm），与 EditorKernel 默认一致。</summary>
    public const double DefaultContinuousDesignHeightMm = 297;

    readonly TextBox _width;
    readonly TextBox _height;
    readonly TextBlock _error;

    /// <summary>为当前纸张创建对话框，预填现有尺寸与连续纸开关。</summary>
    /// <param name="current">当前纸张，用于预填。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Title = "自定义纸张";
        PrimaryButtonText = "确定";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        // 示例字段配色按浅色排布；ContentDialog 默认主题跟随应用（系统深色时深底），
        // 显式锁浅色与 StyleLibraryDialog / 各 ContentDialog 对齐（2026-11-06 巡回发现）。
        RequestedTheme = ElementTheme.Light;

        _width = new TextBox
        {
            Header = "纸宽（mm）",
            Text = current.WidthMm.ToString("0.##", Inv),
        };
        // 连续纸开关不在对话框里（右栏纸张属性下的独立开关是唯一入口）。
        _height = new TextBox
        {
            Header = "纸高（mm）",
            // 连续纸时纸高是设计高度，原样预填；0（旧隐式表达）落默认避免预填成 0。
            Text = (current.HeightMm > 0 ? current.HeightMm : DefaultContinuousDesignHeightMm).ToString("0.##", Inv),
        };
        var hint = new TextBlock
        {
            Text = "连续纸由纸张属性里的开关控制；勾选连续纸时，纸高作为设计画布高度。",
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var orientation = new TextBlock
        {
            Text = "方向沿用当前设置（连续纸固定纵向）",
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

    /// <summary>纸高（mm）。连续纸时这是设计画布高度（连续纸开关在纸张属性里），固定纸高时是分页纸高。</summary>
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

        // 纸高必填：连续纸/固定纸高由纸张属性里的独立开关决定，不再用"留空"表达。
        var heightText = (_height.Text ?? string.Empty).Trim();
        if (!double.TryParse(heightText, NumberStyles.Float, Inv, out var height)
            || !double.IsFinite(height) || height <= 0)
        {
            ShowError("纸高必须是大于 0 的数字（mm）。");
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
