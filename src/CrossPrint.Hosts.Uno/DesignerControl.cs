using System;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;
using EditorPointerPoint = CrossPrint.Designer.PointerPoint;

namespace CrossPrint.Hosts.Uno;

/// <summary>
/// Uno Platform 设计画布：把 <see cref="EditorKernel"/> 的交互与 <see cref="EditorCanvas"/> 的绘制
/// 接到 Uno.WinUI.Graphics2DSK 的 <see cref="SKCanvasElement"/> 上（Skia 桌面头的软件合成路线）。
/// 指针、滚轮、键盘事件全部翻译成内核语义，宿主 UI 不再碰像素。
/// </summary>
/// <remarks>
/// <para>
/// 不用 SkiaSharp.Views.Windows 的 <c>SKSwapChainPanel</c>：它在 Skia 桌面头
/// （WpfHost/X11 等）构造即抛 <c>NotSupportedException</c>（仅 WinUI/WinAppSDK 的交换链可用）。
/// <see cref="SKCanvasElement"/> 走 Uno 合成器的 Skia 通道，各桌面头通用。
/// </para>
/// <para>
/// 坐标纪律：合成器绘制时画布已被预设 <c>RasterizationScale</c> 缩放，即 1 画布单位 = 1 DIP；
/// <see cref="EditorCanvas"/> 与内核按设备 px 工作，所以 <see cref="RenderOverride"/> 里先
/// <c>Scale(96/Dpi)</c> 把画布换回设备 px，再交给引擎。<c>设备px = dip × Dpi / 96</c>，
/// 内核内部再换到 mm，适配层不做任何 mm 数学。
/// </para>
/// <para>
/// DPI 统一取 <see cref="XamlRoot.RasterizationScale"/>（未挂到窗口时为 1.0）乘以 96。
/// 横纵缩放共用同一个值，与 WPF/Avalonia 宿主的口径一致，同一模板在各宿主的毫米落点才重合。
/// </para>
/// <para>
/// 键盘：<see cref="SKCanvasElement"/> 基类是 <see cref="FrameworkElement"/>，没有可覆写的
/// <c>OnKeyDown</c>（Uno 里那是内部方法），因此用事件订阅；控件取得焦点靠
/// <see cref="UIElement.IsTabStop"/> + 指针按下时的 <see cref="UIElement.Focus(FocusState)"/>。
/// 修饰键由 Shift/Ctrl/Alt 的 KeyDown/KeyUp 自行跟踪（WinUI 的 KeyRoutedEventArgs 不带修饰键）。
/// </para>
/// </remarks>
public sealed class DesignerControl : SKCanvasElement
{
    readonly EditorRenderer _overlay;
    EditorKernel _kernel;
    PointerButton _pressedButton;
    bool _autoFitted;
    bool _shiftPressed;
    bool _controlPressed;
    bool _altPressed;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    /// <summary>创建设计画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        // 画布尺寸第一次就位时由内核自动整页适配一次（页面默认居中）。
        _kernel.AutoFitOnFirstLayout = true;
        _overlay = new EditorRenderer();
        Attach(_kernel);

        IsTabStop = true;

        Loaded += (_, _) => _invalidateReady = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    /// <summary>交互内核；外部命令（工具箱、属性面板、层次）都直接对它调用。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层（网格/标尺/选中框）。<see cref="EditorRenderer.Options"/> 可运行时改，改完调用 <see cref="M:Uno.WinUI.Graphics2DSK.SKCanvasElement.Invalidate"/>。</summary>
    public EditorRenderer Overlay => _overlay;

