using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// 系统打印目标取证。真发打印作业在任何 CI 上都无法稳定复现，所以交付通道一律注入替身（<see cref="FakeCommand"/>），
/// 断言集中在**本层自己的职责**上：交付那一刻 PDF 已完整落盘、校验闸与文件目标同口径、
/// 失败折算不抛、临时件生命周期（交付后删除 / 被占用时留给清扫）。平台通道本身只做参数拼装，靠真机人工验证。
/// </summary>
public sealed class SystemPrintTargetTests : IDisposable
{
    readonly Workspace _ws = new();
    readonly FakeCommand _command = new();
    readonly SystemPrintTarget _target;

    public SystemPrintTargetTests()
        => _target = new SystemPrintTarget(_command, () => _ws.Root);

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task 交付成功_命令收到完整PDF且回传页数与告警()
    {
        var template = Fixture.Page(100, 150);
        template.Elements.Add(Fixture.Text("{{orderNo}}", 8, 8, 70, 12));
        var documents = new List<PrintDocument>
        {
            new(template, new { orderNo = "SO-1" }),
            new(template, new { orderNo = "SO-2" }),
            new(template, new { orderNo = "SO-3" }),
        };
        var issues = new RenderReport();

        var result = await _target.PrintBatchAsync(documents, new SystemPrintOptions { PrinterName = "POS-80" }, issues);

        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(3, "交给管线的页数按引擎回传的真实页数计");
        result.Error.Should().BeNull();
        _command.CallCount.Should().Be(1);
        _command.ReceivedPrinter.Should().Be("POS-80");
        _command.ReceivedTimeout.Should().Be(60, "未配置时用默认超时");

        _command.FileAtInvoke.Should().NotBeNull();
        var pdf = _command.FileAtInvoke!;
        Pdf.HasSignature(pdf).Should().BeTrue("交付那一刻临时 PDF 必须已完整落盘");
        Pdf.PageCount(pdf).Should().Be(3);
        Pdf.MediaBoxesMm(pdf).Should().HaveCount(3)
            .And.OnlyContain(m => Math.Abs(m.W - 100) < 0.05, "打印层不许改写纸张尺寸");

        issues.HasIssues.Should().BeFalse("干净模板不该有告警");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty("交付成功后临时 PDF 应已删除");
    }

    [Fact]
    public async Task 不指定打印机时交付动作收到空打印机名()
    {
        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        _command.ReceivedPrinter.Should().BeNull("Windows 走 print 动词 / lp 走默认队列，由系统决定目的地");
    }

    [Fact]
    public async Task 超时秒数透传给交付命令()
    {
        var result = await _target.PrintAsync(
            new PrintDocument(Fixture.Page(80, 60)),
            new SystemPrintOptions { TimeoutSeconds = 7 });

        result.Success.Should().BeTrue(result.Message ?? "成功");
        _command.ReceivedTimeout.Should().Be(7);
    }

    [Fact]
    public async Task 命令报失败折进Failed不抛异常()
    {
        _command.Response = "系统没有能执行打印的 PDF 关联应用（SE_ERR_NOASSOC）。";

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("系统打印交付失败").And.Contain("SE_ERR_NOASSOC");
        result.Error.Should().BeNull("通道失败是结果不是异常");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty("失败作业也不留临时件");
    }

