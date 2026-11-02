using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CrossPrint.Core;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using SkiaSharp;
// 本仓根命名空间 CrossPrint.Engine 与 Jint 的 Engine 类同名，别名消歧
using JintEngine = Jint.Engine;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// formatter/styler 脚本执行器（Jint 内嵌 JS 解释器，hiprint 同款语义）。
/// <list type="bullet">
/// <item><description>纯沙箱：Jint 默认不开放任何 CLR 访问，脚本只能算数据、改样式，碰不到文件与网络。</description></item>
/// <item><description>约束：单次执行 250ms 超时 / 10 万语句上限 / 64 层递归上限，防死循环拖死打印现场。</description></item>
/// <item><description>降级：解析失败、执行异常、超时、返回 undefined 一律降级为"没有脚本"（原值原样式），
/// 并记 <see cref="RenderIssueKind.ScriptIssue"/> 警告，渲染永不中断。</description></item>
/// </list>
/// </summary>
public sealed class ScriptRunner : IScriptRunner
{
    const int TimeoutMs = 250;
    const int MaxStatements = 100_000;
    const int MaxRecursionDepth = 64;
    const int MaxConvertDepth = 32;

    /// <summary>模板里是否存在任何脚本（决定渲染入口要不要创建执行器；无脚本模板零开销）。</summary>
    public static bool HasScripts(PrintTemplate template)
        => template.Elements.Any(e =>
            !string.IsNullOrWhiteSpace(e.Formatter)
            || !string.IsNullOrWhiteSpace(e.Styler)
            || (e is TableElement table && table.Table.Columns.Any(c =>
                !string.IsNullOrWhiteSpace(c.Formatter) || !string.IsNullOrWhiteSpace(c.Styler))));

    /// <inheritdoc/>
    public object? RunFormatter(string source, object?[] args, string? elementId, RenderReport report)
    {
        var result = TryInvoke(source, args, elementId, report);
        if (result is null || result.IsNull() || result.IsUndefined())
            return null;

        return ToClr(result);
    }

    /// <inheritdoc/>
    public ScriptStyle? RunStyler(string source, object?[] args, string? elementId, RenderReport report)
    {
        var result = TryInvoke(source, args, elementId, report);
        if (result is null || result.IsNull() || result.IsUndefined())
            return null;

        var style = StyleParser.Parse(result);
        return style is not null && !StyleParser.IsEmpty(style) ? style : null;
    }

    JsValue? TryInvoke(string source, object?[]? args, string? elementId, RenderReport report)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        try
        {
            // 每次执行独立引擎实例：全局变量、原型篡改都无法影响下一次调用。
            using var engine = new JintEngine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromMilliseconds(TimeoutMs));
                options.MaxStatements(MaxStatements);
                options.LimitRecursion(MaxRecursionDepth);
            });

            var fn = engine.Evaluate($"(function(){{return ({source});}})()");
            if (fn is not Function function)
            {
                report.Add(elementId, RenderIssueKind.ScriptIssue,
                    "脚本源码没有求值出函数（应为 function(...) {...} 或箭头函数），已忽略。");
                return null;
            }

            var jsArgs = args is null
                ? Array.Empty<JsValue>()
                : args.Select(a => ToJs(engine, a, 0)).ToArray();
            return function.Call(JsValue.Undefined, jsArgs);
        }
        catch (Exception ex)
        {
            report.Add(elementId, RenderIssueKind.ScriptIssue,
                $"脚本执行失败已降级为没有脚本：{ex.GetType().Name}: {Summarize(ex.Message)}");
            return null;
        }
    }

    /// <summary>异常消息收敛到一行、限长：告警面板与打印日志都会展示它。</summary>
    static string Summarize(string message)
    {
        var text = message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    // ---------------- CLR → JsValue ----------------

    /// <summary>
    /// 数据源转 JS 值。<see cref="JsonElement"/>/字典/集合递归展开成纯 JS 对象/数组
    /// （脚本能自然点取 <c>templateData.order.no</c>）；其余 CLR 对象用 ObjectWrapper 只读包裹。
    /// </summary>
    static JsValue ToJs(JintEngine engine, object? value, int depth)
    {
        if (depth > MaxConvertDepth)
            return JsValue.Null;

        return value switch
        {
            null => JsValue.Null,
            JsonElement el => FromJson(engine, el, depth),
            bool b => b,
            string s => s,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToDouble(value),
            IDictionary<string, object?> dict => FromDict(engine, dict, depth),
            IDictionary dict => FromDict(engine, dict, depth),
            IEnumerable and not string => FromEnumerable(engine, (IEnumerable)value, depth),
            _ => JsValue.FromObject(engine, value),
        };
    }

    static JsValue FromJson(JintEngine engine, JsonElement el, int depth) => el.ValueKind switch
    {
        JsonValueKind.Object => FromDict(engine, el.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), depth),
        JsonValueKind.Array => FromEnumerable(engine, el.EnumerateArray(), depth),
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => JsBoolean.True,
        JsonValueKind.False => JsBoolean.False,
        _ => JsValue.Null,
    };

    static JsValue FromDict(JintEngine engine, IEnumerable<KeyValuePair<string, object?>> pairs, int depth)
    {
        var obj = engine.Evaluate("({})").AsObject();
        foreach (var pair in pairs)
            obj.Set(pair.Key, ToJs(engine, UnwrapJson(pair.Value), depth + 1));

        return obj;
    }

    static JsValue FromDict(JintEngine engine, IDictionary dict, int depth)
    {
        var obj = engine.Evaluate("({})").AsObject();
        foreach (DictionaryEntry entry in dict)
            obj.Set(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ToJs(engine, UnwrapJson(entry.Value), depth + 1));

        return obj;
    }

    static JsValue FromEnumerable(JintEngine engine, IEnumerable items, int depth)
    {
        var arr = engine.Evaluate("[]").AsArray();
        uint i = 0;
        foreach (var item in items)
            arr.Set(i++, ToJs(engine, UnwrapJson(item), depth + 1));

        return arr;
    }

    /// <summary>JsonElement 装在 object? 里再递归：装箱值直接走 ToJs 的 JsonElement 分支。</summary>
    static object? UnwrapJson(object? value) => value;

    // ---------------- JsValue → CLR ----------------

    static object? ToClr(JsValue value)
    {
        if (value.IsString())
            return value.AsString();
        if (value.IsBoolean())
            return value.AsBoolean();
        if (value.IsNumber())
            return value.AsNumber();
        if (value.IsNull() || value.IsUndefined())
            return null;

        return value.ToString(); // 对象/数组等罕见返回：转显示字符串
    }
}

