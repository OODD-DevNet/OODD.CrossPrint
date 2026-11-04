namespace CrossPrint.Core;

/// <summary>
/// 打印模板根对象：纸张 + 留白 + 元素集合。可由设计器直接编辑（对象图可变）。
/// </summary>
public sealed class PrintTemplate
{
    /// <summary>模板格式版本，供后续迁移使用。当前恒为 <c>"0.1"</c>。</summary>
    public string SchemaVersion { get; set; } = TemplateSchema.CurrentVersion;

    /// <summary>纸张规格。</summary>
    public PaperSpec Paper { get; set; } = new();

    /// <summary>页面留白。</summary>
    public ThicknessMm Margins { get; set; } = new();

    /// <summary>
    /// 页面文字水印：启用后按网格平铺斜纹文字，编辑画布与打印/导出一致生效。
    /// </summary>
    public WatermarkOptions Watermark { get; set; } = new();

    /// <summary>
    /// 面板打印整体左偏移（mm）。由 hiprint 的 <c>leftOffset</c> 导入而来；
    /// 渲染入口统一平移整张页面内容（背景色除外），实现整张纸偏移。
    /// </summary>
    public double LeftOffsetMm { get; set; }

    /// <summary>面板打印整体上偏移（mm）。由 hiprint 的 <c>topOffset</c> 导入而来。</summary>
    public double TopOffsetMm { get; set; }

    /// <summary>
    /// 自动页码（hiprint <c>paperNumber*</c>）：为每一页追加一个显示「第 X / Y 页」的脚注文本元素。
    /// 与模板里手工绑定 <c>{{pageNo}}/{{pageCount}}</c> 的文本元素可二选一或并存。
    /// 仅当模板触发通用分页（含本标志或任意分页元素标志）时生效。
    /// </summary>
    public bool PaperNumberEnabled { get; set; }

    /// <summary>
    /// 自动页码脚注文本格式；支持 <c>{{pageNo}}</c>/<c>{{pageCount}}</c>/<c>{{totalPages}}</c> 占位符。
    /// 空时使用默认「第 {{pageNo}} / {{pageCount}} 页」。
    /// </summary>
    public string? PaperNumberFormat { get; set; }

    /// <summary>自动页码脚注横坐标（mm，相对页面）；null 表示水平居中。</summary>
    public double? PaperNumberXMm { get; set; }

    /// <summary>自动页码脚注纵坐标（mm，相对页面）；null 表示贴底（下留白上方）。</summary>
    public double? PaperNumberYMm { get; set; }

    /// <summary>
    /// 模板级样式库：命名样式的集合，元素/表格列/规则通过 <c>StyleId</c> 引用。
    /// 引用不存在的 id 静默忽略（渲染只看内联值），删除被引用的样式由设计器负责拦截提示。
    /// </summary>
    public List<NamedStyle> Styles { get; set; } = new();

    /// <summary>元素集合，数组顺序即同 zIndex 下的绘制次序。</summary>
    public List<PaperElement> Elements { get; set; } = new();

    /// <summary>
    /// 设计态预览数据：绑定路径 → 样例值（如 <c>barcode</c> → <c>"8801234567890"</c>）。
    /// 用于让设计器/预览在没有真实数据时也能画出 <c>{{字段}}</c> 的内容；
    /// 打印链路只有在调用方显式传入本模板的设计数据时才读它，不会自动把样例值印到纸上。
    /// </summary>
    public Dictionary<string, string> DesignData { get; set; } = new();

    /// <summary>创建默认 A4 纵向、10mm 留白的空模板。</summary>
    public static PrintTemplate CreateDefault(string preset = PaperTypePresets.A4) => new()
    {
        Paper = preset == PaperTypePresets.Custom ? new PaperSpec { Preset = Custom } : PaperSpec.FromPreset(preset),
        // 留白默认 10mm：安全区辅助线（画在留白内框上）与纸边重合就等于看不见，
        // 新模板必须是"能看出安全区在哪"的（旧 JSON 模板缺 margins 字段照旧读 0，不迁移）。
        Margins = new ThicknessMm(10, 10, 10, 10),
    };

    /// <summary>按 id 查找元素，未找到返回 null。</summary>
    public PaperElement? FindElement(string id) => Elements.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// 生成模板内唯一的元素 id（<c>el-N</c> 递增，删除不回收）。
    /// </summary>
    public string NextElementId()
    {
        var max = 0;
        foreach (var e in Elements)
        {
            if (e.Id.StartsWith("el-", StringComparison.Ordinal)
                && int.TryParse(e.Id.Substring(3), out var n) && n > max)
                max = n;
        }

        return $"el-{max + 1}";
    }

    /// <summary>按 id 查找样式，未找到返回 null。</summary>
    public NamedStyle? FindStyle(string? id) =>
        string.IsNullOrEmpty(id) ? null : Styles.FirstOrDefault(s => s.Id == id);

    /// <summary>生成模板内唯一的样式 id（<c>st-N</c> 递增，删除不回收）。</summary>
    public string NextStyleId()
    {
        var max = 0;
        foreach (var s in Styles)
        {
            if (s.Id.StartsWith("st-", StringComparison.Ordinal)
                && int.TryParse(s.Id.Substring(3), out var n) && n > max)
                max = n;
        }

        return $"st-{max + 1}";
    }

