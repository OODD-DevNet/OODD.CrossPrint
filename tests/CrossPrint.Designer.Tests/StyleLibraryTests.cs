using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 模板样式库的内核管理（新增/删除拦截/修改快照/引用清单）与撤销语义。
/// 样式库是模板级资源：引用按样式 id（改名不断链），删除有引用的样式必须拦截——
/// 断链在渲染期只表现为"样式悄悄失效"，这里拦截并指出引用者。
/// </summary>
public class StyleLibraryTests
{
    [Fact]
    public void 新增样式分配递增id且撤销后整体移除()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);

        var first = kernel.AddStyle();
        first.Id.Should().Be("st-1");
        first.Name.Should().Be("新样式1");
        template.Styles.Should().ContainSingle();

        var second = kernel.AddStyle();
        second.Id.Should().Be("st-2", "样式 id 递增，删除不回收");
        template.Styles.Should().HaveCount(2);

        // 撤销第二次新增：整体移除（不是只清字段）。
        Capture(kernel, () => kernel.AddStyle().Id.Should().Be("st-3")).Revert();
        template.Styles.Select(s => s.Id).Should().Equal("st-1", "st-2");
    }

    [Fact]
    public void 修改样式走快照命令撤销还原改前形态()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        var style = kernel.AddStyle();

        var undo = Capture(kernel, () => kernel.UpdateStyle(style.Id, "修改样式 字号", s => s.FontSizePt = 16));
        template.FindStyle(style.Id)!.FontSizePt.Should().Be(16);

        undo.Revert();
        template.FindStyle(style.Id)!.FontSizePt.Should().BeNull("撤销后回到改前快照（null = 未设置）");
    }

    [Fact]
    public void 修改不存在的样式抛异常()
    {
        var kernel = KernelFixture.Kernel(KernelFixture.Page());
        var act = () => kernel.UpdateStyle("st-404", "修改", s => s.Bold = true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*st-404*");
    }

    [Fact]
    public void 引用清单覆盖全部消费点且删除引用中的样式被拦截()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        var style = kernel.AddStyle();
        const string sid = "st-1";

        // 全部消费点各挂一处：元素级、表格级、列、表头格、条件样式、关键词样式。
        var text = new TextElement { XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 10 };
        text.Text.StyleId = sid;
        template.Add(text);

        var table = new TableElement { XMm = 10, YMm = 40, WidthMm = 80, HeightMm = 40 };
        table.Table.Columns.Add(new TableColumn { Title = "品名", Field = "items.name" });
        table.Table.Columns[0].StyleId = sid;
        table.Table.StyleId = sid;
        table.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 0, Title = "品名", StyleId = sid });
        table.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "50", StyleId = sid });
        table.Table.Highlights.Add(new TableHighlightRule { Keyword = "缺货", StyleId = sid });
        template.Add(table);

        var usage = kernel.GetStyleUsage(sid);
        usage.Should().BeEquivalentTo(
        [
            new EditorKernel.StyleUsage(text.Id, "文本样式"),
            new EditorKernel.StyleUsage(table.Id, "表格样式"),
            new EditorKernel.StyleUsage(table.Id, "表格列1"),
            new EditorKernel.StyleUsage(table.Id, "表头格1"),
            new EditorKernel.StyleUsage(table.Id, "条件样式1"),
            new EditorKernel.StyleUsage(table.Id, "关键词样式1"),
        ], "全部消费点（载荷级 + 表格各族）都要计入引用清单");

        var blocked = () => kernel.RemoveStyle(sid);
        blocked.Should().Throw<InvalidOperationException>()
            .WithMessage("*仍被 6 处引用*先取消引用再删除*");
        template.Styles.Should().Contain(s => s.Id == sid, "拦截后样式原地保留");

        // 全部取消引用后可以删，撤销时原位恢复（不追加到末尾）。
        text.Text.StyleId = null;
        table.Table.StyleId = null;
        table.Table.Columns[0].StyleId = null;
        table.Table.HeaderRows[0].StyleId = null;
        table.Table.ConditionalStyles[0].StyleId = null;
        table.Table.Highlights[0].StyleId = null;

        var removeUndo = Capture(kernel, () => kernel.RemoveStyle(sid));
        template.Styles.Should().BeEmpty();

        removeUndo.Revert();
        template.Styles.Select(s => s.Id).Should().Equal([sid], because: "撤销删除后样式回到原位");
    }

    [Fact]
    public void 删除不存在的样式抛异常()
    {
        var kernel = KernelFixture.Kernel(KernelFixture.Page());
        var act = () => kernel.RemoveStyle("st-404");
        act.Should().Throw<InvalidOperationException>().WithMessage("*st-404*");
    }

    [Fact]
    public void 样式引用描述符的值经内核写回可撤销()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        kernel.AddStyle();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        kernel.Selection.Select(rect.Id);

        kernel.SetSelectedProperty("styleId", "st-1");
        rect.StyleId.Should().Be("st-1", "元素级样式引用走属性通道正常写回");

        kernel.SetSelectedProperty("styleId", null);
        rect.StyleId.Should().BeNull("传 null 即取消引用");
    }

    [Fact]
    public void 样式引用写回是命令化改动可重放撤销()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        kernel.AddStyle();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        kernel.Selection.Select(rect.Id);

        var undo = Capture(kernel, () => kernel.SetSelectedProperty("styleId", "st-1"));
        rect.StyleId.Should().Be("st-1");

        undo.Revert();
        rect.StyleId.Should().BeNull("撤销引用与撤销普通属性同口径");

        Capture(kernel, () => kernel.SetSelectedProperty("styleId", "st-1"));
        rect.StyleId.Should().Be("st-1", "重新应用后引用恢复");
    }

    [Fact]
    public void 表格textColor描述符写回即清空()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        var table = new TableElement { XMm = 10, YMm = 40, WidthMm = 80, HeightMm = 40 };
        table.Table.Columns.Add(new TableColumn { Title = "品名", Field = "items.name" });
        template.Add(table);
        kernel.Selection.Select(table.Id);

        kernel.SetSelectedProperty("table.textColor", "#FF0000");
        table.Table.TextColor.Should().Be(new SKColor(255, 0, 0));

        kernel.SetSelectedProperty("table.textColor", null);
        table.Table.TextColor.Should().BeNull("留空 = 黑色默认");
    }

    /// <summary>订阅命令流执行动作，返回动作期间最近一条命令（撤销 = 对它再 RunCommand）。</summary>
    static EditorCommand Capture(EditorKernel kernel, Action action)
    {
        EditorCommand? captured = null;
        void Handler(EditorCommand command) => captured = command;
        kernel.CommandExecuted += Handler;
        try
        {
            action();
        }
        finally
        {
            kernel.CommandExecuted -= Handler;
        }

        return captured ?? throw new InvalidOperationException("动作期间没有命令被执行。");
    }
}
