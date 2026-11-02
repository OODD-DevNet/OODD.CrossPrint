using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace CrossPrint.Core.Hiprint;

/// <summary>
/// hiprint（vue-plugin-hiprint）模板导入器：把 hiprint 设计器导出的 JSON / JS 对象字面量 / JS 模块
/// 映射成 CrossPrint 的 <see cref="PrintTemplate"/>。
/// </summary>
/// <remarks>
/// 映射口径（全部按 hiprint 公开配置与行为对齐）：
/// <list type="bullet">
/// <item><description>hiprint 的 <c>left/top/width/height</c> 是 pt（72dpi），这里统一换算成 mm。</description></item>
/// <item><description>面板 <c>width/height</c> 是 mm；<c>panelPageRule: "none"</c> 表示连续纸，导入为高度 0 的自适应纸张。</description></item>
/// <item><description><c>paperHeader/paperFooter</c> 是 pt 的可打印区边界，导入为留白提示（上/下）。</description></item>
/// <item><description>单行文本的 <c>field</c> 变成 <c>{{field}}</c> 占位符；<c>hideTitle</c> 为假时按 hiprint 规则拼出 <c>标题：{{field}}</c>。</description></item>
/// <item><description>表格 <c>columns</c> 是二维表头网格，按 colspan/rowspan 展开成列定义 + <see cref="TableHeaderCell"/> 列表。</description></item>
/// </list>
/// 无法等价表达的能力（JS 格式化函数、水印、页码、跨页长文本、HTML、单元格内嵌码等）不会静默丢弃，
/// 而是记入 <see cref="HiprintImportResult.Issues"/> 并尽量给出最接近的替代。
/// </remarks>
public static class HiprintTemplateImporter
{
    /// <summary>
    /// 判断文本是否能被 <see cref="Import"/> 认成 hiprint 模板（<c>getJson()</c> 输出、面板数组或 JS 模块）：
    /// 判据与 <see cref="Import"/> 同一个——归一化后必须真能取出至少一个面板对象。
    /// 宿主的"模板 JSON"输入框用它区分 CrossPrint 模板与 hiprint 模板。
    /// </summary>
    public static bool LooksLikeHiprintJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 归一化器同时接受严格 JSON 与 JS 对象字面量/模块；只认带 panels 的结构。
        if (!HiprintJsonNormalizer.TryExtractTemplate(text, out var json, out _))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            // 判据必须和 Import 完全一致（CollectPanels 非空），否则谓词说"是"、Import 却抛错，宿主两头不讨好。
            return CollectPanels(Unwrap(document.RootElement)).Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>导入 hiprint 模板文本。</summary>
    /// <param name="text">严格 JSON、<c>getJson()</c> 输出、JS 对象字面量，或含 <c>export const json = {...}</c> 的整个模块源码。</param>
    /// <param name="options">导入选项；null 用默认（导入第 0 个面板）。</param>
    /// <returns>导入结果（模板 + 问题清单 + 面板概要）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 null。</exception>
    /// <exception cref="HiprintImportException">文本不是可识别的 hiprint 模板。</exception>
    public static HiprintImportResult Import(string text, HiprintImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var opt = options ?? new HiprintImportOptions();

        if (!HiprintJsonNormalizer.TryExtractTemplate(text, out var json, out var error))
            throw new HiprintImportException(error ?? "无法识别 hiprint 模板。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HiprintImportException("hiprint 模板归一化后仍不是合法 JSON。", ex);
        }

        using (document)
        {
            var root = Unwrap(document.RootElement);
            var panels = CollectPanels(root);
            if (panels.Count == 0)
                throw new HiprintImportException("hiprint 模板里没有 panels 面板。");

            var context = new ImportContext();
            var selected = opt.PanelIndex;
            if (selected < 0 || selected >= panels.Count)
            {
                context.Issue(
                    HiprintImportSeverity.Info,
                    "hiprint.panel-index-clamped",
                    $"面板下标 {selected} 越界（共 {panels.Count} 个），已导入第 0 个。");
                selected = 0;
            }

            context.PanelIndex = selected;
            ImportPanel(context, panels[selected]);

            if (panels.Count > 1)
            {
                context.Issue(
                    HiprintImportSeverity.Info,
                    "hiprint.multi-panel",
                    $"模板含 {panels.Count} 个面板（多联单），本次只导入了第 {selected} 个；其余面板可用 PanelIndex 分别导入。");
            }

            var infos = new List<HiprintPanelInfo>(panels.Count);
            for (var i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                var size = PanelSize(panel);
                var count = panel.TryGetProperty("printElements", out var els) && els.ValueKind == JsonValueKind.Array
                    ? els.GetArrayLength()
                    : 0;
                infos.Add(new HiprintPanelInfo(i, Str(panel, "name"), size.WidthMm, size.HeightMm, count, i == selected));
            }

            return new HiprintImportResult(context.Template, context.Issues, infos);
        }
    }

    // ---------------- 面板 ----------------

