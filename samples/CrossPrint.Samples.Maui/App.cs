namespace CrossPrint.Samples.Maui;

/// <summary>MAUI Application：启动即打开主窗口。</summary>
/// <remarks>
/// 示例的全部自定义配色都按浅色主题硬编码（白底 + 深灰字），但 MAUI 默认跟随系统主题：
/// 系统为深色时 MAUI 控件默认样式（按钮白字/输入框深底）落在这些浅色面板上，文字几乎不可读
///（2026-11-06 真机筛查实锤）。与 WinUI/Uno 宿主锁 Light 的做法对齐，这里显式固定浅色。
/// </remarks>
public class App : Application
{
    public App()
    {
        UserAppTheme = Microsoft.Maui.ApplicationModel.AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new MainWindow();
}
