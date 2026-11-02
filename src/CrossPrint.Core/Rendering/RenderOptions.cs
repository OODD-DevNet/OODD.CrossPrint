using SkiaSharp;

namespace CrossPrint.Core;

/// <summary>渲染目标与视口参数。像素仅在渲染瞬间出现，模板内不存像素值。</summary>
/// <remarks>
/// 用 <c>record</c> 而非 <c>class</c>：派生选项（<see cref="WithDpi"/>）靠 <c>with</c> 复制全部字段，
/// 新增属性不会漏抄——手写初始化器版本曾经只能带上当时存在的字段；同时 <c>with</c> 会重新执行
/// init 访问器，所以"入口拒掉非法 Dpi"这条闸对 <c>new</c> 和 <c>with</c> 都成立。
/// </remarks>
public sealed record RenderOptions
{
    double _dpi = 96;
    double _pdfRasterDpi = 2540;

    /// <summary>位图目标的专业 DPI：预览 96，导出 300。PDF 由导出器传 72（1 单位 = 1 PDF 点）。</summary>
    /// <remarks>0/NaN/Infinity 在入口就拒：<see cref="UnitsPerMm"/> 直接除以它，漏进渲染期只会把整页换算成
    /// 0 或 NaN 尺寸——产物变成零墨或空页，比一条参数异常难查得多。Designer 侧同一个坑在
    /// <c>Viewport.Dpi</c> 也有一道闸，口径一致。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">值不是有限正数。</exception>
    public double Dpi
    {
        get => _dpi;
        init => _dpi = RequirePositive(value, nameof(Dpi));
    }

    /// <summary>页面底色。</summary>
    public SKColor PageBackgroundColor { get; init; } = SKColors.White;

    /// <summary>
    /// 交给 <c>SKDocumentPdfMetadata.RasterDpi</c> 的页面量化网格密度。
    /// 实测 2.88.9（探针 §12）：它**不重采样内嵌位图**，只把 MediaBox 量化到
    /// <c>round(mm × dpi / 25.4) × 72 / dpi</c> 网格上。300 时 A4 宽 209.973mm（差 0.027mm）；
    /// 2540 即 0.01mm 网格，A4 差 1e-5mm。故默认取 2540，让"PDF 上量尺子等于 mm"真正成立。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">值不是有限正数——同一公式的分母。</exception>
    public double PdfRasterDpi
    {
        get => _pdfRasterDpi;
        init => _pdfRasterDpi = RequirePositive(value, nameof(PdfRasterDpi));
    }

    /// <summary>
    /// PDF 文字转轮廓（不嵌入字体文件）。
    /// 实测 2.88.9 的 Skia **不做字体子集化**：5 个拉丁字符就让 PDF 膨胀 506KB，
    /// 一个含中文的模板因为整份内嵌微软雅黑直接到 7.5MB。
    /// 置为 true 时改画字形轮廓，体积按字形数线性增长且不再内嵌字体，代价是文字不可选中、不可检索。
    /// 仅 PDF 目标生效；位图目标本来就是栅格化，无此问题。
    /// </summary>
    public bool PdfTextAsOutlines { get; init; }

    /// <summary>
    /// 表格跨页自动分页（FR-BND-05）。true 时 PDF 导出会把"内容超出元素高"的那张表格切成多页续排：
    /// 第 1 页画信头 + 表格开头一段；第 2 页起是纯表格续行（表格顶格接着排、只带页码页脚）；
    /// 最后一页回填表格下方的落款区（合计/签字等）。汇总列按本页数据行重算（本页小计），
    /// 页码绑定 <c>{{pageNo}}</c>/<c>{{pageCount}}</c> 生效。
    /// v1 边界：只对定高纸（<c>heightMm &gt; 0</c>）启用；只支持一张溢出表格驱动页数，
    /// 多张同时溢出时维持单页渲染 + <see cref="RenderIssueKind.TableOverflow"/> 告警（宁可报错也不猜着切）。
    /// </summary>
    public bool AllowTablePagination { get; init; }

