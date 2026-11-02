using CrossPrint.Core;

namespace CrossPrint.Designer;

/// <summary>
/// 几何变更命令（移动 / 缩放 / 方向键微调共用）。
/// 记录的是"元素实例 + 前后 mm 几何"，因此 Revert 不需要重新查找元素：
/// 内核全程复用同一个 <see cref="PaperElement"/> 实例（属性写回、撤销重放都不换实例），
/// 实例引用比 id 查找更稳，也比"整表快照"更省。
/// </summary>
public sealed class GeometryCommand : EditorCommand
{
    /// <summary>
    /// 一条几何记录。
    /// </summary>
    /// <param name="Element">目标元素实例。</param>
    /// <param name="Before">变更前几何（mm）。</param>
    /// <param name="After">变更后几何（mm）。</param>
    public sealed record Entry(PaperElement Element, ElementBoxMm Before, ElementBoxMm After);

    readonly List<Entry> _entries;

    /// <summary>创建几何命令。</summary>
    /// <param name="label">动作名。</param>
    /// <param name="entries">每个元素的前后几何。</param>
    public GeometryCommand(string label, IReadOnlyList<Entry> entries)
    {
        Label = label;
        _entries = [.. entries];
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply()
    {
        foreach (var entry in _entries)
            Write(entry.Element, entry.After);
    }

    /// <inheritdoc/>
    public void Revert()
    {
        foreach (var entry in _entries)
            Write(entry.Element, entry.Before);
    }

    static void Write(PaperElement element, ElementBoxMm box)
    {
        element.XMm = box.X;
        element.YMm = box.Y;
        element.WidthMm = box.Width;
        element.HeightMm = box.Height;
    }
}

/// <summary>
/// 删除命令。按"元素 + 原索引"记录，Revert 时按索引升序插回——
/// 删掉相邻两个元素（索引 2、3）若乱序插回会让数组顺序变化，同 ZIndex 下绘制次序就跟着变，
/// 撤销后用户看到的叠放顺序和删除前不一样。索引升序插回保证数组顺序逐位还原。
/// </summary>
public sealed class DeleteElementsCommand : EditorCommand
{
    readonly PrintTemplate _template;
    readonly Selection _selection;
    readonly List<PaperElement> _removed;
    readonly List<int> _indices;

    /// <summary>创建删除命令。构造时必须仍在模板里，索引在此刻取。</summary>
    /// <param name="template">目标模板。</param>
    /// <param name="selection">内核选中集合，删除时收起、撤销时还原。</param>
    /// <param name="elements">被删除的元素。</param>
    public DeleteElementsCommand(PrintTemplate template, Selection selection, IReadOnlyList<PaperElement> elements)
    {
        _template = template;
        _selection = selection;
        _removed = [.. elements];
        _indices = _removed.Select(e => template.Elements.IndexOf(e)).ToList();
        Label = elements.Count == 1
            ? $"删除 1 个元素（{elements[0].TypeKey}）"
            : $"删除 {elements.Count} 个元素";
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply()
    {
        foreach (var element in _removed)
            _template.Elements.RemoveAll(e => e.Id == element.Id);

        foreach (var element in _removed)
            _selection.Remove(element.Id);
    }

    /// <inheritdoc/>
    public void Revert()
    {
        // 索引升序插回：先插小的，后面的索引才不会被前面的插入顶偏。
        var order = Enumerable.Range(0, _removed.Count)
            .OrderBy(i => _indices[i])
            .ToList();

        foreach (var i in order)
        {
            var index = Math.Clamp(_indices[i], 0, _template.Elements.Count);
            _template.Elements.Insert(index, _removed[i]);
            _selection.Add(_removed[i].Id);
        }
    }
}

/// <summary>
/// 放置命令：把工具箱拖来的元素插进模板。<see cref="Revert"/> 即"放回没放过的状态"。
/// </summary>
public sealed class PlaceElementCommand : EditorCommand
{
    readonly PrintTemplate _template;
    readonly Selection _selection;
    readonly PaperElement _element;
    readonly int _index;

