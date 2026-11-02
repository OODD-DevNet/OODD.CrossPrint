using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp.Views.Desktop;

namespace CrossPrint.Hosts.WinForms;

/// <summary>
/// WinForms 打印预览画布：与设计器共用 <see cref="EditorCanvas.Draw"/> 渲染路径，
/// 但关闭网格/标尺/安全区叠加，也不处理任何输入。
/// </summary>
public class PreviewControl : SkiaCanvasControl
{
    EditorKernel _kernel;
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;
    bool _autoFitted;

    /// <summary>创建预览画布。</summary>
    /// <param name="template">初始模板；为 null 时创建 A4 默认模板。</param>
    public PreviewControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        Overlay = new EditorRenderer
        {
            Options = new EditorRendererOptions
            {
                ShowGrid = false,
                ShowRulers = false,
                ShowMarginGuides = false,
            },
        };
        WireKernel();
        PaintSurface += OnCanvasPaintSurface;
        TabStop = false;
    }

    /// <summary>预览内核：宿主用它读取页面尺寸与视口。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层渲染器；预览默认只保留页纸与页框。</summary>
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

    /// <summary>当前预览的模板；赋值会重建内核。</summary>
    public PrintTemplate Template
    {
        get => _kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            UnwireKernel();
            _kernel = new EditorKernel(value);
            WireKernel();
            _autoFitted = false;
            Invalidate();
        }
    }

    /// <summary>数据绑定源；赋值后立即重绘。</summary>
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

    /// <summary>宿主逻辑 DPI（WinForms <see cref="Control.DeviceDpi"/>）。</summary>
    public double Dpi => DeviceDpi;

    /// <summary>最近一次绘制产生的渲染告警。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每次绘制完成后触发；宿主可读取 <see cref="LastReport"/>。</summary>
    public event EventHandler? Rendered;

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

    void OnCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        // 首次拿到有效尺寸时自动整页适配一次；窗口后续缩放不改变用户的查看比例。
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
}