    /// <summary>每毫米对应的画布单位数（画布已按 1 单位 = 1/72 英寸或按 Dpi 缩放时由引擎计算）。</summary>
    public double UnitsPerMm => Dpi / UnitConverter.MmPerInch;

    /// <summary>派生新选项。</summary>
    public RenderOptions WithDpi(double dpi) => this with { Dpi = dpi };

    /// <summary>入口校验：画布换算要除以这些值，0/NaN/Infinity 一律在构造选项时就拒。</summary>
    static double RequirePositive(double value, string name)
        => double.IsFinite(value) && value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                name,
                value,
                "必须是有限正数：引擎按它换算画布单位，0/NaN/Infinity 会让整页退化成 0 或 NaN 尺寸。");
}

/// <summary>表格跨页的一页窗口：本页渲染集合数据行的区间 <c>[Start, Start + Count)</c>。</summary>
/// <param name="Start">本页第一条数据行在全集合中的下标（0 起）。</param>
/// <param name="Count">本页渲染的数据行数。</param>
public readonly record struct TableWindow(int Start, int Count);

/// <summary>
/// 样式脚本（<c>styler</c>）的解析结果。解析出的字段以最高优先级并入元素样式
/// （脚本 &gt; 内联 &gt; 样式库）；未出现的字段保持级联结果不变。
/// </summary>
public sealed record ScriptStyle
{
    /// <summary>前景色（文本字色、线/形状描边、码前景条色）。</summary>
    public SKColor? Color { get; init; }

    /// <summary>背景色（元素底色、形状填充、码背景）。</summary>
    public SKColor? BackgroundColor { get; init; }

    /// <summary>字号（磅）。</summary>
    public double? FontSizePt { get; init; }

    /// <summary>加粗。</summary>
    public bool? Bold { get; init; }

    /// <summary>倾斜。</summary>
    public bool? Italic { get; init; }

    /// <summary>下划线。</summary>
    public bool? Underline { get; init; }

    /// <summary>删除线。</summary>
    public bool? Strikethrough { get; init; }

    /// <summary>字间距（磅）。</summary>
    public double? LetterSpacingPt { get; init; }

    /// <summary>行高相对字号的倍数。</summary>
    public double? LineSpacing { get; init; }

    /// <summary>水平对齐。</summary>
    public HorizontalAlign? HorizontalAlign { get; init; }

    /// <summary>垂直对齐。</summary>
    public VerticalAlign? VerticalAlign { get; init; }

    /// <summary>内容区内边距（mm）：左/上/右/下。</summary>
    public double? PaddingLeftMm { get; init; }

    /// <summary>内容区内边距（mm）：上。</summary>
    public double? PaddingTopMm { get; init; }

    /// <summary>内容区内边距（mm）：右。</summary>
    public double? PaddingRightMm { get; init; }

    /// <summary>内容区内边距（mm）：下。</summary>
    public double? PaddingBottomMm { get; init; }

    /// <summary>边框色（线元素映射为线色）。</summary>
    public SKColor? BorderColor { get; init; }

    /// <summary>边框宽（mm，线元素映射为线宽）。</summary>
    public double? BorderWidthMm { get; init; }

    /// <summary>边框虚线段长（mm）循环，对应 CSS <c>border-style: dashed/dotted</c>；null 保持不变。</summary>
    public double[]? BorderDashPatternMm { get; init; }
}

/// <summary>
/// 脚本执行抽象（Jint 实现在 Engine 层，Core 不引入 JS 依赖）。
/// 执行承诺：脚本异常、超时、语句超限一律降级——返回 null 并向 <see cref="RenderReport"/>
/// 记 <see cref="RenderIssueKind.ScriptIssue"/> 警告，渲染不中断；返回 undefined 与抛异常同义。
/// </summary>
public interface IScriptRunner
{
    /// <summary>
    /// 执行 formatter。元素级参数 <c>[value, options, target, templateData]</c>（hiprint 同款），
    /// 表格列级 <c>[value, row, index, options, templateData]</c>。返回值将 <c>ToString</c> 后
    /// 替换显示内容；降级（异常/undefined）返回 null 表示用原值。
    /// </summary>
    object? RunFormatter(string source, object?[] args, string? elementId, RenderReport report);

