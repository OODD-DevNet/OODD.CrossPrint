using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 属性通道：一个元素的<b>全部可编辑属性</b>的自描述集合（架构 §4.1）。
/// 宿主属性面板完全数据驱动——它遍历 <see cref="Descriptors"/> 生成表单，
/// 不写死任何字段名。所以这里少一条描述符，界面上就物理上少一个能编辑的框，
/// 用户只能去手改 JSON。这个失效模式由 <c>ElementPropertiesTests</c> 的反射完备性门拦住：
/// 门从 <see cref="CrossPrint.Core"/> 的模型类（加上 <see cref="TemplateJson.PayloadOptions"/>
/// 真实吐出的字段名）反推应有的键，与这里的清单逐条对账。
/// </summary>
/// <remarks>
/// <para><b>表格列的取舍</b>：列是"可变长度的子对象数组"，没有单一控件能表达它，
/// 所以本通道按<b>下标展开</b>为 <c>table.columns[i].title</c> 这样的键——
/// 每列 13 条，条数随元素当前列数生成。列的<b>增删是结构变更</b>，不走单格写回，
/// 但也没被排除在面板之外：它们作为 <see cref="Actions"/> 里的按钮声明出来
/// （<c>table.appendColumn</c> / <c>table.columns[i].remove</c> 等），宿主点击后交给
/// <see cref="EditorKernel.RunElementAction"/>；因此描述符与动作清单都是<b>快照</b>，
/// 列数变了要重新 <see cref="For"/>。
/// 快照过期后 <see cref="PropertyDescriptor.GetValue"/> 返回 null（面板刷新不至于崩），
/// <see cref="PropertyDescriptor.SetValue"/> 抛 <see cref="InvalidOperationException"/>（改了没反应最难查）。</para>
/// <para><b>多行表头同理</b>：<c>table.headerRows[i].title</c> 按当前表头单元格数展开，每格 8 条，
/// 过期语义与列完全一致（读 null、写抛）；表头单元格本身没有增删动作。</para>
/// <para><b>表格规则类列表同理</b>：条件样式 <c>table.conditionalStyles[i].*</c>（每条 11 字段）、
/// 关键词样式 <c>table.highlights[i].*</c>（每条 10 字段）、静态行合并 <c>table.rowSpans[i].*</c>（每条 4 字段）
/// 都按当前条数展开，过期语义与列完全一致；这三类的追加与删除同样以动作按钮声明在 <see cref="Actions"/> 里，
/// 由 <see cref="EditorKernel.RunElementAction"/> 执行结构命令。</para>
/// <para><c>table.rows</c> 则相反：它是一张纯文本网格，宿主用一个多行框最顺手，
/// 所以给<b>一条</b>描述符，值编码为"换行分行、制表符分列"，空行丢弃。</para>
/// </remarks>
public sealed class ElementProperties
{
    const char RowSeparator = '\n';
    const char CellSeparator = '\t';

    readonly List<PropertyDescriptor> _descriptors;
    readonly Dictionary<string, PropertyDescriptor> _byKey;
    readonly List<ElementAction> _actions;

    ElementProperties(PaperElement element, List<PropertyDescriptor> descriptors, List<ElementAction>? actions = null)
    {
        Element = element;
        _descriptors = descriptors;
        _actions = actions ?? [];
        _byKey = new Dictionary<string, PropertyDescriptor>(StringComparer.Ordinal);
        foreach (var d in descriptors)
            _byKey[d.Key] = d;
    }

    /// <summary>为元素建属性通道。未识别的 <see cref="PaperElement"/> 子类只给公共几何字段，不抛。</summary>
    /// <param name="element">目标元素。</param>
    /// <returns>该类型的描述符集合。</returns>
    public static ElementProperties For(PaperElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var list = Common();
        List<ElementAction>? actions = null;
        switch (element)
        {
            case TextElement:
                list.AddRange(ForText());
                break;
            case LineElement:
                list.AddRange(ForLine());
                break;
            case RectangleElement:
                list.AddRange(ForShape(ElementTypeKeys.Rectangle));
                break;
            case EllipseElement:
                list.AddRange(ForShape(ElementTypeKeys.Ellipse));
                break;
            case ImageElement:
                list.AddRange(ForImage());
                break;
            case QrCodeElement:
                list.AddRange(ForQrCode());
                break;
            case BarcodeElement:
                list.AddRange(ForBarcode());
                break;
            case TableElement table:
                list.AddRange(ForTable(table));
                actions = TableActions(table);
                break;
            default:
                break;
        }

        return new ElementProperties(element, list, actions);
    }

    /// <summary>这条通道服务的元素。</summary>
    public PaperElement Element { get; }

    /// <summary>描述符清单：先 <c>id</c> 与 5 个几何字段，再按类型载荷字段顺序。</summary>
    public IReadOnlyList<PropertyDescriptor> Descriptors => _descriptors;

    /// <summary>
    /// 元素级结构动作清单（如表格列的增删）。多数类型为空。
    /// 与描述符一样是<b>快照</b>：列数变了要重新 <see cref="For"/>。
    /// 宿主把动作渲染成按钮，点击时把 <see cref="ElementAction.Key"/> 交给
    /// <see cref="EditorKernel.RunElementAction"/>——面板保持数据驱动，不硬编码动作。
    /// </summary>
    public IReadOnlyList<ElementAction> Actions => _actions;

    /// <summary>按键精确查找（区分大小写），不存在返回 null。</summary>
    /// <param name="key">描述符键，如 <c>text.fontSizePt</c>。</param>
    /// <returns>找到的描述符，否则 null。</returns>
    public PropertyDescriptor? Find(string key) => _byKey.TryGetValue(key, out var d) ? d : null;

    // ---------------- 公共字段（8 类型都有，id 只读） ----------------

