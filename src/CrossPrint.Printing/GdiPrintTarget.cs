using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Engine.Rendering;
using SkiaSharp;

// 本文件所有 GDI 入口（Run/InstalledPrinterNames/DefaultPrinterName）第一行都有运行时平台守卫，
// 非 Windows 折成 Failed/空值——CA1416 的守卫识别对"取反 + 提前返回"模式不可靠，这里文件级压掉。
#pragma warning disable CA1416

namespace CrossPrint.Printing;

/// <summary>
/// GDI 直打目标的选项：渲染与假脱机参数。份数不在这里——份数是作业内容
/// （<see cref="PrintDocument.Copies"/>，与 <see cref="PdfFilePrintTarget"/> 同口径摊平）。
/// </summary>
public sealed record GdiPrintOptions
{
    /// <summary>
    /// 目标打印机名（Windows 假脱机队列名，区分大小写）。null/空白 = 系统**默认打印机**——
    /// 机器上连着的打印机只要设成默认队列就直接出纸，全程走 Windows 打印管线，
    /// 不依赖 PDF 阅读器等任何第三方软件。
    /// 可用 <see cref="GdiPrintTarget.InstalledPrinterNames"/> 枚举本机队列供宿主做选择 UI。
    /// </summary>
    public string? PrinterName { get; init; }

    /// <summary>
    /// 光栅 DPI：页面先渲染成位图再交给 GDI。默认 203——热敏打印头的物理精度（面单/标签场景），
    /// 也是内存的护栏：A4@203 ≈ 14MB（单页峰值 = Skia 位图 + GDI 位图各一份，约 29MB），
    /// A4@300 则要 35MB/页。小票（80mm 宽）在 300 DPI 也只有 ~2.7MB/页，可以调高。
    /// </summary>
    public double Dpi { get; init; } = 203;

    /// <summary>渲染选项，透传引擎；<see cref="GdiPrintOptions.Dpi"/> 会覆盖其 <c>Dpi</c>。</summary>
    public RenderOptions? Options { get; init; }

    /// <summary>字体解析器，透传引擎；null 用引擎默认。</summary>
    public IFontResolver? FontResolver { get; init; }

    /// <summary>图片解析器，透传引擎；null 只有模板内嵌图可用。</summary>
    public IImageResolver? Images { get; init; }

    /// <summary>渲染器实例，透传引擎；null 用 <see cref="TemplateRenderer.Default"/>。</summary>
    public TemplateRenderer? Renderer { get; init; }
}

/// <summary>
/// GDI 直打目标：页面逐页光栅化后经 <see cref="PrintDocument"/> 直接送进 Windows 打印假脱机
/// （FR-PRT-03 的补线）。与 <see cref="SystemPrintTarget"/> 的 ShellExecute 路线互补——
/// 动词路线依赖"系统里装了 PDF 关联应用"，没装（SE_ERR_NOASSOC）就出不了纸；
/// 本目标**零第三方依赖**：位图经 GDI 直接交给打印机队列，有打印驱动就能出纸。
/// </summary>
/// <remarks>
/// <para>
/// <b>内存纪律</b>：同一时刻只活一页——PrintPage 事件里现渲现画现释放，
/// 峰值 = 一页 Skia 位图 + 一页 GDI 位图（约 2 × <see cref="GdiPrintOptions.Dpi"/> 下的单页像素），
/// 与总页数无关。批量 10 万页也吃不出一页以外的东西。
/// </para>
/// <para>
/// <b>分页口径</b>：与 <see cref="BatchPdfExporter"/> 完全一致——
/// 每份文档走 <see cref="TemplatePaginator.PlanLayout"/> 规划（表格续页/长文本续页/页码语义），
/// 逐页渲染模板变体。<see cref="PrintResult.PrintedPages"/> 是交给假脱机的页数。
/// </para>
/// <para>
/// <b>失败不抛</b>：与其他目标同一契约——异常折进 <see cref="PrintResult.Failed(string, Exception?)"/>，
/// 只有 <see cref="OutOfMemoryException"/> 上抛。非 Windows 平台折成失败结果。
/// </para>
/// <para>
/// <b>取消</b>：<see cref="PrintDocument"/> 的打印循环没有协作式取消点，
/// 在每页 PrintPage 前检查令牌，取消即 <see cref="PrintPageEventArgs.Cancel"/> 结束作业（已送假脱机的页不回收）。
/// </para>
/// </remarks>
public sealed class GdiPrintTarget : IPrintTarget
{
    /// <inheritdoc />
    public string Name => "gdi";

