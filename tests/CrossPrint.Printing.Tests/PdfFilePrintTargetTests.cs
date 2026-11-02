using System.Diagnostics;
using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// PDF 文件打印目标取证（FR-PRT-01 / FR-BND-04 / M6 验收：100 条数据 → 100 页）。
/// 断言一律读回产物字节——签名、页数、<c>/FontFile2</c> 次数、逐页内容流是否两两不同，
/// 不看返回值自证：返回 "成功 100 页" 而文件其实是半截，是这个层最贵的错法。
/// </summary>
public sealed class PdfFilePrintTargetTests : IDisposable
{
    readonly Workspace _ws = new();
    readonly PdfFilePrintTarget _target = new();

    [Fact]
    public async Task 单文档打印产物是可读回的真PDF()
    {
        var template = Fixture.Page(100, 150);
        template.Elements.Add(Fixture.Text("CrossPrint", 8, 8, 70, 12));
        var output = _ws.PathOf("single.pdf");

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(1);
        result.Error.Should().BeNull();

        var bytes = File.ReadAllBytes(output);
        bytes.LongLength.Should().BeGreaterThan(0, "零字节文件比失败更坏：归档系统会把它当成功件收走");
        Pdf.HasSignature(bytes).Should().BeTrue("%PDF- 头签名");
        Pdf.PageCount(bytes).Should().Be(1);
        Pdf.MediaBoxesMm(bytes).Should().ContainSingle().Which.W.Should().BeApproximately(100, 0.05, "打印层不许改写引擎算出的纸张尺寸");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty("成功作业不该留临时文件");
    }