/// <summary>渲染器接入脚本（formatter/styler）的公共钩子：参数拼装、值解析、styler 单次执行缓存。</summary>
internal static class ScriptSupport
{
    /// <summary>options/target 参数：hiprint 透传完整元素对象，v0.1 只给 {id, type} 简单对象。</summary>
    internal sealed record ScriptOptions(string Id, string Type);

    static readonly ConditionalWeakTable<RenderContext, Dictionary<PaperElement, ScriptStyle?>> _styleCache = new();

    /// <summary>元素的内容表达式（主绑定字段来源）：图片/线/形状没有 → null。</summary>
    public static string? ContentExpressionOf(PaperElement element) => element switch
    {
        TextElement t => t.Text.Content,
        QrCodeElement q => q.QrCode.Content,
        BarcodeElement b => b.Barcode.Content,
        _ => null,
    };

    /// <summary>元素级脚本参数：[value, options, target, templateData]（options/target 同一简单对象）。</summary>
    public static object?[] ElementArgs(PaperElement element, object? value, object? templateData)
    {
        var opt = new ScriptOptions(element.Id, element.TypeKey);
        return [value, opt, opt, templateData];
    }

    /// <summary>formatter 的 value：主绑定字段原始值；设计态解析不到时回落元素测试数据，让脚本效果画布可见。</summary>
    public static object? ResolveValue(RenderContext ctx, PaperElement element, string? expression)
    {
        if (expression is null || ctx.Scripts is null)
            return null;

        if (DataBinder.MainBoundValue(expression, ctx.Data) is { } value)
            return value;

        if (ctx.PreferTestData && !string.IsNullOrEmpty(element.TestData))
            return element.TestData;

        return null;
    }

    /// <summary>执行元素 formatter；返回 null（无脚本/降级/undefined）表示保留原内容。</summary>
    public static object? FormatterResult(RenderContext ctx, PaperElement element, string? expression = null)
    {
        if (ctx.Scripts is not { } scripts || string.IsNullOrWhiteSpace(element.Formatter))
            return null;

        var value = ResolveValue(ctx, element, expression);
        return scripts.RunFormatter(element.Formatter, ElementArgs(element, value, ctx.Data), element.Id, ctx.Report);
    }

    /// <summary>formatter 返回值替换显示文本。</summary>
    public static string FormatContent(RenderContext ctx, PaperElement element, string? expression, string bound)
    {
        var result = FormatterResult(ctx, element, expression);
        return result is null ? bound : DataBinder.ToString(result);
    }