    readonly TemplateRenderer? _rendererForTest;

    /// <summary>创建 GDI 直打目标。</summary>
    public GdiPrintTarget()
    {
    }

    /// <summary>测试注入：指定渲染器（其余通过 <see cref="GdiPrintOptions"/> 传）。</summary>
    internal GdiPrintTarget(TemplateRenderer renderer)
    {
        _rendererForTest = renderer;
    }

    /// <summary>本机安装的打印机队列名（给宿主做打印机选择 UI 用）。非 Windows 返回空列表。</summary>
    public static IReadOnlyList<string> InstalledPrinterNames()
    {
#if NET461
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return Array.Empty<string>();
#else
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();
#endif

        try
        {
            var names = new List<string>();
            foreach (var name in PrinterSettings.InstalledPrinters)
                names.Add((string)name);
            return names;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 打印服务没起（spooler 停止）等：不给名单，让调用方走失败路径拿到原因
            return Array.Empty<string>();
        }
    }

    /// <summary>系统默认打印机名；没有（打印服务异常）返回 null。</summary>
    public static string? DefaultPrinterName()
    {
#if NET461
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return null;
#else
        if (!OperatingSystem.IsWindows())
            return null;
#endif

        try
        {
            var settings = new PrinterSettings();
            var name = settings.PrinterName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="IPrintTarget"/> 是文件型目标的契约；GDI 直打从 settings 只取渲染透传字段
    /// （<see cref="PrintSettings.OutputPath"/> 与打印无关，被忽略）。
    /// 指定打印机/光栅 DPI 走 <see cref="GdiPrintOptions"/> 重载。
    /// </remarks>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        PrintSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        return PrintAsync(document, FromSettings(settings), cancellationToken);
    }

    /// <summary>打印单文档（推荐入口：能指定打印机与光栅 DPI）。</summary>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        GdiPrintOptions? options = null,
        CancellationToken cancellationToken = default)
        => PrintBatchAsync(new[] { document }, options, null, cancellationToken);

    /// <summary>批量打印：一个假脱机作业，页序 = 文档序 ×（分页计划页序）。每份文档按 <see cref="PrintDocument.Copies"/> 连续出同内容页。</summary>
    public Task<PrintResult> PrintBatchAsync(
        IReadOnlyList<PrintDocument> documents,
        GdiPrintOptions? options = null,
        RenderReport? issues = null,
        CancellationToken cancellationToken = default)
    {
        if (documents is null) throw new ArgumentNullException(nameof(documents));
        return Task.Run(() => Run(documents, options ?? new GdiPrintOptions(), issues, cancellationToken));
    }

