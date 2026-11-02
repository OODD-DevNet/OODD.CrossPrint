using System.Globalization;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 一条可编辑属性的自描述。宿主把 <see cref="ElementProperties.Descriptors"/> 直接铺成表单，
/// 每个描述符对应一个编辑控件：<see cref="Key"/> 是控件与模型之间的唯一地址，
/// <see cref="DisplayName"/> 是控件标签，<see cref="Kind"/> 决定控件形态，
/// <see cref="Options"/>/<see cref="Min"/>/<see cref="Max"/>/<see cref="Step"/> 是取值域提示。
/// </summary>
/// <remarks>
/// <para>
/// <b>Key 就是模板 JSON 的字段路径</b>（camelCase，见 docs/template-schema.md）：载荷字段带类型前缀
/// （<c>text.fontSizePt</c>、<c>qrcode.quietZoneMm</c>），公共几何字段不带前缀（<c>xMm</c>、<c>zIndex</c>），
/// 表格列按下标展开（<c>table.columns[0].title</c>）。键与序列化字段一一对应由
/// <c>ElementPropertiesTests</c> 的反射完备性门守住——属性面板能不能编辑某个字段，
/// 取决于这里有没有它，所以"漏一条"等于"宿主物理上编辑不到"，是必须被测试拦住的失效模式。
/// </para>
/// <para>
/// <see cref="Min"/>/<see cref="Max"/>/<see cref="Step"/> 只是<b>编辑器提示</b>：
/// <see cref="SetValue"/> 不夹紧、不校验范围（夹紧是 <see cref="EditorKernel"/> 的职责，
/// 属性通道再夹一次就会出现"面板写的值和撤销栈记的值不一样"的双重真源）。
/// 范围外的值照样落进模型，渲染期由引擎按既有规则降级。
/// </para>
/// </remarks>
public sealed class PropertyDescriptor
{
    internal PropertyDescriptor(
        string key,
        string displayName,
        PropertyKind kind,
        Func<PaperElement, object?> getValue,
        Action<PaperElement, object?> setValue,
        IReadOnlyList<string>? options = null,
        double? min = null,
        double? max = null,
        double? step = null,
        bool readOnly = false)
    {
        Key = key;
        DisplayName = displayName;
        Kind = kind;
        GetValue = getValue;
        SetValue = setValue;
        Options = options;
        Min = min;
        Max = max;
        Step = step;
        ReadOnly = readOnly;
        Group = PropertyGroups.For(key);
    }

    /// <summary>模板 JSON 字段路径，如 <c>text.fontSizePt</c>。<see cref="ElementProperties.Find"/> 按它精确查找。</summary>
    public string Key { get; }

    /// <summary>中文标签，直接当控件标题用。</summary>
    public string DisplayName { get; }

    /// <summary>编辑器种类。</summary>
    public PropertyKind Kind { get; }

    /// <summary>表单分组标题（如"位置与尺寸""外观""文字"）。宿主按它插入分组头，不必自己猜键的语义。</summary>
    public string Group { get; }

    /// <summary>枚举候选项：<b>CLR 成员名原样</b>（<c>"Ean13"</c> 而非 <c>"EAN13"</c>，见 docs/template-schema.md §4.6）。
    /// 非枚举描述符为 null。</summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>数值下限提示（null 表示无下限）。不参与写入校验。</summary>
    public double? Min { get; }

    /// <summary>数值上限提示（null 表示无上限）。不参与写入校验。</summary>
    public double? Max { get; }

    /// <summary>步进提示（宿主滑块/上下箭头用）。不参与写入校验。</summary>
    public double? Step { get; }

    /// <summary>只读（如 <c>id</c>：身份由内核生成，宿主应展示但禁用编辑）。为 true 时 <see cref="SetValue"/> 必抛</summary>
    public bool ReadOnly { get; }

    /// <summary>读当前值。装箱返回：<see cref="PropertyKind.Number"/> 给 <c>double</c>、
    /// <see cref="PropertyKind.Color"/> 给 <c>SKColor</c>（可空字段可能为 null）、
    /// <see cref="PropertyKind.Enum"/> 给枚举值本身、<see cref="PropertyKind.DashPattern"/> 给 <c>double[]</c>。</summary>
    public Func<PaperElement, object?> GetValue { get; }

