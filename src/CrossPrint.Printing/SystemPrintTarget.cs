using System.Diagnostics;
using System.Runtime.InteropServices;
using CrossPrint.Core;
using CrossPrint.Engine;

namespace CrossPrint.Printing;

/// <summary>
/// 系统打印目标的选项。本组件只负责排版渲染；"把产物送进系统打印管线"是各操作系统 API 的职责：
/// Windows 走 ShellExecute 的 <c>printto</c>/<c>print</c> 动词（交给关联的 PDF 应用执行打印），
/// macOS/Linux 走 CUPS 的 <c>lp</c> 子进程。本记录只收这些通道真正读得走的字段。
/// </summary>
/// <remarks>
/// 份数不在这里：份数是作业内容（<see cref="PrintDocument.Copies"/>），
/// 交付层把它摊平成重复页（与 <see cref="PdfFilePrintTarget"/> 同口径），不伪造各通道没有的"份数"参数。
/// </remarks>
public sealed record SystemPrintOptions
{
    /// <summary>
    /// 目标打印机名。
    /// Windows：给了名字走 <c>printto</c> 动词直接指到该打印机；不给走 <c>print</c> 动词
    /// 交给关联应用（多数应用会弹出打印确认，让用户自己选）。
    /// macOS/Linux：<c>lp -d</c> 指定队列；不给交给 CUPS 默认队列。
    /// </summary>
    public string? PrinterName { get; init; }

    /// <summary>lp 子进程的等待上限（秒），超时按失败折算并终止进程。Windows 的动词交付没有等待语义，此字段只约束 lp。</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>渲染选项，透传引擎；中间 PDF 以 72 DPI（1 单位 = 1 点）生成。</summary>
    public RenderOptions? Options { get; init; }

    /// <summary>字体解析器，透传引擎；null 用引擎默认。</summary>
    public IFontResolver? FontResolver { get; init; }

    /// <summary>图片解析器，透传引擎；null 只有模板内嵌图可用。</summary>
    public IImageResolver? Images { get; init; }

    /// <summary>渲染器实例，透传引擎；null 用 <see cref="TemplateRenderer.Default"/>。</summary>
    public TemplateRenderer? Renderer { get; init; }
}

/// <summary>
/// 调起系统打印的交付动作（平台实现，进程里固定一个）。抽成接口是为了测试可以注入替身——
/// 真发打印作业的测试没法在任何 CI 上稳定复现。
/// </summary>
internal interface ISystemPrintCommand
{
    /// <summary>
    /// 交付受理后，系统侧是否还可能**延迟**读 PDF 文件。
    /// true 时调用方交付成功后不得删临时件（否则就是和关联应用抢跑：
    /// 实测 WPS 被 <c>print</c> 动词拉起后要过一会儿才打开文件，抢先删除只会让它报「不是一个有效的文件」），
    /// 文件留给下次作业的陈旧清扫；false（lp 同步假脱机，退出码 0 即文件已进队列）可立即删。
    /// 失败路径不受此字段影响：命令没受理，文件没人接手，调用方一律立即删。
    /// </summary>
    bool MayReadFileAfterReturn { get; }

    /// <summary>执行交付。返回 null 表示系统已受理；非 null 是失败原因文本（尽量带上系统给出的线索）。</summary>
    string? Invoke(string pdfPath, string? printerName, int timeoutSeconds);
}

/// <summary>
/// Windows：ShellExecute 动词交付。<c>printto</c>（指定打印机）与 <c>print</c>（交给关联应用，常弹确认）。
/// 动词交付是**异步**的：函数一返回只代表关联应用已受理，实际出纸要靠应用自己完成。
/// </summary>
internal sealed class WindowsShellPrintCommand : ISystemPrintCommand
{
    /// <summary>共享实例。</summary>
    public static WindowsShellPrintCommand Instance { get; } = new();

    const int SwHide = 0;
    const int SwShowNormal = 1;

    /// <inheritdoc />
    public bool MayReadFileAfterReturn => true;

