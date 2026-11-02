using System;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp.Views.Windows;

namespace CrossPrint.Hosts.Maui;

/// <summary>
/// MAUI 打印预览画布（跨平台门面）：与 <see cref="DesignerControl"/> 共用 <see cref="EditorCanvas.Draw"/>
/// 渲染路径，但叠加层只保留纸张与页框（无网格/标尺/安全区），且不接任何输入——所见即导出所得。
/// </summary>
/// <remarks>
/// <para>
/// 平台视图直接用 <see cref="SKXamlCanvas"/>（无输入需求，不需要 UserControl 包一层持焦点），
/// 门面在构造时创建并挂好 PaintSurface，Handler 的 <c>CreatePlatformView</c> 直接返回它。
/// </para>
/// <para>
/// DPI 口径与设计画布完全一致（96 × RasterizationScale），同一模板在两边的毫米落点重合。
/// </para>
/// </remarks>
public class PreviewControl : View
{
    const double BaseDpi = 96.0;

    readonly SKXamlCanvas _canvas = new()
    {
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
    };

    readonly EditorRenderer _overlay = new()
    {
        Options = new EditorRendererOptions
        {
            ShowGrid = false,
            ShowRulers = false,
            ShowMarginGuides = false,
        },
    };

    EditorKernel _kernel;
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;
    bool _autoFitted;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    /// <summary>创建预览画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public PreviewControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        _kernel.ViewChanged += OnKernelViewChanged;
        _kernel.TemplateChanged += OnKernelViewChanged;

        _canvas.PaintSurface += OnPaintSurface;
    }

    /// <summary>Windows 平台画布（SKXamlCanvas，由 Handler 直接作为 PlatformView 返回）。</summary>
    public SKXamlCanvas Platform => _canvas;

    /// <summary>渲染内核（只负责把模板与视口交给 <see cref="EditorCanvas"/>）。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层；默认关闭网格、标尺与安全区，宿主可再改（改完调用 <see cref="Invalidate"/>）。</summary>
    public EditorRenderer Overlay => _overlay;

    /// <summary>文档渲染器；默认共享实例。</summary>
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

    /// <summary>数据绑定源；赋值后立即重绘（例如换一条订单数据预览）。</summary>
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

    /// <summary>字体解析器；<c>null</c> 使用引擎默认实现。</summary>
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

    /// <summary>当前模板。赋值会重建内核并重新适配；传同一实例不做事。</summary>
    public PrintTemplate Template
    {
        get => _kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _kernel.Template))
                return;

            _kernel.ViewChanged -= OnKernelViewChanged;
            _kernel.TemplateChanged -= OnKernelViewChanged;
            _kernel = new EditorKernel(value);
            _kernel.ViewChanged += OnKernelViewChanged;
            _kernel.TemplateChanged += OnKernelViewChanged;

            _autoFitted = false;
            FitToPage();
            Invalidate();
        }
    }

    /// <summary>宿主逻辑 DPI：96 × RasterizationScale；未加载时按 100% 兜底。</summary>
    public double Dpi => (_canvas.XamlRoot?.RasterizationScale ?? 1.0) * BaseDpi;

    /// <summary>最近一次绘制的降级告警汇总。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次绘制完成后触发。</summary>
    public event EventHandler? Rendered;

    /// <summary>整页适配窗口。优先用最近一次绘制表面的设备尺寸，未绘制过时按控件 DIP 尺寸换算。</summary>
    public void FitToPage()
    {
        var width = _lastSurfaceWidth;
        var height = _lastSurfaceHeight;
        if (width <= 0 || height <= 0)
        {
            var scale = Dpi / BaseDpi;
            width = _canvas.ActualWidth * scale;
            height = _canvas.ActualHeight * scale;
        }

        if (width <= 0 || height <= 0)
            return;

        FitViewport(width, height, Dpi);
        Invalidate();
    }

    /// <summary>请求重绘画布。</summary>
    public void Invalidate() => _canvas.Invalidate();

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

        var report = EditorCanvas.Draw(
            e.Surface.Canvas,
            _kernel,
            _overlay,
            width,
            height,
            dpi,
            _data,
            _fontResolver,
            _images,
            _renderer);

        LastReport = report;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    void FitViewport(double widthHostPx, double heightHostPx, double dpi)
    {
        _kernel.ViewWidthHostPx = widthHostPx;
        _kernel.ViewHeightHostPx = heightHostPx;
        _kernel.Viewport.Dpi = dpi;
        _kernel.Viewport.FitToPage(
            _kernel.Template.Paper.PageWidthMm,
            _kernel.PageHeightMm,
            widthHostPx,
            heightHostPx,
            dpi);
    }

    void OnKernelViewChanged() => Invalidate();
}