    /// <summary>
    /// 执行 styler（返回样式对象或 CSS 字符串均可）。降级或返回空样式返回 null 表示不改样式。
    /// </summary>
    ScriptStyle? RunStyler(string source, object?[] args, string? elementId, RenderReport report);
}

/// <summary>渲染期只读上下文，供元素渲染器取用。</summary>
public sealed class RenderContext
{
    /// <summary>创建上下文。</summary>
    /// <param name="options">渲染选项。</param>
    /// <param name="report">告警收集器。</param>
    /// <param name="fontResolver">字体解析器。</param>
    /// <param name="images">图片解析器。</param>
    /// <param name="data">数据绑定源。</param>
    /// <param name="preferTestData">设计画布：测试数据优先于宿主数据。</param>
    /// <param name="tableWindows">表格跨页窗口（表格元素 id → 窗口）；null 表示本次渲染不分页。</param>
    /// <param name="pageNo">当前页码（1 起）；0 表示无页码语义。</param>
    /// <param name="pageTotal">总页数；0 表示无页码语义。</param>
    public RenderContext(RenderOptions options, RenderReport report, IFontResolver? fontResolver = null, IImageResolver? images = null, object? data = null, bool preferTestData = false,
        IReadOnlyDictionary<string, TableWindow>? tableWindows = null, int pageNo = 0, int pageTotal = 0)
    {
        Options = options;
        Report = report;
        FontResolver = fontResolver ?? DefaultFontResolver.Shared;
        Images = images;
        Data = data;
        PreferTestData = preferTestData;
        TableWindows = tableWindows;
        PageNo = pageNo;
        PageTotal = pageTotal;
    }

    /// <summary>渲染选项。</summary>
    public RenderOptions Options { get; }

    /// <summary>降级信息收集器。</summary>
    public RenderReport Report { get; }

    /// <summary>字体解析器。</summary>
    public IFontResolver FontResolver { get; }

    /// <summary>外部图片解析器（ResolverKey 图片来源），可为 null。</summary>
    public IImageResolver? Images { get; }

    /// <summary>v0.3 数据绑定的数据源；未绑定时为 null。</summary>
    public object? Data { get; }

    /// <summary>
    /// 设计画布渲染：true 时元素级「测试数据」（<c>PaperElement.TestData</c>）优先于宿主数据——
    /// 用户在属性面板输入测试值就要在画布上看到测试值。打印/预览渲染恒为 false，测试数据不参与。
    /// </summary>
    public bool PreferTestData { get; }

    /// <summary>表格跨页窗口（表格元素 id → 窗口）；null 表示本次渲染不分页，表格照常整表绘制。</summary>
    public IReadOnlyDictionary<string, TableWindow>? TableWindows { get; }

    /// <summary>当前页码（1 起）；0 表示本次渲染没有页码语义，<c>{{pageNo}}</c> 不做页码回退。</summary>
    public int PageNo { get; }

    /// <summary>总页数；0 表示本次渲染没有页码语义，<c>{{pageCount}}</c> 不做页码回退。</summary>
    public int PageTotal { get; }

    /// <summary>
    /// 模板样式库（样式 id → 样式），由渲染入口统一注入。各渲染器按消费点的 <c>StyleId</c>
    /// 查表做「库样式打底、内联覆盖」级联；null 表示本次渲染没有样式库，所有引用静默忽略。
    /// </summary>
    public IReadOnlyDictionary<string, NamedStyle>? Styles { get; set; }

    /// <summary>
    /// 脚本执行器（formatter/styler，hiprint 同款语义），由渲染入口统一注入。
    /// null 表示本次渲染不执行脚本，所有脚本字段静默忽略（与"没有脚本"同义）。
    /// </summary>
    public IScriptRunner? Scripts { get; set; }

    /// <summary>画布单位数 / mm。引擎在入口处对 canvas 施加该缩放，渲染器内部一律以 mm 作图。</summary>
    public double UnitsPerMm => Options.UnitsPerMm;

