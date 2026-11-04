using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.Hosts.Avalonia;
using CrossPrint.Samples.Avalonia;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Hosts.Avalonia.Tests;

/// <summary>
/// 形状元素（shape）宿主链路回归：工具箱放置 → 属性面板改种类 → 属性描述符齐全（完备性门在 Designer.Tests 全量把关）。
/// </summary>
public sealed class ShapeElementHostTests
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    [Fact]
    public async Task 放置形状元素_默认三角形_属性面板可改种类()
    {
        await Session.Dispatch(Run, CancellationToken.None);
    }

    [Fact]
    public async Task 矩形属性面板_圆角半径只留逐角一行()
    {
        await Session.Dispatch(RunCornerRows, CancellationToken.None);
    }

    static void RunCornerRows()
    {
        var window = new Window { Width = 1280, Height = 860, Content = new MainView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var designer = window.GetVisualDescendants().OfType<DesignerControl>().Single();
        var panel = window.GetVisualDescendants().OfType<PropertyPanel>().Single();

        var rect = new RectangleElement { XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 20, Id = "rect-1" };
        designer.Kernel.Template.Elements.Add(rect);
        designer.Kernel.Selection.Select("rect-1");
        panel.Refresh(); // Selection.Select 是纯模型操作，不触发 ViewChanged
        Dispatcher.UIThread.RunJobs();

        // 用户实测：两个「圆角半径」框（标量 + 逐角）引起混淆。标量已隐藏，只留逐角一行。
        CornerRows(panel).Count.Should().Be(1, "面板只保留一个圆角半径（逐角）输入框");

        // 旧模板只有标量圆角时，逐角框显示标量值（编辑不断档）。
        rect.Rectangle.CornerRadiusMm = 2;
        panel.Refresh();
        Dispatcher.UIThread.RunJobs();
        var rows = CornerRows(panel);
        rows.Count.Should().Be(1);
        rows[0].GetVisualDescendants().OfType<TextBox>().First().Text.Should().Be("2",
            "cornerRadiiMm 为空而标量为 2 时，逐角框应显示标量值");

        window.Close();
    }

    static System.Collections.Generic.List<Grid> CornerRows(PropertyPanel panel) =>
        panel.Rows.OfType<Grid>()
            .Where(g => g.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text is not null && t.Text.StartsWith("圆角半径", StringComparison.Ordinal)))
            .ToList();

    static void Run()
    {
        var window = new Window { Width = 1280, Height = 860, Content = new MainView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var kernel = window.GetVisualDescendants().OfType<DesignerControl>().Single().Kernel;
        kernel.AddElementAtCenter(ElementTypeKeys.Shape);

        var shape = kernel.Template.Elements.OfType<ShapeElement>().Single();
        shape.Shape.Kind.Should().Be(ShapeKind.Rectangle, "工具箱放置的形状默认矩形（25×25 即正方形）");

        // 属性面板通道：种类下拉必须存在且可写（枚举松散赋值）。
        var props = ElementProperties.For(shape);
        var kindProp = props.Find("shape.kind");
        kindProp.Should().NotBeNull("形状种类下拉是形状元素的主编辑通道");
        kindProp!.SetValue(shape, "Diamond");
        shape.Shape.Kind.Should().Be(ShapeKind.Diamond, "下拉给字符串也要按枚举语义松散赋值");

        // 撤销链路：放置命令在栈上。
        kernel.CanUndo.Should().BeTrue("放置必须走命令栈可撤销");

        window.Close();
    }
}
