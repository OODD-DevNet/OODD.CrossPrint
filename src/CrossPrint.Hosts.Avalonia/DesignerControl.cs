using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp;
using EditorPointerPoint = CrossPrint.Designer.PointerPoint;

namespace CrossPrint.Hosts.Avalonia;

/// <summary>
/// Avalonia 设计器画布：把 <see cref="EditorKernel"/> 的绘制与输入桥接到一个 <see cref="Control"/>。
/// </summary>
/// <remarks>
/// <para>
/// 绘制走 <c>DrawingContext.Custom</c> → <c>ISkiaSharpApiLeaseFeature</c> → <see cref="SKCanvas"/>，
/// 全程零位图拷贝。租到的画布单位是 DIP，这里先按 <see cref="RenderScaling"/> 换回宿主设备 px，
/// 再交给 <see cref="EditorCanvas.Draw"/>——与 WinForms/WPF 宿主喂给内核的坐标口径一致。
/// </para>
/// <para>
/// 输入事件在本类折成内核契约：位置换算成设备 px、DPI 写成 <c>96 × 缩放</c>、
/// 按下的按键记在字段里供松开事件复用（Avalonia 的移动事件不带按键）。
/// </para>
/// </remarks>
public sealed class DesignerControl : Control
{
    EditorKernel _kernel;
    bool _autoFitDone;
    PointerButton _pressedButton;
    double _lastHostX;
    double _lastHostY;

    /// <summary>创建设计器画布。</summary>
    /// <param name="template">待编辑模板；<c>null</c> 时新建 A4 纵向空模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        AttachKernel();
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>当前编辑内核。替换模板后这个引用会变，宿主应每次从本属性取。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层（页框、网格、标尺、选中框）。改它的 <see cref="EditorRenderer.Options"/> 后调用 <see cref="Redraw"/>。</summary>
    public EditorRenderer Overlay { get; } = new();

    /// <summary>文档渲染器；默认共享实例，宿主可换成带自定义渲染器注册表的实例；换实例后重绘避免残留旧画面。</summary>
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

    /// <summary>数据绑定源；设计态通常为 <c>null</c>。</summary>
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

    /// <summary>字体解析器；<c>null</c> 用系统默认；换解析器后重绘避免残留旧画面。</summary>
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

    /// <summary>图片解析器；<c>null</c> 表示只有内嵌图片可用；换解析器后重绘避免残留旧画面。</summary>
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

    /// <summary>正在编辑的模板（内核直接编辑该实例，不拷贝）。</summary>
    public PrintTemplate Template => _kernel.Template;

    /// <summary>当前渲染缩放（DIP → 设备 px 的倍数）；未挂到窗口时为 1.0。</summary>
    public double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>最近一次绘制的降级告警；尚未绘制过时为 <c>null</c>。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每完成一帧绘制后触发（在渲染线程上）。</summary>
    public event EventHandler? Rendered;

    /// <summary>换一份模板：重建内核并重新接线事件，随后整页适配。</summary>
    /// <param name="template">新模板。</param>
    public void LoadTemplate(PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        _kernel.ViewChanged -= OnKernelChanged;
        _kernel.TemplateChanged -= OnKernelChanged;
        _kernel = new EditorKernel(template);
        AttachKernel();
        _autoFitDone = false;
        FitToPage();
        InvalidateVisual();
    }

    /// <summary>整页适配当前视口。</summary>
    public void FitToPage()
    {
        var scaling = RenderScaling;
        var width = Bounds.Width * scaling;
        var height = Bounds.Height * scaling;
        if (width <= 0 || height <= 0)
            return;

        _kernel.Viewport.FitToPage(Template.Paper.PageWidthMm, _kernel.PageHeightMm, width, height, 96.0 * scaling);
        InvalidateVisual();
    }

