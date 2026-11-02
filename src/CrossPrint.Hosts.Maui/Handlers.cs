using Microsoft.Maui.Handlers;
using SkiaSharp.Views.Windows;

namespace CrossPrint.Hosts.Maui;

/// <summary>
/// <see cref="DesignerControl"/> 的平台 Handler：PlatformView 就是门面构造时创建的
/// <see cref="DesignerCanvas"/>（WinUI UserControl），指针/键盘事件在控件内部自挂，Handler 只做绑定。
/// </summary>
public class DesignerControlHandler : ViewHandler<DesignerControl, DesignerCanvas>
{
    // PlatformView 由门面自持，无 MAUI 属性需要映射；传空 mapper 满足 ViewHandler 的构造约束。
    /// <summary>空属性映射表；平台控件的事件绑定都在门面/控件内部完成。</summary>
    public static readonly IPropertyMapper<DesignerControl, DesignerControlHandler> Mapper =
        new PropertyMapper<DesignerControl, DesignerControlHandler>();

    /// <summary>创建 Handler。</summary>
    public DesignerControlHandler() : base(Mapper)
    {
    }

    /// <summary>返回门面构造时创建的 WinUI 平台画布。</summary>
    protected override DesignerCanvas CreatePlatformView() => VirtualView.Platform;
}

/// <summary>
/// <see cref="PreviewControl"/> 的平台 Handler：PlatformView 是门面持有的 <see cref="SKXamlCanvas"/>，
/// PaintSurface 已在门面构造时挂好，Handler 只做绑定。
/// </summary>
public class PreviewControlHandler : ViewHandler<PreviewControl, SKXamlCanvas>
{
    /// <summary>空属性映射表；PaintSurface 已在门面构造时挂好。</summary>
    public static readonly IPropertyMapper<PreviewControl, PreviewControlHandler> Mapper =
        new PropertyMapper<PreviewControl, PreviewControlHandler>();

    /// <summary>创建 Handler。</summary>
    public PreviewControlHandler() : base(Mapper)
    {
    }

    /// <summary>返回门面持有的 SKXamlCanvas。</summary>
    protected override SKXamlCanvas CreatePlatformView() => VirtualView.Platform;
}

/// <summary>MAUI Handler 注册扩展（MauiProgram 里 <c>ConfigureMauiHandlers(h => h.AddCrossPrintHandlers())</c>）。</summary>
public static class CrossPrintHandlers
{
    /// <summary>把 CrossPrint 门面控件的自定义 Handler 注册进 MAUI Handler 集合。</summary>
    public static IMauiHandlersCollection AddCrossPrintHandlers(this IMauiHandlersCollection handlers) =>
        handlers
            .AddHandler<DesignerControl, DesignerControlHandler>()
            .AddHandler<PreviewControl, PreviewControlHandler>();
}
