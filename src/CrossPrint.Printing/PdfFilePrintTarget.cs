using CrossPrint.Core;
using CrossPrint.Engine;

namespace CrossPrint.Printing;

/// <summary>
/// 打印到 PDF 文件：FR-PRT-01（v0.1 的"PDF 导出即打印"）实现。单文档写一份文件；
/// 批量作业（含份数展开）写成**一份**多页文件，字体等共享对象只嵌一次。
/// </summary>
/// <remarks>
/// 三条纪律，都是踩过坑定的：
/// <list type="number">
/// <item><description>
/// <b>临时名 + 改名</b>：先在目标目录写 <c>{名}.crossprint-{guid}.tmp{扩展名}</c>，整份导出成功并
/// <c>Flush(true)</c> 之后才 <c>File.Move(临时名, 正式名, true)</c>。直接写正式名的后果是：
/// 导出到第 37 页抛异常时，正式路径上留着一份"看上去是完整文件"的半截 PDF，
/// 归档系统会当成功件收走，现场要等客户投诉才发现重印。改名是同目录内的目录项替换，
/// 读者要么看到旧件要么看到新件，绝不会看到半截。临时名必须与正式名同目录：
/// 跨卷 <c>Move</c> 不是改名而是复制，复制就又回到"半截文件"问题。
/// </description></item>
/// <item><description>
/// <b>失败不抛</b>：IO 与引擎异常一律折进 <see cref="PrintResult.Failed(string, Exception?)"/>（接口契约）。
/// 唯一例外是进程级致命异常（<see cref="OutOfMemoryException"/>）——兜住它等于骗人，让它原样上抛。
/// </description></item>
/// <item><description>
/// <b>取消只在写盘前生效</b>：Skia 的 PDF 导出没有协作式取消点，<c>BeginPage</c> 之后只能整份写完。
/// 所以取消检查放在展开页序、建目录、建临时文件之前，一共两次（展开后、渲染前）。
/// 取消的作业返回 <c>Failed</c> 并把 <see cref="OperationCanceledException"/> 登记进
/// <see cref="PrintResult.Error"/>，**不**把它抛出 <c>await</c>：调用方拿到的永远是结果对象，
/// 不是需要 try/catch 的异常——批量循环里一个取消不该把剩下的作业全打断。
/// </description></item>
/// </list>
/// </remarks>
public sealed class PdfFilePrintTarget : IPrintTarget
{
    /// <inheritdoc />
    public string Name => "pdf-file";

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">document 或 settings 为 null。</exception>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        PrintSettings settings,
        CancellationToken cancellationToken = default)
        => PrintAsync(document, settings, null, cancellationToken);

    /// <summary>打印单文档，并把渲染告警明细交给调用方（NFR-07：结构化降级列表）。</summary>
    /// <param name="document">待打印文档。</param>
    /// <param name="settings">输出设置。</param>
    /// <param name="issues">告警收集器；null 表示只要 <see cref="PrintResult.Message"/> 里的分类摘要。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>作业结果。</returns>
    /// <exception cref="ArgumentNullException">document 或 settings 为 null。</exception>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        PrintSettings settings,
        RenderReport? issues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PrintBatchAsync(new[] { document }, settings, issues, cancellationToken);
    }

    /// <summary>批量打印：一个作业、一份多页 PDF。每份文档按 <see cref="PrintDocument.Copies"/> 连续出同内容页。</summary>
    /// <param name="documents">文档列表，顺序即页序（100 条订单 → 100 页）。</param>
    /// <param name="settings">输出设置；<see cref="PrintSettings.OutputPath"/> 就是那份多页文件的路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>作业结果。</returns>
    /// <exception cref="ArgumentNullException">documents 或 settings 为 null。</exception>
    public Task<PrintResult> PrintBatchAsync(
        IReadOnlyList<PrintDocument> documents,
        PrintSettings settings,
        CancellationToken cancellationToken = default)
        => PrintBatchAsync(documents, settings, null, cancellationToken);

    /// <summary>批量打印并交出告警明细。<c>issues</c> 重载是把 <see cref="RenderReport"/> 递到宿主手里的唯一入口：
    /// <see cref="PrintResult"/> 只带分类摘要，不带每条元素级记录。</summary>
    /// <param name="documents">文档列表，顺序即页序。</param>
    /// <param name="settings">输出设置。</param>
    /// <param name="issues">告警收集器；null 表示不要明细。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>作业结果。</returns>
    /// <exception cref="ArgumentNullException">documents 或 settings 为 null。</exception>
    public Task<PrintResult> PrintBatchAsync(
        IReadOnlyList<PrintDocument> documents,
        PrintSettings settings,
        RenderReport? issues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(settings);

        // 引擎是同步的（没有异步面），这里把 CPU + 磁盘活丢线程池，只为让签名保持 Task。
        // 不把 cancellationToken 传给 Task.Run：那样"任务尚未开始就被取消"会变成 await 抛异常，
        // 与"取消也是结果，不是异常"的契约冲突。取消检查一律在 Run 内部做。
        return Task.Run(() => Run(documents, settings, issues, cancellationToken));
    }

    /// <summary>真正的一次作业：校验 → 展开份数 → 建目录 → 写临时文件 → 改名成正式文件。</summary>
    /// <param name="documents">文档列表。</param>
    /// <param name="settings">输出设置。</param>
    /// <param name="issues">告警收集器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>作业结果；本方法不抛（进程级致命异常除外）。</returns>
    PrintResult Run(
        IReadOnlyList<PrintDocument> documents,
        PrintSettings settings,
        RenderReport? issues,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;
        string fullPath = settings.OutputPath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (pages, expandFailure) = ExpandPages(documents);
            if (pages is null)
                return PrintResult.Failed(expandFailure ?? "批次不可打印。");

            fullPath = Path.GetFullPath(settings.OutputPath);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new IOException($"输出路径 {fullPath} 落在根上，没有能放临时文件的父目录。");

            // 缺目录就地补齐：打印目标不该因为"宿主没先建目录"这种琐事报失败
            Directory.CreateDirectory(directory);

            // 进程被杀/断电留下的临时件，只在"同名作业重跑"时清：只碰 {名}.crossprint-*.tmp{扩展名}，
            // 且必须同时满足"够旧"（排除并发作业）与"能独占打开"（排除正在写的文件）。
            CleanStaleTemps(directory, fullPath);

            // 尽力而为的拒绝覆盖（没有加锁）：并发同名作业仍可能后一个盖掉前一个
            if (!settings.Overwrite && File.Exists(fullPath))
                return PrintResult.Failed($"目标文件已存在且 Overwrite=false：{fullPath}。既有文件未被触碰。");

            // 渲染前最后一次取消检查：BeginPage 之后 Skia 没有协作式取消点了
            cancellationToken.ThrowIfCancellationRequested();

            var templates = new List<PrintTemplate>(pages.Count);
            var payloads = new List<object?>(pages.Count);
            foreach (var page in pages)
            {
                templates.Add(page.Template);
                payloads.Add(page.Data);
            }

            tempPath = MakeTempPath(directory, fullPath);
            RenderReport report;
            int printedPages;
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // 一次导出 = 一个 SKDocument = 一份共享字体池；逐页 PdfExporter 会把字库嵌 N 遍。
                // ExportPages 回传真实页数：表格自动续页（FR-BND-05）后页数可能大于摊平的文档数，
                // 拿文档数自证就是虚报（见 PrintResult.PrintedPages 的口径注释）。
                (report, printedPages) = BatchPdfExporter.ExportPages(
                    templates,
                    stream,
                    settings.Options,
                    index => payloads[index],
                    settings.FontResolver,
                    settings.Images,
                    settings.Renderer);

                // Flush(true) 连元数据一起落盘，再改名；否则改名可能先于缓冲写入完成
                stream.Flush(true);
            }

            File.Move(tempPath, fullPath, true);

            // 改名成功即视为正式文件已就位：清掉句柄，finally 不再删（删了就是把产物删了）
            tempPath = null;

            issues?.AddRange(report);
            return PrintResult.Ok(printedPages, Summarize(report));
        }
        catch (OperationCanceledException ex)
        {
            DeleteQuietly(tempPath);
            return PrintResult.Failed($"作业已取消，{fullPath} 未写出。", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 半截临时文件必须跟着失败一起消失，正式路径从头到尾没被开过
            DeleteQuietly(tempPath);
            return PrintResult.Failed($"打印失败：{ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            // 双保险：catch 之外的返回路径（例如不变式在 Ok 里拦下构造）也不许留垃圾
            DeleteQuietly(tempPath);
        }
    }

    /// <summary>
    /// 把文档列表按份数摊平成页序，并做第二轮校验。
    /// <c>null</c> 文档、<c>null</c> 模板、<c>Copies &lt; 1</c> 都要在这里拦住：
    /// <see cref="PrintDocument"/> 的构造器管住了 <c>new</c>，管不住 <c>with</c> 表达式（它走复制构造器）。
    /// 空批次也算失败而不是"成功 0 页"：0 字节的 PDF 比一句报错难查得多。
    /// 系统打印目标（<see cref="SystemPrintTarget"/>）与文件目标共用同一套校验闸——两处各写一遍迟早漂移。
    /// </summary>
    /// <param name="documents">文档列表。</param>
    /// <returns>摊平后的页序（每个元素对应一页）与失败原因；两者恰有一个为 null。</returns>
    internal static (List<PrintDocument>? Pages, string? Reason) ExpandPages(IReadOnlyList<PrintDocument> documents)
    {
        var flat = new List<PrintDocument>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            if (document is null)
                return (null, $"批次第 {i + 1} 项是 null 文档。");

            if (document.Template is null)
                return (null, $"批次第 {i + 1} 项没有模板。");

            if (document.Copies < 1)
                return (null, $"批次第 {i + 1} 项份数为 {document.Copies}，必须 ≥ 1。");

            for (var copy = 0; copy < document.Copies; copy++)
                flat.Add(document);
        }

        return flat.Count == 0
            ? (null, "批次为空：没有可打印的页面。本层不产出 0 页 PDF。")
            : (flat, null);
    }

    /// <summary>陈旧临时件的最短年龄：比一次正常作业高一个数量级，长作业与并发同名作业都不会被误删。</summary>
    static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>
    /// 清掉同名输出留下的陈旧临时件（崩溃/断电残留）。只匹配 <c>{名}.crossprint-*.tmp{扩展名}</c>，
    /// 不碰别的输出名；打不开（有作业正在写）或删不掉都直接跳过，清理失败绝不影响本次作业。
    /// </summary>
    /// <param name="directory">正式文件所在目录。</param>
    /// <param name="fullPath">正式文件路径。</param>
    static void CleanStaleTemps(string directory, string fullPath)
    {
        var prefix = Path.GetFileNameWithoutExtension(fullPath) + ".crossprint-";
        var suffix = ".tmp" + Path.GetExtension(fullPath);
        var cutoff = DateTime.UtcNow - StaleTempAge;

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in candidates)
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                // 独占打开：拿不到句柄说明有作业正在写它，宁留勿删
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>临时输出路径：同目录、同扩展名、带 <c>crossprint-{guid}</c> 尾巴，保证并发作业互不撞名。</summary>
    /// <param name="directory">正式文件的父目录（临时文件必须同目录，见类 remarks 第 1 条）。</param>
    /// <param name="fullPath">正式文件路径。</param>
    /// <returns>尚未存在的临时文件路径。</returns>
    static string MakeTempPath(string directory, string fullPath)
        => Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(fullPath)}.crossprint-{Guid.NewGuid():N}.tmp{Path.GetExtension(fullPath)}");

    /// <summary>把渲染告警压成一行分类摘要（明细走 <c>issues</c> 重载）。无告警返回 null。</summary>
    /// <param name="report">引擎交回的告警汇总。</param>
    /// <returns>摘要文本或 null。</returns>
    static string? Summarize(RenderReport report)
        => report.HasIssues
            ? $"产物已写出，但有 {report.Issues.Count} 条渲染告警："
              + string.Join("、", report.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"))
            : null;

    /// <summary>删临时文件，删不掉也不作声——作业真正的失败原因不能被子清理异常盖掉。</summary>
    /// <param name="path">临时文件路径，可为 null。</param>
    static void DeleteQuietly(string? path)
    {
        if (path is null)
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