    static List<PropertyDescriptor> Common() =>
    [
        ReadOnlyText("id", "编号（内核生成）"),
        Num<PaperElement>("xMm", "X 坐标 (mm)", el => el.XMm, (el, v) => el.XMm = v, min: 0, step: 0.1),
        Num<PaperElement>("yMm", "Y 坐标 (mm)", el => el.YMm, (el, v) => el.YMm = v, min: 0, step: 0.1),
        Num<PaperElement>("widthMm", "宽 (mm)", el => el.WidthMm, (el, v) => el.WidthMm = v, min: 0, step: 0.1),
        Num<PaperElement>("heightMm", "高 (mm)", el => el.HeightMm, (el, v) => el.HeightMm = v, min: 0, step: 0.1),
        Int("zIndex", "绘制层级", el => el.ZIndex, (el, v) => el.ZIndex = v, step: 1),
        ColorOpt<PaperElement>("backgroundColor", "背景色（留空透明）", el => el.BackgroundColor, (el, v) => el.BackgroundColor = v),
        ColorOpt<PaperElement>("borderColor", "边框色（留空不画）", el => el.BorderColor, (el, v) => el.BorderColor = v),
        Num<PaperElement>("borderWidthMm", "边框宽 (mm)", el => el.BorderWidthMm, (el, v) => el.BorderWidthMm = v, min: 0, max: 50, step: 0.05),
        Dash<PaperElement>("borderDashPatternMm", "边框虚线段长 (mm)", el => el.BorderDashPatternMm, (el, v) => el.BorderDashPatternMm = v),
        EnumProp<PaperElement, BorderSide>("borderSides", "边框边（可多选）", el => el.BorderSides, (el, v) => el.BorderSides = v),
        Num<PaperElement>("rotationDegrees", "旋转角度 (°)", el => el.RotationDegrees, (el, v) => el.RotationDegrees = v, min: -360, max: 360, step: 1),
        Num<PaperElement>("opacity", "不透明度 (0–1)", el => el.Opacity, (el, v) => el.Opacity = v, min: 0, max: 1, step: 0.05),
        StyleRef<PaperElement>("styleId", "样式引用（模板样式库）", el => el.StyleId, (el, v) => el.StyleId = v),
        MultilineStrOpt<PaperElement>("formatter", "格式化脚本 (JS)：function(value, options, target, templateData)，返回值替换显示内容（undefined 保持原值）", el => el.Formatter, (el, v) => el.Formatter = v),
        MultilineStrOpt<PaperElement>("styler", "样式脚本 (JS)：返回 {color, fontSize, ...} 或 CSS 字符串，优先级高于内联与样式库", el => el.Styler, (el, v) => el.Styler = v),
        MultilineStrOpt<PaperElement>("testData", "测试数据（占位符兜底值；表格按换行分行、制表符分列）", el => el.TestData, (el, v) => el.TestData = v),
    ];

    // ---------------- text ----------------

    static List<PropertyDescriptor> ForText()
    {
        const string P = ElementTypeKeys.Text + ".";
        return
        [
            StyleRef<TextElement>(P + "styleId", "样式引用（模板样式库）", t => t.Text.StyleId, (t, v) => t.Text.StyleId = v),
            MultilineStr<TextElement>(P + "content", "文本内容", t => t.Text.Content, (t, v) => t.Text.Content = v),
            Str<TextElement>(P + "fontFamily", "字体名", t => t.Text.FontFamily, (t, v) => t.Text.FontFamily = v),
            Num<TextElement>(P + "fontSizePt", "字号 (pt)", t => t.Text.FontSizePt, (t, v) => t.Text.FontSizePt = v, min: 1, max: 1000, step: 0.5),
            Bool<TextElement>(P + "bold", "加粗", t => t.Text.Bold, (t, v) => t.Text.Bold = v),
            Bool<TextElement>(P + "italic", "倾斜", t => t.Text.Italic, (t, v) => t.Text.Italic = v),
            Bool<TextElement>(P + "underline", "下划线", t => t.Text.Underline, (t, v) => t.Text.Underline = v),
            Bool<TextElement>(P + "strikethrough", "删除线", t => t.Text.Strikethrough, (t, v) => t.Text.Strikethrough = v),
            Color<TextElement>(P + "color", "字色", t => t.Text.Color, (t, v) => t.Text.Color = v),
            EnumProp<TextElement, HorizontalAlign>(P + "horizontalAlign", "水平对齐", t => t.Text.HorizontalAlign, (t, v) => t.Text.HorizontalAlign = v),
            EnumProp<TextElement, VerticalAlign>(P + "verticalAlign", "垂直对齐", t => t.Text.VerticalAlign, (t, v) => t.Text.VerticalAlign = v),
            Bool<TextElement>(P + "wrap", "按宽换行", t => t.Text.Wrap, (t, v) => t.Text.Wrap = v),
            Num<TextElement>(P + "lineSpacing", "行距倍数", t => t.Text.LineSpacing, (t, v) => t.Text.LineSpacing = v, min: 0.1, max: 10, step: 0.05),
            Num<TextElement>(P + "paddingLeftMm", "左内边距 (mm)", t => t.Text.PaddingLeftMm, (t, v) => t.Text.PaddingLeftMm = v, min: 0, step: 0.1),
            Num<TextElement>(P + "paddingRightMm", "右内边距 (mm)", t => t.Text.PaddingRightMm, (t, v) => t.Text.PaddingRightMm = v, min: 0, step: 0.1),
            Num<TextElement>(P + "paddingTopMm", "上内边距 (mm)", t => t.Text.PaddingTopMm, (t, v) => t.Text.PaddingTopMm = v, min: 0, step: 0.1),
            Num<TextElement>(P + "paddingBottomMm", "下内边距 (mm)", t => t.Text.PaddingBottomMm, (t, v) => t.Text.PaddingBottomMm = v, min: 0, step: 0.1),
            Num<TextElement>(P + "letterSpacingPt", "字间距 (pt)", t => t.Text.LetterSpacingPt, (t, v) => t.Text.LetterSpacingPt = v, min: 0, step: 0.1),
            Bool<TextElement>(P + "ellipsis", "超宽省略号", t => t.Text.Ellipsis, (t, v) => t.Text.Ellipsis = v),
        ];
    }

    // ---------------- line ----------------

    static List<PropertyDescriptor> ForLine()
    {
        const string P = ElementTypeKeys.Line + ".";
        return
        [
            StyleRef<LineElement>(P + "styleId", "样式引用（模板样式库）", t => t.Line.StyleId, (t, v) => t.Line.StyleId = v),
            Color<LineElement>(P + "strokeColor", "线色", t => t.Line.StrokeColor, (t, v) => t.Line.StrokeColor = v),
            Num<LineElement>(P + "strokeWidthMm", "线宽 (mm)", t => t.Line.StrokeWidthMm, (t, v) => t.Line.StrokeWidthMm = v, min: 0, max: 50, step: 0.05),
            EnumProp<LineElement, LineDirection>(P + "direction", "方向", t => t.Line.Direction, (t, v) => t.Line.Direction = v),
            Dash<LineElement>(P + "dashPatternMm", "虚线段长 (mm)", t => t.Line.DashPatternMm, (t, v) => t.Line.DashPatternMm = v),
        ];
    }

    // ---------------- rectangle / ellipse（同一个 ShapeOptions，键前缀不同） ----------------