    /// <summary>创建放置命令。</summary>
    /// <param name="template">目标模板。</param>
    /// <param name="selection">内核选中集合：放置后选中新元素，撤销时把它从选中里收掉。</param>
    /// <param name="element">待插入元素（id 必须已分配好）。</param>
    /// <param name="index">插入位置。</param>
    public PlaceElementCommand(PrintTemplate template, Selection selection, PaperElement element, int index)
    {
        _template = template;
        _selection = selection;
        _element = element;
        _index = index;
        Label = $"放置 {element.TypeKey}";
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply()
    {
        // 幂等：重复 Apply（撤销栈将来重放）不得插出两个同 id 元素。
        if (!_template.Elements.Any(e => e.Id == _element.Id))
            _template.Elements.Insert(Math.Clamp(_index, 0, _template.Elements.Count), _element);

        _selection.Select(_element.Id);
    }

    /// <inheritdoc/>
    public void Revert()
    {
        _template.Elements.RemoveAll(e => e.Id == _element.Id);
        _selection.Remove(_element.Id);
    }

    /// <summary>被放置的元素。</summary>
    public PaperElement Element => _element;
}

/// <summary>
/// 叠放次序命令。记录"整表顺序 + 每个元素的 ZIndex"两份快照，
/// 因为置顶/置底改的是 <c>ZIndex</c>、上移/下移在同值时改的是数组位置，
/// 用一份完整快照覆盖两种写法，Revert 就能原样还原，不必区分这次到底改了哪一头。
/// </summary>
public sealed class ZOrderCommand : EditorCommand
{
    readonly PrintTemplate _template;
    readonly List<(PaperElement Element, int ZIndex)> _before;
    readonly List<(PaperElement Element, int ZIndex)> _after;

    /// <summary>创建叠放次序命令。</summary>
    /// <param name="template">目标模板。</param>
    /// <param name="label">动作名。</param>
    /// <param name="before">变更前的表顺序与各元素 ZIndex。</param>
    /// <param name="after">变更后的表顺序与各元素 ZIndex。</param>
    public ZOrderCommand(PrintTemplate template, string label, IReadOnlyList<(PaperElement Element, int ZIndex)> before, IReadOnlyList<(PaperElement Element, int ZIndex)> after)
    {
        _template = template;
        Label = label;
        _before = [.. before];
        _after = [.. after];
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply() => Commit(_after);

    /// <inheritdoc/>
    public void Revert() => Commit(_before);

    void Commit(List<(PaperElement Element, int ZIndex)> state)
    {
        foreach (var (element, zIndex) in state)
            element.ZIndex = zIndex;

        // 就地重建而不是换一个新 List：宿主可能正持有这个 List 的引用（属性面板、绑定）。
        _template.Elements.Clear();
        foreach (var (element, _) in state)
            _template.Elements.Add(element);
    }
}

/// <summary>
/// 纸张变更命令。整份 <see cref="PaperSpec"/> 换引用，Revert 换回旧实例——
/// 纸张是模板级配置而不是元素属性，宿主与渲染器每次绘制都会重新读取 <see cref="PrintTemplate.Paper"/>。
/// </summary>
public sealed class PaperCommand : EditorCommand
{
    readonly PrintTemplate _template;
    readonly PaperSpec _before;
    readonly PaperSpec _after;

    /// <summary>创建纸张命令。</summary>
    /// <param name="template">目标模板。</param>
    /// <param name="label">动作名，如"设置纸张 A4"。</param>
    /// <param name="before">变更前纸张。</param>
    /// <param name="after">变更后纸张。</param>
    public PaperCommand(PrintTemplate template, string label, PaperSpec before, PaperSpec after)
    {
        _template = template;
        Label = label;
        _before = before;
        _after = after;
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply() => _template.Paper = _after;

    /// <inheritdoc/>
    public void Revert() => _template.Paper = _before;
}

/// <summary>
/// 创建副本命令：把克隆出来的元素追加到模板尾部并改选中它们；Revert 移除副本并恢复原选中。
/// 副本沿用源元素的 ZIndex，因此在同一层里排在源元素之后（视觉上压住源元素）。
/// </summary>
public sealed class DuplicateElementsCommand : EditorCommand
{
    readonly PrintTemplate _template;
    readonly Selection _selection;
    readonly List<PaperElement> _clones;
    readonly List<string> _beforeSelection;