    /// <inheritdoc />
    public string? Invoke(string pdfPath, string? printerName, int timeoutSeconds)
    {
        // timeoutSeconds 只约束 lp 子进程；ShellExecute 没有可等待的完成点，这里不使用
        var hasPrinter = !string.IsNullOrWhiteSpace(printerName);
        var verb = hasPrinter ? "printto" : "print";
        var result = ShellExecute(IntPtr.Zero, verb, pdfPath, hasPrinter ? $"\"{printerName}\"" : null, null, hasPrinter ? SwHide : SwShowNormal);
        var code = result.ToInt64();
        if (code > 32)
            return null;

        return code switch
        {
            2 or 3 => "系统找不到刚写出的 PDF 文件（SE_ERR_FNF/PNF）——磁盘或杀软拦截了临时文件。",
            5 => "访问被拒绝（SE_ERR_ACCESSDENIED）：检查打印机名与关联应用的权限。",
            8 or 0 => "内存不足，系统拒绝了打印交付（SE_ERR_OOM）。",
            27 => "PDF 关联信息不完整（SE_ERR_ASSOCINCOMPLETE）：关联应用装坏了，重装或改用 PdfFilePrintTarget 落盘。",
            31 => "系统没有能执行打印的 PDF 关联应用（SE_ERR_NOASSOC）：请安装 PDF 阅读器，或改用 PdfFilePrintTarget 落盘。",
            _ => $"ShellExecute 动词 '{verb}' 交付失败（SE_ERR={code}）。",
        };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ShellExecute(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int showCmd);
}

/// <summary>
/// macOS/Linux：CUPS 的 <c>lp</c> 子进程。lp 默认**同步假脱机**：退出码 0 即 CUPS 已收件入队，
/// 之后删临时 PDF 不会打断作业。
/// </summary>
internal sealed class UnixLpPrintCommand : ISystemPrintCommand
{
    /// <summary>共享实例。</summary>
    public static UnixLpPrintCommand Instance { get; } = new();

    /// <inheritdoc />
    public bool MayReadFileAfterReturn => false;

    /// <inheritdoc />
    public string? Invoke(string pdfPath, string? printerName, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("lp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(printerName);
        }

        psi.ArgumentList.Add(pdfPath);

        using var process = Process.Start(psi);
        if (process is null)
            return "无法启动 lp 进程：PATH 里没有 CUPS 的 lp。请安装 cups 并确认 /usr/bin/lp 可用。";

        // 先挂上异步读尾再等退出：缓冲区被灌满时先 WaitForExit 会死锁
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Math.Max(timeoutSeconds, 1) * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程恰好已退出：什么都不做
            }

            return $"lp 超时（{timeoutSeconds}s）未完成假脱机，进程已被终止。";
        }

        if (process.ExitCode == 0)
            return null;

        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        var detail = stderr.Length > 0 ? stderr : stdout;
        return detail.Length > 0
            ? $"lp 退出码 {process.ExitCode}：{detail}"
            : $"lp 退出码 {process.ExitCode}（CUPS 未给出原因）。";
    }
}

/// <summary>
/// 系统打印目标：把渲染产物交给操作系统的打印管线（FR-PRT-03 的"靠各系统 API"路线）。
/// 一次作业 = 渲染一份临时多页 PDF → 调系统 API 交付 → 删临时文件。
/// Windows 用 ShellExecute 的 <c>printto</c>/<c>print</c> 动词；macOS/Linux 用 CUPS 的 <c>lp</c>。
/// 组件不做打印机对接（GDI 光栅、驱动协商）——那是系统与应用的职责。
/// </summary>
/// <remarks>
/// <para>
/// <b>结果语义</b>：<see cref="PrintResult.Ok"/> 的 <see cref="PrintResult.PrintedPages"/> 是**交给管线的页数**
/// （表格续页后以引擎回传的真实页数计），不是出纸确认。Windows 的动词交付是异步的，关联应用受理后
/// 本层无法得知实际打印结果；lp 同步假脱机，退出码 0 即 CUPS 已收件。
/// </para>
/// <para>
/// <b>临时件生命周期</b>：失败、取消、lp 同步假脱机（文件已进 CUPS 队列）都立即删除；
/// Windows 动词交付成功后**不删**——关联应用是被异步拉起的，可能过好几秒才打开文件，
/// 立即删就是抢跑（实测 WPS 报「不是一个有效的文件」），文件留给下次作业的陈旧清扫
/// （超过 1 小时的 <c>crossprint-system-*.pdf</c> 才收）。
/// </para>
/// <para>
/// <b>失败不抛</b>：与 <see cref="PdfFilePrintTarget"/> 同一契约——IO、平台、引擎异常一律折进
/// <see cref="PrintResult.Failed(string, Exception?)"/>，只有进程级致命异常（<see cref="OutOfMemoryException"/>）上抛。
/// 不支持的平台（如手机）折成失败结果，不抛 <see cref="PlatformNotSupportedException"/>。
/// </para>
/// </remarks>
public sealed class SystemPrintTarget : IPrintTarget
{
    /// <inheritdoc />
    public string Name => "system";

    readonly ISystemPrintCommand? _command;
    readonly Func<string>? _tempDirectory;

    /// <summary>创建系统打印目标（命令按当前 OS 自动选择）。</summary>
    public SystemPrintTarget()
    {
    }

    /// <summary>测试注入：指定交付命令与临时目录来源。</summary>
    internal SystemPrintTarget(ISystemPrintCommand command, Func<string>? tempDirectory = null)
    {
        _command = command;
        _tempDirectory = tempDirectory;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="IPrintTarget"/> 是文件型目标的契约；系统目标从 settings 只取渲染透传字段
    /// （<see cref="PrintSettings.OutputPath"/> 与"交给系统打印"无关，被忽略——目的地由系统管线决定）。
    /// 打印机选择走 <see cref="SystemPrintOptions.PrinterName"/>，但接口入口给不了它，
    /// 需要"指定打印机"的宿主请走 <see cref="PrintAsync(PrintDocument, SystemPrintOptions?, CancellationToken)"/>。
    /// </remarks>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        PrintSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        return PrintAsync(document, FromSettings(settings), cancellationToken);
    }

