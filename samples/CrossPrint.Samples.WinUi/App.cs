using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace CrossPrint.Samples.WinUi;

/// <summary>WinUI 应用对象：启动时创建并激活主窗口。</summary>
public sealed class App : Application, IXamlMetadataProvider
{
    Window? _window;
    XamlControlsXamlMetaDataProvider? _metadataProvider;

    /// <summary>创建应用实例（无 XAML，故不调用 InitializeComponent）。</summary>
    public App()
    {
        // 纯代码应用（无 App.xaml）在此构造函数里不能访问 Application.Resources 等运行时属性：
        // 底层 COM 对象要等 Application.Start 回调返回后才完成初始化，提前访问会抛
        // E_UNEXPECTED(0x8000FFFF)，并以 0xC000027B stowed exception 静默崩溃。
        // XamlControlsResources 的合并因此放在 OnLaunched 里做。
    }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 常规 WinUI 模板把这一条写在 App.xaml 的 MergedDictionaries 里；本示例完全用 C# 组合，
        // 必须在代码里合并，否则 Button/TextBox 等控件拿不到默认样式。
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        _window = new MainWindow();
        _window.Activate();
    }

    // 无 App.xaml 的纯代码应用没有编译器生成的元数据提供者；XamlControlsResources 激活
    // （解析 themeresources.xaml 等主题资源）要求应用对象实现该接口并委托到控件库提供者，
    // 否则运行时报“Cannot find a resource with the given key: AcrylicBackgroundFillColorDefaultBrush”。
    IXamlType IXamlMetadataProvider.GetXamlType(Type type)
        => (_metadataProvider ??= new XamlControlsXamlMetaDataProvider()).GetXamlType(type);

    IXamlType IXamlMetadataProvider.GetXamlType(string fullName)
        => (_metadataProvider ??= new XamlControlsXamlMetaDataProvider()).GetXamlType(fullName);

    XmlnsDefinition[] IXamlMetadataProvider.GetXmlnsDefinitions()
        => (_metadataProvider ??= new XamlControlsXamlMetaDataProvider()).GetXmlnsDefinitions();
}