    [Fact]
    public async Task 取消在交付前生效()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)), cancellationToken: cts.Token);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("已取消");
        result.Error.Should().BeOfType<OperationCanceledException>();
        _command.CallCount.Should().Be(0, "取消的作业不许发起交付");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 批次校验与文件目标同口径()
    {
        var template = Fixture.Page(80, 60);

        (await _target.PrintBatchAsync(new PrintDocument[] { null! }))
            .Message.Should().Contain("null 文档");
        (await _target.PrintBatchAsync(Array.Empty<PrintDocument>()))
            .Message.Should().Contain("批次为空");
        (await _target.PrintBatchAsync(new[] { new PrintDocument(template) with { Copies = 0 } }))
            .Message.Should().Contain("份数为 0，必须 ≥ 1",
                "record 的 with 绕过构造器校验，这一层必须再拦一次");
        _command.CallCount.Should().Be(0, "校验不过的批次不许发起交付");
    }

    [Fact]
    public async Task 同步交付方成功后立即删除临时件()
    {
        // lp 语义：MayReadFileAfterReturn=false，退出码 0 即文件已进 CUPS 队列，没人再读
        _command.MayReadFileAfterReturn = false;

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(_command.ReceivedPath).Should().BeFalse("同步假脱机后临时件应立即删除");
    }

    [Fact]
    public async Task 异步交付方成功后不删临时件_留给陈旧清扫()
    {
        // ShellExecute 语义：MayReadFileAfterReturn=true。关联应用是被异步拉起的，可能过好几秒才打开文件，
        // 立即删就是抢跑（实测 WPS 报「不是一个有效的文件」）——文件必须留给下次作业的陈旧清扫。
        _command.MayReadFileAfterReturn = true;

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(_command.ReceivedPath).Should().BeTrue("交付成功后系统侧还会读文件，不许立即删");
        File.Delete(_command.ReceivedPath!);
    }

    [Fact]
    public async Task 失败时无论交付方语义一律删除临时件()
    {
        // 失败 = 命令没受理，文件没人接手，即使 Windows 语义也立即删
        _command.MayReadFileAfterReturn = true;
        _command.Response = "交付被拒绝。";

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeFalse();
        File.Exists(_command.ReceivedPath).Should().BeFalse("没人接手的临时件应立即删除");
    }

    [Fact]
    public async Task 陈旧临时件被清扫_新鲜的保留()
    {
        var stale = _ws.PathOf("crossprint-system-stale.pdf");
        var fresh = _ws.PathOf("crossprint-system-fresh.pdf");
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.WriteAllBytes(fresh, [1, 2, 3]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

        var result = await _target.PrintAsync(new PrintDocument(Fixture.Page(80, 60)));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(stale).Should().BeFalse("超过 1 小时的残留临时件应在下次作业开始时回收");
        File.Exists(fresh).Should().BeTrue("够新的文件可能在交付中，宁留勿删");
    }

    [Fact]
    public async Task IPrintTarget入口忽略OutputPath且不落盘()
    {
        var output = _ws.PathOf("ignored.pdf");

        var result = await ((IPrintTarget)_target).PrintAsync(
            new PrintDocument(Fixture.Page(80, 60)),
            new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(output).Should().BeFalse("系统目标的打印目的地由系统管线决定，OutputPath 被忽略");
        _command.CallCount.Should().Be(1, "settings 里的渲染透传字段生效，交付照常发起");
    }
}

/// <summary>替身交付命令：记录收到的参数，交付那一刻把 PDF 字节抓下来当证据。</summary>
internal sealed class FakeCommand : ISystemPrintCommand
{
    /// <summary>固定应答：null = 受理，非 null = 失败原因。</summary>
    public string? Response { get; set; }

    /// <summary>系统侧是否还会延迟读文件（模拟 Windows 动词交付时置 true）。</summary>
    public bool MayReadFileAfterReturn { get; set; }

    /// <summary>高级场景钩子；返回值优先于 <see cref="Response"/>。</summary>
    public Func<string, string?>? OnInvoke { get; set; }

    public string? ReceivedPath { get; private set; }
    public string? ReceivedPrinter { get; private set; }
    public int ReceivedTimeout { get; private set; }
    public byte[]? FileAtInvoke { get; private set; }
    public int CallCount { get; private set; }

    public string? Invoke(string pdfPath, string? printerName, int timeoutSeconds)
    {
        CallCount++;
        ReceivedPath = pdfPath;
        ReceivedPrinter = printerName;
        ReceivedTimeout = timeoutSeconds;
        FileAtInvoke = File.ReadAllBytes(pdfPath);
        return OnInvoke?.Invoke(pdfPath) ?? Response;
    }
}
