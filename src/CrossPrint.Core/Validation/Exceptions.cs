namespace CrossPrint.Core;

/// <summary>模板结构非法（缺失必填字段、类型不匹配）。</summary>
public class TemplateValidationException : Exception
{
    /// <summary>创建异常。</summary>
    public TemplateValidationException(string message)
        : base(message)
    {
    }

    /// <summary>创建异常并定位到具体元素与字段。</summary>
    public TemplateValidationException(string? elementId, string fieldName, string? detail = null)
        : base(BuildMessage(elementId, fieldName, detail))
    {
        ElementId = elementId;
        FieldName = fieldName;
    }

    /// <summary>出问题的元素 id；顶层字段为 null。</summary>
    public string? ElementId { get; }

    /// <summary>出问题的字段名。</summary>
    public string? FieldName { get; }

    static string BuildMessage(string? elementId, string fieldName, string? detail)
    {
        var scope = elementId is null ? "模板根节点" : $"元素 '{elementId}'";
        var suffix = detail is null ? string.Empty : $"：{detail}";
        return $"{scope} 缺少或非法字段 '{fieldName}'{suffix}";
    }
}

/// <summary>模板中出现未注册的元素类型。</summary>
public sealed class UnknownElementTypeException : TemplateValidationException
{
    /// <summary>创建异常。</summary>
    public UnknownElementTypeException(string? elementId, string typeKey)
        : base(elementId, "type", $"未知元素类型 '{typeKey}'。已注册：{string.Join(", ", ElementTypeKeys.All)}")
    {
        TypeKey = typeKey;
    }

    /// <summary>未识别的类型键。</summary>
    public string TypeKey { get; }
}
