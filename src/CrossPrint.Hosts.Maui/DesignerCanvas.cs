using System;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp.Views.Windows;
using Windows.System;
using Windows.UI.Core;
using PointerPoint = CrossPrint.Designer.PointerPoint;

namespace CrossPrint.Hosts.Maui;

/// <summary>
/// MAUI 宿主的 Windows 平台设计画布：与 Hosts.WinUi 的 DesignerControl 同一套交互翻译逻辑
/// （指针/滚轮/键盘 → <see cref="EditorKernel"/> 语义，宿主 UI 不碰像素），但事件面多两项
/// 供样例（而非宿主）消费：
/// <list type="bullet">
/// <item><see cref="CanvasRightPressed"/>：右键位置（画布 DIP + 窗口根 DIP），样例据此弹页内菜单；</item>
/// <item><see cref="RawKeyDown"/>：内核未映射的按键（含修饰键状态），样例用它接 Ctrl+D 之类的快捷键。</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// 为什么是 WinUI UserControl 而不是 MAUI ScrollView 包 SKCanvasView：MAUI 的
/// ScrollViewHandler 会用自管 ContentPanel 接管 ScrollViewer.Content，外部无法直接向平台视图
/// 挂事件；SKXamlCanvas 又是 Panel 拿不到键盘焦点。这里的结构（UserControl 包 SKXamlCanvas、
/// 控件本身持焦点）与 WinUI 宿主完全一致，注册为自定义 Handler 的 PlatformView。
/// </para>
/// <para>
/// 坐标纪律：<c>设备px = DIP × Dpi / 96</c>，DPI = <c>XamlRoot.RasterizationScale × 96</c>。
/// </para>
/// </remarks>
public sealed class DesignerCanvas : UserControl
{
    const double BaseDpi = 96.0;

    readonly SKXamlCanvas _canvas;
    readonly EditorRenderer _overlay;

    EditorKernel _kernel;
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;
    PointerButton _pressedButton;
    bool _autoFitted;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    /// <summary>创建平台设计画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public DesignerCanvas(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        // 画布尺寸第一次就位时由内核自动整页适配一次（页面默认居中）。
        _kernel.AutoFitOnFirstLayout = true;
        _overlay = new EditorRenderer();

        _canvas = new SKXamlCanvas
        {
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        };
        _canvas.PaintSurface += OnPaintSurface;

        Content = _canvas;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        Attach(_kernel);

