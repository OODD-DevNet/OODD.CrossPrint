using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace CrossPrint.Hosts.WinForms;

/// <summary>
/// WinForms 设计画布：把鼠标、滚轮、键盘翻译成 <see cref="EditorKernel"/> 的语义输入，
/// 并通过 <see cref="EditorCanvas.Draw"/> 完成"叠加层 → 文档 → 叠加层"的唯一绘制编排。
/// </summary>
public class DesignerControl : SkiaCanvasControl
{
    EditorKernel _kernel;
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;
    bool _autoFitted;
    PointerButton _pressedButton;

    /// <summary>创建设计画布。</summary>
    /// <param name="template">初始模板；为 null 时创建 A4 默认模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        Overlay = new EditorRenderer();
        TabStop = true;
        WireKernel();
        PaintSurface += OnCanvasPaintSurface;
    }

    /// <summary>编辑器内核：宿主用它添加元素、执行命令与读取选中属性。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层渲染器（页框、网格、标尺、选中框）。修改其 <see cref="EditorRenderer.Options"/> 后调用 <c>Invalidate()</c>。</summary>
    public EditorRenderer Overlay { get; }

    /// <summary>文档渲染器；默认 <see cref="TemplateRenderer.Default"/>。</summary>
    public TemplateRenderer Renderer
    {
        get => _renderer;
        set
        {
            _renderer = value ?? throw new ArgumentNullException(nameof(value));
            Invalidate();
        }
    }

    /// <summary>数据绑定源；设计态通常为 null。</summary>
    public object? Data
    {
        get => _data;
        set
        {
            _data = value;
            Invalidate();
        }
    }

    /// <summary>字体解析器；null 使用系统默认。</summary>
    public IFontResolver? FontResolver
    {
        get => _fontResolver;
        set
        {
            _fontResolver = value;
            Invalidate();
        }
    }

    /// <summary>图片解析器；null 表示只有内嵌图片可用。</summary>
    public IImageResolver? Images
    {
        get => _images;
        set
        {
            _images = value;
            Invalidate();
        }
    }

    /// <summary>正在编辑的模板（与 <see cref="Kernel"/> 共用同一实例）。</summary>
    public PrintTemplate Template => _kernel.Template;

    /// <summary>宿主逻辑 DPI（WinForms <see cref="Control.DeviceDpi"/>）。</summary>
    public double Dpi => DeviceDpi;

    /// <summary>最近一次绘制产生的渲染告警。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次绘制完成后触发；宿主可读取 <see cref="LastReport"/>。</summary>
    public event EventHandler? Rendered;

    /// <summary>替换模板：重建内核、重接事件并整页适配当前控件尺寸。</summary>
    /// <param name="template">新模板。</param>
    public void LoadTemplate(PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        UnwireKernel();
        _kernel = new EditorKernel(template);
        WireKernel();

        _autoFitted = ClientSize.Width > 0 && ClientSize.Height > 0;
        FitToPage();
        Invalidate();
    }

    /// <summary>按当前客户区尺寸整页适配。</summary>
    public void FitToPage()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        _kernel.ViewWidthHostPx = width;
        _kernel.ViewHeightHostPx = height;
        _kernel.Viewport.Dpi = DeviceDpi;
        _kernel.Viewport.FitToPage(
            _kernel.Template.Paper.PageWidthMm,
            _kernel.PageHeightMm,
            width,
            height,
            DeviceDpi);
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        var button = ToPointerButton(e.Button);
        if (button == PointerButton.None)
            return;

        Focus();

        // 右键交给 ContextMenuStrip：若在这里 Capture，Windows 弹出菜单时捕获会被悄悄释放，
        // 后续 MouseUp 收不到，_pressedButton 就悬着。右键的命中与选中由菜单的 Opening 调 Kernel.SelectAt 完成。
        if (button == PointerButton.Secondary)
            return;

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        _pressedButton = button;
        Capture = true;
        _kernel.ShiftPressed = (ModifierKeys & Keys.Shift) == Keys.Shift;
        _kernel.OnPointerPressed(new PointerPoint(e.X, e.Y, DeviceDpi, button));
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 修饰键可能在拖拽中途按下/松开；内核不读键盘，必须由宿主持续同步。
        _kernel.ShiftPressed = (ModifierKeys & Keys.Shift) == Keys.Shift;
        _kernel.OnPointerMoved(new PointerPoint(e.X, e.Y, DeviceDpi, PointerButton.None));
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_pressedButton == PointerButton.None)
            return;

        // 拖拽中途松开另一个按键不该把进行中的拖拽提前收尾：只在松开的就是按下那个时才复位。
        var button = ToPointerButton(e.Button);
        if (button != _pressedButton)
            return;

        _pressedButton = PointerButton.None;
        Capture = false;
        _kernel.OnPointerReleased(new PointerPoint(e.X, e.Y, DeviceDpi, button));
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);

        // MouseUp 收不到时（Alt+Tab、右键菜单抢焦点）内核会停在拖拽态、_pressedButton 悬着让左键失灵：
        // 焦点一失就按 Esc 语义回滚，与 Eto 宿主 OnLostFocus 同一口径。
        if (_pressedButton == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        _kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Ctrl+滚轮缩放、Shift+滚轮横滚、普通滚轮纵滚：修饰键由这里读，内核按它分派；
        // WinForms 的鼠标事件参数没有 Handled，消费后不再调 base 即等效于已处理。
        var modifiers = ModifierKeys;
        if (!_kernel.OnMouseWheel(
                e.Delta,
                new PointerPoint(e.X, e.Y, DeviceDpi, PointerButton.None),
                modifiers.HasFlag(Keys.Control),
                modifiers.HasFlag(Keys.Shift)))
            base.OnMouseWheel(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        _kernel.ShiftPressed = e.Shift;

        var action = e.KeyCode switch
        {
            Keys.Delete or Keys.Back => KeyAction.Delete,
            Keys.Escape => KeyAction.Escape,
            Keys.Left => KeyAction.NudgeLeft,
            Keys.Right => KeyAction.NudgeRight,
            Keys.Up => KeyAction.NudgeUp,
            Keys.Down => KeyAction.NudgeDown,
            Keys.Oemplus or Keys.Add => KeyAction.ZoomIn,
            Keys.OemMinus or Keys.Subtract => KeyAction.ZoomOut,
            Keys.D0 or Keys.NumPad0 => KeyAction.ZoomActual,
            Keys.F => KeyAction.ZoomFit,
            _ => KeyAction.None,
        };

        if (action == KeyAction.None)
            return;

        var handled = _kernel.OnKeyDown(new KeyInfo(action, e.Shift, e.Control, e.Alt));
        e.Handled = handled;
    }

    /// <inheritdoc/>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _kernel.ShiftPressed = (ModifierKeys & Keys.Shift) == Keys.Shift;
    }

    void OnCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        // 首次拿到有效尺寸时自动整页适配一次；之后由用户缩放/平移，尺寸变化不再重排。
        if (!_autoFitted && e.Info.Width > 0 && e.Info.Height > 0)
        {
            _autoFitted = true;
            _kernel.ViewWidthHostPx = e.Info.Width;
            _kernel.ViewHeightHostPx = e.Info.Height;
            _kernel.Viewport.Dpi = DeviceDpi;
            _kernel.Viewport.FitToPage(
                _kernel.Template.Paper.PageWidthMm,
                _kernel.PageHeightMm,
                e.Info.Width,
                e.Info.Height,
                DeviceDpi);
        }

        var report = EditorCanvas.Draw(
            e.Surface.Canvas,
            _kernel,
            Overlay,
            e.Info.Width,
            e.Info.Height,
            DeviceDpi,
            _data,
            _fontResolver,
            _images,
            _renderer);

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    void WireKernel()
    {
        // 构造与 LoadTemplate 都走这里：首帧拿到视口尺寸后由内核自动整页适配一次。
        _kernel.AutoFitOnFirstLayout = true;
        _kernel.ViewChanged += OnKernelViewChanged;
        _kernel.TemplateChanged += OnKernelTemplateChanged;
    }

    void UnwireKernel()
    {
        _kernel.ViewChanged -= OnKernelViewChanged;
        _kernel.TemplateChanged -= OnKernelTemplateChanged;
    }

    void OnKernelViewChanged() => Invalidate();

    void OnKernelTemplateChanged() => Invalidate();

    static PointerButton ToPointerButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => PointerButton.Primary,
        MouseButtons.Middle => PointerButton.Middle,
        MouseButtons.Right => PointerButton.Secondary,
        _ => PointerButton.None,
    };
}
