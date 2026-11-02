using System;
using System.Windows;

namespace CrossPrint.Samples.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 样例刻意不建 App.xaml：整个 UI 用代码搭出来，读起来就是一份可复制的接线说明。
        var app = new Application();
        app.Run(new MainWindow());
    }
}
