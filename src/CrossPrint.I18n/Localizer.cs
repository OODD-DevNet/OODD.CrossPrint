using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace CrossPrint.I18n;

/// <summary>
/// 极简 JSON 内嵌资源本地化器：所有宿主 UI 通过 <see cref="T"/> 取当前 <see cref="CultureInfo.CurrentUICulture"/> 下的字符串。
/// 资源文件作为 EmbeddedResource 内嵌进本程序集，命名约定 <c>Strings.{culture}.json</c>。
/// 找不到请求 culture 时 fallback 到 en-US；再没有就原样返回 key。
/// 资源解析一次后缓存。
/// </summary>
public static class Localizer
{
    static readonly ConcurrentDictionary<string, Dictionary<string, string>> _cache = new();
    static readonly Assembly _asm = typeof(Localizer).Assembly;

    static Localizer()
    {
        // 预热 en-US 和 zh-CN，这两个是肯定有的。
        _ = GetTable("en-US");
        _ = GetTable("zh-CN");
    }

    /// <summary>
    /// 取当前 UI culture 下 <paramref name="key"/> 对应的字符串；缺参数时返回原文。
    /// 格式串参数用 <c>{0} {1}</c> 占位符，例如 <c>Localizer.T("Error.LoadFailed", ex.Message)</c>。
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var table = GetTable(CultureInfo.CurrentUICulture.Name);
        if (!table.TryGetValue(key, out var value))
            return args.Length == 0 ? key : string.Format(key, args);
        return args.Length == 0 ? value : string.Format(value, args);
    }

    /// <summary>取 en-US 对应项（用于 About 页同时展示英文/中文时）。</summary>
    public static string En(string key)
    {
        var t = GetTable("en-US");
        return t.TryGetValue(key, out var v) ? v : key;
    }

    /// <summary>取 zh-CN 对应项。</summary>
    public static string Zh(string key)
    {
        var t = GetTable("zh-CN");
        return t.TryGetValue(key, out var v) ? v : key;
    }

    /// <summary>
    /// 返回给定 culture 可用的完整翻译表，或 fallback 到 en-US。
    /// </summary>
    public static Dictionary<string, string> GetTable(string culture)
    {
        if (_cache.TryGetValue(culture, out var table)) return table;

        // 先精确匹配，再挑父 culture（例如 zh-CN → zh），最后兜底 en-US。
        var candidates = new List<string> { culture };
        if (culture.Contains('-'))
            candidates.Add(culture.Split('-')[0]);
        if (!candidates.Contains("en-US"))
            candidates.Add("en-US");
        candidates.Add("zh-CN"); // 最后兜底中文

        foreach (var candidate in candidates)
        {
            table = TryLoad(candidate);
            if (table is not null)
            {
                _cache[culture] = table;
                return table;
            }
        }

        // 全都找不到——返回空字典，让调用方看到 key 本身。
        _cache[culture] = [];
        return [];
    }

    static Dictionary<string, string>? TryLoad(string culture)
    {
        var resourceName = $"CrossPrint.I18n.Strings.{culture}.json";
        using var stream = _asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        try
        {
            var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString()!;
            }
            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>清空缓存（单元测试 / 热切换 culture 后调用）。</summary>
    public static void ClearCache() => _cache.Clear();
}
