using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SkiaSharp;

namespace CrossPrint.Core;

/// <summary>模板读取的容错策略。</summary>
public sealed class TemplateJsonLoadOptions
{
    /// <summary>遇到未注册元素类型时跳过并记警告，而不是抛 <see cref="UnknownElementTypeException"/>。</summary>
    public bool SkipUnknownElementTypes { get; set; }

    /// <summary>遇到缺必填字段的元素时跳过并记警告，而不是抛 <see cref="TemplateValidationException"/>。</summary>
    public bool SkipInvalidElements { get; set; }

    /// <summary>严格模式：任何非法都抛异常。</summary>
    public static TemplateJsonLoadOptions Strict { get; } = new();

    /// <summary>宽容模式：非法元素跳过并记入警告。</summary>
    public static TemplateJsonLoadOptions Tolerant { get; } = new()
    {
        SkipUnknownElementTypes = true,
        SkipInvalidElements = true,
    };
}

/// <summary>模板读取结果。</summary>
public sealed class TemplateLoadResult
{
    internal TemplateLoadResult(PrintTemplate template, List<string> warnings)
    {
        Template = template;
        Warnings = warnings;
    }

    /// <summary>解析得到的模板。</summary>
    public PrintTemplate Template { get; }

    /// <summary>被跳过的元素等非致命问题。</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// 模板 JSON 读写。信封（schemaVersion/paper/margins/elements 公共字段）显式遍历，
/// 仅元素载荷交给源生成器，全程无反射。
/// </summary>
public static class TemplateJson
{
    /// <summary>读写元素载荷共用的 JSON 选项（camelCase + 字符串枚举 + 十六进制颜色）。</summary>
    public static JsonSerializerOptions PayloadOptions { get; } = CreatePayloadOptions();

    static JsonSerializerOptions CreatePayloadOptions()
    {
        var options = new JsonSerializerOptions(PrintTemplateJsonContext.Default.Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new SkColorJsonConverter());
        return options;
    }

    // ---------------- 序列化 ----------------

    /// <summary>
    /// 序列化为 JSON 文本。空/重复的 id 按 <see cref="PrintTemplate.Add"/> 的规则补齐（元素 <c>el-N</c>、样式 <c>st-N</c>），
    /// 因此写出的文本一定能被严格模式读回，且「序列化 → 反序列化 → 序列化」结果一致；输入模板不会被修改。
    /// </summary>
    public static string Serialize(PrintTemplate template, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(template);

        using var ms = new MemoryStream();
        WriteTo(template, ms, indented);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>序列化写入流；id 补齐口径同 <see cref="Serialize"/>，同样不动输入模板。</summary>
    public static void WriteTo(PrintTemplate template, Stream stream, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented });
        WriteTemplate(writer, template);
        writer.Flush();
    }

    static void WriteTemplate(Utf8JsonWriter writer, PrintTemplate t)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", t.SchemaVersion);

        writer.WritePropertyName("paper");
        WritePaper(writer, t.Paper);

        writer.WritePropertyName("margins");
        WriteMargins(writer, t.Margins);

        // 写出前先按 Add 的规则把空/重复 id 补齐：写出去的文件必须严格模式读得回来。
        var styleIds = UniqueIds(t.Styles.Select(s => s.Id).ToList(), "st-");
        var elementIds = UniqueIds(t.Elements.Select(e => e.Id).ToList(), "el-");

        if (t.Styles.Count > 0)
        {
            writer.WriteStartArray("styles");
            for (var i = 0; i < t.Styles.Count; i++)
            {
                var style = t.Styles[i];
                // 只有需要改 id 时才克隆：序列化不该动用户的对象图（撤销栈还持有原对象）。
                if (style.Id != styleIds[i])
                {
                    var copy = style.Clone();
                    copy.Id = styleIds[i];
                    style = copy;
                }

                JsonSerializer.Serialize(writer, style, PayloadInfo<NamedStyle>());
            }

            writer.WriteEndArray();
        }

        if (t.DesignData.Count > 0)
        {
            writer.WriteStartObject("designData");
            foreach (var pair in t.DesignData)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                writer.WriteString(pair.Key, pair.Value ?? string.Empty);
            }

