using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// hiprint 表格 <c>columns</c> 的「行内分组列对象」回归。
/// 分组列对象 <c>{title, columns:[…]}</c> 既会出现在扁平清单里，也会整行包在二维表头的某一行里；
/// 后者若被当成一个单元格读，分组下面的子列全体丢失，表格只剩 1 列，
/// 设计器 <c>InteractionCommands.Remove</c> 的「只剩一列不能删」守卫就会一直抛异常——
/// 用户看到的正是「导入的表格加不了/删不了列」。这里钉住两种写法都展开成子列。
/// 列宽不在这里断言：<c>ScaleWidths</c> 会把显式宽度按元素宽度整体缩放，数值由真实导入结果决定。
/// </summary>
public class HiprintTableGroupRowTests
{
    static string TableJson(string columnsJson)
        => """
           {
             "options": {
               "left": 5, "top": 5, "width": 300, "height": 20,
               "columns": 
           """ + columnsJson + """
             },
             "printElementType": { "type": "table" }
           }
           """;

    static HiprintImportResult ImportTable(string columnsJson)
        => HiprintTemplateImporter.Import(
            "{ \"panels\": [ { \"width\": 210, \"height\": 297, \"printElements\": [ "
            + TableJson(columnsJson)
            + " ] } ] }");

    [Fact]
    public void 表头_二维表头行里的分组列对象展开子列且列数不丢()
    {
        const string columns = """
            [[
              { "title": "基本信息", "columns": [
                { "title": "品名", "field": "name", "width": 40, "checked": true },
                { "title": "数量", "field": "qty", "width": 20, "checked": true }
              ] }
            ]]
            """;

        var result = ImportTable(columns);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2, "行内分组必须展开成子列；当成一个单元格读会让整张表只剩 1 列");
        table.Table.Columns.Select(c => c.Title).Should().Equal("品名", "数量");
        table.Table.Columns.Select(c => c.Field).Should().Equal("name", "qty");
        table.Table.Columns.Should().HaveCountGreaterOrEqualTo(2,
            "设计器 Remove 的守卫是 Columns.Count <= 1 就抛异常，列数 ≥ 2 才能删列");
        table.Table.HeaderRows.Should().HaveCount(2, "展开后的两列各自落在第 0 行表头");
        table.Table.HeaderRows.Select(h => (h.Row, h.Column)).Should().Equal((0, 0), (0, 1));

        var issue = result.Issues.Should().ContainSingle(i => i.Code == "hiprint.table-group-header").Subject;
        issue.Severity.Should().Be(HiprintImportSeverity.Info, "分组标题留不住属可接受的降级，不是警告");
        issue.ElementId.Should().Be("el-1");
        issue.Message.Should().Contain("基本信息", "提示要列出被丢掉的分组标题");
    }

    [Fact]
    public void 表头_二维表头行没有分组时列数与表头行原样保留()
    {
        const string columns = """
            [[
              { "title": "品名", "field": "name", "width": 40, "checked": true },
              { "title": "数量", "field": "qty", "width": 20, "checked": true }
            ]]
            """;

        var result = ImportTable(columns);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2, "没有分组对象时行路径不能被展开逻辑吃掉单元格");
        table.Table.Columns.Select(c => c.Field).Should().Equal("name", "qty");
        table.Table.HeaderRows.Should().HaveCount(2);
        table.Table.HeaderRows.Select(h => (h.Row, h.Column)).Should().Equal((0, 0), (0, 1));
        result.Issues.Should().BeEmpty("普通二维表头是受支持的写法，不该有降级提示");
    }

    [Fact]
    public void 表头_多行里各自的分组列对象只记一条分组表头提示()
    {
        const string columns = """
            [
              [
                { "title": "组一", "columns": [
                  { "title": "甲", "field": "a", "width": 40, "checked": true },
                  { "title": "乙", "field": "b", "width": 40, "checked": true }
                ] }
              ],
              [
                { "title": "丙", "field": "c", "width": 40, "checked": true },
                { "title": "丁", "field": "d", "width": 40, "checked": true }
              ]
            ]
            """;

        var result = ImportTable(columns);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2);
        table.Table.Columns.Select(c => c.Field).Should().Equal(new[] { "c", "d" },
            "叶列取最底层表头单元格，分组标题只作用于上一行");
        table.Table.HeaderRows.Should().HaveCount(4, "上层展开出 2 个分组子列 + 下层 2 个普通单元格");
        table.Table.HeaderRows.Count(h => h.Row == 0).Should().Be(2);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == "hiprint.table-group-header").Subject;
        issue.Message.Should().Contain("组一", "分组标题被丢掉要能指名");
    }

    [Fact]
    public void 表头_行内分组展开后各行格数不同时由固定网格归一化()
    {
        // 分组展开后第 0 行 2 格、第 1 行 1 格。这里不发明跨列模型，
        // 只钉住 PlaceGrid/BuildColumns 的真实结果：列数取最长行，叶列按最底层单元格决定标题与字段。
        const string columns = """
            [
              [
                { "title": "基本信息", "columns": [
                  { "title": "品名", "field": "name", "width": 40, "checked": true },
                  { "title": "数量", "field": "qty", "width": 20, "checked": true }
                ] }
              ],
              [ { "title": "备注", "field": "note", "width": 60, "checked": true } ]
            ]
            """;

        var result = ImportTable(columns);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2, "列数取各行 colspan 之和的最大值");
        // 网格按位置摆放、列字段自下而上取第一个有值的单元格：第 1 行只有 1 格，
        // 于是第 0 列被“备注”占住，“品名”只剩表头标题、不再对应任何列的字段绑定。
        table.Table.Columns.Select(c => c.Field).Should().Equal(new[] { "note", "qty" },
            "第 1 列没有底层单元格，字段回落到自下而上第一个有值的单元格");
        table.Table.HeaderRows.Should().HaveCount(3);
        table.Table.HeaderRows.Select(h => (h.Row, h.Column)).Should().Equal((0, 0), (0, 1), (1, 0));

        result.Issues.Should().ContainSingle(i => i.Code == "hiprint.table-group-header",
            "整张表只记一条分组表头提示");
    }
}
