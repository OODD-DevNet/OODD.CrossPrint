using System;
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
#if !BROWSER
        // 桌面端：Semi Design 主题（圆角按钮、阴影输入框、Ant Design 蓝主色调）
        // Semi.Avalonia 是主题不是控件库——原生 Button/TextBox/ComboBox/DataGrid 自动套用 Semi 风格
        Styles.Add(new SemiTheme());
#else
        // 浏览器 WASM：Semi 未经过 WASM 验证，保持 FluentTheme
        Styles.Add(new FluentTheme());
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
}