    static List<PropertyDescriptor> ForShape(string typeKey)
    {
        var p = typeKey + ".";

        // 两种元素共用 ShapeOptions：矩形用圆角，椭圆只画内切椭圆，字段照样全给（少一条就少一个可编辑框）。
        Func<PaperElement, ShapeOptions> options = typeKey == ElementTypeKeys.Rectangle
            ? el => ((RectangleElement)el).Rectangle
            : el => ((EllipseElement)el).Ellipse;

        var cornerName = typeKey == ElementTypeKeys.Rectangle ? "圆角半径 (mm)" : "圆角半径 (mm)（椭圆不生效）";
        return
        [
            StyleRef<PaperElement>(p + "styleId", "样式引用（模板样式库）", el => options(el).StyleId, (el, v) => options(el).StyleId = v),
            ColorOpt<PaperElement>(p + "strokeColor", "描边色（留空不描边）", el => options(el).StrokeColor, (el, v) => options(el).StrokeColor = v),
            Num<PaperElement>(p + "strokeWidthMm", "描边宽 (mm)", el => options(el).StrokeWidthMm, (el, v) => options(el).StrokeWidthMm = v, min: 0, max: 50, step: 0.05),
            ColorOpt<PaperElement>(p + "fillColor", "填充色（留空不填充）", el => options(el).FillColor, (el, v) => options(el).FillColor = v),
            Num<PaperElement>(p + "cornerRadiusMm", cornerName, el => options(el).CornerRadiusMm, (el, v) => options(el).CornerRadiusMm = v, min: 0, step: 0.1),
            Dash<PaperElement>(p + "dashPatternMm", "虚线段长 (mm)", el => options(el).DashPatternMm, (el, v) => options(el).DashPatternMm = v),
        ];
    }

    // ---------------- image ----------------

    static List<PropertyDescriptor> ForImage()
    {
        const string P = ElementTypeKeys.Image + ".";
        return
        [
            EnumProp<ImageElement, ImageSourceType>(P + "sourceType", "图片来源", t => t.Image.SourceType, (t, v) => t.Image.SourceType = v),
            MultilineStrOpt<ImageElement>(P + "data", "Base64 图片数据", t => t.Image.Data, (t, v) => t.Image.Data = v),
            StrOpt<ImageElement>(P + "resolverKey", "资源键", t => t.Image.ResolverKey, (t, v) => t.Image.ResolverKey = v),
            EnumProp<ImageElement, ImageFitMode>(P + "fitMode", "适配方式", t => t.Image.FitMode, (t, v) => t.Image.FitMode = v),
            Num<ImageElement>(P + "cornerRadiusMm", "圆角半径 (mm)", t => t.Image.CornerRadiusMm, (t, v) => t.Image.CornerRadiusMm = v, min: 0, step: 0.1),
        ];
    }

    // ---------------- qrcode ----------------

    static List<PropertyDescriptor> ForQrCode()
    {
        // 载荷键是 "qrcode"（读时兼容 "qrCode"，但写出去就是 "qrcode"，通道跟写侧一致）。
        const string P = ElementTypeKeys.QrCode + ".";
        return
        [
            StyleRef<QrCodeElement>(P + "styleId", "样式引用（模板样式库）", t => t.QrCode.StyleId, (t, v) => t.QrCode.StyleId = v),
            MultilineStr<QrCodeElement>(P + "content", "编码内容", t => t.QrCode.Content, (t, v) => t.QrCode.Content = v),
            EnumProp<QrCodeElement, QrErrorCorrection>(P + "errorCorrection", "纠错等级", t => t.QrCode.ErrorCorrection, (t, v) => t.QrCode.ErrorCorrection = v),
            Color<QrCodeElement>(P + "foregroundColor", "前景色", t => t.QrCode.ForegroundColor, (t, v) => t.QrCode.ForegroundColor = v),
            ColorOpt<QrCodeElement>(P + "backgroundColor", "背景色（留空透明）", t => t.QrCode.BackgroundColor, (t, v) => t.QrCode.BackgroundColor = v),
            Num<QrCodeElement>(P + "quietZoneMm", "静区宽 (mm)", t => t.QrCode.QuietZoneMm, (t, v) => t.QrCode.QuietZoneMm = v, min: 0, step: 0.1),
            Str<QrCodeElement>(P + "caption", "可读文本", t => t.QrCode.Caption, (t, v) => t.QrCode.Caption = v),
            Bool<QrCodeElement>(P + "showCaption", "显示可读文本", t => t.QrCode.ShowCaption, (t, v) => t.QrCode.ShowCaption = v),
            Num<QrCodeElement>(P + "captionFontSizePt", "可读文本字号 (pt)", t => t.QrCode.CaptionFontSizePt, (t, v) => t.QrCode.CaptionFontSizePt = v, min: 1, max: 1000, step: 0.5),
            Str<QrCodeElement>(P + "captionFontFamily", "可读文本字体名", t => t.QrCode.CaptionFontFamily, (t, v) => t.QrCode.CaptionFontFamily = v),
            Color<QrCodeElement>(P + "captionColor", "可读文本颜色", t => t.QrCode.CaptionColor, (t, v) => t.QrCode.CaptionColor = v),
            EnumProp<QrCodeElement, HorizontalAlign>(P + "captionAlign", "可读文本对齐", t => t.QrCode.CaptionAlign, (t, v) => t.QrCode.CaptionAlign = v),
            Num<QrCodeElement>(P + "captionLineSpacing", "可读文本行距倍数", t => t.QrCode.CaptionLineSpacing, (t, v) => t.QrCode.CaptionLineSpacing = v, min: 0.1, max: 10, step: 0.05),
        ];
    }

    // ---------------- barcode ----------------

    static List<PropertyDescriptor> ForBarcode()
    {
        const string P = ElementTypeKeys.Barcode + ".";
        return
        [
            StyleRef<BarcodeElement>(P + "styleId", "样式引用（模板样式库）", t => t.Barcode.StyleId, (t, v) => t.Barcode.StyleId = v),
            Str<BarcodeElement>(P + "content", "编码内容", t => t.Barcode.Content, (t, v) => t.Barcode.Content = v),
            EnumProp<BarcodeElement, BarcodeSymbology>(P + "symbology", "码制", t => t.Barcode.Symbology, (t, v) => t.Barcode.Symbology = v),
            Color<BarcodeElement>(P + "barColor", "条色", t => t.Barcode.BarColor, (t, v) => t.Barcode.BarColor = v),
            ColorOpt<BarcodeElement>(P + "backgroundColor", "背景色（留空透明）", t => t.Barcode.BackgroundColor, (t, v) => t.Barcode.BackgroundColor = v),
            Bool<BarcodeElement>(P + "showText", "显示可读文本", t => t.Barcode.ShowText, (t, v) => t.Barcode.ShowText = v),
            Num<BarcodeElement>(P + "fontSizePt", "文本字号 (pt)", t => t.Barcode.FontSizePt, (t, v) => t.Barcode.FontSizePt = v, min: 1, max: 1000, step: 0.5),
            Str<BarcodeElement>(P + "fontFamily", "文本字体名", t => t.Barcode.FontFamily, (t, v) => t.Barcode.FontFamily = v),
            Num<BarcodeElement>(P + "quietZoneMm", "静区宽 (mm)", t => t.Barcode.QuietZoneMm, (t, v) => t.Barcode.QuietZoneMm = v, min: 0, step: 0.1),
        ];
    }

