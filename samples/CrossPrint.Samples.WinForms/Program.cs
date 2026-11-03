using CrossPrint.I18n;

namespace CrossPrint.Samples.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 界面文字在控件构造时一次性取好，所以必须在建窗口之前定下 UI 语言。
        LocalizationSwitch.Apply();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
