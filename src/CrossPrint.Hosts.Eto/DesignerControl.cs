using System.Runtime.InteropServices;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using Eto.Drawing;
using Eto.Forms;
using SkiaSharp;

namespace CrossPrint.Hosts.Eto;

/// <summary>
/// Eto.Forms 设计画布：把 <see cref="EditorKernel"/> 的交互与 <see cref="EditorCanvas"/> 的绘制接到
/// <see cref="Drawable"/> 上。鼠标、滚轮、键盘事件全部翻译成内核语义，宿主 UI 不再碰像素。
/// </summary>
/// <remarks>
/// <para>
/// 坐标纪律：Eto 的控件尺寸与事件坐标都是<b>逻辑像素</b>（<see cref="Window.LogicalPixelSize"/> 才是设备倍率），
/// 而 <see cref="PointerPoint"/> 要求宿主设备 px，所以按下式换算一次：
/// <c>设备px = 逻辑px × Dpi / 96</c>。内核内部再换到 mm。
/// </para>
/// <para>
/// 渲染路径：按设备 px 分配 <see cref="Bitmap"/>（<see cref="PixelFormat.Format32bppRgba"/>），
/// 用 <see cref="Bitmap.Lock"/> 拿到 <see cref="BitmapData"/> 后把 Skia 直接画进该缓冲区；
/// 若 Skia 的光栅后端不接受非预乘 alpha，则退到预乘 <see cref="SKBitmap"/> 再逐行
/// <see cref="SKPixmap.ReadPixels(SKImageInfo, IntPtr, int, int, int)"/> 转写，行跨距取
/// <see cref="BitmapData.ScanWidth"/>、上下方向按 <see cref="BitmapData.Flipped"/> 处理。
/// 设计画布整幅被工作区底色覆盖（不透明），两条路径的显示结果一致。
/// </para>
/// </remarks>
public sealed class DesignerControl : Drawable
{
    readonly EditorRenderer _overlay;

    EditorKernel _kernel;
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;

    Bitmap? _frame;
    int _frameWidth;
    int _frameHeight;

    PointerButton _pressedButton;
    bool _autoFitted;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    double _dpi;
    double? _dpiOverride;

    /// <summary>创建设计画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public DesignerControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        _overlay = new EditorRenderer();
        _dpi = DetectScreenDpi();
        Attach(_kernel);

        CanFocus = true;

