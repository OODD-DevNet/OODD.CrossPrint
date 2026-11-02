using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 纸张设置、创建副本、右键选中与居中：这些是设计器 UX 的"命令层"，
/// 全部走 <see cref="EditorCommand"/>，因此测试同时核对模型数值、选中集合与 Revert。
/// </summary>
public class KernelFeatureTests
{
    [Fact]
    public void 纸张预设_改尺寸并发命令_撤销还原原纸张()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        var changed = 0;
        kernel.TemplateChanged += () => changed++;

        var command = CaptureCommand(kernel, c => c.SetPaperPreset(PaperTypePresets.A3));

        // A3 的纵向尺寸来自 PaperTypePresets 表：297×420。这里按表值写死，不用被测实现推算。
        template.Paper.Preset.Should().Be(PaperTypePresets.A3);
        template.Paper.WidthMm.Should().Be(297);
        template.Paper.HeightMm.Should().Be(420);
        template.Paper.PageWidthMm.Should().Be(297);
        template.Paper.PageHeightMm.Should().Be(420);
        changed.Should().Be(1);
        command.Label.Should().Be("设置纸张 A3");

        command.Revert();

        template.Paper.WidthMm.Should().Be(210, "Revert 必须换回变更前的纸张实例");
        template.Paper.HeightMm.Should().Be(297);
    }

    [Fact]
    public void 横向_交换页面宽高_撤销恢复纵向()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        var kernel = KernelFixture.Kernel(template);

        kernel.SetPaperOrientation(PaperOrientation.Landscape);

        // A4 纵向 210×297；横向后实际页面 = 297×210。
        template.Paper.PageWidthMm.Should().Be(297);
        template.Paper.PageHeightMm.Should().Be(210);

        var command = CaptureCommand(kernel, c => c.SetPaperOrientation(PaperOrientation.Portrait));
        command.Label.Should().Be("纸张纵向");
        command.Revert();

        template.Paper.PageWidthMm.Should().Be(297);
        template.Paper.PageHeightMm.Should().Be(210);
    }

    [Fact]
    public void 连续纸_选择预设后强制纵向且拒绝旋转()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        var kernel = KernelFixture.Kernel(template);
        kernel.SetPaperOrientation(PaperOrientation.Landscape);

        kernel.SetPaperPreset(PaperTypePresets.Receipt80);

        // Receipt80 的纵向尺寸是 80×0（高 0 = 连续纸）。
        template.Paper.WidthMm.Should().Be(80);
        template.Paper.HeightMm.Should().Be(0);
        template.Paper.IsContinuous.Should().BeTrue();
        template.Paper.Orientation.Should().Be(PaperOrientation.Portrait, "连续纸没有固定页高，旋转会把页宽换成 0");
        template.Paper.PageWidthMm.Should().Be(80);

        var rotate = () => kernel.SetPaperOrientation(PaperOrientation.Landscape);
        rotate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 自定义纸张_写入Custom预设与尺寸_撤销还原()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        var kernel = KernelFixture.Kernel(template);

        kernel.SetCustomPaper(100, 150);

        template.Paper.Preset.Should().Be(PaperTypePresets.Custom);
        template.Paper.WidthMm.Should().Be(100);
        template.Paper.HeightMm.Should().Be(150);
        template.Paper.PageWidthMm.Should().Be(100);
        template.Paper.PageHeightMm.Should().Be(150);

        var command = CaptureCommand(kernel, c => c.SetCustomPaper(58, 0));
        command.Label.Should().Contain("58×0");
        template.Paper.IsContinuous.Should().BeTrue("高度 0 表示连续纸");
        template.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
        command.Revert();

        template.Paper.WidthMm.Should().Be(100);
        template.Paper.HeightMm.Should().Be(150);
    }

    [Fact]
    public void 创建副本_新id偏移5mm并选中副本_撤销移除副本恢复原选中()
    {
        var template = KernelFixture.Page();
        var source = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(source.Id);

        kernel.DuplicateSelection();

        template.Elements.Should().HaveCount(2);
        var clone = template.Elements[1];
        clone.Id.Should().NotBe(source.Id);
        clone.TypeKey.Should().Be(source.TypeKey);
        // 偏移量来自 EditorKernel.DuplicateOffsetMm = 5mm：10+5、10+5。
        clone.XMm.Should().Be(15);
        clone.YMm.Should().Be(15);
        kernel.Selection.Ids.Should().ContainSingle().Which.Should().Be(clone.Id, "副本创建后应该选中副本，用户能立刻继续拖");

        var command = CaptureCommand(kernel, c => c.DuplicateSelection());
        command.Label.Should().Be("创建副本 1 个元素");
        template.Elements.Should().HaveCount(3);
        command.Revert();

        template.Elements.Should().HaveCount(2);
        kernel.Selection.Ids.Should().ContainSingle().Which.Should().Be(clone.Id, "撤销第二次副本要回到第一次副本的选中态");
    }

    [Fact]
    public void 创建副本靠近页边时夹在页内()
    {
        var template = KernelFixture.Page();
        // 页宽 210；盒宽 20，右上角能放到的最大 x = 190。
        var source = KernelFixture.AddRect(template, 195, 290, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(source.Id);

        kernel.DuplicateSelection();

        var clone = template.Elements[1];
        clone.XMm.Should().Be(190, "195+5 会越出 210mm 页宽，副本要夹回页内");
        // 页高 297；盒高 15，最大 y = 282。
        clone.YMm.Should().Be(282);
    }

    [Fact]
    public void 右键命中元素即单选_命中空白清空选中()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var other = KernelFixture.AddRect(template, 60, 60, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(other.Id);

        var hit = kernel.SelectAt(KernelFixture.At(kernel, 20, 15, button: PointerButton.Secondary));

        hit.Should().BeSameAs(rect);
        kernel.Selection.Ids.Should().ContainSingle().Which.Should().Be(rect.Id);

        var blank = kernel.SelectAt(KernelFixture.At(kernel, 100, 100, button: PointerButton.Secondary));

        blank.Should().BeNull();
        kernel.Selection.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void 水平与垂直居中_按页面尺寸计算且可撤销()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        var horizontal = CaptureCommand(kernel, c => c.CenterSelectionHorizontally());
        // (210 - 20) / 2 = 95；y 不动。
        rect.XMm.Should().Be(95);
        rect.YMm.Should().Be(10);
        horizontal.Label.Should().Be("水平居中 1 个元素");
        horizontal.Revert();
        rect.XMm.Should().Be(10);

        var vertical = CaptureCommand(kernel, c => c.CenterSelectionVertically());
        // (297 - 15) / 2 = 141；x 不动。
        rect.YMm.Should().Be(141);
        rect.XMm.Should().Be(10);
        vertical.Label.Should().Be("垂直居中 1 个元素");
        vertical.Revert();
        rect.YMm.Should().Be(10);
    }

    static EditorCommand CaptureCommand(EditorKernel kernel, Action<EditorKernel> action)
    {
        EditorCommand? captured = null;
        void Handler(EditorCommand command) => captured = command;
        kernel.CommandExecuted += Handler;
        try
        {
            action(kernel);
        }
        finally
        {
            kernel.CommandExecuted -= Handler;
        }

        captured.Should().NotBeNull("这次操作必须产生一条命令，否则撤销栈里就没有它");
        return captured!;
    }
}