    // ---------------- table ----------------

    static List<PropertyDescriptor> ForTable(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".";
        const string headerFontSizeKey = P + "headerFontSizePt";
        var list = new List<PropertyDescriptor>
        {
            StyleRef<TableElement>(P + "styleId", "样式引用（模板样式库，管整表字体/字号/行距/字色）", t => t.Table.StyleId, (t, v) => t.Table.StyleId = v),
            ColorOpt<TableElement>(P + "gridColor", "网格线色（留空不画线）", t => t.Table.GridColor, (t, v) => t.Table.GridColor = v),
            Num<TableElement>(P + "gridWidthMm", "网格线宽 (mm)", t => t.Table.GridWidthMm, (t, v) => t.Table.GridWidthMm = v, min: 0, max: 50, step: 0.05),
            Bool<TableElement>(P + "showBorder", "绘制外框", t => t.Table.ShowBorder, (t, v) => t.Table.ShowBorder = v),
            Bool<TableElement>(P + "showHeader", "绘制表头", t => t.Table.ShowHeader, (t, v) => t.Table.ShowHeader = v),
            ColorOpt<TableElement>(P + "headerFillColor", "表头底色（留空不填）", t => t.Table.HeaderFillColor, (t, v) => t.Table.HeaderFillColor = v),
            Bool<TableElement>(P + "headerBold", "表头加粗", t => t.Table.HeaderBold, (t, v) => t.Table.HeaderBold = v),
            Str<TableElement>(P + "field", "数据源字段（空=按列首段推断）", t => t.Table.Field, (t, v) => t.Table.Field = v),
            Num<TableElement>(P + "headerRowHeightMm", "表头行高 (mm)（-1 自适应）", t => t.Table.HeaderRowHeightMm, (t, v) => t.Table.HeaderRowHeightMm = v, min: -1, step: 0.1),
            new PropertyDescriptor(headerFontSizeKey, "表头字号 (pt)（留空继承正文）", PropertyKind.Number,
                el => ((TableElement)el).Table.HeaderFontSizePt,
                (el, v) => ((TableElement)el).Table.HeaderFontSizePt = PropertyValues.OptionalDouble(headerFontSizeKey, v, allowNull: true),
                min: 1, max: 1000, step: 0.5),
            ColorOpt<TableElement>(P + "stripeColor", "斑马纹底色（留空关闭）", t => t.Table.StripeColor, (t, v) => t.Table.StripeColor = v),
            Num<TableElement>(P + "fontSizePt", "正文字号 (pt)", t => t.Table.FontSizePt, (t, v) => t.Table.FontSizePt = v, min: 1, max: 1000, step: 0.5),
            Str<TableElement>(P + "fontFamily", "字体名", t => t.Table.FontFamily, (t, v) => t.Table.FontFamily = v),
            Num<TableElement>(P + "rowHeightMm", "行高 (mm)（-1 自适应）", t => t.Table.RowHeightMm, (t, v) => t.Table.RowHeightMm = v, min: -1, step: 0.1),
            Num<TableElement>(P + "cellPaddingMm", "单元格内边距 (mm)", t => t.Table.CellPaddingMm, (t, v) => t.Table.CellPaddingMm = v, min: 0, step: 0.1),
            Num<TableElement>(P + "lineSpacing", "行距倍数", t => t.Table.LineSpacing, (t, v) => t.Table.LineSpacing = v, min: 0.1, max: 10, step: 0.05),
            ColorOpt<TableElement>(P + "textColor", "正文字色（留空黑色）", t => t.Table.TextColor, (t, v) => t.Table.TextColor = v),
            Rows(P + "rows", "静态数据行（换行分行、制表符分列）"),
        };

        list.AddRange(ForColumns(table));
        list.AddRange(ForHeaderRows(table));
        list.AddRange(ForConditionalStyles(table));
        list.AddRange(ForHighlights(table));
        list.AddRange(ForRowSpans(table));
        return list;
    }

    /// <summary>表格的结构动作：列、条件样式、关键词样式与静态行合并的增删。</summary>
    static List<ElementAction> TableActions(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".";
        var actions = new List<ElementAction> { new(P + "appendColumn", "追加列") };
        for (var i = 0; i < table.Table.Columns.Count; i++)
        {
            actions.Add(new ElementAction($"{P}columns[{i}].insertBefore", $"在列{i + 1}前插入"));
            actions.Add(new ElementAction($"{P}columns[{i}].remove", $"删除列{i + 1}"));
        }

        actions.Add(new ElementAction(P + "appendConditional", "追加条件样式"));
        for (var i = 0; i < table.Table.ConditionalStyles.Count; i++)
            actions.Add(new ElementAction($"{P}conditionalStyles[{i}].remove", $"删除条件样式{i + 1}"));

        actions.Add(new ElementAction(P + "appendHighlight", "追加关键词样式"));
        for (var i = 0; i < table.Table.Highlights.Count; i++)
            actions.Add(new ElementAction($"{P}highlights[{i}].remove", $"删除关键词样式{i + 1}"));

        actions.Add(new ElementAction(P + "appendRowSpan", "追加静态行合并"));
        for (var i = 0; i < table.Table.RowSpans.Count; i++)
            actions.Add(new ElementAction($"{P}rowSpans[{i}].remove", $"删除合并{i + 1}"));

        return actions;
    }

