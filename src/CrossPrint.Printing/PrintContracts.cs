using CrossPrint.Core;
using CrossPrint.Engine;

namespace CrossPrint.Printing;

/// <summary>
/// 一份待打印文档：模板 + 该文档的数据源 + 份数。
/// </summary>
/// <remarks>
/// <b>份数为什么挂在文档上而不是 <see cref="PrintSettings"/> 上</b>（写死在这里，免得后人"顺手统一"）：
/// 份数是"这份东西要出几张纸"的事实，属于作业内容；settings 是"往哪儿、怎么写"的通道参数，
/// 一次批量作业里 A4 发票印 1 份、面单印 2 份是真实需求（配货单联数不同），
/// 把份数塞进 settings 等于宣布"一批只能一个份数"——那是按省事裁剪。
/// <para>
/// 构造器挡住 <c>Copies &lt; 1</c>，但 record 的 <c>with</c> 表达式不走构造器，
/// 所以 <see cref="PdfFilePrintTarget"/> 在展开页序时再校验一次。两处都要有闸：
/// 少一道闸的代价是现场少印一张单子，而不是编译错误。
/// </para>
/// </remarks>
public sealed record PrintDocument
{
    /// <summary>打印一份文档。</summary>
    /// <param name="template">排版模板。同一实例可以出现在批量作业的多个文档里（引擎按页序渲染，模板只读）。</param>
    /// <param name="data">数据绑定源（POCO / 字典 / JsonDocument，交给 <see cref="DataBinder"/> 解析）；无绑定传 null。</param>
    /// <param name="copies">份数，必须 ≥ 1。0 份不是"不打"，是调用方算错了，静默吞掉会让现场少出纸。</param>
    /// <exception cref="ArgumentNullException">template 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">copies 小于 1。</exception>
    public PrintDocument(PrintTemplate template, object? data = null, int copies = 1)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template), "没有模板的文档不是待打印物，是 null。");
        Data = data;
        Copies = copies >= 1
            ? copies
            : throw new ArgumentOutOfRangeException(nameof(copies), copies, "份数必须 ≥ 1：负数与零都不是合法作业。");
    }

    /// <summary>排版模板。</summary>
    public PrintTemplate Template { get; init; }

    /// <summary>数据绑定源，可为 null。</summary>
    public object? Data { get; init; }

    /// <summary>份数。同一文档印 N 份 = PDF 里连续 N 页同样内容。</summary>
    public int Copies { get; init; }

    /// <summary>位置化解构，保持 <c>record PrintDocument(PrintTemplate, object?, int)</c> 的对外形状。</summary>
    /// <param name="template">模板。</param>
    /// <param name="data">数据源。</param>
    /// <param name="copies">份数。</param>
    public void Deconstruct(out PrintTemplate template, out object? data, out int copies)
    {
        template = Template;
        data = Data;
        copies = Copies;
    }
}

/// <summary>
/// 打印输出设置。v0.1 只收 <see cref="PdfFilePrintTarget"/> 真正读得走的字段。
/// </summary>
/// <remarks>
/// <b>这里故意没有</b>打印机名、双面、页码范围、纸张覆盖等旋钮：v0.1 没有任何实现会读它们，
/// 挂上去就是骗调用方"我设置生效了"。本仓对死旋钮有先例——不起作用的 <c>AutoPageHeight</c> 被整条删掉。
/// v0.2 的 GDI / lp 目标要这些参数时，加在各自的目标类型或其派生设置上，别改这里。
/// <para>
/// 也<b>没有</b> "每条数据一个文件" 的路径模板：一份数据一个 PDF 意味着每份都整份内嵌一次字库
/// （实测 CJK 下 100 个单文件 = 100 × 字库体积），是个诱导错用法的旋钮。真要分文件就循环调
/// <see cref="IPrintTarget.PrintAsync"/>，路径由宿主自己拼。
/// </para>
/// </remarks>
public sealed record PrintSettings
{
    /// <summary>输出设置。</summary>
    /// <param name="outputPath">输出 PDF 的完整路径（含文件名）。目标目录不存在时由打印目标创建。</param>
    /// <exception cref="ArgumentException">outputPath 为 null/空白——"打印到哪里"不是可以事后补的参数。</exception>
    public PrintSettings(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        OutputPath = outputPath;
    }

