using System.Collections.Generic;
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
    static readonly Regex MoveColumnUp = new(@"^上移列(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex MoveColumnDown = new(@"^下移列(\d+)$", RegexOptions.CultureInvariant);
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

        match = MoveColumnUp.Match(chineseDisplayName);
        if (match.Success)
            return $"Move column {match.Groups[1].Value} up";

        match = MoveColumnDown.Match(chineseDisplayName);
        if (match.Success)
            return $"Move column {match.Groups[1].Value} down";

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

    /// <summary>
    /// 枚举下拉选项的本地化显示名。<paramref name="descriptorKey"/> 定位是哪个枚举，
    /// <paramref name="option"/> 是枚举 CLR 名（下拉的值语义不变，只是显示翻译）。
    /// 中文界面查映射表、没有映射的原样返回；英文界面返回英文（枚举名本身就是英文）。
    /// </summary>
    public static string Option(string descriptorKey, string option)
    {
        if (IsEnglish)
            return option;

        // 按描述符键选表：同一个 "None" 在边框边是「不画」、在图片适配是「原始尺寸」、
        // 在尾行汇总是「不汇总」，用一张通用表会翻错。没建表的枚举（码制名等专有名词）原样返回。
        return descriptorKey switch
        {
            "shape.kind" => Lookup(ShapeKindZh, option),
            _ when descriptorKey.EndsWith("cellRender", StringComparison.Ordinal)
                => Lookup(CellRenderZh, option),
            _ when descriptorKey.EndsWith("cellImageSource", StringComparison.Ordinal)
                => Lookup(CellImageSourceZh, option),
            _ when descriptorKey.EndsWith("borderSides", StringComparison.Ordinal)
                => Lookup(BorderSideZh, option),
            _ when descriptorKey.EndsWith("horizontalAlign", StringComparison.Ordinal)
                || descriptorKey.EndsWith("captionAlign", StringComparison.Ordinal)
                || descriptorKey.EndsWith(".align", StringComparison.Ordinal)
                => Lookup(AlignZh, option),
            _ when descriptorKey.EndsWith("verticalAlign", StringComparison.Ordinal)
                => Lookup(VerticalAlignZh, option),
            _ when descriptorKey.EndsWith("line.direction", StringComparison.Ordinal)
                => Lookup(LineDirectionZh, option),
            _ when descriptorKey.EndsWith("fitMode", StringComparison.Ordinal)
                || descriptorKey.EndsWith("logoFit", StringComparison.Ordinal)
                => Lookup(FitZh, option),
            _ when descriptorKey.EndsWith("sourceType", StringComparison.Ordinal)
                => Lookup(ImageSourceTypeZh, option),
            _ when descriptorKey.IndexOf("ErrorCorrection", StringComparison.OrdinalIgnoreCase) >= 0
                => Lookup(QrErrorCorrectionZh, option),
            _ when descriptorKey.EndsWith(".summary", StringComparison.Ordinal)
                => Lookup(SummaryZh, option),
            _ when descriptorKey.EndsWith(".operator", StringComparison.Ordinal)
                => Lookup(ConditionOperatorZh, option),
            _ => option,
        };
    }

    static string Lookup(Dictionary<string, string> table, string option)
        => table.TryGetValue(option, out var zh) ? zh : option;

    /// <summary>
    /// 表格列「渲染方式」下拉的中文显示名（键 = TableCellRender CLR 名）。
    /// 四项齐全，Image 显示「图片」而不是裸 "Image"，避免用户以为只有文字/一维码/二维码三种。
    /// </summary>
    static readonly Dictionary<string, string> CellRenderZh = new(StringComparer.Ordinal)
    {
        ["Text"] = "文字",
        ["Barcode"] = "一维码",
        ["QrCode"] = "二维码",
        ["Image"] = "图片",
    };

    /// <summary>形状种类下拉的中文显示名（键 = ShapeKind CLR 名）。</summary>
    /// <summary>表格图片列「图片数据类型」下拉（键 = TableCellImageSource CLR 名）。</summary>
    static readonly Dictionary<string, string> CellImageSourceZh = new(StringComparer.Ordinal)
    {
        ["Auto"] = "自动识别",
        ["Url"] = "链接图片",
        ["Base64"] = "Base64 图片",
    };

    static readonly Dictionary<string, string> BorderSideZh = new(StringComparer.Ordinal)
    {
        ["None"] = "不画",
        ["Top"] = "上边",
        ["Right"] = "右边",
        ["Bottom"] = "下边",
        ["Left"] = "左边",
        ["All"] = "四边全画",
    };

    static readonly Dictionary<string, string> AlignZh = new(StringComparer.Ordinal)
    {
        ["Left"] = "左对齐",
        ["Center"] = "居中",
        ["Right"] = "右对齐",
        ["Justify"] = "两端对齐",
    };

    static readonly Dictionary<string, string> VerticalAlignZh = new(StringComparer.Ordinal)
    {
        ["Top"] = "顶部",
        ["Middle"] = "垂直居中",
        ["Bottom"] = "底部",
    };

    static readonly Dictionary<string, string> LineDirectionZh = new(StringComparer.Ordinal)
    {
        ["Horizontal"] = "水平线",
        ["Vertical"] = "垂直线",
    };

    static readonly Dictionary<string, string> FitZh = new(StringComparer.Ordinal)
    {
        ["Contain"] = "等比容纳",
        ["Cover"] = "等比覆盖",
        ["Fill"] = "拉伸填满",
        ["None"] = "原始尺寸",
        ["Original"] = "原尺寸",
    };

    static readonly Dictionary<string, string> ImageSourceTypeZh = new(StringComparer.Ordinal)
    {
        ["EmbeddedBase64"] = "内嵌 Base64",
        ["ResolverKey"] = "宿主资源键",
        ["Url"] = "链接地址",
    };

    static readonly Dictionary<string, string> QrErrorCorrectionZh = new(StringComparer.Ordinal)
    {
        ["L"] = "低（约 7%）",
        ["M"] = "中（约 15%）",
        ["Q"] = "较高（约 25%）",
        ["H"] = "高（约 30%）",
    };

    static readonly Dictionary<string, string> SummaryZh = new(StringComparer.Ordinal)
    {
        ["None"] = "不汇总",
        ["Count"] = "计数",
        ["Sum"] = "求和",
        ["Average"] = "平均值",
        ["Min"] = "最小值",
        ["Max"] = "最大值",
        ["Text"] = "固定文本",
    };

    static readonly Dictionary<string, string> ConditionOperatorZh = new(StringComparer.Ordinal)
    {
        ["GreaterThan"] = "大于",
        ["GreaterOrEqual"] = "大于等于",
        ["LessThan"] = "小于",
        ["LessOrEqual"] = "小于等于",
        ["Equal"] = "等于",
        ["NotEqual"] = "不等于",
        ["Contains"] = "包含",
        ["NotContains"] = "不包含",
        ["StartsWith"] = "开头是",
        ["EndsWith"] = "结尾是",
    };

    static readonly Dictionary<string, string> ShapeKindZh = new(StringComparer.Ordinal)
    {
        ["Rectangle"] = "矩形",
        ["RoundRect"] = "圆角矩形",
        ["Triangle"] = "等腰三角形",
        ["RightTriangle"] = "直角三角形",
        ["Diamond"] = "菱形",
        ["Parallelogram"] = "平行四边形",
        ["Trapezoid"] = "梯形",
        ["Pentagon"] = "五边形",
        ["Hexagon"] = "六边形",
        ["Octagon"] = "八边形",
        ["Star5"] = "五角星",
        ["ArrowRight"] = "右箭头",
        ["ArrowLeft"] = "左箭头",
        ["ArrowUp"] = "上箭头",
        ["ArrowDown"] = "下箭头",
        ["Plus"] = "十字",
        ["Cylinder"] = "圆柱",
    };

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
            "shape" => "Shape",
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
                "形状" => "Shape",
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
        ["分页"] = "Pagination",
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
        ["每页重复显示（页眉/页脚/常驻水印打勾后每页都画）"] = "Repeat on every page (headers / footers / persistent watermark)",
        ["本元素之后强制分页"] = "Force a page break after this element",
        ["显示页区间（如 1,3-5；留空 = 所有页显示）"] = "Show on pages (e.g. 1,3-5; empty = all pages)",
        ["隐藏页区间（如 1-3,7-8；优先于显示区间）"] = "Hide on pages (e.g. 1-3,7-8; overrides show range)",
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
        ["上划线"] = "Overline",
        ["整段左缩进 (mm)"] = "Paragraph indent (mm)",
        ["首行额外缩进 (mm)"] = "First-line indent (mm)",
        ["内容超出自动续页"] = "Continue on overflow to next page",

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
        ["圆角半径 (mm)（仅圆角矩形生效）"] = "Corner radius (mm) (round-rect only)",
        ["圆角半径（逐角，逗号分隔：左上,右上,左下,右下；只填一个值＝四角相同）"] =
            "Corner radius per corner (comma-separated: top-left,top-right,bottom-left,bottom-right; a single value applies to all corners)",
        // 椭圆的逐角圆角（与矩形版相比省略"只填一个值"尾注——椭圆已隐藏该字段，仅完备性门要求存在）
        ["圆角半径（逐角，逗号分隔：左上,右上,左下,右下）"] =
            "Corner radius per corner (comma-separated: top-left,top-right,bottom-left,bottom-right)",
        ["圆角半径（逐顶点，逗号分隔：按顶点序，矩形/圆角矩形=左上,右上,左下,右下，三角形=上,右下,左下；只填一个值＝全部相同）"] =
            "Corner radius per vertex (comma-separated, vertex order; rectangle/round-rect = top-left,top-right,bottom-left,bottom-right; triangle = top,bottom-right,bottom-left; a single value applies to all vertices)",
        ["形状种类"] = "Shape kind",
        ["样式引用（模板样式库）"] = "Style reference (template style library)",

        // 图片
        ["图片来源"] = "Image source",
        ["适配方式"] = "Fit mode",
        ["资源键"] = "Resource key",
        ["Base64 图片数据"] = "Base64 image data",
        ["内嵌图片 (data URI / Base64 / SVG)"] = "Embedded image (data URI / Base64 / SVG)",
        ["图片地址 (http/https)"] = "Image URL (http/https)",
        ["字段绑定"] = "Field binding",
        ["字段绑定 ({{字段}} 或纯字段名，留空 = 不绑定)"] = "Field binding ({{field}} or plain path, empty = none)",

        // 二维码 / 一维码
        ["编码内容"] = "Encoded content",
        ["码制"] = "Symbology",
        ["纠错等级"] = "Error correction level",
        ["静区宽 (mm)"] = "Quiet zone (mm)",
        ["模块宽度 (mm)（0=拉伸到元素宽）"] = "Module width (mm) (0 = stretch to element width)",
        ["模块宽度 (mm)"] = "Module width (mm)",
        ["前景色"] = "Foreground color",
        ["显示可读文本"] = "Show human-readable text",
        ["渲染方式"] = "Render mode",
        ["分组标题"] = "Group title",
        ["条色"] = "Bar color",
        ["可读文本"] = "Human-readable text",
        ["可读文本字体名"] = "Human-readable font family",
        ["可读文本字号 (pt)"] = "Human-readable font size (pt)",
        ["可读文本颜色"] = "Human-readable text color",
        ["可读文本对齐"] = "Human-readable alignment",
        ["可读文本行距倍数"] = "Human-readable line spacing",
        ["中心 Logo 数据 (data URI / Base64)"] = "Center logo data (data URI / Base64)",
        ["中心 Logo 字段（{{占位符}}）"] = "Center logo field ({{placeholder}})",
        ["中心 Logo 适配"] = "Center logo fit",
        ["条码标题"] = "Barcode caption",
        ["显示条码标题"] = "Show barcode caption",
        ["条码标题字号 (pt)"] = "Barcode caption size (pt)",
        ["条码标题字体名"] = "Barcode caption font family",
        ["条码标题颜色"] = "Barcode caption color",
        ["条码标题对齐"] = "Barcode caption alignment",
        ["条码标题行距倍数"] = "Barcode caption line spacing",

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
        ["每页重复表头"] = "Repeat header on each page",

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
        ["图片数据类型"] = "Image source type",
        ["内容宽 (mm)（0 跟随列宽）"] = "Content width (mm) (0 = column width)",
        ["内容高 (mm)（0 跟随行高）"] = "Content height (mm) (0 = row height)",

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
