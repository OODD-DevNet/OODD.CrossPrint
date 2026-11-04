using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 浏览器单视图的模态 overlay 宿主：提供挂 overlay 的层（MainView 实现）。
/// </summary>
internal interface IModalOverlayHost
{
    /// <summary>模态 overlay 的挂载层；显示期间会往里塞遮罩，关闭时移除。</summary>
    Panel? ModalLayer { get; }
}

/// <summary>
/// 跨端模态对话框宿主。
/// 桌面：包成 Window 走 ShowDialog，行为与直接 new Window 一致。
/// 浏览器：单视图生命周期没有 WindowingPlatform（连 new Window 都会抛），
/// 把内容挂进 <see cref="IModalOverlayHost"/>（MainView）根部的 overlay 层——
/// 半透明遮罩 + 居中卡片 + 标题栏（标题 + ✕）+ Esc 关闭，模拟模态。
/// 弹窗内部的关闭按钮统一调 <see cref="CloseHost"/>，两端都走通。
/// </summary>
internal static class ModalDialog
{
    /// <summary>挂在 overlay/内容根上的关闭句柄（attached，向上查找触发）。</summary>
    static readonly AttachedProperty<Action> CloseRequestProperty =
        AvaloniaProperty.RegisterAttached<Visual, Action>("CloseRequest", typeof(ModalDialog));

    static void SetCloseRequest(Visual visual, Action close) => visual.SetValue(CloseRequestProperty, close);

    static Action? GetCloseRequest(Visual visual) => visual.GetValue(CloseRequestProperty);

    /// <summary>从任意子控件向上找最近的关闭句柄并触发（弹窗内「关闭/确定/取消」按钮用）。</summary>
    public static void CloseHost(Visual from)
    {
        foreach (var ancestor in from.GetSelfAndVisualAncestors())
        {
            if (ancestor is Visual visual && GetCloseRequest(visual) is { } close)
            {
                close();
                return;
            }
        }
    }

    /// <summary>通用确认框：确定 = true，取消/Esc/✕ = false。两端通用（浏览器 overlay 会叠在已有弹窗之上）。</summary>
    public static Task<bool> ConfirmAsync(Visual anchor, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = false;

        var body = BuildMessageBody("!", Color.FromRgb(0xF5, 0xA6, 0x23), message);
        var ok = DialogButton("确定", primary: true);
        var cancel = DialogButton("取消", primary: false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel { Margin = new Thickness(24, 20) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);

        ok.Click += (_, _) =>
        {
            confirmed = true;
            CloseHost(root);
        };
        cancel.Click += (_, _) => CloseHost(root);

        _ = ShowAsync(anchor, title, root, 420, 190, 360, 150, canResize: false,
            onClosed: () => tcs.TrySetResult(confirmed));
        return tcs.Task;
    }

    /// <summary>通用单行输入框：确定返回输入文本，取消/Esc/✕ 返回 null。</summary>
    public static Task<string?> PromptAsync(Visual anchor, string title, string label, string initial = "")
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = (string?)null;

        var input = new TextBox
        {
            Text = initial,
            Height = 34,
            CornerRadius = new CornerRadius(4),
        };
        var ok = DialogButton("确定", primary: true);
        var cancel = DialogButton("取消", primary: false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Children =
            {
                new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) },
                input,
                buttons,
            },
        };

        void Accept()
        {
            result = (input.Text ?? string.Empty).Trim();
            CloseHost(root);
        }
        ok.Click += (_, _) => Accept();
        // 取消按钮：返回 null（未赋 result 即 null），与 ConfirmAsync/ThreeWayAsync 对齐。
        cancel.Click += (_, _) => CloseHost(root);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
        };

        _ = ShowAsync(anchor, title, root, 420, 210, 360, 160, canResize: false,
            onClosed: () => tcs.TrySetResult(result));

        // 弹出即聚焦，方便直接改名。
        Dispatcher.UIThread.Post(() =>
        {
            input.Focus();
            input.SelectAll();
        });
        return tcs.Task;
    }

    /// <summary>显示模态内容。桌面与浏览器共用；onClosed 在关闭后回调（如刷新面板）。</summary>
    public static async Task ShowAsync(
        Visual anchor,
        string title,
        Control content,
        double width,
        double height,
        double minWidth = 0,
        double minHeight = 0,
        bool canResize = true,
        Action? onClosed = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        void Finish()
        {
            if (finished)
                return;
            finished = true;
            onClosed?.Invoke();
            tcs.TrySetResult();
        }

#if BROWSER
        ShowOverlay(anchor, title, content, width, height, minWidth, Finish);
#else
        var owner = TopLevel.GetTopLevel(anchor) as Window;
        var window = CreateWindow(anchor, title, content, width, height, minWidth, minHeight, canResize, Finish);
        if (owner is null)
            window.Show();
        else
            await window.ShowDialog(owner);
#endif
        await tcs.Task;
    }

    /// <summary>
    /// 创建带自绘标题栏的窗口（模态与非模态共用）。调用方自行决定 Show / ShowDialog。
    /// onClosed 可空；未传时用窗口 Closed 自身（无额外回调）。
    /// </summary>
    public static Window CreateWindow(
        Visual anchor,
        string title,
        Control content,
        double width,
        double height,
        double minWidth = 0,
        double minHeight = 0,
        bool canResize = true,
        Action? onClosed = null)
    {
        var owner = TopLevel.GetTopLevel(anchor) as Window;
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = minWidth > 0 ? minWidth : width,
            MinHeight = minHeight > 0 ? minHeight : height,
            CanResize = canResize,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
        };
        window.Content = BuildDesktopChrome(window, title, content);
        // 去掉系统表头，用自绘标题栏（Semi 对话框观感）；保留系统边框以维持圆角与缩放边。
        window.SystemDecorations = SystemDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        window.ExtendClientAreaTitleBarHeightHint = -1;
        if (onClosed is not null)
            window.Closed += (_, _) => onClosed();
        // 桌面端也挂关闭句柄：弹窗内确定/取消按钮统一走 CloseHost，缺了它按钮全是"没反应"。
        SetCloseRequest(window, window.Close);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
        };
        return window;
    }

    /// <summary>
    /// 自绘窗口 chrome：顶部标题栏（标题 + ✕ 关闭，可拖动窗口）+ 原内容。
    /// 标题栏背景必须是**非空画刷**——null 背景不参与命中测试，拖动就失效。
    /// </summary>
    static Control BuildDesktopChrome(Window window, string title, Control content)
    {
        var closeButton = new Button
        {
            Content = "✕",
            Width = 38,
            Height = 30,
            MinHeight = 0,
            Padding = new Thickness(0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x62, 0x66)),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, _) => window.Close();

        var titleBar = new Grid
        {
            Height = 38,
            Background = Brushes.White,
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
        };
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(titleText, 0);
        titleBar.Children.Add(titleText);
        Grid.SetColumn(closeButton, 2);
        titleBar.Children.Add(closeButton);

        // 标题栏空白区按下拖动窗口；标题文本/关闭按钮自身的事件不受影响。
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                try
                {
                    window.BeginMoveDrag(e);
                }
                catch
                {
                    // 非指针设备触发等场景拖不动就算了。
                }
            }
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(content);
        return root;
    }

    /// <summary>
    /// 三选一确认框（退出场景）：取消 → null，<paramref name="okText"/> → "ok"，
    /// <paramref name="neutralText"/>（主按钮）→ "neutral"。
    /// </summary>
    public static Task<string?> ThreeWayAsync(
        Visual anchor,
        string title,
        string message,
        string neutralText,
        string okText)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = (string?)null;

        var body = BuildMessageBody("!", Color.FromRgb(0xF5, 0xA6, 0x23), message);
        var neutral = DialogButton(neutralText, primary: true);
        var ok = DialogButton(okText, primary: false);
        var cancel = DialogButton("取消", primary: false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        buttons.Children.Add(neutral);

        var root = new DockPanel { Margin = new Thickness(24, 20) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);

        ok.Click += (_, _) =>
        {
            result = "ok";
            CloseHost(root);
        };
        neutral.Click += (_, _) =>
        {
            result = "neutral";
            CloseHost(root);
        };
        cancel.Click += (_, _) => CloseHost(root);

        _ = ShowAsync(anchor, title, root, 460, 190, 400, 150, canResize: false,
            onClosed: () => tcs.TrySetResult(result));
        return tcs.Task;
    }

    /// <summary>弹窗内主/次按钮的统一形态（Semi 风格：32 高、4 圆角、主按钮蓝底白字）。</summary>
    static Button DialogButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 76,
            Height = 32,
            MinHeight = 0,
            Padding = new Thickness(16, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (primary)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
            button.Foreground = Brushes.White;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
        }
        return button;
    }

    /// <summary>消息体：左侧圆形图标 + 右侧文案（垂直居中），Semi 对话框的排版。</summary>
    static Control BuildMessageBody(string glyph, Color iconBackground, string message)
    {
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(iconBackground),
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                },
            },
        };
    }

