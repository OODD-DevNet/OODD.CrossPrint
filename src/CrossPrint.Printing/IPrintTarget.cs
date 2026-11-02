namespace CrossPrint.Printing;

/// <summary>
/// 打印目标抽象：任何"把渲染结果送去某处"都收口到这里（PDF 文件、打印机、云打印网关）。
/// </summary>
/// <remarks>
/// v0.1 接口冻结（docs/02-architecture.md §6）。v0.2 的 Windows GDI 直发 Spooler、命令行 lp
/// 目标各自新增实现，本接口的形状不许改。
/// <para>
/// 契约：<see cref="PrintAsync"/> 永不把作业失败变成异常——失败一律折成
/// <see cref="PrintResult.Failed(string, Exception?)"/> 返回，真异常躺在 <c>Error</c> 里。
/// 只有调用方自己的编程错误（传 null）才抛。宿主循环打印 100 单时不必逐次 try/catch，
/// 也不会因为第 37 单写盘失败就让整批崩在半路。
/// </para>
/// <para>
/// 批量作业（100 条数据 → 100 页一份文件）走具体实现的批量入口，见
/// <see cref="PdfFilePrintTarget.PrintBatchAsync(IReadOnlyList{PrintDocument}, PrintSettings, CrossPrint.Core.RenderReport, CancellationToken)"/>。
/// 冻结接口只承诺单文档语义：把批量塞进接口，等于提前替 v0.2 的打印机目标定它未必能兑现的契约。
/// </para>
/// </remarks>
public interface IPrintTarget
{
    /// <summary>目标标识，用于宿主的选择列表与日志（如 <c>"pdf-file"</c>）。不是本地化显示名。</summary>
    string Name { get; }

    /// <summary>打印一个文档。</summary>
    /// <param name="document">要打印的文档（模板 + 数据 + 份数）。</param>
    /// <param name="settings">输出设置。</param>
    /// <param name="cancellationToken">取消令牌。实现只在昂贵工作开始前检查它，见 <see cref="PdfFilePrintTarget"/> 的 remarks。</param>
    /// <returns>作业结果。失败时 <see cref="PrintResult.Success"/> 为 false，异常在 <see cref="PrintResult.Error"/> 里。</returns>
    /// <exception cref="ArgumentNullException">document 或 settings 为 null（调用方的编程错误，不折进结果）。</exception>
    Task<PrintResult> PrintAsync(
        PrintDocument document,
        PrintSettings settings,
        CancellationToken cancellationToken = default);
}
