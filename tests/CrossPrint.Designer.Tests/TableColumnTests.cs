using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 表格列与规则类列表（条件样式/关键词样式/静态行合并）的结构变更（增删）与属性通道的动作声明。
/// 一次列增删同时牵动 Columns、Rows 每行单元格、HeaderRows 的列下标与跨度三处，
/// 每个用例都对这三处各自断言——漏一处就是"列数对了但数据错位"的隐形 bug。
/// 规则类列表不带联动，重点在：追加即有可见默认样式、删除可撤销、stale 动作键明确报错。
/// </summary>
public class TableColumnTests
{
    static TableElement AddTable(PrintTemplate template, int columns = 3, bool withRowSpans = false)
    {
        var element = new TableElement
        {
            XMm = 10,
            YMm = 10,
            WidthMm = 60,
            HeightMm = 30,
            Table = new TableOptions
            {
                Columns = Enumerable.Range(0, columns)
                    .Select(i => new TableColumn { Title = $"列{i + 1}", WidthMm = 20 })
                    .ToList(),
                Rows =
                [
                    ["a1", "a2", "a3"],
                    ["b1", "b2", "b3"],
                ],
                HeaderRows =
                [
                    new TableHeaderCell { Row = 0, Column = 0, ColSpan = 2, Title = "左组合" },
                    new TableHeaderCell { Row = 0, Column = 2, Title = "右" },
                ],
                // 与表头同构的两条静态行合并：列下标语义一致，供增删列联动用例校验。
                RowSpans = withRowSpans
                    ?
                    [
                        new TableRowSpan { Row = 0, Column = 0, ColSpan = 2 },
                        new TableRowSpan { Row = 1, Column = 2 },
                    ]
                    : [],
            },
        };
        template.Add(element);
        return element;
    }

    [Fact]
    public void 追加列_列行头三处都补齐()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.appendColumn");