    /// <summary>
    /// 磅 → 当前画布坐标系字号。
    /// 引擎在入口对 canvas 施加了 Scale(UnitsPerMm)，渲染器全部以 mm 作图；
    /// SKFont.Size 会被 CTM 再次放大，故此处必须给 mm 值而不是目标单位值。
    /// </summary>
    public double PtToUnits(double pt) => UnitConverter.PtToMm(pt);
}

/// <summary>渲染降级原因分类。</summary>
public enum RenderIssueKind
{
    /// <summary>字体缺失，已回退默认字体。</summary>
    FontFallback,

    /// <summary>图片解码失败，已绘占位框。</summary>
    ImageDecodeFailed,

    /// <summary>图片数据缺失或来源类型不受支持。</summary>
    ImageMissing,

    /// <summary>二维码内容无法编码。</summary>
    QrCodeFailed,

    /// <summary>一维码内容不符合码制约束。</summary>
    BarcodeFailed,

    /// <summary>元素类型没有对应渲染器。</summary>
    RendererMissing,

    /// <summary>渲染元素时发生未预期异常，该元素被跳过。</summary>
    ElementSkipped,

    /// <summary>绑定字段在数据源中不存在。</summary>
    BindingMissingField,

    /// <summary>表格内容超出元素高度，被裁切。</summary>
    TableOverflow,

    /// <summary>元素超出页面可印区域。</summary>
    ElementOutOfBounds,

    /// <summary>formatter/styler 脚本异常或超时，已降级为"没有脚本"。</summary>
    ScriptIssue,
}

/// <summary>单条渲染降级记录。</summary>
public sealed record RenderIssue(string? ElementId, RenderIssueKind Kind, string Message);

/// <summary>渲染过程的降级信息汇总。引擎承诺不抛异常，问题一律进这里。</summary>
public sealed class RenderReport
{
    readonly List<RenderIssue> _issues = new();

    /// <summary>全部降级记录，按发生顺序。</summary>
    public IReadOnlyList<RenderIssue> Issues => _issues;

    /// <summary>是否发生任何降级。</summary>
    public bool HasIssues => _issues.Count > 0;

    /// <summary>追加一条记录。</summary>
    public void Add(string? elementId, RenderIssueKind kind, string message) => _issues.Add(new RenderIssue(elementId, kind, message));

    /// <summary>合并另一份报告。</summary>
    public void AddRange(RenderReport other) => _issues.AddRange(other._issues);

    /// <summary>清空。</summary>
    public void Clear() => _issues.Clear();

    /// <summary>生成便于展示的多行文本。</summary>
    public override string ToString()
        => _issues.Count == 0
            ? "无渲染告警"
            : string.Join(Environment.NewLine, _issues.Select(i => $"[{i.Kind}] {(i.ElementId is null ? "-" : i.ElementId)}: {i.Message}"));
}

/// <summary>字体解析抽象：把族名 + 样式解析为 SKTypeface。</summary>
public interface IFontResolver
{
    /// <summary>
    /// 解析字体。找不到时必须回退，不得返回 null 或抛异常。
    /// </summary>
    /// <param name="fontFamily">字体族名。</param>
    /// <param name="bold">是否加粗。</param>
    /// <param name="italic">是否倾斜。</param>
    /// <param name="probeChar">
    /// 回退探测字符：族名不存在时，用它去系统里找真正能画出该字符的字体。
    /// 传文本首字符可让中文模板在缺字体时落到含汉字的字体，而不是只能画西文的字体。
    /// </param>
    SKTypeface Resolve(string fontFamily, bool bold, bool italic, char probeChar = 'A');

    /// <summary>
    /// 该族名在本解析器上是否真实存在。渲染器用它决定要不要记 <see cref="RenderIssueKind.FontFallback"/> 告警——
    /// <see cref="Resolve"/> 承诺永不失败，所以"是否发生了回退"只能由这里回答。
    /// 宿主自定义解析器（内嵌字体包等）应覆写；默认实现保守地回答"存在"，即不产生告警。
    /// </summary>
    /// <param name="fontFamily">字体族名。</param>
    bool FamilyExists(string fontFamily) => true;
}

