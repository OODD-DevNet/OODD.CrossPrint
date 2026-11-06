using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossPrint.Core;
using FreeSql;
using FreeSql.DataAnnotations;

namespace CrossPrint.SolutionStorage;

/// <summary>解决方案里的一条模板记录。</summary>
public sealed record TemplateRecord(string Id, string Name, string Json, DateTime UpdatedAt);

/// <summary>解决方案模板存储：列出 / 读取 / 保存 / 删除。</summary>
public interface ITemplateStore
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

/// <summary>按当前宿主选择存储实现（桌面 = SQLite）。</summary>
public static class TemplateStoreFactory
{
    /// <summary>创建存储实例（桌面 = SQLite）。</summary>
    public static ITemplateStore Create() => new SqliteTemplateStore();
}

/// <summary>SQLite 实现（FreeSql，库文件在 AppData CrossPrint templates.db）。所有桌面宿主共用。</summary>
internal sealed class SqliteTemplateStore : ITemplateStore
{
    readonly IFreeSql _db;

    public SqliteTemplateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CrossPrint");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "templates.db");

        _db = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, "Data Source=" + path + ";Pooling=true;Max Pool Size=2")
            .UseAutoSyncStructure(true) // 实体变更自动建表/加列，省迁移脚本
            .Build();
    }

    public IReadOnlyList<TemplateRecord> List() =>
        _db.Select<TemplateEntity>()
            .OrderByDescending(e => e.UpdatedAt)
            .ToList(e => new TemplateRecord(e.Id, e.Name, e.Json, e.UpdatedAt));

    public TemplateRecord? Load(string id) =>
        _db.Select<TemplateEntity>()
            .Where(e => e.Id == id)
            .First(e => new TemplateRecord(e.Id, e.Name, e.Json, e.UpdatedAt));

    public string Save(string? id, string name, string json)
    {
        var recordId = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
        var entity = new TemplateEntity { Id = recordId, Name = name, Json = json, UpdatedAt = DateTime.Now };
        _db.Delete<TemplateEntity>().Where(e => e.Id == recordId).ExecuteAffrows();
        _db.Insert(entity).ExecuteAffrows();
        return recordId;
    }

    public void Delete(string id) =>
        _db.Delete<TemplateEntity>().Where(e => e.Id == id).ExecuteAffrows();
}

[Table(Name = "template")]
internal class TemplateEntity
{
    [Column(IsPrimary = true, StringLength = 40)]
    public string Id { get; set; } = string.Empty;

    [Column(StringLength = 200)]
    public string Name { get; set; } = string.Empty;

    // StringLength = -1 → TEXT，模板 JSON 不限长。
    [Column(StringLength = -1)]
    public string Json { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
