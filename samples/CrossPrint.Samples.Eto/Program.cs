using CrossPrint.I18n;
using Eto.Forms;

namespace CrossPrint.Samples.Eto;

/// <summary>示例入口：显式指定 Windows（WinForms）平台后运行主窗口。</summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 界面文字在控件构造时一次性取好，所以必须在建窗口之前定下 UI 语言。
        LocalizationSwitch.Apply();

        // Eto 2.10 的 Eto.Platforms 没有 Windows 字段（只有 WinForms/Wpf），Windows 走 WinForms 后端。
        // global:: 前缀是必需的：本文件命名空间以 .Eto 结尾，裸写 Eto 会被解析成 CrossPrint.Samples.Eto。
        new Application(global::Eto.Platforms.WinForms).Run(new MainForm());
    }
}
