using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    public App()
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
