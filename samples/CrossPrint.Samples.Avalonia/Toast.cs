using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 轻量 Toast 通知（Semi Notification 风格）：窗口右上角滑出的成功/提示卡片，3 秒自动消失。
/// 不拦截鼠标（IsHitTestVisible=false），不打断操作；同一时间只保留最新一条。
/// 桌面/浏览器共用：都从锚点向上找 <see cref="IModalOverlayHost"/> 的 ModalLayer 挂载。
/// </summary>
internal static class Toast
{
    static readonly AttachedProperty<bool> ToastMarkProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("ToastMark", typeof(Toast));

    /// <summary>弹一条 Toast。message 为空时只显示标题。</summary>
    public static void Show(Visual anchor, string title, string? message = null)
    {
        Panel? layer = null;
        foreach (var ancestor in anchor.GetSelfAndVisualAncestors())
        {
            if (ancestor is IModalOverlayHost host && host.ModalLayer is { } l)
            {
                layer = l;
                break;
            }
        }
        if (layer is null)
            return; // 找不到挂载层就静默放弃（状态栏仍有反馈）。

        // 同一位置只留一条：先清掉旧的。
        for (var i = layer.Children.Count - 1; i >= 0; i--)
            if (layer.Children[i] is Visual v && v.GetValue(ToastMarkProperty))
                layer.Children.RemoveAt(i);

        var icon = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(0x34, 0xB3, 0x4A)),
            Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        });
        if (!string.IsNullOrEmpty(message))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x62, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
            });
        }

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE9, 0xF0)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #33000000"),
            Padding = new Thickness(14, 11),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { icon, textStack },
            },
        };

        var toastHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 14, 0),
            Child = card,
            IsHitTestVisible = false, // 通知绝不挡操作。
        };
        toastHost.SetValue(ToastMarkProperty, true);

        layer.Children.Add(toastHost);

        // 3 秒自动消失。
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => layer.Children.Remove(toastHost)));
    }
}