    /// <summary>请求重绘。</summary>
    public void Redraw() => InvalidateVisual();

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        context.Custom(new SkiaDrawOperation(new Rect(Bounds.Size), DrawFrame));
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var button = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == PointerButton.None)
            return;

        Focus();

        // 右键交给宿主的上下文菜单：这里不 Capture、不 Handled。
        // 否则菜单弹不出来（或弹出即被捕获抢焦点），后续 PointerReleased 也收不到。
        // 命中与选中由宿主在打开菜单前调 Kernel.SelectAt 完成。
        if (button == PointerButton.Secondary)
            return;

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        _pressedButton = button;
        e.Pointer.Capture(this);
        _kernel.ShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _kernel.OnPointerPressed(ToPointerPoint(e, button));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // 移动事件不带按键：内核只按状态机消费，传 None 即可。
        _kernel.OnPointerMoved(ToPointerPoint(e, PointerButton.None));
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // 拖拽中途松开另一个按键不该把进行中的拖拽提前收尾：只在能确定松开的按键
        // 且它与按下时不一致时跳过；个别指针报 Other（换算不出按键）时维持原行为。
        var released = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (_pressedButton != PointerButton.None && released != PointerButton.None && released != _pressedButton)
            return;

        var button = _pressedButton != PointerButton.None ? _pressedButton : released;
        if (button == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        e.Pointer.Capture(null);
        _kernel.OnPointerReleased(ToPointerPoint(e, button));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // 窗口失焦等情况下捕获会被系统收回；此时补一次松开，避免内核停在拖拽状态。
        if (_pressedButton == PointerButton.None)
            return;

        var button = _pressedButton;
        _pressedButton = PointerButton.None;
        _kernel.OnPointerReleased(new EditorPointerPoint(_lastHostX, _lastHostY, 96.0 * RenderScaling, button));
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+滚轮缩放、Shift+滚轮横滚、普通滚轮纵滚：修饰键由这里读，内核按它分派。
        if (_kernel.OnMouseWheel(
                e.Delta.Y,
                ToPointerPoint(e, PointerButton.None),
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _kernel.ShiftPressed = shift;
        var handled = _kernel.OnKeyDown(new KeyInfo(
            MapKey(e.Key),
            shift,
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Alt)));

        if (handled)
            e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _kernel.ShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    void AttachKernel()
    {
        // 构造与 LoadTemplate 都走这里：首帧拿到视口尺寸后由内核自动整页适配一次。
        _kernel.AutoFitOnFirstLayout = true;
        _kernel.ViewChanged += OnKernelChanged;
        _kernel.TemplateChanged += OnKernelChanged;
    }

    void OnKernelChanged() => InvalidateVisual();

    void DrawFrame(SKCanvas canvas, Rect bounds)
    {
        var scaling = RenderScaling;
        var width = bounds.Width * scaling;
        var height = bounds.Height * scaling;
        if (width < 1 || height < 1)
            return;

        var dpi = 96.0 * scaling;

        // 内核的滚轮滚动/滚动条/自动适配都以视口尺寸为准；其余宿主在 FitViewport 里同步，
        // 本控件直接驱动 Viewport，这里每帧补齐（幂等赋值），否则普通滚轮永远滚不动。
        _kernel.Viewport.Dpi = dpi;
        _kernel.ViewWidthHostPx = width;
        _kernel.ViewHeightHostPx = height;

        // 只在第一次拿到真实尺寸时适配一次：窗口缩放不该把用户的缩放/平移重置掉。
        if (!_autoFitDone)
        {
            _autoFitDone = true;
            _kernel.Viewport.FitToPage(Template.Paper.PageWidthMm, _kernel.PageHeightMm, width, height, dpi);
        }

        var save = canvas.Save();
        canvas.Scale(1f / (float)scaling); // 租到的画布是 DIP，内核契约要设备 px
        RenderReport report;
        try
        {
            report = EditorCanvas.Draw(canvas, _kernel, Overlay, width, height, dpi, Data, FontResolver, Images, Renderer);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    EditorPointerPoint ToPointerPoint(PointerEventArgs e, PointerButton button)
    {
        var scaling = RenderScaling;
        var position = e.GetPosition(this);
        _lastHostX = position.X * scaling;
        _lastHostY = position.Y * scaling;
        return new EditorPointerPoint(_lastHostX, _lastHostY, 96.0 * scaling, button);
    }

    static PointerButton MapButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => PointerButton.Primary,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => PointerButton.Secondary,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => PointerButton.Middle,
        _ => PointerButton.None,
    };

    static KeyAction MapKey(Key key) => key switch
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
}
