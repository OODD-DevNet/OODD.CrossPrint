using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CrossPrint.Samples.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 样例刻意不建 App.xaml：整个 UI 用代码搭出来，读起来就是一份可复制的接线说明。
        var app = new Application();

        // HandyControl 主题（Material Design 风格圆角按钮、阴影输入框）：
        // 原生 Button/TextBox/ComboBox/DataGrid 自动套用，不需要换控件 Tag。
        // SkinDefault.xaml 是基础皮肤颜色 + 尺寸，Theme.xaml 是各控件的 ControlTemplate。
        // 顺序：先加皮肤再加主题，否则主题里引用的 StaticResource 找不到。
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml")
        });

        app.Run(new MainWindow());
    }
}