    /// <summary>
    /// 执行元素 styler。每次渲染上下文内每个元素只执行一次（框底/边框与内容绘制共用结果），
    /// 设计器在同一上下文里反复重绘不重复跑脚本。
    /// 缓存键用元素引用而不是 Id：Id 冲突（导入/手工构造的模板）时不能让两个元素共用一次执行结果。
    /// </summary>
    public static ScriptStyle? StyleOf(RenderContext ctx, PaperElement element, string? expression = null)
    {
        if (ctx.Scripts is not { } scripts || string.IsNullOrWhiteSpace(element.Styler))
            return null;

        var cache = _styleCache.GetOrCreateValue(ctx);
        if (cache.TryGetValue(element, out var cached))
            return cached;

        var value = ResolveValue(ctx, element, expression);
        var style = scripts.RunStyler(element.Styler, ElementArgs(element, value, ctx.Data), element.Id, ctx.Report);
        cache[element] = style;
        return style;
    }

    /// <summary>表格列脚本参数：[value, row, index, options, templateData]（hiprint 列级签名）。</summary>
    public static object?[] ColumnArgs(object? value, object? row, int index, string elementId, object? templateData)
        => [value, row, index, new ScriptOptions(elementId, ElementTypeKeys.Table), templateData];

    /// <summary>执行表格列 formatter；返回 null 表示保留原单元格文本。</summary>
    public static object? ColumnFormatterResult(RenderContext ctx, TableColumn col, object? value, object? row, int index, string elementId)
    {
        if (ctx.Scripts is not { } scripts || string.IsNullOrWhiteSpace(col.Formatter))
            return null;

        return scripts.RunFormatter(col.Formatter, ColumnArgs(value, row, index, elementId, ctx.Data), elementId, ctx.Report);
    }

    /// <summary>执行表格列 styler；返回 null 表示不改样式。</summary>
    public static ScriptStyle? ColumnStyler(RenderContext ctx, TableColumn col, object? value, object? row, int index, string elementId)
    {
        if (ctx.Scripts is not { } scripts || string.IsNullOrWhiteSpace(col.Styler))
            return null;

        return scripts.RunStyler(col.Styler, ColumnArgs(value, row, index, elementId, ctx.Data), elementId, ctx.Report);
    }
}

/// <summary>styler 返回值 → <see cref="ScriptStyle"/> 解析：JS 样式对象（camelCase）与 CSS 字符串（kebab-case）双形态。</summary>
internal static class StyleParser
{
    /// <summary>解析 styler 返回值；形态不认识返回 null。未知字段/取值静默忽略（与 CSS 容错口径一致）。</summary>
    public static ScriptStyle? Parse(JsValue value)
    {
        if (value.IsString())
            return ParseCss(value.AsString());
        if (value.IsObject())
            return ParseObject(value.AsObject());

        return null;
    }

    /// <summary>是否一个字段都没解析出来（视为"没有脚本样式"）。</summary>
    public static bool IsEmpty(ScriptStyle s)
        => s.Color is null && s.BackgroundColor is null && s.FontSizePt is null && s.Bold is null
           && s.Italic is null && s.Underline is null && s.Strikethrough is null && s.LetterSpacingPt is null
           && s.LineSpacing is null && s.HorizontalAlign is null && s.VerticalAlign is null
           && s.PaddingLeftMm is null && s.PaddingTopMm is null && s.PaddingRightMm is null && s.PaddingBottomMm is null
           && s.BorderColor is null && s.BorderWidthMm is null && s.BorderDashPatternMm is null;

    /// <summary>CSS 字符串形态："color:red;background-color:#f5f5f5"。</summary>
    static ScriptStyle ParseCss(string css)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in css.Split(new [] {';'}, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = ToCamel(part[..colon].Trim());
            var value = part[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0 && !props.ContainsKey(key))
                props[key] = value;
        }

