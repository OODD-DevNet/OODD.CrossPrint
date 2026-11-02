using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossPrint.Core;

namespace CrossPrint.Engine;

/// <summary>
/// <c>{{路径}}</c> 占位符与字段路径解析。支持 <c>order.customer.name</c>、<c>items[0].sku</c>
/// 与链式下标 <c>matrix[0][1]</c>；畸形下标段（未闭合/非整数）按"取不到"处理。
/// 解析不到字段时**替换为空串并记 <see cref="RenderIssueKind.BindingMissingField"/>**，绝不抛异常，
/// 也不把解析成功的 <c>{{}}</c> 原样留在打印件上——现场印错一张面单比留一串花括号更贵。
/// 唯一例外是语法不完整的占位符（如 <c>{{name}</c>）：内容原样打印（语义猜不准）但必记告警。
/// 键的大小写口径：泛型字典按宿主比较器查键，POCO 反射忽略大小写，JSON 数据源
/// （<see cref="JsonElement"/> / <see cref="JsonNode"/>）按 JSON 语义 Ordinal 精确匹配（见 <see cref="TryMember"/>）。
/// </summary>
public static class DataBinder
{
    static readonly char[] _pathSeparators = ['.', '[', ']'];
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> _propertyCache = new();

    /// <summary>占位符正则：<c>{{</c> 与 <c>}}</c> 之间允许空白。</summary>
    static readonly System.Text.RegularExpressions.Regex _placeholder =
        new(@"\{\{\s*([^{}]*?)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 把文本里的 <c>{{字段}}</c> 换成数据值。没有占位符时原样返回，零开销。
    /// </summary>
    /// <param name="text">可能含占位符的模板文本。</param>
    /// <param name="data">数据源，可为 null。</param>
    /// <param name="report">告警收集器。</param>
    /// <param name="elementId">告警归属元素。</param>
    /// <param name="missingFallback">
    /// 元素级测试数据。为空时只是"解析不到"的兜底值（不打告警）；为空串/null 时保持原行为（空串 + 告警）。
    /// </param>
    /// <param name="preferTestData">
    /// 设计画布专用：true 且测试数据非空时，测试数据最优先——宿主数据解析得到也用测试值，
    /// 用户输入什么就看什么；打印/预览渲染恒传 false，测试数据不参与真实输出。
    /// </param>
    /// <param name="context">
    /// 渲染上下文，供 <c>{{pageNo}}</c>/<c>{{pageCount}}</c> 的页码回退：数据源里没有这两个字段时
    /// 读 <see cref="RenderContext.PageNo"/>/<see cref="RenderContext.PageTotal"/>（0 = 无页码语义，不回退）。
    /// </param>
    /// <returns>替换后的文本。</returns>
    public static string BindText(string? text, object? data, RenderReport report, string? elementId, string? missingFallback = null, bool preferTestData = false, RenderContext? context = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text ?? string.Empty;

        var matches = _placeholder.Matches(text);
        var result = _placeholder.Replace(text, match =>
        {
            var path = match.Groups[1].Value;

            // 设计画布：用户输入的测试数据说了算——"输入一个测试数据看效果"必须立刻看得到。
            if (preferTestData && !string.IsNullOrEmpty(missingFallback))
                return missingFallback;

            if (TryResolve(data, path, out var value))
                return ToString(value);

            if (context is not null && TryPageValue(path, context, out var pageText))
                return pageText;

            if (!string.IsNullOrEmpty(missingFallback))
                return missingFallback;

            report.Add(elementId, RenderIssueKind.BindingMissingField, $"绑定路径 '{path}' 在数据源中不存在，已替换为空串。");
            return string.Empty;
        });

        // 未闭合占位符（{{name}）正则匹配不到：内容原样打印——猜"空串"还是猜"字面花括号"都会猜错；
        // 但必须记告警，模板语法错误静默印上面单比缺字段更难被发现。按原文计数，替换值里带 "{{" 不会误报。
        if (CountOpenPairs(text) > matches.Count)
            report.Add(elementId, RenderIssueKind.BindingMissingField,
                $"文本中有占位符未闭合（缺少 }}}}），已原样打印：{Shorten(text)}");

        return result;
    }

    /// <summary>非重叠计数 <c>{{</c> 出现次数：每个成功匹配的占位符消耗一对，多出来的就是未闭合的。</summary>
    static int CountOpenPairs(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{')
            {
                count++;
                i++;
            }
        }

        return count;
    }

    static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    /// <summary>
    /// 页码绑定回退：数据源没有 <c>pageNo</c>/<c>pageCount</c> 字段时读渲染上下文。
    /// 上下文没带页码语义（值为 0）时返回 false，照常走缺字段告警——
    /// 页码是打印会话的事实，静默印成空串比一句告警更难排查。
    /// </summary>
    static bool TryPageValue(string path, RenderContext context, out string text)
    {
        text = string.Empty;
        if (path.Equals("pageNo", StringComparison.OrdinalIgnoreCase))
        {
            if (context.PageNo > 0)
            {
                text = context.PageNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        if (path.Equals("pageCount", StringComparison.OrdinalIgnoreCase)
            || path.Equals("totalPages", StringComparison.OrdinalIgnoreCase))
        {
            if (context.PageTotal > 0)
            {
                text = context.PageTotal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>路径是否为占位符形式（供表格判断某列是否需要按行绑定）。</summary>
    public static bool HasPlaceholder(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains("{{", StringComparison.Ordinal);

    /// <summary>
    /// 取文本中第一个占位符路径的**原始值**（formatter 的 value 参数，hiprint 同款语义）。
    /// 无占位符或解析不到返回 null；值本身为 null 但字段存在也算解析到（返回 null，与缺字段不可区分，无碍）。
    /// </summary>
    public static object? MainBoundValue(string? text, object? data)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var match = _placeholder.Match(text);
        if (!match.Success)
            return null;

        return TryResolve(data, match.Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>沿路径取值。</summary>
    /// <param name="data">数据源根对象。</param>
    /// <param name="path">点分路径，段后可跟 <c>[下标]</c>。</param>
    /// <param name="value">取到的值；失败时为 null。</param>
    /// <returns>路径是否存在（值为 null 但键存在也算存在）。</returns>
    public static bool TryResolve(object? data, string? path, out object? value)
    {
        value = null;
        if (data is null || string.IsNullOrWhiteSpace(path))
            return false;

        var current = data;
        foreach (var segment in SplitPath(path))
        {
            if (current is null)
                return false;
            if (!TryStep(current, segment.Name, segment.Index, out current))
                return false;
        }

        value = current;
        return true;
    }

    /// <summary>
    /// 取一个集合型路径的全部元素，供表格按行展开。
    /// 字符串不当集合（否则 "abc" 会变成 3 行）。
    /// </summary>
    /// <param name="data">数据源根对象。</param>
    /// <param name="path">字段路径。</param>
    /// <param name="items">展开后的元素；路径不是集合时为 null。</param>
    /// <returns>是否成功取到集合。</returns>
    public static bool TryResolveCollection(object? data, string? path, out IReadOnlyList<object?>? items)
    {
        items = null;
        if (!TryResolve(data, path, out var value) || value is null or string)
            return false;

        // JsonElement 数组不实现 IEnumerable，走不到下面的展开——宿主直接把 JSON 递进来是最常见的用法
        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            var jsonItems = new List<object?>(array.GetArrayLength());
            foreach (var item in array.EnumerateArray())
                jsonItems.Add(item);
            items = jsonItems;
            return true;
        }

        if (value is not IEnumerable enumerable)
            return false;

        var list = new List<object?>();
        foreach (var item in enumerable)
            list.Add(item);

        items = list;
        return true;
    }

    /// <summary>
    /// 取路径的首段名（表格用它找出"所有列共用的行集合前缀"）。
    /// 两端空白一律剥掉：手写 JSON 里 <c>" customer . name "</c> 很常见，
    /// 留着空格会让两个本来同源的列被判成"首段不一致"而不展开数据行。
    /// </summary>
    public static string FirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim();
        var cut = trimmed.IndexOfAny(['.', '[']);
        return cut < 0 ? trimmed : trimmed[..cut].TrimEnd();
    }

    /// <summary>
    /// 去掉路径首段，返回剩余部分；无剩余时为空串。带下标的首段（<c>items[0].sku</c>）要连同下标一起剥掉。
    /// 两端空白一并剥掉，理由同 <see cref="FirstSegment"/>。
    /// </summary>
    public static string StripFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.TrimStart();
        var dot = trimmed.IndexOf('.');
        var bracket = trimmed.IndexOf('[');

        int cut;
        if (bracket >= 0 && (dot < 0 || bracket < dot))
        {
            var close = trimmed.IndexOf(']', bracket);
            // 下标没闭合就整条当作不可剥——把 "items[0" 剥成 "0" 会静默绑到错字段
            cut = close > bracket ? close + 1 : -1;
        }
        else
        {
            cut = dot;
        }

        return cut < 0 ? string.Empty : trimmed[cut..].Trim('.', ' ');
    }

    static IEnumerable<(string Name, int? Index)> SplitPath(string path)
    {
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bracket = raw.IndexOf('[');
            if (bracket < 0)
            {
                yield return (raw, null);
                continue;
            }

            // 链式下标逐层拆步（matrix[0][1] → ("matrix",0) + ("",1)），旧解析只取第一个方括号，
            // 第二层被静默丢弃——错一层下标就印一整行错数据。畸形段（未闭合/非整数/带尾巴）
            // 把整段当成员名：查不到 → 走"缺字段"空串+告警，而不是静默返回集合的 ToString。
            var pos = bracket;
            List<int>? indices = new();
            while (pos < raw.Length)
            {
                if (raw[pos] != '[')
                {
                    indices = null;
                    break;
                }

                var close = raw.IndexOf(']', pos + 1);
                if (close < 0 || !int.TryParse(raw.AsSpan(pos + 1, close - pos - 1), out var idx))
                {
                    indices = null;
                    break;
                }

                indices.Add(idx);
                pos = close + 1;
            }

            if (indices is null || indices.Count == 0)
            {
                yield return (raw, null);
                continue;
            }

            yield return (raw[..bracket], indices[0]);
            for (var i = 1; i < indices.Count; i++)
                yield return (string.Empty, indices[i]);
        }
    }

    static bool TryStep(object target, string name, int? index, out object? value)
    {
        value = null;
        object? current = target;

        if (name.Length > 0 && !TryMember(current, name, out current))
            return false;

        if (index is null)
        {
            value = current;
            return true;
        }

        var i = index.Value;
        switch (current)
        {
            case null:
                return false;
            case string s:
                if ((uint)i >= s.Length)
                    return false;
                value = s[i];
                return true;
            case IList list:
                if ((uint)i >= (uint)list.Count)
                    return false;
                value = list[i];
                return true;
            case IDictionary dict:
                // 字典按整数键取值（极少见），否则视为非法下标
                if (!dict.Contains(i))
                    return false;
                value = dict[i];
                return true;
            case JsonElement { ValueKind: JsonValueKind.Array } json:
                if ((uint)i >= (uint)json.GetArrayLength())
                    return false;
                value = json[i];
                return true;
            default:
                if (current is IEnumerable seq)
                {
                    var n = 0;
                    foreach (var e in seq)
                    {
                        if (n++ == i)
                        {
                            value = e;
                            return true;
                        }
                    }

                    return false;
                }

                return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "POCO 反射是有意的兜底路径：裁剪后成员可能消失，此时 TryMember 返回 false，由 BindText 记 BindingMissingField 并替换为空串，不会崩溃；需要确定行为的 Trim/AOT 应用应走 IDictionary/IDictionaryProvider。")]
    static bool TryMember(object? target, string name, out object? value)
    {
        value = null;

        // 大小写口径（实测已钉死于 DataBinderTests）：泛型字典走宿主自己的比较器——Ordinal 字典查不到
        // "NAME" 是宿主语义，引擎不悄悄改成不敏感；POCO 反射（BuildProperties）忽略大小写。
        switch (target)
        {
            case null:
                return false;
            case IDictionary<string, object?> generic:
                if (!generic.TryGetValue(name, out value))
                    return false;
                return true;
            case IDictionary dict:
                if (!dict.Contains(name))
                    return false;
                value = dict[name];
                return true;
            case IDictionaryProvider provider:
                return provider.TryGetMember(name, out value);
            // JSON 属性名按 Ordinal 精确匹配：JSON 本身区分大小写，悄悄改成不敏感会让
            // {"Name":…} 与 {{name}} 也能绑上，而同一个对象里的 Name/name 是两个不同字段。
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                if (!json.TryGetProperty(name, out var property))
                    return false;
                value = property;
                return true;
            // JsonObject 实现了泛型字典接口，但键类型是 JsonNode?，上面两个字典分支都匹配不到。
            case JsonObject jsonObject:
                if (!jsonObject.TryGetPropertyValue(name, out var node))
                    return false;
                value = node;
                return true;
            default:
                var props = _propertyCache.GetOrAdd(target.GetType(), BuildProperties);
                if (props.TryGetValue(name, out var prop))
                {
                    value = prop.GetValue(target);
                    return true;
                }

                var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field is null)
                    return false;

                value = field.GetValue(target);
                return true;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2070",
        Justification = "类型来自用户运行时的数据对象（object？），无法给 GetType() 加 DynamicallyAccessedMembers；裁剪后属性可能缺失属预期兜底，见 TryMember 的说明。")]
    static Dictionary<string, PropertyInfo> BuildProperties(Type type)
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.CanRead && p.GetIndexParameters().Length == 0)
                map[p.Name] = p;
        }

        return map;
    }

