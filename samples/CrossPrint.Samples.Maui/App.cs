namespace CrossPrint.Samples.Maui;

/// <summary>MAUI Application：启动即打开主窗口。</summary>
public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new MainWindow();
}
