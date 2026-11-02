using System;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;

namespace CrossPrint.Hosts.Maui;

/// <summary>
/// MAUI 设计画布（跨平台门面）：把交互内核与绘制接到 <see cref="DesignerCanvas"/>（WinUI 平台控件，
/// 注册见 <see cref="CrossPrintHandlers"/>）。鼠标、滚轮、键盘事件全部翻译成内核语义，宿主 UI 不碰像素。
/// </summary>
/// <remarks>
/// 门面持有平台控件实例（构造时即创建），Handler 的 <c>CreatePlatformView</c> 直接返回它——
/// 属性在挂进视觉树之前就可用，样例代码与其它宿主完全同构。
/// </remarks>
public class DesignerControl : View
{
    /// <summary>Windows 平台画布（WinUI UserControl 包 SKXamlCanvas，本身可持键盘焦点）。</summary>
    public DesignerCanvas Platform { get; }

    /// <summary>创建设计画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        Platform = new DesignerCanvas(template);
    }

    /// <summary>交互内核；外部命令（工具箱、属性面板、层次）都直接对它调用。</summary>
    public EditorKernel Kernel => Platform.Kernel;

    /// <summary>叠加层（网格/标尺/选中框）。<see cref="EditorRenderer.Options"/> 可运行时改，改完调用 <see cref="Invalidate"/>。</summary>
    public EditorRenderer Overlay => Platform.Overlay;

    /// <summary>文档渲染器；默认共享实例，换实例后下一次重绘生效。</summary>
    public TemplateRenderer Renderer
    {
        get => Platform.Renderer;
        set => Platform.Renderer = value;
    }

    /// <summary>数据绑定源；设计态通常为 <c>null</c>（占位符原样显示为空），示例可传样例数据。</summary>
    public object? Data
    {
        get => Platform.Data;
        set => Platform.Data = value;
    }

    /// <summary>字体解析器；<c>null</c> 使用引擎的系统字体默认实现。</summary>
    public IFontResolver? FontResolver
    {
        get => Platform.FontResolver;
        set => Platform.FontResolver = value;
    }

    /// <summary>图片解析器；<c>null</c> 表示只有模板内嵌图片可用。</summary>
    public IImageResolver? Images
    {
        get => Platform.Images;
        set => Platform.Images = value;
    }

    /// <summary>当前模板（内核直接编辑它，不拷贝）。</summary>
    public PrintTemplate Template => Platform.Template;

    /// <summary>宿主逻辑 DPI：96 × RasterizationScale；未加载时按 100% 兜底。</summary>
    public double Dpi => Platform.Dpi;

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport => Platform.LastReport;

    /// <summary>每次平台画布绘制完成后触发（含告警信息）。</summary>
    public event EventHandler? Rendered
    {
        add => Platform.Rendered += value;
        remove => Platform.Rendered -= value;
    }

    /// <summary>右键按下（焦点已收回、指针未捕获、内核未介入）。参数带画布内与窗口根两套 DIP 坐标。</summary>
    public event EventHandler<CanvasRightPressedEventArgs>? CanvasRightPressed
    {
        add => Platform.CanvasRightPressed += value;
        remove => Platform.CanvasRightPressed -= value;
    }

    /// <summary>内核未映射的按键（含修饰键状态）；样例用于 Ctrl+D 等自定义快捷键。</summary>
    public event EventHandler<RawKeyEventArgs>? RawKeyDown
    {
        add => Platform.RawKeyDown += value;
        remove => Platform.RawKeyDown -= value;
    }

    /// <summary>替换当前模板：旧内核解绑、新内核重新绑定事件，并按当前视口尺寸整页适配一次。</summary>
    public void LoadTemplate(PrintTemplate template) => Platform.LoadTemplate(template);

    /// <summary>整页适配窗口。优先用最近一次绘制表面的设备尺寸，未绘制过时按控件 DIP 尺寸换算。</summary>
    public void FitToPage() => Platform.FitToPage();

    /// <summary>请求重绘画布（改完 <see cref="EditorRenderer.Options"/> 等外部状态后调用）。</summary>
    public void Invalidate() => Platform.Invalidate();

    /// <summary>把键盘焦点收回到画布（属性面板/按钮操作完成后由样例调用）。</summary>
    public void TakeFocus() => Platform.TakeFocus();
}
