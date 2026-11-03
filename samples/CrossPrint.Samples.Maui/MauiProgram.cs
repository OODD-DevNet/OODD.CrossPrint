using CrossPrint.Hosts.Maui;
using CrossPrint.I18n;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace CrossPrint.Samples.Maui;

/// <summary>MAUI 应用入口：注册 App 与 CrossPrint 宿主 Handler。</summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // 界面文字在控件构造时一次性取好，所以必须在建窗口之前定下 UI 语言。
        LocalizationSwitch.Apply();

        return MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddCrossPrintHandlers())
            .Build();
    }
}
