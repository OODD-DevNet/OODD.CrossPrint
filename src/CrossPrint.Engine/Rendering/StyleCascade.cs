using CrossPrint.Core;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 样式库级联解析：CSS 同款「库样式打底、内联覆盖」。
/// 非可空内联字段无法区分「用户显式设回默认值」与「从未改过」，采用哨兵口径——
/// 内联值等于模型默认值时视为未设置，样式补位；可空内联字段 null 即未设置。
/// 各 Pick 调用点传入的模型默认值字面量必须与模型属性初始化器保持一致。
/// </summary>
internal static class StyleCascade
{
    /// <summary>按 id 找样式；id 空、无样式库或引用断链一律返回 null（只影响外观，不报错）。</summary>
    public static NamedStyle? Find(RenderContext context, string? styleId)
        => string.IsNullOrEmpty(styleId) || context.Styles is not { } styles
            ? null
            : styles.TryGetValue(styleId, out var style) ? style : null;

    /// <summary>非可空值类型内联字段：内联偏离模型默认值则内联优先，否则样式补位。</summary>
    public static T Pick<T>(T inline, T modelDefault, T? styleValue) where T : struct
        => styleValue is { } value && EqualityComparer<T>.Default.Equals(inline, modelDefault) ? value : inline;

    /// <summary>非可空字符串内联字段（字体族）：同上哨兵口径。</summary>
    public static string Pick(string inline, string modelDefault, string? styleValue)
        => styleValue is not null && inline == modelDefault ? styleValue : inline;

    /// <summary>可空内联字段：null 或等于模型默认值都视为未设置，样式补位；无样式时回落内联值。</summary>
    public static T? PickNullable<T>(T? inline, T modelDefault, T? styleValue) where T : struct
    {
        if (inline is { } value && !EqualityComparer<T>.Default.Equals(value, modelDefault))
            return value;
        return styleValue ?? inline;
    }
}
