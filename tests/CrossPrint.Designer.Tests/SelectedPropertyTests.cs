using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 属性通道与内核的接线：面板只认"键 + 值"，内核负责校验、规范化、落库、广播、可撤销。
/// 这一层的每个失败模式都对应一个真实的坏体验：写不进去（面板显示改了实际没改）、
/// 写进去不可撤销、多选时面板形状乱跳。
/// </summary>
public class SelectedPropertyTests
{
    [Fact]
    public void 单选给出属性通道_空选与多选为null()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);

        kernel.SelectedProperties.Should().BeNull("空选时没有可编辑对象");

        kernel.Selection.Select(rect.Id);
        var properties = kernel.SelectedProperties;
        properties.Should().NotBeNull();
        properties!.Element.Should().BeSameAs(rect, "通道必须指向模型里的那个实例，不能是拷贝");
        properties.Find("xMm").Should().NotBeNull("每种元素都有公共几何字段");
        properties.Find("rectangle.fillColor").Should().NotBeNull("矩形要暴露自己的载荷字段");

        var other = KernelFixture.AddRect(kernel.Template, 50, 10, 10, 10);
        kernel.Selection.Add(other.Id);
        kernel.SelectedProperties.Should().BeNull("多选时没有单一类型的描述符集合可给");
    }

    [Fact]
    public void 写回是一条命令_撤销与重做都还原到正确值()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);
        kernel.Selection.Select(rect.Id);

        var executed = new List<EditorCommand>();
        kernel.CommandExecuted += executed.Add;
        var changes = 0;
        kernel.TemplateChanged += () => changes++;

        kernel.SetSelectedProperty("xMm", 42.5);

        rect.XMm.Should().Be(42.5);
        executed.Should().HaveCount(1, "一次写回只该产生一条命令");
        executed[0].Label.Should().Contain("X 坐标", "命令名要能直接显示在撤销菜单里");
        changes.Should().Be(1, "宿主靠 TemplateChanged 刷新画布与面板");

        executed[0].Revert();
        rect.XMm.Should().Be(10, "撤销必须回到写之前的取值（不是 0，也不是未定义）");

        executed[0].Apply();
        rect.XMm.Should().Be(42.5, "命令前向必须幂等地回到 42.5");
    }

    [Fact]
    public void 字符串值先规范化再落库_撤销还原的是数值而不是字符串()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);
        kernel.Selection.Select(rect.Id);

        var executed = new List<EditorCommand>();
        kernel.CommandExecuted += executed.Add;

        kernel.SetSelectedProperty("widthMm", "12.5");

        rect.WidthMm.Should().Be(12.5, "面板送来的多是字符串，落库必须是 double");
        executed.Should().HaveCount(1);
        executed[0].Revert();
        rect.WidthMm.Should().Be(20);
    }

    [Fact]
    public void 未知键与只读键分别报错_且模型一动不动()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);
        kernel.Selection.Select(rect.Id);

        Action unknown = () => kernel.SetSelectedProperty("does.not.exist", 1);
        unknown.Should().Throw<ArgumentException>().WithMessage("*does.not.exist*");

        Action readOnly = () => kernel.SetSelectedProperty("id", "el-999");
        readOnly.Should().Throw<InvalidOperationException>("id 由内核分配，面板不能改");
        rect.Id.Should().NotBe("el-999");
        rect.XMm.Should().Be(10);
    }

    [Fact]
    public void 多选时写回报错_不会悄悄只改第一个()
    {
        var kernel = KernelFixture.Kernel();
        var a = KernelFixture.AddRect(kernel.Template, 10, 10, 20, 10);
        var b = KernelFixture.AddRect(kernel.Template, 50, 10, 20, 10);
        kernel.Selection.Select(a.Id);
        kernel.Selection.Add(b.Id);

        Action act = () => kernel.SetSelectedProperty("xMm", 5);
        act.Should().Throw<InvalidOperationException>("多选的批量编辑走对齐/分布命令，不走单元素属性面板");
        a.XMm.Should().Be(10);
        b.XMm.Should().Be(50);
    }
}
