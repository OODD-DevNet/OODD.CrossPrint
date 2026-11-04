#if !BROWSER
using System;
using System.Collections.Generic;
using System.IO;
using FreeSql;
using FreeSql.DataAnnotations;

namespace CrossPrint.Samples.Avalonia;

/// <summary>FreeSql 实体。</summary>
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

/// <summary>桌面实现：SQLite（FreeSql），库文件在 AppData CrossPrint templates.db。</summary>
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
#endif
