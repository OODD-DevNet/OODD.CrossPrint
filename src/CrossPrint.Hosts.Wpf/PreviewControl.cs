using System;
using System.Windows;
using System.Windows.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CrossPrint.Hosts.Wpf;

/// <summary>
/// WPF 打印预览画布：与 <see cref="DesignerControl"/> 共用 <see cref="EditorCanvas.Draw"/> 渲染路径，
/// 但叠加层只保留纸张与页框（无网格/标尺/安全区），且不接任何输入——所见即导出所得。
/// </summary>
/// <remarks>
/// DPI 口径与设计画布完全一致（96 × 横轴缩放），因此同一模板在两边的毫米落点重合。
/// </remarks>
public sealed class PreviewControl : SKElement
{
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

        PaintSurface += OnPaintSurface;
    }

    /// <summary>渲染内核（只负责把模板与视口交给 <see cref="EditorCanvas"/>）。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层；默认关闭网格、标尺与安全区，宿主可再改。</summary>
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
            InvalidateVisual();
        }
    }

    TemplateRenderer _renderer = TemplateRenderer.Default;

    /// <summary>数据绑定源；赋值后立即重绘（例如换一条订单数据预览）。</summary>
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

    /// <summary>字体解析器；<c>null</c> 使用引擎默认实现。</summary>
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

    /// <summary>当前模板。赋值会重建内核并重新适配；传同一实例不做事。</summary>
    public PrintTemplate Template
    {
        get => Kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, Kernel.Template))
                return;

            _kernel.ViewChanged -= OnKernelViewChanged;
            _kernel.TemplateChanged -= OnKernelViewChanged;
            _kernel = new EditorKernel(value);
            _kernel.ViewChanged += OnKernelViewChanged;
            _kernel.TemplateChanged += OnKernelViewChanged;

            _autoFitted = false;
            FitToPage();
            InvalidateVisual();
        }
    }

    /// <summary>宿主逻辑 DPI：96 × 系统缩放（横轴缩放，两轴共用）。</summary>
    public double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX * 96.0;

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
            var scale = Dpi / 96.0;
            width = ActualWidth * scale;
            height = ActualHeight * scale;
        }

        if (width <= 0 || height <= 0)
            return;

        FitViewport(width, height);
        InvalidateVisual();
    }

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var width = e.Info.Width;
        var height = e.Info.Height;
        if (width <= 0 || height <= 0)
            return;

        _lastSurfaceWidth = width;
        _lastSurfaceHeight = height;

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

    void OnKernelViewChanged() => InvalidateVisual();
}
