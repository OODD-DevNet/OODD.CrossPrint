using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using SkiaSharp;

namespace CrossPrint.Engine;

/// <summary>位图导出器。预览与"导出图片"共用。</summary>
public static class PngExporter
{
    /// <summary>PNG 字节与渲染告警。</summary>
    /// <param name="Bytes">PNG 内容。</param>
    /// <param name="Report">告警汇总。</param>
    public sealed record PngResult(byte[] Bytes, RenderReport Report);

    /// <summary>导出 PNG 到流。</summary>
    /// <param name="template">模板。</param>
    /// <param name="stream">目标流；本方法不关闭它。</param>
    /// <param name="options">渲染选项；<see cref="RenderOptions.Dpi"/> 决定像素密度，默认 96。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="renderer">渲染器实例；null 用 <see cref="TemplateRenderer.Default"/>。</param>
    /// <returns>告警汇总。</returns>
    public static RenderReport Export(
        PrintTemplate template,
        Stream stream,
        RenderOptions? options = null,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = ExportBytes(template, options, data, fontResolver, images, renderer);
        stream.Write(result.Bytes);
        return result.Report;
    }

    /// <summary>导出 PNG 字节。</summary>
    /// <param name="template">模板。</param>
    /// <param name="options">渲染选项。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="renderer">渲染器实例。</param>
    /// <returns>PNG 字节与告警。</returns>
    public static PngResult ExportBytes(
        PrintTemplate template,
        RenderOptions? options = null,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var r = renderer ?? TemplateRenderer.Default;
        var result = r.RenderToBitmap(template, options, data, fontResolver, images);
        try
        {
            using var image = SKImage.FromBitmap(result.Bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return new PngResult(encoded.ToArray(), result.Report);
        }
        finally
        {
            result.Bitmap.Dispose();
        }
    }
}

/// <summary>
/// PDF 导出器。页面尺寸按 <c>mm / 25.4 × 72</c> 建模，即 1 用户单位 = 1 PostScript 点，
/// 因此传给渲染器的 <see cref="RenderOptions.Dpi"/> 恒为 72；
/// <c>BeginPage</c> 之后 <see cref="TemplateRenderer"/> 再乘 <c>72/25.4</c> 回到 mm 作图。
/// </summary>
/// <remarks>
/// 实测（探针 §8/§12，SkiaSharp 2.88.9）：
/// <list type="bullet">
/// <item><description>文本走 <c>BT/Tf/Tj</c> + <c>/Type0</c> + <c>/FontFile2</c>，是矢量且内嵌字体，不退化成位图；</description></item>
/// <item><description><c>Metadata.RasterDpi</c> 不重采样内嵌位图，只把 MediaBox 量化到 <c>round(mm×dpi/25.4)×72/dpi</c> 网格，
/// 故默认取 2540（0.01mm 网格），A4 宽度误差 1e-5mm；</description></item>
/// <item><description>Skia 2.88.9 不做字体子集化，仅 5 个拉丁字符就内嵌整份字体（Segoe UI 506KB / 微软雅黑 7.5MB），
/// 需要小体积归档时把 <see cref="RenderOptions.PdfTextAsOutlines"/> 置 true 改画轮廓。</description></item>
/// </list>
/// </remarks>
public static class PdfExporter
{
    /// <summary>PDF 的画布单位密度：1 单位 = 1 点。</summary>
    public const double PdfUnitsPerInch = 72;

    /// <summary>导出单页 PDF 到流。</summary>
    /// <param name="template">模板。</param>
    /// <param name="stream">目标流；本方法不关闭它。</param>
    /// <param name="options">渲染选项；<see cref="RenderOptions.Dpi"/> 由本方法强制为 72。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="renderer">渲染器实例。</param>
    /// <returns>告警汇总。</returns>
    public static RenderReport Export(
        PrintTemplate template,
        Stream stream,
        RenderOptions? options = null,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(stream);

        var opt = (options ?? new RenderOptions()).WithDpi(PdfUnitsPerInch);
        var r = renderer ?? TemplateRenderer.Default;
        var report = new RenderReport();
        var size = TemplateRenderer.GetPageSizeMm(template, opt);

        var context = new RenderContext(opt, report, fontResolver, images, data);
        // 图片租约要延到页面录制真正被读走之后才还：DrawImage 只是记进页面，读像素是关闭文档时。
        // 峰值仍是"一页取用的图"，不会随页数涨。
        var leases = ImageLeaseBag.Open(context);
        try
        {
            using (var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { RasterDpi = (float)opt.PdfRasterDpi }))
            {
                var page = doc.BeginPage((float)UnitConverter.MmToPt(size.Width), (float)UnitConverter.MmToPt(size.Height));
                try
                {
                    r.Render(template, page, context);
                }
                finally
                {
                    page.Dispose();
                }
            }
        }
        finally
        {
            leases.Complete();
        }