    static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("json", out var inner)
            && inner.ValueKind == JsonValueKind.Object
            && inner.TryGetProperty("panels", out _))
            return inner;

        return root;
    }

    static List<JsonElement> CollectPanels(JsonElement root)
    {
        var list = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var panel in root.EnumerateArray())
            {
                if (panel.ValueKind == JsonValueKind.Object)
                    list.Add(panel);
            }

            return list;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("panels", out var panels)
            && panels.ValueKind == JsonValueKind.Array)
        {
            foreach (var panel in panels.EnumerateArray())
            {
                if (panel.ValueKind == JsonValueKind.Object)
                    list.Add(panel);
            }
        }

        return list;
    }

    static (double WidthMm, double HeightMm) PanelSize(JsonElement panel)
    {
        var width = Dbl(panel, "width") ?? 210;
        if (width <= 0)
            width = 210;

        var height = Dbl(panel, "height") ?? 297;
        var rule = (Str(panel, "panelPageRule") ?? "").Trim();
        var continuous = height <= 0 || rule.Equals("none", StringComparison.OrdinalIgnoreCase);
        return (width, continuous ? 0 : height);
    }

    static void ImportPanel(ImportContext ctx, JsonElement panel)
    {
        var (widthMm, heightMm) = PanelSize(panel);
        ctx.Template.Paper = BuildPaper(widthMm, heightMm);

        var rawHeightMm = Dbl(panel, "height") ?? 0;
        var headerMm = Math.Max(0, Pt(Dbl(panel, "paperHeader") ?? 0));
        var footerPt = Dbl(panel, "paperFooter") ?? 0;
        var bottomMm = footerPt > 0 && rawHeightMm > 0 ? Math.Max(0, rawHeightMm - Pt(footerPt)) : 0;
        ctx.Template.Margins = new ThicknessMm(headerMm, 0, bottomMm, 0);

        if (panel.TryGetProperty("watermarkOptions", out var watermark)
            && watermark.ValueKind == JsonValueKind.Object
            && watermark.EnumerateObject().Any())
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.watermark-unsupported",
                "hiprint 水印（watermarkOptions）没有对应能力，已忽略；如需水印请在打印前用背景图/自定义绘制实现。");
        }

        if (Has(panel, "paperNumberLeft") || Has(panel, "paperNumberTop") || Has(panel, "paperNumberFormat")
            || Has(panel, "paperNumberContinue") || Has(panel, "paperNumberDisabled"))
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.page-number-unsupported",
                "hiprint 页码（paperNumber*）没有对应能力，已忽略。");
        }

        if (Has(panel, "leftOffset") || Has(panel, "topOffset"))
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.panel-offset",
                "面板打印偏移（leftOffset/topOffset）没有对应能力，已忽略。");
        }

        if (panel.TryGetProperty("panelLayoutOptions", out var layout)
            && layout.ValueKind == JsonValueKind.Object
            && layout.EnumerateObject().Any())
        {
            ctx.Issue(
                HiprintImportSeverity.Info,
                "hiprint.panel-layout-options",
                "面板布局选项（panelLayoutOptions）已忽略。");
        }

        if (!panel.TryGetProperty("printElements", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.panel-no-elements", "面板缺少 printElements 数组，导入结果为空模板。");
            return;
        }

        var index = 0;
        foreach (var printElement in elements.EnumerateArray())
        {
            var path = $"panels[{ctx.PanelIndex}].printElements[{index}]";
            index++;

            if (printElement.ValueKind != JsonValueKind.Object)
            {
                ctx.Issue(HiprintImportSeverity.Warning, "hiprint.element-malformed", "printElements 里出现了非对象条目，已跳过。", path: path);
                continue;
            }

            var id = ctx.Template.NextElementId();
            var element = BuildElement(ctx, printElement, path, id);
            if (element is null)
                continue;

            element.Id = id;
            ctx.Template.Add(element);
        }
    }

    static PaperSpec BuildPaper(double widthMm, double heightMm)
    {
        if (heightMm <= 0)
        {
            foreach (var preset in (string[])[PaperTypePresets.Receipt80, PaperTypePresets.Receipt58])
            {
                if (PaperTypePresets.TryGet(preset, out var size) && Nearly(size.WidthMm, widthMm))
                    return new PaperSpec { Preset = preset, WidthMm = size.WidthMm, HeightMm = 0 };
            }

            return new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = widthMm, HeightMm = 0 };
        }

        foreach (var name in PaperTypePresets.Names)
        {
            if (name == PaperTypePresets.Custom || !PaperTypePresets.TryGet(name, out var size) || size.HeightMm <= 0)
                continue;

            if (Nearly(size.WidthMm, widthMm) && Nearly(size.HeightMm, heightMm))
                return new PaperSpec { Preset = name, WidthMm = size.WidthMm, HeightMm = size.HeightMm };

            if (Nearly(size.WidthMm, heightMm) && Nearly(size.HeightMm, widthMm))
            {
                return new PaperSpec
                {
                    Preset = name,
                    WidthMm = size.WidthMm,
                    HeightMm = size.HeightMm,
                    Orientation = PaperOrientation.Landscape,
                };
            }
        }

        return new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = widthMm, HeightMm = heightMm };
    }

    static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.6;

    // ---------------- 元素 ----------------

    static PaperElement? BuildElement(ImportContext ctx, JsonElement printElement, string path, string id)
    {
        if (!printElement.TryGetProperty("printElementType", out var typeElement) || typeElement.ValueKind != JsonValueKind.Object)
        {
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.element-malformed", "元素缺少 printElementType，已跳过。", id, path);
            return null;
        }

        if (!printElement.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object)
        {
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.element-malformed", "元素缺少 options 对象，已跳过。", id, path);
            return null;
        }

        var type = (Str(typeElement, "type") ?? "").Trim().ToLowerInvariant();
        var textType = (Str(options, "textType") ?? "").Trim().ToLowerInvariant();

        CaptureDesignData(ctx, options);

        return type switch
        {
            "text" when textType == "barcode" => BuildBarcode(ctx, options, path, id),
            "text" when textType == "qrcode" => BuildQrCode(ctx, options, path, id),
            "text" => BuildText(ctx, options, path, id, longText: false),
            "longtext" => BuildText(ctx, options, path, id, longText: true),
            "image" => BuildImage(ctx, options, path, id),
            "table" => BuildTable(ctx, options, path, id),
            "tablecustom" => BuildLegacyTable(ctx, options, path, id),
            "html" => BuildHtml(ctx, options, path, id),
            "hline" => BuildLine(ctx, options, path, id, horizontal: true),
            "vline" => BuildLine(ctx, options, path, id, horizontal: false),
            "rect" => BuildShape(ctx, options, path, id, oval: false),
            "oval" => BuildShape(ctx, options, path, id, oval: true),
            "barcode" => BuildBarcode(ctx, options, path, id),
            "qrcode" => BuildQrCode(ctx, options, path, id),
            "" when textType == "barcode" => BuildBarcode(ctx, options, path, id),
            "" when textType == "qrcode" => BuildQrCode(ctx, options, path, id),
            _ => UnknownElement(ctx, type, options, path, id),
        };
    }

    /// <summary>
    /// hiprint 旧版的 <c>tableCustom</c> 元素。新版已并入 <c>table</c>（设计器会提示替换），
    /// 但历史模板仍是这个类型，必须照常识别，否则整张表会被当成未知元素静默消失。
    /// </summary>
    static TableElement? BuildLegacyTable(ImportContext ctx, JsonElement options, string path, string id)
    {
        ctx.Issue(
            HiprintImportSeverity.Info,
            "hiprint.table-custom-legacy",
            "tableCustom 是 hiprint 旧版表格元素，已按 table 导入。",
            id,
            path);

        return BuildTable(ctx, options, path, id);
    }

    /// <summary>
    /// 把元素上的 <c>field + testData</c> 收集成模板的设计态预览数据。
    /// hiprint 设计视图就是拿 testData 画绑定字段的；丢掉它，预览里的条码会绑定成空串。
    /// </summary>
    static void CaptureDesignData(ImportContext ctx, JsonElement options)
    {
        var field = (Str(options, "field") ?? "").Trim();
        if (field.Length == 0)
            return;

        var testData = Str(options, "testData");
        if (string.IsNullOrWhiteSpace(testData))
            return;

        ctx.Template.DesignData.TryAdd(field, testData);
    }

    static PaperElement? UnknownElement(ImportContext ctx, string type, JsonElement options, string path, string id)
    {
        var label = type.Length == 0 ? "(空)" : type;
        ctx.Issue(
            HiprintImportSeverity.Warning,
            "hiprint.unknown-element-type",
            $"未知或自定义元素类型 '{label}'（通常来自 hiprint 插件），已跳过。可用内置类型：text/longText/image/table/html/hline/vline/rect/oval/barcode/qrcode。",
            id,
            path);

        if (options.ValueKind != JsonValueKind.Object)
            return null;

        return null;
    }

    static T ApplyCommon<T>(ImportContext ctx, T element, JsonElement options, string path, string typeKey, string id)
        where T : PaperElement
    {
        var defaults = DefaultsFor(typeKey);
        element.XMm = Pt(Dbl(options, "left") ?? 0);
        element.YMm = Pt(Dbl(options, "top") ?? 0);
        var widthPt = Dbl(options, "width") ?? defaults.WidthPt;
        var heightPt = Dbl(options, "height") ?? defaults.HeightPt;
        element.WidthMm = Math.Max(0, Pt(widthPt));
        element.HeightMm = Math.Max(0, Pt(heightPt));
        WarnDegenerateSize(ctx, options, path, id, "width", widthPt);
        WarnDegenerateSize(ctx, options, path, id, "height", heightPt);

        if (Dbl(options, "zIndex") is { } z)
            element.ZIndex = (int)Math.Round(z);

        element.RotationDegrees = ParseRotation(ctx, options, path, id);
        element.Formatter = ScriptOrNull(Str(options, "formatter"));
        element.Styler = ScriptOrNull(Str(options, "styler"));
        return element;
    }

    /// <summary>脚本字段归一化：空白视为没有脚本（模型约定 null = 无脚本）。</summary>
    static string? ScriptOrNull(string? script) => string.IsNullOrWhiteSpace(script) ? null : script;

    /// <summary>
    /// 非正宽高会被钳成 0mm：元素在设计器里点不中、打印时也不可见，但导入器不替用户编尺寸。
    /// 钳位照旧，只是必须说清是哪一维、原值是多少。
    /// </summary>
    static void WarnDegenerateSize(ImportContext ctx, JsonElement options, string path, string id, string field, double rawPt)
    {
        if (rawPt > 0 || !Has(options, field))
            return;

        var axis = field == "width" ? "宽" : "高";
        ctx.Issue(
            HiprintImportSeverity.Warning,
            "hiprint.element-degenerate-size",
            $"{field}: {rawPt.ToString(CultureInfo.InvariantCulture)}pt 不是正数，元素{axis}已钳成 0mm（设计器点不中、打印时不可见）；尺寸照 0 导入，不替你猜值。",
            id,
            path);
    }

    readonly record struct SizeDefaults(double WidthPt, double HeightPt);

    static SizeDefaults DefaultsFor(string typeKey) => typeKey switch
    {
        "text" or "longText" => new SizeDefaults(120, 9.75),
        "image" => new SizeDefaults(100, 100),
        "table" => new SizeDefaults(550, 42),
        "html" => new SizeDefaults(90, 90),
        "hline" => new SizeDefaults(90, 9),
        "vline" => new SizeDefaults(9, 90),
        "rect" or "oval" => new SizeDefaults(90, 90),
        "barcode" => new SizeDefaults(160, 40),
        "qrcode" => new SizeDefaults(80, 80),
        _ => new SizeDefaults(100, 20),
    };

    static readonly Regex _rotation = new(
        @"rotate\(\s*(-?\d+(?:\.\d+)?)\s*deg\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static double ParseRotation(ImportContext ctx, JsonElement options, string path, string id)
    {
        var transform = Str(options, "transform");
        if (string.IsNullOrWhiteSpace(transform))
            return 0;

        var match = _rotation.Match(transform);
        if (!match.Success)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.transform-unsupported",
                $"transform '{transform}' 不是 rotate(Ndeg)，已忽略。",
                id,
                path);
            return 0;
        }

        var residual = _rotation.Replace(transform, "").Trim().Trim(';', ' ');
        if (residual.Length > 0)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.transform-partial",
                $"transform '{transform}' 里除旋转外的部分已忽略。",
                id,
                path);
        }

        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // ---------------- 文本 ----------------

    static TextElement BuildText(ImportContext ctx, JsonElement options, string path, string id, bool longText)
    {
        var element = ApplyCommon(ctx, new TextElement(), options, path, longText ? "longText" : "text", id);
        var text = element.Text;

        element.BackgroundColor = OptionalColor(ctx, options, "backgroundColor", path, id);

        text.Content = BuildTextContent(options);
        text.FontFamily = FontFamilyOrDefault(Str(options, "fontFamily"));
        text.FontSizePt = Positive(Dbl(options, "fontSize")) ?? 9;
        text.Bold = IsBold(Str(options, "fontWeight"));
        text.Italic = string.Equals(Str(options, "fontStyle")?.Trim(), "italic", StringComparison.OrdinalIgnoreCase);
        text.Color = ColorOr(ctx, options, "color", SKColors.Black, path, id);
        text.VerticalAlign = ParseVertical(ctx, Str(options, "textContentVerticalAlign"), id, path);

        var align = ParseAlign(Str(options, "textAlign"), out var unknownAlign);
        if (align is { } horizontal)
            text.HorizontalAlign = horizontal;
        if (unknownAlign is not null)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.align-unknown", $"未知 textAlign '{unknownAlign}'，按左对齐处理。", id, path);

        var wrap = (Str(options, "textContentWrap") ?? "").Trim().ToLowerInvariant();
        switch (wrap)
        {
            case "nowrap":
            case "clip":
                text.Wrap = false;
                break;
            case "ellipsis":
                text.Wrap = false;
                text.Ellipsis = true;
                break;
            case "":
            case "default":
            case "wrap":
                text.Wrap = true;
                break;
            default:
                ctx.Issue(HiprintImportSeverity.Info, "hiprint.text-wrap-unknown", $"未知 textContentWrap '{wrap}'，按自动换行处理。", id, path);
                text.Wrap = true;
                break;
        }

        text.LineSpacing = LineSpacing(options, text.FontSizePt);
        text.LetterSpacingPt = Math.Max(0, Dbl(options, "letterSpacing") ?? 0);

        ApplyPadding(options, text);
        ApplyDecoration(ctx, options, text, path, id);
        WarnTextFeatures(ctx, options, path, id);
        ApplyBoxBorder(ctx, element, options, path, id, defaultWidthPt: 0);

        if (longText)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.long-text-pagination",
                "hiprint longText 会按页自动续排；CrossPrint 在元素框内渲染并由元素边界裁切，超长内容不会自动分页。",
                id,
                path);

            if (Positive(Dbl(options, "longTextIndent")) is not null)
                ctx.Issue(HiprintImportSeverity.Warning, "hiprint.long-text-indent", "longTextIndent 无对应属性，已忽略。", id, path);
        }

        return element;
    }

    static string BuildTextContent(JsonElement options)
    {
        var title = Str(options, "title") ?? "";
        var field = (Str(options, "field") ?? "").Trim();
        if (field.Length == 0)
            return title;

        var hideTitle = Bool(options, "hideTitle") ?? false;
        var prefix = hideTitle || title.Length == 0 ? string.Empty : title + "：";
        return prefix + "{{" + field + "}}";
    }

    static void ApplyPadding(JsonElement options, TextOptions text)
    {
        var hasLegacy = options.TryGetProperty("contentPadding", out var legacy) && legacy.ValueKind == JsonValueKind.Object;
        text.PaddingLeftMm = PaddingMm(options, hasLegacy ? legacy : null, "contentPaddingLeft", "left");
        text.PaddingTopMm = PaddingMm(options, hasLegacy ? legacy : null, "contentPaddingTop", "top");
        text.PaddingRightMm = PaddingMm(options, hasLegacy ? legacy : null, "contentPaddingRight", "right");
        text.PaddingBottomMm = PaddingMm(options, hasLegacy ? legacy : null, "contentPaddingBottom", "bottom");
    }

    static double PaddingMm(JsonElement options, JsonElement? legacy, string flatName, string nestedName)
    {
        var pt = Dbl(options, flatName);
        if (pt is null && legacy is { } nested)
            pt = Dbl(nested, nestedName);
        return Math.Max(0, Pt(pt ?? 0));
    }

    static void ApplyDecoration(ImportContext ctx, JsonElement options, TextOptions text, string path, string id)
    {
        var raw = Str(options, "textDecoration");
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var value = raw.ToLowerInvariant();
        text.Underline = value.Contains("underline", StringComparison.Ordinal);
        text.Strikethrough = value.Contains("line-through", StringComparison.Ordinal);

        if (value.Contains("overline", StringComparison.Ordinal))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.overline-unsupported", "textDecoration: overline 无对应样式，已忽略。", id, path);
    }

    static void WarnTextFeatures(ImportContext ctx, JsonElement options, string path, string id)
    {
        // formatter/styler 已在 ApplyCommon 保留入库（CrossPrint 内嵌 Jint 执行，语义与 hiprint 兼容），不再告警
        if (!string.IsNullOrWhiteSpace(Str(options, "dataType")) || !string.IsNullOrWhiteSpace(Str(options, "format")))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.data-format", "dataType/format 的日期、布尔格式化未实现，已按原始值打印。", id, path);

        if (Str(options, "upperCase") is { Length: > 0 } upper && upper != "0" && upper != "false")
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.upper-case", "upperCase（中文金额大写等）未实现，已按原值打印。", id, path);

        if (Bool(options, "pageBreak") == true)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.page-break", "pageBreak（随后续元素强制分页）未实现。", id, path);

        if (!string.IsNullOrWhiteSpace(Str(options, "showInPage")) || !string.IsNullOrWhiteSpace(Str(options, "unShowInPage")))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.page-filter", "showInPage/unShowInPage（按页选择性打印）未实现。", id, path);

        if (Bool(options, "fixed") == true)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.fixed-repeat", "fixed（每页重复打印该元素）未实现；元素只会出现一次。", id, path);
    }

    // ---------------- 图片 ----------------

    static ImageElement BuildImage(ImportContext ctx, JsonElement options, string path, string id)
    {
        var element = ApplyCommon(ctx, new ImageElement(), options, path, "image", id);
        element.BackgroundColor = OptionalColor(ctx, options, "backgroundColor", path, id);
        ApplyBoxBorder(ctx, element, options, path, id, defaultWidthPt: 0);

        var image = element.Image;
        var source = (Str(options, "src") ?? Str(options, "image") ?? "").Trim();

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma > 0 && source[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                image.SourceType = ImageSourceType.EmbeddedBase64;
                image.Data = source[(comma + 1)..].Trim();
            }
            else
            {
                ctx.Issue(HiprintImportSeverity.Warning, "hiprint.image-data-url", "图片 data URL 不是 base64 形式，无法内嵌，渲染为占位框。", id, path);
            }
        }
        else if (source.Length > 0)
        {
            image.SourceType = ImageSourceType.ResolverKey;
            image.ResolverKey = source;
            ctx.Issue(
                HiprintImportSeverity.Info,
                "hiprint.image-url",
                $"图片使用外链地址 '{Shorten(source)}'，需要宿主提供 IImageResolver 才能打印；否则渲染占位框。",
                id,
                path);
        }
        else
        {
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.image-missing", "图片元素没有 src，渲染为占位框。", id, path);
        }

        if ((Str(options, "field") ?? "").Trim().Length > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.image-field", "图片字段绑定（field）无对应数据通道，已忽略 field。", id, path);

        image.FitMode = ParseFit(ctx, Str(options, "fit"), path, id);
        image.CornerRadiusMm = Math.Max(0, Pt(Dbl(options, "borderRadius") ?? 0));
        return element;
    }

    static ImageFitMode ParseFit(ImportContext ctx, string? raw, string path, string id)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "contain":
                return ImageFitMode.Contain;
            case "cover":
                return ImageFitMode.Cover;
            case "fill":
                return ImageFitMode.Fill;
            case "none":
                return ImageFitMode.None;
            case "scale-down":
                ctx.Issue(HiprintImportSeverity.Info, "hiprint.image-fit", "fit: scale-down 按 contain 处理。", id, path);
                return ImageFitMode.Contain;
            default:
                ctx.Issue(HiprintImportSeverity.Warning, "hiprint.image-fit", $"未知 fit '{raw}'，按 contain 处理。", id, path);
                return ImageFitMode.Contain;
        }
    }

    // ---------------- 线段 / 形状 / HTML ----------------

    static LineElement BuildLine(ImportContext ctx, JsonElement options, string path, string id, bool horizontal)
    {
        var element = ApplyCommon(ctx, new LineElement(), options, path, horizontal ? "hline" : "vline", id);
        var line = element.Line;

        var widthPt = Dbl(options, "borderWidth") ?? 0.75;
        var style = (Str(options, "borderStyle") ?? "solid").Trim().ToLowerInvariant();

        line.Direction = horizontal ? LineDirection.Horizontal : LineDirection.Vertical;
        line.StrokeColor = ColorOr(ctx, options, "borderColor", SKColors.Black, path, id);
        line.StrokeWidthMm = Math.Max(0.05, Pt(widthPt <= 0 ? 0.75 : widthPt));
        line.DashPatternMm = DashPattern(style, line.StrokeWidthMm);

        if (style is not ("" or "solid" or "dashed" or "dotted")
            && !style.Contains("dash", StringComparison.Ordinal)
            && !style.Contains("dot", StringComparison.Ordinal))
        {
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.line-style", $"线型 '{style}' 无对应描边样式，按实线绘制。", id, path);
        }

        return element;
    }

    static PaperElement BuildShape(ImportContext ctx, JsonElement options, string path, string id, bool oval)
    {
        if (oval)
        {
            var ellipse = ApplyCommon(ctx, new EllipseElement(), options, path, "oval", id);
            FillShape(ctx, options, ellipse.Ellipse, path, id, oval: true);
            return ellipse;
        }

        var rectangle = ApplyCommon(ctx, new RectangleElement(), options, path, "rect", id);
        FillShape(ctx, options, rectangle.Rectangle, path, id, oval: false);
        return rectangle;
    }

    static void FillShape(ImportContext ctx, JsonElement options, ShapeOptions shape, string path, string id, bool oval)
    {
        var style = (Str(options, "borderStyle") ?? "solid").Trim().ToLowerInvariant();
        var widthPt = Dbl(options, "borderWidth") ?? 0.75;
        var visible = style.Length > 0 && style != "none" && widthPt > 0;

        shape.StrokeColor = visible ? ColorOr(ctx, options, "borderColor", SKColors.Black, path, id) : null;
        shape.StrokeWidthMm = visible ? Math.Max(0.05, Pt(widthPt)) : 0;
        shape.DashPatternMm = visible ? DashPattern(style, shape.StrokeWidthMm) : null;
        shape.FillColor = OptionalColor(ctx, options, "backgroundColor", path, id);
        shape.CornerRadiusMm = oval ? 0 : Math.Max(0, Pt(Dbl(options, "borderRadius") ?? 0));

        if (Has(options, "borderLeft") || Has(options, "borderTop") || Has(options, "borderRight") || Has(options, "borderBottom"))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.border-sides", "矩形/椭圆不支持分边线型，已按统一样式绘制。", id, path);
    }

    static RectangleElement BuildHtml(ImportContext ctx, JsonElement options, string path, string id)
    {
        var element = ApplyCommon(ctx, new RectangleElement(), options, path, "html", id);
        element.Rectangle.StrokeColor = new SKColor(176, 176, 176);
        element.Rectangle.StrokeWidthMm = 0.25;
        element.Rectangle.DashPatternMm = [2, 1];
        element.Rectangle.FillColor = null;

        ctx.Issue(
            HiprintImportSeverity.Warning,
            "hiprint.html-unsupported",
            "hiprint 的 html 元素依赖浏览器渲染 HTML/CSS，CrossPrint 用虚线占位框代替；请改用文本/图片/表格元素。",
            id,
            path);

        return element;
    }

    // ---------------- 条码 / 二维码 ----------------

    static BarcodeElement BuildBarcode(ImportContext ctx, JsonElement options, string path, string id)
    {
        var element = ApplyCommon(ctx, new BarcodeElement(), options, path, "barcode", id);
        var barcode = element.Barcode;

        var field = (Str(options, "field") ?? "").Trim();
        barcode.Content = field.Length > 0
            ? "{{" + field + "}}"
            : FirstNonEmpty(Str(options, "text"), Str(options, "testData"), Str(options, "title"));

        barcode.Symbology = ParseSymbology(ctx, Str(options, "barcodeType") ?? Str(options, "barcodeMode"), path, id);
        barcode.ShowText = !(Bool(options, "hideTitle") ?? false);
        barcode.FontFamily = FontFamilyOrDefault(Str(options, "fontFamily"));
        barcode.FontSizePt = Positive(Dbl(options, "fontSize")) ?? 7;
        barcode.BarColor = ColorOr(ctx, options, "barColor", ColorOr(ctx, options, "color", SKColors.Black, path, id), path, id);
        barcode.BackgroundColor = OptionalColor(ctx, options, "backgroundColor", path, id) ?? SKColors.White;
        barcode.QuietZoneMm = 0;

        if (Positive(Dbl(options, "barWidth")) is { } barWidth)
        {
            ctx.Issue(
                HiprintImportSeverity.Info,
                "hiprint.barcode-module-width",
                $"barWidth {barWidth.ToString(CultureInfo.InvariantCulture)}pt 无法按固定模块宽打印；条码会等比拉伸到元素宽度，仍可扫描。",
                id,
                path);
        }

        WarnTextFeatures(ctx, options, path, id);
        return element;
    }

    static QrCodeElement BuildQrCode(ImportContext ctx, JsonElement options, string path, string id)
    {
        var element = ApplyCommon(ctx, new QrCodeElement(), options, path, "qrcode", id);
        var qr = element.QrCode;

        var field = (Str(options, "field") ?? "").Trim();
        var title = Str(options, "title") ?? "";
        var text = Str(options, "text") ?? "";

        qr.Content = field.Length > 0
            ? "{{" + field + "}}"
            : FirstNonEmpty(text, Str(options, "testData"), title);

        var level = Dbl(options, "qrCodeLevel") ?? Dbl(options, "qrcodeLevel");
        WarnQrCodeLevel(ctx, options, level, path, id);
        qr.ErrorCorrection = ParseErrorCorrection(level);
        qr.ForegroundColor = ColorOr(ctx, options, "barColor", ColorOr(ctx, options, "color", SKColors.Black, path, id), path, id);
        qr.BackgroundColor = OptionalColor(ctx, options, "backgroundColor", path, id) ?? SKColors.White;
        qr.QuietZoneMm = 0;

        var hideTitle = Bool(options, "hideTitle") ?? false;
        qr.Caption = BuildQrCaption(field, title, text);
        qr.ShowCaption = !hideTitle && qr.Caption.Length > 0;
        qr.CaptionFontSizePt = Positive(Dbl(options, "fontSize")) ?? 9;
        qr.CaptionFontFamily = FontFamilyOrDefault(Str(options, "fontFamily"));
        qr.CaptionColor = ColorOr(ctx, options, "color", SKColors.Black, path, id);
        qr.CaptionLineSpacing = LineSpacing(options, qr.CaptionFontSizePt);

        var align = ParseAlign(Str(options, "textAlign"), out _);
        if (align is { } horizontal)
            qr.CaptionAlign = horizontal;

        WarnTextFeatures(ctx, options, path, id);
        return element;
    }

    static string BuildQrCaption(string field, string title, string text)
    {
        if (field.Length > 0)
            return title;
        if (title.Length == 0)
            return text;
        if (text.Length == 0)
            return title;
        return title + ":" + text;
    }

    static QrErrorCorrection ParseErrorCorrection(double? level) => (int?)level switch
    {
        1 => QrErrorCorrection.L,
        2 => QrErrorCorrection.H,
        3 => QrErrorCorrection.Q,
        _ => QrErrorCorrection.M,
    };

    /// <summary>
    /// hiprint 的 qrCodeLevel 只会写 0..3（数字串也算，实测 Dbl 接得住）；写成别的取值时导入器只能按默认 M，
    /// 这一下替用户改了纠错能力，必须留痕。
    /// </summary>
    static void WarnQrCodeLevel(ImportContext ctx, JsonElement options, double? level, string path, string id)
    {
        if (!Has(options, "qrCodeLevel") && !Has(options, "qrcodeLevel"))
            return;

        if (level is 0 or 1 or 2 or 3)
            return;

        var raw = Str(options, "qrCodeLevel") ?? Str(options, "qrcodeLevel") ?? string.Empty;
        ctx.Issue(
            HiprintImportSeverity.Warning,
            "hiprint.qrcode-level-unknown",
            $"无法识别的 qrCodeLevel '{raw}'，按默认纠错等级 M 导入。",
            id,
            path);
    }

    static BarcodeSymbology ParseSymbology(ImportContext ctx, string? raw, string path, string id)
    {
        var key = (raw ?? "").Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        switch (key)
        {
            case "":
            case "code128":
                return BarcodeSymbology.Code128;
            case "ean13":
                return BarcodeSymbology.Ean13;
            case "ean8":
                return BarcodeSymbology.Ean8;
            case "upca":
            case "upc":
                return BarcodeSymbology.UpcA;
            case "upce":
                return BarcodeSymbology.UpcE;
            case "itf":
            case "itf14":
            case "interleaved2of5":
                return BarcodeSymbology.Itf;
            case "codabar":
                return BarcodeSymbology.Codabar;
            case "code39":
                return BarcodeSymbology.Code39;
            case "code93":
                return BarcodeSymbology.Code93;
            case "msi":
            case "msi10":
                return BarcodeSymbology.Msi;
            default:
                ctx.Issue(
                    HiprintImportSeverity.Warning,
                    "hiprint.barcode-type-unknown",
                    $"未知条码类型 '{raw}'，按 Code128 导入。",
                    id,
                    path);
                return BarcodeSymbology.Code128;
        }
    }

    // ---------------- 表格 ----------------

    sealed class HeaderCellSource
    {
        public JsonElement Element { get; init; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int RowSpan { get; set; } = 1;
        public int ColSpan { get; set; } = 1;
        public string Title { get; init; } = string.Empty;
        public string Field { get; init; } = string.Empty;
        public double? WidthPt { get; init; }
        public string? Align { get; init; }
        public string? HAlign { get; init; }
        public double? FontSizePt { get; init; }
        public string? TextType { get; init; }
        public string? Summary { get; init; }
        public bool? SummaryTitle { get; init; }
        public string? SummaryText { get; init; }
        public double? SummaryNumFormat { get; init; }
        public double? SummaryColspan { get; init; }
        public bool Hidden { get; init; }
        public string? Formatter { get; init; }
        public string? Styler { get; init; }
    }

    static TableElement? BuildTable(ImportContext ctx, JsonElement options, string path, string id)
    {
        var element = ApplyCommon(ctx, new TableElement(), options, path, "table", id);
        // 表级 formatter/styler 是作用于整表的回调（分组/行样式等），无法映射到具体列；
        // 留着只会变成属性面板里一段不生效的脚本，清掉，由 WarnTableFeatures 的降级告警交代。
        element.Formatter = null;
        element.Styler = null;
        var table = element.Table;

        var rows = ReadColumnGrid(ctx, options, path, id);
        if (rows.Count == 0)
            rows = GridFromFields(ctx, options, path, id);

        // 没有可用的列定义时不再丢弃整张表：形状（位置/尺寸/边框）是用户画出来的，
        // 丢掉就只剩一张空白页，现场根本不知道发生了什么。改成占位 1 列并留警告。
        if (!rows.Any(row => row.Any(cell => !cell.Hidden)))
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.table-no-columns",
                "表格没有可用的 columns/fields 列定义（缺失或全部隐藏），已按 1 列占位导入；请在属性面板补列。",
                id,
                path);

            rows = [[new HeaderCellSource { Title = "列1" }]];
        }

        var placed = PlaceGrid(rows, out var grid);
        if (placed.Count == 0)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.table-no-columns",
                "表格列定义展开后没有可见单元格，已按 1 列占位导入。",
                id,
                path);

            placed = PlaceGrid([[new HeaderCellSource { Title = "列1" }]], out grid);
        }

        var columnCount = grid.GetLength(1);

        var hidden = rows.Sum(row => row.Count(c => c.Hidden));
        if (hidden > 0)
            ctx.Issue(HiprintImportSeverity.Info, "hiprint.table-hidden-column", $"已忽略 {hidden} 个 checked=false 的隐藏表头单元格。", id, path);

        BuildColumns(ctx, options, element, grid, placed, path, id);
        BuildHeaderCells(ctx, table, placed);
        ApplyTableOptions(ctx, options, table, path, id);
        WarnTableFeatures(ctx, options, placed, path, id);

        return element;
    }

    static List<List<HeaderCellSource>> ReadColumnGrid(ImportContext ctx, JsonElement options, string path, string id)
    {
        var result = new List<List<HeaderCellSource>>();
        if (!options.TryGetProperty("columns", out var columns) || columns.ValueKind == JsonValueKind.Null)
            return result;

        // 个别导出器把 columns 写成"下标 → 单元格"的对象而不是二维数组
        if (columns.ValueKind == JsonValueKind.Object)
        {
            var isMap = columns.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Object);
            if (isMap)
            {
                var single = new List<HeaderCellSource>();
                foreach (var property in columns.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                        single.Add(ReadCell(property.Value));
                }

                if (single.Count > 0)
                    result.Add(single);
            }
            else
            {
                // 单个扁平列定义 { title, field, width }：当成一列，而不是被下标映射分支读空。
                result.Add([ReadCell(columns)]);
            }

            return result;
        }

        if (columns.ValueKind != JsonValueKind.Array)
            return result;

        var elements = columns.EnumerateArray().ToList();

        // 扁平列清单 [{title,field,width},…] 每个元素是一列，多行表头每个元素是一行（数组或"下标→列"对象）。
        // 形状只在整体判一次：按元素逐个决定会把扁平写法读成"一个对象一行"，最后只剩 1 列，设计器也就删不掉列。
        if (elements.Count > 0 && elements.All(e => e.ValueKind == JsonValueKind.Object && !IsHeaderRow(e)))
        {
            var flat = new List<HeaderCellSource>();
            var groups = new List<string>();
            CollectFlatColumns(elements, flat, groups);
            if (flat.Count == 0)
                return result;

            if (groups.Count > 0)
            {
                var names = string.Join("、", groups.Where(title => title.Length > 0));
                ctx.Issue(
                    HiprintImportSeverity.Info,
                    "hiprint.table-group-header",
                    $"分组表头{(names.Length > 0 ? $"「{names}」" : string.Empty)}已展开为子列并入同一行，单行表头无法保留分组标题。",
                    id,
                    path);
            }

            result.Add(flat);
            return result;
        }

        var droppedGroups = new List<string>();
        foreach (var rowElement in columns.EnumerateArray())
        {
            var row = new List<HeaderCellSource>();
            if (rowElement.ValueKind == JsonValueKind.Array)
            {
                // 行里可能包着分组对象 {title, columns:[…]}（hiprint 用它在二维表头里分组）。
                // 当成一个单元格读，分组下的子列会全丢、整表只剩 1 列，设计器连删列都删不了。
                CollectFlatColumns(rowElement.EnumerateArray(), row, droppedGroups);
            }
            else if (rowElement.ValueKind == JsonValueKind.Object)
            {
                // 走到这里说明外层是混合形态（既有行又有列）；下标映射行 { "0": {title…}, "1": {…} } 仍按一行读出全部列。
                if (IsHeaderRow(rowElement))
                {
                    foreach (var property in rowElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object)
                            CollectFlatColumns([property.Value], row, droppedGroups);
                    }
                }
                else
                {
                    CollectFlatColumns([rowElement], row, droppedGroups);
                }
            }

            result.Add(row);
        }

        // 整张表只记一条：分组标题是表头结构信息，逐行上报只会让问题清单被同一件事刷屏。
        if (droppedGroups.Count > 0)
        {
            var names = string.Join("、", droppedGroups.Where(title => title.Length > 0));
            ctx.Issue(
                HiprintImportSeverity.Info,
                "hiprint.table-group-header",
                $"分组表头{(names.Length > 0 ? $"「{names}」" : string.Empty)}已展开为子列并入所在表头行，当前表头模型无法保留分组标题。",
                id,
                path);
        }

        return result;
    }

    /// <summary>元素是否是"表头行"：属性值是列对象（下标 → 列）而不是标量。</summary>
    static bool IsHeaderRow(JsonElement element)
        => element.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Object);

    /// <summary>
    /// 把一段列定义递归展开成一个个单元格：扁平清单的每一列、二维表头行里的每个单元格都走这里。
    /// 分组对象 <c>{title, columns:[…]}</c> 只带子列，分组标题本身无法在现有表头模型里保留，交给调用方降级提示。
    /// </summary>
    static void CollectFlatColumns(IEnumerable<JsonElement> elements, List<HeaderCellSource> cells, List<string> droppedGroupTitles)
    {
        foreach (var element in elements)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                CollectFlatColumns(element.EnumerateArray(), cells, droppedGroupTitles);
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (element.TryGetProperty("columns", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                droppedGroupTitles.Add((Str(element, "title") ?? string.Empty).Trim());
                CollectFlatColumns(nested.EnumerateArray(), cells, droppedGroupTitles);
                continue;
            }

            cells.Add(ReadCell(element));
        }
    }

    static HeaderCellSource ReadCell(JsonElement cell) => new()
    {
        Element = cell,
        Title = Str(cell, "title") ?? string.Empty,
        Field = (Str(cell, "field") ?? string.Empty).Trim(),
        WidthPt = Dbl(cell, "width"),
        Align = Str(cell, "align"),
        HAlign = Str(cell, "halign"),
        FontSizePt = Positive(Dbl(cell, "fontSize")),
        TextType = Str(cell, "tableTextType"),
        Summary = Str(cell, "tableSummary"),
        SummaryTitle = Bool(cell, "tableSummaryTitle"),
        SummaryText = Str(cell, "tableSummaryText"),
        SummaryNumFormat = Dbl(cell, "tableSummaryNumFormat"),
        SummaryColspan = Dbl(cell, "tableSummaryColspan"),
        Formatter = ScriptOrNull(Str(cell, "formatter")),
        Styler = ScriptOrNull(Str(cell, "styler")),
        Hidden = Bool(cell, "checked") is false,
        RowSpan = Math.Max(1, (int)Math.Round(Dbl(cell, "rowspan") ?? 1)),
        ColSpan = Math.Max(1, (int)Math.Round(Dbl(cell, "colspan") ?? 1)),
    };

    static List<List<HeaderCellSource>> GridFromFields(ImportContext ctx, JsonElement options, string path, string id)
    {
        if (!options.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return [];

        var row = new List<HeaderCellSource>();
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                continue;

            row.Add(new HeaderCellSource
            {
                Element = field,
                Title = Str(field, "text") ?? Str(field, "title") ?? string.Empty,
                Field = (Str(field, "field") ?? string.Empty).Trim(),
                WidthPt = Dbl(field, "width"),
            });
        }

        if (row.Count > 0)
            ctx.Issue(HiprintImportSeverity.Info, "hiprint.table-from-fields", "表格没有 columns，已按 fields 生成单行列定义。", id, path);

        return row.Count == 0 ? [] : [row];
    }

    static List<HeaderCellSource> PlaceGrid(List<List<HeaderCellSource>> rows, out HeaderCellSource?[,] grid)
    {
        var rowCount = rows.Count;
        var columnCount = 0;
        foreach (var row in rows)
            columnCount = Math.Max(columnCount, row.Sum(c => c.Hidden ? 0 : c.ColSpan));
        columnCount = Math.Max(1, columnCount);

        grid = new HeaderCellSource?[rowCount, columnCount];
        var placed = new List<HeaderCellSource>();

        for (var r = 0; r < rowCount; r++)
        {
            var c = 0;
            foreach (var cell in rows[r])
            {
                if (cell.Hidden)
                    continue;

                while (c < columnCount && grid[r, c] is not null)
                    c++;
                if (c >= columnCount)
                    break;

                var columnSpan = Math.Clamp(cell.ColSpan, 1, columnCount - c);
                var rowSpan = Math.Clamp(cell.RowSpan, 1, rowCount - r);
                cell.Row = r;
                cell.Column = c;
                cell.ColSpan = columnSpan;
                cell.RowSpan = rowSpan;

                for (var rr = r; rr < r + rowSpan; rr++)
                {
                    for (var cc = c; cc < c + columnSpan; cc++)
                    {
                        if (grid[rr, cc] is null)
                            grid[rr, cc] = cell;
                    }
                }

                placed.Add(cell);
                c += columnSpan;
            }
        }

        return placed;
    }

    static void BuildColumns(
        ImportContext ctx,
        JsonElement options,
        TableElement element,
        HeaderCellSource?[,] grid,
        List<HeaderCellSource> placed,
        string path,
        string id)
    {
        var rowCount = grid.GetLength(0);
        var columnCount = grid.GetLength(1);
        var lastRow = rowCount - 1;
        var tableAlign = ParseAlign(Str(options, "textAlign"), out var unknownTableAlign);
        if (unknownTableAlign is not null)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.align-unknown", $"未知 textAlign '{unknownTableAlign}'，按左对齐处理。", id, path);

        var widths = new double?[columnCount];
        var unknownAligns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownSummaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cellTypes = 0;

        for (var c = 0; c < columnCount; c++)
        {
            var leaf = grid[lastRow, c];
            var title = string.Empty;
            var field = string.Empty;
            for (var r = lastRow; r >= 0; r--)
            {
                var cell = grid[r, c];
                if (cell is null)
                    continue;
                if (title.Length == 0)
                    title = cell.Title;
                if (field.Length == 0)
                    field = cell.Field;
            }

            var align = ParseAlign(leaf?.Align, out var unknownAlign);
            if (unknownAlign is not null)
                unknownAligns.Add(unknownAlign);
            align ??= tableAlign;

            var sequence = string.Equals(leaf?.TextType?.Trim(), "sequence", StringComparison.OrdinalIgnoreCase);
            var textType = (leaf?.TextType ?? string.Empty).Trim().ToLowerInvariant();
            if (!sequence && textType is "barcode" or "qrcode" or "image")
                cellTypes++;

            if (sequence)
                field = string.Empty;

            var summary = ParseSummary(leaf, unknownSummaries);

            element.Table.Columns.Add(new TableColumn
            {
                Title = title,
                Field = field,
                Align = align ?? HorizontalAlign.Left,
                FontSizePt = leaf?.FontSizePt,
                Sequence = sequence,
                Summary = summary.Kind,
                SummaryLabel = summary.Label,
                SummaryDecimals = summary.Decimals,
                Formatter = leaf?.Formatter,
                Styler = leaf?.Styler,
            });

            var widthPt = leaf?.WidthPt;
            if (widthPt is { } value)
                widths[c] = value / Math.Max(1, leaf!.ColSpan);
        }

        ScaleWidths(element.Table.Columns, widths, element.WidthMm);

        if (unknownAligns.Count > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.align-unknown", $"未知列对齐 {string.Join("、", unknownAligns)}，按左对齐处理。", id, path);

        if (unknownSummaries.Count > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.table-summary-unknown", $"未知 tableSummary {string.Join("、", unknownSummaries)}，按不汇总处理。", id, path);

        if (cellTypes > 0)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.table-cell-code",
                $"{cellTypes} 个列使用 tableTextType 内嵌条码/二维码/图片，CrossPrint 暂不支持单元格内嵌码，已按文本字段打印。",
                id,
                path);
        }
    }

    static void ScaleWidths(List<TableColumn> columns, double?[] widths, double elementWidthMm)
    {
        var explicitSum = 0.0;
        var autoCount = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            if (widths[i] is { } value)
                explicitSum += Pt(value);
            else
                autoCount++;
        }

        var target = elementWidthMm > 0 ? elementWidthMm : explicitSum;
        if (autoCount > 0)
        {
            var remaining = target - explicitSum;
            var autoMm = remaining > 0.1 ? remaining / autoCount : target / Math.Max(1, widths.Length);
            for (var i = 0; i < widths.Length; i++)
            {
                if (widths[i] is null)
                    widths[i] = UnitConverter.MmToPt(autoMm);
            }
        }

        var total = 0.0;
        for (var i = 0; i < widths.Length; i++)
            total += Pt(widths[i] ?? 0);

        if (total <= 0)
        {
            var even = target > 0 ? target / Math.Max(1, widths.Length) : 20;
            foreach (var column in columns)
                column.WidthMm = even;
            return;
        }

        var factor = target > 0 ? target / total : 1.0;
        for (var i = 0; i < columns.Count && i < widths.Length; i++)
            columns[i].WidthMm = Math.Max(0.1, Pt(widths[i] ?? 0) * factor);
    }

    static (TableSummary Kind, string Label, int Decimals) ParseSummary(HeaderCellSource? cell, HashSet<string> unknown)
    {
        var raw = (cell?.Summary ?? string.Empty).Trim().ToLowerInvariant();
        var kind = raw switch
        {
            "" or "none" => TableSummary.None,
            "count" => TableSummary.Count,
            "sum" => TableSummary.Sum,
            "avg" or "average" => TableSummary.Average,
            "min" => TableSummary.Min,
            "max" => TableSummary.Max,
            "text" => TableSummary.Text,
            _ => TableSummary.None,
        };

        if (kind == TableSummary.None && raw.Length > 0)
            unknown.Add(raw);

        var showTitle = cell?.SummaryTitle ?? true;
        var label = kind switch
        {
            TableSummary.Count => showTitle ? "计数：" : string.Empty,
            TableSummary.Sum => showTitle ? "合计：" : string.Empty,
            TableSummary.Average => showTitle ? "平均值：" : string.Empty,
            TableSummary.Min => showTitle ? "最小值：" : string.Empty,
            TableSummary.Max => showTitle ? "最大值：" : string.Empty,
            TableSummary.Text => cell?.SummaryText ?? string.Empty,
            _ => string.Empty,
        };

        var decimals = (int)Math.Round(cell?.SummaryNumFormat ?? 2);
        decimals = Math.Clamp(decimals, 0, 6);
        return (kind, label, decimals);
    }

    static void BuildHeaderCells(ImportContext ctx, TableOptions table, List<HeaderCellSource> placed)
    {
        foreach (var cell in placed)
        {
            var align = ParseAlign(cell.HAlign, out _) ?? ParseAlign(cell.Align, out _) ?? HorizontalAlign.Center;
            table.HeaderRows.Add(new TableHeaderCell
            {
                Row = cell.Row,
                Column = cell.Column,
                Title = cell.Title,
                ColSpan = cell.ColSpan,
                RowSpan = cell.RowSpan,
                Align = align,
                FontSizePt = cell.FontSizePt,
            });
        }

        table.HeaderRows.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
    }

    static void ApplyTableOptions(ImportContext ctx, JsonElement options, TableOptions table, string path, string id)
    {
        table.Field = (Str(options, "field") ?? string.Empty).Trim();
        table.FontSizePt = Positive(Dbl(options, "fontSize")) ?? 9;
        table.FontFamily = FontFamilyOrDefault(Str(options, "fontFamily"));
        table.LineSpacing = LineSpacing(options, table.FontSizePt);
        table.HeaderFontSizePt = Positive(Dbl(options, "tableHeaderFontSize"));

        if (Positive(Dbl(options, "tableHeaderRowHeight")) is { } headerHeightPt)
            table.HeaderRowHeightMm = Pt(headerHeightPt);

        if (Positive(Dbl(options, "tableBodyRowHeight")) is { } bodyHeightPt)
            table.RowHeightMm = Pt(bodyHeightPt);

        table.HeaderBold = IsBoldWeight(Str(options, "tableHeaderFontWeight"), defaultBold: true);

        table.HeaderFillColor = Has(options, "tableHeaderBackground")
            ? OptionalColor(ctx, options, "tableHeaderBackground", path, id)
            : new SKColor(238, 238, 238);

        table.StripeColor = null;
        table.ShowHeader = true;
        table.CellPaddingMm = 1;

        ApplyGridBorders(ctx, options, table, path, id);
    }

    static void ApplyGridBorders(ImportContext ctx, JsonElement options, TableOptions table, string path, string id)
    {
        string[] keys =
        [
            "tableBorder",
            "tableHeaderBorder",
            "tableHeaderCellBorder",
            "tableBodyRowBorder",
            "tableBodyCellBorder",
            "tableFooterBorder",
            "tableFooterCellBorder",
        ];

        var present = new List<(string Key, string Value)>();
        foreach (var key in keys)
        {
            if (Str(options, key) is { } value)
                present.Add((key, value.Trim()));
        }

        if (present.Count == 0)
            return;

        var active = present.Where(p => p.Value.Length > 0 && !p.Value.Equals("noBorder", StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 0)
        {
            table.GridColor = null;
            table.ShowBorder = false;
            return;
        }

        if (active.Count < present.Count)
        {
            var disabled = present.Where(p => !active.Contains(p)).Select(p => p.Key);
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.table-border-partial",
                $"表格边框部分为 noBorder（{string.Join("、", disabled)}），CrossPrint 只有统一网格开关，已保留网格。",
                id,
                path);
        }

        if (TryParseCssBorder(active[0].Value, out var widthMm, out var color))
        {
            table.GridWidthMm = Math.Max(0.05, widthMm);
            if (color is { } parsed)
                table.GridColor = parsed;
        }

        table.ShowBorder = true;
    }

    static readonly Regex _cssLength = new(
        @"(\d+(?:\.\d+)?)\s*(px|pt|mm)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex _cssHexColor = new(
        @"#[0-9a-fA-F]{3,8}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static bool TryParseCssBorder(string css, out double widthMm, out SKColor? color)
    {
        widthMm = 0.15;
        color = null;
        if (string.IsNullOrWhiteSpace(css) || css.Equals("noBorder", StringComparison.OrdinalIgnoreCase))
            return false;

        var length = _cssLength.Match(css);
        if (length.Success && double.TryParse(length.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            widthMm = length.Groups[2].Value.ToLowerInvariant() switch
            {
                "pt" => Pt(value),
                "mm" => value,
                _ => Pt(value * 0.75),
            };
        }

        var hex = _cssHexColor.Match(css);
        if (hex.Success)
            color = CssColor(hex.Value);

        return true;
    }

    static void WarnTableFeatures(ImportContext ctx, JsonElement options, List<HeaderCellSource> placed, string path, string id)
    {
        var tableJs = new[]
        {
            "rowsColumnsMerge", "rowsColumnsMergeClean", "groupFieldsFormatter", "groupFormatter",
            "groupFooterFormatter", "gridColumnsFooterFormatter", "footerFormatter", "rowStyler", "styler", "formatter",
        }.Count(key => !string.IsNullOrWhiteSpace(Str(options, key)));

        if (tableJs > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.js-formatter", $"表格使用了 {tableJs} 个 JavaScript 回调（合并单元格/分组/格式化），无法执行，已按静态网格导入。", id, path);

        if (Has(options, "gridColumns"))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.table-grid-columns", "表格卡片网格（gridColumns）无对应能力，已忽略。", id, path);

        if (!string.IsNullOrWhiteSpace(Str(options, "maxRows")))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.table-max-rows", "maxRows 无对应属性，已忽略。", id, path);

        var columnJs = placed.Count(cell =>
            !string.IsNullOrWhiteSpace(Str(cell.Element, "renderFormatter"))
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "formatter2"))
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "styler2"))
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "stylerHeader"))
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "tableSummaryFormatter")));

        if (columnJs > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.js-formatter", $"{columnJs} 个列使用 JavaScript 渲染/样式回调，无法执行，已按纯文本单元格导入。", id, path);

        var columnFeatures = placed.Count(cell =>
            !string.IsNullOrWhiteSpace(Str(cell.Element, "upperCase"))
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "tableColumnHeight"))
            || Positive(Dbl(cell.Element, "paddingLeft")) is not null
            || Positive(Dbl(cell.Element, "paddingRight")) is not null
            || (Dbl(cell.Element, "tableSummaryColspan") ?? 1) > 1
            || !string.IsNullOrWhiteSpace(Str(cell.Element, "tableSummaryAlign"))
            || Bool(cell.Element, "showCodeTitle") == true);

        if (columnFeatures > 0)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.table-column-feature", $"{columnFeatures} 个列的单元格级特性（内边距/大写/行高/汇总跨列等）无对应属性，已按统一表格样式导入。", id, path);
    }

    // ---------------- 通用外观 ----------------

    static void ApplyBoxBorder(ImportContext ctx, PaperElement element, JsonElement options, string path, string id, double defaultWidthPt)
    {
        var color = ColorOr(ctx, options, "borderColor", SKColors.Black, path, id);
        var globalStyle = (Str(options, "borderStyle") ?? string.Empty).Trim().ToLowerInvariant();
        var widthPt = Dbl(options, "borderWidth");

        var hasSides = Has(options, "borderTop") || Has(options, "borderRight") || Has(options, "borderBottom") || Has(options, "borderLeft");
        var sides = BorderSide.None;
        var styles = new List<string>();

        if (hasSides)
        {
            foreach (var (name, side) in _sideMap)
            {
                var style = (Str(options, name) ?? string.Empty).Trim().ToLowerInvariant();
                if (style.Length == 0 || style == "none")
                    continue;
                sides |= side;
                styles.Add(style);
            }
        }

        var globalVisible = globalStyle.Length > 0 && globalStyle != "none";
        var width = widthPt ?? (sides != BorderSide.None || globalVisible ? defaultWidthPt : 0);
        if (width <= 0)
            return;

        if (sides == BorderSide.None)
        {
            if (!globalVisible)
                return;
            sides = BorderSide.All;
        }

        var effective = styles.Count > 0 ? styles[0] : globalStyle;
        if (styles.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            ctx.Issue(
                HiprintImportSeverity.Warning,
                "hiprint.border-mixed",
                $"四边使用了不同线型（{string.Join("、", styles.Distinct(StringComparer.OrdinalIgnoreCase))}），已按 '{effective}' 统一绘制。",
                id,
                path);
        }

        if (effective.Contains("double", StringComparison.Ordinal))
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.border-double", "borderStyle: double 无对应线型，按实线绘制。", id, path);

        element.BorderColor = color;
        element.BorderWidthMm = Pt(width);
        element.BorderSides = sides;
        element.BorderDashPatternMm = DashPattern(effective, element.BorderWidthMm);
    }

    static readonly (string Name, BorderSide Side)[] _sideMap =
    [
        ("borderTop", BorderSide.Top),
        ("borderRight", BorderSide.Right),
        ("borderBottom", BorderSide.Bottom),
        ("borderLeft", BorderSide.Left),
    ];

    static double[]? DashPattern(string style, double widthMm)
    {
        var width = Math.Max(0.1, widthMm);
        if (style.Contains("dash", StringComparison.Ordinal))
            return [width * 3, width * 3];
        if (style.Contains("dot", StringComparison.Ordinal))
            return [width, width];
        return null;
    }

    // ---------------- 取值辅助 ----------------

    static bool Has(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out _);

    static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    static double? Dbl(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
            return parsed;

        return null;
    }

    static bool? Bool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetDouble(out var number) && Math.Abs(number) > double.Epsilon,
            JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" or "" => false,
                _ => null,
            },
            _ => null,
        };
    }

    static double? Positive(double? value) => value is > 0 ? value : null;

    static double Pt(double pt) => UnitConverter.PtToMm(pt);

    // hiprint 的元素内联 font-family: inherit，真正落到页面默认字体；但模板的宽度是按
    // print-lock.css 的 SimSun 量出来的（ASCII 半角、CJK 全角）。实测同一元素框：
    // "2019年05月09日" 在 69pt 框里 SimSun 22.2mm 放得下、雅黑 24.42mm 会折行；
    // "NO" 在 24pt 框里 SimSun 6.35mm、雅黑 10.34mm。按 SimSun 导入才能保住设计意图。
    static string FontFamilyOrDefault(string? family)
        => string.IsNullOrWhiteSpace(family) ? "SimSun" : family.Trim();

    static double LineSpacing(JsonElement options, double fontSizePt)
    {
        var lineHeight = Positive(Dbl(options, "lineHeight")) ?? Positive(Dbl(options, "lHeight"));
        if (lineHeight is { } height && fontSizePt > 0)
            return height / fontSizePt;

        // 未写 lineHeight 时 print-lock.css 给的是固定 9.75pt（不随字号缩放）。
        // 实测 fs=13pt 的表头元素 computed line-height 仍是 13px=9.75pt。
        return fontSizePt > 0 ? 9.75 / fontSizePt : 1.2;
    }

    static bool IsBold(string? weight)
    {
        if (string.IsNullOrWhiteSpace(weight))
            return false;

        var value = weight.Trim().ToLowerInvariant();
        if (value is "bold" or "bolder")
            return true;
        if (value is "normal" or "lighter")
            return false;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric >= 600;
    }

    static bool IsBoldWeight(string? weight, bool defaultBold)
    {
        if (string.IsNullOrWhiteSpace(weight))
            return defaultBold;
        return IsBold(weight);
    }

    // hiprint 只在显式 middle/bottom 时才给内容 div 加居中/底对齐类，默认顶对齐（实测 DOM 无对齐类）。
    // 认不出的取值同样落顶对齐，但要留痕：那是模板写错了，不是 hiprint 的本意。
    static VerticalAlign ParseVertical(ImportContext ctx, string? raw, string id, string path)
    {
        var key = raw?.Trim().ToLowerInvariant();
        switch (key)
        {
            case null or "":
            case "top":
                return VerticalAlign.Top;
            case "middle":
                return VerticalAlign.Middle;
            case "bottom":
                return VerticalAlign.Bottom;
            default:
                ctx.Issue(
                    HiprintImportSeverity.Warning,
                    "hiprint.valign-unknown",
                    $"未知 textContentVerticalAlign '{key}'，按顶对齐处理。",
                    id,
                    path);
                return VerticalAlign.Top;
        }
    }

    static HorizontalAlign? ParseAlign(string? raw, out string? unknown)
    {
        unknown = null;
        var value = raw?.Trim().ToLowerInvariant();
        switch (value)
        {
            case null or "":
                return null;
            case "left" or "start":
                return HorizontalAlign.Left;
            case "center" or "middle":
                return HorizontalAlign.Center;
            case "right" or "end":
                return HorizontalAlign.Right;
            case "justify":
                return HorizontalAlign.Justify;
            default:
                unknown = value;
                return null;
        }
    }

    static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return string.Empty;
    }

    static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    static SKColor ColorOr(ImportContext ctx, JsonElement options, string name, SKColor fallback, string path, string id)
    {
        var raw = Str(options, name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var parsed = CssColor(raw);
        if (parsed is { } color)
            return color;

        ctx.Issue(HiprintImportSeverity.Warning, "hiprint.color-unparsed", $"无法解析颜色 '{raw}'（{name}），已用默认色。", id, path);
        return fallback;
    }

    static SKColor? OptionalColor(ImportContext ctx, JsonElement options, string name, string path, string id)
    {
        var raw = Str(options, name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parsed = CssColor(raw);
        if (parsed is null)
            ctx.Issue(HiprintImportSeverity.Warning, "hiprint.color-unparsed", $"无法解析颜色 '{raw}'（{name}），已按透明处理。", id, path);

        return parsed;
    }

    /// <summary>CSS 颜色：#RGB/#RGBA/#RRGGBB/#RRGGBBAA、rgb()/rgba()、常见颜色名。无法识别返回 null。</summary>
    static SKColor? CssColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return new SKColor(0, 0, 0, 0);

        if (value[0] == '#')
            return HexColor(value[1..]);

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = value.IndexOf('(');
            var close = value.LastIndexOf(')');
            if (open < 0 || close <= open)
                return null;

            var parts = value[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;

            var r = Component(parts[0]);
            var g = Component(parts[1]);
            var b = Component(parts[2]);
            if (r is null || g is null || b is null)
                return null;

            var a = parts.Length >= 4 ? Alpha(parts[3]) : (byte)255;
            return new SKColor(r.Value, g.Value, b.Value, a);
        }

        return NamedColor(value);
    }

    static SKColor? HexColor(string hex)
    {
        if (hex.Length is not (3 or 4 or 6 or 8))
            return null;

        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        byte Digit(int index) => (byte)(HexValue(hex[index]) * 16 + HexValue(hex[index + 1]));
        byte Half(int index) => (byte)(HexValue(hex[index]) * 17);

        return hex.Length switch
        {
            3 => new SKColor(Half(0), Half(1), Half(2)),
            4 => new SKColor(Half(0), Half(1), Half(2), Half(3)),
            6 => new SKColor(Digit(0), Digit(2), Digit(4)),
            _ => new SKColor(Digit(0), Digit(2), Digit(4), Digit(6)),
        };
    }

    static int HexValue(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

    static byte? Component(string part)
    {
        if (part.EndsWith('%'))
        {
            return double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? (byte)Math.Clamp(Math.Round(percent * 2.55), 0, 255)
                : null;
        }

        return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (byte)Math.Clamp(Math.Round(number), 0, 255)
            : null;
    }

    static byte Alpha(string part)
    {
        if (part.EndsWith('%'))
        {
            return double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? (byte)Math.Clamp(Math.Round(percent * 2.55), 0, 255)
                : (byte)255;
        }

        return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (byte)Math.Clamp(Math.Round(number * 255), 0, 255)
            : (byte)255;
    }

    static SKColor? NamedColor(string name) => name.ToLowerInvariant() switch
    {
        "black" => new SKColor(0, 0, 0),
        "white" => new SKColor(255, 255, 255),
        "red" => new SKColor(255, 0, 0),
        "green" => new SKColor(0, 128, 0),
        "blue" => new SKColor(0, 0, 255),
        "yellow" => new SKColor(255, 255, 0),
        "gray" or "grey" => new SKColor(128, 128, 128),
        "silver" => new SKColor(192, 192, 192),
        "maroon" => new SKColor(128, 0, 0),
        "olive" => new SKColor(128, 128, 0),
        "lime" => new SKColor(0, 255, 0),
        "aqua" or "cyan" => new SKColor(0, 255, 255),
        "teal" => new SKColor(0, 128, 128),
        "navy" => new SKColor(0, 0, 128),
        "fuchsia" or "magenta" => new SKColor(255, 0, 255),
        "purple" => new SKColor(128, 0, 128),
        "orange" => new SKColor(255, 165, 0),
        "brown" => new SKColor(165, 42, 42),
        "pink" => new SKColor(255, 192, 203),
        _ => null,
    };

    // ---------------- 上下文 ----------------

    sealed class ImportContext
    {
        public PrintTemplate Template { get; } = new();

        public List<HiprintImportIssue> Issues { get; } = new();

        public int PanelIndex { get; set; }

        public void Issue(HiprintImportSeverity severity, string code, string message, string? elementId = null, string? path = null)
            => Issues.Add(new HiprintImportIssue(severity, code, message, elementId, path));
    }
}
