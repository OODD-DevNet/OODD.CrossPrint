using System;
using System.Globalization;
using System.Threading.Tasks;
using CrossPrint.Core;
using CrossPrint.Designer;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrossPrint.Samples.Uno;

/// <summary>
/// 纸张面板：预设下拉、横/纵向切换、自定义纸张对话框，以及当前实际页面尺寸说明。
/// </summary>
/// <remarks>
/// <para>
/// 语义与 WinForms 示例一致：所有纸张变更都调 <see cref="EditorKernel"/> 的纸张 API
/// （<see cref="EditorKernel.SetPaperPreset"/>、<see cref="EditorKernel.RotatePaper"/>、
/// <see cref="EditorKernel.SetCustomPaper"/>），内核自己整页适配，面板不再碰视口。
/// </para>
/// <para>
/// 回填纪律：订阅 <see cref="EditorKernel.TemplateChanged"/>，在 <see cref="Sync"/> 里把当前纸张
/// 映射回下拉项、按钮可用性与说明文字。设置 <see cref="ComboBox.SelectedIndex"/> 会同步触发
/// <see cref="ComboBox.SelectionChanged"/>，所以整个回填过程用 <c>_syncing</c> 守卫，回调里直接返回，
/// 不会把"回填"误当成用户选择而再次下发纸张命令。
/// </para>
/// </remarks>
internal sealed class PaperPanel : UserControl
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 顺序即下拉顺序；Key 为 null 的那项是"自定义…"，选中时弹对话框而不是直接调内核。
    static readonly PaperChoice[] Choices =
    [
        new(PaperTypePresets.A3, "A3（297×420）"),
        new(PaperTypePresets.A4, "A4（210×297）"),
        new(PaperTypePresets.A5, "A5（148×210）"),
        new(PaperTypePresets.B5, "B5（176×250）"),
        new(PaperTypePresets.Receipt80, "80mm 连续纸"),
        new(PaperTypePresets.Receipt58, "58mm 连续纸"),
        new(PaperTypePresets.Label100x150, "100×150 面单"),
        new(null, "自定义…"),
    ];

    EditorKernel _kernel;
    readonly Action? _focusCanvas;
    readonly ComboBox _combo = new();
    readonly Button _rotateButton = new();
    readonly Button _customButton = new();
    readonly TextBlock _info = new();

    bool _syncing;
    bool _dialogOpen;

    /// <summary>创建纸张面板并立即按当前模板回填一次。</summary>
    /// <param name="kernel">编辑器内核；面板直接对它调用纸张命令。</param>
    /// <param name="focusCanvas">操作完成后把焦点还给画布的回调，可为 null。</param>
    public PaperPanel(EditorKernel kernel, Action? focusCanvas = null)
    {
        _kernel = kernel;
        _focusCanvas = focusCanvas;

        // 节标题即分组（不用 GroupBox 外框），留白收紧，把竖向空间让给下面的工具箱。
        var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };

        stack.Children.Add(new TextBlock
        {
            Text = "纸张",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        _combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _combo.ItemsSource = Array.ConvertAll(Choices, choice => choice.Label);
        _combo.SelectionChanged += (_, _) => OnChoiceChanged();
        stack.Children.Add(_combo);

        var buttons = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _rotateButton.Content = "转横向";
        _rotateButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rotateButton.Margin = new Thickness(0, 0, 3, 0);
        _rotateButton.Click += (_, _) => ToggleOrientation();

        _customButton.Content = "自定义…";
        _customButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _customButton.Margin = new Thickness(3, 0, 0, 0);
        _customButton.Click += async (_, _) => await ShowCustomPaperDialogAsync();

        Grid.SetColumn(_rotateButton, 0);
        Grid.SetColumn(_customButton, 1);
        buttons.Children.Add(_rotateButton);
        buttons.Children.Add(_customButton);
        stack.Children.Add(buttons);

        _info.FontSize = 11;
        _info.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        _info.TextWrapping = TextWrapping.Wrap;
        _info.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_info);

        Content = stack;
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);

        _kernel.TemplateChanged += Sync;
        Sync();
    }

    /// <summary>切换到另一个内核（宿主换模板后调用）。</summary>
    public void Attach(EditorKernel kernel)
    {
        _kernel.TemplateChanged -= Sync;
        _kernel = kernel;
        _kernel.TemplateChanged += Sync;
        Sync();
    }

    /// <summary>需要回显到状态栏的消息。</summary>
    public event Action<string>? StatusRequested;

    /// <summary>切换纸张方向；连续纸没有固定页高，直接给出提示而不调内核。</summary>
    public void ToggleOrientation()
    {
        var paper = _kernel.Template.Paper;
        if (paper.IsContinuous)
        {
            Report("连续纸没有固定页高，不能旋转。");
            return;
        }

        try
        {
            _kernel.RotatePaper();
        }
        catch (Exception ex)
        {
            Report("设置纸张失败：" + ex.Message);
            Sync();
            return;
        }

        Sync();
        Report(_kernel.Template.Paper.Orientation == PaperOrientation.Landscape
            ? "纸张已转为横向。"
            : "纸张已转为纵向。");
        _focusCanvas?.Invoke();
    }

    /// <summary>
    /// 弹出自定义纸张对话框：纸宽必填且大于 0，纸高留空表示连续纸（0）。
    /// 非法输入留在对话框里提示（不关闭），确定后调 <see cref="EditorKernel.SetCustomPaper"/>；
    /// 取消时下拉恢复当前纸张。
    /// </summary>
    /// <returns>对话框关闭（确定或取消）后完成的任务。</returns>
    public async Task ShowCustomPaperDialogAsync()
    {
        if (_dialogOpen)
            return;

        if (XamlRoot is null)
        {
            Report("窗口尚未就绪，无法打开自定义纸张对话框。");
            Sync();
            return;
        }

        var paper = _kernel.Template.Paper;

        var widthBox = new TextBox
        {
            Text = paper.WidthMm.ToString("0.##", Inv),
            Header = "纸宽（mm）",
            PlaceholderText = "例如 210",
        };

        var heightBox = new TextBox
        {
            Text = paper.HeightMm > 0 ? paper.HeightMm.ToString("0.##", Inv) : string.Empty,
            Header = "纸高（mm）",
            PlaceholderText = "留空表示连续纸",
        };

        var hint = new TextBlock
        {
            Text = "纸高留空表示连续纸（长度随内容自适应）。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 2),
        };

        var orientation = new TextBlock
        {
            Text = paper.IsContinuous ? "方向：连续纸固定纵向" : "方向沿用当前设置",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
            TextWrapping = TextWrapping.Wrap,
        };

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var content = new StackPanel { Width = 300 };
        content.Children.Add(widthBox);
        content.Children.Add(heightBox);
        content.Children.Add(hint);
        content.Children.Add(orientation);
        content.Children.Add(error);

        var width = paper.WidthMm;
        var height = Math.Max(0, paper.HeightMm);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义纸张",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };

        // 校验与写入都发生在"确定"里；不合法就 args.Cancel = true 把对话框留在原地，用户改完再提交。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            error.Text = string.Empty;
            error.Visibility = Visibility.Collapsed;

            if (!double.TryParse((widthBox.Text ?? string.Empty).Trim(), NumberStyles.Float, Inv, out var parsedWidth)
                || !double.IsFinite(parsedWidth)
                || parsedWidth <= 0)
            {
                ShowDialogError(error, "纸宽必须是大于 0 的数字（mm）。", widthBox);
                args.Cancel = true;
                return;
            }

            var heightText = (heightBox.Text ?? string.Empty).Trim();
            var parsedHeight = 0.0;
            if (heightText.Length > 0
                && (!double.TryParse(heightText, NumberStyles.Float, Inv, out parsedHeight)
                    || !double.IsFinite(parsedHeight)
                    || parsedHeight < 0))
            {
                ShowDialogError(error, "纸高必须是 0 或更大的数字（mm），留空表示连续纸。", heightBox);
                args.Cancel = true;
                return;
            }

            try
            {
                _kernel.SetCustomPaper(parsedWidth, parsedHeight);
            }
            catch (Exception ex)
            {
                ShowDialogError(error, "设置纸张失败：" + ex.Message, widthBox);
                args.Cancel = true;
                return;
            }

            width = parsedWidth;
            height = parsedHeight;
        };

        _dialogOpen = true;
        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                // 取消：下拉恢复当前纸张，焦点还给画布。
                Sync();
                _focusCanvas?.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            Report("打开自定义纸张对话框失败：" + ex.Message);
            Sync();
            return;
        }
        finally
        {
            _dialogOpen = false;
        }

        Report(height > 0
            ? $"自定义纸张：{width:0.##} × {height:0.##} mm。"
            : $"自定义连续纸：宽 {width:0.##} mm，长度随内容自适应。");
        _focusCanvas?.Invoke();
    }

    void OnChoiceChanged()
    {
        if (_syncing || _combo.SelectedIndex < 0 || _combo.SelectedIndex >= Choices.Length)
            return;

        var choice = Choices[_combo.SelectedIndex];
        if (choice.Key is null)
        {
            // "自定义…"是动作而不是状态：弹框期间下拉先回到当前纸张，取消后自然停在原值，
            // 确定后由 SetCustomPaper 的 TemplateChanged 把它回填成"自定义…"。
            _ = ShowCustomPaperDialogAsync();
            Sync();
            return;
        }

        try
        {
            _kernel.SetPaperPreset(choice.Key);
            var paper = _kernel.Template.Paper;
            Report(paper.IsContinuous
                ? $"纸张已设为连续纸：{paper.PageWidthMm:0.##} mm 宽。"
                : $"纸张已设为 {paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm。");
        }
        catch (Exception ex)
        {
            Report("设置纸张失败：" + ex.Message);
        }

        Sync();
        _focusCanvas?.Invoke();
    }

    /// <summary>把当前纸张回填到下拉、按钮与说明文字；设置 SelectedIndex 引发的回调被 <c>_syncing</c> 挡住。</summary>
    void Sync()
    {
        if (_syncing)
            return;

        _syncing = true;
        try
        {
            var paper = _kernel.Template.Paper;
            var index = Array.FindIndex(
                Choices,
                choice => choice.Key is not null
                    && string.Equals(choice.Key, paper.Preset, StringComparison.OrdinalIgnoreCase));
            _combo.SelectedIndex = index >= 0 ? index : Choices.Length - 1;

            var continuous = paper.IsContinuous;
            _rotateButton.IsEnabled = !continuous;
            _rotateButton.Content = paper.Orientation == PaperOrientation.Landscape ? "转纵向" : "转横向";
            _info.Text = DescribePaper(paper);
        }
        finally
        {
            _syncing = false;
        }
    }

    static string DescribePaper(PaperSpec paper)
    {
        var name = paper.Preset is null || paper.Preset == PaperTypePresets.Custom ? "自定义" : paper.Preset;
        return paper.IsContinuous
            ? $"{name}：{paper.PageWidthMm:0.##} mm 宽，长度随内容（固定纵向）"
            : $"{name}：{paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm（{(paper.Orientation == PaperOrientation.Landscape ? "横向" : "纵向")}）";
    }

    static void ShowDialogError(TextBlock error, string message, TextBox focus)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        focus.Focus(FocusState.Programmatic);
    }

    void Report(string message) => StatusRequested?.Invoke(message);

    sealed record PaperChoice(string? Key, string Label);
}