#if BROWSER
    /// <summary>浏览器 overlay 模态：遮罩 + 居中卡片 + 标题栏（✕）+ Esc。</summary>
    static void ShowOverlay(
        Visual anchor, string title, Control content,
        double width, double height, double minWidth, Action finish)
    {
        // 注意：本仓 Avalonia 11.3 没有 IPanel（Avalonia 12 才引入），直接用 Panel。
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
            throw new InvalidOperationException("找不到模态 overlay 宿主（MainView 尚未挂树？）");

        var closeButton = new Button
        {
            Content = "✕",
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 10, 8, 8),
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(closeButton, 1);
        titleBar.Children.Add(closeButton);

        content.Margin = new Thickness(12, 0, 12, 10);

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDF, 0xE8)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 10 36 0 #66000000"),
            Width = width,
            MinWidth = minWidth > 0 ? minWidth : 360,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new DockPanel { LastChildFill = true },
        };
        var cardRoot = (DockPanel)card.Child!;
        DockPanel.SetDock(titleBar, Dock.Top);
        cardRoot.Children.Add(titleBar);
        cardRoot.Children.Add(content);

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0x10, 0x18, 0x28)),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Grid
                {
                    Children = { card },
                },
            },
        };
        overlay.Focusable = true;

        var closed = false;
        void Close()
        {
            if (closed)
                return;
            closed = true;
            layer.SizeChanged -= OnLayerSizeChanged;
            layer.Children.Remove(overlay);
            finish();
        }

        SetCloseRequest(overlay, Close);
        closeButton.Click += (_, _) => Close();
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        layer.Children.Add(overlay);
        overlay.Focus();

        // 高度约束：卡片不超过视口，超出部分随 ScrollViewer 滚动。
        void Constrain()
        {
            var viewport = layer.Bounds.Height;
            if (viewport > 40)
                card.MaxHeight = viewport - 40;
        }
        void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e) => Constrain();
        layer.SizeChanged += OnLayerSizeChanged;
        Constrain();
    }
#endif
}
