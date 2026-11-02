using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Logging;
using Avalonia.Media;
using CrossPrint.Samples.Avalonia;

namespace CrossPrint.Samples.Avalonia.Browser;

/// <summary>浏览器入口：把共享的 <see cref="App"/> 以单视图生命周期挂到页面 &lt;div id="out"&gt; 上。</summary>
internal static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            await AppBuilder.Configure<App>()
                .UseBrowser()
                // Debug 级日志（含 FontManager 的"族未找到"诊断）走 Trace → stdout。
                .LogToTrace(LogEventLevel.Debug)
                // WASM 没有系统字体：默认族指到 App.OnFrameworkInitializationCompleted 里注册的内嵌 Noto Sans SC。
                .With(new FontManagerOptions { DefaultFamilyName = EmbeddedFonts.FontKey + "#" + EmbeddedFonts.FamilyName })
                .StartBrowserAppAsync("out");
        }
        catch (Exception ex)
        {
            WebDiagnostics.Report(ex);
            throw;
        }
    }
}