        return ParseDict(props);
    }

    /// <summary>对象形态：只按已知键取值（对象原型上可能混入奇怪成员，逐键 Get 最稳）。</summary>
    static ScriptStyle ParseObject(ObjectInstance obj)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KnownKeys)
        {
            var value = obj.Get(key);
            var text = value switch
            {
                _ when value.IsString() => value.AsString().Trim(),
                _ when value.IsNumber() => value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ when value.IsBoolean() => value.AsBoolean() ? "true" : "false",
                _ => null,
            };
            if (!string.IsNullOrEmpty(text))
                props[key] = text;
        }

        return ParseDict(props);
    }

    static readonly string[] KnownKeys =
    [
        "color", "backgroundColor", "fontSize", "fontWeight", "fontStyle", "textDecoration",
        "letterSpacing", "lineHeight", "textAlign", "verticalAlign",
        "padding", "paddingLeft", "paddingTop", "paddingRight", "paddingBottom",
        "borderColor", "borderWidth", "borderStyle",
    ];

    /// <summary>键名（已统一成 camelCase）→ 样式字段。</summary>
    static ScriptStyle ParseDict(Dictionary<string, string> props)
    {
        string? Get(string key) => props.TryGetValue(key, out var v) ? v : null;

        // text-decoration 是复合声明：underline / line-through 可同时出现；none 显式关闭两者
        bool? underline = null, strikethrough = null;
        var decoration = Get("textDecoration");
        if (decoration is not null)
        {
            if (decoration.IndexOf("underline", StringComparison.OrdinalIgnoreCase) >= 0)
                underline = true;
            if (decoration.IndexOf("line-through", StringComparison.OrdinalIgnoreCase) >= 0)
                strikethrough = true;
            if (decoration.Equals("none", StringComparison.OrdinalIgnoreCase))
                underline = strikethrough = false;
        }

        var padding = ParseShorthand4(Get("padding"));

        return new ScriptStyle
        {
            Color = ParseColor(Get("color")),
            BackgroundColor = ParseColor(Get("backgroundColor")),
            FontSizePt = ParseLengthPt(Get("fontSize")),
            Bold = ParseWeight(Get("fontWeight")),
            Italic = ParseItalic(Get("fontStyle")),
            Underline = underline,
            Strikethrough = strikethrough,
            LetterSpacingPt = ParseLengthPt(Get("letterSpacing")),
            LineSpacing = ParseMultiple(Get("lineHeight")),
            HorizontalAlign = ParseHAlign(Get("textAlign")),
            VerticalAlign = ParseVAlign(Get("verticalAlign")),
            PaddingTopMm = padding?[0] ?? ParseMm(Get("paddingTop")),
            PaddingRightMm = padding?[1] ?? ParseMm(Get("paddingRight")),
            PaddingBottomMm = padding?[2] ?? ParseMm(Get("paddingBottom")),
            PaddingLeftMm = padding?[3] ?? ParseMm(Get("paddingLeft")),
            BorderColor = ParseColor(Get("borderColor")),
            BorderWidthMm = ParseMm(Get("borderWidth")),
            BorderDashPatternMm = ParseBorderStyle(Get("borderStyle")),
        };
    }

    /// <summary>background-color → backgroundColor（CSS 键统一成 JS 形态）。</summary>
    static string ToCamel(string kebab)
    {
        var partsRaw = kebab.Split(new [] {'-'}, StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(partsRaw.Length);
        foreach (var p in partsRaw)
        {
            var t = p.Trim();
            if (t.Length > 0) parts.Add(t);
        }
        if (parts.Count == 0)
            return string.Empty;

        return parts[0] + string.Concat(parts.Skip(1).Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }

    static SKColor? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();

        // rgb()/rgba()：alpha 兼容 0..1 与 0..255 两种写法
        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = value[(value.IndexOf('(') + 1)..].TrimEnd(')');
            var partsRaw = inner.Split(',');
            var parts = new string[partsRaw.Length];
            for (var i = 0; i < partsRaw.Length; i++) parts[i] = partsRaw[i].Trim();
            if (parts.Length is 3 or 4
                && TryByte(parts[0], out var r) && TryByte(parts[1], out var g) && TryByte(parts[2], out var b))
            {
                var alpha = parts.Length == 4
                    ? (double.TryParse(parts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) && a <= 1
                        ? (byte)Math.Round(a * 255)
                        : (byte)255)
                    : (byte)255;
                return new SKColor(r, g, b, alpha);
            }

            return null;
        }

        if (NamedColors.TryGetValue(value.ToLowerInvariant(), out var named))
            return named;

        // #RGB / #RRGGBB / #AARRGGBB；#RGB 展开成 #RRGGBB
        if (value.StartsWith("#"))
        {
            var hex = value[1..];
            if (hex.Length == 3 && hex.All(Uri.IsHexDigit))
                hex = string.Concat(hex.Select(c => $"{c}{c}"));

            return SKColor.TryParse("#" + hex, out var color) ? color : null;
        }

        return SKColor.TryParse(value, out var plain) ? plain : null;
    }

    static bool TryByte(string text, out byte value)
        => byte.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);

    static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.Ordinal)
    {
        ["black"] = SKColors.Black, ["white"] = SKColors.White, ["red"] = SKColors.Red,
        ["lime"] = SKColors.Lime, ["green"] = SKColors.Green, ["blue"] = SKColors.Blue,
        ["yellow"] = SKColors.Yellow, ["cyan"] = SKColors.Cyan, ["aqua"] = SKColors.Cyan,
        ["magenta"] = SKColors.Magenta, ["fuchsia"] = SKColors.Magenta, ["silver"] = SKColors.Silver,
        ["gray"] = SKColors.Gray, ["grey"] = SKColors.Gray, ["maroon"] = SKColors.Maroon,
        ["olive"] = SKColors.Olive, ["navy"] = SKColors.Navy, ["teal"] = SKColors.Teal,
        ["purple"] = SKColors.Purple, ["orange"] = new SKColor(255, 165, 0),
        ["pink"] = new SKColor(255, 192, 203), ["brown"] = new SKColor(165, 42, 42),
        ["gold"] = new SKColor(255, 215, 0), ["transparent"] = new SKColor(0, 0, 0, 0),
    };

    /// <summary>长度 → 磅：px（含无单位数字按 px，96dpi）/ pt / mm。单位不认识返回 null。</summary>
    static double? ParseLengthPt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().ToLowerInvariant();
        if (TryLength(value, "pt", 1.0, out var pt)) return pt;
        if (TryLength(value, "px", 72.0 / 96, out var px)) return px;
        if (TryLength(value, "mm", 72.0 / 25.4, out var mm)) return mm;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var unitless))
            return unitless * 72.0 / 96;
        return null;
    }

    /// <summary>长度 → 毫米：mm / pt / px（含无单位数字按 px）。</summary>
    static double? ParseMm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().ToLowerInvariant();
        if (TryLength(value, "mm", 1.0, out var mm)) return mm;
        if (TryLength(value, "pt", 25.4 / 72, out var pt)) return pt;
        if (TryLength(value, "px", 25.4 / 96, out var px)) return px;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var unitless))
            return unitless * 25.4 / 96;
        return null;
    }

    static bool TryLength(string value, string unit, double factorPerUnit, out double result)
    {
        result = 0;
        if (!value.EndsWith(unit, StringComparison.Ordinal) || value.Length <= unit.Length)
            return false;

        var number = value[..^unit.Length].Trim();
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return false;

        result = Math.Max(0, amount * factorPerUnit);
        return true;
    }

    /// <summary>font-weight：bold/bolder → true，normal/lighter → false，数值 ≥700 → true。</summary>
    static bool? ParseWeight(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().ToLowerInvariant();
        if (value is "bold" or "bolder")
            return true;
        if (value is "normal" or "lighter")
            return false;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var weight))
            return weight >= 700;
        return null;
    }

    static bool? ParseItalic(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "italic" or "oblique" => true,
        "normal" => false,
        _ => null,
    };

    /// <summary>line-height：无单位倍数（CSS 推荐形态）。带单位的返回 null（v0.1 不换算）。</summary>
    static double? ParseMultiple(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var multiple))
            return null;

        return Math.Max(0.1, Math.Min(10, multiple));
    }

    static HorizontalAlign? ParseHAlign(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "left" => HorizontalAlign.Left,
        "center" => HorizontalAlign.Center,
        "right" => HorizontalAlign.Right,
        "justify" => HorizontalAlign.Justify,
        _ => null,
    };

    static VerticalAlign? ParseVAlign(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "top" => VerticalAlign.Top,
        "middle" or "center" => VerticalAlign.Middle,
        "bottom" => VerticalAlign.Bottom,
        _ => null,
    };

    /// <summary>CSS padding 简写 → [top, right, bottom, left]（mm）。</summary>
    static double[]? ParseShorthand4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var partsRaw = text.Split(new [] {' ', '\t'}, StringSplitOptions.RemoveEmptyEntries);
        var parts = new string[partsRaw.Length];
        for (var i = 0; i < partsRaw.Length; i++) parts[i] = partsRaw[i].Trim();
        var values = new double?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = ParseMm(parts[i]);
            if (values[i] is null)
                return null;
        }

        return values.Length switch
        {
            1 => new double[] { values[0]!.Value, values[0]!.Value, values[0]!.Value, values[0]!.Value },
            2 => new double[] { values[0]!.Value, values[1]!.Value, values[0]!.Value, values[1]!.Value },
            3 => new double[] { values[0]!.Value, values[1]!.Value, values[2]!.Value, values[1]!.Value },
            4 => new double[] { values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value },
            _ => null,
        };
    }

    /// <summary>border-style：dashed/dotted 映射虚线段长（mm 循环）；solid 不改虚线形态。</summary>
    static double[]? ParseBorderStyle(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "dashed" => [1.5, 1.0],
        "dotted" => [0.5, 0.5],
        _ => null,
    };
}
