using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrossPrint.Core;
using CrossPrint.Hosts.Avalonia;
using CrossPrint.Samples.Avalonia;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Hosts.Avalonia.Tests;

/// <summary>
/// 「测试数据」行与画布一致（用户实测：画布印出数据值而属性框空白）：
/// 元素测试数据为空时，面板应显示画布当前生效值（宿主数据/模板设计数据解析结果）。
/// </summary>
public sealed class TestDataPanelTests
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    [Fact]
    public async Task 绑定元素测试数据为空_面板显示画布生效值()
    {
        await Session.Dispatch(Run, CancellationToken.None);
    }

    static void Run()
    {
        var window = new Window { Width = 1280, Height = 860, Content = new MainView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var designer = window.GetVisualDescendants().OfType<DesignerControl>().Single();
        var panel = window.GetVisualDescendants().OfType<PropertyPanel>().Single();

        // 模板 DesignData 为空时，画布取宿主数据——直接给一个含 barcode 的数据源。
        designer.Data = new Dictionary<string, object?> { ["barcode"] = "8801234567892" };

        var barcode = new BarcodeElement { XMm = 10, YMm = 10, WidthMm = 60, HeightMm = 20, Id = "barcode-1" };
        barcode.Binding = "barcode";
        barcode.Barcode = new BarcodeOptions { Content = "{{barcode}}" };
        designer.Kernel.Template.Elements.Add(barcode);
        designer.Kernel.Selection.Select(barcode.Id);
        panel.Refresh(); // Selection.Select 是纯模型操作，不触发 ViewChanged
        Dispatcher.UIThread.RunJobs();

        var testDataBox = FindTextBoxByLabel(panel.Rows, "测试数据");
        testDataBox.Should().NotBeNull("选中绑定元素后面板应有测试数据行");
        testDataBox!.Text.Should().Be("8801234567892",
            "元素测试数据为空时，测试数据框应显示画布当前生效值（与画布一致）");

        // 元素自身的测试数据优先：直接改模型后强制重建面板（绕过命令的写入不改变签名）。
        barcode.TestData = "12345";
        panel.Refresh();
        Dispatcher.UIThread.RunJobs();
        FindTextBoxByLabel(panel.Rows, "测试数据")!.Text.Should().Be("12345");

        window.Close();
    }

    /// <summary>只认 BuildRow 的 Grid 行结构（水印整段容器是 StackPanel 顶层，天然排除），避免同名标签干扰。</summary>
    static TextBox? FindTextBoxByLabel(System.Collections.Generic.IEnumerable<Control> rows, string label)
    {
        foreach (var row in rows)
        {
            if (row is not Grid grid)
                continue;
            if (grid.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label))
                return grid.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        }
        return null;
    }
}
