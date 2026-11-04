using System.Globalization;

namespace CrossPrint.Core;

/// <summary>
/// 页码区间规格（hiprint <c>showInPage</c> / <c>unShowInPage</c>）：
/// <c>"1,3-5,8"</c>、<c>"all"</c>、<c>"*"</c>。
/// 设计器属性通道与分页引擎共用这一份解析与规范化，避免两端口径分歧。
/// </summary>
public static class PageRangeSpec
{
    /// <summary>
    /// 把面板输入规范成稳定写法：全角逗号转半角、去空白、倒置区间翻正、
    /// 整数去前导零、重复段去重。null / 空白返回 null（= 不限页）。
    /// 无法识别的片段原样保留（由 <see cref="Matches"/> 忽略），不静默吞掉用户输入。
    /// </summary>
    public static string? Normalize(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var text = spec.Replace('，', ',');
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;

            var canonical = Canonicalize(part);
            if (seen.Add(canonical))
                parts.Add(canonical);
        }

        return parts.Count == 0 ? text.Trim() : string.Join(",", parts);
    }

    /// <summary>规格是否命中指定显示页（1 起）。null / 空白不命中；<c>all</c> / <c>*</c> 恒命中。</summary>
    public static bool Matches(string? spec, int page)
    {
        var normalized = Normalize(spec);
        if (normalized is null)
            return false;
        if (normalized.Equals("all", StringComparison.OrdinalIgnoreCase) || normalized == "*")
            return true;

        foreach (var part in normalized.Split(','))
        {
            if (part.IndexOf('-') >= 0)
            {
                var segs = part.Split('-');
                if (segs.Length == 2
                    && int.TryParse(segs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                    && int.TryParse(segs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
                    && page >= Math.Min(a, b) && page <= Math.Max(a, b))
                    return true;
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == page)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 元素在指定显示页是否可见：<c>unShowInPages</c> 优先排除，其次 <c>showInPages</c> 限定，
    /// 两者都空 = 每页可见。分页引擎与宿主都用这个顺序，不得各写一份。
    /// </summary>
    public static bool IsVisibleOn(string? showInPages, string? unShowInPages, int page)
    {
        if (!string.IsNullOrWhiteSpace(unShowInPages) && Matches(unShowInPages, page))
            return false;
        if (!string.IsNullOrWhiteSpace(showInPages))
            return Matches(showInPages, page);
        return true;
    }

    static string Canonicalize(string part)
    {
        if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
            return "all";
        if (part == "*")
            return "*";

        var dash = part.IndexOf('-');
        if (dash >= 0 && part.IndexOf('-', dash + 1) < 0)
        {
            var left = part.Substring(0, dash).Trim();
            var right = part.Substring(dash + 1).Trim();
            if (int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                && int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                return Number(Math.Min(a, b)) + "-" + Number(Math.Max(a, b));
            return part;
        }

        return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
            ? Number(single)
            : part;
    }

    static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