    /// <summary>创建副本命令。</summary>
    /// <param name="template">目标模板。</param>
    /// <param name="selection">内核选中集合。</param>
    /// <param name="clones">已分配新 id 的副本元素。</param>
    public DuplicateElementsCommand(PrintTemplate template, Selection selection, IReadOnlyList<PaperElement> clones)
    {
        _template = template;
        _selection = selection;
        _clones = [.. clones];
        _beforeSelection = [.. selection.Ids];
        Label = clones.Count == 1 ? "创建副本 1 个元素" : $"创建副本 {clones.Count} 个元素";
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply()
    {
        foreach (var clone in _clones)
        {
            // 幂等：撤销栈重放时不能插出两份。
            if (!_template.Elements.Any(e => e.Id == clone.Id))
                _template.Elements.Add(clone);
        }

        _selection.Select(null);
        foreach (var clone in _clones)
            _selection.Add(clone.Id);
    }

    /// <inheritdoc/>
    public void Revert()
    {
        foreach (var clone in _clones)
            _template.Elements.RemoveAll(e => e.Id == clone.Id);

        _selection.Select(null);
        foreach (var id in _beforeSelection)
            _selection.Add(id);
    }
}

/// <summary>
/// 属性写回命令。属性面板的一次编辑 = 一段"前向 setter / 反向 setter"闭包，
/// 内核不需要知道改了哪个字段，因此新增元素类型不必改这里（架构 §9 的扩展点约束）。
/// </summary>
public sealed class PropertyChangeCommand : EditorCommand
{
    readonly Action _apply;
    readonly Action _revert;

    /// <summary>创建属性写回命令。</summary>
    /// <param name="label">动作名，如"修改 字号"。</param>
    /// <param name="apply">前向写入。</param>
    /// <param name="revert">撤销写入，须把 <paramref name="apply"/> 改过的字段还原成原值。</param>
    public PropertyChangeCommand(string label, Action apply, Action revert)
    {
        Label = label;
        _apply = apply;
        _revert = revert;
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply() => _apply();

    /// <inheritdoc/>
    public void Revert() => _revert();
}

/// <summary>
/// 表格列结构变更命令（插入/删除一列）。一次列增删同时牵动四处模型：
/// Columns 列表、每行 Rows 的单元格数、多行表头 HeaderRows 与静态行合并 RowSpans 的列下标/跨列数——
/// 与其写"逆操作"逐处推导回退（跨列单元格收窄、对位列整格消失这些分支极易漏），
/// 不如构造时整体快照、<see cref="Revert"/> 原样还原。
/// 插入定宽列还可能撑宽元素本体（见 <see cref="Insert"/>），所以元素宽也进快照，一起还原。
/// </summary>
public sealed class TableColumnCommand : EditorCommand
{
    readonly TableElement _element;
    readonly TableOptions _table;
    readonly List<TableColumn> _beforeColumns;
    readonly List<List<string>> _beforeRows;
    readonly List<TableHeaderCell> _beforeHeaderRows;
    readonly List<TableRowSpan> _beforeRowSpans;
    readonly double _beforeWidthMm;
    readonly double _targetWidthMm;
    readonly Action _mutate;

    TableColumnCommand(TableElement element, string label, Action mutate, double targetWidthMm)
    {
        Label = label;
        _element = element;
        _table = element.Table;
        _beforeColumns = _table.Columns.Select(c => c.Clone()).ToList();
        _beforeRows = _table.Rows.Select(r => r.ToList()).ToList();
        _beforeHeaderRows = _table.HeaderRows.Select(h => h.Clone()).ToList();
        _beforeRowSpans = _table.RowSpans.Select(s => s.Clone()).ToList();
        _beforeWidthMm = element.WidthMm;
        _targetWidthMm = targetWidthMm;
        _mutate = mutate;
    }

    /// <summary>
    /// 创建"在 <paramref name="columnIndex"/> 前插入一列"的命令；<paramref name="columnIndex"/> 等于列数即追加。
    /// 新列是定宽且定宽之和装不进元素时，元素宽加到盖得住它，上限 <paramref name="maxWidthMm"/>（纸边）。
    /// </summary>
    /// <param name="element">目标表格元素。</param>
    /// <param name="columnIndex">插入位置（0 起，夹紧到 [0, 列数]）。</param>
    /// <param name="column">新列（Title/WidthMm 等由调用方定）。</param>
    /// <param name="maxWidthMm">元素能加宽到的上限（mm），由调用方按纸边算；不限制传 <see cref="double.PositiveInfinity"/>。</param>
    public static TableColumnCommand Insert(TableElement element, int columnIndex, TableColumn column, double maxWidthMm)
    {
        var index = Math.Clamp(columnIndex, 0, element.Table.Columns.Count);
        var targetWidthMm = GrownWidthMm(element, column, maxWidthMm);
        var what = index == element.Table.Columns.Count
            ? $"追加表格列（第 {index + 1} 列"
            : $"插入表格列（第 {index + 1} 列前";

        // 顺手加宽了元素就要写进动作名：撤销菜单里只说"追加列"，用户不知道表格宽度也变了。
        var grown = targetWidthMm > element.WidthMm
            ? $"，表宽 {element.WidthMm:0.##}→{targetWidthMm:0.##}mm"
            : string.Empty;

        return new TableColumnCommand(element, what + grown + "）", () =>
        {
            // 幂等：重复 Apply（将来撤销栈重放）时新列已在其位就不再插。
            if (element.Table.Columns.Contains(column))
                return;

            InsertColumnAt(element.Table, index, column);
        }, targetWidthMm);
    }