        PointerPressed += OnPointerPressedHandler;
        PointerMoved += OnPointerMovedHandler;
        PointerReleased += OnPointerReleasedHandler;
        PointerCaptureLost += OnPointerCaptureLostHandler;
        PointerWheelChanged += OnPointerWheelChangedHandler;
        KeyDown += OnKeyDownHandler;
    }

    /// <summary>交互内核；外部命令（工具箱、属性面板、层次）都直接对它调用。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层（网格/标尺/选中框）。<see cref="EditorRenderer.Options"/> 可运行时改，改完调用 <see cref="Invalidate"/>。</summary>
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
    /// <remarks>
    /// 有意遮蔽 <see cref="Control.Template"/>（控件模板属性）：WinUI 的控件模板由默认样式提供，
    /// 本宿主从不按名赋值；这里沿用其它宿主的统一 API 名。
    /// </remarks>
    public new PrintTemplate Template => Kernel.Template;

    /// <summary>宿主逻辑 DPI：96 × <see cref="XamlRoot.RasterizationScale"/>；未加载时按 100% 兜底。</summary>
    public double Dpi => (XamlRoot?.RasterizationScale ?? 1.0) * BaseDpi;

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次 <see cref="SKXamlCanvas.PaintSurface"/> 绘制完成后触发（含告警信息）。</summary>
    public event EventHandler? Rendered;

    /// <summary>右键按下（焦点已收回、指针未捕获、内核未介入）。参数带画布内与窗口根两套 DIP 坐标。</summary>
    public event EventHandler<CanvasRightPressedEventArgs>? CanvasRightPressed;

    /// <summary>内核未映射的按键（<see cref="KeyAction.None"/>），修饰键状态随事件给出；样例用于 Ctrl+D 等自定义快捷键。</summary>
    public event EventHandler<RawKeyEventArgs>? RawKeyDown;

    /// <summary>把键盘焦点收回到画布（属性面板/按钮操作完成后由样例调用）。</summary>
    public void TakeFocus() => Focus(FocusState.Programmatic);

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
        // 换内核同样开首帧自动适配，与构造时行为一致。
        _kernel.AutoFitOnFirstLayout = true;
        Attach(_kernel);

        FitToPage();
        Invalidate();
    }

    /// <summary>整页适配窗口。优先用最近一次绘制表面的设备尺寸，未绘制过时按控件 DIP 尺寸换算。</summary>
    public void FitToPage()
    {
        var width = _lastSurfaceWidth;
        var height = _lastSurfaceHeight;
        if (width <= 0 || height <= 0)
        {
            var scale = Dpi / BaseDpi;
            width = ActualWidth * scale;
            height = ActualHeight * scale;
        }

        if (width <= 0 || height <= 0)
            return;

        FitViewport(width, height, Dpi);
        Invalidate();
    }

    /// <summary>请求重绘画布（改完 <see cref="EditorRenderer.Options"/> 等外部状态后调用）。</summary>
    public void Invalidate() => _canvas.Invalidate();

    void OnPointerPressedHandler(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);

        var point = e.GetCurrentPoint(_canvas);
        var properties = point.Properties;

        // 右键交给样例上下文菜单（与 WinUI 宿主同一套处理）：这里若 CapturePointer，
        // 弹出层出现时系统会释放捕获，PointerCaptureLost 随即按 Esc 语义把刚选中的元素清掉。
        // 所以右键既不捕获、不进内核，也不标记已处理，只把焦点收过来并通知样例（带两套坐标）。
        if (properties.IsRightButtonPressed)
        {
            var windowPos = _canvas.TransformToVisual(null)?.TransformPoint(point.Position) ?? point.Position;
            CanvasRightPressed?.Invoke(this, new CanvasRightPressedEventArgs(
                point.Position.X,
                point.Position.Y,
                windowPos.X,
                windowPos.Y));
            return;
        }

        // 已有按键在拖拽时不接受第二个按键，否则 _pressedButton 被覆盖后先按下的那个永远收不到松开。
        if (_pressedButton != PointerButton.None)
            return;

        var button = properties.IsLeftButtonPressed
            ? PointerButton.Primary
            : properties.IsMiddleButtonPressed
                ? PointerButton.Middle
                : PointerButton.None;

        if (button == PointerButton.None)
            return;

        _pressedButton = button;
        SyncShift();
        CapturePointer(e.Pointer);
        Kernel.OnPointerPressed(ToPointer(e, button));
        e.Handled = true;
    }

    void OnPointerMovedHandler(object sender, PointerRoutedEventArgs e)
    {
        // 拖拽期间 Shift 可能中途按下/松开，必须每次同步；空闲时内核会自行忽略。
        SyncShift();
        Kernel.OnPointerMoved(ToPointer(e, PointerButton.None));
    }

    void OnPointerReleasedHandler(object sender, PointerRoutedEventArgs e)
    {
        var button = _pressedButton;
        if (button == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnPointerReleased(ToPointer(e, button));
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    void OnPointerCaptureLostHandler(object sender, PointerRoutedEventArgs e)
    {
        // 捕获意外丢失（窗口失活等）时内核可能还停在拖拽态：按 Esc 语义回滚，避免后续无键移动继续改模型。
        if (_pressedButton == PointerButton.None)
            return;

        _pressedButton = PointerButton.None;
        Kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));
    }

    void OnPointerWheelChangedHandler(object sender, PointerRoutedEventArgs e)
    {
        // WinUI 的 MouseWheelDelta 是原生 ±120 刻度，与 WinForms 一致；内核只看符号。
        var delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        // 平台层是 WinUI UserControl，PointerWheelChanged 自带修饰键：Ctrl=以光标为锚缩放，
        // Shift=横向滚动，普通滚轮=纵向滚动（MAUI 跨平台层无滚轮事件，故在平台画布上挂）。
        var control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (Kernel.OnMouseWheel(delta, ToPointer(e, PointerButton.None), control, shift))
            e.Handled = true;
    }

    void OnKeyDownHandler(object sender, KeyRoutedEventArgs e)
    {
        SyncShift();

        var action = e.Key switch
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

        if (action == KeyAction.None)
        {
            // 内核没有映射的按键交给样例（Ctrl+D、自定义快捷键……）；Ctrl 单按也会到这里，
            // 样例自己组合判断，不会误触发。
            RawKeyDown?.Invoke(this, new RawKeyEventArgs(
                e.Key,
                ModifierDown(VirtualKey.Control),
                ModifierDown(VirtualKey.Shift),
                ModifierDown(VirtualKey.Menu)));
            return;
        }

        var handled = Kernel.OnKeyDown(new KeyInfo(
            action,
            ModifierDown(VirtualKey.Shift),
            ModifierDown(VirtualKey.Control),
            ModifierDown(VirtualKey.Menu)));

        if (handled)
            e.Handled = true;
    }

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var width = e.Info.Width;
        var height = e.Info.Height;
        if (width <= 0 || height <= 0)
            return;

        _lastSurfaceWidth = width;
        _lastSurfaceHeight = height;

        var dpi = Dpi;

        // 只在第一次拿到有效尺寸时适配；窗口之后怎么拉伸都不再自动改缩放。
        if (!_autoFitted)
        {
            _autoFitted = true;
            FitViewport(width, height, dpi);
        }

        // e.Info 已是宿主设备 px（SKXamlCanvas 默认 IgnorePixelScaling=false），画布不再二次缩放。
        var report = EditorCanvas.Draw(
            e.Surface.Canvas,
            Kernel,
            Overlay,
            width,
            height,
            dpi,
            Data,
            FontResolver,
            Images,
            Renderer);

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    void FitViewport(double widthHostPx, double heightHostPx, double dpi)
    {
        Kernel.ViewWidthHostPx = widthHostPx;
        Kernel.ViewHeightHostPx = heightHostPx;
        Kernel.Viewport.Dpi = dpi;
        Kernel.Viewport.FitToPage(
            Kernel.Template.Paper.PageWidthMm,
            Kernel.PageHeightMm,
            widthHostPx,
            heightHostPx,
            dpi);
    }

    void Attach(EditorKernel kernel)
    {
        kernel.ViewChanged += OnKernelViewChanged;
        kernel.TemplateChanged += OnKernelViewChanged;
    }

    void OnKernelViewChanged() => Invalidate();

    void SyncShift()
        => Kernel.ShiftPressed = ModifierDown(VirtualKey.Shift);

    static bool ModifierDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    PointerPoint ToPointer(PointerRoutedEventArgs e, PointerButton button)
    {
        var dip = e.GetCurrentPoint(_canvas).Position;
        var dpi = Dpi;
        var scale = dpi / BaseDpi;
        return new PointerPoint(dip.X * scale, dip.Y * scale, dpi, button);
    }
}

