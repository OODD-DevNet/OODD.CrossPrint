using System;
using Uno.UI.Runtime.Skia;

namespace CrossPrint.Samples.Uno;

/// <summary>Uno Platform Skia 桌面头的入口点。</summary>
/// <remarks>
/// 必须经 <see cref="SkiaHostBuilder"/> 创建平台宿主（Windows 上为 WPF 头）再运行：
/// 宿主负责初始化原生调度器并驱动 WPF 消息循环。若直接调用
/// <see cref="Microsoft.UI.Xaml.Application.Start"/>，原生调度器的线程访问委托
/// 不会被赋值，App 构造函数加载资源字典时会在
/// <c>DispatcherQueue.GetForCurrentThread</c> 处触发原生访问冲突（0xC0000005）。
/// </remarks>
internal static class Program
{
    /// <summary>创建 Skia 宿主并启动 XAML 应用。</summary>
    /// <param name="args">命令行参数（目前未使用）。</param>
    [STAThread]
    static void Main(string[] args)
    {
        SkiaHostBuilder.Create()
            .UseWindows()
            .App(() => new App())
            .Build()
            .Run();
    }
}
