using System;
using System.Runtime.InteropServices;
using System.Threading;
using CrossPrint.I18n;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 程序入口。示例完全用 C# 组合界面（无 App.xaml），所以入口点由这里显式提供：
/// 初始化 WinRT 包装器 → 建立 DispatcherQueueSynchronizationContext → 创建 <see cref="App"/>。
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    /// <summary>应用主入口（单线程单元，WinUI 要求）。</summary>
    [STAThread]
    private static void Main()
    {
        // 界面文字在控件构造时一次性取好，所以必须在建窗口之前定下 UI 语言。
        LocalizationSwitch.Apply();

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // 注意不能写 "_ = new App()"：回调参数就叫 _，会变成给回调参数赋值。
            // 应用实例由 WinUI 的 Application.Current 持有。
            new App();
        });
    }
}