/// <summary>
/// 默认字体解析器，基于系统字体库并缓存结果。
/// 缓存是定容 LRU（<see cref="Capacity"/>）：键里带探测字符（CJK 回退按字符匹配，见 <see cref="Resolve"/>），
/// 批量打印的首字符空间巨大——不设上限的 Dictionary 会让常驻打印服务的内存随数据单调增长。
/// 淘汰只丢引用：SKTypeface 是托管包装，原生件随 GC 释放；被淘汰的键下次解析重新 <c>MatchCharacter</c>，语义不变。
/// </summary>
public sealed class DefaultFontResolver : IFontResolver
{
    /// <summary>共享实例。</summary>
    public static DefaultFontResolver Shared { get; } = new();

    /// <summary>缓存条数上限（超出按最近最少使用淘汰）。与 Engine 层位图缓存的容量纪律同一口径。</summary>
    internal const int Capacity = 256;

    readonly Dictionary<(string Family, bool Bold, bool Italic, char Probe), LinkedListNode<Entry>> _cache = new();
    readonly LinkedList<Entry> _lru = new();
    readonly SKFontManager _manager = SKFontManager.Default;
    readonly object _familiesGate = new();
    HashSet<string>? _families;

    sealed record Entry((string Family, bool Bold, bool Italic, char Probe) Key, SKTypeface Typeface);

    /// <summary>系统字体族名列表（是**本地化名**，中文系统上是"微软雅黑"而不是"Microsoft YaHei"），首次访问时枚举一次。</summary>
    HashSet<string> Families
    {
        get
        {
            var set = _families;
            if (set is not null)
                return set;

            lock (_familiesGate)
                return _families ??= new HashSet<string>(_manager.FontFamilies, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc/>
    public bool FamilyExists(string fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            return false;

        var name = fontFamily.Trim();

        // 两个口径都得认（探针 §22）：中文 Windows 的 FontFamilies 把雅黑列成"微软雅黑"，
        // 而按英文名匹配时 Skia 用的是字体内部英文名 "Microsoft YaHei"。
        // 只看列表会把装得好好的字体报成"不可用"，告警通道立刻变成噪音。
        if (Families.Contains(name))
            return true;

        var matched = Resolve(name, bold: false, italic: false, 'A');
        return string.Equals(matched.FamilyName.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public SKTypeface Resolve(string fontFamily, bool bold, bool italic, char probeChar = 'A')
    {
        var family = fontFamily ?? string.Empty;
        var key = (family, bold, italic, probeChar);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // 命中即前移，保证淘汰的是真正久未使用的（与位图缓存同一套 LRU 纪律）
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Typeface;
            }

            var typeface = Match(family, bold, italic, probeChar);
            _cache[key] = _lru.AddFirst(new Entry(key, typeface));

            while (_cache.Count > Capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }

            return typeface;
        }
    }

    /// <summary>当前缓存条数（诊断口径：常驻服务观察字体缓存水位，恒 ≤ <see cref="Capacity"/>）。</summary>
    internal int CacheCount
    {
        get
        {
            lock (_cache)
                return _cache.Count;
        }
    }

    SKTypeface Match(string family, bool bold, bool italic, char probeChar)
    {
        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

        try
        {
            // MatchCharacter 自带「族名不存在则按字符找可用字体」的回退语义。
            // 标注虽为非空，运行时确实可能返回 null，故显式声明为可空。
            SKTypeface? typeface = string.IsNullOrWhiteSpace(family)
                ? null
                : _manager.MatchCharacter(family, weight, SKFontStyleWidth.Normal, slant, null, probeChar);

            typeface ??= _manager.MatchCharacter(probeChar);
            typeface ??= SKTypeface.Default;
            return typeface;
        }
        catch
        {
            // 字体族枚举在个别平台会抛；字体缺失属用户数据问题，降级不抛
            return SKTypeface.Default;
        }
    }
}

/// <summary>外部图片资源解析抽象。</summary>
public interface IImageResolver
{
    /// <summary>按 key 取图片字节；不存在返回 null。</summary>
    byte[]? Resolve(string key);
}