    /// <summary>
    /// 写回模型。传 null 表示清空可空字段（<c>image.data</c>、<c>table.stripeColor</c>、
    /// <c>table.columns[i].fontSizePt</c> 等），对不可空字段传 null 抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <remarks>
    /// 宿主表单交回来的东西形状很松，所以数值描述符认 <c>int/long/float/double/decimal</c> 和可解析的字符串，
    /// 布尔认 <c>"true"/"false"</c>，颜色认 <c>SKColor</c> 和 <c>"#RRGGBB"/"#AARRGGBB"</c>，
    /// 虚线认 <c>double[]</c> 和 <c>"1.5,1"</c>。形状对不上时抛 <see cref="ArgumentException"/>，
    /// 消息里带上键名与实际收到的值——这是宿主接线写错（编程错误），不是用户模板数据非法，
    /// 按 docs/03-development.md §4 的异常语义直接抛，不静默吞掉（吞掉的后果是"改了没反应"，最难查）。
    /// </remarks>
    public Action<PaperElement, object?> SetValue { get; }
}

/// <summary>
/// 描述符分组：只由键推导，避免每条描述符都要重复写一遍分组名。
/// 宿主拿到分组后按顺序插入小标题即可，不需要认识任何具体字段。
/// </summary>
internal static class PropertyGroups
{
    public const string Basic = "基本";
    public const string Layout = "位置与尺寸";
    public const string Appearance = "外观";
    public const string Text = "文字";
    public const string Line = "线条";
    public const string Shape = "形状";
    public const string Image = "图片";
    public const string QrCode = "二维码";
    public const string Barcode = "一维码";
    public const string Table = "表格";
    public const string TableColumns = "表格列";
    public const string TableHeader = "表头";
    public const string TableConditional = "条件样式";
    public const string TableHighlights = "关键词样式";
    public const string TableRowSpans = "静态行合并";

    public static string For(string key) => key switch
    {
        "id" => Basic,
        "xMm" or "yMm" or "widthMm" or "heightMm" => Layout,
        "zIndex" => Basic,
        "backgroundColor" or "borderColor" or "borderWidthMm" or "borderDashPatternMm" or "borderSides" or "rotationDegrees" or "opacity" => Appearance,
        "styleId" => Appearance,
        _ when key.StartsWith("text.", StringComparison.Ordinal) => Text,
        _ when key.StartsWith("line.", StringComparison.Ordinal) => Line,
        _ when key.StartsWith("rectangle.", StringComparison.Ordinal) || key.StartsWith("ellipse.", StringComparison.Ordinal) => Shape,
        _ when key.StartsWith("image.", StringComparison.Ordinal) => Image,
        _ when key.StartsWith("qrcode.", StringComparison.Ordinal) => QrCode,
        _ when key.StartsWith("barcode.", StringComparison.Ordinal) => Barcode,
        _ when key.StartsWith("table.columns[", StringComparison.Ordinal) => TableColumns,
        _ when key.StartsWith("table.headerRows[", StringComparison.Ordinal) => TableHeader,
        _ when key.StartsWith("table.conditionalStyles[", StringComparison.Ordinal) => TableConditional,
        _ when key.StartsWith("table.highlights[", StringComparison.Ordinal) => TableHighlights,
        _ when key.StartsWith("table.rowSpans[", StringComparison.Ordinal) => TableRowSpans,
        _ when key.StartsWith("table.", StringComparison.Ordinal) => Table,
        _ => Basic,
    };
}

/// <summary>
/// 描述符取值整理。集中在这里的目的只有一个：八种类型的读写委托不许各自发明一套
/// "字符串转数字"的口径（否则 <c>"12,5"</c> 在某个中文区域的宿主里会当场炸出与模板无关的 bug）。
/// 所有解析一律 <see cref="CultureInfo.InvariantCulture"/>。
/// </summary>
internal static class PropertyValues
{
    public static double ToDouble(string key, object? value) => OptionalDouble(key, value, allowNull: false)!.Value;

    public static double? OptionalDouble(string key, object? value, bool allowNull)
    {
        if (value is null)
        {
            if (allowNull)
                return null;
            throw Bad(key, value, "数值（不可空）");
        }

        double parsed;
        switch (value)
        {
            case double d:
                parsed = d;
                break;
            case float f:
                parsed = f;
                break;
            case int i:
                parsed = i;
                break;
            case long l:
                parsed = l;
                break;
            case short s:
                parsed = s;
                break;
            case byte b:
                parsed = b;
                break;
            case decimal m:
                parsed = (double)m;
                break;
            case string text:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fromText))
                    throw Bad(key, value, "数值或可解析为数字的字符串");
                parsed = fromText;
                break;
            default:
                throw Bad(key, value, "数值或可解析为数字的字符串");
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            throw Bad(key, value, "有限数值（不接受 NaN/无穷）");