    /// <summary>创建"删除第 <paramref name="columnIndex"/> 列"的命令。表格至少保留一列。</summary>
    /// <exception cref="InvalidOperationException">表格只剩一列。</exception>
    public static TableColumnCommand Remove(TableElement element, int columnIndex)
    {
        if (element.Table.Columns.Count <= 1)
            throw new InvalidOperationException("表格至少要保留一列，不能删除。");

        var index = Math.Clamp(columnIndex, 0, element.Table.Columns.Count - 1);
        return new TableColumnCommand(element, $"删除表格列（第 {index + 1} 列）", () =>
        {
            // 幂等：列已不在（数量对不上）就当作已删过，跳过。
            if (index >= element.Table.Columns.Count)
                return;

            RemoveColumnAt(element.Table, index);
        }, element.WidthMm);
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply()
    {
        _mutate();

        // 引擎按定宽累加列宽且不缩放，装不下的部分会画到元素框外面，所以这里把框撑到盖得住（只放大）。
        if (_targetWidthMm > _element.WidthMm)
            _element.WidthMm = _targetWidthMm;
    }

    /// <inheritdoc/>
    public void Revert()
    {
        // 就地重建而不是换新 List：与 ZOrderCommand 同理，宿主可能正持有列表引用。
        Replace(_table.Columns, _beforeColumns);
        Replace(_table.Rows, _beforeRows);
        Replace(_table.HeaderRows, _beforeHeaderRows);
        Replace(_table.RowSpans, _beforeRowSpans);
        _element.WidthMm = _beforeWidthMm;
    }

    /// <summary>插入定宽列后元素该有的宽度：定宽之和装得下就原样，装不下才加宽，且不越过纸边。</summary>
    static double GrownWidthMm(TableElement element, TableColumn column, double maxWidthMm)
    {
        var needed = 0d;
        if (column.WidthMm >= 0)
            needed += column.WidthMm;

        foreach (var existing in element.Table.Columns)
            if (existing.WidthMm >= 0)
                needed += existing.WidthMm;

        if (needed <= element.WidthMm)
            return element.WidthMm;

        // 上限低于当前宽（表格本来就挂出纸外）时不加宽，更不能顺手缩表。
        return Math.Min(needed, Math.Max(maxWidthMm, element.WidthMm));
    }

    static void Replace<T>(List<T> target, List<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    static void InsertColumnAt(TableOptions table, int index, TableColumn column)
    {
        table.Columns.Insert(index, column);

        // 静态行：每行在第 index 个位置补一个空单元格。行短于 index（脏数据）先补齐再插，
        // 保住"每行单元格数 = 列数"的渲染契约。
        foreach (var row in table.Rows)
        {
            while (row.Count < index)
                row.Add(string.Empty);
            row.Insert(index, string.Empty);
        }

        // 表头：起始列在插入点之后的整体右移；跨度覆盖插入点的把新列并入自己（Column == index 的
        // 单元格不动——新列插在它左边）。
        foreach (var cell in table.HeaderRows)
        {
            if (cell.Column >= index)
                cell.Column++;
            else if (cell.Column + cell.ColSpan > index)
                cell.ColSpan++;
        }

        // 静态行合并的 Column/ColSpan 同样是列下标，随插入走与表头一致的规则，否则合并整体错位一列。
        foreach (var span in table.RowSpans)
        {
            if (span.Column >= index)
                span.Column++;
            else if (span.Column + span.ColSpan > index)
                span.ColSpan++;
        }
    }

    static void RemoveColumnAt(TableOptions table, int index)
    {
        table.Columns.RemoveAt(index);

        foreach (var row in table.Rows)
        {
            if (index < row.Count)
                row.RemoveAt(index);
        }

        // 表头：起始列在删除点之后的左移；覆盖删除点的跨度收窄一格，
        // 收到 0 的单元格（单格正好对位被删列）整个移除——该表头标题随列一起消失。
        for (var i = table.HeaderRows.Count - 1; i >= 0; i--)
        {
            var cell = table.HeaderRows[i];
            if (cell.Column > index)
            {
                cell.Column--;
            }
            else if (cell.Column + cell.ColSpan > index)
            {
                cell.ColSpan--;
                if (cell.ColSpan <= 0)
                    table.HeaderRows.RemoveAt(i);
            }
        }

        // 静态行合并：与表头同规则收窄/左移，收到 0 的整格移除（该合并正好对位被删列）。
        for (var i = table.RowSpans.Count - 1; i >= 0; i--)
        {
            var span = table.RowSpans[i];
            if (span.Column > index)
            {
                span.Column--;
            }
            else if (span.Column + span.ColSpan > index)
            {
                span.ColSpan--;
                if (span.ColSpan <= 0)
                    table.RowSpans.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// 表格列表条目的结构变更命令（追加/删除一条），泛型覆盖条件样式、关键词样式、静态行合并三类列表。
/// 这类列表不带联动（不像列牵动行与表头），但撤销语义与 <see cref="TableColumnCommand"/> 保持同一套：
/// 构造时快照整份列表，<see cref="Revert"/> 原样还原——条目里的字段、阈值、颜色都随快照复原，
/// 不必逐字段推导回退。
/// </summary>
/// <typeparam name="T">列表条目类型（<see cref="TableConditionalStyle"/> / <see cref="TableHighlightRule"/> / <see cref="TableRowSpan"/>）。</typeparam>
public sealed class TableListItemCommand<T> : EditorCommand where T : class
{
    readonly TableOptions _table;
    readonly Func<TableOptions, List<T>> _selector;
    readonly List<T> _beforeItems;
    readonly Action _mutate;

    TableListItemCommand(TableElement element, Func<TableOptions, List<T>> selector, Func<T, T> clone, string label, Action mutate)
    {
        Label = label;
        _table = element.Table;
        _selector = selector;
        _beforeItems = selector(_table).Select(clone).ToList();
        _mutate = mutate;
    }

    /// <summary>创建"追加一条条目"的命令。</summary>
    /// <param name="element">目标表格元素。</param>
    /// <param name="selector">从 <see cref="TableOptions"/> 取目标列表的函数。</param>
    /// <param name="clone">条目克隆函数（快照用，通常是模型上的 <c>Clone()</c>）。</param>
    /// <param name="item">新条目（内容由调用方定）。</param>
    /// <param name="listName">列表中文名，用于动作标签（如"条件样式"）。</param>
    public static TableListItemCommand<T> Append(TableElement element, Func<TableOptions, List<T>> selector, Func<T, T> clone, T item, string listName)
    {
        var ordinal = selector(element.Table).Count + 1;
        return new TableListItemCommand<T>(element, selector, clone, $"追加{listName}（第 {ordinal} 条）", () =>
        {
            // 幂等：重复 Apply（将来撤销栈重放）时新条目已在其位就不再插。
            if (selector(element.Table).Contains(item))
                return;

            selector(element.Table).Add(item);
        });
    }

    /// <summary>创建"删除一条条目"的命令。列表允许为空（没有条目只是不生效，不是非法模板）。</summary>
    /// <param name="element">目标表格元素。</param>
    /// <param name="selector">从 <see cref="TableOptions"/> 取目标列表的函数。</param>
    /// <param name="clone">条目克隆函数（快照用，通常是模型上的 <c>Clone()</c>）。</param>
    /// <param name="itemIndex">删除位置（0 起，夹紧到 [0, 条目数-1]）。</param>
    /// <param name="listName">列表中文名，用于动作标签（如"条件样式"）。</param>
    public static TableListItemCommand<T> Remove(TableElement element, Func<TableOptions, List<T>> selector, Func<T, T> clone, int itemIndex, string listName)
    {
        var index = Math.Clamp(itemIndex, 0, selector(element.Table).Count - 1);
        return new TableListItemCommand<T>(element, selector, clone, $"删除{listName}（第 {index + 1} 条）", () =>
        {
            // 幂等：条目已不在（数量对不上）就当作已删过，跳过。
            if (index >= selector(element.Table).Count)
                return;

            selector(element.Table).RemoveAt(index);
        });
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public void Apply() => _mutate();

    /// <inheritdoc/>
    public void Revert()
    {
        // 就地重建而不是换新 List：与 TableColumnCommand 同理，宿主可能正持有列表引用。
        var list = _selector(_table);
        list.Clear();
        foreach (var item in _beforeItems)
            list.Add(item);
    }
}