    /// <summary>按当前列数逐列展开描述符；列的增删是结构命令，不走属性通道。</summary>
    static IEnumerable<PropertyDescriptor> ForColumns(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".columns[";
        for (var i = 0; i < table.Table.Columns.Count; i++)
        {
            var index = i;
            var head = $"{P}{index}].";
            var label = $"列{index + 1}";

            var styleKey = head + "styleId";
            yield return new PropertyDescriptor(styleKey, $"{label}·样式引用（模板样式库）", PropertyKind.StyleRef,
                el => Peek(el, index)?.StyleId,
                (el, v) => Column(el, index, styleKey).StyleId = PropertyValues.OptionalText(styleKey, v));

            var titleKey = head + "title";
            yield return new PropertyDescriptor(titleKey, $"{label}·标题", PropertyKind.Text,
                el => Peek(el, index)?.Title,
                (el, v) => Column(el, index, titleKey).Title = PropertyValues.ToText(titleKey, v));

            var widthKey = head + "widthMm";
            yield return new PropertyDescriptor(widthKey, $"{label}·列宽 (mm)（-1 均分）", PropertyKind.Number,
                el => Peek(el, index)?.WidthMm,
                (el, v) => Column(el, index, widthKey).WidthMm = PropertyValues.ToDouble(widthKey, v),
                min: -1, step: 0.1);

            var fieldKey = head + "field";
            yield return new PropertyDescriptor(fieldKey, $"{label}·数据字段", PropertyKind.Text,
                el => Peek(el, index)?.Field,
                (el, v) => Column(el, index, fieldKey).Field = PropertyValues.ToText(fieldKey, v));

            var alignKey = head + "align";
            var alignNames = Enum.GetNames<HorizontalAlign>();
            yield return new PropertyDescriptor(alignKey, $"{label}·对齐", PropertyKind.Enum,
                el => Peek(el, index)?.Align,
                (el, v) => Column(el, index, alignKey).Align = PropertyValues.ToEnum<HorizontalAlign>(alignKey, v, alignNames),
                options: alignNames);

            var fontKey = head + "fontSizePt";
            yield return new PropertyDescriptor(fontKey, $"{label}·字号 (pt)（留空继承表体）", PropertyKind.Number,
                el => Peek(el, index)?.FontSizePt,
                (el, v) => Column(el, index, fontKey).FontSizePt = PropertyValues.OptionalDouble(fontKey, v, allowNull: true),
                min: 1, max: 1000, step: 0.5);

            var formatterKey = head + "formatter";
            yield return new PropertyDescriptor(formatterKey, $"{label}·格式化脚本 (JS)：function(value, row, index, options)", PropertyKind.MultilineText,
                el => Peek(el, index)?.Formatter,
                (el, v) => Column(el, index, formatterKey).Formatter = PropertyValues.OptionalText(formatterKey, v));

            var stylerKey = head + "styler";
            yield return new PropertyDescriptor(stylerKey, $"{label}·样式脚本 (JS)：优先级高于条件样式与列内联", PropertyKind.MultilineText,
                el => Peek(el, index)?.Styler,
                (el, v) => Column(el, index, stylerKey).Styler = PropertyValues.OptionalText(stylerKey, v));

            var sequenceKey = head + "sequence";
            yield return new PropertyDescriptor(sequenceKey, $"{label}·显示行序号", PropertyKind.Boolean,
                el => Peek(el, index)?.Sequence,
                (el, v) => Column(el, index, sequenceKey).Sequence = PropertyValues.ToBool(sequenceKey, v));

            var summaryKey = head + "summary";
            var summaryNames = Enum.GetNames<TableSummary>();
            yield return new PropertyDescriptor(summaryKey, $"{label}·尾行汇总", PropertyKind.Enum,
                el => Peek(el, index)?.Summary,
                (el, v) => Column(el, index, summaryKey).Summary = PropertyValues.ToEnum<TableSummary>(summaryKey, v, summaryNames),
                options: summaryNames);

            var summaryLabelKey = head + "summaryLabel";
            yield return new PropertyDescriptor(summaryLabelKey, $"{label}·汇总前缀", PropertyKind.Text,
                el => Peek(el, index)?.SummaryLabel,
                (el, v) => Column(el, index, summaryLabelKey).SummaryLabel = PropertyValues.ToText(summaryLabelKey, v));

            var summaryDecimalsKey = head + "summaryDecimals";
            yield return new PropertyDescriptor(summaryDecimalsKey, $"{label}·汇总小数位", PropertyKind.Integer,
                el => Peek(el, index)?.SummaryDecimals,
                (el, v) => Column(el, index, summaryDecimalsKey).SummaryDecimals = PropertyValues.ToInt(summaryDecimalsKey, v),
                step: 1);

            var mergeKey = head + "mergeSameValue";
            yield return new PropertyDescriptor(mergeKey, $"{label}·同值合并（连续数据行）", PropertyKind.Boolean,
                el => Peek(el, index)?.MergeSameValue,
                (el, v) => Column(el, index, mergeKey).MergeSameValue = PropertyValues.ToBool(mergeKey, v));
        }
    }

    /// <summary>按当前表头单元格数逐格展开描述符；表头的增删是结构命令，不走属性通道。</summary>
    static IEnumerable<PropertyDescriptor> ForHeaderRows(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".headerRows[";
        for (var i = 0; i < table.Table.HeaderRows.Count; i++)
        {
            var index = i;
            var head = $"{P}{index}].";
            var label = $"表头{index + 1}";

            var styleKey = head + "styleId";
            yield return new PropertyDescriptor(styleKey, $"{label}·样式引用（模板样式库）", PropertyKind.StyleRef,
                el => PeekHeader(el, index)?.StyleId,
                (el, v) => HeaderCell(el, index, styleKey).StyleId = PropertyValues.OptionalText(styleKey, v));

            var rowKey = head + "row";
            yield return new PropertyDescriptor(rowKey, $"{label}·行", PropertyKind.Integer,
                el => PeekHeader(el, index)?.Row,
                (el, v) => HeaderCell(el, index, rowKey).Row = PropertyValues.ToInt(rowKey, v),
                step: 1);

            var columnKey = head + "column";
            yield return new PropertyDescriptor(columnKey, $"{label}·列", PropertyKind.Integer,
                el => PeekHeader(el, index)?.Column,
                (el, v) => HeaderCell(el, index, columnKey).Column = PropertyValues.ToInt(columnKey, v),
                step: 1);

            var titleKey = head + "title";
            yield return new PropertyDescriptor(titleKey, $"{label}·标题", PropertyKind.Text,
                el => PeekHeader(el, index)?.Title,
                (el, v) => HeaderCell(el, index, titleKey).Title = PropertyValues.ToText(titleKey, v));

            var colSpanKey = head + "colSpan";
            yield return new PropertyDescriptor(colSpanKey, $"{label}·跨列数", PropertyKind.Integer,
                el => PeekHeader(el, index)?.ColSpan,
                (el, v) => HeaderCell(el, index, colSpanKey).ColSpan = PropertyValues.ToInt(colSpanKey, v),
                min: 1, step: 1);

            var rowSpanKey = head + "rowSpan";
            yield return new PropertyDescriptor(rowSpanKey, $"{label}·跨行数", PropertyKind.Integer,
                el => PeekHeader(el, index)?.RowSpan,
                (el, v) => HeaderCell(el, index, rowSpanKey).RowSpan = PropertyValues.ToInt(rowSpanKey, v),
                min: 1, step: 1);

            var alignKey = head + "align";
            var alignNames = Enum.GetNames<HorizontalAlign>();
            yield return new PropertyDescriptor(alignKey, $"{label}·对齐", PropertyKind.Enum,
                el => PeekHeader(el, index)?.Align,
                (el, v) => HeaderCell(el, index, alignKey).Align = PropertyValues.ToEnum<HorizontalAlign>(alignKey, v, alignNames),
                options: alignNames);

            var fontKey = head + "fontSizePt";
            yield return new PropertyDescriptor(fontKey, $"{label}·字号 (pt)（留空继承表头）", PropertyKind.Number,
                el => PeekHeader(el, index)?.FontSizePt,
                (el, v) => HeaderCell(el, index, fontKey).FontSizePt = PropertyValues.OptionalDouble(fontKey, v, allowNull: true),
                min: 1, max: 1000, step: 0.5);
        }
    }