    /// <inheritdoc cref="PrintBatchAsync(IReadOnlyList{PrintDocument}, GdiPrintOptions?, RenderReport?, CancellationToken)"/>
    PrintResult Run(
        IReadOnlyList<PrintDocument> documents,
        GdiPrintOptions options,
        RenderReport? issues,
        CancellationToken cancellationToken)
    {
        try
        {
#if NET461
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("GDI 直打仅支持 Windows；macOS/Linux 请用 SystemPrintTarget（lp）或 PdfFilePrintTarget。");
#else
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("GDI 直打仅支持 Windows；macOS/Linux 请用 SystemPrintTarget（lp）或 PdfFilePrintTarget。");
#endif

            cancellationToken.ThrowIfCancellationRequested();

            var (pages, expandFailure) = PdfFilePrintTarget.ExpandPages(documents);
            if (pages is null)
                return PrintResult.Failed(expandFailure ?? "批次不可打印。");

            var opt = options.Options?.WithDpi(options.Dpi) ?? new RenderOptions().WithDpi(options.Dpi);
            var renderer = _rendererForTest ?? options.Renderer ?? TemplateRenderer.Default;

            var job = new SpoolJob(pages, opt, renderer, options.FontResolver, options.Images, cancellationToken);
            using var printer = new System.Drawing.Printing.PrintDocument();

            var printerName = string.IsNullOrWhiteSpace(options.PrinterName)
                ? DefaultPrinterName()
                : options.PrinterName;
            if (string.IsNullOrWhiteSpace(printerName))
                return PrintResult.Failed("没有可用的打印机：系统默认打印机为空（打印服务可能没起）。可用 GdiPrintOptions.PrinterName 指定队列。");
            printer.PrinterSettings.PrinterName = printerName;

            // 静默控制器：不出 .NET 自带的进度对话框，作业直接进队列
            printer.PrintController = new StandardPrintController();
            printer.BeginPrint += (_, _) => job.OnBegin(printer);
            printer.QueryPageSettings += (_, e) => job.OnQueryPageSettings(e);
            printer.PrintPage += (_, e) => job.OnPrintPage(e, printer, opt, renderer, options);
            printer.EndPrint += (_, _) => { /* 无状态需要收尾 */ };

            try
            {
                printer.Print();
            }
            catch (InvalidPrinterException ex)
            {
                return PrintResult.Failed($"打印机 '{printerName}' 不可用（InvalidPrinterException）：确认队列存在且打印机在线。", ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return PrintResult.Failed($"GDI 打印作业失败：{ex.GetType().Name}: {ex.Message}", ex);
            }

            if (job.Canceled)
                return PrintResult.Failed($"作业已取消：已送假脱机 {job.PagesSpooled} 页，剩余页未打印。", job.CancelException);

            // 渲染期告警合并交回（与 PdfFilePrintTarget 同口径：结果对象只带摘要，明细走 issues）
            issues?.AddRange(job.Report);
            return PrintResult.Ok(job.PagesSpooled, Summarize(job.Report));
        }
        catch (OperationCanceledException ex)
        {
            return PrintResult.Failed("作业已取消，GDI 打印未发起。", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PrintResult.Failed($"GDI 直打失败：{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 一个假脱机作业的页游标：把「文档序 ×（分页计划页序）」摊平成 PrintPage 事件序列。
    /// 只持游标与告警，页面位图现渲现画现释放（内存纪律见类 remarks）。
    /// </summary>
#if !NET461
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    sealed class SpoolJob
    {
        readonly IReadOnlyList<PrintDocument> _pages;
        readonly RenderOptions _options;
        readonly TemplateRenderer _renderer;
        readonly IFontResolver? _fonts;
        readonly IImageResolver? _images;
        readonly CancellationToken _cancellationToken;

        int _documentIndex;
        IReadOnlyList<TemplatePaginator.PaginationPage> _plan = Array.Empty<TemplatePaginator.PaginationPage>();
        int _planIndex;
        bool _planReady;

        /// <summary>连续纸自动增高后的工作副本（固定纸为 null，即用原模板）；纸张尺寸与渲染都用它。</summary>
        PrintTemplate? _workingTemplate;

        /// <summary>已交给假脱机的页数（PrintPage 事件里累计）。</summary>
        public int PagesSpooled { get; private set; }

        /// <summary>渲染告警汇总（每页一份报告合并）。</summary>
        public RenderReport Report { get; } = new();

        /// <summary>取消后为 true：作业被令牌掐断，页序没有走完。</summary>
        public bool Canceled { get; private set; }

        /// <summary>取消时的原始异常（用于结果对象携带原因）。</summary>
        public OperationCanceledException? CancelException { get; private set; }

        public SpoolJob(
            IReadOnlyList<PrintDocument> pages,
            RenderOptions options,
            TemplateRenderer renderer,
            IFontResolver? fonts,
            IImageResolver? images,
            CancellationToken cancellationToken)
        {
            _pages = pages;
            _options = options;
            _renderer = renderer;
            _fonts = fonts;
            _images = images;
            _cancellationToken = cancellationToken;
        }

        public void OnBegin(System.Drawing.Printing.PrintDocument printer)
        {
            // GDI 打印服务拒绝 0 页作业之前，这里已经由 ExpandPages 保证至少 1 页
        }

        /// <summary>每页的纸张设置跟当前文档走：批量里不同模板可能纸张不同。
        /// 连续纸用自动增高后的工作副本——纸盒必须比内容高，否则页尾整段被截。</summary>
        public void OnQueryPageSettings(QueryPageSettingsEventArgs e)
        {
            var page = CurrentDocument();
            CurrentPlan(page); // 确保增高工作副本已就绪（首张 QueryPageSettings 先于首个 PrintPage 触发）
            var sizeMm = TemplateRenderer.GetPageSizeMm(_workingTemplate ?? page.Template, _options);
            // PaperSize 单位是百分之一英寸；自定义纸型对不支持的驱动会被忽略（回落驱动默认纸）——
            // 这类驱动差异不属于引擎语义，不折算失败。
            var hundredthsW = (int)Math.Round(sizeMm.Width / UnitConverter.MmPerInch * 100);
            var hundredthsH = (int)Math.Round(sizeMm.Height / UnitConverter.MmPerInch * 100);
            e.PageSettings.PaperSize = new PaperSize("CrossPrint", hundredthsW, hundredthsH);
            e.PageSettings.Margins = new Margins(0, 0, 0, 0);
        }

        public void OnPrintPage(
            PrintPageEventArgs e,
            System.Drawing.Printing.PrintDocument printer,
            RenderOptions options,
            TemplateRenderer renderer,
            GdiPrintOptions gdiOptions)
        {
            // 打印循环没有取消点：每页动手前查一次令牌，取消即掐断（已进队列的页不回收）
            if (_cancellationToken.IsCancellationRequested)
            {
                Canceled = true;
                CancelException = new OperationCanceledException(_cancellationToken);
                e.HasMorePages = false;
                return;
            }

            var page = CurrentDocument();
            var plan = CurrentPlan(page);
            var payload = plan[_planIndex];

            // 现渲：一页一份 Skia 位图（RGBA Premul），画完即释放
            var report = new RenderReport();
            var tableWindows = payload.TableElementId.Length > 0
                ? new Dictionary<string, TableWindow> { [payload.TableElementId] = payload.Window }
                : null;
            var context = new RenderContext(options, report, _fonts, _images, page.Data,
                tableWindows: tableWindows,
                pageNo: payload.PageNo,
                pageTotal: payload.PageTotal);
            var leases = ImageLeaseBag.Open(context);
            SKBitmap bitmap;
            try
            {
                var size = TemplateRenderer.GetPageSizeMm(payload.Template, options);
                var width = Math.Max(1, (int)Math.Ceiling(size.Width * options.UnitsPerMm));
                var height = Math.Max(1, (int)Math.Ceiling(size.Height * options.UnitsPerMm));
                bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                try
                {
                    using var canvas = new SKCanvas(bitmap);
                    renderer.Render(payload.Template, canvas, context);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }
            finally
            {
                leases.Complete();
            }

            Report.AddRange(report);

            // 现画：Skia RGBA → GDI 32bppPArgb（同 premul，字节序 R/G/B/A → B/G/R/A 逐行搬移），
            // 画进整页（Graphics 单位 Display = 百分之一英寸，直接给纸张尺寸）
            using (bitmap)
            using (var gdi = ToGdi(bitmap))
            {
                var sizeMm = TemplateRenderer.GetPageSizeMm(payload.Template, options);
                var destW = (float)(sizeMm.Width / UnitConverter.MmPerInch * 100);
                var destH = (float)(sizeMm.Height / UnitConverter.MmPerInch * 100);
                e.Graphics?.DrawImage(gdi, 0f, 0f, destW, destH);
            }

            PagesSpooled++;
            _planIndex++;

            if (_planIndex < plan.Count)
            {
                e.HasMorePages = true;
                return;
            }

            // 当前文档计划走完：看下一份文档
            _documentIndex++;
            _planReady = false;
            _plan = Array.Empty<TemplatePaginator.PaginationPage>();
            _planIndex = 0;
            e.HasMorePages = _documentIndex < _pages.Count;
        }

        PrintDocument CurrentDocument() => _pages[Math.Min(_documentIndex, _pages.Count - 1)];

        IReadOnlyList<TemplatePaginator.PaginationPage> CurrentPlan(PrintDocument page)
        {
            if (!_planReady)
            {
                // 连续纸先自动增高再规划：QueryPageSettings 的纸张尺寸与渲染都用增高后的工作副本，
                // 否则打印机纸盒按原模板算，纸比内容短，页尾整段被截。
                var measureContext = new RenderContext(_options, new RenderReport(), _fonts, _images, page.Data);
                _workingTemplate = TemplateRenderer.AutoGrowContinuousTables(page.Template, measureContext);
                _plan = TemplatePaginator.PlanLayout(_workingTemplate, _options, page.Data, _fonts, _images);
                _planIndex = 0;
                _planReady = true;
            }

            return _plan;
        }

        /// <summary>
        /// Skia RGBA8888(Premul) → GDI 32bppPArgb：同为预乘 alpha，仅字节序 R/G/B/A ↔ B/G/R/A 交换。
        /// 逐行 LockBits 写入，无 PNG 编码等中间放大——这是内存纪律的另一半。
        /// </summary>
        static Bitmap ToGdi(SKBitmap sk)
        {
            var gdi = new Bitmap(sk.Width, sk.Height, PixelFormat.Format32bppPArgb);
            var bounds = new Rectangle(0, 0, sk.Width, sk.Height);
            var data = gdi.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                unsafe
                {
                    var srcBase = (byte*)sk.GetPixels().ToPointer();
                    var dstBase = (byte*)data.Scan0.ToPointer();
                    var stride = data.Stride;
                    for (var y = 0; y < sk.Height; y++)
                    {
                        var src = srcBase + (long)y * sk.RowBytes;
                        var dst = dstBase + (long)y * stride;
                        for (var x = 0; x < sk.Width; x++)
                        {
                            var s = src + x * 4;
                            var d = dst + x * 4;
                            d[0] = s[2]; // B
                            d[1] = s[1]; // G
                            d[2] = s[0]; // R
                            d[3] = s[3]; // A（预乘口径一致，直拷）
                        }
                    }
                }
            }
            finally
            {
                gdi.UnlockBits(data);
            }

            return gdi;
        }
    }

    static GdiPrintOptions FromSettings(PrintSettings settings) => new()
    {
        Options = settings.Options,
        FontResolver = settings.FontResolver,
        Images = settings.Images,
        Renderer = settings.Renderer,
    };

#if NET461
    static bool IsWindowsSupported() => Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool IsWindowsSupported()
    {
        // 调用点已内联 OperatingSystem.IsWindows() 守卫（CA1416 只认内联模式），此方法仅供 net461 语义对齐
        OperatingSystem.IsWindows();
        return true;
    }
#endif

    /// <summary>把渲染告警压成一行分类摘要（与 <see cref="PdfFilePrintTarget"/> 同口径）。</summary>
    static string? Summarize(RenderReport report)
        => report.HasIssues
            ? $"产物已交给打印假脱机，但有 {report.Issues.Count} 条渲染告警："
              + string.Join("、", report.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"))
            : null;
}
