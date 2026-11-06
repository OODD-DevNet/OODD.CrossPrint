namespace CrossPrint.I18n;

/// <summary>
/// 工具箱磁贴的中英双语过滤助手。Avalonia 基准与其余六宿主共用同一份匹配口径，
/// 避免每个宿主各写一套「中文名/英文名包含查询串」的逻辑。
/// </summary>
public static class ToolboxNames
{
    /// <summary>
    /// 元素类型键 → 词表键（<c>Element.*</c>）。注意 qrcode 的词表键是 <c>QrCode</c>，
    /// 不能简单首字母大写，否则会查到不存在的 <c>Element.Qrcode</c>。
    /// </summary>
    public static string I18nKeyOf(string typeKey)
        => "Element." + (typeKey == "qrcode"
            ? "QrCode"
            : char.ToUpperInvariant(typeKey[0]) + typeKey.Substring(1));

    /// <summary>
    /// 该磁贴是否命中查询：中文名 / 英文名 / 类型键（忽略大小写）任一包含即可。
    /// 查询为空白时恒为 true（不过滤）。
    /// </summary>
    public static bool Matches(string typeKey, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return true;

        var key = I18nKeyOf(typeKey);
        return Contains(Localizer.Zh(key), q)
               || Contains(Localizer.En(key), q)
               || Contains(typeKey, q);
    }

    // netstandard2.0 没有 string.Contains(string, StringComparison)，统一走 IndexOf。
    static bool Contains(string source, string value)
        => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