    /// <summary>文档渲染器；默认共享实例，换实例后下一次重绘生效。</summary>
    public TemplateRenderer Renderer
    {
        get => _renderer;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_renderer, value))
                return;

            _renderer = value;
            InvalidateSafe();
        }
    }

    TemplateRenderer _renderer = TemplateRenderer.Default;

    /// <summary>数据绑定源；设计态通常为 <c>null</c>（占位符原样显示为空），示例可传样例数据。</summary>
    public object? Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(_data, value))
                return;

            _data = value;
            InvalidateSafe();
        }
    }

    object? _data;

    /// <summary>字体解析器；<c>null</c> 使用引擎的系统字体默认实现。</summary>
    public IFontResolver? FontResolver
    {
        get => _fontResolver;
        set
        {
            if (ReferenceEquals(_fontResolver, value))
                return;

            _fontResolver = value;
            InvalidateSafe();
        }
    }

    IFontResolver? _fontResolver;

    /// <summary>图片解析器；<c>null</c> 表示只有模板内嵌图片可用。</summary>
    public IImageResolver? Images
    {
        get => _images;
        set
        {
            if (ReferenceEquals(_images, value))
                return;

            _images = value;
            InvalidateSafe();
        }
    }

    IImageResolver? _images;

    /// <summary>当前模板（内核直接编辑它，不拷贝）。</summary>
    public PrintTemplate Template => Kernel.Template;

    /// <summary>宿主逻辑 DPI：96 × <see cref="XamlRoot.RasterizationScale"/>。横纵缩放取同一个值，见类备注。</summary>
    public double Dpi => (XamlRoot?.RasterizationScale ?? 1.0) * 96.0;

    /// <summary>
    /// 安全失效：控件未挂入可视树时 <see cref="M:Uno.WinUI.Graphics2DSK.SKCanvasElement.Invalidate"/>
    /// 会因内部 visual 未创建而抛 NRE。挂树前的变更无需手动失效——首次合成必然整幅绘制。
    /// </summary>
    void InvalidateSafe()
    {
        if (_invalidateReady)
            Invalidate();
    }

    bool _invalidateReady;

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次 <see cref="RenderOverride"/> 绘制完成后触发（含告警信息）。</summary>
    public event EventHandler? Rendered;

    /// <summary>替换当前模板：旧内核解绑、新内核重新绑定事件，并按当前视口尺寸整页适配一次。</summary>
    /// <param name="template">新模板；内核直接编辑这个实例。</param>
    public void LoadTemplate(PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (ReferenceEquals(template, Kernel.Template))
            return;

        _kernel.ViewChanged -= OnKernelChanged;
        _kernel.TemplateChanged -= OnKernelChanged;
        _kernel = new EditorKernel(template);
        // 换内核同样开首帧自动适配，与构造时行为一致。
        _kernel.AutoFitOnFirstLayout = true;
        Attach(_kernel);

        FitToPage();
        InvalidateSafe();
    }

    /// <summary>整页适配窗口。优先用最近一次绘制表面的设备尺寸，未绘制过时按控件逻辑尺寸换算。</summary>
    public void FitToPage()
    {
        var width = _lastSurfaceWidth;
        var height = _lastSurfaceHeight;
        if (width <= 0 || height <= 0)
        {
            var scale = Dpi / 96.0;
            width = ActualWidth * scale;
            height = ActualHeight * scale;
        }

        if (width <= 0 || height <= 0)
            return;

        FitViewport(width, height);
        InvalidateSafe();
    }

    /// <summary>引擎绘制入口：合成器回调，画布处于 DIP 坐标系，见类备注的坐标纪律。</summary>
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        // 合成器画布处于 DIP 坐标系（WpfHost/X11 渲染前已按 RasterizationScale 预缩放），
        // 而引擎按设备 px 绘制：先撤销预缩放，把画布换回设备 px。
        var dpiScale = Dpi / 96.0;
        var width = (int)Math.Round(area.Width * dpiScale);
        var height = (int)Math.Round(area.Height * dpiScale);
        if (width <= 0 || height <= 0)
            return;

        _lastSurfaceWidth = width;
        _lastSurfaceHeight = height;

        // 只在第一次拿到有效尺寸时适配；窗口之后怎么拉伸都不再自动改缩放。
        if (!_autoFitted)
        {
            _autoFitted = true;
            FitViewport(width, height);
        }

        var save = canvas.Save();
        canvas.Scale((float)(1.0 / dpiScale));
        RenderReport report;
        try
        {
            report = EditorCanvas.Draw(
                canvas,
                Kernel,
                Overlay,
                width,
                height,
                Dpi,
                Data,
                FontResolver,
                Images,
                Renderer);
        }
        finally
        {
            // 绘制抛异常也要还原：否则一次失败会把缩放变换永久留在合成画布上，之后每帧都错位。
            canvas.RestoreToCount(save);
        }

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var button = MapButton(e.GetCurrentPoint(this).Properties);
        if (button == PointerButton.None)
            return;

        // 右键不绑定内核动作，交给宿主做上下文菜单（示例用 MenuFlyout）：这里若 CapturePointer，
        // 菜单弹出时捕获会被释放，PointerCaptureLost 会按 Esc 回滚并清掉刚命中的选中，
        // 菜单里的"副本/层次"随即失去作用对象。与 WinForms 宿主的处理一致，右键只取焦点。
        if (button == PointerButton.Secondary)
        {
            Focus(FocusState.Programmatic);
            return;
        }

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        _pressedButton = button;
        SyncModifiers(e.KeyModifiers);
        Focus(FocusState.Programmatic);
        CapturePointer(e.Pointer);
        Kernel.OnPointerPressed(ToPointer(e, button));
        e.Handled = true;
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 拖拽期间 Shift 可能中途按下/松开，必须每次同步；空闲时内核会自行忽略。
        SyncModifiers(e.KeyModifiers);
        Kernel.OnPointerMoved(ToPointer(e, PointerButton.None));
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var button = _pressedButton != PointerButton.None
            ? _pressedButton
            : MapButton(e.GetCurrentPoint(this).Properties);
        if (button == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        ReleasePointerCapture(e.Pointer);
        Kernel.OnPointerReleased(ToPointer(e, button));
        e.Handled = true;
    }

    void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // 捕获意外丢失（窗口失活等）时内核可能还停在拖拽态：按 Esc 语义回滚，避免后续无键移动继续改模型。
        if (_pressedButton == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));
    }

    void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Uno 的 MouseWheelDelta 同样是原生刻度，内核只看符号；修饰键取事件自带 KeyModifiers
        // （与 SyncModifiers 同一来源）：Ctrl=以光标为锚缩放，Shift=横向滚动，普通滚轮=纵向滚动。
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (Kernel.OnMouseWheel(delta, ToPointer(e, PointerButton.None), control, shift))
            e.Handled = true;
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        TrackModifier(e.Key, isDown: true);

        var action = MapKey(e.Key);
        if (action == KeyAction.None)
            return;

        var handled = Kernel.OnKeyDown(new KeyInfo(action, _shiftPressed, _controlPressed, _altPressed));
        if (handled)
            e.Handled = true;
    }

    void OnKeyUp(object sender, KeyRoutedEventArgs e) => TrackModifier(e.Key, isDown: false);

    void FitViewport(double widthHostPx, double heightHostPx)
    {
        Kernel.ViewWidthHostPx = widthHostPx;
        Kernel.ViewHeightHostPx = heightHostPx;
        Kernel.Viewport.FitToPage(
            Kernel.Template.Paper.PageWidthMm,
            Kernel.PageHeightMm,
            widthHostPx,
            heightHostPx,
            Dpi);
    }

    void Attach(EditorKernel kernel)
    {
        kernel.ViewChanged += OnKernelChanged;
        kernel.TemplateChanged += OnKernelChanged;
    }

    void OnKernelChanged() => InvalidateSafe();

    /// <summary>修饰键状态：WinUI 的键盘事件不带修饰键，自己跟踪，指针事件再用 KeyModifiers 校准。</summary>
    void TrackModifier(VirtualKey key, bool isDown)
    {
        switch (key)
        {
            case VirtualKey.Shift:
                _shiftPressed = isDown;
                break;
            case VirtualKey.Control:
                _controlPressed = isDown;
                break;
            case VirtualKey.Menu:
                _altPressed = isDown;
                break;
            default:
                return;
        }

        Kernel.ShiftPressed = _shiftPressed;
    }

    void SyncModifiers(VirtualKeyModifiers modifiers)
    {
        _shiftPressed = (modifiers & VirtualKeyModifiers.Shift) != 0;
        _controlPressed = (modifiers & VirtualKeyModifiers.Control) != 0;
        _altPressed = (modifiers & VirtualKeyModifiers.Menu) != 0;
        Kernel.ShiftPressed = _shiftPressed;
    }

    EditorPointerPoint ToPointer(PointerRoutedEventArgs e, PointerButton button)
    {
        var dip = e.GetCurrentPoint(this).Position;
        var dpi = Dpi;
        return new EditorPointerPoint(dip.X * dpi / 96.0, dip.Y * dpi / 96.0, dpi, button);
    }

    static PointerButton MapButton(PointerPointProperties properties)
    {
        if (properties.IsLeftButtonPressed)
            return PointerButton.Primary;
        if (properties.IsMiddleButtonPressed)
            return PointerButton.Middle;
        if (properties.IsRightButtonPressed)
            return PointerButton.Secondary;

        return PointerButton.None;
    }

    static KeyAction MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Delete or VirtualKey.Back => KeyAction.Delete,
        VirtualKey.Escape => KeyAction.Escape,
        VirtualKey.Left => KeyAction.NudgeLeft,
        VirtualKey.Right => KeyAction.NudgeRight,
        VirtualKey.Up => KeyAction.NudgeUp,
        VirtualKey.Down => KeyAction.NudgeDown,
        VirtualKey.Add or (VirtualKey)0xBB => KeyAction.ZoomIn,       // Add / OEM '+'
        VirtualKey.Subtract or (VirtualKey)0xBD => KeyAction.ZoomOut, // Subtract / OEM '-'
        VirtualKey.Number0 or VirtualKey.NumberPad0 => KeyAction.ZoomActual,
        VirtualKey.F => KeyAction.ZoomFit,
        _ => KeyAction.None,
    };
}