        Paint += OnPaint;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += OnLostFocus;
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
            Invalidate();
        }
    }

    /// <summary>数据绑定源；设计态通常为 <c>null</c>（占位符原样显示为空），示例可传样例数据。</summary>
    public object? Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(_data, value))
                return;

            _data = value;
            Invalidate();
        }
    }

    /// <summary>字体解析器；<c>null</c> 使用引擎的系统字体默认实现。</summary>
    public IFontResolver? FontResolver
    {
        get => _fontResolver;
        set
        {
            if (ReferenceEquals(_fontResolver, value))
                return;

            _fontResolver = value;
            Invalidate();
        }
    }

    /// <summary>图片解析器；<c>null</c> 表示只有模板内嵌图片可用。</summary>
    public IImageResolver? Images
    {
        get => _images;
        set
        {
            if (ReferenceEquals(_images, value))
                return;

            _images = value;
            Invalidate();
        }
    }

    /// <summary>当前模板（内核直接编辑它，不拷贝）。</summary>
    public PrintTemplate Template => Kernel.Template;

    /// <summary>
    /// 宿主逻辑 DPI：默认取 <see cref="Screen.PrimaryScreen"/> 的 <see cref="Screen.DPI"/>（96 = 100%）。
    /// </summary>
    /// <remarks>
    /// <see cref="DpiOverride"/> 非 <c>null</c> 时以覆盖值为准；此时给本属性赋值只更新"屏幕基准值"，
    /// 不会改变当前生效的 DPI。写回覆盖值 <c>null</c> 即可恢复。
    /// </remarks>
    public double Dpi
    {
        get => _dpiOverride ?? _dpi;
        set
        {
            if (!IsValidDpi(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "DPI 必须是有限正数。");

            if (_dpi.Equals(value))
                return;

            _dpi = value;
            Invalidate();
        }
    }

    /// <summary>
    /// DPI 覆盖值；<c>null</c> 表示跟随 <see cref="Dpi"/> 的屏幕基准值。
    /// 无边框窗口/嵌入宿主等拿不到正确屏幕 DPI 的场景用它钉死口径。
    /// </summary>
    public double? DpiOverride
    {
        get => _dpiOverride;
        set
        {
            if (value is { } dpi && !IsValidDpi(dpi))
                throw new ArgumentOutOfRangeException(nameof(value), value, "DPI 覆盖值必须是有限正数。");

            if (Nullable.Equals(_dpiOverride, value))
                return;

            _dpiOverride = value;
            Invalidate();
        }
    }

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次 <see cref="Drawable.Paint"/> 绘制完成后触发（含告警信息）。</summary>
    public event EventHandler? Rendered;

    /// <summary>替换当前模板：旧内核解绑、新内核重新绑定事件，并按当前视口尺寸整页适配一次。</summary>
    /// <param name="template">新模板；内核直接编辑这个实例。</param>
    public void LoadTemplate(PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (ReferenceEquals(template, Kernel.Template))
            return;

        Detach(_kernel);
        _kernel = new EditorKernel(template);
        Attach(_kernel);

        FitToPage();
        Invalidate();
    }

    /// <summary>整页适配窗口。优先用最近一次绘制的设备尺寸，未绘制过时按控件逻辑尺寸换算。</summary>
    public void FitToPage()
    {
        var width = _lastSurfaceWidth;
        var height = _lastSurfaceHeight;
        if (width <= 0 || height <= 0)
        {
            var scale = Dpi / 96.0;
            width = Size.Width * scale;
            height = Size.Height * scale;
        }

        if (width <= 0 || height <= 0)
            return;

        FitViewport(width, height);
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach(_kernel);
            _frame?.Dispose();
            _frame = null;
        }

        base.Dispose(disposing);
    }

    void OnPaint(object? sender, PaintEventArgs e)
    {
        var size = Size;
        var dpi = Dpi;
        var scale = dpi / 96.0;
        var width = (int)Math.Round(size.Width * scale, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(size.Height * scale, MidpointRounding.AwayFromZero);
        if (width <= 0 || height <= 0)
            return;

        // 只在第一次拿到有效尺寸时自动适配；之后窗口怎么拉伸都不再自动改缩放。
        _lastSurfaceWidth = width;
        _lastSurfaceHeight = height;
        if (!_autoFitted)
        {
            _autoFitted = true;
            FitViewport(width, height);
        }

        var frame = EnsureFrame(width, height);
        if (frame is null)
            return;

        LastReport = RenderFrame(frame, width, height, dpi);
        e.Graphics.DrawImage(frame, new RectangleF(0, 0, size.Width, size.Height));
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        var button = MapButton(e.Buttons);
        if (button == PointerButton.None)
            return;

        Focus();

        // 右键交给宿主的上下文菜单（示例在 MouseUp 里 Show）：若在这里记按压态，
        // 菜单弹出导致 LostFocus 时会按 Esc 语义把刚选中的元素清掉，菜单项随即失去作用对象。
        // 与其他宿主同一口径：右键只取焦点，不进内核、不置 Handled。
        if (button == PointerButton.Secondary)
            return;

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        _pressedButton = button;
        SyncShift(e.Modifiers);
        Kernel.OnPointerPressed(ToPointer(e, button));
        Invalidate();
        e.Handled = true;
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        // 拖拽期间 Shift 可能中途按下/松开，必须每次同步；空闲时内核会自行忽略。
        SyncShift(e.Modifiers);
        Kernel.OnPointerMoved(ToPointer(e, PointerButton.None));
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_pressedButton == PointerButton.None)
            return;

        // 个别平台在 MouseUp 时不带按键位；此时把"当前按下的键"当作松开的就是它。
        var button = MapButton(e.Buttons);
        if (button != PointerButton.None && button != _pressedButton)
            return;

        var released = _pressedButton;
        _pressedButton = PointerButton.None;
        Kernel.OnPointerReleased(ToPointer(e, released));
        Invalidate();
        e.Handled = true;
    }

    void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // Eto 的 Delta 是 SizeF（横向/纵向滚动量），内核只用符号；优先纵向，横向滚轮退到 Width。
        var delta = e.Delta.Height != 0 ? e.Delta.Height : e.Delta.Width;
        if (delta == 0)
            return;

        SyncShift(e.Modifiers);
        // Ctrl+滚轮缩放、Shift+滚轮横滚、普通滚轮纵滚：修饰键由这里读，内核按它分派。
        if (Kernel.OnMouseWheel(
                delta,
                ToPointer(e, PointerButton.None),
                e.Modifiers.HasFlag(Keys.Control),
                e.Modifiers.HasFlag(Keys.Shift)))
            e.Handled = true;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        Kernel.ShiftPressed = e.Shift;

        var action = MapKey(e.Key);
        if (action == KeyAction.None)
            return;

        var handled = Kernel.OnKeyDown(new KeyInfo(action, e.Shift, e.Control, e.Alt));
        if (handled)
            e.Handled = true;
    }

    void OnKeyUp(object? sender, KeyEventArgs e)
        => Kernel.ShiftPressed = e.Shift;

    void OnLostFocus(object? sender, EventArgs e)
    {
        // 焦点被其它控件抢走时内核可能还停在拖拽态：按 Esc 语义回滚，避免后续无键移动继续改模型。
        if (_pressedButton == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));
    }

    Bitmap? EnsureFrame(int width, int height)
    {
        if (_frame is not null && _frameWidth == width && _frameHeight == height)
            return _frame;

        _frame?.Dispose();
        _frame = new Bitmap(width, height, PixelFormat.Format32bppRgba);
        _frameWidth = width;
        _frameHeight = height;
        return _frame;
    }

    RenderReport RenderFrame(Bitmap frame, int width, int height, double dpi)
    {
        using var data = frame.Lock();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        SKSurface? surface = null;
        try
        {
            // Skia 的光栅后端在部分版本只接受预乘 alpha；先按非预乘试，失败再走下面逐行转写。
            surface = SKSurface.Create(info, data.Data, data.ScanWidth);
        }
        catch (Exception)
        {
            surface = null;
        }

        if (surface is not null)
        {
            using (surface)
                return DrawFrame(surface.Canvas, width, height, dpi);
        }

        var premultiplied = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(premultiplied);
        RenderReport report;
        using (var canvas = new SKCanvas(bitmap))
            report = DrawFrame(canvas, width, height, dpi);

        CopyRows(bitmap, data, info);
        return report;
    }

    RenderReport DrawFrame(SKCanvas canvas, double widthHostPx, double heightHostPx, double dpi)
        => EditorCanvas.Draw(
            canvas,
            Kernel,
            Overlay,
            widthHostPx,
            heightHostPx,
            dpi,
            Data,
            FontResolver,
            Images,
            Renderer);

    static void CopyRows(SKBitmap source, BitmapData destination, SKImageInfo info)
    {
        using var pixmap = source.PeekPixels();
        if (pixmap is null)
            return;

        var rowBytes = info.RowBytes;
        var buffer = new byte[rowBytes];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var rowInfo = new SKImageInfo(info.Width, 1, info.ColorType, info.AlphaType);
            var rowPointer = handle.AddrOfPinnedObject();
            for (var y = 0; y < info.Height; y++)
            {
                if (!pixmap.ReadPixels(rowInfo, rowPointer, rowBytes, 0, y))
                    continue;

                // BitmapData 可能是上下翻转的：源第 0 行要落到目标最后一行。
                var destinationRow = destination.Flipped ? info.Height - 1 - y : y;
                Marshal.Copy(buffer, 0, destination.Data + destinationRow * destination.ScanWidth, rowBytes);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    void FitViewport(double widthHostPx, double heightHostPx)
    {
        Kernel.ViewWidthHostPx = widthHostPx;
        Kernel.ViewHeightHostPx = heightHostPx;
        Kernel.Viewport.Dpi = Dpi;
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

    void Detach(EditorKernel kernel)
    {
        kernel.ViewChanged -= OnKernelViewChanged;
        kernel.TemplateChanged -= OnKernelViewChanged;
    }

    void OnKernelViewChanged() => Invalidate();

    void SyncShift(Keys modifiers) => Kernel.ShiftPressed = (modifiers & Keys.Shift) != 0;

    PointerPoint ToPointer(MouseEventArgs e, PointerButton button)
    {
        var dpi = Dpi;
        var scale = dpi / 96.0;
        return new PointerPoint(e.Location.X * scale, e.Location.Y * scale, dpi, button);
    }

    static bool IsValidDpi(double value)
        => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

    static double DetectScreenDpi()
    {
        try
        {
            var screen = Screen.PrimaryScreen;
            return screen is null || screen.DPI <= 0 ? 96.0 : screen.DPI;
        }
        catch (Exception)
        {
            // 平台尚未初始化（在 Application 之前构造控件）：退回 100%，由 Dpi/DpiOverride 再纠偏。
            return 96.0;
        }
    }

    static PointerButton MapButton(MouseButtons button) => button switch
    {
        MouseButtons.Primary => PointerButton.Primary,
        MouseButtons.Alternate => PointerButton.Secondary,
        MouseButtons.Middle => PointerButton.Middle,
        _ => PointerButton.None,
    };

    static KeyAction MapKey(Keys key) => key switch
    {
        Keys.Delete or Keys.Backspace => KeyAction.Delete,
        Keys.Escape => KeyAction.Escape,
        Keys.Left => KeyAction.NudgeLeft,
        Keys.Right => KeyAction.NudgeRight,
        Keys.Up => KeyAction.NudgeUp,
        Keys.Down => KeyAction.NudgeDown,
        // Eto 2.4 起 + 号键改叫 Equal（与 VK_OEM_PLUS 同一个物理键），Keys.Plus 已标记过时。
        Keys.Equal or Keys.Add => KeyAction.ZoomIn,
        Keys.Minus or Keys.Subtract => KeyAction.ZoomOut,
        Keys.D0 or Keys.Keypad0 => KeyAction.ZoomActual,
        Keys.F => KeyAction.ZoomFit,
        _ => KeyAction.None,
    };
}