    /// <summary>输出 PDF 的文件路径（含目录）。批量作业默认写成一整份多页文件，也用这个路径。</summary>
    public string OutputPath { get; init; }

    /// <summary>渲染选项，透传引擎；null 用引擎默认。PDF 目标会强制把 <c>Dpi</c> 设为 72（1 单位 = 1 点）。</summary>
    public RenderOptions? Options { get; init; }

    /// <summary>
    /// 目标文件已存在时是否覆盖。默认 <c>true</c>：同名即覆盖，不提示、不备份。
    /// 置 <c>false</c> 则作业以失败结束且绝不碰既有文件——给了这个旋钮就必须真的拒绝，否则它是死旋钮。
    /// </summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>字体解析器，透传引擎；null 用引擎默认解析器（走系统字体库 + Skia 的按字符回退）。</summary>
    public IFontResolver? FontResolver { get; init; }

    /// <summary>图片解析器，透传引擎；null 表示只有模板内嵌 base64 图可用，ResolverKey 来源记降级告警。</summary>
    public IImageResolver? Images { get; init; }

    /// <summary>
    /// 渲染器实例，透传引擎；null 用 <see cref="TemplateRenderer.Default"/>。
    /// 批量作业复用同一实例才谈得上共享字体/位图缓存。
    /// </summary>
    public TemplateRenderer? Renderer { get; init; }

    /// <summary>位置化解构，只暴露 v0.1 的核心三件；其余字段按名字读。</summary>
    /// <param name="outputPath">输出路径。</param>
    /// <param name="options">渲染选项。</param>
    /// <param name="overwrite">是否覆盖。</param>
    public void Deconstruct(out string outputPath, out RenderOptions? options, out bool overwrite)
    {
        outputPath = OutputPath;
        options = Options;
        overwrite = Overwrite;
    }
}

/// <summary>
/// 打印作业结果。<b>不变式</b>（构造期强制，违反就抛）：
/// <list type="bullet">
/// <item><description><see cref="Success"/> = true ⇒ <see cref="Error"/> 恒为 null 且 <see cref="PrintedPages"/> ≥ 1；</description></item>
/// <item><description><see cref="Success"/> = false ⇒ <see cref="PrintedPages"/> 恒为 0——失败作业不许声称印了几页，
/// 否则宿主的"已印 37/100"日志就是假账；同时必须有 <see cref="Message"/> 或 <see cref="Error"/>，
/// 让调用方至少有一条可排查的线索。</description></item>
/// <item><description>属性是 <c>private init</c>：宿主写不出 <c>result with { PrintedPages = 0 }</c>，
/// record 的 <c>with</c> 绕过构造器校验这条路从编译期就堵死。结果只由 <see cref="Ok"/> / <see cref="Failed"/> 产出。</description></item>
/// </list>
/// </summary>
public sealed record PrintResult
{
    /// <summary>
    /// 造结果。<b>优先用 <see cref="Ok"/> / <see cref="Failed"/></b>——它们把不变式写在一处；
    /// 本构造器留着是为了保持 <c>record PrintResult(bool, int, string?, Exception?)</c> 的对外形状。
    /// </summary>
    /// <param name="success">作业是否成功产出可用文件。</param>
    /// <param name="printedPages">写出的页数；失败作业必须传 0。</param>
    /// <param name="message">人类可读摘要：成功时是降级告警摘要（可为 null），失败时是失败原因。</param>
    /// <param name="error">捕获到的异常；成功时必须为 null。</param>
    /// <exception cref="ArgumentException">违反不变式：成功却带异常、成功却零页、失败却计了页、或失败却无任何原因。</exception>
    public PrintResult(bool success, int printedPages, string? message = null, Exception? error = null)
    {
        if (success)
        {
            if (error is not null)
                throw new ArgumentException($"Success=true 不许带 Error（{error.GetType().Name}）：要么失败并交出异常，要么成功并保持干净。", nameof(error));
            if (printedPages < 1)
                throw new ArgumentException($"Success=true 但 PrintedPages={printedPages}：一页没印不叫成功。", nameof(printedPages));
        }
        else
        {
            if (printedPages != 0)
                throw new ArgumentException($"Success=false 但 PrintedPages={printedPages}：失败作业不许计页，半成功在本层不存在。", nameof(printedPages));
            if (string.IsNullOrWhiteSpace(message) && error is null)
                throw new ArgumentException("失败结果必须带原因：message 与 error 不能同时为空。", nameof(message));
        }

        Success = success;
        PrintedPages = printedPages;
        Message = message;
        Error = error;
    }

