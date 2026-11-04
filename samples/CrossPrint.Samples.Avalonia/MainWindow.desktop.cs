using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CrossPrint.I18n;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 桌面宿主的窗口包装：浏览器单视图生命周期不支持 WindowingPlatform（连 new Window 都会抛），
/// 所以全部 UI 在 <see cref="MainView"/>（UserControl）里，本类只负责窗口标题/尺寸。
/// Browser 工程共享源码时本文件也会编译——浏览器分支永远不实例化它，无副作用。
/// </summary>
public sealed class MainWindow : Window
{
    bool _forceClose;

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

        // 样例外壳用固定浅色调色板，不跟系统深色走；同时必须铺一层**不透明**窗口底色：
        // 否则没显式铺色的面板（右侧属性栏、底部状态栏等）会透出桌面，
        // 窗口放大/最大化后尤其明显（右侧一条"透明"）。
        RequestedThemeVariant = ThemeVariant.Light;
        Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));

        Content = new MainView();

        // 关闭前确认：取消留下 / 保存并退出 / 不保存退出。
        Closing += async (_, e) =>
        {
            if (_forceClose)
                return;
            if (Content is not MainView view)
                return;
            e.Cancel = true;
            var choice = await ModalDialog.ThreeWayAsync(
                view, "退出",
                "确认退出 CrossPrint？未保存的修改将丢失。",
                "保存并退出", "退出");
            if (choice is null)
                return; // 取消：留在编辑器。
            if (choice == "neutral" && !await view.SaveForExitAsync())
                return; // 用户在另存为弹窗里取消：不退出。
            _forceClose = true;
            Close();
        };

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