    /// <summary>打印单文档（推荐入口：能指定打印机与超时）。</summary>
    public Task<PrintResult> PrintAsync(
        PrintDocument document,
        SystemPrintOptions? options = null,
        CancellationToken cancellationToken = default)
        => PrintBatchAsync(new[] { document }, options, null, cancellationToken);

    /// <summary>
    /// 批量打印：一份多页 PDF 交给系统管线。每份文档按 <see cref="PrintDocument.Copies"/> 连续出同内容页。
    /// </summary>
    public Task<PrintResult> PrintBatchAsync(
        IReadOnlyList<PrintDocument> documents,
        SystemPrintOptions? options = null,
        RenderReport? issues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return Task.Run(() => Run(documents, options ?? new SystemPrintOptions(), issues, cancellationToken));
    }

    /// <inheritdoc cref="PrintBatchAsync(IReadOnlyList{PrintDocument}, SystemPrintOptions?, RenderReport?, CancellationToken)"/>
    PrintResult Run(
        IReadOnlyList<PrintDocument> documents,
        SystemPrintOptions options,
        RenderReport? issues,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;

        // finally 里判定临时件去留用：文件是否已交给系统、系统侧是否还会延迟读它
        var handedOff = false;
        var commandMayReadFile = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (pages, expandFailure) = PdfFilePrintTarget.ExpandPages(documents);
            if (pages is null)
                return PrintResult.Failed(expandFailure ?? "批次不可打印。");

            var tempDir = Directory.CreateDirectory(_tempDirectory?.Invoke() ?? Path.GetTempPath()).FullName;
            SweepStaleTemps(tempDir);
            tempPath = Path.Combine(tempDir, $"crossprint-system-{Guid.NewGuid():N}.pdf");

            var templates = new List<PrintTemplate>(pages.Count);
            foreach (var page in pages)
                templates.Add(page.Template);

            RenderReport report;
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                (report, _) = BatchPdfExporter.ExportPages(
                    templates,
                    stream,
                    options.Options,
                    index => pages[index].Data,
                    options.FontResolver,
                    options.Images,
                    options.Renderer);
            }

            // 渲染完、交付前的最后一次取消检查：交付发起后没有协作式取消点了
            cancellationToken.ThrowIfCancellationRequested();

            var command = _command
                ?? (OperatingSystem.IsWindows() ? WindowsShellPrintCommand.Instance
                    : OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? UnixLpPrintCommand.Instance
                    : throw new PlatformNotSupportedException(
                        $"系统打印目标支持 Windows（ShellExecute 动词）与 macOS/Linux（lp），当前是 {Environment.OSVersion.Platform}。"));

            var failure = command.Invoke(tempPath, options.PrinterName, options.TimeoutSeconds);
            handedOff = failure is null;
            commandMayReadFile = command.MayReadFileAfterReturn;
            if (failure is not null)
                return PrintResult.Failed($"系统打印交付失败：{failure}");

            issues?.AddRange(report);
            return PrintResult.Ok(pages.Count, Summarize(report));
        }
        catch (OperationCanceledException ex)
        {
            return PrintResult.Failed("作业已取消，系统打印未发起。", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PrintResult.Failed($"系统打印失败：{ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            // 交付成功且系统侧会延迟读文件（Windows 动词交付）时不删：删了就是和关联应用抢跑，
            // 实测 WPS 会报「不是一个有效的文件」。文件留给下次作业的陈旧清扫。
            // 其余情况（失败、取消、lp 同步假脱机）文件没人再读，立即删。
            if (tempPath is not null && !(handedOff && commandMayReadFile))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>陈旧临时件的最短年龄，与 <see cref="PdfFilePrintTarget"/> 同口径。</summary>
    static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>清掉上次作业被占用没删成的临时件（超过 1 小时才收；打开失败说明有人正在用，宁留勿删）。</summary>
    static void SweepStaleTemps(string directory)
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "crossprint-system-*.pdf");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleTempAge;
        foreach (var file in candidates)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                // 独占打开：拿不到句柄说明有关联应用或并发作业正在用它
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

    static SystemPrintOptions FromSettings(PrintSettings settings) => new()
    {
        Options = settings.Options,
        FontResolver = settings.FontResolver,
        Images = settings.Images,
        Renderer = settings.Renderer,
    };

    /// <summary>把渲染告警压成一行分类摘要（与 <see cref="PdfFilePrintTarget"/> 同口径）。</summary>
    static string? Summarize(RenderReport report)
        => report.HasIssues
            ? $"产物已交给系统打印管线，但有 {report.Issues.Count} 条渲染告警："
              + string.Join("、", report.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"))
            : null;
}
