using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 用户报障的端到端复现：hiprint 导出的表格用**扁平 columns 数组**（一维列清单）书写时，
/// 导入后必须拿到真列数，并且能在编辑器里点中、删列、加列。
/// 这条链跨 Core（导入器）与 Designer（命中/结构动作），任何一环退化都会让"表格删不掉列"复现。
/// </summary>
public class HiprintTableEditingTests
{
    internal const string FlatColumnsTable = """
    {
      "panels": [
        {
          "width": 210,
          "height": 297,
          "printElements": [
            {
              "options": {
                "left": 20,
                "top": 30,
                "width": 300,
                "height": 100,
                "field": "items",
                "columns": [
                  { "title": "品名", "field": "name", "width": 100 },
                  { "title": "数量", "field": "qty", "width": 50 },
                  { "title": "单价", "field": "price", "width": 60 },
                  { "title": "金额", "field": "amount", "width": 70 }
                ]
              },
              "printElementType": { "type": "table" }
            }
          ]
        }
      ]
    }
    """;

    static TableElement ImportSingleTable(string json)
    {
        var result = HiprintTemplateImporter.Import(json);
        result.HasErrors.Should().BeFalse(string.Join(" | ", result.Issues.Select(i => i.Message)));
        return result.Template.Elements.Should().ContainSingle().Which.Should().BeOfType<TableElement>().Subject;
    }

    [Fact]
    public void 导入扁平columns的表格_点得中_删得列_加得列()
    {
        var table = ImportSingleTable(FlatColumnsTable);
        table.Table.Columns.Select(c => c.Title).Should().Equal("品名", "数量", "单价", "金额");
        table.Table.HeaderRows.Select(c => c.Row).Should().OnlyContain(row => row == 0, "一行表头不该被拆成四行");

        var kernel = KernelFixture.Kernel();
        kernel.Template.Elements.Add(table);

        // 元素中心落点：命中必须把这张导进来的表格选上，否则属性面板（连同删列按钮）根本出不来。
        var centerX = table.XMm + table.WidthMm / 2;
        var centerY = table.YMm + table.HeightMm / 2;
        kernel.OnPointerPressed(KernelFixture.At(kernel, centerX, centerY));
        kernel.Selection.Contains(table.Id).Should().BeTrue("导入的表格在画布上必须点得中");

        kernel.RunElementAction("table.columns[1].remove");
        table.Table.Columns.Select(c => c.Title).Should().Equal("品名", "单价", "金额");
        table.Table.Rows.Should().BeEmpty("静态行没写就不该凭空造出数据行");

        kernel.RunElementAction("table.appendColumn");
        table.Table.Columns.Should().HaveCount(4);
        table.Table.Columns[3].Title.Should().Be("列4");
    }

    [Fact]
    public void 导入表格的高度不是零_否则选不中()
    {
        var table = ImportSingleTable(FlatColumnsTable);

        table.HeightMm.Should().BeGreaterThan(0, "零高的表格元素在画布上选不中，属性面板也就打不开");
        table.WidthMm.Should().BeGreaterThan(0);
    }
}
