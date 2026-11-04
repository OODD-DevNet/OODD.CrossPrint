using System;
using System.Collections.Generic;
#if BROWSER
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
#endif

namespace CrossPrint.Samples.Avalonia;

/// <summary>解决方案里的一条模板记录。</summary>
public sealed record TemplateRecord(string Id, string Name, string Json, DateTime UpdatedAt);

/// <summary>
/// 解决方案模板存储。桌面 = SQLite（FreeSql，见 SqliteTemplateStore.cs）；
/// 浏览器 = localStorage（本机浏览器持久，按浏览器隔离；它不是共享服务器存储，不存在多人冲突）。
/// </summary>
internal interface ITemplateStore
{
    /// <summary>全部记录，按更新时间倒序。</summary>
    IReadOnlyList<TemplateRecord> List();

    /// <summary>按 id 读记录；不存在返回 null。</summary>
    TemplateRecord? Load(string id);

    /// <summary>保存（id 为 null 即新增），返回记录 id。</summary>
    string Save(string? id, string name, string json);

    /// <summary>删除记录；不存在静默忽略。</summary>
    void Delete(string id);
}

/// <summary>按当前宿主选择存储实现：桌面 SQLite / 浏览器 localStorage。</summary>
internal static class TemplateStoreFactory
{
    public static ITemplateStore Create() =>
#if BROWSER
        new LocalStorageTemplateStore();
#else
        new SqliteTemplateStore();
#endif
}

#if BROWSER
/// <summary>浏览器实现：localStorage 单键存 JSON 数组（本机持久；模板 JSON KB 级，整存整取够用）。</summary>
internal sealed partial class LocalStorageTemplateStore : ITemplateStore
{
    const string Key = "crossprint.templates.v1";
    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    [JSImport("localStorage.getItem", "globalThis")]
    private static partial string? GetItem(string key);

    [JSImport("localStorage.setItem", "globalThis")]
    private static partial void SetItem(string key, string value);

    List<TemplateRecord> ReadAll()
    {
        try
        {
            var raw = GetItem(Key);
            if (string.IsNullOrEmpty(raw))
                return [];
            return JsonSerializer.Deserialize<List<TemplateRecord>>(raw, Options) ?? [];
        }
        catch
        {
            // 存储被禁用/数据损坏：按空处理，不让保存面板挂掉。
            return [];
        }
    }

    void WriteAll(List<TemplateRecord> records) =>
        SetItem(Key, JsonSerializer.Serialize(records, Options));

    public IReadOnlyList<TemplateRecord> List() =>
        [.. ReadAll().OrderByDescending(r => r.UpdatedAt)];

    public TemplateRecord? Load(string id) =>
        ReadAll().FirstOrDefault(r => r.Id == id);

    public string Save(string? id, string name, string json)
    {
        var records = ReadAll();
        var recordId = id ?? Guid.NewGuid().ToString("N");
        records.RemoveAll(r => r.Id == recordId);
        records.Add(new TemplateRecord(recordId, name, json, DateTime.Now));
        WriteAll(records);
        return recordId;
    }

    public void Delete(string id)
    {
        var records = ReadAll();
        records.RemoveAll(r => r.Id == id);
        WriteAll(records);
    }
}
#endif