    /// <summary>
    /// 把宿主数据与设计态兜底数据合成一个绑定源：宿主有该字段就用宿主的，
    /// 宿主没有才回落到设计数据。两者都没有时与直接绑定宿主数据完全一致。
    /// </summary>
    /// <param name="data">宿主提供的数据源，可为 null。</param>
    /// <param name="designData">设计态数据（路径 → 样例值），可为 null。</param>
    /// <returns>可直接传给 <see cref="BindText"/> 的绑定源。</returns>
    public static object? WithDesignData(object? data, object? designData)
    {
        if (designData is not IDictionary<string, object?> fallback || fallback.Count == 0)
            return data;

        if (data is null)
            return fallback;

        return new FallbackDataProvider(data, fallback);
    }

    sealed class FallbackDataProvider(object primary, IDictionary<string, object?> fallback) : IDictionaryProvider
    {
        public bool TryGetMember(string name, out object? value)
        {
            // 宿主字段（含值为 null 的字段）优先；只有整个路径不存在才看设计数据。
            if (TryResolve(primary, name, out value))
                return true;

            return fallback.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// 绑定值 → 打印文本。<c>null</c> 与 <see cref="DBNull"/> 一律空串；
    /// <c>IFormattable</c> 用**宿主当前文化**格式化——面单上的日期与金额是给本地操作员看的，
    /// 不是给机器解析的，所以这里不能强推不变文化。
    /// </summary>
    public static string ToString(object? value)
        => value switch
        {
            null or DBNull => string.Empty,
            // 数字保留 JSON 原文："1.50" 不能变成 "1.5"，金额尾零是有意义的；对象/数组打印原始 JSON
            JsonElement json => json.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.String => json.GetString() ?? string.Empty,
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => json.GetRawText(),
            },
            // JsonNode 口径与 JsonElement 对齐：数字/对象/数组走 ToJsonString() 保原文，布尔按 .NET 口径
            JsonNode node => node.GetValueKind() switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.String => node is JsonValue text && text.TryGetValue<string>(out var s)
                    ? s ?? string.Empty
                    : node.ToJsonString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => node.ToJsonString(),
            },
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
            _ => value.ToString() ?? string.Empty,
        };
}

/// <summary>
/// 宿主想自定义取值口径（如 <c>JsonElement</c>、<c>ExpandoObject</c> 之外的私有结构）时实现它。
/// 绑定路径解析到对象实现本接口时，优先走接口而不是反射。
/// </summary>
public interface IDictionaryProvider
{
    /// <summary>按段名取值；不存在返回 false。</summary>
    /// <param name="name">路径段名。</param>
    /// <param name="value">取到的值。</param>
    bool TryGetMember(string name, out object? value);
}