    [Fact]
    public async Task 输出路径缺目录时打印目标自动创建多级目录()
    {
        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("deep", 6, 6, 40, 10));
        var output = _ws.PathOf("a", "b", "c", "job.pdf");

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(output).Should().BeTrue();
        Pdf.HasSignature(File.ReadAllBytes(output)).Should().BeTrue();
    }

    [Fact]
    public async Task 批量一百条数据生成一百页PDF且字体只嵌一份()
    {
        // M6 可行性主张在 100 页尺度上的复测：一页一份文件的话 100 页要嵌 100 份字库，
        // 走同一份 SKDocument 才只嵌 1 份、页增量只剩内容流。数字全部当场实测，不引记忆值。
        var small = Fixture.Orders(5);
        var hundred = Fixture.Orders(100);
        var smallPath = _ws.PathOf("five.pdf");
        var hundredPath = _ws.PathOf("hundred.pdf");

        var smallSw = Stopwatch.StartNew();
        var smallResult = await _target.PrintBatchAsync(small, new PrintSettings(smallPath));
        smallSw.Stop();

        var sw = Stopwatch.StartNew();
        var result = await _target.PrintBatchAsync(hundred, new PrintSettings(hundredPath));
        sw.Stop();

        var smallBytes = File.ReadAllBytes(smallPath);
        var bytes = File.ReadAllBytes(hundredPath);

        smallResult.Success.Should().BeTrue(smallResult.Message ?? "成功");
        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(100);

        Pdf.HasSignature(bytes).Should().BeTrue();
        Pdf.PageCount(bytes).Should().Be(100, "100 条数据 = 100 页（FR-BND-04 验收口径）");

        var fontStreams = Pdf.Count(bytes, "/FontFile2");
        fontStreams.Should().Be(1, $"100 页只该内嵌一份字库（实测 {fontStreams} 份）");

        var delta = bytes.LongLength - smallBytes.LongLength;
        var perPage = delta / 95;
        perPage.Should().BeLessThan(4096,
            $"5 页 {smallBytes.LongLength}B → 100 页 {bytes.LongLength}B，增量 {delta}B，即每页 {perPage}B；每页各嵌一份字库的话这里是 MB 级");
        bytes.LongLength.Should().BeLessThan(smallBytes.LongLength * 5,
            $"总体积随页数亚线性增长（实测 5 页 {smallBytes.LongLength / 1024}KB、100 页 {bytes.LongLength / 1024}KB，比值 {bytes.LongLength / (double)smallBytes.LongLength:F2}×）");

        var perPageMs = sw.ElapsedMilliseconds / 100.0;
        // Debug 跑的是没优化的 IL，按 NFR-01 的 200ms/页判会天天假失败，退化成 10 倍粗线
        var budget = Fixture.IsOptimized ? 200 : 2000;
        perPageMs.Should().BeLessThan(budget, $"西文版面每页预算（实测 {perPageMs:F2}ms/页，共 {sw.ElapsedMilliseconds}ms）");

        Fixture.Bench($"西文 100 页批量：{bytes.LongLength}B / {sw.ElapsedMilliseconds}ms（每页 {perPageMs:F2}ms）/ 5 页 {smallBytes.LongLength}B / " +
                      $"/FontFile2={fontStreams} / 页增量 {perPage}B");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 中文默认字体批次同样只嵌一份字库并记录耗时归因()
    {
        // NFR-01 的已知代价：SkiaSharp 2.88.9 不做子集化，一份 CJK 版面要把整份字库塞进文档，
        // 单页多花 ~700ms。这里要确认的是这份代价在批量路径上是"每文档一次"还是"每页一次"——
        // 若是后者，100 页面单就是 70 秒，M6 整条批量链路不成立。
        var hundredPath = _ws.PathOf("cjk-hundred.pdf");
        var onePath = _ws.PathOf("cjk-one.pdf");

        var oneSw = Stopwatch.StartNew();
        var oneResult = await _target.PrintBatchAsync(Fixture.Orders(1, Fixture.CjkFont), new PrintSettings(onePath));
        oneSw.Stop();

        var sw = Stopwatch.StartNew();
        var result = await _target.PrintBatchAsync(Fixture.Orders(100, Fixture.CjkFont), new PrintSettings(hundredPath));
        sw.Stop();

        oneResult.Success.Should().BeTrue(oneResult.Message ?? "成功");
        result.Success.Should().BeTrue(result.Message ?? "成功");
        Pdf.PageCount(File.ReadAllBytes(onePath)).Should().Be(1);

        var bytes = File.ReadAllBytes(hundredPath);
        Pdf.PageCount(bytes).Should().Be(100);
        var fontStreams = Pdf.Count(bytes, "/FontFile2");
        fontStreams.Should().Be(1, $"100 页 CJK 也只嵌一份字库（实测 {fontStreams} 份）");

        var ratio = sw.ElapsedMilliseconds / (double)Math.Max(1, oneSw.ElapsedMilliseconds);
        ratio.Should().BeLessThan(30,
            $"100 页 CJK 不该比 1 页贵 100 倍（实测 1 页 {oneSw.ElapsedMilliseconds}ms、100 页 {sw.ElapsedMilliseconds}ms = {ratio:F1}×）");

        Fixture.Bench($"CJK 100 页批量：{bytes.LongLength}B / {sw.ElapsedMilliseconds}ms（1 页对照 {oneSw.ElapsedMilliseconds}ms、" +
                      $"{File.ReadAllBytes(onePath).LongLength}B）/ /FontFile2={fontStreams}");
    }

    [Fact]
    public async Task 批量每页绑定各自数据且无缺字段告警()
    {
        var docs = Fixture.Orders(100);
        var output = _ws.PathOf("binding.pdf");
        var report = new RenderReport();

        var result = await _target.PrintBatchAsync(docs, new PrintSettings(output), report, default);

        result.Success.Should().BeTrue(result.Message ?? "成功");

        // 结构化明细走 issues 重载（NFR-07），分类摘要走 Message——两条通道都得干净
        report.Issues.Where(i => i.Kind == RenderIssueKind.BindingMissingField)
            .Should().BeEmpty("每条记录都带自己的 orderNo，一个都不该缺字段");
        (result.Message ?? string.Empty).Should().NotContain("BindingMissingField", "成功结果的告警摘要里不该出现缺字段");

        // 字节级实证：PDF 里文字是 GID 编码，搜不到明文，所以改验"逐页内容流两两不同"。
        // 数据没落到各自页时 100 条流要么全等（同一条数据）要么干脆没有（缺字段渲染成空串），
        // 这一条是 dataSelector 丢失的字节级探测器，不看返回值自证。
        var streams = Pdf.TextContentStreams(File.ReadAllBytes(output));
        streams.Should().HaveCountGreaterThanOrEqualTo(100, $"一页一条内容流（实测 {streams.Count} 条）");
        streams.Distinct().Count().Should().BeGreaterThanOrEqualTo(100,
            $"100 页内容流必须两两不同才说明各页印的是各条数据（实测 {streams.Distinct().Count()} 种/{streams.Count} 条）");
    }

    [Fact]
    public async Task 目录被同名文件挡住时失败不抛异常也不留文件()
    {
        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("blocked", 6, 6, 40, 10));
        var blocker = _ws.PathOf("blocked");
        File.WriteAllText(blocker, "这是个占位文件，不是目录");
        var output = Path.Combine(blocker, "out", "job.pdf");

        // 直接 await：若实现把 IO 故障抛成异常，这条测试就以未处理异常的形式红给你看
        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull("失败必须交出真异常");
        result.PrintedPages.Should().Be(0);
        File.Exists(output).Should().BeFalse();
        File.ReadAllText(blocker).Should().Be("这是个占位文件，不是目录", "挡路的既有文件不该被写层碰过");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 导出中途失败时正式路径不留半截文件()
    {
        // 故障点在"文件已经建好之后"：Paper 为 null 让引擎在 SKDocument 建流后才炸。
        // 这才是临时名 + 改名那条纪律的考点——直接写正式路径的实现会在这里留下一份半截 PDF。
        var broken = Fixture.Page(100, 60);
        broken.Paper = null!;
        var output = _ws.PathOf("midway.pdf");

        var result = await _target.PrintAsync(new PrintDocument(broken), new PrintSettings(output));

        result.Success.Should().BeFalse(result.Message ?? "无消息");
        result.Error.Should().NotBeNull();
        result.PrintedPages.Should().Be(0);
        File.Exists(output).Should().BeFalse("正式路径上绝不许出现半截 PDF：看着像完整文件，归档会当成功件收走");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty("临时文件也得跟着清掉");
    }

    [Fact]
    public async Task 同名作业重跑时清掉崩溃残留的陈旧临时件()
    {
        // 进程被杀/断电时 {名}.crossprint-{guid}.tmp.pdf 会留在输出目录，下一次同名作业要能自己收掉
        var template = Fixture.Page(100, 60);
        template.Elements.Add(Fixture.Text("resume", 6, 6, 60, 10));
        var output = _ws.PathOf("resume.pdf");
        var stale = _ws.PathOf("resume.crossprint-0123456789abcdef.tmp.pdf");
        File.WriteAllText(stale, "崩溃残留");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(stale).Should().BeFalse("陈旧残留必须跟着同名作业一起清掉");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 新鲜的临时件与别的输出名的残留都不碰()
    {
        var template = Fixture.Page(100, 60);
        template.Elements.Add(Fixture.Text("keep", 6, 6, 60, 10));
        var output = _ws.PathOf("keep.pdf");

        // 同名的"新鲜"临时件：可能是另一个还在跑的作业，按年龄跳过
        var fresh = _ws.PathOf("keep.crossprint-fedcba9876543210.tmp.pdf");
        File.WriteAllText(fresh, "并发作业");

        // 别的输出名的陈旧残留：不在本次作业的责任范围
        var foreign = _ws.PathOf("other.crossprint-aaaabbbbccccdddd.tmp.pdf");
        File.WriteAllText(foreign, "别的作业");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-5));

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        File.Exists(fresh).Should().BeTrue("够新的同名临时件可能是并发作业正在写，不能删");
        File.Exists(foreign).Should().BeTrue("别的输出名的残留不归这次作业清理");
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task 正在被占用且已陈旧的临时件也跳过()
    {
        var template = Fixture.Page(100, 60);
        template.Elements.Add(Fixture.Text("busy", 6, 6, 60, 10));
        var output = _ws.PathOf("busy.pdf");
        var busy = _ws.PathOf("busy.crossprint-0011223344556677.tmp.pdf");
        File.WriteAllText(busy, "仍在写");
        File.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddHours(-3));

        // 独占占用模拟"另一个作业正拿着这个句柄写"
        using (var held = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

            result.Success.Should().BeTrue(result.Message ?? "成功");
            File.Exists(busy).Should().BeTrue("拿不到独占句柄就必须跳过：删正在写的文件比留残留更贵");
        }
    }

    [Fact]
    public async Task 预取消令牌返回失败且不落任何文件()
    {
        var template = Fixture.Page(100, 150);
        template.Elements.Add(Fixture.Text("cancel", 8, 8, 60, 12));
        var output = _ws.PathOf("cancelled.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output), cts.Token);

        result.Success.Should().BeFalse();
        result.Error.Should().BeAssignableTo<OperationCanceledException>("取消按契约折进结果，不抛出 await");
        result.PrintedPages.Should().Be(0);
        File.Exists(output).Should().BeFalse();
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 份数为二时一份文件两页()
    {
        // 本层把份数落成"同一份文件里连续两页同样内容"，不是两个文件：
        // 一次作业一份产物，宿主的重印/补打逻辑才不会分叉。
        var template = Fixture.Page(100, 150);
        template.Elements.Add(Fixture.Text("份数两联", 8, 8, 60, 12, Fixture.CjkFont));
        var output = _ws.PathOf("copies.pdf");

        var result = await _target.PrintAsync(new PrintDocument(template, null, 2), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(2);
        var bytes = File.ReadAllBytes(output);
        Pdf.PageCount(bytes).Should().Be(2);
        Directory.GetFiles(_ws.Root, "*.pdf").Should().ContainSingle("份数不产生第二个文件");
    }

    [Fact]
    public async Task 空批次明确失败且不产出零页PDF()
    {
        var output = _ws.PathOf("empty.pdf");

        var result = await _target.PrintBatchAsync(Array.Empty<PrintDocument>(), new PrintSettings(output));

        result.Success.Should().BeFalse("空集合不是「零页成功」，是调用方算错了批次");
        result.PrintedPages.Should().Be(0);
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().BeNull("空批次没有异常，只有策略性拒绝");
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task 禁止覆盖时拒绝写盘且不动既有文件()
    {
        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("keep", 6, 6, 40, 10));
        var output = _ws.PathOf("no-overwrite.pdf");
        File.WriteAllText(output, "既有内容");

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output) { Overwrite = false });

        result.Success.Should().BeFalse();
        result.Error.Should().BeNull("拒绝覆盖是策略性失败，不是异常");
        File.Exists(output).Should().BeTrue();
        File.ReadAllText(output).Should().Be("既有内容", "Overwrite=false 就得一个字节都不动");
        TempFiles.Leftovers(_ws.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task 默认覆盖同名文件()
    {
        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("replace", 6, 6, 40, 10));
        var output = _ws.PathOf("overwrite.pdf");
        File.WriteAllText(output, "旧产物");

        var result = await _target.PrintAsync(new PrintDocument(template), new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        Pdf.HasSignature(File.ReadAllBytes(output)).Should().BeTrue("PrintSettings.Overwrite 默认 true：同名即覆盖，文档里写明过");
    }

    [Fact]
    public async Task with绕过份数校验时打印目标拦住()
    {
        // PrintDocument 的构造器管住 new，管不住 with（走复制构造器）。第二道闸在展开页序处。
        var template = Fixture.Page(80, 60);
        template.Elements.Add(Fixture.Text("zero-copy", 6, 6, 40, 10));
        var output = _ws.PathOf("bad-copies.pdf");
        var document = new PrintDocument(template) { Copies = 0 };
        document.Copies.Should().Be(0, "with/init 确实绕过了构造器——所以目标侧的校验不是摆设");

        var result = await _target.PrintAsync(document, new PrintSettings(output));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("份数");
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task 批量与单文档共用同一条页序路径()
    {
        // 份数展开后再批量：2 个文档、各 2 份 → 4 页。断的是"份数与批量互相吞页"这类回归。
        var template = Fixture.Page(100, 60);
        template.Elements.Add(Fixture.Text("{{no}}", 8, 8, 60, 10));
        var docs = new List<PrintDocument>
        {
            new(template, new { no = "A" }, 2),
            new(template, new { no = "B" }, 2),
        };
        var output = _ws.PathOf("copies-batch.pdf");

        var result = await _target.PrintBatchAsync(docs, new PrintSettings(output));

        result.Success.Should().BeTrue(result.Message ?? "成功");
        result.PrintedPages.Should().Be(4);
        Pdf.PageCount(File.ReadAllBytes(output)).Should().Be(4);
    }

    [Fact]
    public void 目标标识可用于宿主选择列表()
        => _target.Name.Should().Be("pdf-file");

    /// <summary>删掉本条测试的工作区。产物一律落在 %TEMP% 下的 GUID 目录里，仓库不留东西。</summary>
    public void Dispose() => _ws.Dispose();
}