        table.Table.Columns.Should().HaveCount(4, "追加后多一列");
        table.Table.Columns[3].Title.Should().Be("列4", "新列标题按序号取");
        table.Table.Rows.Should().HaveCount(2);
        table.Table.Rows[0].Should().Equal("a1", "a2", "a3", "");
        table.Table.Rows[1].Should().Equal("b1", "b2", "b3", "");
        // 追加不影响既有表头单元格的下标与跨度。
        table.Table.HeaderRows.Select(c => $"{c.Column},{c.ColSpan}")
            .Should().Equal("0,2", "2,1");
    }

    [Fact]
    public void 追加定宽列_元素宽加到盖得住_撤销一并还原()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);   // 元素宽 60mm，3 × 20mm 定宽列刚好填满
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.appendColumn");

        // 定宽之和 4 × 20 = 80mm > 元素宽 60mm：引擎按定宽累加不缩放，不加宽就画到选中框外面
        table.WidthMm.Should().Be(80, "元素宽只多出新增列那 20mm");
        executed!.Label.Should().Contain("60→80", "顺带加宽了元素，撤销菜单要说实话");

        executed.Revert();

        table.WidthMm.Should().Be(60, "撤销把元素宽一起还原");
        table.Table.Columns.Should().HaveCount(3);
    }

    [Fact]
    public void 插入定宽列_加宽夹到纸边不越纸()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, columns: 2);
        table.XMm = 160;
        table.WidthMm = 40;   // 右缘 200mm，离 A4（210mm）纸边只剩 50mm
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.columns[0].insertBefore");

        // 定宽之和 60mm 要右缘 220mm 才装得下，纸边上限是 210−160 = 50mm → 只加到纸边为止
        table.WidthMm.Should().Be(50, "加宽夹在纸边，不能越出去");
        (table.XMm + table.WidthMm).Should().Be(210, "加宽的上限就是纸边");
        executed!.Label.Should().Contain("40→50", "夹过的宽度也要写进命令名，否则用户以为表格还是 40mm");
    }

    [Fact]
    public void 均分列宽的表格_追加列不动元素宽()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        foreach (var column in table.Table.Columns)
            column.WidthMm = -1;   // 三列均分，没有定宽之和可谈

        kernel.Selection.Select(table.Id);
        kernel.RunElementAction("table.appendColumn");

        table.WidthMm.Should().Be(60, "新列 20mm 仍装得下 60mm 元素宽，不该整体加宽");
        table.Table.Columns.Take(3).Select(c => c.WidthMm).Should().Equal(-1, -1, -1);
    }

    [Fact]
    public void 删除列_元素宽保持不变()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[0].remove");

        table.WidthMm.Should().Be(60, "删列只是腾出空间，缩表是用户自己的事");
    }

    // ---------------- 新列标题 ----------------

    [Fact]
    public void 插入列_新列标题避开既有标题()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);   // 既有标题：列1 / 列2 / 列3
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[1].insertBefore");

        // 按插入位置取名会再生成一个"列2"，面板里两条同名根本分不出改的是哪一列
        table.Table.Columns.Select(c => c.Title).Should().Equal("列1", "列4", "列2", "列3");
        table.Table.Columns.Select(c => c.Title).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 中间插入列_行单元格插在下标处_表头下标右移()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[1].insertBefore");

        table.Table.Columns.Should().HaveCount(4);
        table.Table.Columns[1].Title.Should().Be("列4", "新列按现有标题续号，不与既有的列2同名");
        table.Table.Columns[2].Title.Should().Be("列2", "原第 2 列整体右移，标题跟着位置走而不是重新编号");
        table.Table.Rows[0].Should().Equal("a1", "", "a2", "a3");
        // 起始列 2 的单元格右移到 3；跨度 2 的单元格覆盖插入点，并入一格变 3。
        table.Table.HeaderRows.Select(c => $"{c.Column},{c.ColSpan}")
            .Should().Equal("0,3", "3,1");
    }

    [Fact]
    public void 插入到跨列单元格覆盖点_跨度并入一格()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        // "左组合"覆盖列 0–1：在列 1 前插入，新列应并入该单元格的跨度。
        kernel.RunElementAction("table.columns[1].insertBefore");

        table.Table.HeaderRows[0].Column.Should().Be(0);
        table.Table.HeaderRows[0].ColSpan.Should().Be(3, "插入点在跨度内，跨度加一");
        table.Table.HeaderRows[1].Column.Should().Be(3, "起始列在插入点之后的右移");
    }

    [Fact]
    public void 删除列_行去格_表头下标左移()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[1].remove");

        table.Table.Columns.Select(c => c.Title).Should().Equal("列1", "列3");
        table.Table.Rows[0].Should().Equal("a1", "a3");
        // 跨度覆盖被删列的收窄一格；起始列 2 的左移到 1。
        table.Table.HeaderRows.Select(c => $"{c.Column},{c.ColSpan}")
            .Should().Equal("0,1", "1,1");
    }

    [Fact]
    public void 删除对位列_单格表头整格消失()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        // "右"单元格（Column=2, ColSpan=1）正好对位列 2。
        kernel.RunElementAction("table.columns[2].remove");

        table.Table.Columns.Should().HaveCount(2);
        table.Table.HeaderRows.Should().ContainSingle("对位的单格表头随列一起消失");
        table.Table.HeaderRows[0].Title.Should().Be("左组合");
    }

    [Fact]
    public void 删除后撤销_三处原样还原()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.columns[0].remove");

        table.Table.Columns.Select(c => c.Title).Should().Equal("列2", "列3");
        // 对位的跨度收窄一格，右侧单元格左移。
        table.Table.HeaderRows.Select(c => $"{c.Column},{c.ColSpan}")
            .Should().Equal("0,1", "1,1");

        executed!.Revert();

        table.Table.Columns.Select(c => c.Title).Should().Equal("列1", "列2", "列3");
        table.Table.Rows[0].Should().Equal("a1", "a2", "a3");
        // 快照还原必须连表头的跨度与下标一起复原。
        table.Table.HeaderRows.Select(c => $"{c.Column},{c.ColSpan}")
            .Should().Equal("0,2", "2,1");
        table.Table.HeaderRows[1].Title.Should().Be("右");
    }

    [Fact]
    public void 插入列_静态行合并随列右移_覆盖点加宽()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, withRowSpans: true);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[1].insertBefore");

        // 合并跨度的列语义与表头一致：起始列 2 右移到 3；覆盖插入点的跨度并入一格变 3。
        table.Table.RowSpans.Select(s => $"{s.Column},{s.ColSpan}")
            .Should().Equal("0,3", "3,1");
    }

    [Fact]
    public void 删除列_静态行合并随列左移_覆盖点收窄()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, withRowSpans: true);
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.columns[1].remove");

        // 跨度覆盖被删列的收窄一格；起始列 2 的左移到 1。
        table.Table.RowSpans.Select(s => $"{s.Column},{s.ColSpan}")
            .Should().Equal("0,1", "1,1");
    }

    [Fact]
    public void 删除对位列_单格合并整格消失()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, withRowSpans: true);
        kernel.Selection.Select(table.Id);

        // (Column=2,ColSpan=1) 正好对位列 2，删列后该合并随之消失。
        kernel.RunElementAction("table.columns[2].remove");

        table.Table.RowSpans.Should().ContainSingle("对位的单格合并随列一起消失");
        table.Table.RowSpans[0].Column.Should().Be(0);
        table.Table.RowSpans[0].ColSpan.Should().Be(2);
    }

    [Fact]
    public void 增删列后撤销_静态行合并原样还原()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, withRowSpans: true);
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.columns[0].remove");

        // 删列先联动合并跨度：覆盖被删列的收窄一格，右侧单格左移。
        table.Table.RowSpans.Select(s => $"{s.Column},{s.ColSpan}")
            .Should().Equal("0,1", "1,1");

        executed!.Revert();

        // 撤销连合并的下标与跨度一起复原（克隆快照带回原跨度）。
        table.Table.RowSpans.Select(s => $"{s.Column},{s.ColSpan}")
            .Should().Equal("0,2", "2,1");
        table.Table.RowSpans[0].ColSpan.Should().Be(2);
    }

    [Fact]
    public void 删除最后一列_拒绝()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, columns: 1);
        kernel.Selection.Select(table.Id);

        var act = () => kernel.RunElementAction("table.columns[0].remove");
        act.Should().Throw<InvalidOperationException>("表格没有列就渲染不出任何东西");
        table.Table.Columns.Should().HaveCount(1, "拒绝后模型不得被碰过");
    }

    [Fact]
    public void 动作键_未选中_多选_非表格_都拒绝()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        var rect = KernelFixture.AddRect(kernel.Template, 100, 100, 10, 10);

        kernel.Invoking(k => k.RunElementAction("table.appendColumn"))
            .Should().Throw<InvalidOperationException>("空选没有可操作对象");

        kernel.Selection.Select(table.Id);
        kernel.Selection.Add(rect.Id);
        kernel.Invoking(k => k.RunElementAction("table.appendColumn"))
            .Should().Throw<InvalidOperationException>("多选不指明改哪个表格");

        kernel.Selection.Select(rect.Id);
        kernel.Invoking(k => k.RunElementAction("table.appendColumn"))
            .Should().Throw<InvalidOperationException>("动作只对表格元素有效");
    }

    [Fact]
    public void 未知或越界的动作键_明确报错()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        kernel.Invoking(k => k.RunElementAction("table.columns[9].remove"))
            .Should().Throw<InvalidOperationException>("动作清单是快照，指向不存在的列必须报错而不是夹到别列");

        kernel.Invoking(k => k.RunElementAction("text.fontSizePt"))
            .Should().Throw<ArgumentException>("描述符键混进动作入口是接线错误");

        kernel.Invoking(k => k.RunElementAction("table.columns[x].remove"))
            .Should().Throw<ArgumentException>("解析不了的键直接拒绝");

        kernel.Invoking(k => k.RunElementAction("table.conditionalStyles[0].remove"))
            .Should().Throw<InvalidOperationException>("规则类清单也是快照，指向不存在的条目必须报错而不是夹到别条");
    }

    [Fact]
    public void 属性通道声明动作_表格有_其他类型没有()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template, columns: 2);
        var rect = KernelFixture.AddRect(kernel.Template, 100, 100, 10, 10);

        kernel.Selection.Select(table.Id);
        var actions = kernel.SelectedProperties!.Actions;
        actions.Select(a => a.Key).Should().Equal(
            "table.appendColumn",
            "table.columns[0].insertBefore",
            "table.columns[0].remove",
            "table.columns[1].insertBefore",
            "table.columns[1].remove",
            "table.appendConditional",
            "table.appendHighlight",
            "table.appendRowSpan");
        actions.All(a => a.DisplayName.Length > 0).Should().BeTrue("按钮文案直接用 DisplayName");

        kernel.Selection.Select(rect.Id);
        kernel.SelectedProperties!.Actions.Should().BeEmpty("非表格类型没有结构动作");
    }

    [Fact]
    public void 动作执行后广播_命令带人类可读名()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        var executed = new List<EditorCommand>();
        kernel.CommandExecuted += executed.Add;
        var changes = 0;
        kernel.TemplateChanged += () => changes++;

        kernel.RunElementAction("table.columns[1].remove");

        executed.Should().HaveCount(1);
        executed[0].Label.Should().Contain("删除表格列", "命令名要能直接显示在撤销菜单里");
        changes.Should().Be(1, "宿主靠 TemplateChanged 刷新画布");
    }

    // ---------------- 条件样式 / 关键词样式 / 静态行合并 ----------------

    [Fact]
    public void 追加条件样式_默认底色可见_撤销后清空()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.appendConditional");

        table.Table.ConditionalStyles.Should().HaveCount(1);
        table.Table.ConditionalStyles[0].BackgroundColor.Should().NotBeNull("新样式给可见底色，阈值一填就见效果，不至于加了没反应");
        executed!.Label.Should().Contain("追加条件样式", "命令名要能直接显示在撤销菜单里");

        executed.Revert();
        table.Table.ConditionalStyles.Should().BeEmpty("撤销还原到没有样式的状态");
    }

    [Fact]
    public void 删除条件样式_字段阈值底色随撤销原样还原()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        table.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Operator = TableConditionOperator.GreaterThan, Value = "50", BackgroundColor = SKColors.Gray });
        table.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "qty", Operator = TableConditionOperator.LessThan, Value = "10", TextColor = SKColors.Khaki });
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.conditionalStyles[0].remove");

        table.Table.ConditionalStyles.Should().ContainSingle("第 1 条被删，第 2 条顶上来")
            .Which.Field.Should().Be("qty");

        executed!.Revert();
        table.Table.ConditionalStyles.Should().HaveCount(2);
        table.Table.ConditionalStyles[0].Field.Should().Be("amount");
        table.Table.ConditionalStyles[0].Value.Should().Be("50", "比较值随快照复原");
        table.Table.ConditionalStyles[0].BackgroundColor.Should().Be(SKColors.Gray, "底色随快照复原");
        table.Table.ConditionalStyles[1].Field.Should().Be("qty");
    }

    [Fact]
    public void 删除最后一条条件样式_允许清空()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        table.Table.ConditionalStyles.Add(new TableConditionalStyle { Field = "amount", Value = "50", BackgroundColor = SKColors.Gray });
        kernel.Selection.Select(table.Id);

        kernel.RunElementAction("table.conditionalStyles[0].remove");

        table.Table.ConditionalStyles.Should().BeEmpty("没有样式只是不上色，是合法状态，不像列必须保留一列");
    }

    [Fact]
    public void 关键词样式_追加带占位关键词_撤销后清空()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.appendHighlight");

        table.Table.Highlights.Should().ContainSingle().Which.Keyword.Should().Be("关键词", "新样式带占位关键词，面板里立刻可见可改");
        executed!.Label.Should().Contain("追加关键词样式", "命令名要能直接显示在撤销菜单里");

        executed.Revert();
        table.Table.Highlights.Should().BeEmpty();
    }

    [Fact]
    public void 静态行合并_追加默认占位_撤销后清空()
    {
        var kernel = KernelFixture.Kernel();
        var table = AddTable(kernel.Template);
        kernel.Selection.Select(table.Id);

        EditorCommand? executed = null;
        kernel.CommandExecuted += command => executed = command;
        kernel.RunElementAction("table.appendRowSpan");

        table.Table.RowSpans.Should().ContainSingle().Which.RowSpan.Should().Be(1, "默认是单格占位，等用户改成目标行列与跨度");
        executed!.Label.Should().Contain("追加静态行合并", "命令名要能直接显示在撤销菜单里");

        executed.Revert();
        table.Table.RowSpans.Should().BeEmpty();
    }
}
