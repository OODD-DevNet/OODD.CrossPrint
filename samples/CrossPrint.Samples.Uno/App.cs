using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI;

namespace CrossPrint.Samples.Uno;

/// <summary>
/// 示例应用对象：加载 Uno 的 Fluent 控件资源，并在 <see cref="OnLaunched"/> 里打开主窗口。
/// </summary>
/// <remarks>
/// 本示例刻意不写 App.xaml / MainWindow.xaml：XAML 编译需要 Uno 工具链，纯代码可让
/// 适配层与示例的编译面尽量小，也便于在没有 XAML 编译器的环境下审阅。
/// </remarks>
internal sealed class App : Application
{
    Window? _window;

    /// <summary>创建应用并合入控件主题资源（纯代码方式等价于 App.xaml 的 MergedDictionaries）。</summary>
    /// <remarks>
    /// 示例配色按浅色主题硬编码，必须在此处（窗口创建前）显式锁应用级 Light：
    /// Uno Skia 桌面头下窗口根 Grid 的 <c>RequestedTheme = ElementTheme.Light</c>
    /// 不会把 Fluent 控件资源级联成浅色（按钮白字/输入框深底照旧，2026-11-06 复测实锤），
    /// 只有应用级 <see cref="Application.RequestedTheme"/> 才真正切换主题字典。
    /// </remarks>
    public App()
    {
        RequestedTheme = ApplicationTheme.Light;
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        // Uno 默认的 SymbolThemeFontFamily 指向 ms-appx:///Assets/Fonts/uno-fluentui-assets.ttf#Symbols，
        // 但示例没有 XAML，Uno 源生成器不会把 UnoFonts 包里的实际路径写进 FeatureConfiguration，
        // 于是下拉箭头、复选勾、工具栏图标全部退化成豆腐块。
        // 该属性会把字体写进 Fluent 的主题字典（Resources.MergedDictionaries 里那份），
        // 所以必须在合并 XamlControlsResources 之后设置；字体本身随 Uno.Fonts.Fluent 复制到输出目录，
        // 各平台路径一致，CJK 正文仍由默认文本字体渲染。
        FeatureConfiguration.Font.SymbolsFont = "ms-appx:///Uno.Fonts.Fluent/Fonts/uno-fluentui-assets.ttf#Symbols";
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
