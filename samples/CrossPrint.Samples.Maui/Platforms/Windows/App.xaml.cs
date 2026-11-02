using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace CrossPrint.Samples.Maui.WinUI;

/// <summary>
/// WinUI Application 宿主（官方模板形态）。XAML 编译器以本文件 + App.xaml 为
/// ApplicationDefinition，自动生成三样东西：
/// ① Main 入口点（App.xaml.g.cs：XamlCheckProcessRequirements → InitializeComWrappers →
///    Application.Start），无需手写 Program.cs；
/// ② XamlTypeInfo.g.cs（应用实现 IXamlMetadataProvider，含 App.xaml 类型图触及的
///    Microsoft.Maui.* 类型），供运行时解析主题资源里的 MAUI 自定义类型；
/// ③ App.xbf，并随 WindowsAppSDK 1.6 自带的 PRI 工具链（Microsoft.Build.Msix.dll +
///    makepri）把库 pri（Microsoft.Maui.pri 等）合并进应用 resources.pri。
/// 三者缺一都会以 0xC000027B stowed exception 静默崩溃——纯 C# 组合（无 App.xaml）时
/// MAUI 的 Resources.xbf（引用 Microsoft.Maui.Platform.ColorConverter 等）无法解析。
/// </summary>
public partial class App : MauiWinUIApplication
{
    /// <summary>由 MAUI 基类在 OnLaunched 时调用，构建 DI 容器并取出 Controls.Application。</summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
