using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Printing.Tests;

/// <summary>
/// 契约类型的不变式。<see cref="PrintResult"/> 的两条规矩必须在构造期就钉死——
/// 宿主读 <c>Success</c> 时要能无条件相信它，出现"成功但带异常"或"失败却计了页"的结果，
/// 上层日志与补打逻辑就全建立在假账上。
/// </summary>
public class PrintContractsTests
{
    [Fact]
    public void 成功结果不许带异常()
    {
        var act = () => new PrintResult(true, 1, "好了", new IOException("磁盘满了"));

        act.Should().Throw<ArgumentException>().WithMessage("*Success=true 不许带 Error*");
    }

    [Fact]
    public void 成功结果不许零页()
    {
        var act = () => new PrintResult(true, 0, "好了");

        act.Should().Throw<ArgumentException>().WithMessage("*一页没印不叫成功*");
        var ok = () => PrintResult.Ok(0);
        ok.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 失败结果不许计页()
    {
        var act = () => new PrintResult(false, 37, "失败了", new IOException());

        act.Should().Throw<ArgumentException>().WithMessage("*失败作业不许计页*");
    }

    [Fact]
    public void 失败结果必须带原因()
    {
        var bare = () => new PrintResult(false, 0);
        bare.Should().Throw<ArgumentException>().WithMessage("*必须带原因*");

        var blankMessage = () => PrintResult.Failed("  ");
        blankMessage.Should().Throw<ArgumentException>();

        // 没有异常、只有一句人话的失败是合法的（策略性拒绝，例如禁止覆盖）
        var policy = PrintResult.Failed("目标文件已存在且 Overwrite=false");
        policy.Success.Should().BeFalse();
        policy.Error.Should().BeNull();
        policy.PrintedPages.Should().Be(0);
    }

    [Fact]
    public void 工厂方法与构造器造出同一种结果()
    {
        PrintResult.Ok(5, "有 2 条告警").Should().Be(new PrintResult(true, 5, "有 2 条告警", null), "record 的值相等语义是宿主断言的基础");

        var (success, pages, message, error) = PrintResult.Ok(2);
        success.Should().BeTrue();
        pages.Should().Be(2);
        message.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void 份数小于一时构造即抛()
    {
        var template = Fixture.Page(80, 60);

        new Func<PrintDocument>(() => new PrintDocument(template, null, 0)).Should().Throw<ArgumentOutOfRangeException>();
        new Func<PrintDocument>(() => new PrintDocument(template, null, -2)).Should().Throw<ArgumentOutOfRangeException>();

        // null 模板同理：待打印物不能是"没有内容"
        new Func<PrintDocument>(() => new PrintDocument(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 文档默认一份且可位置解构()
    {
        var template = Fixture.Page(80, 60);
        var data = new { orderNo = "SO-1" };

        var document = new PrintDocument(template, data);
        var (t, d, copies) = document;

        t.Should().BeSameAs(template);
        d.Should().BeSameAs(data);
        copies.Should().Be(1, "缺省份数 1：调用方不写份数时不该出两张纸");
    }

    [Fact]
    public void 输出路径为空时构造即抛()
    {
        new Func<PrintSettings>(() => new PrintSettings("  ")).Should().Throw<ArgumentException>();
        new Func<PrintSettings>(() => new PrintSettings(null!)).Should().Throw<ArgumentException>();

        var settings = new PrintSettings("out.pdf");
        settings.OutputPath.Should().Be("out.pdf");
        settings.Overwrite.Should().BeTrue("默认覆盖，且这条默认在 XML 文档里写死过");
        settings.Options.Should().BeNull("v0.1 的选项都是可空透传，null 表示用引擎默认");
    }

    [Fact]
    public void 设置里没有无人读取的旋钮()
    {
        // v0.1 冻结面的守门测试：打印机名/双面/页码范围这类字段必须先有实现再出现，
        // 出现即由本条测试逼着提交者解释"谁读它"。这里断言的是当前属性清单。
        var propertyNames = typeof(PrintSettings).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        propertyNames.Should().Equal(
            nameof(PrintSettings.FontResolver),
            nameof(PrintSettings.Images),
            nameof(PrintSettings.Options),
            nameof(PrintSettings.OutputPath),
            nameof(PrintSettings.Overwrite),
            nameof(PrintSettings.Renderer));
    }
}
