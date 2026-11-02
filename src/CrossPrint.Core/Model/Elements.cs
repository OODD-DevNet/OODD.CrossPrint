using System.Text.Json.Serialization;
using SkiaSharp;

namespace CrossPrint.Core;

/// <summary>
/// 元素载荷的每个颜色与枚举属性都显式挂转换器：
/// 这样无论走 <see cref="TemplateJson"/> 还是宿主直接用源生成上下文，
/// 序列化形状都一致，不会出现「枚举写成数字、颜色写成对象」的路径依赖 bug。
/// </summary>
public sealed class TextOptions
{
    /// <summary>文本内容，可含 <c>{{占位符}}</c>。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 引用模板样式库的命名样式 id，为字体/字色/对齐/内边距等提供默认值；
    /// 下方的内联字段非默认语义不变——数值与布尔的"未设置"就是模型默认值，
    /// 因此文本的内联字段总是有效（样式只补内联保留默认值的字段）。
    /// </summary>
    public string? StyleId { get; set; }

    /// <summary>字体族名。缺失时回退系统默认字体，不抛异常。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>字号（磅）。</summary>
    public double FontSizePt { get; set; } = 12;

    /// <summary>加粗。</summary>
    public bool Bold { get; set; }

    /// <summary>倾斜。</summary>
    public bool Italic { get; set; }

    /// <summary>字色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor Color { get; set; } = SKColors.Black;

    /// <summary>水平对齐。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HorizontalAlign>))]
    public HorizontalAlign HorizontalAlign { get; set; } = HorizontalAlign.Left;

    /// <summary>垂直对齐。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VerticalAlign>))]
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Top;

    /// <summary>是否按元素宽度自动换行。</summary>
    public bool Wrap { get; set; } = true;

    /// <summary>行高相对字号的倍数。</summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>字间距（磅）。0 表示按字体默认字距。</summary>
    public double LetterSpacingPt { get; set; }

    /// <summary>内容区内边距（mm）：左。</summary>
    public double PaddingLeftMm { get; set; }

    /// <summary>内容区内边距（mm）：上。</summary>
    public double PaddingTopMm { get; set; }

    /// <summary>内容区内边距（mm）：右。</summary>
    public double PaddingRightMm { get; set; }

    /// <summary>内容区内边距（mm）：下。</summary>
    public double PaddingBottomMm { get; set; }

    /// <summary>不换行且超宽时用省略号截断（hiprint <c>textContentWrap: ellipsis</c>）。</summary>
    public bool Ellipsis { get; set; }

    /// <summary>是否绘制下划线。</summary>
    public bool Underline { get; set; }

    /// <summary>是否绘制删除线。</summary>
    public bool Strikethrough { get; set; }

    internal TextOptions Clone() => new()
    {
        Content = Content,
        StyleId = StyleId,
        FontFamily = FontFamily,
        FontSizePt = FontSizePt,
        Bold = Bold,
        Italic = Italic,
        Color = Color,
        HorizontalAlign = HorizontalAlign,
        VerticalAlign = VerticalAlign,
        Wrap = Wrap,
        LineSpacing = LineSpacing,
        LetterSpacingPt = LetterSpacingPt,
        PaddingLeftMm = PaddingLeftMm,
        PaddingTopMm = PaddingTopMm,
        PaddingRightMm = PaddingRightMm,
        PaddingBottomMm = PaddingBottomMm,
        Ellipsis = Ellipsis,
        Underline = Underline,
        Strikethrough = Strikethrough,
    };
}

