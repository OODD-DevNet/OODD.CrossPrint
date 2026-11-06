using System;
using System.Collections.Generic;
using System.Linq;
using CrossPrint.Core;

namespace CrossPrint.SolutionStorage;

/// <summary>
/// 一个宿主内的「当前解决方案」会话：跟踪当前模板 id / 名称 / 脏标记，
/// 提供序列化、保存、打开、列出、最近记录等 UI 无关操作。
/// 宿主负责把当前 <see cref="PrintTemplate"/> 喂进来、把打开的模板应用到设计器。
/// </summary>
public sealed class SolutionSession
{
    readonly ITemplateStore _store = TemplateStoreFactory.Create();

    /// <summary>当前存储记录 id；null 表示尚未保存到解决方案（新建/未命名）。</summary>
    public string? CurrentId;

    /// <summary>当前模板名称（用于保存/另存为名、UI 标题）。</summary>
    public string? CurrentName = "未命名模板";

    /// <summary>自上次保存以来是否有未落库的改动。</summary>
    public bool IsDirty;

    /// <summary>状态（id/名称/脏位）变化后触发，宿主可用来刷新标题/菜单。</summary>
    public event Action? StateChanged;

    /// <summary>全部记录，按更新时间倒序。</summary>
    public IReadOnlyList<TemplateRecord> List() => _store.List();

    /// <summary>最近更新的前 n 条记录。</summary>
    public IReadOnlyList<TemplateRecord> Recent(int n = 8) =>
        _store.List().OrderByDescending(r => r.UpdatedAt).Take(n).ToList();

    /// <summary>按 id 读取完整记录（含 JSON）。</summary>
    public TemplateRecord? Load(string id) => _store.Load(id);

    /// <summary>把当前模板序列化并写入存储（compact JSON）。id 为 null 视为新增。</summary>
    public string SaveCurrent(PrintTemplate template, string? name = null)
    {
        var json = TemplateJson.Serialize(template, indented: false);
        var id = _store.Save(CurrentId, name ?? CurrentName ?? "未命名模板", json);
        CurrentId = id;
        CurrentName = name ?? CurrentName;
        IsDirty = false;
        StateChanged?.Invoke();
        return id;
    }

    /// <summary>标记已打开某条记录（落库状态），不清空脏位以外的状态。</summary>
    public void OpenRecord(TemplateRecord record)
    {
        CurrentId = record.Id;
        CurrentName = record.Name;
        IsDirty = false;
        StateChanged?.Invoke();
    }

    /// <summary>新建空白：清空当前记录，回到「未保存」状态。</summary>
    public void New()
    {
        CurrentId = null;
        CurrentName = "未命名模板";
        IsDirty = false;
        StateChanged?.Invoke();
    }

    /// <summary>外部已落库后调用，清除脏标记。</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        StateChanged?.Invoke();
    }

    /// <summary>把存储里的 JSON 解析回模板（宽松模式；失败返回 null）。</summary>
    public static PrintTemplate? Parse(string json)
    {
        try
        {
            return TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant).Template;
        }
        catch
        {
            return null;
        }
    }
}
