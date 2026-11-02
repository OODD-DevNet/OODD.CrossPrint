using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// M3 3.2/3.3/3.4/3.7：内核命中测试、交互状态机、命令对象、工具箱放置的无头测试。
/// </summary>
/// <remarks>
/// 换算锚点全在这里，每条期望值的算式都写在断言上方，不靠记忆也不靠"跑一遍看看实际值"：
/// <list type="bullet">
/// <item><description>1mm = 96/25.4 = <c>3.7795275590551180</c> 视口 px（zoom=1）。</description></item>
/// <item><description>视口 px → 设备 px：<c>× dpi/96</c>；设备 px → 视口 px：<c>× 96/dpi</c>。</description></item>
/// <item><description>页面 mm =（设备 px × 96/dpi − Pan）/ Zoom / 3.7795275590551180。</description></item>
/// </list>
/// </remarks>
public class EditorKernelTests
{
    // 96 / 25.4：与 Viewport.MmToBasePx 同值，但这里按算式重写一遍，避免用被测函数推期望值。
    const double MmPx = 96.0 / 25.4;

    // ---------------------------------------------------------------- 命中与选中

    [Fact]
    public void 按下命中元素即选中并进入移动状态()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));

        kernel.Selection.PrimaryId.Should().Be(rect.Id);
        kernel.State.Should().Be(InteractionState.Moving);
        kernel.ActiveHandle.Should().Be(ResizeHandle.None);
    }

    [Fact]
    public void 按下空白处清空选中且不进入任何拖拽()
    {
        var template = KernelFixture.Page();
        KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.Selection.IsEmpty.Should().BeFalse();

        // (100,100)mm 离那个盒子（x 10..30、y 10..25mm）很远
        kernel.OnPointerPressed(KernelFixture.At(kernel, 100, 100));

        kernel.Selection.IsEmpty.Should().BeTrue();
        kernel.State.Should().Be(InteractionState.Idle);
    }

    [Fact]
    public void 重叠元素命中最上层_也就是绘制序最后被画的那个()
    {
        // 引擎的绘制序是 Elements.OrderBy(ZIndex) 稳定排序（TemplateRenderer.Render），画得最晚的盖在上面。
        // 所以"点到的"必须是 ZIndex 大的那个，否则用户点的是被盖住的盒子、改的却是它下面那个。
        var template = KernelFixture.Page();
        var under = KernelFixture.AddRect(template, 10, 10, 40, 30, zIndex: 0);  // el-1，数组第 0 位
        var over = KernelFixture.AddRect(template, 20, 20, 40, 30, zIndex: 1);   // el-2，压在上面

        var kernel = KernelFixture.Kernel(template);
        kernel.OnPointerPressed(KernelFixture.At(kernel, 25, 25));   // 两个盒子的交叠区

        kernel.Selection.PrimaryId.Should().Be(over.Id, "交叠区里 ZIndex 大的绘制更晚，才是用户看见的那个");
        kernel.Selection.Contains(under.Id).Should().BeFalse();
    }

    [Fact]
    public void 同ZIndex时数组靠后的元素在上_命中跟着数组顺序走()
    {
        var template = KernelFixture.Page();
        var first = KernelFixture.AddRect(template, 10, 10, 40, 30);
        var second = KernelFixture.AddRect(template, 20, 20, 40, 30);
        var kernel = KernelFixture.Kernel(template);

        kernel.OnPointerPressed(KernelFixture.At(kernel, 25, 25));

        kernel.Selection.PrimaryId.Should().Be(second.Id, "同 ZIndex 时数组顺序就是绘制次序，稳定的 OrderBy 不会把它打乱");

        // 把下层抬高一层，命中必须立刻翻转——证明命中用的是绘制序而不是数组序。
        first.ZIndex = 5;
        kernel.OnPointerPressed(KernelFixture.At(kernel, 25, 25));
        kernel.Selection.PrimaryId.Should().Be(first.Id);
    }

    [Fact]
    public void 线段命中框按描边展宽到半毫米_发丝线才点得中()
    {
        var template = KernelFixture.Page();
        var line = KernelFixture.AddLine(template, LineDirection.Horizontal, 10, 20, 40, strokeWidthMm: 0.3);
        var kernel = KernelFixture.Kernel(template);

        // 渲染器把水平线画在 y = YMm = 20 这条轴上；命中带 = max(0.3, 0.5) = 0.5mm，
        // 以该轴为中心 → 可点区间 y ∈ [20 − 0.25, 20 + 0.25] = [19.75, 20.25]。
        kernel.OnPointerPressed(KernelFixture.At(kernel, 25, 20.2));
        kernel.Selection.PrimaryId.Should().Be(line.Id);

        kernel.OnPointerPressed(KernelFixture.At(kernel, 25, 20.4));
        kernel.Selection.IsEmpty.Should().BeTrue("0.4mm 距线轴 0.4mm > 半带宽 0.25mm，命中带不该无限制地胖下去");
    }

    [Fact]
    public void 手柄命中用固定设备像素半径_按在角上进入缩放()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        // 外接框 = (10,10)–(30,25)mm；右下角锚点 (30,25)mm → 设备 px (30×3.7795, 25×3.7795)。
        var corner = KernelFixture.At(kernel, 30, 25);
        kernel.OnPointerPressed(corner);

        kernel.State.Should().Be(InteractionState.Resizing);
        kernel.ActiveHandle.Should().Be(ResizeHandle.BottomRight);
        kernel.OnPointerReleased(corner);

        // 半径 6 设备 px：离锚点 5px 仍算握柄（半径不随 zoom 变，见 HandleHitRadiusHostPx 文档）。
        kernel.OnPointerPressed(KernelFixture.Px(corner.X - 5, corner.Y - 5));
        kernel.ActiveHandle.Should().Be(ResizeHandle.BottomRight);
        kernel.OnPointerReleased(KernelFixture.Px(corner.X - 5, corner.Y - 5));

        // 盒子正中（离角 20/15mm ≈ 75/57px）不是手柄，该走移动。
        kernel.OnPointerPressed(KernelFixture.Px(75.59055118110236, 56.69291338582677));
        kernel.State.Should().Be(InteractionState.Moving);
        kernel.ActiveHandle.Should().Be(ResizeHandle.None);
        kernel.OnPointerReleased(KernelFixture.Px(75.59055118110236, 56.69291338582677));
    }

    // ---------------------------------------------------------------- 移动：三种换算各一条

    [Fact]
    public void 拖拽位移在96dpi与zoom1下按每毫米3_7795px换算()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        // 按下点 mm(20,15) → 设备 px（dpi=96 时设备 px == 视口 px）：
        //   x = 20 × 3.7795275590551180 = 75.59055118110236
        //   y = 15 × 3.7795275590551180 = 56.69291338582677
        // 拖 +20 设备 px。闭合算式（由 ToMm 的定义推出）：Δmm = Δ设备px × 25.4 / (dpi × zoom)
        //   = 20 × 25.4 / (96 × 1) = 508/96 = 5.2916666666666667
        // 期望 X = 10 + 5.2916666666666667 = 15.2916666666666667
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(75.59055118110236, 56.69291338582677),
            KernelFixture.Px(95.59055118110236, 56.69291338582677));

        rect.XMm.Should().BeApproximately(10 + 20 * 25.4 / 96, 1e-9);
        rect.YMm.Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void 拖拽算出非有限点_停在最后一个有限位置且不发命令()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        var executed = new List<EditorCommand>();
        kernel.CommandExecuted += executed.Add;

        // 按下点正常（dpi 96 → mm(20,15) 落在元素里），移动事件的 DPI 是 NaN → ToMm 出来 NaN
        kernel.OnPointerPressed(KernelFixture.Px(75.59055118110236, 56.69291338582677));
        kernel.State.Should().Be(InteractionState.Moving);

        kernel.OnPointerMoved(KernelFixture.Px(95.59055118110236, 56.69291338582677, double.NaN));

        rect.XMm.Should().Be(10, "非有限的拖拽点不能写进模型——写进去就会被 JSON 持久化");
        rect.YMm.Should().Be(10);

        kernel.OnPointerReleased(KernelFixture.Px(75.59055118110236, 56.69291338582677));

        executed.Should().BeEmpty("被拒绝的移动没有改几何，就不该冒出一条移动命令");
        rect.XMm.Should().Be(10);
    }

    [Fact]
    public void 缩放2倍时同样的20设备像素只走一半毫米()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Viewport.Zoom = 2.0;

        // 按下 mm(20,15) → 设备 px = mm × 3.7795275590551180 × zoom（dpi=96、Pan=0）：
        //   x = 20 × 3.7795275590551180 × 2 = 151.18110236220472
        //   y = 15 × 3.7795275590551180 × 2 = 113.38582677165354
        // 拖 +20 设备 px → Δmm = 20 × 25.4 / (96 × 2) = 2.6458333333333333（同一闭合算式，zoom 进分母）
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(151.18110236220472, 113.38582677165354),
            KernelFixture.Px(171.18110236220472, 113.38582677165354));

        rect.XMm.Should().BeApproximately(10 + 20 * 25.4 / (96 * 2), 1e-9);
    }

    [Fact]
    public void dpi120下的拖拽位移必须按96比dpi折算_20设备像素等于4_2333毫米()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        // dpi=120、zoom=1、Pan=0。设备 px 先乘 96/120 落到视口 px，再除 3.7795275590551180：
        //   按下 x=100 设备px → 80 视口px → 80 / 3.7795275590551180 = 21.166666666666668mm
        //   按下 y=80  设备px → 64 视口px → 16.933333333333334mm（在盒子 y 10..25mm 内）
        //   终点 x=120 设备px → 96 视口px → 25.4mm
        //   Δmm = 25.4 − 21.166666666666668 = 4.233333333333332
        // 另一条独立算式核对：20 设备px × 25.4 / 120 = 4.233333333333333mm（dpi 直接换毫米）
        // 若换算漏掉 dpi（按 96 处理），Δmm 会是 20 × 25.4/96 = 5.2910mm —— 差 25%。
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(100, 80, KernelFixture.Dpi120),
            KernelFixture.Px(120, 80, KernelFixture.Dpi120));

        rect.XMm.Should().BeApproximately(10 + 20 * 25.4 / 120, 1e-9);
        rect.YMm.Should().BeApproximately(10, 1e-9, "y 方向没有位移，120dpi 不该在另一轴上引入误差");
    }

    [Fact]
    public void 多选移动同时改动全部选中元素()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 20, 10);
        var b = KernelFixture.AddRect(template, 40, 40, 20, 10);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Add(a.Id);
        kernel.Selection.Add(b.Id);

        // 按在 a 内部（mm(15,15) → 设备 px 15 × 3.7795275590551180 = 56.69291338582677）：
        // a 已在选中集合里，所以这次按下不能把 b 挤掉。
        kernel.OnPointerPressed(KernelFixture.Px(56.69291338582677, 56.69291338582677));
        kernel.Selection.Ids.Should().HaveCount(2);
        kernel.State.Should().Be(InteractionState.Moving);

        // 拖到 mm(25,15) → 设备 px = 25 × 3.7795275590551180 = 94.48818897637795，即横移 10mm
        kernel.OnPointerMoved(KernelFixture.Px(94.48818897637795, 56.69291338582677));
        kernel.OnPointerReleased(KernelFixture.Px(94.48818897637795, 56.69291338582677));

        a.XMm.Should().BeApproximately(20, 1e-9);
        b.XMm.Should().BeApproximately(50, 1e-9);
        b.YMm.Should().BeApproximately(40, 1e-9);
    }

    [Fact]
    public void 纯点击不产生命令()
    {
        var template = KernelFixture.Page();
        KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        var at = KernelFixture.At(kernel, 20, 15);
        KernelFixture.Drag(kernel, at, at);

        commands.Should().BeEmpty("按下即松开只改选中，不是编辑动作，不该往命令流里塞一条零位移的移动");
        kernel.Selection.IsEmpty.Should().BeFalse();
    }

    // ---------------------------------------------------------------- 缩放尺寸

    [Fact]
    public void 缩放下限1毫米_往回拖过头也不越过对边()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        // 右手柄锚点 (30, 17.5)mm → 设备 px (113.38582677165354, 66.14173228346456)
        // 拖到 mm(5, 17.5) → 设备 px (18.89763779527559, 66.14173228346456)
        // 未夹紧时宽 = 5 − 10 = −5mm；夹紧后 = 左边缘 + 1mm → 宽恒 1，左上角不动。
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(113.38582677165354, 66.14173228346456),
            KernelFixture.Px(18.89763779527559, 66.14173228346456));

        rect.WidthMm.Should().Be(1.0, "夹紧走的是 left + MinElementMm 这条分支，必须是恰好 1mm 而不是近似");
        rect.XMm.Should().Be(10, "拖的是右边，左边缘是锚点，不该跟着跑");
    }

    [Fact]
    public void 缩放下限1毫米_拖过对边时钉在对边减1毫米处()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        // 左手柄锚点 (10, 17.5)mm → 设备 px (37.795275590551180, 66.14173228346456)
        // 拖到 mm(35, 17.5) → 设备 px (132.28346456692913, 66.14173228346456)：越过右边缘 30mm
        // 夹紧后左边缘停在 30 − 1 = 29mm、宽 1mm（右边缘是锚点，恒在 30）。
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(37.795275590551180, 66.14173228346456),
            KernelFixture.Px(132.28346456692913, 66.14173228346456));

        rect.XMm.Should().Be(29.0);
        rect.WidthMm.Should().Be(1.0);
        rect.RightMm.Should().Be(30.0, "对边是锚点；越过对边之后右边缘必须还钉在 30mm，否则元素翻了面");
    }

    [Fact]
    public void Shift等比缩放按原始宽高比反算主导轴()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 10);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);
        kernel.ShiftPressed = true;

        // 原始比 = 20/10 = 2。右下角锚点 (30,20)mm → 设备 px (113.38582677165354, 75.59055118110236)
        // 拖到 mm(40,30) → 设备 px (151.18110236220472, 113.38582677165354)，即 Δ = (+10, +10)mm
        // 不加 Shift 会得到 30×20；等比时看相对变化：宽 (30/20−1)=0.5 < 高 (20/10−1)=1.0
        //   → 高当主导，宽 = 20 × 2 = 40mm；左上角是锚点，仍在 (10,10)。
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(113.38582677165354, 75.59055118110236),
            KernelFixture.Px(151.18110236220472, 113.38582677165354));

        rect.WidthMm.Should().BeApproximately(40, 1e-9);
        rect.HeightMm.Should().BeApproximately(20, 1e-9);
        rect.XMm.Should().BeApproximately(10, 1e-9);
        rect.YMm.Should().BeApproximately(10, 1e-9);
        (rect.WidthMm / rect.HeightMm).Should().BeApproximately(2.0, 1e-9, "等比这条纪律的全部意义就是宽高比不被拖拽破坏");
    }

    [Fact]
    public void 不按Shift时两条轴各自独立伸缩()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 10);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        // 与上一条同一段拖拽（Δ = +10mm,+10mm），但没有 Shift → 宽高各加 10：30×20
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(113.38582677165354, 75.59055118110236),
            KernelFixture.Px(151.18110236220472, 113.38582677165354));

        rect.WidthMm.Should().BeApproximately(30, 1e-9);
        rect.HeightMm.Should().BeApproximately(20, 1e-9);
    }

    [Fact]
    public void 垂直线只沿纵向改长度_宽度恒为零()
    {
        var template = KernelFixture.Page();
        var line = KernelFixture.AddLine(template, LineDirection.Vertical, 20, 30, 40);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(line.Id);

        // 零宽盒的三个"下柄"锚点重合在 (20,70)mm，角柄先判所以握到的是 BottomRight；
        // 纵向锁轴的效果与手柄名无关：只有下边缘参与，右边缘根本不参与缩放。
        // 下端 (20,70)mm 拖到 (20,80)mm → Δ = +10mm → 线长 50mm。
        KernelFixture.Drag(kernel, KernelFixture.At(kernel, 20, 70), KernelFixture.At(kernel, 20, 80));

        line.HeightMm.Should().BeApproximately(50, 1e-9);
        line.WidthMm.Should().Be(0, "垂直线的宽不是几何量，被拖宽就成了一条歪描边");
        line.XMm.Should().BeApproximately(20, 1e-9);
        line.YMm.Should().BeApproximately(30, 1e-9);
    }

    [Fact]
    public void 水平线只沿横向改长度_并且长度也夹到最小1毫米()
    {
        var template = KernelFixture.Page();
        var line = KernelFixture.AddLine(template, LineDirection.Horizontal, 20, 30, 50);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(line.Id);

        // 先正常伸长：右端 (70,30)mm 拖到 (80,30)mm，Δ = +10mm → 长 60mm
        KernelFixture.Drag(kernel, KernelFixture.At(kernel, 70, 30), KernelFixture.At(kernel, 80, 30));
        line.WidthMm.Should().BeApproximately(60, 1e-9);
        line.XMm.Should().BeApproximately(20, 1e-9, "拖的是右端，左端是锚点");

        // 再拖过头：右端 (80,30)mm 拖回 (20,30)mm，Δ = −60mm → 不夹紧会得到 60−60 = 0 以下
        KernelFixture.Drag(kernel, KernelFixture.At(kernel, 80, 30), KernelFixture.At(kernel, 20, 30));
        line.WidthMm.Should().Be(1.0, "线长同样受 MinElementMm 约束，夹紧分支给出恰好 1mm");
        line.HeightMm.Should().Be(0, "水平线的高不是几何量，纵向手柄/角柄都不许把它拉开");
    }

    // ---------------------------------------------------------------- 平移

    [Fact]
    public void 中键拖动只平移视口不动任何元素()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        kernel.OnPointerPressed(KernelFixture.Px(300, 300, KernelFixture.Dpi96, PointerButton.Middle));
        kernel.State.Should().Be(InteractionState.Panning);

        kernel.OnPointerMoved(KernelFixture.Px(320, 318, KernelFixture.Dpi96, PointerButton.Middle));
        kernel.OnPointerReleased(KernelFixture.Px(320, 318, KernelFixture.Dpi96, PointerButton.Middle));

        // Pan 活在 96 基准 px 层，dpi=96 时设备 px 与基准 px 一比一 → (+20, +18)
        kernel.Viewport.Pan.Should().Be(new Point2D(20, 18));
        kernel.State.Should().Be(InteractionState.Idle);
        rect.XMm.Should().Be(10);
        rect.YMm.Should().Be(10);
    }

    [Fact]
    public void 中键在空白处也能平移且位移按dpi折算()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);

        // dpi=120：设备 px 差 120 → 基准 px 差 = 120 × 96/120 = 96
        kernel.OnPointerPressed(KernelFixture.Px(100, 100, KernelFixture.Dpi120, PointerButton.Middle));
        kernel.OnPointerMoved(KernelFixture.Px(220, 100, KernelFixture.Dpi120, PointerButton.Middle));
        kernel.OnPointerReleased(KernelFixture.Px(220, 100, KernelFixture.Dpi120, PointerButton.Middle));

        kernel.Viewport.Pan.X.Should().BeApproximately(96, 1e-6);
        rect.XMm.Should().Be(10, "平移视口不是编辑动作，模型一个数都不该动");
        rect.WidthMm.Should().Be(20);
    }

    [Fact]
    public void 右键按下不改变任何状态()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        var at = KernelFixture.Px(75.59055118110236, 56.69291338582677, KernelFixture.Dpi96, PointerButton.Secondary);
        kernel.OnPointerPressed(at);
        kernel.OnPointerMoved(KernelFixture.Px(113.38582677165354, 94.48818897637795, KernelFixture.Dpi96, PointerButton.Secondary));
        kernel.OnPointerReleased(at);

        kernel.State.Should().Be(InteractionState.Idle);
        kernel.Selection.Ids.Should().Equal(rect.Id);
        rect.XMm.Should().Be(10);
        rect.YMm.Should().Be(10);
        kernel.Viewport.Pan.Should().Be(new Point2D(0, 0));
        commands.Should().BeEmpty();
    }

    [Fact]
    public void 拖拽被中键打断仍恰好提交一条移动命令且可撤销()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        // 主键按下 mm(20,15) 抓到快照 (10,10,20,15)，拖到 mm(40,15)：dx=20 → XMm 10→30、YMm 不变。
        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 40, 15, button: PointerButton.Primary));

        // 中键按下打断：必须先把在途的移动如实落地成一条命令，再切平移。
        kernel.OnPointerPressed(KernelFixture.At(kernel, 40, 15, button: PointerButton.Middle));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 60, 15, button: PointerButton.Middle));
        kernel.OnPointerReleased(KernelFixture.At(kernel, 60, 15, button: PointerButton.Middle));
        kernel.OnPointerReleased(KernelFixture.At(kernel, 40, 15, button: PointerButton.Primary));

        commands.Should().ContainSingle("被打断的移动也是一次真实改动，必须留下恰好一条可撤销命令");
        rect.XMm.Should().BeApproximately(30, 1e-6);
        rect.YMm.Should().BeApproximately(10, 1e-6);

        commands[0].Revert();
        rect.XMm.Should().BeApproximately(10, 1e-6, "撤销该命令应回到按下前的起点");
        rect.YMm.Should().BeApproximately(10, 1e-6);
    }

    [Fact]
    public void 第二次主键按下打断拖拽_前一段位移单独成一条命令()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        // 第一段：按下 mm(20,15)，拖到 mm(30,15)（dx=10 → XMm 10→20）。
        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 30, 15, button: PointerButton.Primary));

        // 第二段起手（主键再按在 mm(30,15)）：必须先把第一段 (10→20) 单独提交，再以当前几何重抓快照。
        kernel.OnPointerPressed(KernelFixture.At(kernel, 30, 15));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 40, 15, button: PointerButton.Primary));
        kernel.OnPointerReleased(KernelFixture.At(kernel, 40, 15, button: PointerButton.Primary));

        commands.Should().HaveCount(2, "两段位移各自可撤销，第一段不能被折进第二段基线");
        rect.XMm.Should().BeApproximately(30, 1e-6);

        commands[1].Revert();
        rect.XMm.Should().BeApproximately(20, 1e-6);
        commands[0].Revert();
        rect.XMm.Should().BeApproximately(10, 1e-6);
    }

    [Fact]
    public void 拖拽被中键打断提交后Esc只收起选中不回滚模型()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 40, 15, button: PointerButton.Primary));
        kernel.OnPointerPressed(KernelFixture.At(kernel, 40, 15, button: PointerButton.Middle));
        kernel.OnPointerReleased(KernelFixture.At(kernel, 40, 15, button: PointerButton.Middle));

        commands.Should().ContainSingle("中键打断即提交，Esc 之后这条命令仍应存在");
        kernel.Selection.Contains(rect.Id).Should().BeTrue();

        // Esc：空闲态下只收起选中，绝不能把已提交的移动悄悄还原。
        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));

        kernel.Selection.IsEmpty.Should().BeTrue();
        rect.XMm.Should().BeApproximately(30, 1e-6, "Esc 不是撤销，已提交的位移要留住");
        commands.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- 滚轮与键盘

    [Fact]
    public void 滚轮以光标为锚_光标底下的毫米数缩放前后不变()
    {
        var kernel = KernelFixture.Kernel();

        // 光标停在设备 (300,200)、dpi=96、zoom=1、Pan=0：
        //   mmX = 300 / 3.7795275590551180 = 79.37373737373737
        //   mmY = 200 / 3.7795275590551180 = 52.91666666666667
        var before = kernel.Viewport.ToMm(300, 200, KernelFixture.Dpi96);

        var views = 0;
        kernel.ViewChanged += () => views++;

        kernel.OnMouseWheel(120, KernelFixture.Px(300, 200)).Should().BeTrue("WinForms 传 ±120，只用符号就够");

        kernel.Viewport.Zoom.Should().Be(1.2, "一档 = ×ZoomStepFactor(1.2)");
        // Pan 反算：新 Pan = 锚点基准px − (锚点基准px − 旧Pan) × 新/旧 = 300 − 300 × 1.2 = −60
        kernel.Viewport.Pan.X.Should().BeApproximately(-60, 1e-6);

        var after = kernel.Viewport.ToMm(300, 200, KernelFixture.Dpi96);
        after.X.Should().BeApproximately(before.X, 1e-9);
        after.Y.Should().BeApproximately(before.Y, 1e-9);
        views.Should().Be(1);
    }

    [Fact]
    public void 滚轮向下缩小一档()
    {
        var kernel = KernelFixture.Kernel();
        kernel.OnMouseWheel(-1, KernelFixture.Px(400, 300)).Should().BeTrue();
        kernel.Viewport.Zoom.Should().BeApproximately(1.0 / 1.2, 1e-12);
    }

    [Fact]
    public void 已在最大缩放时向上滚返回false让宿主去滚页()
    {
        var kernel = KernelFixture.Kernel();
        kernel.Viewport.Zoom = Viewport.MaxZoom;
        var views = 0;
        kernel.ViewChanged += () => views++;

        kernel.OnMouseWheel(120, KernelFixture.Px(300, 200)).Should().BeFalse();

        kernel.Viewport.Zoom.Should().Be(Viewport.MaxZoom);
        views.Should().Be(0, "没改变视口就不该广播一次重绘，否则宿主每滚一格都白刷一遍");
    }

    [Fact]
    public void 已在最小缩放时向下滚返回false向上滚仍然可用()
    {
        var kernel = KernelFixture.Kernel();
        kernel.Viewport.Zoom = Viewport.MinZoom;

        kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200)).Should().BeFalse();
        kernel.OnMouseWheel(120, KernelFixture.Px(300, 200)).Should().BeTrue();
        kernel.Viewport.Zoom.Should().BeApproximately(Viewport.MinZoom * 1.2, 1e-12);
    }

    [Fact]
    public void 方向键微调三档_1毫米_10毫米_0_1毫米()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        // 无修饰 = NudgeStepMm(1)；Shift = 粗调 10mm；Ctrl = 微调 0.1mm。
        kernel.OnKeyDown(new KeyInfo(KeyAction.NudgeRight)).Should().BeTrue();
        rect.XMm.Should().Be(11.0);

        kernel.OnKeyDown(new KeyInfo(KeyAction.NudgeRight, Shift: true));
        rect.XMm.Should().Be(21.0);

        kernel.OnKeyDown(new KeyInfo(KeyAction.NudgeRight, Control: true));
        rect.XMm.Should().BeApproximately(21.1, 1e-12);

        kernel.OnKeyDown(new KeyInfo(KeyAction.NudgeUp));
        rect.YMm.Should().Be(9.0, "上 = y 减小（页面坐标 y 向下）");

        kernel.OnKeyDown(new KeyInfo(KeyAction.NudgeDown, Shift: true));
        rect.YMm.Should().Be(19.0);
    }

    [Fact]
    public void 键盘缩放三档各自落到视口上()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        kernel.Viewport.Zoom = 2.0;
        kernel.Viewport.Pan = new Point2D(50, 50);

        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        kernel.Viewport.Zoom.Should().Be(1.0);
        kernel.Viewport.Pan.Should().Be(new Point2D(0, 0));

        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomIn)).Should().BeTrue();
        kernel.Viewport.Zoom.Should().BeApproximately(1.2, 1e-12);

        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomOut)).Should().BeTrue();
        kernel.Viewport.Zoom.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void 整页适配按模板页高_连续纸走引擎的自适应判据()
    {
        var kernel = KernelFixture.Kernel();
        kernel.Viewport.Zoom = 3.0;

        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomFit)).Should().BeTrue();

        // 视口 800×600 设备px（dpi=96）→ 基准 px 800×600；四周留白取滚动条内容盒的留白 48 设备 px = 48 基准 px。
        // 页 210×297mm = 793.70078740157480 × 1122.5196850393701 基准 px
        // 可用 = (800−96) × (600−96) = 704 × 504 → 取小的那一边：504 / 1122.5196850393701
        kernel.Viewport.Zoom.Should().BeApproximately(504 / (297 * MmPx), 1e-9);

        // 连续纸：页高没有定值，用引擎同一判据（内容底边 + 2mm 余量），否则适配出来框不住纸。
        var receipt = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 80, HeightMm = 0 },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };
        KernelFixture.AddRect(receipt, 5, 40, 40, 30);   // 底边 40+30 = 70mm → 页高 70 + 2 = 72mm
        var receiptKernel = KernelFixture.Kernel(receipt);
        receiptKernel.OnKeyDown(new KeyInfo(KeyAction.ZoomFit));
        receiptKernel.Viewport.Zoom.Should().BeApproximately(Math.Min(704 / (80 * MmPx), 504 / (72 * MmPx)), 1e-9);
    }

    [Fact]
    public void 删除键删掉选中并收起选中()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        kernel.OnKeyDown(new KeyInfo(KeyAction.Delete)).Should().BeTrue();

        template.Elements.Should().BeEmpty();
        kernel.Selection.IsEmpty.Should().BeTrue();

        kernel.OnKeyDown(new KeyInfo(KeyAction.Delete)).Should().BeFalse("没有选中就没的可删，按键该交回宿主");
        kernel.OnKeyDown(new KeyInfo(KeyAction.None)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- Esc 取消

    [Fact]
    public void Esc在移动中途回到按下前的几何且不产生命令()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.OnPointerMoved(KernelFixture.At(kernel, 40, 25));
        rect.XMm.Should().BeApproximately(30, 1e-9, "拖拽中途必须实时改模型，画布才跟着重绘");
        rect.YMm.Should().BeApproximately(20, 1e-9);

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape)).Should().BeTrue();

        rect.XMm.Should().Be(10);
        rect.YMm.Should().Be(10);
        kernel.State.Should().Be(InteractionState.Idle);
        commands.Should().BeEmpty("拖拽从没提交过，回滚是取消而不是撤销，不该往命令流里塞东西");
    }

    [Fact]
    public void Esc在缩放中途回到按下前的尺寸()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);

        kernel.OnPointerPressed(KernelFixture.At(kernel, 30, 25));
        kernel.State.Should().Be(InteractionState.Resizing);
        kernel.OnPointerMoved(KernelFixture.At(kernel, 60, 45));
        rect.WidthMm.Should().BeApproximately(50, 1e-9);

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));

        rect.WidthMm.Should().Be(20);
        rect.HeightMm.Should().Be(15);
        kernel.ActiveHandle.Should().Be(ResizeHandle.None);
        kernel.State.Should().Be(InteractionState.Idle);
    }

    [Fact]
    public void Esc在没有拖拽时清空选中()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.OnPointerPressed(KernelFixture.At(kernel, 20, 15));
        kernel.OnPointerReleased(KernelFixture.At(kernel, 20, 15));
        kernel.State.Should().Be(InteractionState.Idle);

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));

        kernel.Selection.IsEmpty.Should().BeTrue();
        rect.XMm.Should().Be(10, "清空选中不是编辑");
    }

    [Fact]
    public void Esc在放置中途丢弃幽灵框且不入模板()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);

        kernel.BeginPlacing(ElementTypeKeys.QrCode, KernelFixture.At(kernel, 50, 50)).Should().BeTrue();
        kernel.State.Should().Be(InteractionState.Placing);
        kernel.PlacingGhostMm.Should().NotBeNull();

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape));

        template.Elements.Should().BeEmpty();
        kernel.PlacingGhostMm.Should().BeNull();
        kernel.State.Should().Be(InteractionState.Idle);
    }

    // ---------------------------------------------------------------- 命令与 Revert

    [Fact]
    public void 移动命令广播并可用Revert还原位移()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        var templateChanged = 0;
        kernel.CommandExecuted += commands.Add;
        kernel.TemplateChanged += () => templateChanged++;

        // 拖 +10mm（Δ设备 px = 10 × 3.7795275590551180 = 37.79527559055118）
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(75.59055118110236, 56.69291338582677),
            KernelFixture.Px(113.38582677165354, 56.69291338582677));

        rect.XMm.Should().BeApproximately(20, 1e-9);
        commands.Should().ContainSingle("一次拖拽只出一条命令");
        templateChanged.Should().Be(1);

        commands[0].Revert();
        rect.XMm.Should().BeApproximately(10, 1e-9);

        commands[0].Apply();
        rect.XMm.Should().BeApproximately(20, 1e-9, "P1 的撤销栈要重放命令，Apply 必须能从 Revert 之后的状态再走一遍");
    }

    [Fact]
    public void 缩放命令Revert还原宽高()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 10, 10, 20, 15);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Select(rect.Id);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        // 右下角 (30,25)mm → (40,35)mm：宽高各 +10mm
        KernelFixture.Drag(
            kernel,
            KernelFixture.Px(113.38582677165354, 94.48818897637795),
            KernelFixture.Px(151.18110236220472, 132.28346456692913));

        rect.WidthMm.Should().BeApproximately(30, 1e-9);
        rect.HeightMm.Should().BeApproximately(25, 1e-9);
        commands.Should().ContainSingle();
        commands[0].Label.Should().Contain("缩放");

        commands[0].Revert();
        rect.WidthMm.Should().Be(20);
        rect.HeightMm.Should().Be(15);
    }

    [Fact]
    public void 删除命令Revert把元素插回原索引并保持同id同zIndex()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10);            // el-1，索引 0
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10, zIndex: 7);  // el-2，索引 1
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10);            // el-3，索引 2
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.Selection.Select(b.Id);
        kernel.DeleteSelection();

        template.Elements.Should().Equal(a, c);
        commands.Should().ContainSingle();

        commands[0].Revert();

        // 断"索引"而不是断"数量"：插到末尾也能凑回 3 个元素，但同 ZIndex 的绘制次序就错了。
        template.Elements.Should().HaveCount(3);
        template.Elements.IndexOf(b).Should().Be(1, "b 原本夹在 a 与 c 中间，插回末尾会让它在同 ZIndex 下画到最后");
        template.Elements.Should().Equal(a, b, c);
        b.Id.Should().Be("el-2");
        b.ZIndex.Should().Be(7, "撤销删除不该顺手改层次");
    }

    [Fact]
    public void 删除多个元素后Revert逐个回到原位()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10);
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10);
        var d = KernelFixture.AddRect(template, 70, 70, 10, 10);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.Selection.Add(b.Id);
        kernel.Selection.Add(c.Id);
        kernel.DeleteSelection();
        template.Elements.Should().Equal(a, d);

        commands[0].Revert();
        template.Elements.Should().Equal(a, b, c, d);
        kernel.Selection.Ids.Should().Contain(b.Id).And.Contain(c.Id);
    }

    [Fact]
    public void 放置命令Revert移除元素并收起选中()
    {
        var kernel = KernelFixture.Kernel();
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.AddElementAtCenter(ElementTypeKeys.Rectangle);

        commands.Should().ContainSingle();
        commands[0].Label.Should().Contain("放置");
        kernel.Selection.IsEmpty.Should().BeFalse();

        commands[0].Revert();
        kernel.Template.Elements.Should().BeEmpty();
        kernel.Selection.IsEmpty.Should().BeTrue();

        commands[0].Apply();
        kernel.Template.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void 层次命令Revert还原ZIndex与数组顺序()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10, zIndex: 5);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.Selection.Select(a.Id);
        kernel.BringToFront();
        a.ZIndex.Should().Be(6, "全表 max(0,5) + 1");

        commands[0].Revert();
        a.ZIndex.Should().Be(0);

        // 同 ZIndex 的那条分支改的是数组位置，撤销同样要还原位置。
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10, zIndex: 6);
        var before = template.Elements.ToList();
        kernel.Selection.Select(b.Id);   // b 的 ZIndex=5，c=6 在它上面
        kernel.BringForward();
        b.ZIndex.Should().Be(6);
        c.ZIndex.Should().Be(5);
        commands[^1].Revert();
        b.ZIndex.Should().Be(5);
        c.ZIndex.Should().Be(6);
        template.Elements.Should().Equal(before);
    }

    [Fact]
    public void 同ZIndex上移改的是数组位置而不是号()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10);
        var kernel = KernelFixture.Kernel(template);

        kernel.Selection.Select(a.Id);
        kernel.BringForward();

        a.ZIndex.Should().Be(0);
        b.ZIndex.Should().Be(0);
        template.Elements.Should().Equal(new PaperElement[] { b, a }, "两个号一样时换号等于没换，只能交换数组位置");

        kernel.SendBackward();
        template.Elements.Should().Equal(new PaperElement[] { a, b }, "a 已经在最上面，上移是空操作；下移才把它换回数组首位");
    }

    // ---------------------------------------------------------------- zIndex 四操作

    [Fact]
    public void 置顶与置底取全表最大加1与最小减1()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10, zIndex: 0);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10, zIndex: 5);
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10, zIndex: 10);
        var kernel = KernelFixture.Kernel(template);

        kernel.Selection.Select(a.Id);
        kernel.BringToFront();
        a.ZIndex.Should().Be(11, "max(0,5,10) + 1");

        kernel.Selection.Select(c.Id);
        kernel.SendToBack();
        b.ZIndex.Should().Be(5);
        c.ZIndex.Should().Be(4, "此刻表内 ZIndex 是 a=11、b=5、c=10 → min = 5 → 5 − 1 = 4");
    }

    [Fact]
    public void 上移下移各一层与相邻非选中元素交换层次()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10, zIndex: 0);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10, zIndex: 5);
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10, zIndex: 10);
        var kernel = KernelFixture.Kernel(template);

        kernel.Selection.Select(a.Id);
        kernel.BringForward();
        a.ZIndex.Should().Be(5);
        b.ZIndex.Should().Be(0, "换的是号，不是把 a 直接顶到顶层");

        kernel.Selection.Select(c.Id);
        kernel.SendBackward();
        c.ZIndex.Should().Be(5);
        a.ZIndex.Should().Be(10, "此刻绘制序是 b(0) → a(5) → c(10)，c 的下一个下方邻居是 a");
        b.ZIndex.Should().Be(0);
    }

    [Fact]
    public void 上移对已在顶层的元素是空操作且不发命令()
    {
        var template = KernelFixture.Page();
        var top = KernelFixture.AddRect(template, 10, 10, 10, 10, zIndex: 9);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.Selection.Select(top.Id);
        kernel.BringForward();
        top.ZIndex.Should().Be(9);
        commands.Should().BeEmpty("无事发生的操作不该产出一条空命令，否则撤销栈会被垃圾填满");

        kernel.BringToFront();
        commands.Should().ContainSingle();
        top.ZIndex.Should().Be(10);
    }

    [Fact]
    public void 多选层次操作保持选中元素彼此次序()
    {
        var template = KernelFixture.Page();
        var a = KernelFixture.AddRect(template, 10, 10, 10, 10, zIndex: 0);
        var b = KernelFixture.AddRect(template, 30, 30, 10, 10, zIndex: 1);
        var c = KernelFixture.AddRect(template, 50, 50, 10, 10, zIndex: 2);
        var d = KernelFixture.AddRect(template, 70, 70, 10, 10, zIndex: 3);
        var kernel = KernelFixture.Kernel(template);
        kernel.Selection.Add(a.Id);
        kernel.Selection.Add(b.Id);

        kernel.BringToFront();

        // 全表 max = 3 → a 拿 4、b 拿 5（按当前绘制序逐个 +1），a 仍在 b 下面。
        a.ZIndex.Should().Be(4);
        b.ZIndex.Should().Be(5);
        a.ZIndex.Should().BeLessThan(b.ZIndex);
        c.ZIndex.Should().Be(2);
        d.ZIndex.Should().Be(3);
    }

    // ---------------------------------------------------------------- 放置

    [Fact]
    public void 八个类型都能居中放置且落在页面内()
    {
        var kernel = KernelFixture.Kernel();

        foreach (var key in ElementTypeKeys.All)
            kernel.AddElementAtCenter(key);

        kernel.Template.Elements.Should().HaveCount(8);

        // 可见页面区域（mm）：
        //   可见右 = 800 / 3.7795275590551180 = 211.66666666666666 > 页宽 210 → 取 210
        //   可见底 = 600 / 3.7795275590551180 = 158.75 < 页高 297 → 取 158.75
        //   中心 = (105, 79.375)
        // 最宽/最高的默认盒是表格 60×30 → x 75..135、y 64.375..94.375，全在页内。
        foreach (var element in kernel.Template.Elements)
        {
            element.XMm.Should().BeGreaterThanOrEqualTo(0, $"{element.TypeKey} 不能放到纸外");
            element.YMm.Should().BeGreaterThanOrEqualTo(0);
            element.RightMm.Should().BeLessThanOrEqualTo(KernelFixture.PageWidthMm);
            element.BottomMm.Should().BeLessThanOrEqualTo(KernelFixture.PageHeightMm);
            element.WidthMm.Should().BeGreaterThan(0);
        }

        var table = kernel.Template.Elements.Single(e => e.TypeKey == ElementTypeKeys.Table);
        (table.XMm + table.WidthMm / 2).Should().BeApproximately(105, 1e-9);
        (table.YMm + table.HeightMm / 2).Should().BeApproximately(79.375, 1e-9);

        var qr = kernel.Template.Elements.Single(e => e.TypeKey == ElementTypeKeys.QrCode);
        qr.WidthMm.Should().Be(30);
        qr.HeightMm.Should().Be(30);
        qr.XMm.Should().BeApproximately(90, 1e-9, "105 − 30/2");
        qr.YMm.Should().BeApproximately(64.375, 1e-9, "79.375 − 30/2");

        var line = kernel.Template.Elements.Single(e => e.TypeKey == ElementTypeKeys.Line);
        line.WidthMm.Should().Be(50, "线的默认长就是默认宽，横向");
        line.HeightMm.Should().Be(0, "线的高不是几何量，渲染器沿 YMm 那条轴画");
    }

    [Fact]
    public void 放置的id逐个递增且不重复_删除最大号后也不回收()
    {
        var kernel = KernelFixture.Kernel();
        foreach (var key in ElementTypeKeys.All)
            kernel.AddElementAtCenter(key);

        var ids = kernel.Template.Elements.Select(e => e.Id).ToList();
        ids.Should().OnlyHaveUniqueItems("同一模板里 id 重复会让 JSON 往返与宿主属性面板互相打架");
        ids.Should().Equal("el-1", "el-2", "el-3", "el-4", "el-5", "el-6", "el-7", "el-8");

        kernel.Selection.Select(ids[^1]);
        kernel.DeleteSelection();
        kernel.AddElementAtCenter(ElementTypeKeys.Text);

        kernel.Template.Elements.Should().HaveCount(8);
        kernel.Template.Elements.Select(e => e.Id).Should().NotContain("el-8");
        kernel.Template.Elements.Last().Id.Should().Be("el-9");
    }

    [Fact]
    public void 工具箱拖入落在松手点_拖拽期间幽灵框跟手()
    {
        var template = KernelFixture.Page();
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        kernel.CommandExecuted += commands.Add;

        kernel.BeginPlacing(ElementTypeKeys.QrCode, KernelFixture.At(kernel, 50, 50)).Should().BeTrue();

        // 默认 30×30，中心 (50,50) → 幽灵框左上 (35,35)、右下 (65,65)
        var ghost = kernel.PlacingGhostMm!.Value;
        ((double)ghost.Left).Should().BeApproximately(35, 1e-6);
        ((double)ghost.Top).Should().BeApproximately(35, 1e-6);
        ((double)ghost.Right).Should().BeApproximately(65, 1e-6);
        ((double)ghost.Bottom).Should().BeApproximately(65, 1e-6);
        template.Elements.Should().BeEmpty("放置途中只是预览，元素还没进模板");

        kernel.OnPointerMoved(KernelFixture.At(kernel, 60, 70));
        kernel.State.Should().Be(InteractionState.Placing);

        kernel.OnPointerReleased(KernelFixture.At(kernel, 60, 70));

        kernel.State.Should().Be(InteractionState.Idle);
        kernel.PlacingGhostMm.Should().BeNull();
        template.Elements.Should().ContainSingle();
        var placed = template.Elements[0];
        placed.XMm.Should().BeApproximately(45, 1e-9, "60 − 30/2：落在松手点而不是按下点");
        placed.YMm.Should().BeApproximately(55, 1e-9);
        kernel.Selection.PrimaryId.Should().Be(placed.Id);
        commands.Should().ContainSingle();

        commands[0].Revert();
        template.Elements.Should().BeEmpty();
    }

    [Fact]
    public void 未知类型键放置失败且不改变状态()
    {
        var kernel = KernelFixture.Kernel();

        kernel.BeginPlacing("mystery", KernelFixture.Px(100, 100)).Should().BeFalse();
        kernel.AddElementAtCenter("mystery");

        kernel.State.Should().Be(InteractionState.Idle);
        kernel.PlacingGhostMm.Should().BeNull();
        kernel.Template.Elements.Should().BeEmpty();
    }

    [Fact]
    public void 属性写回也走命令通道且可撤销()
    {
        var template = KernelFixture.Page();
        var text = new TextElement { XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 8 };
        template.Add(text);
        var kernel = KernelFixture.Kernel(template);
        var commands = new List<EditorCommand>();
        var changed = 0;
        kernel.CommandExecuted += commands.Add;
        kernel.TemplateChanged += () => changed++;

        var before = text.Text.FontSizePt;
        kernel.CommitPropertyChange("修改 字号",
            () => text.Text.FontSizePt = 18,
            () => text.Text.FontSizePt = before);

        text.Text.FontSizePt.Should().Be(18);
        changed.Should().Be(1);
        commands[0].Label.Should().Be("修改 字号");

        commands[0].Revert();
        text.Text.FontSizePt.Should().Be(before);
    }

    [Fact]
    public void 放置后的元素立刻点得中()
    {
        // 放置没落位、命中还按旧坐标，是设计器最常见的"拖了个寂寞"。
        var kernel = KernelFixture.Kernel();
        kernel.AddElementAtCenter(ElementTypeKeys.Rectangle);
        var placed = kernel.Template.Elements[0];

        // 矩形默认 40×20，中心 (105, 79.375) → 中心点必然在盒内
        kernel.OnPointerPressed(KernelFixture.At(kernel, placed.XMm + placed.WidthMm / 2, placed.YMm + placed.HeightMm / 2));

        kernel.Selection.PrimaryId.Should().Be(placed.Id);
        kernel.State.Should().Be(InteractionState.Moving);
    }

    [Fact]
    public void 后放置的元素拿到最大ZIndex加一_压住先放的元素()
    {
        var template = KernelFixture.Page();
        // 先放的元素被抬高到 5：若新元素还是 ZIndex=0，渲染按 ZIndex 升序画它会一直被压在底下。
        var first = KernelFixture.AddRect(template, 10, 10, 20, 15, zIndex: 5);
        var kernel = KernelFixture.Kernel(template);

        kernel.AddElementAtCenter(ElementTypeKeys.Rectangle);
        var placed = kernel.Template.Elements.Single(e => e.Id != first.Id);

        placed.ZIndex.Should().Be(6, "渲染按 ZIndex 升序稳定排序，新元素必须排到现存最大值之上，否则'后放的反而看不见'");
        kernel.Selection.PrimaryId.Should().Be(placed.Id, "放置完就该选中它，用户马上要调它");
    }
}