        return report;
    }
}

/// <summary>
/// 批量导出：多张模板（或同一模板配不同数据）合成**一个**多页 PDF。
/// 每一页用各自模板的纸张尺寸，混排 A4 与 100×150 面单也在一份文件里。
/// <see cref="RenderOptions.AllowTablePagination"/> 打开时，内容超高的表格会把所属模板撑成多页
/// （见 <see cref="TemplatePaginator"/>），每页带窗口与页码重渲染一遍。
/// </summary>
public static class BatchPdfExporter
{
    /// <summary>导出多页 PDF。</summary>
    /// <param name="templates">模板列表，顺序即页序。</param>
    /// <param name="stream">目标流；本方法不关闭它。</param>
    /// <param name="options">渲染选项。</param>
    /// <param name="dataSelector">按页序取每页数据；null 表示全部不绑定。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="renderer">渲染器实例。</param>
    /// <returns>合并后的告警汇总（每条带页序前缀无法定位元素时才有意义，这里靠元素 id 定位）。</returns>
    public static RenderReport Export(
        IReadOnlyList<PrintTemplate> templates,
        Stream stream,
        RenderOptions? options = null,
        Func<int, object?>? dataSelector = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
        => ExportPages(templates, stream, options, dataSelector, fontResolver, images, renderer).Report;

    /// <summary>
    /// 导出多页 PDF 并回传**真实页数**：表格自动续页后页数可能大于模板条数，
    /// 调用方（如 PdfFilePrintTarget 打印目标）报"印了几页"必须用这个数，不许拿模板数自证。
    /// </summary>
    /// <param name="templates">模板列表，顺序即页序。</param>
    /// <param name="stream">目标流；本方法不关闭它。</param>
    /// <param name="options">渲染选项。</param>
    /// <param name="dataSelector">按模板序取每份数据；null 表示全部不绑定。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="renderer">渲染器实例。</param>
    /// <returns>合并告警与总页数（≥ 1）。</returns>
    public static (RenderReport Report, int PageCount) ExportPages(
        IReadOnlyList<PrintTemplate> templates,
        Stream stream,
        RenderOptions? options = null,
        Func<int, object?>? dataSelector = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(stream);

        // 返回契约写的是"总页数 ≥ 1"：空列表会让 Skia 收一份 0 页文档，阅读器/打印机多半直接拒收，
        // 而调用方只会看到"印了 0 页"再来猜原因。在写流之前就拒，流里不会留半截文件。
        if (templates.Count == 0)
            throw new ArgumentException("模板列表为空：批量 PDF 至少需要一页，本方法不产出 0 页文件。", nameof(templates));

        var opt = (options ?? new RenderOptions()).WithDpi(PdfExporter.PdfUnitsPerInch);
        var r = renderer ?? TemplateRenderer.Default;
        var merged = new RenderReport();
        var totalPages = 0;

        using (var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { RasterDpi = (float)opt.PdfRasterDpi }))
        {
            for (var i = 0; i < templates.Count; i++)
            {
                var template = templates[i];
                ArgumentNullException.ThrowIfNull(template);

                var data = dataSelector?.Invoke(i);
                var pages = TemplatePaginator.PlanLayout(template, opt, data, fontResolver, images);
                var size = TemplateRenderer.GetPageSizeMm(template, opt);

                for (var p = 0; p < pages.Count; p++)
                {
                    var page = doc.BeginPage((float)UnitConverter.MmToPt(size.Width), (float)UnitConverter.MmToPt(size.Height));
                    // 一页一个租约袋：页面录制结束才还图，同时把峰值钉在"一页取用的图"上，不随总页数涨
                    ImageLeaseBag? leases = null;
                    try
                    {
                        var report = new RenderReport();
                        var plan = pages[p];
                        // 按计划自身的语义注入窗口与页码：1 页也可能是分页计划
                        // （内容超元素高但首页撑满后装得下，表格撑满、落款让位）。
                        var context = plan.TableElementId.Length > 0
                            ? new RenderContext(opt, report, fontResolver, images, data,
                                tableWindows: new Dictionary<string, TableWindow> { [plan.TableElementId] = plan.Window },
                                pageNo: plan.PageNo,
                                pageTotal: plan.PageTotal)
                            : new RenderContext(opt, report, fontResolver, images, data);
                        leases = ImageLeaseBag.Open(context);
                        r.Render(plan.Template, page, context);
                        merged.AddRange(report);
                        totalPages++;
                    }
                    finally
                    {
                        page.Dispose();
                        leases?.Complete();
                    }
                }
            }
        }

        return (merged, totalPages);
    }
}
