namespace CrossPrint.Core.Hiprint;

/// <summary>hiprint 模板导入选项。</summary>
public sealed class HiprintImportOptions
{
    /// <summary>
    /// 要导入的面板下标（0 起）。hiprint 一个模板可含多个面板（多联单），
    /// CrossPrint 一个 <see cref="PrintTemplate"/> 对应一个页面，因此一次只导入一个面板；
    /// 越界时回落到第一个面板并记一条警告。
    /// </summary>
    public int PanelIndex { get; set; }
}

/// <summary>导入问题的严重级别。</summary>
public enum HiprintImportSeverity
{
    /// <summary>提示：已按等价方式处理，不损失内容。</summary>
    Info,

    /// <summary>警告：该特性无法等价表达，已降级或忽略，打印结果会与 hiprint 有差异。</summary>
    Warning,

    /// <summary>错误：该元素/面板无法导入，已跳过。其余元素仍然正常导入。</summary>
    Error,
}

/// <summary>
/// 导入过程中的一条问题。hiprint 支持 JavaScript 格式化函数、水印、页码、跨页长文本等
/// CrossPrint 当前不表达的能力；这些一律降级并在结果里留痕，绝不静默丢弃。
/// </summary>
public sealed class HiprintImportIssue
{
    internal HiprintImportIssue(
        HiprintImportSeverity severity,
        string code,
        string message,
        string? elementId = null,
        string? sourcePath = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        ElementId = elementId;
        SourcePath = sourcePath;
    }

    /// <summary>严重级别。</summary>
    public HiprintImportSeverity Severity { get; }

    /// <summary>稳定的问题代码，如 <c>hiprint.js-formatter</c>，供宿主分类展示。</summary>
    public string Code { get; }

    /// <summary>人读说明。</summary>
    public string Message { get; }

    /// <summary>相关元素 id（已导入的元素才有）。</summary>
    public string? ElementId { get; }

    /// <summary>hiprint 源路径，如 <c>panels[0].printElements[3]</c>。</summary>
    public string? SourcePath { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        var scope = SourcePath is null ? string.Empty : $" @ {SourcePath}";
        var element = ElementId is null ? string.Empty : $" [{ElementId}]";
        return $"[{Severity}] {Code}{element}{scope}: {Message}";
    }
}

/// <summary>模板中一个面板的概要信息，便于宿主让用户选择导入哪一个。</summary>
public sealed class HiprintPanelInfo
{
    internal HiprintPanelInfo(int index, string? name, double widthMm, double heightMm, int elementCount, bool imported)
    {
        Index = index;
        Name = name;
        WidthMm = widthMm;
        HeightMm = heightMm;
        ElementCount = elementCount;
        Imported = imported;
    }

    /// <summary>面板下标（0 起）。</summary>
    public int Index { get; }

    /// <summary>面板名（hiprint <c>panel.name</c>，可能为 null）。</summary>
    public string? Name { get; }

    /// <summary>面板宽度（mm）。</summary>
    public double WidthMm { get; }

    /// <summary>面板高度（mm）；0 表示连续纸。</summary>
    public double HeightMm { get; }

    /// <summary>面板内元素数量。</summary>
    public int ElementCount { get; }

    /// <summary>是否为本次导入的面板。</summary>
    public bool Imported { get; }
}

/// <summary>hiprint 模板导入结果。</summary>
public sealed class HiprintImportResult
{
    internal HiprintImportResult(
        PrintTemplate template,
        IReadOnlyList<HiprintImportIssue> issues,
        IReadOnlyList<HiprintPanelInfo> panels)
    {
        Template = template;
        Issues = issues;
        Panels = panels;
    }

    /// <summary>导入得到的 CrossPrint 模板（可直接交给渲染/编辑/打印链路）。</summary>
    public PrintTemplate Template { get; }

    /// <summary>降级、忽略与跳过等全部问题，按发现顺序排列。</summary>
    public IReadOnlyList<HiprintImportIssue> Issues { get; }

    /// <summary>源模板里的全部面板（含未导入的）。</summary>
    public IReadOnlyList<HiprintPanelInfo> Panels { get; }

    /// <summary>是否存在 <see cref="HiprintImportSeverity.Error"/> 级问题（有元素/面板被跳过）。</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == HiprintImportSeverity.Error);
}

/// <summary>
/// 文本不是可识别的 hiprint 模板（无法归一化为 JSON、找不到 panels 数组、面板越界且无法回落）时抛出。
/// 单个元素的问题不会抛异常，而是进入 <see cref="HiprintImportResult.Issues"/>。
/// </summary>
public sealed class HiprintImportException : Exception
{
    /// <summary>创建异常。</summary>
    public HiprintImportException(string message)
        : base(message)
    {
    }

    /// <summary>创建异常并保留内部原因。</summary>
    public HiprintImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