    /// <summary>按当前条数逐条展开条件样式描述符；增删是结构命令，不走属性通道。</summary>
    static IEnumerable<PropertyDescriptor> ForConditionalStyles(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".conditionalStyles[";
        for (var i = 0; i < table.Table.ConditionalStyles.Count; i++)
        {
            var index = i;
            var head = $"{P}{index}].";
            var label = $"条件{index + 1}";

            var styleKey = head + "styleId";
            yield return new PropertyDescriptor(styleKey, $"{label}·样式引用（模板样式库，内联留空处补位）", PropertyKind.StyleRef,
                el => PeekConditional(el, index)?.StyleId,
                (el, v) => Conditional(el, index, styleKey).StyleId = PropertyValues.OptionalText(styleKey, v));

            var fieldKey = head + "field";
            yield return new PropertyDescriptor(fieldKey, $"{label}·取值字段（空=用格内文本）", PropertyKind.Text,
                el => PeekConditional(el, index)?.Field,
                (el, v) => Conditional(el, index, fieldKey).Field = PropertyValues.ToText(fieldKey, v));

            var operatorKey = head + "operator";
            var operatorNames = Enum.GetNames<TableConditionOperator>();
            yield return new PropertyDescriptor(operatorKey, $"{label}·比较方式", PropertyKind.Enum,
                el => PeekConditional(el, index)?.Operator,
                (el, v) => Conditional(el, index, operatorKey).Operator = PropertyValues.ToEnum<TableConditionOperator>(operatorKey, v, operatorNames),
                options: operatorNames);

            var valueKey = head + "value";
            yield return new PropertyDescriptor(valueKey, $"{label}·比较值", PropertyKind.Text,
                el => PeekConditional(el, index)?.Value,
                (el, v) => Conditional(el, index, valueKey).Value = PropertyValues.OptionalText(valueKey, v));

            var columnKey = head + "column";
            yield return new PropertyDescriptor(columnKey, $"{label}·限定列字段（空=整行）", PropertyKind.Text,
                el => PeekConditional(el, index)?.Column,
                (el, v) => Conditional(el, index, columnKey).Column = PropertyValues.OptionalText(columnKey, v));

            var bgKey = head + "backgroundColor";
            yield return new PropertyDescriptor(bgKey, $"{label}·底色（留空不覆盖）", PropertyKind.Color,
                el => PeekConditional(el, index)?.BackgroundColor,
                (el, v) => Conditional(el, index, bgKey).BackgroundColor = PropertyValues.OptionalColor(bgKey, v, allowNull: true));

            var fgKey = head + "textColor";
            yield return new PropertyDescriptor(fgKey, $"{label}·字色（留空不覆盖）", PropertyKind.Color,
                el => PeekConditional(el, index)?.TextColor,
                (el, v) => Conditional(el, index, fgKey).TextColor = PropertyValues.OptionalColor(fgKey, v, allowNull: true));

            var boldKey = head + "bold";
            yield return new PropertyDescriptor(boldKey, $"{label}·加粗（勾选覆盖）", PropertyKind.Boolean,
                el => PeekConditional(el, index)?.Bold,
                (el, v) => Conditional(el, index, boldKey).Bold = PropertyValues.OptionalBool(boldKey, v) is true ? true : null);

            var italicKey = head + "italic";
            yield return new PropertyDescriptor(italicKey, $"{label}·倾斜（勾选覆盖）", PropertyKind.Boolean,
                el => PeekConditional(el, index)?.Italic,
                (el, v) => Conditional(el, index, italicKey).Italic = PropertyValues.OptionalBool(italicKey, v) is true ? true : null);

            var ptKey = head + "fontSizePt";
            yield return new PropertyDescriptor(ptKey, $"{label}·字号 (pt)（留空不覆盖）", PropertyKind.Number,
                el => PeekConditional(el, index)?.FontSizePt,
                (el, v) => Conditional(el, index, ptKey).FontSizePt = PropertyValues.OptionalDouble(ptKey, v, allowNull: true),
                min: 1, max: 1000, step: 0.5);

            var familyKey = head + "fontFamily";
            yield return new PropertyDescriptor(familyKey, $"{label}·字体名（留空不覆盖）", PropertyKind.Text,
                el => PeekConditional(el, index)?.FontFamily,
                (el, v) => Conditional(el, index, familyKey).FontFamily = PropertyValues.OptionalText(familyKey, v));
        }
    }

