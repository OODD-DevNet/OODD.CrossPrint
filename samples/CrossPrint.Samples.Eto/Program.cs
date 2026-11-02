using Eto.Forms;

namespace CrossPrint.Samples.Eto;

/// <summary>示例入口：显式指定 Windows（WinForms）平台后运行主窗口。</summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Eto 2.10 的 Eto.Platforms 没有 Windows 字段（只有 WinForms/Wpf），Windows 走 WinForms 后端。
        // global:: 前缀是必需的：本文件命名空间以 .Eto 结尾，裸写 Eto 会被解析成 CrossPrint.Samples.Eto。
        new Application(global::Eto.Platforms.WinForms).Run(new MainForm());
    }
}
