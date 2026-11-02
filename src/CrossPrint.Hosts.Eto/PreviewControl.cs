using System.Runtime.InteropServices;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Engine;
using Eto.Drawing;
using Eto.Forms;
using SkiaSharp;

namespace CrossPrint.Hosts.Eto;

/// <summary>
/// Eto.Forms 打印预览画布：与 <see cref="DesignerControl"/> 共用 <see cref="EditorCanvas.Draw"/> 渲染路径，
/// 但叠加层只保留纸张与页框（无网格/标尺/安全区），且不接任何输入——所见即导出所得。
/// </summary>
/// <remarks>
/// DPI 口径与设计画布完全一致（<see cref="Screen.DPI"/> 或 <see cref="DpiOverride"/>），
/// 因此同一模板在两边的毫米落点重合。
/// </remarks>
public sealed class PreviewControl : Drawable
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
    TemplateRenderer _renderer = TemplateRenderer.Default;
    object? _data;
    IFontResolver? _fontResolver;
    IImageResolver? _images;

    Bitmap? _frame;
    int _frameWidth;
    int _frameHeight;

    bool _autoFitted;
    double _lastSurfaceWidth;
    double _lastSurfaceHeight;

    double _dpi;
    double? _dpiOverride;

    /// <summary>创建预览画布。</summary>
    /// <param name="template">初始模板；<c>null</c> 时使用默认 A4 纵向空模板。</param>
    public PreviewControl(PrintTemplate? template = null)
    {
        _kernel = new EditorKernel(template ?? PrintTemplate.CreateDefault(PaperTypePresets.A4));
        _dpi = DetectScreenDpi();
        Attach(_kernel);

        Paint += OnPaint;
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
        get => Kernel.Template;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, Kernel.Template))
                return;

            Detach(_kernel);
            _kernel = new EditorKernel(value);
            Attach(_kernel);

            _autoFitted = false;
            FitToPage();
            Invalidate();
        }
    }

    /// <summary>
    /// 宿主逻辑 DPI：默认取 <see cref="Screen.PrimaryScreen"/> 的 <see cref="Screen.DPI"/>（96 = 100%）。
    /// </summary>
    /// <remarks>
    /// <see cref="DpiOverride"/> 非 <c>null</c> 时以覆盖值为准；此时给本属性赋值只更新"屏幕基准值"。
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

    /// <summary>DPI 覆盖值；<c>null</c> 表示跟随 <see cref="Dpi"/> 的屏幕基准值。</summary>
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

    /// <summary>每次 <see cref="Drawable.Paint"/> 绘制完成后触发。</summary>
    public event EventHandler? Rendered;

    /// <summary>替换当前模板：旧内核解绑、新内核重新绑定事件，并按当前视口尺寸整页适配一次。</summary>
    /// <param name="template">新模板；内核直接编辑这个实例。</param>
    public void LoadTemplate(PrintTemplate template) => Template = template;

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
        kernel.ViewChanged += OnKernelViewChanged;
        kernel.TemplateChanged += OnKernelViewChanged;
    }

    void Detach(EditorKernel kernel)
    {
        kernel.ViewChanged -= OnKernelViewChanged;
        kernel.TemplateChanged -= OnKernelViewChanged;
    }

    void OnKernelViewChanged() => Invalidate();

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
}
