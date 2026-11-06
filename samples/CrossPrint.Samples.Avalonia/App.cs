using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
#if !BROWSER
using Semi.Avalonia;
#else
using Avalonia.Themes.Fluent;
#endif

namespace CrossPrint.Samples.Avalonia;

public sealed class App : Application
{
    public App()
    {
#if BROWSER
        // 浏览器没有系统字体：必须在这里（构造函数）注册内嵌 Noto Sans SC，不能等到
        // OnFrameworkInitializationCompleted——Avalonia.Browser 在 AfterApplicationSetup
        // 阶段（早于该回调）就 new AvaloniaView → TopLevel/Compositor 初始化会解析默认
        // 字体族，集合尚未注册会直接抛 "Could not create glyphTypeface. Font family: $Default"。
        // 此刻 Skia 平台服务已初始化（FontManagerOptions 已在 Program.cs 指向我们的注册键）。
        EmbeddedFonts.RegisterAvaloniaFont();
#endif
    }

    public override void Initialize()
    {
        // 样例外壳固定浅色调色板：主窗口/弹窗/菜单浮层统一亮色。
        // 之前只把 MainWindow 锁了 Light，弹出的新 Window 跟随系统（暗色系统下全变黑底）。
        RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Light;

#if !BROWSER
        // 桌面端：Semi Design 主题（圆角按钮、阴影输入框、Ant Design 蓝主色调）
        // Semi.Avalonia 是主题不是控件库——原生 Button/TextBox/ComboBox/DataGrid 自动套用 Semi 风格
        Styles.Add(new SemiTheme());
        // 取色面板（ColorView/ColorPicker 控件族）的 Semi 皮肤：属性面板颜色行的弹层取色器要靠它才有样式
        Styles.Add(new Semi.Avalonia.ColorPicker.ColorPickerSemiTheme());
#else
        // 浏览器 WASM：Semi 未经过 WASM 验证，保持 FluentTheme
        Styles.Add(new FluentTheme());
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if !BROWSER
        // 全局异常兜底：任何未处理异常（UI 线程最终冒泡到 AppDomain、后台线程、Task）先落盘再原样抛出，
        // 把"应用直接消失"变成"留下现场"——日志在 %TEMP%\crossprint-crash.log，追加写。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception, "UnobservedTaskException");
            e.SetObserved(); // 已记录即视为观察到，避免 GC 终结器再次引爆
        };
#endif

        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();

            // 浏览器（Avalonia.Browser）走单视图生命周期：MainView（UserControl）直接挂进页面，
            // 不能 new Window——Avalonia.Browser 没有 windowing platform。
            if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
                singleView.MainView = new MainView();

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
#if BROWSER
            CrossPrint.Samples.Avalonia.Browser.WebDiagnostics.Report(ex);
#endif
            // Trace 无条件执行，让非 BROWSER 构建也用到 ex（避免 CS0168 未使用变量警告）。
            System.Diagnostics.Trace.TraceError(ex.ToString());
            throw;
        }
    }

#if !BROWSER
    /// <summary>崩溃现场落盘：时间 + 分发来源 + 完整异常（含堆栈）。追加写，失败静默（兜底不能再炸）。</summary>
    static void WriteCrashLog(Exception ex, string source)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "crossprint-crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // 日志写不进去就不写：兜底路径绝不二次抛出。
        }
    }
#endif
}
