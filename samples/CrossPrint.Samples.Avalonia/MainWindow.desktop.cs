using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CrossPrint.I18n;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 桌面宿主的窗口包装：浏览器单视图生命周期不支持 WindowingPlatform（连 new Window 都会抛），
/// 所以全部 UI 在 <see cref="MainView"/>（UserControl）里，本类只负责窗口标题/尺寸。
/// Browser 工程共享源码时本文件也会编译——浏览器分支永远不实例化它，无副作用。
/// </summary>
public sealed class MainWindow : Window
{
    /// <summary>创建桌面主窗口并承载 MainView。</summary>
    public MainWindow()
    {
        Title = Localizer.T("App.Title.Short") + " — Avalonia";
        // 布局基准与 WPF 版一致：窗口 1320×880，最小 1024×640。
        Width = 1320;
        Height = 880;
        MinWidth = 1024;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new MainView();

        // 设置窗口图标（从 I18n 嵌入资源加载，失败静默跳过）
        try
        {
            using var stream = LogoLoader.GetLogoStream();
            if (stream is not null)
                Icon = new WindowIcon(stream);
        }
        catch { /* icon 加载失败不影响使用 */ }
    }
}
