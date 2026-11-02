using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using SkiaSharp;

namespace CrossPrint.Hosts.Avalonia;

/// <summary>
/// 打印预览画布：与设计器共用 <see cref="EditorCanvas"/> 这一条绘制路径，差别只有
/// 数据源（通常非空）与叠加层参数（不画网格/标尺/安全区），且不处理任何输入。
/// </summary>
/// <remarks>
/// 预览不是"另一套渲染"：文档仍由 <see cref="TemplateRenderer"/> 画出，因此预览与
/// PDF/打印机产物同源；这里只负责把画布、DPI 与数据交给内核。
/// </remarks>
public sealed class PreviewControl : Control
{
    EditorKernel _kernel;
    bool _autoFitDone;

    /// <summary>创建预览画布。</summary>
    /// <param name="template">待预览模板；<c>null</c> 时新建 A4 纵向空模板。</param>
    public PreviewControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        _kernel.ViewChanged += OnKernelChanged;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>预览使用的内核（视口与页面高度换算）。</summary>
    public EditorKernel Kernel => _kernel;

    /// <summary>叠加层；预览默认关掉网格、标尺与安全区。</summary>
    public EditorRenderer Overlay { get; } = new()
    {
        Options = new EditorRendererOptions
        {
            ShowGrid = false,
            ShowRulers = false,
            ShowMarginGuides = false,
        },
    };

    /// <summary>文档渲染器；默认共享实例；换实例后重绘避免残留旧画面。</summary>
    public TemplateRenderer Renderer
    {
        get => _renderer;
        set
        {
            _renderer = value;
            InvalidateVisual();
        }
    }

    TemplateRenderer _renderer = TemplateRenderer.Default;

    /// <summary>数据绑定源，如样例订单对象；<c>null</c> 时占位符原样输出。</summary>
    public object? Data
    {
        get => _data;
        set
        {
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
            _images = value;
            InvalidateVisual();
        }
    }

    IImageResolver? _images;

    /// <summary>当前预览的模板；赋值会重建内核并重新整页适配。</summary>
    public PrintTemplate Template
    {
        get => _kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _kernel.ViewChanged -= OnKernelChanged;
            _kernel = new EditorKernel(value);
            _kernel.ViewChanged += OnKernelChanged;
            _autoFitDone = false;
            InvalidateVisual();
        }
    }

    /// <summary>当前渲染缩放（DIP → 设备 px 的倍数）；未挂到窗口时为 1.0。</summary>
    public double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>最近一次绘制的降级告警；尚未绘制过时为 <c>null</c>。</summary>
    public RenderReport? LastReport { get; private set; }

    /// <summary>每完成一帧绘制后触发（在渲染线程上）。</summary>
    public event EventHandler? Rendered;

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

    void OnKernelChanged() => InvalidateVisual();

    void DrawFrame(SKCanvas canvas, Rect bounds)
    {
        var scaling = RenderScaling;
        var width = bounds.Width * scaling;
        var height = bounds.Height * scaling;
        if (width < 1 || height < 1)
            return;

        var dpi = 96.0 * scaling;

        if (!_autoFitDone)
        {
            _autoFitDone = true;
            _kernel.Viewport.FitToPage(Template.Paper.PageWidthMm, _kernel.PageHeightMm, width, height, dpi);
        }

        var save = canvas.Save();
        canvas.Scale(1f / (float)scaling);
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
}