        return parsed;
    }

    public static int ToInt(string key, object? value)
    {
        var d = ToDouble(key, value);
        if (d != Math.Truncate(d))
            throw Bad(key, value, "整数");

        return (int)Math.Truncate(d);
    }

    public static bool ToBool(string key, object? value)
    {
        switch (value)
        {
            case bool b:
                return b;
            case string text:
                if (bool.TryParse(text.Trim(), out var parsed))
                    return parsed;
                throw Bad(key, value, "布尔或 \"true\"/\"false\"");
            default:
                throw Bad(key, value, "布尔或 \"true\"/\"false\"");
        }
    }

    /// <summary>可空布尔：null 就是清空（三态覆盖字段里 null 表示"不覆盖"）。</summary>
    public static bool? OptionalBool(string key, object? value) => value is null ? null : ToBool(key, value);

    public static string ToText(string key, object? value)
    {
        if (value is string s)
            return s;

        throw Bad(key, value, "字符串（不可空）");
    }

    public static string? OptionalText(string key, object? value) => value switch
    {
        null => null,
        string s => s,
        _ => throw Bad(key, value, "字符串或 null"),
    };

    public static SKColor ToColor(string key, object? value) => OptionalColor(key, value, allowNull: false)!.Value;

    public static SKColor? OptionalColor(string key, object? value, bool allowNull)
    {
        switch (value)
        {
            case null when allowNull:
                return null;
            case null:
                throw Bad(key, value, "颜色（不可空）");
            case SKColor c:
                return c;
        }

        if (value is not string text)
            throw Bad(key, value, "SKColor 或形如 #RRGGBB / #AARRGGBB 的颜色串");

        try
        {
            return SkColorJsonConverter.Parse(text, throwOnError: true, defaultValue: default);
        }
        catch (FormatException ex)
        {
            // 统一成 ArgumentException：宿主只该 catch 一种"接线错了"，
            // 但保留原文解释为什么这串不是颜色。
            throw Bad(key, value, $"合法颜色串（#RRGGBB 或 #AARRGGBB）——{ex.Message}");
        }
    }

    public static T ToEnum<T>(string key, object? value, IReadOnlyList<string> names) where T : struct, Enum
    {
        if (value is T direct)
            return direct;

        // 只认成员名原样、区分大小写：JsonStringEnumConverter 写的就是成员名，
        // 这里若放宽成大小写不敏感，通道接受的取值反而可能落不进 JSON。
        if (value is string name)
        {
            if (names.Contains(name, StringComparer.Ordinal))
                return (T)Enum.Parse(typeof(T), name);

            // [Flags] 允许宿主交回 "Top, Right" 这样的组合串（多选面板）。
            // 每个分段仍按上面的口径逐字校验，再交给 Enum.Parse 合成位组合；
            // 非 [Flags] 枚举走不到这里，组合串照样被下面的 Bad 拒绝。
            if (typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false) && TryParseFlags<T>(name, names, out var combined))
                return combined;
        }

        throw Bad(key, value, $"成员名之一：{string.Join(" / ", names)}");
    }

    static bool TryParseFlags<T>(string text, IReadOnlyList<string> names, out T parsed) where T : struct, Enum
    {
        parsed = default;
        var parts = text.Split(',');
        if (parts.Length < 2)
            return false;

        var trimmed = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0 || !names.Contains(part, StringComparer.Ordinal))
                return false;
            trimmed[i] = part;
        }

        parsed = (T)Enum.Parse(typeof(T), string.Join(", ", trimmed));
        return true;
    }

    public static double[]? ToDash(string key, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case double[] array:
                return Validate(key, value, array);
            case float[] floats:
                return Validate(key, value, floats.Select(d => (double)d).ToArray());
            case IEnumerable<double> items:
                return Validate(key, value, items.ToArray());
            case string text:
            {
                var parts = text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var values = new double[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                    values[i] = ToDouble(key, parts[i]);
                return values.Length == 0 ? null : Validate(key, value, values);
            }
        }

        throw Bad(key, value, "double[] 或形如 \"1.5,1\" 的段长串");

        // 段长 <= 0 不报错：引擎 Geometry.ApplyDash 已把退化段当实线处理，
        // 属性通道再加一条规则只会和渲染口径打架。
        static double[] Validate(string k, object? source, double[] values)
        {
            foreach (var v in values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v))
                    throw Bad(k, source, "有限的段长");
            }

            return values;
        }
    }

    static ArgumentException Bad(string key, object? value, string expected)
        => new($"属性 \"{key}\" 写入失败：期望{expected}，实际收到 {Describe(value)}。", paramName: key);

    static string Describe(object? value)
    {
        if (value is null)
            return "null";

        var text = value is string s ? s : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Length > 40)
            text = text[..40] + "…";

        return $"\"{text}\"（{value.GetType().Name}）";
    }
}