    /// <summary>添加元素；id 为空时自动分配，重复 id 会抛异常。</summary>
    public PaperElement Add(PaperElement element)
    {
        if (string.IsNullOrEmpty(element.Id))
            element.Id = NextElementId();
        else if (FindElement(element.Id) is not null)
            throw new ArgumentException($"元素 id '{element.Id}' 在模板内重复。", nameof(element));

        Elements.Add(element);
        return element;
    }

    /// <summary>移除元素（按引用）。</summary>
    public bool Remove(PaperElement element) => Elements.Remove(element);

    /// <summary>深拷贝整个模板。</summary>
    public PrintTemplate Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Paper = Paper.Clone(),
        Margins = Margins.Clone(),
        Watermark = Watermark.Clone(),
        LeftOffsetMm = LeftOffsetMm,
        TopOffsetMm = TopOffsetMm,
        PaperNumberEnabled = PaperNumberEnabled,
        PaperNumberFormat = PaperNumberFormat,
        PaperNumberXMm = PaperNumberXMm,
        PaperNumberYMm = PaperNumberYMm,
        DesignData = new Dictionary<string, string>(DesignData),
        Styles = Styles.Select(s => s.Clone()).ToList(),
        Elements = Elements.Select(e => e.CloneDeep()).ToList(),
    };

    /// <summary>
    /// 把扁平的设计态数据（<c>a.b[0].c</c> 形式）展开成可以交给 <c>DataBinder</c> 的嵌套对象图；没有设计数据时返回 null。
    /// </summary>
    /// <remarks>
    /// 返回类型用 <see cref="object"/> 是为了不把 Engine 的绑定实现细节带进 Core 的公开签名；
    /// 实际形状是 <c>Dictionary&lt;string, object?&gt;</c> / <c>List&lt;object?&gt;</c>，
    /// 与引擎绑定器支持的字典路径一致。
    /// <para>
    /// 列表下标只补齐到 <see cref="MaxDesignListIndex"/>：畸形 designData（如 <c>items[2000000]</c>）
    /// 会让每次重绘都分配百万级列表，超过上限的键整体跳过，绑定按未命中走"缺失字段"回落。
    /// </para>
    /// </remarks>
    public object? BuildDesignBindingData()
    {
        if (DesignData.Count == 0)
            return null;

        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in DesignData)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            SetDesignValue(root, pair.Key.Trim(), pair.Value);
        }

        return root.Count == 0 ? null : root;
    }

    /// <summary>designData 路径里允许补齐的最大列表下标；更大的一律视为畸形数据，不扩容也不写入。</summary>
    const int MaxDesignListIndex = 512;

    static void SetDesignValue(Dictionary<string, object?> root, string path, string value)
    {
        var current = root;
        var segments = path.Split('.').Where(s => !string.IsNullOrEmpty(s)).Select(s => s.Trim()).ToArray();
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var bracket = segment.IndexOf('[');
            var name = (bracket < 0 ? segment : segment.Substring(0, bracket)).Trim();
            if (name.Length == 0)
                return;

            int? index = null;
            if (bracket >= 0 && segment.EndsWith("]")
                && int.TryParse(segment.Substring(bracket + 1, segment.Length - bracket - 2), out var parsed) && parsed >= 0)
                index = parsed;

            if (index > MaxDesignListIndex)
                return;

            var last = i == segments.Length - 1;

            if (index is null)
            {
                if (last)
                {
                    current[name] = value;
                    return;
                }

                if (!current.TryGetValue(name, out var next) || next is not Dictionary<string, object?> child)
                {
                    child = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[name] = child;
                }

                current = child;
                continue;
            }

            if (!current.TryGetValue(name, out var existing) || existing is not List<object?> list)
            {
                list = new List<object?>();
                current[name] = list;
            }

            while (list.Count <= index.Value)
                list.Add(null);

            if (last)
            {
                list[index.Value] = value;
                return;
            }

            if (list[index.Value] is not Dictionary<string, object?> item)
            {
                item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                list[index.Value] = item;
            }

            current = item;
        }
    }

    /// <summary>
    /// 内容自适应页高（连续纸）：取最底部元素下边缘 + 下留白 + 2mm 余量。
    /// 固定纸张直接返回 <see cref="PaperSpec.PageHeightMm"/>。
    /// </summary>
    public double ResolvePageHeightMm()
    {
        if (!Paper.IsContinuous)
            return Paper.PageHeightMm;

        var bottom = Margins.Bottom;
        foreach (var e in Elements)
            bottom = Math.Max(bottom, e.BottomMm);

        return bottom + 2;
    }

    const string Custom = PaperTypePresets.Custom;
}

/// <summary>模板格式版本常量。</summary>
public static class TemplateSchema
{
    /// <summary>当前实现的模板格式版本。</summary>
    public const string CurrentVersion = "0.1";

    /// <summary>本版本能读取的最低 schemaVersion。</summary>
    public const string MinSupportedVersion = "0.1";
}