/// <summary>文本元素。</summary>
public sealed class TextElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Text;

    /// <summary>文本载荷。</summary>
    public TextOptions Text { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new TextElement { Text = Text.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>线段样式。</summary>
public sealed class LineOptions
{
    /// <summary>引用模板样式库的命名样式 id：样式里的边框族（色/宽/虚线）作为线段的默认值。</summary>
    public string? StyleId { get; set; }

    /// <summary>线色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor StrokeColor { get; set; } = SKColors.Black;

    /// <summary>线宽（mm）。</summary>
    public double StrokeWidthMm { get; set; } = 0.2;

    /// <summary>方向。水平线长度取 <see cref="PaperElement.WidthMm"/>，垂直线取 <see cref="PaperElement.HeightMm"/>。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LineDirection>))]
    public LineDirection Direction { get; set; } = LineDirection.Horizontal;

    /// <summary>虚线段长（mm）循环，null 为实线。</summary>
    public double[]? DashPatternMm { get; set; }

    internal LineOptions Clone() => new()
    {
        StyleId = StyleId,
        StrokeColor = StrokeColor,
        StrokeWidthMm = StrokeWidthMm,
        Direction = Direction,
        DashPatternMm = DashPatternMm is null ? null : (double[])DashPatternMm.Clone(),
    };
}

/// <summary>直线元素。</summary>
public sealed class LineElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Line;

    /// <summary>线段载荷。</summary>
    public LineOptions Line { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new LineElement { Line = Line.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>描边 + 填充通用样式（矩形/椭圆共用）。</summary>
public sealed class ShapeOptions
{
    /// <summary>引用模板样式库的命名样式 id：样式里的边框族映射描边、底色映射填充。</summary>
    public string? StyleId { get; set; }

    /// <summary>描边色，null 表示不描边。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? StrokeColor { get; set; } = SKColors.Black;

    /// <summary>描边宽（mm）。</summary>
    public double StrokeWidthMm { get; set; } = 0.2;

    /// <summary>填充色，null 表示不填充。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? FillColor { get; set; }

    /// <summary>圆角半径（mm），仅矩形生效，渲染时夹取到短边一半。</summary>
    public double CornerRadiusMm { get; set; }

    /// <summary>虚线段长（mm）循环，null 为实线。</summary>
    public double[]? DashPatternMm { get; set; }

    internal ShapeOptions Clone() => new()
    {
        StyleId = StyleId,
        StrokeColor = StrokeColor,
        StrokeWidthMm = StrokeWidthMm,
        FillColor = FillColor,
        CornerRadiusMm = CornerRadiusMm,
        DashPatternMm = DashPatternMm is null ? null : (double[])DashPatternMm.Clone(),
    };
}

/// <summary>矩形元素。</summary>
public sealed class RectangleElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Rectangle;

    /// <summary>形状载荷。</summary>
    public ShapeOptions Rectangle { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new RectangleElement { Rectangle = Rectangle.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>椭圆元素（内切于元素矩形）。</summary>
public sealed class EllipseElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Ellipse;

    /// <summary>形状载荷（<see cref="ShapeOptions.CornerRadiusMm"/> 对本类型无意义）。</summary>
    public ShapeOptions Ellipse { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new EllipseElement { Ellipse = Ellipse.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>图片样式与数据。</summary>
public sealed class ImageOptions
{
    /// <summary>数据来源类型。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ImageSourceType>))]
    public ImageSourceType SourceType { get; set; } = ImageSourceType.EmbeddedBase64;

    /// <summary>Base64 图片数据（<see cref="SourceType"/> 为 EmbeddedBase64 时有效）。</summary>
    public string? Data { get; set; }

    /// <summary>资源键（<see cref="SourceType"/> 为 ResolverKey 时由宿主解析器解释）。</summary>
    public string? ResolverKey { get; set; }

    /// <summary>适配方式。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ImageFitMode>))]
    public ImageFitMode FitMode { get; set; } = ImageFitMode.Contain;

    /// <summary>圆角半径（mm），0 为直角。</summary>
    public double CornerRadiusMm { get; set; }

    internal ImageOptions Clone() => new()
    {
        SourceType = SourceType,
        Data = Data,
        ResolverKey = ResolverKey,
        FitMode = FitMode,
        CornerRadiusMm = CornerRadiusMm,
    };
}

/// <summary>图片元素。</summary>
public sealed class ImageElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Image;

    /// <summary>图片载荷。</summary>
    public ImageOptions Image { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new ImageElement { Image = Image.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>二维码样式与内容。</summary>
public sealed class QrCodeOptions
{
    /// <summary>引用模板样式库的命名样式 id：字色映射前景与标题色、底色映射背景、字体族映射标题字体。</summary>
    public string? StyleId { get; set; }

    /// <summary>编码内容，可含 <c>{{占位符}}</c>。</summary>
    public string Content { get; set; } = "https://example.com";

    /// <summary>纠错等级。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<QrErrorCorrection>))]
    public QrErrorCorrection ErrorCorrection { get; set; } = QrErrorCorrection.M;

    /// <summary>前景色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor ForegroundColor { get; set; } = SKColors.Black;

    /// <summary>背景色，null 表示透明。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; } = SKColors.White;

    /// <summary>静区宽度（mm）。规范建议不小于模块尺寸的 4 倍。</summary>
    public double QuietZoneMm { get; set; } = 1;

    /// <summary>码下标题文本，可含 <c>{{占位符}}</c>；仅 <see cref="ShowCaption"/> 为 true 时绘制。</summary>
    public string Caption { get; set; } = string.Empty;

    /// <summary>是否在二维码下方绘制标题（hiprint 的 <c>hideTitle = false</c>）。</summary>
    public bool ShowCaption { get; set; }

    /// <summary>标题字号（磅）。</summary>
    public double CaptionFontSizePt { get; set; } = 10.5;

    /// <summary>标题字体族。</summary>
    public string CaptionFontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>标题颜色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor CaptionColor { get; set; } = SKColors.Black;

    /// <summary>标题水平对齐。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HorizontalAlign>))]
    public HorizontalAlign CaptionAlign { get; set; } = HorizontalAlign.Center;

    /// <summary>标题行距倍数。</summary>
    public double CaptionLineSpacing { get; set; } = 1.2;

    internal QrCodeOptions Clone() => new()
    {
        StyleId = StyleId,
        Content = Content,
        ErrorCorrection = ErrorCorrection,
        ForegroundColor = ForegroundColor,
        BackgroundColor = BackgroundColor,
        QuietZoneMm = QuietZoneMm,
        Caption = Caption,
        ShowCaption = ShowCaption,
        CaptionFontSizePt = CaptionFontSizePt,
        CaptionFontFamily = CaptionFontFamily,
        CaptionColor = CaptionColor,
        CaptionAlign = CaptionAlign,
        CaptionLineSpacing = CaptionLineSpacing,
    };
}

/// <summary>二维码元素。</summary>
public sealed class QrCodeElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.QrCode;

    /// <summary>二维码载荷。</summary>
    public QrCodeOptions QrCode { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new QrCodeElement { QrCode = QrCode.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>一维码样式与内容。</summary>
public sealed class BarcodeOptions
{
    /// <summary>引用模板样式库的命名样式 id：字色映射条色、底色映射背景、字体族/字号映射可读文本。</summary>
    public string? StyleId { get; set; }

    /// <summary>编码内容，可含 <c>{{占位符}}</c>。</summary>
    public string Content { get; set; } = "ABC-123";

    /// <summary>码制。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BarcodeSymbology>))]
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;

    /// <summary>条色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor BarColor { get; set; } = SKColors.Black;

    /// <summary>背景色，null 表示透明。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; } = SKColors.White;

    /// <summary>是否在条下方绘制可读文本。</summary>
    public bool ShowText { get; set; } = true;

    /// <summary>可读文本字号（磅）。</summary>
    public double FontSizePt { get; set; } = 7;

    /// <summary>文本字体族。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>
    /// 静区宽度（mm）：条码两侧留白的最小值。扫描枪靠这段空白判定符号起止，
    /// 取 0 会让相邻元素的黑像素被当成条的一部分而拒读。ISO/IEC 15416 要求 ≥10 倍窄条宽，
    /// 2.5mm 是热敏面单上窄条 ≥0.25mm 时的经验下限。
    /// </summary>
    public double QuietZoneMm { get; set; } = 2.5;

    internal BarcodeOptions Clone() => new()
    {
        StyleId = StyleId,
        Content = Content,
        Symbology = Symbology,
        BarColor = BarColor,
        BackgroundColor = BackgroundColor,
        ShowText = ShowText,
        FontSizePt = FontSizePt,
        FontFamily = FontFamily,
        QuietZoneMm = QuietZoneMm,
    };
}

/// <summary>一维码元素。</summary>
public sealed class BarcodeElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Barcode;

    /// <summary>一维码载荷。</summary>
    public BarcodeOptions Barcode { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new BarcodeElement { Barcode = Barcode.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>表格列定义。</summary>
public sealed class TableColumn
{
    /// <summary>引用模板样式库的命名样式 id：对齐/字体族/字号/加粗/倾斜/字色/底色作为该列默认值，内联字段优先。</summary>
    public string? StyleId { get; set; }

    /// <summary>表头标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>列宽（mm）。取 -1 表示均分剩余宽度。</summary>
    public double WidthMm { get; set; } = 20;

    /// <summary>数据字段路径，如 <c>items.name</c>；空表示该列不参与数据绑定。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>该列水平对齐。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HorizontalAlign>))]
    public HorizontalAlign Align { get; set; } = HorizontalAlign.Left;

    /// <summary>该列字号（磅），null 表示继承表格字号。</summary>
    public double? FontSizePt { get; set; }

    /// <summary>是否显示行序号（1 起）而不是字段值；对应 hiprint <c>tableTextType: sequence</c>。</summary>
    public bool Sequence { get; set; }

    /// <summary>尾行汇总方式。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TableSummary>))]
    public TableSummary Summary { get; set; } = TableSummary.None;

    /// <summary>汇总文本前缀，如 <c>"合计："</c>。</summary>
    public string SummaryLabel { get; set; } = string.Empty;

    /// <summary>汇总数值的小数位数。</summary>
    public int SummaryDecimals { get; set; } = 2;

    /// <summary>
    /// 数据行同值合并：该列连续数据行的单元格文本相同时纵向合并为一格。
    /// 合并组在跨页窗口边界处断开（每页独立成组）；合并区域底色取组首行的行底色，
    /// 文本按组首行样式垂直居中绘制。
    /// </summary>
    public bool MergeSameValue { get; set; }

    /// <summary>
    /// 单元格格式化脚本（JavaScript，hiprint 列 <c>formatter</c> 同款）：
    /// <c>function(value, row, index, options)</c>——value 是该行字段原始值，row 是整行数据，
    /// 返回字符串替换单元格显示文本。null/空白表示没有脚本。
    /// </summary>
    public string? Formatter { get; set; }

    /// <summary>
    /// 单元格样式脚本（JavaScript，hiprint 列 <c>styler</c> 同款）：返回样式对象或 CSS 字符串，
    /// 按行值动态改单元格样式（文本变色、改背景等），优先级高于该列全部静态样式
    /// （条件样式/关键词样式/列内联）。解析失败或脚本报错降级为"没有脚本"。
    /// </summary>
    public string? Styler { get; set; }

    internal TableColumn Clone() => new()
    {
        StyleId = StyleId,
        Title = Title,
        WidthMm = WidthMm,
        Field = Field,
        Align = Align,
        FontSizePt = FontSizePt,
        Sequence = Sequence,
        Summary = Summary,
        SummaryLabel = SummaryLabel,
        SummaryDecimals = SummaryDecimals,
        MergeSameValue = MergeSameValue,
        Formatter = Formatter,
        Styler = Styler,
    };
}

/// <summary>
/// 多行表头中的一个单元格。行/列下标从 0 起，跨度覆盖相邻单元格。
/// 单行表头可以留空 <see cref="TableOptions.HeaderRows"/>，渲染器会回退到列标题。
/// </summary>
public sealed class TableHeaderCell
{
    /// <summary>引用模板样式库的命名样式 id：对齐/字体族/字号/加粗/字色/底色作为该格默认值，内联字段优先。</summary>
    public string? StyleId { get; set; }

    /// <summary>所在表头行（0 起）。</summary>
    public int Row { get; set; }

    /// <summary>起始列（0 起）。</summary>
    public int Column { get; set; }

    /// <summary>标题文本。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>横向跨列数。</summary>
    public int ColSpan { get; set; } = 1;

    /// <summary>纵向跨行数。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>水平对齐。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HorizontalAlign>))]
    public HorizontalAlign Align { get; set; } = HorizontalAlign.Center;

    /// <summary>字号（磅），null 表示继承表头字号。</summary>
    public double? FontSizePt { get; set; }

    internal TableHeaderCell Clone() => new()
    {
        StyleId = StyleId,
        Row = Row,
        Column = Column,
        Title = Title,
        ColSpan = ColSpan,
        RowSpan = RowSpan,
        Align = Align,
        FontSizePt = FontSizePt,
    };
}

/// <summary>条件样式的比较运算符。</summary>
public enum TableConditionOperator
{
    /// <summary>大于（数值优先，双方都解析不出数值时按文本序比较）。</summary>
    GreaterThan,

    /// <summary>大于等于。</summary>
    GreaterOrEqual,

    /// <summary>小于。</summary>
    LessThan,

    /// <summary>小于等于。</summary>
    LessOrEqual,

    /// <summary>等于。</summary>
    Equal,

    /// <summary>不等于。</summary>
    NotEqual,

    /// <summary>包含子串（忽略大小写）。</summary>
    Contains,

    /// <summary>不包含子串（忽略大小写）。</summary>
    NotContains,
}

/// <summary>
/// 通用条件样式：数据行的 <see cref="Field"/> 值与 <see cref="Value"/> 按 <see cref="Operator"/>
/// 比较命中后，把样式载荷套到 <see cref="Column"/> 指定的列（空 = 整行）。样式字段全部可空，
/// null 表示不覆盖——只想要底色就只填底色，只想要字号就只填字号。
/// </summary>
/// <remarks>
/// 比较语义：双方都能解析成数值时按数值比较，否则按文本（序数、忽略大小写）比较；
/// Contains/NotContains 永远按子串匹配。<see cref="Field"/> 留空时取命中列单元格自身的文本
/// （此时必须指定 <see cref="Column"/>，静态行与测试数据行也参与求值）。
/// 规则按数组顺序评估，后命中的覆盖先命中的。
/// </remarks>
public sealed class TableConditionalStyle
{
    /// <summary>引用模板样式库的命名样式 id：下方可空样式字段的默认值来源，内联非 null 优先。</summary>
    public string? StyleId { get; set; }

    /// <summary>取值字段路径，相对集合行对象本身，如 <c>amount</c>（对应列字段 <c>lines.amount</c> 去掉首段）；空 = 用单元格自身文本。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>比较运算符。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TableConditionOperator>))]
    public TableConditionOperator Operator { get; set; } = TableConditionOperator.GreaterThan;

    /// <summary>比较值；数值比较按不变文化解析，子串比较按原文。</summary>
    public string? Value { get; set; }

    /// <summary>限定列的字段路径（如 <c>amount</c> 或 <c>lines.amount</c>）；空表示整行。</summary>
    public string? Column { get; set; }

    /// <summary>命中底色；null 不覆盖。整行规则覆盖斑马纹与旧行底色，限定列规则只画该格。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; }

    /// <summary>命中字色；null 不覆盖。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? TextColor { get; set; }

    /// <summary>命中加粗；null 不覆盖。</summary>
    public bool? Bold { get; set; }

    /// <summary>命中倾斜；null 不覆盖。</summary>
    public bool? Italic { get; set; }

    /// <summary>命中字号（磅）；null 不覆盖。影响折行与行高。</summary>
    public double? FontSizePt { get; set; }

    /// <summary>命中字体族；null 不覆盖。缺失字体按渲染器常规回退。</summary>
    public string? FontFamily { get; set; }

    internal TableConditionalStyle Clone() => new()
    {
        StyleId = StyleId,
        Field = Field,
        Operator = Operator,
        Value = Value,
        Column = Column,
        BackgroundColor = BackgroundColor,
        TextColor = TextColor,
        Bold = Bold,
        Italic = Italic,
        FontSizePt = FontSizePt,
        FontFamily = FontFamily,
    };
}

/// <summary>
/// 单元格片段样式（关键词高亮）：在命中列（<see cref="Column"/> 空 = 所有列）的单元格文本里找
/// <see cref="Keyword"/> 的全部出现，逐段套样式——实现"改字符串中某一个字的背景/字色/字体"。
/// </summary>
/// <remarks>规则按数组顺序评估；重叠匹配先到先得。折行是片段感知的：片段字号/字体参与测宽，
/// 一行内可混排多种样式。</remarks>
public sealed class TableHighlightRule
{
    /// <summary>引用模板样式库的命名样式 id：下方可空样式字段的默认值来源，内联非 null 优先。</summary>
    public string? StyleId { get; set; }

    /// <summary>待匹配子串；空串表示整格命中（等价于给整格上样式）。</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>限定列的字段路径；空表示所有数据列。</summary>
    public string? Column { get; set; }

    /// <summary>忽略大小写匹配。</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>片段底色；null 不覆盖。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; }

    /// <summary>片段字色；null 不覆盖。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? TextColor { get; set; }

    /// <summary>片段加粗；null 不覆盖。</summary>
    public bool? Bold { get; set; }

    /// <summary>片段倾斜；null 不覆盖。</summary>
    public bool? Italic { get; set; }

    /// <summary>片段字号（磅）；null 不覆盖。</summary>
    public double? FontSizePt { get; set; }

    /// <summary>片段字体族；null 不覆盖。</summary>
    public string? FontFamily { get; set; }

    internal TableHighlightRule Clone() => new()
    {
        StyleId = StyleId,
        Keyword = Keyword,
        Column = Column,
        IgnoreCase = IgnoreCase,
        BackgroundColor = BackgroundColor,
        TextColor = TextColor,
        Bold = Bold,
        Italic = Italic,
        FontSizePt = FontSizePt,
        FontFamily = FontFamily,
    };
}

/// <summary>
/// 静态行合并单元格：表体 <see cref="TableOptions.Rows"/> 上的显式跨度（行下标 0 起，含静态行）。
/// 数据行（集合展开）的跨行合并用 <see cref="TableColumn.MergeSameValue"/>——动态行数没法预先指下标。
/// </summary>
public sealed class TableRowSpan
{
    /// <summary>起始行（0 起，表体行序，静态行在前）。</summary>
    public int Row { get; set; }

    /// <summary>起始列（0 起）。</summary>
    public int Column { get; set; }

    /// <summary>纵向跨行数。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>横向跨列数。</summary>
    public int ColSpan { get; set; } = 1;

    internal TableRowSpan Clone() => new() { Row = Row, Column = Column, RowSpan = RowSpan, ColSpan = ColSpan };
}

/// <summary>表格样式与数据。</summary>
public sealed class TableOptions
{
    /// <summary>引用模板样式库的命名样式 id：字体族/字号/行距/正文默认字色作为整表默认值，内联字段优先。</summary>
    public string? StyleId { get; set; }

    /// <summary>列定义，顺序即从左到右。</summary>
    public List<TableColumn> Columns { get; set; } = new();

    /// <summary>
    /// 行集合的数据字段路径，如 <c>items</c>；空表示由列字段首段推断。
    /// 列字段互不共享首段时（如 <c>a.name</c> 与 <c>b.qty</c>）必须显式给出，否则推断失败。
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>静态数据行；每行单元格数应与列数一致。数据绑定产生的行在渲染期追加其后。</summary>
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>多行表头单元格；为空时按 <see cref="Columns"/> 的标题渲染单行表头。</summary>
    public List<TableHeaderCell> HeaderRows { get; set; } = new();

    /// <summary>表头行高（mm）；取 -1 表示按表头字号自适应。</summary>
    public double HeaderRowHeightMm { get; set; } = -1;

    /// <summary>表头字号（磅），null 表示继承 <see cref="FontSizePt"/>。</summary>
    public double? HeaderFontSizePt { get; set; }

    /// <summary>网格线色，null 表示不画线。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? GridColor { get; set; } = SKColors.Black;

    /// <summary>网格线宽（mm）。</summary>
    public double GridWidthMm { get; set; } = 0.15;

    /// <summary>是否绘制外框。</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>是否绘制表头行。</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>表头背景色。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? HeaderFillColor { get; set; } = new SKColor(238, 238, 238);

    /// <summary>表头是否加粗。</summary>
    public bool HeaderBold { get; set; } = true;

    /// <summary>斑马纹偶数行底色，null 表示不启用。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? StripeColor { get; set; }

    /// <summary>
    /// 通用条件样式规则，只作用于表体（数据行的 <see cref="TableConditionalStyle.Field"/> 条件求值；
    /// Field 留空的"单元格文本"规则对静态行与测试数据行同样生效）。按数组顺序评估，后命中的覆盖先命中的；
    /// 样式载荷逐字段可空，null 不覆盖。命中行底色替代斑马纹。
    /// </summary>
    public List<TableConditionalStyle> ConditionalStyles { get; set; } = new();

    /// <summary>
    /// 单元格片段样式（关键词高亮）：对表体所有单元格文本做子串匹配，逐段套背景/字色/字体。
    /// 与 <see cref="ConditionalStyles"/> 独立——条件样式管"整格/整行"，片段样式管"格内某几个字"。
    /// </summary>
    public List<TableHighlightRule> Highlights { get; set; } = new();

    /// <summary>静态行显式合并单元格（跨行/跨列）。数据行的跨行合并走 <see cref="TableColumn.MergeSameValue"/>。</summary>
    public List<TableRowSpan> RowSpans { get; set; } = new();

    /// <summary>正文字号（磅）。</summary>
    public double FontSizePt { get; set; } = 9;

    /// <summary>字体族。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>固定行高（mm）；取 -1 表示按内容自适应。</summary>
    public double RowHeightMm { get; set; } = -1;

    /// <summary>单元格文字内边距（mm）。</summary>
    public double CellPaddingMm { get; set; } = 1;

    /// <summary>行高相对字号的倍数（自适应行高时使用）。</summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>表格文字颜色（表头/表体/汇总的默认字色），null 黑色；条件/片段样式与列样式可再覆盖。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? TextColor { get; set; }

    /// <summary>深拷贝。渲染引擎用它在渲染期构建「样式解析后」的副本，不允许改到原模板。</summary>
    public TableOptions Clone() => new()
    {
        StyleId = StyleId,
        Columns = Columns.Select(c => c.Clone()).ToList(),
        Field = Field,
        Rows = Rows.Select(r => r.ToList()).ToList(),
        HeaderRows = HeaderRows.Select(h => h.Clone()).ToList(),
        HeaderRowHeightMm = HeaderRowHeightMm,
        HeaderFontSizePt = HeaderFontSizePt,
        GridColor = GridColor,
        GridWidthMm = GridWidthMm,
        ShowBorder = ShowBorder,
        ShowHeader = ShowHeader,
        HeaderFillColor = HeaderFillColor,
        HeaderBold = HeaderBold,
        StripeColor = StripeColor,
        ConditionalStyles = ConditionalStyles.Select(r => r.Clone()).ToList(),
        Highlights = Highlights.Select(r => r.Clone()).ToList(),
        RowSpans = RowSpans.Select(r => r.Clone()).ToList(),
        FontSizePt = FontSizePt,
        FontFamily = FontFamily,
        RowHeightMm = RowHeightMm,
        CellPaddingMm = CellPaddingMm,
        LineSpacing = LineSpacing,
        TextColor = TextColor,
    };
}

/// <summary>表格元素。</summary>
public sealed class TableElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Table;

    /// <summary>表格载荷。</summary>
    public TableOptions Table { get; set; } = new();

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var clone = new TableElement { Table = Table.Clone() };
        clone.CopyCommonFrom(this);
        return clone;
    }
}

/// <summary>元素类型判别符常量。</summary>
public static class ElementTypeKeys
{
    /// <summary>文本。</summary>
    public const string Text = "text";

    /// <summary>直线。</summary>
    public const string Line = "line";

    /// <summary>矩形。</summary>
    public const string Rectangle = "rectangle";

    /// <summary>椭圆。</summary>
    public const string Ellipse = "ellipse";

    /// <summary>图片。</summary>
    public const string Image = "image";

    /// <summary>二维码。</summary>
    public const string QrCode = "qrcode";

    /// <summary>一维码。</summary>
    public const string Barcode = "barcode";

    /// <summary>表格。</summary>
    public const string Table = "table";

    /// <summary>全部内置类型键，顺序与工具箱默认展示一致。</summary>
    public static IReadOnlyList<string> All { get; } = [Text, Line, Rectangle, Ellipse, Image, QrCode, Barcode, Table];
}

/// <summary>
/// 模板级命名样式（样式库条目）。全部样式字段可空——null 表示"这一项没配"，
/// 渲染时由消费点的内联值兜底。一个样式可被任意多个元素/表格列/规则通过 <c>StyleId</c> 引用，
/// 改一处全局生效；解析口径是 CSS 同款的「库样式打底、内联覆盖」。
/// </summary>
public sealed class NamedStyle
{
    /// <summary>样式 id，模板内唯一（<c>st-N</c> 递增）。引用按 id，不随改名断链。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名（设计器里展示与下拉选择用）。</summary>
    public string Name { get; set; } = "新样式";

    // ---- 字体族 ----

    /// <summary>字体族名；null 不设置。</summary>
    public string? FontFamily { get; set; }

    /// <summary>字号（磅）；null 不设置。</summary>
    public double? FontSizePt { get; set; }

    /// <summary>加粗；null 不设置。</summary>
    public bool? Bold { get; set; }

    /// <summary>倾斜；null 不设置。</summary>
    public bool? Italic { get; set; }

    /// <summary>下划线；null 不设置。</summary>
    public bool? Underline { get; set; }

    /// <summary>删除线；null 不设置。</summary>
    public bool? Strikethrough { get; set; }

    /// <summary>字间距（磅）；null 不设置。</summary>
    public double? LetterSpacingPt { get; set; }

    /// <summary>行距倍数；null 不设置。</summary>
    public double? LineSpacing { get; set; }

    // ---- 颜色 ----

    /// <summary>字色/前景色；null 不设置。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? TextColor { get; set; }

    /// <summary>底色/填充色；null 不设置。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; }

    // ---- 对齐 ----

    /// <summary>水平对齐；null 不设置。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HorizontalAlign>))]
    public HorizontalAlign? HorizontalAlign { get; set; }

    /// <summary>垂直对齐；null 不设置。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VerticalAlign>))]
    public VerticalAlign? VerticalAlign { get; set; }

    // ---- 内边距（mm） ----

    /// <summary>内边距：左；null 不设置。</summary>
    public double? PaddingLeftMm { get; set; }

    /// <summary>内边距：上；null 不设置。</summary>
    public double? PaddingTopMm { get; set; }

    /// <summary>内边距：右；null 不设置。</summary>
    public double? PaddingRightMm { get; set; }

    /// <summary>内边距：下；null 不设置。</summary>
    public double? PaddingBottomMm { get; set; }

    // ---- 边框（线段/形状/元素框共用一族） ----

    /// <summary>边框色；null 不设置。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BorderColor { get; set; }

    /// <summary>边框宽（mm）；null 不设置。</summary>
    public double? BorderWidthMm { get; set; }

    /// <summary>边框虚线段长（mm）循环，null 为实线。</summary>
    public double[]? BorderDashPatternMm { get; set; }

    /// <summary>深拷贝，供撤销栈复用。</summary>
    internal NamedStyle Clone() => new()
    {
        Id = Id,
        Name = Name,
        FontFamily = FontFamily,
        FontSizePt = FontSizePt,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
        LetterSpacingPt = LetterSpacingPt,
        LineSpacing = LineSpacing,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
        HorizontalAlign = HorizontalAlign,
        VerticalAlign = VerticalAlign,
        PaddingLeftMm = PaddingLeftMm,
        PaddingTopMm = PaddingTopMm,
        PaddingRightMm = PaddingRightMm,
        PaddingBottomMm = PaddingBottomMm,
        BorderColor = BorderColor,
        BorderWidthMm = BorderWidthMm,
        BorderDashPatternMm = BorderDashPatternMm is null ? null : (double[])BorderDashPatternMm.Clone(),
    };
}
