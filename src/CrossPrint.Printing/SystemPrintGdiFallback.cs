using CrossPrint.Core;
using CrossPrint.Engine;

namespace CrossPrint.Printing;

/// <summary>
/// <see cref="SystemPrintTarget"/> 的 GDI 直打兜底（独立文件，避免宿主级差异污染主流程）。
/// 动词交付依赖系统里的 PDF 关联应用，没装（SE_ERR_NOASSOC/ASSOCINCOMPLETE）就出不了纸；
/// GDI 直打把页面光栅成位图经假脱机直连打印机队列，零第三方依赖。
/// </summary>
internal static class SystemPrintGdiFallback
{
    /// <summary>
    /// 动词交付失败后的兜底入口：默认启用、仅 Windows 动词路径
    /// （<see cref="WindowsShellPrintCommand"/>；测试注入的替身命令与 lp 路径不回落）。
    /// pages 已摊平份数直接复用；GDI 成功 = 作业已进队列，临时 PDF 交给调用方 finally 删除。
    /// </summary>
    public static PrintResult FailWithGdiFallback(
        ISystemPrintCommand command,
        SystemPrintOptions options,
        RenderReport? issues,
        CancellationToken cancellationToken,
        List<PrintDocument> pages,
        string failure)
    {
        if (command is not WindowsShellPrintCommand || !options.GdiFallback)
            return PrintResult.Failed($"系统打印交付失败：{failure}");

        var gdiResult = new GdiPrintTarget()
            .PrintBatchAsync(pages, ToGdiOptions(options), issues, cancellationToken)
            .GetAwaiter().GetResult();
        if (gdiResult.Success)
            return gdiResult;

        return PrintResult.Failed(
            $"系统打印交付失败：{failure}；已尝试 GDI 直打兜底仍未成功：{gdiResult.Message}", gdiResult.Error);
    }

    static GdiPrintOptions ToGdiOptions(SystemPrintOptions options) => new()
    {
        PrinterName = options.PrinterName,
        Options = options.Options,
        FontResolver = options.FontResolver,
        Images = options.Images,
        Renderer = options.Renderer,
    };
}
