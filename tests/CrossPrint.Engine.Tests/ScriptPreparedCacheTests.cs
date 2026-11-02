using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 预编译 AST 缓存的取证。它是进程级静态的，所以断言只押两件事：条数不超过上限、
/// 被淘汰过的脚本还能照常跑对（AST 不可变，淘汰的代价只是重解析）。
/// 上限是全局的、别的测试类也会往里放，所以每条用例都先把缓存填满再断言——填满之后计数就钉在上限上。
/// </summary>
public class ScriptPreparedCacheTests
{
    static readonly object?[] NoArgs = [null, null, null, null];

    /// <summary>第 i 份互不相同的脚本源码：返回一个可核对的数，顺便证明"跑对了"而不是"没报错"。</summary>
    static string Script(int i) => $"function(value, options, target, templateData){{ return {i} * 3 + 1; }}";

    static object? Run(string source)
    {
        var report = new RenderReport();
        var result = new ScriptRunner().RunFormatter(source, NoArgs, "e1", report);
        report.Issues.Should().BeEmpty($"脚本 {source[..Math.Min(40, source.Length)]} 不该降级");
        return result;
    }

    [Fact]
    public void 预编译缓存条数不超过上限()
    {
        // 塞进超过上限的不同脚本：模拟常驻服务持续加载新模板
        for (var i = 0; i < ScriptRunner.PreparedCacheCapacity + 50; i++)
            Run(Script(i));

        ScriptRunner.PreparedCacheCount.Should().Be(ScriptRunner.PreparedCacheCapacity, "超过上限必须按最近最少使用回收，而不是无限增长");
    }

    [Fact]
    public void 被回收过的脚本重解析后照样跑对()
    {
        var total = ScriptRunner.PreparedCacheCapacity + 50;
        Run(Script(0));
        for (var i = 1; i < total; i++)
            Run(Script(i));

        // 第 0 条早已被挤出缓存：走重解析分支，结果必须与首次一致
        Run(Script(0)).Should().Be(1.0);
        Run(Script(total - 1)).Should().Be((total - 1) * 3.0 + 1);
        ScriptRunner.PreparedCacheCount.Should().Be(ScriptRunner.PreparedCacheCapacity);
    }

    [Fact]
    public void 同一份脚本重复执行不增条数()
    {
        // 先填满，让全局计数钉在上限上（别的测试类只会同样填满，不会填空）
        var total = ScriptRunner.PreparedCacheCapacity + 10;
        for (var i = 0; i < total; i++)
            Run(Script(i));

        var before = ScriptRunner.PreparedCacheCount;
        for (var i = 0; i < 20; i++)
            Run(Script(7));

        ScriptRunner.PreparedCacheCount.Should().Be(before, "同一份源码只解析一次");
        Run(Script(7)).Should().Be(22.0);
    }

    [Fact]
    public void 宿主可以主动清空预编译缓存()
    {
        // 先填满，计数钉在上限上；清空之后必须肉眼可见地掉下来
        for (var i = 0; i < ScriptRunner.PreparedCacheCapacity + 5; i++)
            Run(Script(i));
        ScriptRunner.PreparedCacheCount.Should().Be(ScriptRunner.PreparedCacheCapacity);

        var beforeClear = ScriptRunner.PreparedCacheCount;
        ScriptRunner.ClearPreparedCache();

        // 别的测试类可能并发往里补几条，所以只断言"清空真的放掉了"，不断言精确 0
        ScriptRunner.PreparedCacheCount.Should().BeLessThan(beforeClear, "清空后计数必须掉下来；并发补进来的零星条目填不满上限");

        // 清空只是丢掉 AST，语义不变：照样解析并跑对
        Run(Script(2)).Should().Be(7.0);
    }

    [Fact]
    public void 缓存命中与淘汰不影响渲染降级口径()
    {
        // 语法错的脚本：不管在缓存的哪条分支上，都必须降级成"没有脚本"并记一条告警
        var report = new RenderReport();
        var result = new ScriptRunner().RunFormatter("function(value){ return ", NoArgs, "e1", report);
        result.Should().BeNull();
        report.Issues.Single().Kind.Should().Be(RenderIssueKind.ScriptIssue);

        ScriptRunner.ClearPreparedCache();
        for (var i = 0; i < ScriptRunner.PreparedCacheCapacity + 5; i++)
            Run(Script(i));

        report = new RenderReport();
        new ScriptRunner().RunFormatter("function(value){ return ", NoArgs, "e1", report).Should().BeNull();
        report.Issues.Single().Kind.Should().Be(RenderIssueKind.ScriptIssue);
        ScriptRunner.PreparedCacheCount.Should().Be(ScriptRunner.PreparedCacheCapacity, "解析失败的源码不进缓存");
    }
}