    /// <summary>按当前条数逐条展开关键词（片段）样式描述符；增删是结构命令，不走属性通道。</summary>
    static IEnumerable<PropertyDescriptor> ForHighlights(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".highlights[";
        for (var i = 0; i < table.Table.Highlights.Count; i++)
        {
            var index = i;
            var head = $"{P}{index}].";
            var label = $"关键词{index + 1}";

            var styleKey = head + "styleId";
            yield return new PropertyDescriptor(styleKey, $"{label}·样式引用（模板样式库，内联留空处补位）", PropertyKind.StyleRef,
                el => PeekHighlight(el, index)?.StyleId,
                (el, v) => Highlight(el, index, styleKey).StyleId = PropertyValues.OptionalText(styleKey, v));

            var keywordKey = head + "keyword";
            yield return new PropertyDescriptor(keywordKey, $"{label}·关键词（空=整格）", PropertyKind.Text,
                el => PeekHighlight(el, index)?.Keyword,
                (el, v) => Highlight(el, index, keywordKey).Keyword = PropertyValues.ToText(keywordKey, v));

            var columnKey = head + "column";
            yield return new PropertyDescriptor(columnKey, $"{label}·限定列字段（空=所有列）", PropertyKind.Text,
                el => PeekHighlight(el, index)?.Column,
                (el, v) => Highlight(el, index, columnKey).Column = PropertyValues.OptionalText(columnKey, v));

            var ignoreCaseKey = head + "ignoreCase";
            yield return new PropertyDescriptor(ignoreCaseKey, $"{label}·忽略大小写", PropertyKind.Boolean,
                el => PeekHighlight(el, index)?.IgnoreCase,
                (el, v) => Highlight(el, index, ignoreCaseKey).IgnoreCase = PropertyValues.ToBool(ignoreCaseKey, v));

            var bgKey = head + "backgroundColor";
            yield return new PropertyDescriptor(bgKey, $"{label}·片段底色（留空不覆盖）", PropertyKind.Color,
                el => PeekHighlight(el, index)?.BackgroundColor,
                (el, v) => Highlight(el, index, bgKey).BackgroundColor = PropertyValues.OptionalColor(bgKey, v, allowNull: true));

            var fgKey = head + "textColor";
            yield return new PropertyDescriptor(fgKey, $"{label}·片段字色（留空不覆盖）", PropertyKind.Color,
                el => PeekHighlight(el, index)?.TextColor,
                (el, v) => Highlight(el, index, fgKey).TextColor = PropertyValues.OptionalColor(fgKey, v, allowNull: true));

            var boldKey = head + "bold";
            yield return new PropertyDescriptor(boldKey, $"{label}·加粗（勾选覆盖）", PropertyKind.Boolean,
                el => PeekHighlight(el, index)?.Bold,
                (el, v) => Highlight(el, index, boldKey).Bold = PropertyValues.OptionalBool(boldKey, v) is true ? true : null);

            var italicKey = head + "italic";
            yield return new PropertyDescriptor(italicKey, $"{label}·倾斜（勾选覆盖）", PropertyKind.Boolean,
                el => PeekHighlight(el, index)?.Italic,
                (el, v) => Highlight(el, index, italicKey).Italic = PropertyValues.OptionalBool(italicKey, v) is true ? true : null);

            var ptKey = head + "fontSizePt";
            yield return new PropertyDescriptor(ptKey, $"{label}·字号 (pt)（留空不覆盖）", PropertyKind.Number,
                el => PeekHighlight(el, index)?.FontSizePt,
                (el, v) => Highlight(el, index, ptKey).FontSizePt = PropertyValues.OptionalDouble(ptKey, v, allowNull: true),
                min: 1, max: 1000, step: 0.5);

            var familyKey = head + "fontFamily";
            yield return new PropertyDescriptor(familyKey, $"{label}·字体名（留空不覆盖）", PropertyKind.Text,
                el => PeekHighlight(el, index)?.FontFamily,
                (el, v) => Highlight(el, index, familyKey).FontFamily = PropertyValues.OptionalText(familyKey, v));
        }
    }

    /// <summary>按当前条数逐条展开静态行合并描述符；增删是结构命令，不走属性通道。</summary>
    static IEnumerable<PropertyDescriptor> ForRowSpans(TableElement table)
    {
        const string P = ElementTypeKeys.Table + ".rowSpans[";
        for (var i = 0; i < table.Table.RowSpans.Count; i++)
        {
            var index = i;
            var head = $"{P}{index}].";
            var label = $"合并{index + 1}";

            var rowKey = head + "row";
            yield return new PropertyDescriptor(rowKey, $"{label}·起始行", PropertyKind.Integer,
                el => PeekRowSpan(el, index)?.Row,
                (el, v) => RowSpan(el, index, rowKey).Row = PropertyValues.ToInt(rowKey, v),
                min: 0, step: 1);

            var columnKey = head + "column";
            yield return new PropertyDescriptor(columnKey, $"{label}·起始列", PropertyKind.Integer,
                el => PeekRowSpan(el, index)?.Column,
                (el, v) => RowSpan(el, index, columnKey).Column = PropertyValues.ToInt(columnKey, v),
                min: 0, step: 1);

            var rowSpanKey = head + "rowSpan";
            yield return new PropertyDescriptor(rowSpanKey, $"{label}·跨行数", PropertyKind.Integer,
                el => PeekRowSpan(el, index)?.RowSpan,
                (el, v) => RowSpan(el, index, rowSpanKey).RowSpan = PropertyValues.ToInt(rowSpanKey, v),
                min: 1, step: 1);

            var colSpanKey = head + "colSpan";
            yield return new PropertyDescriptor(colSpanKey, $"{label}·跨列数", PropertyKind.Integer,
                el => PeekRowSpan(el, index)?.ColSpan,
                (el, v) => RowSpan(el, index, colSpanKey).ColSpan = PropertyValues.ToInt(colSpanKey, v),
                min: 1, step: 1);
        }
    }

    static TableColumn? Peek(PaperElement element, int index)
    {
        var columns = ((TableElement)element).Table.Columns;
        return index < columns.Count ? columns[index] : null;
    }

    static TableColumn Column(PaperElement element, int index, string key)
    {
        var columns = ((TableElement)element).Table.Columns;
        if (index >= columns.Count)
        {
            throw new InvalidOperationException(
                $"属性 \"{key}\" 指向的列已不存在（当前只有 {columns.Count} 列）。描述符是快照，" +
                "列数变化后必须重新调用 ElementProperties.For。");
        }

        return columns[index];
    }

    static TableHeaderCell? PeekHeader(PaperElement element, int index)
    {
        var cells = ((TableElement)element).Table.HeaderRows;
        return index < cells.Count ? cells[index] : null;
    }

    static TableHeaderCell HeaderCell(PaperElement element, int index, string key)
    {
        var cells = ((TableElement)element).Table.HeaderRows;
        if (index >= cells.Count)
        {
            throw new InvalidOperationException(
                $"属性 \"{key}\" 指向的表头单元格已不存在（当前只有 {cells.Count} 个）。描述符是快照，" +
                "表头行变化后必须重新调用 ElementProperties.For。");
        }

        return cells[index];
    }

    static TableConditionalStyle? PeekConditional(PaperElement element, int index)
    {
        var styles = ((TableElement)element).Table.ConditionalStyles;
        return index < styles.Count ? styles[index] : null;
    }

    static TableConditionalStyle Conditional(PaperElement element, int index, string key)
    {
        var styles = ((TableElement)element).Table.ConditionalStyles;
        if (index >= styles.Count)
        {
            throw new InvalidOperationException(
                $"属性 \"{key}\" 指向的条件样式已不存在（当前只有 {styles.Count} 条）。描述符是快照，" +
                "规则数变化后必须重新调用 ElementProperties.For。");
        }

        return styles[index];
    }

