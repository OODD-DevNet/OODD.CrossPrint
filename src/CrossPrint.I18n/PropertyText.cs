using System.Globalization;
using System.Text.RegularExpressions;

namespace CrossPrint.I18n;

/// <summary>
/// 属性面板文案本地化：描述符/分组/元素类型/结构动作的中文原文在 CrossPrint.Designer
/// 里是唯一事实源，界面层只在中文界面直接用原文，
/// 英文界面走这里的对照表。键/分组/动作的中文文案本来就带序号前缀（如 <c>列3·宽度 (mm)</c>），
/// 所以按前缀拆开翻译，序号原样保留。
/// </summary>
/// <remarks>
/// 放在 I18n 而不是 Designer：Designer 的属性通道不该依赖界面语言；I18n 只做字符串，
/// 不引用任何跨域类型。对照表漏词时原样返回中文——宁可露出中文，也不要把 <c>key</c> 这种
/// 内部标识印到面板上。覆盖度由 <c>PropertyTextTests</c> 用真实描述符逐条把门。
/// </remarks>
public static class PropertyText
{
    /// <summary>当前界面语言是否为英文（只有 zh-CN / en-US 两张表，非中文即英文口径）。</summary>
    public static bool IsEnglish =>
        !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);

    static readonly Regex IndexedLabel = new(@"^(列|表头|条件|关键词|合并)(\d+)·(.+)$", RegexOptions.CultureInvariant);
    static readonly Regex InsertBeforeColumn = new(@"^在列(\d+)前插入$", RegexOptions.CultureInvariant);
    static readonly Regex DeleteColumn = new(@"^删除列(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex DeleteConditional = new(@"^删除条件样式(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex DeleteHighlight = new(@"^删除关键词样式(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex DeleteMerge = new(@"^删除合并(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>属性标签：中文界面用描述符原文，英文界面查对照表。</summary>
    public static string Label(string chineseDisplayName) =>
        IsEnglish ? English(chineseDisplayName, _labels, _indexedPrefixes) : chineseDisplayName;

    /// <summary>分组小标题。</summary>
    public static string Group(string chineseGroup) =>
        IsEnglish ? English(chineseGroup, _groups, null) : chineseGroup;

    /// <summary>结构动作按钮（追加列/删除列N/追加条件样式……）。</summary>
    public static string Action(string chineseDisplayName)
    {
        if (!IsEnglish)
            return chineseDisplayName;

        var match = InsertBeforeColumn.Match(chineseDisplayName);
        if (match.Success)
            return $"Insert before column {match.Groups[1].Value}";

        match = DeleteColumn.Match(chineseDisplayName);
        if (match.Success)
            return $"Delete column {match.Groups[1].Value}";

        match = DeleteConditional.Match(chineseDisplayName);
        if (match.Success)
            return $"Delete conditional style {match.Groups[1].Value}";

        match = DeleteHighlight.Match(chineseDisplayName);
        if (match.Success)
            return $"Delete highlight style {match.Groups[1].Value}";

        match = DeleteMerge.Match(chineseDisplayName);
        if (match.Success)
            return $"Delete merge {match.Groups[1].Value}";

        return _actions.TryGetValue(chineseDisplayName, out var english) ? english : chineseDisplayName;
    }

    /// <summary>元素类型名（属性面板标题里的「表格」「二维码」等）。</summary>
    public static string ElementType(string typeKey, string chineseDisplayName)
    {
        if (!IsEnglish)
            return chineseDisplayName;

        return typeKey switch
        {
            "text" => "Text",
            "line" => "Line",
            "rectangle" => "Rectangle",
            "ellipse" => "Ellipse",
            "image" => "Image",
            "qrcode" => "QR Code",
            "barcode" => "Barcode",
            "table" => "Table",
            _ => chineseDisplayName switch
            {
                "文字" => "Text",
                "线段" or "线条" => "Line",
                "矩形" => "Rectangle",
                "椭圆" => "Ellipse",
                "图片" => "Image",
                "二维码" => "QR Code",
                "一维码" => "Barcode",
                "表格" => "Table",
                _ => chineseDisplayName,
            },
        };
    }

    static string English(string chinese, Dictionary<string, string> table, Dictionary<string, string>? indexedPrefixes)
    {
        if (table.TryGetValue(chinese, out var direct))
            return direct;

        if (indexedPrefixes is not null)
        {
            var match = IndexedLabel.Match(chinese);
            if (match.Success
                && indexedPrefixes.TryGetValue(match.Groups[1].Value, out var prefix)
                && table.TryGetValue(match.Groups[3].Value, out var suffix))
            {
                return $"{prefix} {match.Groups[2].Value} · {suffix}";
            }
        }

        return chinese;
    }

    static readonly Dictionary<string, string> _indexedPrefixes = new(StringComparer.Ordinal)
    {
        ["列"] = "Column",
        ["表头"] = "Header",
        ["条件"] = "Condition",
        ["关键词"] = "Highlight",
        ["合并"] = "Merge",
    };

    static readonly Dictionary<string, string> _groups = new(StringComparer.Ordinal)
    {
        ["基本"] = "General",
        ["位置与尺寸"] = "Position & Size",
        ["外观"] = "Appearance",
        ["文字"] = "Text",
        ["线条"] = "Line",
        ["形状"] = "Shape",
        ["图片"] = "Image",
        ["二维码"] = "QR Code",
        ["一维码"] = "Barcode",
        ["表格"] = "Table",
        ["表格列"] = "Table Columns",
        ["表头"] = "Table Header",
        ["条件样式"] = "Conditional Styles",
        ["关键词样式"] = "Highlight Styles",
        ["静态行合并"] = "Row Merges",
    };

    static readonly Dictionary<string, string> _actions = new(StringComparer.Ordinal)
    {
        ["追加列"] = "Add column",
        ["追加条件样式"] = "Add conditional style",
        ["追加关键词样式"] = "Add highlight style",
        ["追加静态行合并"] = "Add row merge",
    };

    static readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal)
    {
        // 公共
        ["编号（内核生成）"] = "Id (generated by kernel)",
        ["X 坐标 (mm)"] = "X position (mm)",
        ["Y 坐标 (mm)"] = "Y position (mm)",
        ["宽 (mm)"] = "Width (mm)",
        ["高 (mm)"] = "Height (mm)",
        ["绘制层级"] = "Z-order",
        ["背景色（留空透明）"] = "Background color (empty = transparent)",
        ["边框色（留空不画）"] = "Border color (empty = none)",
        ["边框宽 (mm)"] = "Border width (mm)",
        ["边框虚线段长 (mm)"] = "Border dash length (mm)",
        ["边框边（可多选）"] = "Border sides (multi-select)",
        ["旋转角度 (°)"] = "Rotation (°)",
        ["不透明度 (0–1)"] = "Opacity (0–1)",
        ["样式引用（模板样式库）"] = "Style reference (template style library)",
        ["样式引用（模板样式库，内联留空处补位）"] =
            "Style reference (template style library; fills inline gaps)",
        ["样式引用（模板样式库，管整表字体/字号/行距/字色）"] =
            "Style reference (template style library; controls table font, size, line spacing, text color)",
        ["格式化脚本 (JS)：function(value, options, target, templateData)，返回值替换显示内容（undefined 保持原值）"] =
            "Formatter script (JS): function(value, options, target, templateData); return value replaces the display (undefined keeps original)",
        ["样式脚本 (JS)：返回 {color, fontSize, ...} 或 CSS 字符串，优先级高于内联与样式库"] =
            "Styler script (JS): returns {color, fontSize, ...} or a CSS string; overrides inline and library styles",
        ["测试数据（占位符兜底值；表格按换行分行、制表符分列）"] =
            "Test data (placeholder fallback; newline = table row, tab = column)",

        // 文字
        ["文本内容"] = "Text",
        ["文本字体名"] = "Font family",
        ["文本字号 (pt)"] = "Font size (pt)",
        ["字体名"] = "Font family",
        ["字号 (pt)"] = "Font size (pt)",
        ["加粗"] = "Bold",
        ["倾斜"] = "Italic",
        ["下划线"] = "Underline",
        ["删除线"] = "Strikethrough",
        ["字色"] = "Text color",
        ["水平对齐"] = "Horizontal alignment",
        ["垂直对齐"] = "Vertical alignment",
        ["按宽换行"] = "Wrap to width",
        ["行距倍数"] = "Line spacing",
        ["字间距 (pt)"] = "Letter spacing (pt)",
        ["超宽省略号"] = "Ellipsis when overflow",

        // 线条
        ["线色"] = "Line color",
        ["线宽 (mm)"] = "Line width (mm)",
        ["虚线段长 (mm)"] = "Dash length (mm)",

        // 形状
        ["填充色（留空不填充）"] = "Fill color (empty = no fill)",
        ["描边色（留空不描边）"] = "Stroke color (empty = no stroke)",
        ["描边宽 (mm)"] = "Stroke width (mm)",
        ["圆角半径 (mm)"] = "Corner radius (mm)",
        ["圆角半径 (mm)（椭圆不生效）"] = "Corner radius (mm) (ignored for ellipse)",

        // 图片
        ["图片来源"] = "Image source",
        ["适配方式"] = "Fit mode",
        ["资源键"] = "Resource key",
        ["Base64 图片数据"] = "Base64 image data",

        // 二维码 / 一维码
        ["编码内容"] = "Encoded content",
        ["码制"] = "Symbology",
        ["纠错等级"] = "Error correction level",
        ["静区宽 (mm)"] = "Quiet zone (mm)",
        ["前景色"] = "Foreground color",
        ["显示可读文本"] = "Show human-readable text",
        ["条色"] = "Bar color",
        ["可读文本"] = "Human-readable text",
        ["可读文本字体名"] = "Human-readable font family",
        ["可读文本字号 (pt)"] = "Human-readable font size (pt)",
        ["可读文本颜色"] = "Human-readable text color",
        ["可读文本对齐"] = "Human-readable alignment",
        ["可读文本行距倍数"] = "Human-readable line spacing",

        // 表格
        ["绘制表头"] = "Show header",
        ["表头行高 (mm)（-1 自适应）"] = "Header row height (mm) (-1 = auto)",
        ["表头加粗"] = "Header bold",
        ["表头字号 (pt)（留空继承正文）"] = "Header font size (pt) (empty = inherit body)",
        ["表头底色（留空不填）"] = "Header background (empty = none)",
        ["数据源字段（空=按列首段推断）"] = "Data source field (empty = infer from first column)",
        ["正文字号 (pt)"] = "Body font size (pt)",
        ["正文字色（留空黑色）"] = "Body text color (empty = black)",
        ["行高 (mm)（-1 自适应）"] = "Row height (mm) (-1 = auto)",
        ["绘制外框"] = "Draw outer border",
        ["网格线色（留空不画线）"] = "Grid line color (empty = none)",
        ["网格线宽 (mm)"] = "Grid line width (mm)",
        ["斑马纹底色（留空关闭）"] = "Stripe background (empty = off)",
        ["单元格内边距 (mm)"] = "Cell padding (mm)",
        ["上内边距 (mm)"] = "Top padding (mm)",
        ["下内边距 (mm)"] = "Bottom padding (mm)",
        ["左内边距 (mm)"] = "Left padding (mm)",
        ["右内边距 (mm)"] = "Right padding (mm)",
        ["静态数据行（换行分行、制表符分列）"] = "Static rows (newline = row, tab = column)",
        ["方向"] = "Direction",

        // 表格列（前缀「列N·」由 _indexedPrefixes 翻译）
        ["列"] = "Column",
        ["列宽 (mm)（-1 均分）"] = "Width (mm) (-1 = equal share)",
        ["标题"] = "Title",
        ["字号 (pt)（留空继承表体）"] = "Font size (pt) (empty = inherit body)",
        ["数据字段"] = "Data field",
        ["对齐"] = "Alignment",
        ["格式化脚本 (JS)：function(value, row, index, options)"] =
            "Formatter script (JS): function(value, row, index, options)",
        ["样式脚本 (JS)：优先级高于条件样式与列内联"] =
            "Styler script (JS): overrides conditional styles and inline column styles",
        ["显示行序号"] = "Show row number",
        ["尾行汇总"] = "Footer summary",
        ["汇总前缀"] = "Summary prefix",
        ["汇总小数位"] = "Summary decimals",
        ["同值合并（连续数据行）"] = "Merge equal values (consecutive rows)",

        // 表头单元格（前缀「表头N·」）
        ["行"] = "Row",
        ["起始行"] = "Start row",
        ["起始列"] = "Start column",
        ["跨列数"] = "Column span",
        ["跨行数"] = "Row span",
        ["字号 (pt)（留空继承表头）"] = "Font size (pt) (empty = inherit header)",

        // 条件样式（前缀「条件N·」）
        ["取值字段（空=用格内文本）"] = "Value field (empty = cell text)",
        ["比较方式"] = "Comparison",
        ["比较值"] = "Comparison value",
        ["限定列字段（空=整行）"] = "Restrict to column (empty = whole row)",
        ["底色（留空不覆盖）"] = "Background (empty = no override)",
        ["字色（留空不覆盖）"] = "Text color (empty = no override)",
        ["加粗（勾选覆盖）"] = "Bold (checked to override)",
        ["倾斜（勾选覆盖）"] = "Italic (checked to override)",
        ["字号 (pt)（留空不覆盖）"] = "Font size (pt) (empty = no override)",
        ["字体名（留空不覆盖）"] = "Font family (empty = no override)",

        // 关键词样式（前缀「关键词N·」）
        ["关键词（空=整格）"] = "Keyword (empty = whole cell)",
        ["限定列字段（空=所有列）"] = "Restrict to column (empty = all columns)",
        ["忽略大小写"] = "Ignore case",
        ["片段底色（留空不覆盖）"] = "Fragment background (empty = no override)",
        ["片段字色（留空不覆盖）"] = "Fragment text color (empty = no override)",
    };
}
