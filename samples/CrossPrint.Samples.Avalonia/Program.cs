using Avalonia;
using CrossPrint.I18n;

namespace CrossPrint.Samples.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 界面文字在控件构造时一次性取好，所以必须在建窗口之前定下 UI 语言。
        LocalizationSwitch.Apply();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