    /// <summary>作业是否成功产出可用文件。调用方可以无条件相信它——不变式在构造期就把矛盾组合拦住了。</summary>
    public bool Success { get; private init; }

    /// <summary>
    /// 成功写出的页数；失败作业恒为 0。
    /// 口径是本层按份数摊平出的**页框数**（引擎目前一份模板一页就是页），不是回读 PDF 页树得到的真数——
    /// 表格自动续页（FR-BND-05，P2）落地后这里必须改成由引擎回传的真实页数，否则批量作业会虚报。
    /// </summary>
    public int PrintedPages { get; private init; }

    /// <summary>
    /// 人类可读摘要。成功且有渲染告警（缺字段、缺字体等）时，这里是告警分类汇总，
    /// 结构化明细走 <see cref="PdfFilePrintTarget.PrintBatchAsync(IReadOnlyList{PrintDocument}, PrintSettings, CrossPrint.Core.RenderReport, CancellationToken)"/>
    /// 的 <c>issues</c> 重载（NFR-07）。
    /// </summary>
    public string? Message { get; private init; }

    /// <summary>捕获到的异常；成功时恒为 null。</summary>
    public Exception? Error { get; private init; }

    /// <summary>造成功结果。</summary>
    /// <param name="printedPages">写出的页数（≥1）。</param>
    /// <param name="message">降级告警摘要，无告警传 null。</param>
    /// <returns>成功结果。</returns>
    /// <exception cref="ArgumentException">printedPages 小于 1。</exception>
    public static PrintResult Ok(int printedPages, string? message = null)
        => printedPages >= 1
            ? new PrintResult(true, printedPages, message, null)
            : throw new ArgumentException($"成功结果至少要有 1 页，拿到 {printedPages} 页。", nameof(printedPages));

    /// <summary>造失败结果。页数为 0，异常只登记不重抛。</summary>
    /// <param name="message">失败原因（必填）。</param>
    /// <param name="error">捕获到的异常，可为 null（如"文件已存在且禁止覆盖"这类没有异常的策略性失败）。</param>
    /// <returns>失败结果。</returns>
    /// <exception cref="ArgumentException">message 为空白。</exception>
    public static PrintResult Failed(string message, Exception? error = null)
        => string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("失败结果必须带原因，空消息等于让调用方猜。", nameof(message))
            : new PrintResult(false, 0, message, error);

    /// <summary>位置化解构，保持 <c>record PrintResult(bool, int, string?, Exception?)</c> 的对外形状。</summary>
    /// <param name="success">是否成功。</param>
    /// <param name="printedPages">页数。</param>
    /// <param name="message">摘要。</param>
    /// <param name="error">异常。</param>
    public void Deconstruct(out bool success, out int printedPages, out string? message, out Exception? error)
    {
        success = Success;
        printedPages = PrintedPages;
        message = Message;
        error = Error;
    }
}
