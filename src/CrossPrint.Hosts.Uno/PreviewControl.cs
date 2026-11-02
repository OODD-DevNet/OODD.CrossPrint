using System;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace CrossPrint.Hosts.Uno;

/// <summary>
/// Uno Platform 打印预览画布：与 <see cref="DesignerControl"/> 共用 <see cref="EditorCanvas.Draw"/> 渲染路径，
/// 但叠加层只保留纸张与页框（无网格/标尺/安全区），且不接任何输入——所见即导出所得。
/// </summary>
/// <remarks>
/// 与设计画布一样基于 <see cref="SKCanvasElement"/>（Skia 桌面头通用；SKSwapChainPanel 在桌面头不可用），
/// 坐标换算规则见 <see cref="DesignerControl"/> 的备注。
/// DPI 口径与设计画布完全一致（96 × <see cref="XamlRoot.RasterizationScale"/>），因此同一模板在两边的毫米落点重合。
/// 与 WinUI/WPF 预览控件一样，它只是设计器的只读视图，所有编辑命令都不接。
/// </remarks>
public sealed class PreviewControl : SKCanvasElement
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
        _kernel.ViewChanged += OnKernelChanged;
        _kernel.TemplateChanged += OnKernelChanged;

        Loaded += (_, _) => _invalidateReady = true;
    }

    /// <summary>渲染内核（只负责把模板与视口交给 <see cref="EditorCanvas"/>）。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层；默认关闭网格、标尺与安全区，宿主可再改（改完调用 <see cref="M:Uno.WinUI.Graphics2DSK.SKCanvasElement.Invalidate"/>）。</summary>
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
            InvalidateSafe();
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
            InvalidateSafe();
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

    /// <summary>当前模板。赋值会重建内核并重新适配；传同一实例不做事。</summary>
    public PrintTemplate Template
    {
        get => Kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, Kernel.Template))
                return;

            _kernel.ViewChanged -= OnKernelChanged;
            _kernel.TemplateChanged -= OnKernelChanged;
            _kernel = new EditorKernel(value);
            _kernel.ViewChanged += OnKernelChanged;
            _kernel.TemplateChanged += OnKernelChanged;

            _autoFitted = false;
            FitToPage();
            InvalidateSafe();
        }
    }

    /// <summary>宿主逻辑 DPI：96 × <see cref="XamlRoot.RasterizationScale"/>（两轴共用）。</summary>
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

    /// <summary>每次绘制完成后触发。</summary>
    public event EventHandler? Rendered;

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

    /// <summary>引擎绘制入口：合成器回调，画布处于 DIP 坐标系，换算规则同 <see cref="DesignerControl"/>。</summary>
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        // 与 DesignerControl 相同的坐标纪律：画布处于 DIP 坐标系，先撤销预缩放再按设备 px 交给引擎。
        var dpiScale = Dpi / 96.0;
        var width = (int)Math.Round(area.Width * dpiScale);
        var height = (int)Math.Round(area.Height * dpiScale);
        if (width <= 0 || height <= 0)
            return;

        _lastSurfaceWidth = width;
        _lastSurfaceHeight = height;

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

    void OnKernelChanged() => InvalidateSafe();
}
