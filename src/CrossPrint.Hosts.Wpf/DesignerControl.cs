using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CrossPrint.Hosts.Wpf;

/// <summary>
/// WPF 设计画布：把 <see cref="EditorKernel"/> 的交互与 <see cref="EditorCanvas"/> 的绘制接到
/// <see cref="SKElement"/> 上。鼠标、滚轮、键盘事件全部翻译成内核语义，宿主 UI 不再碰像素。
/// </summary>
/// <remarks>
/// <para>
/// 坐标纪律：WPF 事件坐标是 DIP，<see cref="PointerPoint"/> 要求宿主设备 px，
/// 所以按下式换算一次：<c>设备px = dip × Dpi / 96</c>。内核内部再换到 mm。
/// </para>
/// <para>
/// DPI 统一取 <see cref="VisualTreeHelper.GetDpi(Visual)"/> 的横轴缩放：WPF 的横纵 DpiScale
/// 在真实显示设备上恒等，换轴只会让本控件与其它宿主的口径分叉，故两轴共用同一个值。
/// </para>
/// </remarks>
public sealed class DesignerControl : SKElement
{
    readonly EditorRenderer _overlay;
    EditorKernel _kernel;
    PointerButton _pressedButton;
    bool _autoFitted;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    /// <summary>创建设计画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        _overlay = new EditorRenderer();
        Attach(_kernel);

        Focusable = true;
        PaintSurface += OnPaintSurface;
    }

    /// <summary>交互内核；外部命令（工具箱、属性面板、层次）都直接对它调用。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层（网格/标尺/选中框）。<see cref="EditorRenderer.Options"/> 可运行时改。</summary>
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
            InvalidateVisual();
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
            InvalidateVisual();
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
            InvalidateVisual();
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
            InvalidateVisual();
        }
    }

    IImageResolver? _images;

    /// <summary>当前模板（内核直接编辑它，不拷贝）。</summary>
    public PrintTemplate Template => Kernel.Template;

    /// <summary>宿主逻辑 DPI：96 × 系统缩放。横纵缩放取同一个值，见类备注。</summary>
    public double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX * 96.0;

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次 <see cref="SKElement.PaintSurface"/> 绘制完成后触发（含告警信息）。</summary>
    public event EventHandler? Rendered;

    /// <summary>替换当前模板：旧内核解绑、新内核重新绑定事件，并按当前视口尺寸整页适配一次。</summary>
    /// <param name="template">新模板；内核直接编辑这个实例。</param>
    public void LoadTemplate(PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (ReferenceEquals(template, Kernel.Template))
            return;

        _kernel.ViewChanged -= OnKernelViewChanged;
        _kernel.TemplateChanged -= OnKernelViewChanged;
        _kernel = new EditorKernel(template);
        Attach(_kernel);

        FitToPage();
        InvalidateVisual();
    }

    /// <summary>整页适配窗口。优先用最近一次绘制表面的设备尺寸，未绘制过时按控件 DIP 尺寸换算。</summary>
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
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var button = MapButton(e.ChangedButton);
        if (button == PointerButton.None)
            return;

        // 右键交给宿主 ContextMenu：这里若 CaptureMouse，菜单弹出/关闭时捕获会被夺走，
        // OnLostMouseCapture 会按 Esc 回滚并清空选中。右键的命中与选中由菜单打开前调 Kernel.SelectAt 完成。
        if (button == PointerButton.Secondary)
            return;

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        _pressedButton = button;
        SyncShift();
        CaptureMouse();
        Kernel.OnPointerPressed(ToPointer(e, button));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 拖拽期间 Shift 可能中途按下/松开，必须每次同步；空闲时内核会自行忽略。
        SyncShift();
        Kernel.OnPointerMoved(ToPointer(e, PointerButton.None));
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        var button = MapButton(e.ChangedButton);
        if (button == PointerButton.None || button != _pressedButton)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnPointerReleased(ToPointer(e, button));
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // 捕获意外丢失（窗口失活等）时内核可能还停在拖拽态：按 Esc 语义回滚，避免后续无键移动继续改模型。
        if (_pressedButton == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Ctrl+滚轮缩放、Shift+滚轮横滚、普通滚轮纵滚：修饰键由这里读，内核按它分派。
        var modifiers = Keyboard.Modifiers;
        if (Kernel.OnMouseWheel(
                e.Delta,
                ToPointer(e, PointerButton.None),
                modifiers.HasFlag(ModifierKeys.Control),
                modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            return;
        }

        // 内核未消费（该轴没滚动条或已滚到头）：把滚动让给外层 ScrollViewer 等祖先控件。
        base.OnMouseWheel(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var action = e.Key switch
        {
            Key.Delete or Key.Back => KeyAction.Delete,
            Key.Escape => KeyAction.Escape,
            Key.Left => KeyAction.NudgeLeft,
            Key.Right => KeyAction.NudgeRight,
            Key.Up => KeyAction.NudgeUp,
            Key.Down => KeyAction.NudgeDown,
            Key.OemPlus or Key.Add => KeyAction.ZoomIn,
            Key.OemMinus or Key.Subtract => KeyAction.ZoomOut,
            Key.D0 or Key.NumPad0 => KeyAction.ZoomActual,
            Key.F => KeyAction.ZoomFit,
            _ => KeyAction.None,
        };

        if (action == KeyAction.None)
        {
            base.OnKeyDown(e);
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var handled = Kernel.OnKeyDown(new KeyInfo(
            action,
            (modifiers & ModifierKeys.Shift) != 0,
            (modifiers & ModifierKeys.Control) != 0,
            (modifiers & ModifierKeys.Alt) != 0));

        if (handled)
            e.Handled = true;
        else
            base.OnKeyDown(e);
    }

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var width = e.Info.Width;
        var height = e.Info.Height;
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

        var report = EditorCanvas.Draw(
            e.Surface.Canvas,
            Kernel,
            Overlay,
            width,
            height,
            Dpi,
            Data,
            FontResolver,
            Images,
            Renderer);

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

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
        // 构造与 LoadTemplate 都走这里：首帧拿到视口尺寸后由内核自动整页适配一次。
        kernel.AutoFitOnFirstLayout = true;
        kernel.ViewChanged += OnKernelViewChanged;
        kernel.TemplateChanged += OnKernelViewChanged;
    }

    void OnKernelViewChanged() => InvalidateVisual();

    void SyncShift()
        => Kernel.ShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    PointerPoint ToPointer(MouseEventArgs e, PointerButton button)
    {
        var dip = e.GetPosition(this);
        var dpi = Dpi;
        return new PointerPoint(dip.X * dpi / 96.0, dip.Y * dpi / 96.0, dpi, button);
    }

    static PointerButton MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => PointerButton.Primary,
        MouseButton.Middle => PointerButton.Middle,
        MouseButton.Right => PointerButton.Secondary,
        _ => PointerButton.None,
    };
}