            writer.WriteEndObject();
        }

        writer.WriteStartArray("elements");
        for (var i = 0; i < t.Elements.Count; i++)
            WriteElement(writer, t.Elements[i], elementIds[i]);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// 与 <see cref="PrintTemplate.Add"/> 同一条补 id 规则：<c>prefix + (已有最大序号 + 1)</c> 递增，
    /// 第一个出现的 id 原样保留，空 id 与后来的重复 id 才重新分配。
    /// </summary>
    static List<string> UniqueIds(List<string> ids, string prefix)
    {
        var max = 0;
        foreach (var id in ids)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(id.AsSpan(prefix.Length), out var n)
                && n > max)
                max = n;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            var value = id;
            if (string.IsNullOrEmpty(value) || !used.Add(value))
            {
                do
                {
                    value = $"{prefix}{++max}";
                }
                while (!used.Add(value));
            }

            result.Add(value);
        }

        return result;
    }

    static void WritePaper(Utf8JsonWriter w, PaperSpec p)
    {
        w.WriteStartObject();
        if (p.Preset is not null)
            w.WriteString("preset", p.Preset);
        w.WriteNumber("widthMm", p.WidthMm);
        w.WriteNumber("heightMm", p.HeightMm);
        w.WriteString("orientation", p.Orientation.ToString());
        w.WriteEndObject();
    }

    static void WriteMargins(Utf8JsonWriter w, ThicknessMm m)
    {
        w.WriteStartObject();
        w.WriteNumber("top", m.Top);
        w.WriteNumber("right", m.Right);
        w.WriteNumber("bottom", m.Bottom);
        w.WriteNumber("left", m.Left);
        w.WriteEndObject();
    }

    static void WriteElement(Utf8JsonWriter w, PaperElement e, string id)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("type", e.TypeKey);
        w.WriteNumber("xMm", e.XMm);
        w.WriteNumber("yMm", e.YMm);
        w.WriteNumber("widthMm", e.WidthMm);
        w.WriteNumber("heightMm", e.HeightMm);
        w.WriteNumber("zIndex", e.ZIndex);

        WriteOptionalColor(w, "backgroundColor", e.BackgroundColor);
        WriteOptionalColor(w, "borderColor", e.BorderColor);
        w.WriteNumber("borderWidthMm", e.BorderWidthMm);
        w.WriteString("borderSides", e.BorderSides.ToString());
        WriteOptionalDash(w, "borderDashPatternMm", e.BorderDashPatternMm);
        w.WriteNumber("rotationDegrees", e.RotationDegrees);
        w.WriteNumber("opacity", e.Opacity);

        if (!string.IsNullOrEmpty(e.TestData))
            w.WriteString("testData", e.TestData);

        if (!string.IsNullOrEmpty(e.StyleId))
            w.WriteString("styleId", e.StyleId);

        if (!string.IsNullOrEmpty(e.Formatter))
            w.WriteString("formatter", e.Formatter);

        if (!string.IsNullOrEmpty(e.Styler))
            w.WriteString("styler", e.Styler);

        switch (e)
        {
            case TextElement x:
                WritePayload(w, "text", x.Text);
                break;
            case LineElement x:
                WritePayload(w, "line", x.Line);
                break;
            case RectangleElement x:
                WritePayload(w, "rectangle", x.Rectangle);
                break;
            case EllipseElement x:
                WritePayload(w, "ellipse", x.Ellipse);
                break;
            case ImageElement x:
                WritePayload(w, "image", x.Image);
                break;
            case QrCodeElement x:
                WritePayload(w, "qrcode", x.QrCode);
                break;
            case BarcodeElement x:
                WritePayload(w, "barcode", x.Barcode);
                break;
            case TableElement x:
                WritePayload(w, "table", x.Table);
                break;
            default:
                throw new TemplateValidationException(e.Id, "type", $"元素类型 {e.GetType().Name} 未由序列化器支持。");
        }

        w.WriteEndObject();
    }

    static void WritePayload<T>(Utf8JsonWriter w, string name, T payload)
    {
        // 走 PayloadOptions 解析出的 JsonTypeInfo：既保留十六进制颜色/字符串枚举转换器，
        // 又让裁剪器看得见类型（反射式泛型重载在 Trim/AOT 下会报 IL2026）。
        w.WritePropertyName(name);
        JsonSerializer.Serialize(w, payload, PayloadInfo<T>());
    }

    static void WriteOptionalColor(Utf8JsonWriter w, string name, SKColor? color)
    {
        if (color is { } value)
            w.WriteString(name, SkColorJsonConverter.ToHex(value));
    }

    static void WriteOptionalDash(Utf8JsonWriter w, string name, double[]? dashMm)
    {
        if (dashMm is null)
            return;

        w.WriteStartArray(name);
        foreach (var segment in dashMm)
            w.WriteNumberValue(segment);
        w.WriteEndArray();
    }

    static JsonTypeInfo<T> PayloadInfo<T>() => (JsonTypeInfo<T>)PayloadOptions.GetTypeInfo(typeof(T));

    // ---------------- 反序列化 ----------------

    /// <summary>从 JSON 文本加载模板。加载前先做旧版字段迁移（<c>rowFillRules</c> → <c>conditionalStyles</c>）。</summary>
    public static TemplateLoadResult Load(string json, TemplateJsonLoadOptions? loadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Load(JsonDocument.Parse(MigrateLegacyRowFillRules(json)), loadOptions);
    }

    /// <summary>从 JSON 流加载模板。迁移语义同字符串重载。</summary>
    public static TemplateLoadResult Load(Stream stream, TemplateJsonLoadOptions? loadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Load(reader.ReadToEnd(), loadOptions);
    }

    /// <summary>加载并只取模板对象（严格模式）。</summary>
    public static PrintTemplate Deserialize(string json) => Load(json, TemplateJsonLoadOptions.Strict).Template;

    /// <summary>
    /// 旧版迁移：把任意层级的 <c>rowFillRules: [{field, min, color}]</c> 原地改写成
    /// <c>conditionalStyles: [{field, operator: "GreaterThan", value: "&lt;min&gt;", backgroundColor: color}]</c>。
    /// 旧规则只是新条件样式的一个特例（大于 + 整行底色），转出的新模型语义与旧渲染完全一致。
    /// JSON 不含该字段或解析失败（留给标准解析路径报错）时原样返回。
    /// </summary>
    public static string MigrateLegacyRowFillRules(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.Contains("rowFillRules", StringComparison.Ordinal))
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json; // 非法 JSON 不在迁移层报错，让标准解析路径给出统一异常
        }

        if (root is null || MigrateRowFillRulesNode(root) == 0)
            return json;

        return root.ToJsonString();
    }

    static int MigrateRowFillRulesNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var count = 0;
                foreach (var prop in obj)
                    count += MigrateRowFillRulesNode(prop.Value);

                if (obj.TryGetPropertyValue("rowFillRules", out var rules) && rules is JsonArray arr)
                {
                    var converted = new JsonArray();
#pragma warning disable IL2026 // JsonArray.Add<T> 内部对非原始类型用 JsonSerializer 反射；此处入参全是原始类型样式字段（string/double/bool），安全
                    foreach (var rule in arr.OfType<JsonObject>())
                    {
                        var style = new JsonObject();
                        if (rule.TryGetPropertyValue("field", out var field))
                            style["field"] = field?.DeepClone();
                        style["operator"] = nameof(TableConditionOperator.GreaterThan);
                        if (rule.TryGetPropertyValue("min", out var min)
                            && min is JsonValue minVal
                            && minVal.TryGetValue<double>(out var threshold))
                            style["value"] = threshold.ToString(CultureInfo.InvariantCulture);
                        if (rule.TryGetPropertyValue("color", out var color))
                            style["backgroundColor"] = color?.DeepClone();
                        converted.Add(style);
                        count++;
                    }

                    obj.Remove("rowFillRules");
                    obj["conditionalStyles"] = converted;
                }

                return count;
            }
            case JsonArray array:
            {
                var count = 0;
                foreach (var item in array)
                    count += MigrateRowFillRulesNode(item);
                return count;
            }
            default:
                return 0;
        }
    }

    static TemplateLoadResult Load(JsonDocument doc, TemplateJsonLoadOptions? loadOptions)
    {
        using (doc)
        {
            var opt = loadOptions ?? TemplateJsonLoadOptions.Strict;
            var warnings = new List<string>();
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new TemplateValidationException(null, "root", $"模板根节点必须是 JSON 对象，实际为 {root.ValueKind}。");

            var template = new PrintTemplate();

            // schemaVersion（必填）
            if (!root.TryGetProperty("schemaVersion", out var schemaEl) || schemaEl.ValueKind != JsonValueKind.String)
                throw new TemplateValidationException(null, "schemaVersion", "模板必须声明 schemaVersion 以便后续版本迁移。");
            template.SchemaVersion = schemaEl.GetString()!.Trim();
            ValidateSchemaVersion(template.SchemaVersion);

            // paper（必填）；兼容历史字段名 page
            var paperEl = PickObject(root, "paper", "page")
                ?? throw new TemplateValidationException(null, "paper", "模板缺少 paper 对象。");
            template.Paper = ReadPaper(paperEl);

            // margins（可选，默认 10mm）；兼容 marginMm
            var marginsEl = PickObject(root, "margins", "marginMm");
            if (marginsEl is not null)
                template.Margins = ReadMargins(marginsEl.Value);

            // designData（可选）：设计态预览用的字段值
            if (root.TryGetProperty("designData", out var designEl) && designEl.ValueKind == JsonValueKind.Object)
                ReadDesignData(designEl, template.DesignData);

            // styles（可选）：模板级样式库
            if (root.TryGetProperty("styles", out var stylesEl) && stylesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in stylesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    template.Styles.Add(Read<NamedStyle>(item.GetRawText(), null, "styles"));
                }

                EnsureUniqueStyleIds(template, warnings);
            }

            // elements（可选）
            if (root.TryGetProperty("elements", out var elementsEl) && elementsEl.ValueKind != JsonValueKind.Null)
            {
                if (elementsEl.ValueKind != JsonValueKind.Array)
                    throw new TemplateValidationException(null, "elements", $"elements 必须是数组，实际为 {elementsEl.ValueKind}。");

                foreach (var el in elementsEl.EnumerateArray())
                {
                    PaperElement? element;
                    try
                    {
                        element = ReadElement(el, opt, warnings);
                    }
                    catch (TemplateValidationException ex) when (opt.SkipInvalidElements)
                    {
                        warnings.Add(ex.Message);
                        continue;
                    }

                    if (element is not null)
                        template.Elements.Add(element);
                }

                EnsureUniqueIds(template, opt, warnings);
            }

            return new TemplateLoadResult(template, warnings);
        }
    }

    static void ValidateSchemaVersion(string version)
    {
        if (!Version.TryParse(version.Trim(), out var parsed) || parsed is null)
            throw new TemplateValidationException(null, "schemaVersion", $"schemaVersion '{version}' 不是合法版本号（形如 0.1）。");

        // 只比较主/次版本，忽略 "0.1.0" 这类补零写法造成的 Build/Revision 差异。
        if (parsed.Major != Current.Major)
            throw new TemplateValidationException(null, "schemaVersion",
                $"模板主版本 {version} 与本实现 {TemplateSchema.CurrentVersion} 不兼容，需要模板迁移器。");

        if (parsed.Minor > Current.Minor)
            throw new TemplateValidationException(null, "schemaVersion",
                $"模板版本 {version} 高于本实现支持的 {TemplateSchema.CurrentVersion}，请升级 CrossPrint。");
    }

    static readonly Version Current = Version.Parse(TemplateSchema.CurrentVersion);

    static PaperSpec ReadPaper(JsonElement p)
    {
        var paper = new PaperSpec();
        foreach (var prop in p.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "preset":
                    paper.Preset = prop.Value.ValueKind == JsonValueKind.Null ? null : RequireString(prop, null);
                    break;
                case "widthMm":
                    paper.WidthMm = RequireDouble(prop, null);
                    break;
                case "heightMm":
                    paper.HeightMm = RequireDouble(prop, null);
                    break;
                case "orientation":
                    if (!Enum.TryParse<PaperOrientation>(RequireString(prop, null), ignoreCase: true, out var o))
                        throw new TemplateValidationException(null, "orientation", $"非法取值 '{prop.Value}'，期望 Portrait / Landscape。");
                    paper.Orientation = o;
                    break;
                default:
                    break; // 未知字段忽略，向前兼容
            }
        }

        if (paper.WidthMm <= 0)
            throw new TemplateValidationException(null, "paper.widthMm", "纸宽必须为正数（mm）。");

        // 预设名与显式宽高不一致时，显式宽高优先，但校验预设合法性
        if (paper.Preset is not null && paper.Preset != PaperTypePresets.Custom)
        {
            if (!PaperTypePresets.TryGet(paper.Preset, out _))
                throw new TemplateValidationException(null, "paper.preset",
                    $"未知纸张预设 '{paper.Preset}'。可用：{string.Join(", ", PaperTypePresets.Names)}。");
        }

        return paper;
    }

    static ThicknessMm ReadMargins(JsonElement m)
    {
        var t = new ThicknessMm();
        foreach (var prop in m.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "top": t.Top = RequireDouble(prop, null); break;
                case "right": t.Right = RequireDouble(prop, null); break;
                case "bottom": t.Bottom = RequireDouble(prop, null); break;
                case "left": t.Left = RequireDouble(prop, null); break;
                default: break;
            }
        }

        return t;
    }

    static void ReadDesignData(JsonElement el, Dictionary<string, string> target)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
                continue;

            // 设计数据只承载标量预览值；对象/数组/显式 null 不作为可绑定的字段值。
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            if (value is not null)
                target[prop.Name] = value;
        }
    }

    static PaperElement? ReadElement(JsonElement el, TemplateJsonLoadOptions opt, List<string> warnings)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new TemplateValidationException(null, "element", $"元素必须是 JSON 对象，实际为 {el.ValueKind}。");

        var id = ReadOptionalString(el, "id");
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            throw new TemplateValidationException(id, "type", "元素缺少 type 字段。");

        var typeKey = typeEl.GetString()!;
        var factory = ElementFactory.Find(typeKey);
        if (factory is null)
        {
            if (opt.SkipUnknownElementTypes)
            {
                warnings.Add($"元素 '{id ?? "(无 id)"}' 类型 '{typeKey}' 未注册，已跳过。");
                return null;
            }

            throw new UnknownElementTypeException(id, typeKey);
        }

        var element = factory();
        element.Id = id ?? string.Empty;

        ReadCommon(el, element, id, opt, warnings);

        // 载荷子对象（缺失即用默认值）
        if (el.TryGetProperty(typeKey, out var payload) && payload.ValueKind == JsonValueKind.Object)
            ApplyPayload(element, payload);
        else if (el.TryGetProperty(PayloadAlias(typeKey), out var alias) && alias.ValueKind == JsonValueKind.Object)
            ApplyPayload(element, alias);

        return element;
    }

    static void ReadCommon(JsonElement el, PaperElement element, string? id, TemplateJsonLoadOptions opt, List<string> warnings)
    {
        element.XMm = ReadRequiredDouble(el, "xMm", id, opt, warnings);
        element.YMm = ReadRequiredDouble(el, "yMm", id, opt, warnings);
        element.WidthMm = ReadRequiredDouble(el, "widthMm", id, opt, warnings);
        element.HeightMm = ReadRequiredDouble(el, "heightMm", id, opt, warnings);

        if (el.TryGetProperty("zIndex", out var z) && z.ValueKind == JsonValueKind.Number)
            element.ZIndex = z.GetInt32();

        element.BackgroundColor = ReadOptionalColor(el, "backgroundColor", id);
        element.BorderColor = ReadOptionalColor(el, "borderColor", id);
        element.BorderWidthMm = ReadOptionalDouble(el, "borderWidthMm", element.BorderWidthMm);
        element.BorderSides = ReadOptionalBorderSide(el, "borderSides", id);
        element.BorderDashPatternMm = ReadOptionalDash(el, "borderDashPatternMm", id);
        element.RotationDegrees = ReadOptionalDouble(el, "rotationDegrees", element.RotationDegrees);
        element.Opacity = ReadOptionalDouble(el, "opacity", element.Opacity);
        element.TestData = ReadOptionalString(el, "testData");
        element.StyleId = ReadOptionalString(el, "styleId");
        element.Formatter = ReadOptionalString(el, "formatter");
        element.Styler = ReadOptionalString(el, "styler");

        if (element.WidthMm < 0 || element.HeightMm < 0)
            throw new TemplateValidationException(id, "widthMm/heightMm", "宽高不能为负。");
    }

    static double ReadOptionalDouble(JsonElement el, string name, double fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static SKColor? ReadOptionalColor(JsonElement el, string name, string? id)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;

        if (v.ValueKind != JsonValueKind.String)
            throw new TemplateValidationException(id, name, $"颜色必须是 #RRGGBB / #AARRGGBB 字符串或 null，实际为 {v.ValueKind}。");

        try
        {
            return SkColorJsonConverter.Parse(v.GetString(), throwOnError: true, defaultValue: default);
        }
        catch (FormatException ex)
        {
            throw new TemplateValidationException(id, name, ex.Message);
        }
    }

    static BorderSide ReadOptionalBorderSide(JsonElement el, string name, string? id)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return BorderSide.All;

        if (v.ValueKind != JsonValueKind.String)
            throw new TemplateValidationException(id, name, $"边框边集合必须是 \"Top, Right\" 形式的字符串，实际为 {v.ValueKind}。");

        if (!Enum.TryParse<BorderSide>(v.GetString(), ignoreCase: true, out var sides))
            throw new TemplateValidationException(id, name, $"非法边集合 '{v.GetString()}'，可用：{string.Join(" / ", Enum.GetNames<BorderSide>())}。");

        return sides;
    }

    static double[]? ReadOptionalDash(JsonElement el, string name, string? id)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;

        if (v.ValueKind != JsonValueKind.Array)
            throw new TemplateValidationException(id, name, $"虚线段长必须是数字数组或 null，实际为 {v.ValueKind}。");

        var values = new List<double>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
                throw new TemplateValidationException(id, name, "虚线段长数组里只能是数字。");
            values.Add(item.GetDouble());
        }

        return values.Count == 0 ? null : values.ToArray();
    }

    static double ReadRequiredDouble(JsonElement el, string name, string? id, TemplateJsonLoadOptions opt, List<string> warnings)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();

        if (!opt.SkipInvalidElements)
            throw new TemplateValidationException(id, name, "缺失必填几何字段或类型不是数字。");

        // 宽容模式保住元素内容，但几何兜 0 后它看不见也点不中：必须点名是哪个元素的哪个字段。
        warnings.Add($"元素 '{id ?? "(无 id)"}' 的几何字段 {name} 缺失或不是数字，已按 0 导入。");
        return 0;
    }

    static string PayloadAlias(string typeKey) => typeKey switch
    {
        ElementTypeKeys.QrCode => "qrCode",
        _ => typeKey,
    };

    static void ApplyPayload(PaperElement element, JsonElement payload)
    {
        var text = payload.GetRawText();
        switch (element)
        {
            case TextElement e:
                e.Text = Read<TextOptions>(text, e.Id, ElementTypeKeys.Text);
                break;
            case LineElement e:
                e.Line = Read<LineOptions>(text, e.Id, ElementTypeKeys.Line);
                break;
            case RectangleElement e:
                e.Rectangle = Read<ShapeOptions>(text, e.Id, ElementTypeKeys.Rectangle);
                break;
            case EllipseElement e:
                e.Ellipse = Read<ShapeOptions>(text, e.Id, ElementTypeKeys.Ellipse);
                break;
            case ImageElement e:
                e.Image = Read<ImageOptions>(text, e.Id, ElementTypeKeys.Image);
                break;
            case QrCodeElement e:
                e.QrCode = Read<QrCodeOptions>(text, e.Id, ElementTypeKeys.QrCode);
                break;
            case BarcodeElement e:
                e.Barcode = Read<BarcodeOptions>(text, e.Id, ElementTypeKeys.Barcode);
                break;
            case TableElement e:
                e.Table = Read<TableOptions>(text, e.Id, ElementTypeKeys.Table);
                break;
        }
    }

    static T Read<T>(string json, string? elementId, string fieldName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, PayloadInfo<T>())!;
        }
        catch (JsonException ex)
        {
            throw new TemplateValidationException(elementId, fieldName, ex.Message);
        }
    }

    static void EnsureUniqueIds(PrintTemplate template, TemplateJsonLoadOptions opt, List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in template.Elements)
        {
            if (string.IsNullOrEmpty(e.Id))
            {
                if (!opt.SkipInvalidElements)
                    throw new TemplateValidationException(null, "id", "元素缺少 id 字段。");

                e.Id = template.NextElementId();
                warnings.Add($"元素缺少 id，已自动分配 '{e.Id}'。");
            }

            if (seen.Add(e.Id))
                continue;

            if (!opt.SkipInvalidElements)
                throw new TemplateValidationException(e.Id, "id", "元素 id 在模板内重复。");

            var duplicated = e.Id;
            e.Id = template.NextElementId();
            warnings.Add($"元素 id '{duplicated}' 在模板内重复，已重新分配为 '{e.Id}'。");
            seen.Add(e.Id);
        }
    }

    /// <summary>
    /// 样式 id 的容错：元素 id 走严格/宽容两套规则，样式 id 只被元素引用（找不到就是"样式没生效"），
    /// 所以两种模式都补号并留警告，绝不因此拒绝整份模板。
    /// </summary>
    static void EnsureUniqueStyleIds(PrintTemplate template, List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in template.Styles)
        {
            if (string.IsNullOrEmpty(s.Id))
            {
                s.Id = template.NextStyleId();
                warnings.Add($"样式缺少 id，已自动分配 '{s.Id}'。");
            }

            if (seen.Add(s.Id))
                continue;

            var duplicated = s.Id;
            s.Id = template.NextStyleId();
            warnings.Add($"样式 id '{duplicated}' 在模板内重复，已重新分配为 '{s.Id}'。");
            seen.Add(s.Id);
        }
    }

    // ---------------- 取值辅助 ----------------

    static JsonElement? PickObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                return v;
        }

        return null;
    }

    static string? ReadOptionalString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string RequireString(JsonProperty prop, string? elementId)
    {
        if (prop.Value.ValueKind != JsonValueKind.String)
            throw new TemplateValidationException(elementId, prop.Name, $"期望字符串，实际为 {prop.Value.ValueKind}。");
        return prop.Value.GetString()!;
    }

    static double RequireDouble(JsonProperty prop, string? elementId)
    {
        if (prop.Value.ValueKind != JsonValueKind.Number)
            throw new TemplateValidationException(elementId, prop.Name, $"期望数字，实际为 {prop.Value.ValueKind}。");
        return prop.Value.GetDouble();
    }
}

/// <summary>元素类型键 → 构造工厂。未注册的类型键视为未知元素。</summary>
public static class ElementFactory
{
    static readonly Dictionary<string, Func<PaperElement>> _map = new(StringComparer.Ordinal)
    {
        [ElementTypeKeys.Text] = static () => new TextElement(),
        [ElementTypeKeys.Line] = static () => new LineElement(),
        [ElementTypeKeys.Rectangle] = static () => new RectangleElement(),
        [ElementTypeKeys.Ellipse] = static () => new EllipseElement(),
        [ElementTypeKeys.Image] = static () => new ImageElement(),
        [ElementTypeKeys.QrCode] = static () => new QrCodeElement(),
        [ElementTypeKeys.Barcode] = static () => new BarcodeElement(),
        [ElementTypeKeys.Table] = static () => new TableElement(),
    };

    /// <summary>查找工厂，未注册返回 null。</summary>
    public static Func<PaperElement>? Find(string typeKey) => _map.TryGetValue(typeKey, out var f) ? f : null;

    /// <summary>按类型键创建元素实例。</summary>
    public static PaperElement Create(string typeKey)
        => Find(typeKey)?.Invoke()
            ?? throw new UnknownElementTypeException(null, typeKey);
}
