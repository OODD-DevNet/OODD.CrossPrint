namespace CrossPrint.Designer;

/// <summary>
/// 选中集合。v0.1 交互路径只会单选，但<b>接口按多选设计</b>：宿主与叠加层都读
/// <see cref="Ids"/>，将来加框选/连选不需要改任何调用点。
/// 存的是元素 <see cref="CrossPrint.Core.PaperElement.Id"/>，不存引用——
/// 元素可能被命令替换掉（撤销重放、属性写回），id 才是稳定身份。
/// </summary>
public sealed class Selection
{
    readonly List<string> _ids = [];

    /// <summary>选中 id，按选中先后排列，末位是主选。</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>主选 id（最后选中的那个）。空选中时为 null。</summary>
    public string? PrimaryId => _ids.Count == 0 ? null : _ids[^1];

    /// <summary>是否为空选中。</summary>
    public bool IsEmpty => _ids.Count == 0;

    /// <summary>是否包含某元素。</summary>
    /// <param name="id">元素 id。</param>
    /// <returns>已选中返回 true。</returns>
    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>单选：替换现有选中。</summary>
    /// <param name="id">元素 id。null 或空串等同清空。</param>
    public void Select(string? id)
    {
        _ids.Clear();
        if (!string.IsNullOrEmpty(id))
            _ids.Add(id);
    }

    /// <summary>追加选中（Shift/Ctrl 点选走这条；v0.1 内核不调，留给宿主）。</summary>
    /// <param name="id">元素 id。</param>
    public void Add(string id)
    {
        if (!string.IsNullOrEmpty(id) && !_ids.Contains(id))
            _ids.Add(id);
    }

    /// <summary>移除一个选中项。</summary>
    /// <param name="id">元素 id。</param>
    public void Remove(string id) => _ids.Remove(id);

    /// <summary>清空选中。</summary>
    public void Clear() => _ids.Clear();
}
