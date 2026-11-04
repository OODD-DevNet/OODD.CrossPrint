using System.Text.Json;

namespace CrossPrint.Core.Hiprint;

/// <summary>
/// 插件自定义元素（hiprint 插件注册的 <c>printElementType</c>）导入时的上下文。
/// 宿主/插件按 <see cref="Type"/> 注册工厂，把不认识的自定义元素映射成内置元素。
/// </summary>
public sealed class HiprintCustomElementArgs
{
    /// <summary>hiprint 元素类型名（小写，来自 <c>printElementType.type</c>）。</summary>
    public required string Type { get; init; }

    /// <summary>导入器为这个元素分配的 Id。</summary>
    public required string ElementId { get; init; }

    /// <summary>元素在 hiprint JSON 里的路径，便于日志定位。</summary>
    public required string Path { get; init; }

    /// <summary>元素左边距（mm）。</summary>
    public required double XMm { get; init; }

    /// <summary>元素上边距（mm）。</summary>
    public required double YMm { get; init; }

    /// <summary>元素宽度（mm）。</summary>
    public required double WidthMm { get; init; }

    /// <summary>元素高度（mm）。</summary>
    public required double HeightMm { get; init; }

    /// <summary><c>options</c> 的只读副本（已脱离原文档，可安全缓存）。</summary>
    public required JsonElement Options { get; init; }

    /// <summary>读一个字符串选项（不存在/非字符串/空白返回 null）。</summary>
    public string? String(string name)
        => Options.ValueKind == JsonValueKind.Object
           && Options.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>读一个数值选项（字符串里的数字也认）。</summary>
    public double? Number(string name)
    {
        if (Options.ValueKind != JsonValueKind.Object || !Options.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>读一个布尔选项。</summary>
    public bool? Boolean(string name)
    {
        if (Options.ValueKind != JsonValueKind.Object || !Options.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>
/// 插件自定义元素的导入工厂注册表。没有注册的类型不会丢：导入器会画一个占位框（见
/// <c>HiprintTemplateImporter</c> 的未知元素分支），注册后即可映射成真正的内置元素。
/// </summary>
/// <remarks>
/// 与仓库里其它静态注册表一致：进程级、可重复注册（后到者覆盖）、<see cref="Reset"/> 清空。
/// 导入期间只读，注册应在导入前完成。
/// </remarks>
public static class HiprintCustomElementRegistry
{
    static readonly object Gate = new();
    static readonly Dictionary<string, Entry> Factories = new(StringComparer.OrdinalIgnoreCase);

    sealed record Entry(Func<HiprintCustomElementArgs, PaperElement?> Factory, string? DisplayName);

    /// <summary>已注册的类型名（快照）。</summary>
    public static IReadOnlyCollection<string> Types
    {
        get
        {
            lock (Gate)
                return Factories.Keys.ToArray();
        }
    }

    /// <summary>
    /// 注册一个类型名的导入工厂。<paramref name="factory"/> 返回 null 表示"放弃转换"，
    /// 导入器会退化成占位框（不会抛异常，也不会让元素消失）。
    /// </summary>
    /// <param name="type">hiprint 元素类型名（大小写不敏感）。</param>
    /// <param name="factory">元素工厂。</param>
    /// <param name="displayName">可选显示名，用于导入提示文案。</param>
    public static void Register(string type, Func<HiprintCustomElementArgs, PaperElement?> factory, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("类型名不能为空。", nameof(type));
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        lock (Gate)
            Factories[type.Trim()] = new Entry(factory, displayName);
    }

    /// <summary>注销一个类型名，返回是否存在。</summary>
    public static bool Unregister(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        lock (Gate)
            return Factories.Remove(type.Trim());
    }

    /// <summary>是否注册过该类型。</summary>
    public static bool Contains(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        lock (Gate)
            return Factories.ContainsKey(type.Trim());
    }

    /// <summary>取注册时给的显示名（未注册/未指定返回 null）。</summary>
    public static string? DisplayNameOf(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return null;

        lock (Gate)
            return Factories.TryGetValue(type.Trim(), out var entry) ? entry.DisplayName : null;
    }

    /// <summary>清空全部注册（测试收尾用）。</summary>
    public static void Reset()
    {
        lock (Gate)
            Factories.Clear();
    }

    /// <summary>调用注册的工厂；未注册、抛异常或返回 null 都算"没转到"。</summary>
    internal static bool TryCreate(HiprintCustomElementArgs args, out PaperElement? element)
    {
        element = null;

        Func<HiprintCustomElementArgs, PaperElement?>? factory;
        lock (Gate)
        {
            if (!Factories.TryGetValue(args.Type, out var entry))
                return false;
            factory = entry.Factory;
        }

        try
        {
            element = factory(args);
            return element is not null;
        }
        catch (Exception)
        {
            // 插件工厂是外部代码：它坏掉只影响这一个元素，不能拖挂整份模板的导入。
            return false;
        }
    }
}
