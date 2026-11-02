using CrossPrint.Hosts.Maui;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace CrossPrint.Samples.Maui;

/// <summary>MAUI 应用入口：注册 App 与 CrossPrint 宿主 Handler。</summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp() =>
        MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddCrossPrintHandlers())
            .Build();
}
