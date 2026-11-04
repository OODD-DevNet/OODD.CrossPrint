using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 撤销/重做栈：执行即入栈、撤销回滚（含删除恢复）、重做重放、容量 200 裁剪、新命令作废重做链。
/// </summary>
public class UndoRedoTests
{
    [Fact]
    public void 执行_撤销_重做_往返还原()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 30, 20);
        kernel.Template.Elements.Count.Should().Be(1);

        kernel.Selection.Select(rect.Id);
        kernel.DeleteSelection();
        kernel.Template.Elements.Should().BeEmpty("删除命令已执行");
        kernel.CanUndo.Should().BeTrue();
        kernel.CanRedo.Should().BeFalse();

        kernel.Undo().Should().BeTrue("删除可撤销");
        kernel.Template.Elements.Should().ContainSingle("撤销把元素插回原索引");
        kernel.Template.Elements[0].Id.Should().Be(rect.Id, "同一实例插回");
        kernel.CanRedo.Should().BeTrue();

        kernel.Redo().Should().BeTrue("重做重放删除");
        kernel.Template.Elements.Should().BeEmpty();
        kernel.CanRedo.Should().BeFalse("重做链已耗尽");
    }

    [Fact]
    public void 拖拽产生的几何命令_可撤销()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 30, 20);
        kernel.Selection.Select(rect.Id);

        // 按 1:1 换算拖 10mm：起点 (10,10) → 终点 (20,10)。
        KernelFixture.Drag(kernel,
            KernelFixture.At(kernel, 25, 15),
            KernelFixture.At(kernel, 35, 15));

        rect.XMm.Should().BeApproximately(20, 1e-6, "px↔mm 往返有浮点噪声");
        kernel.Undo().Should().BeTrue();
        rect.XMm.Should().BeApproximately(10, 1e-6, "撤销把几何还原成按下时的快照");
        kernel.Redo().Should().BeTrue();
        rect.XMm.Should().BeApproximately(20, 1e-6);
    }

    [Fact]
    public void 容量200_超限丢弃最旧的()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 30, 20);
        kernel.UndoCapacity.Should().Be(EditorKernel.DefaultUndoCapacity, "默认 200 条");

        // 201 条属性命令：栈里只留最近 200 条，第一条已被挤出。
        for (var i = 0; i < EditorKernel.DefaultUndoCapacity + 1; i++)
        {
            var value = i;
            kernel.CommitPropertyChange(
                $"微调 {value}",
                () => rect.WidthMm = 30 + value,
                () => rect.WidthMm = value == 0 ? 30 : 30 + value - 1);
        }

        for (var i = 0; i < EditorKernel.DefaultUndoCapacity; i++)
            kernel.Undo().Should().BeTrue();

        kernel.CanUndo.Should().BeFalse("200 条撤销完，最早那条已被容量裁掉");
        rect.WidthMm.Should().Be(30, "剩下的可撤销内容正好回到初值");
    }

    [Fact]
    public void 撤销后执行新命令_重做链作废()
    {
        var kernel = KernelFixture.Kernel();
        var rect = KernelFixture.AddRect(kernel.Template, 10, 10, 30, 20);
        kernel.Selection.Select(rect.Id);
        kernel.DeleteSelection();
        kernel.Undo().Should().BeTrue();

        kernel.CommitPropertyChange("改宽", () => rect.WidthMm = 40, () => rect.WidthMm = 30);
        kernel.CanRedo.Should().BeFalse("新命令必须作废整条重做链");
        kernel.Undo().Should().BeTrue();
        rect.WidthMm.Should().Be(30, "撤销回到新命令之前的状态，而不是重放旧删除");
    }

    [Fact]
    public void 空栈_撤销重做_返回False()
    {
        var kernel = KernelFixture.Kernel();
        kernel.Undo().Should().BeFalse();
        kernel.Redo().Should().BeFalse();

        kernel.ClearUndoHistory();
        kernel.CanUndo.Should().BeFalse();
        kernel.CanRedo.Should().BeFalse();
    }
}
