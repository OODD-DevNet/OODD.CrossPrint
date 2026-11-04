using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 「hex 文本框 + 色块按钮」颜色字段：点色块经 <see cref="Flyout"/> 直接弹出取色面板本体（ColorView）。
/// 注意不要用 ColorPicker——它自带「色块+下拉箭头」外壳，塞进 Flyout 要点两次才见取色面板。
/// 面板拖选实时回填文本框与色块，关闭面板才提交——拖一次颜色不会把撤销栈灌满。
/// 属性面板与水印设置共用。
/// </summary>
internal static class ColorField
{
    public sealed record Handle(Grid Root, TextBox Box, Button Swatch);

    /// <summary>创建字段。onCommit 在回车/失焦/关闭取色面板时收到原始文本（空串 = 清除）。</summary>
    public static Handle Create(string? initialHex, Action<string?> onCommit, string? tooltip = null)
    {
        var box = new TextBox
        {
            Text = initialHex ?? string.Empty,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var swatch = new Button
        {
            Width = 32,
            Height = 32,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.LightGray,
            Background = new SolidColorBrush(ToColor(initialHex) ?? Colors.White),
        };
        if (tooltip is not null)
            ToolTip.SetTip(swatch, tooltip);

        var picker = new global::Avalonia.Controls.ColorView
        {
            IsAlphaEnabled = true,
            Color = ToColor(initialHex) ?? Colors.White,
        };
        // 用 Button.Flyout 而不是裸 Popup：Flyout 由按钮自带锚点、自动开合与轻触关闭；
        // 裸 Popup（哪怕补了 PlacementTarget）在部分宿主路径下依旧静默不显示（真机实测 2026-11-04）。
        // ColorView 是面板本体（无 ColorPicker 的「色块+下拉」外壳），点色块一次直达取色。
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = picker,
        };
        swatch.Flyout = flyout;

        picker.ColorChanged += (_, e) =>
        {
            swatch.Background = new SolidColorBrush(e.NewColor);
            box.Text = e.NewColor.A == 255
                ? $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"
                : $"#{e.NewColor.A:X2}{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        };
        // 关面板才提交：拖色时 Text 一直在变，逐帧提交会把撤销栈灌满。
        flyout.Closed += (_, _) => onCommit(box.Text);

        void CommitText() => onCommit(box.Text);
        box.LostFocus += (_, _) => CommitText();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                CommitText();
            }
        };

        var root = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        root.Children.Add(box);
        Grid.SetColumn(swatch, 1);
        root.Children.Add(swatch);
        return new Handle(root, box, swatch);
    }

    /// <summary>程序化同步显示值（换模板刷新用）；与当前显示相同则跳过，避免重建循环。</summary>
    public static void Update(Handle handle, string? hex)
    {
        var text = hex ?? string.Empty;
        if (handle.Box.Text != text)
        {
            handle.Box.Text = text;
            handle.Swatch.Background = new SolidColorBrush(ToColor(text) ?? Colors.White);
        }
        else
        {
            handle.Swatch.Background = new SolidColorBrush(ToColor(text) ?? Colors.White);
        }
    }

    static Color? ToColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        return SKColor.TryParse(hex.Trim(), out var color)
            ? Color.FromRgb(color.Red, color.Green, color.Blue)
            : null;
    }
}