/// <summary>画布右键按下事件参数：画布内 DIP 坐标 + 窗口根 DIP 坐标（页内浮动菜单定位用）。</summary>
public sealed class CanvasRightPressedEventArgs : EventArgs
{
    /// <summary>创建右键按下事件参数。</summary>
    /// <param name="xDip">相对画布左上角的 X（DIP）。</param>
    /// <param name="yDip">相对画布左上角的 Y（DIP）。</param>
    /// <param name="windowXDip">相对窗口内容根的 X（DIP）。</param>
    /// <param name="windowHeightDip">相对窗口内容根的 Y（DIP）。</param>
    public CanvasRightPressedEventArgs(double xDip, double yDip, double windowXDip, double windowHeightDip)
    {
        XDip = xDip;
        YDip = yDip;
        WindowXDip = windowXDip;
        WindowYDip = windowHeightDip;
    }

    /// <summary>相对画布左上角的 X（DIP）。</summary>
    public double XDip { get; }

    /// <summary>相对画布左上角的 Y（DIP）。</summary>
    public double YDip { get; }

    /// <summary>相对窗口内容根的 X（DIP）；用于把浮动菜单放到右键位置。</summary>
    public double WindowXDip { get; }

    /// <summary>相对窗口内容根的 Y（DIP）。</summary>
    public double WindowYDip { get; }
}

/// <summary>内核未映射按键事件参数（样例自定义快捷键用）。</summary>
public sealed class RawKeyEventArgs : EventArgs
{
    /// <summary>创建未映射按键事件参数。</summary>
    /// <param name="key">原始虚拟键。</param>
    /// <param name="control">Ctrl 是否按下。</param>
    /// <param name="shift">Shift 是否按下。</param>
    /// <param name="alt">Alt 是否按下。</param>
    public RawKeyEventArgs(VirtualKey key, bool control, bool shift, bool alt)
    {
        Key = key;
        Control = control;
        Shift = shift;
        Alt = alt;
    }

    /// <summary>原始虚拟键。</summary>
    public VirtualKey Key { get; }

    /// <summary>Ctrl 是否按下。</summary>
    public bool Control { get; }

    /// <summary>Shift 是否按下。</summary>
    public bool Shift { get; }

    /// <summary>Alt 是否按下。</summary>
    public bool Alt { get; }
}