    static TableHighlightRule? PeekHighlight(PaperElement element, int index)
    {
        var rules = ((TableElement)element).Table.Highlights;
        return index < rules.Count ? rules[index] : null;
    }

    static TableHighlightRule Highlight(PaperElement element, int index, string key)
    {
        var rules = ((TableElement)element).Table.Highlights;
        if (index >= rules.Count)
        {
            throw new InvalidOperationException(
                $"属性 \"{key}\" 指向的关键词样式已不存在（当前只有 {rules.Count} 条）。描述符是快照，" +
                "规则数变化后必须重新调用 ElementProperties.For。");
        }

        return rules[index];
    }

    static TableRowSpan? PeekRowSpan(PaperElement element, int index)
    {
        var spans = ((TableElement)element).Table.RowSpans;
        return index < spans.Count ? spans[index] : null;
    }

    static TableRowSpan RowSpan(PaperElement element, int index, string key)
    {
        var spans = ((TableElement)element).Table.RowSpans;
        if (index >= spans.Count)
        {
            throw new InvalidOperationException(
                $"属性 \"{key}\" 指向的静态行合并已不存在（当前只有 {spans.Count} 条）。描述符是快照，" +
                "合并数变化后必须重新调用 ElementProperties.For。");
        }

        return spans[index];
    }

    // ---------------- 表格行：一条多行文本承载二维表 ----------------

    static PropertyDescriptor Rows(string key, string displayName) => new(
        key, displayName, PropertyKind.MultilineText,
        el => EncodeRows(((TableElement)el).Table.Rows),
        (el, v) => ((TableElement)el).Table.Rows = DecodeRows(key, v));

    static string EncodeRows(List<List<string>> rows)
        => string.Join(RowSeparator, rows.Select(row => string.Join(CellSeparator, row)));

    static List<List<string>> DecodeRows(string key, object? value)
    {
        // Rows 在模型里不可空，所以 null 属宿主接线错误；清空要给 ""。
        var text = PropertyValues.ToText(key, value);
        var rows = new List<List<string>>();
        if (text.Length == 0)
            return rows;

        // 全空行丢弃：否则"清空文本框"会留下若干 [""] 行，渲染出一张空表而不是无表。
        foreach (var line in text.Split(RowSeparator))
        {
            var row = line.TrimEnd('\r');
            if (row.Length > 0)
                rows.Add(row.Split(CellSeparator).ToList());
        }

        return rows;
    }

    // ---------------- 描述符工厂 ----------------

    static PropertyDescriptor ReadOnlyText(string key, string displayName) => new(
        key, displayName, PropertyKind.Text,
        el => el.Id,
        static (el, v) => throw new ArgumentException("属性 \"id\" 只读：元素编号由内核生成与维护，宿主不得改写。", paramName: "id"),
        readOnly: true);

    static PropertyDescriptor Num<T>(string key, string displayName, Func<T, double> get, Action<T, double> set, double? min = null, double? max = null, double? step = null)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Number, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToDouble(key, v)), min: min, max: max, step: step);

    static PropertyDescriptor Int(string key, string displayName, Func<PaperElement, int> get, Action<PaperElement, int> set, double? step = null) => new(
        key, displayName, PropertyKind.Integer, el => get(el), (el, v) => set(el, PropertyValues.ToInt(key, v)), step: step);

    static PropertyDescriptor Bool<T>(string key, string displayName, Func<T, bool> get, Action<T, bool> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Boolean, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToBool(key, v)));

    static PropertyDescriptor Str<T>(string key, string displayName, Func<T, string> get, Action<T, string> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Text, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToText(key, v)));

    static PropertyDescriptor StrOpt<T>(string key, string displayName, Func<T, string?> get, Action<T, string?> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Text, el => get((T)el), (el, v) => set((T)el, PropertyValues.OptionalText(key, v)));

    static PropertyDescriptor MultilineStr<T>(string key, string displayName, Func<T, string> get, Action<T, string> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.MultilineText, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToText(key, v)));

    static PropertyDescriptor MultilineStrOpt<T>(string key, string displayName, Func<T, string?> get, Action<T, string?> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.MultilineText, el => get((T)el), (el, v) => set((T)el, PropertyValues.OptionalText(key, v)));

    static PropertyDescriptor Color<T>(string key, string displayName, Func<T, SKColor> get, Action<T, SKColor> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Color, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToColor(key, v)));

    static PropertyDescriptor ColorOpt<T>(string key, string displayName, Func<T, SKColor?> get, Action<T, SKColor?> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.Color, el => get((T)el), (el, v) => set((T)el, PropertyValues.OptionalColor(key, v, allowNull: true)));

    static PropertyDescriptor Dash<T>(string key, string displayName, Func<T, double[]?> get, Action<T, double[]?> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.DashPattern, el => get((T)el), (el, v) => set((T)el, PropertyValues.ToDash(key, v)));

    /// <summary>样式库引用：值是模板样式库的样式 id（可空）。候选项是运行时样式清单，宿主自己填下拉。</summary>
    static PropertyDescriptor StyleRef<T>(string key, string displayName, Func<T, string?> get, Action<T, string?> set)
        where T : PaperElement
        => new(key, displayName, PropertyKind.StyleRef, el => get((T)el), (el, v) => set((T)el, PropertyValues.OptionalText(key, v)));

    static PropertyDescriptor EnumProp<T, E>(string key, string displayName, Func<T, E> get, Action<T, E> set)
        where T : PaperElement
        where E : struct, Enum
    {
        var names = Enum.GetNames<E>();
        return new PropertyDescriptor(key, displayName, PropertyKind.Enum,
            el => get((T)el),
            (el, v) => set((T)el, PropertyValues.ToEnum<E>(key, v, names)),
            options: names);
    }
}

/// <summary>
/// 元素级结构动作（表格列的增删等）。与 <see cref="PropertyDescriptor"/> 一样是纯数据——
/// 属性通道只声明"有什么动作"，不持有内核；宿主把动作渲染成按钮，点击时把 <see cref="Key"/>
/// 交给 <see cref="EditorKernel.RunElementAction"/>。动作不进描述符：它们不是"某个字段的值"，
/// 没有单一编辑控件能表达，本质是结构变更命令。
/// </summary>
/// <param name="Key">动作地址，与描述符键同风格（如 <c>table.appendColumn</c>、<c>table.columns[0].remove</c>）。</param>
/// <param name="DisplayName">按钮文案，如"追加列"。</param>
public sealed record ElementAction(string Key, string DisplayName);
