using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 滚动条：叠加层（<see cref="EditorRenderer"/> 画轨道/滑块）与内核（命中/拖拽）共用
/// <see cref="EditorScrollBars"/> 一份几何算法。几何用算式断言；交互走指针事件状态机。
/// </summary>
/// <remarks>
/// 换算锚点（96dpi、zoom=1、Pan=0，页框左上角落在宿主 (0,0)）：
/// <list type="bullet">
/// <item><description>页宽 = 210 × 96/25.4 = 793.70px，页高 = 297 × 96/25.4 = 1122.52px。</description></item>
/// <item><description>内容盒 = 页框外扩 48px 留白 → 889.70 × 1218.52，对 800×600 的视口双轴溢出。</description></item>
/// <item><description>滚动位置 s 与平移的关系：<c>纸张顶边 = 48 − s</c>（见 <see cref="EditorScrollBars"/> remarks）。</description></item>
/// </list>
/// </remarks>
public class ScrollBarTests
{
    const double MmPx = 96.0 / 25.4;

    // ---------------------------------------------------------------- 几何

    [Fact]
    public void A4在800乘600视口里双轴溢出_几何符合算式()
    {
        var kernel = KernelFixture.Kernel();
        var metrics = EditorScrollBars.Compute(kernel);

        // 内容盒 = 页框外扩 48px：宽 793.70 + 96 = 889.70 > 800，高 1122.52 + 96 = 1218.52 > 600。
        // 页框走 SKRect（float 存储），期望值也按 float 精度取，否则 1e-9 的容差会被舍位差打穿。
        var contentW = (double)(float)(210 * MmPx) + 2 * EditorScrollBars.ContentPaddingHostPx;
        var contentH = (double)(float)(297 * MmPx) + 2 * EditorScrollBars.ContentPaddingHostPx;

        metrics.Horizontal.Should().NotBeNull();
        metrics.Vertical.Should().NotBeNull();

        var h = metrics.Horizontal!.Value;
        var v = metrics.Vertical!.Value;

        // Pan=0：纸张左/顶边贴视口左/上边，滚动位置 = 视口边到内容盒边的留白 48px
        h.ScrollPos.Should().BeApproximately(EditorScrollBars.ContentPaddingHostPx, 1e-9);
        v.ScrollPos.Should().BeApproximately(EditorScrollBars.ContentPaddingHostPx, 1e-9);
        h.ScrollMax.Should().BeApproximately(contentW - KernelFixture.ViewWidthPx, 1e-9);
        v.ScrollMax.Should().BeApproximately(contentH - KernelFixture.ViewHeightPx, 1e-9);

        // 两轴都在：轨道互相让出对方厚度（水平 800−14=786 贴底边，垂直 600−14=586 贴右缘）
        h.Track.Should().Be(SKRect.Create(0, 586, 786, 14));
        v.Track.Should().Be(SKRect.Create(786, 0, 14, 586));

        // 滑块长 = max(24, 轨道长 × 视口 / 内容)
        var thumbW = Math.Max(EditorScrollBars.MinThumbLengthHostPx, 786 * KernelFixture.ViewWidthPx / contentW);
        var thumbH = Math.Max(EditorScrollBars.MinThumbLengthHostPx, 586 * KernelFixture.ViewHeightPx / contentH);
        ((double)h.Thumb.Width).Should().BeApproximately(thumbW, 1e-2);
        ((double)v.Thumb.Height).Should().BeApproximately(thumbH, 1e-2);

        // 滑块位置 = 滚动位置 / 滚动范围 × 可滑行程
        h.Thumb.Left.Should().BeApproximately((float)(h.ScrollPos / h.ScrollMax * (786 - thumbW)), 1e-2f);
        v.Thumb.Top.Should().BeApproximately((float)(v.ScrollPos / v.ScrollMax * (586 - thumbH)), 1e-2f);

        // 滑块永远在轨道里（画出去 = 点不到 = 滚动条失效）
        h.Thumb.Left.Should().BeGreaterThanOrEqualTo(h.Track.Left);
        h.Thumb.Right.Should().BeLessThanOrEqualTo(h.Track.Right);
        v.Thumb.Top.Should().BeGreaterThanOrEqualTo(v.Track.Top);
        v.Thumb.Bottom.Should().BeLessThanOrEqualTo(v.Track.Bottom);
    }

    [Fact]
    public void 内容不超出视口时两轴都没有滚动条()
    {
        // 100×80mm 小票：内容 473.95 × 398.36，远小于 800×600
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 100, HeightMm = 80 },
        };
        var kernel = KernelFixture.Kernel(template);

        var metrics = EditorScrollBars.Compute(kernel);

        metrics.Horizontal.Should().BeNull("空滚动条只会误导用户以为还能滚");
        metrics.Vertical.Should().BeNull();
    }

    [Fact]
    public void 只有一轴溢出时只有那根轴有滚动条()
    {
        // 260×80mm：内容宽 260×3.7795…+96 = 1078.68 > 800 溢出；高 398.36 < 600 不溢出
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 260, HeightMm = 80 },
        };
        var kernel = KernelFixture.Kernel(template);

        var metrics = EditorScrollBars.Compute(kernel);

        metrics.Vertical.Should().BeNull();
        var h = metrics.Horizontal!.Value;
        // 垂直轴没出现 → 水平轨道占满整个视口宽，不用给右下角让位
        h.Track.Should().Be(SKRect.Create(0, 586, 800, 14));
    }

    [Fact]
    public void 内容特别长时滑块缩到最小长度为止()
    {
        var kernel = KernelFixture.Kernel();
        kernel.ViewWidthHostPx = 100;
        kernel.ViewHeightHostPx = 100;

        var metrics = EditorScrollBars.Compute(kernel);

        // 内容 889.70 × 1218.52 对 100×100 的视口：轨道都只剩 86px，
        // 自然滑块 86×100/889.70 = 9.67px、86×100/1218.52 = 7.06px，都小于 24 → 被最小长度托底
        ((double)metrics.Horizontal!.Value.Thumb.Width).Should().Be(EditorScrollBars.MinThumbLengthHostPx);
        ((double)metrics.Vertical!.Value.Thumb.Height).Should().Be(EditorScrollBars.MinThumbLengthHostPx);
    }

    // ---------------------------------------------------------------- 交互

    [Fact]
    public void 拖垂直滑块按压缩比滚动_松手回到空闲()
    {
        var kernel = KernelFixture.Kernel();

        // 垂直轴几何：scrollMax = 1218.52 − 600 = 618.52；thumb = 586×600/1218.52 = 288.55；
        //   usable = 586 − 288.55 = 297.45；offset = 48/618.52 × 297.45 = 23.08 → 滑块占 y 23.08..311.63
        var contentH = 297 * MmPx + 2 * EditorScrollBars.ContentPaddingHostPx;
        var scrollMax = contentH - KernelFixture.ViewHeightPx;
        var trackLength = KernelFixture.ViewHeightPx - EditorScrollBars.DefaultThicknessHostPx;
        var thumbLength = trackLength * KernelFixture.ViewHeightPx / contentH;
        var usable = trackLength - thumbLength;
        var offset = EditorScrollBars.ContentPaddingHostPx / scrollMax * usable;
        var thumbCenterY = offset + thumbLength / 2;

        // 垂直轨道贴右缘（x 786..800），按在滑块中点
        kernel.OnPointerPressed(KernelFixture.Px(793, thumbCenterY));
        kernel.State.Should().Be(InteractionState.Scrolling);

        // 下拖 100 设备 px → 滚动量 = 100 × (scrollMax/usable) = 100 × 618.52/297.45 = 207.94
        //   Pan.Y = 48 − (48 + 207.94) = −207.94：滑块 1px 压缩了整个滚动范围
        kernel.OnPointerMoved(KernelFixture.Px(793, thumbCenterY + 100));
        ((double)kernel.Viewport.Pan.Y).Should().BeApproximately(-100 * scrollMax / usable, 1e-3);
        kernel.Viewport.Pan.X.Should().Be(0, "拖垂直滑块不该动另一根轴");

        kernel.OnPointerReleased(KernelFixture.Px(793, thumbCenterY + 100));
        kernel.State.Should().Be(InteractionState.Idle, "拖滑块不发命令，松手即结束");
    }

    [Fact]
    public void 点击轨道空白朝那一侧整页翻滚()
    {
        var kernel = KernelFixture.Kernel();

        // 滑块占 y 23.08..311.63：按 y=500 在滑块下方 → 朝下翻一屏 = 0.9×600 = 540
        kernel.OnPointerPressed(KernelFixture.Px(793, 500));

        kernel.State.Should().Be(InteractionState.Idle, "轨道点击是瞬时翻页，不进入拖拽状态");
        // Pan.Y = 48 − (48 + 540) = −540
        ((double)kernel.Viewport.Pan.Y).Should().BeApproximately(-KernelFixture.ViewHeightPx * 0.9, 1e-3);
    }

    [Fact]
    public void Esc取消滑块拖拽_视口还原到按下前()
    {
        var kernel = KernelFixture.Kernel();

        var contentH = 297 * MmPx + 2 * EditorScrollBars.ContentPaddingHostPx;
        var scrollMax = contentH - KernelFixture.ViewHeightPx;
        var trackLength = KernelFixture.ViewHeightPx - EditorScrollBars.DefaultThicknessHostPx;
        var thumbLength = trackLength * KernelFixture.ViewHeightPx / contentH;
        var offset = EditorScrollBars.ContentPaddingHostPx / scrollMax * (trackLength - thumbLength);

        kernel.OnPointerPressed(KernelFixture.Px(793, offset + thumbLength / 2));
        kernel.OnPointerMoved(KernelFixture.Px(793, offset + thumbLength / 2 + 100));
        kernel.Viewport.Pan.Y.Should().BeLessThan(0, "前置条件：拖拽确实滚动过视口");

        kernel.OnKeyDown(new KeyInfo(KeyAction.Escape)).Should().BeTrue();

        kernel.State.Should().Be(InteractionState.Idle);
        kernel.Viewport.Pan.X.Should().Be(0);
        kernel.Viewport.Pan.Y.Should().Be(0, "Esc 是取消不是撤销，视口回到按下那一刻的快照");
    }

    [Fact]
    public void 内容不溢出时右缘按下不被拦截()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 100, HeightMm = 80 },
        };
        var kernel = KernelFixture.Kernel(template);

        // 没有滚动条，"右缘窄条该在的位置"按下必须走常规命中（空白 → 清空选中），不能进 Scrolling
        kernel.OnPointerPressed(KernelFixture.Px(793, 500));

        kernel.State.Should().Be(InteractionState.Idle);
        kernel.Selection.IsEmpty.Should().BeTrue();
    }

    // ---------------------------------------------------------------- 厚度：画的多粗就按多粗命中

    [Fact]
    public void 宿主加粗滚动条时按画出来的滑块按下即拖拽()
    {
        var template = KernelFixture.Page();
        KernelFixture.AddRect(template, 100, 10, 110, 100);   // 右缘 210mm = 793.7 设备 px，压在右缘窄条下

        var kernel = KernelFixture.Kernel(template);
        var overlay = new EditorRenderer { Options = { ScrollBarThicknessHostPx = 20 } };

        using var bitmap = Surface.White(800, 600);
        using var canvas = new SKCanvas(bitmap);
        EditorCanvas.Draw(canvas, kernel, overlay, 800, 600, KernelFixture.Dpi96);

        // 画出来的滑块贴右缘 20px（x 780..800）：(782,150) 是看得见的滑块本体
        Surface.Pixel(bitmap, 782, 150).Red.Should().Be(0x9B, "前置条件：滑块确实画到了 782 这一列");

        kernel.OnPointerPressed(KernelFixture.Px(782, 150));

        kernel.State.Should().Be(InteractionState.Scrolling, "叠加层画 20px 粗，命中就得按 20px 判——看得见的滑块抓不住是硬伤");
    }

    [Fact]
    public void 宿主画细滚动条时细条外仍归元素()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 100, 10, 110, 100);

        var kernel = KernelFixture.Kernel(template);
        var overlay = new EditorRenderer { Options = { ScrollBarThicknessHostPx = 8 } };

        using var bitmap = Surface.White(800, 600);
        using var canvas = new SKCanvas(bitmap);
        EditorCanvas.Draw(canvas, kernel, overlay, 800, 600, KernelFixture.Dpi96);

        // 画出来的窄条只有 x 792..800；789 在它外面 → 该命中底下的元素，而不是被看不见的 14px 条抢走
        kernel.OnPointerPressed(KernelFixture.Px(789, 150));

        kernel.State.Should().Be(InteractionState.Moving, "窄条外那几像素归元素");
        kernel.Selection.Contains(rect.Id).Should().BeTrue();
    }

    [Fact]
    public void 默认厚度下画与命中本来就是同一条窄条()
    {
        var template = KernelFixture.Page();
        var rect = KernelFixture.AddRect(template, 100, 10, 110, 100);
        var kernel = KernelFixture.Kernel(template);

        using var bitmap = Surface.White(800, 600);
        using var canvas = new SKCanvas(bitmap);
        EditorCanvas.Draw(canvas, kernel, new EditorRenderer(), 800, 600, KernelFixture.Dpi96);

        // 默认 14px：窄条 x 786..800 抓得住，779 在条外归元素——改厚度之前的正确行为必须原样保住
        kernel.OnPointerPressed(KernelFixture.Px(793, 150));
        kernel.State.Should().Be(InteractionState.Scrolling);

        kernel.OnPointerReleased(KernelFixture.Px(793, 150));
        kernel.OnPointerPressed(KernelFixture.Px(779, 150));
        kernel.State.Should().Be(InteractionState.Moving);
        kernel.Selection.Contains(rect.Id).Should().BeTrue();
    }

    // ---------------------------------------------------------------- 滚轮：Ctrl 缩放 / 普通=纵向 / Shift=横向

    [Fact]
    public void Ctrl滚轮以光标为锚缩放_普通滚轮不再缩放()
    {
        var kernel = KernelFixture.Kernel();

        // 锚点 (300,200) 缩放 1.2 档不动：新 Pan = 200 − 200×1.2 = −40
        kernel.OnMouseWheel(120, KernelFixture.Px(300, 200), control: true, shift: false).Should().BeTrue();
        kernel.Viewport.Zoom.Should().Be(1.2);
        kernel.Viewport.Pan.Y.Should().BeApproximately(-40, 1e-6);
    }

    [Fact]
    public void 普通滚轮纵向滚动_一步四分之一视口_滚到头为止()
    {
        var kernel = KernelFixture.Kernel();

        // 下滚一步：滚动位置 48 + 0.25×600 = 198 → Pan.Y = 48 − 198 = −150
        kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200), control: false, shift: false).Should().BeTrue();
        ((double)kernel.Viewport.Pan.Y).Should().BeApproximately(-150, 1e-3);
        kernel.Viewport.Zoom.Should().Be(1, "普通滚轮不再缩放");

        // 连滚到头（scrollMax = 1122.52 + 96 − 600 = 618.52）后不再消费：Pan.Y 停在 48 − 618.52
        while (kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200), control: false, shift: false)) { }
        ((double)kernel.Viewport.Pan.Y).Should().BeApproximately(48 - (297 * MmPx + 2 * EditorScrollBars.ContentPaddingHostPx - 600), 1e-2);

        // 到头再滚返回 false，宿主可以拿去干别的
        kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200), control: false, shift: false).Should().BeFalse();
    }

    [Fact]
    public void Shift滚轮横向滚动_不动纵向()
    {
        var kernel = KernelFixture.Kernel();

        // 水平轴：scrollMax = 793.70 + 96 − 800 = 89.70；一步 = 0.25×800 = 200 → 目标 248 截到 89.70
        //   Pan.X = 48 − 89.70 = −41.70
        kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200), control: false, shift: true).Should().BeTrue();
        ((double)kernel.Viewport.Pan.X).Should().BeApproximately(48 - (210 * MmPx + 2 * EditorScrollBars.ContentPaddingHostPx - 800), 1e-2);
        ((double)kernel.Viewport.Pan.Y).Should().Be(0, "横向滚动不动纵向");
    }

    [Fact]
    public void 内容不溢出时普通滚轮不消费()
    {
        var template = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = 100, HeightMm = 80 },
        };
        var kernel = KernelFixture.Kernel(template);

        kernel.OnMouseWheel(-120, KernelFixture.Px(300, 200), control: false, shift: false).Should().BeFalse();
        ((double)kernel.Viewport.Pan.Y).Should().Be(0);
    }

    // ---------------------------------------------------------------- 首帧自动整页适配

    [Fact]
    public void 整页适配后不留幽灵滚动条()
    {
        var kernel = KernelFixture.Kernel();
        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomFit)).Should().BeTrue();

        // 适配留白与内容盒留白必须是同一个数：留 24 而内容盒按 48 算，页高恰好等于视口时
        // 内容盒仍比视口高 2×(48−24)=48px → 一根滚不动东西的竖条永远挂着。
        var metrics = EditorScrollBars.Compute(kernel);

        metrics.Vertical.Should().BeNull("整页适配后纵向没有任何可滚范围，出现滚动条就是幽灵条");
        metrics.Horizontal.Should().BeNull();
    }

    [Fact]
    public void 装不进视口的长模板_适配后纵向滚动范围仍在()
    {
        // 100×100 视口：适配能给的缩放被下限 5% 夹住，A4 页高 1122.52×0.05 = 56.1px，
        // 内容盒 56.1 + 2×48 = 152.1px > 100px → 溢出是真的，竖条必须还在。
        var kernel = new EditorKernel(KernelFixture.Page());
        kernel.ViewWidthHostPx = 100;
        kernel.ViewHeightHostPx = 100;
        kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomFit)).Should().BeTrue();
        kernel.Viewport.Zoom.Should().Be(Viewport.MinZoom, "前置条件：这次适配被最小缩放夹住了");

        var metrics = EditorScrollBars.Compute(kernel);

        metrics.Vertical.Should().NotBeNull("内容真的溢出，不能为了消灭幽灵条把滚动条关掉");
        metrics.Vertical!.Value.ScrollMax.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 首帧尺寸就位自动整页适配_页面默认居中()
    {
        var kernel = new EditorKernel(KernelFixture.Page());
        kernel.AutoFitOnFirstLayout = true;

        // 先宽后高也能触发：两个尺寸都 > 0 才适配（宿主首帧常常也是一先一后）
        kernel.ViewWidthHostPx = 800;
        kernel.Viewport.Zoom.Should().Be(1, "高还不知道，先别动");
        kernel.ViewHeightHostPx = 600;

        // 整页适配：留白与滚动条内容盒同一个数（48），缩放 = min((800−96)/793.70, (600−96)/1122.52) ≈ 0.449
        kernel.Viewport.Zoom.Should().BeLessThan(1.0);
        // 页面中心 mm(105, 148.5) 落在视口中心 (400, 300)
        var center = kernel.Viewport.ToHostPx(105, 148.5, KernelFixture.Dpi96);
        ((double)center.X).Should().BeApproximately(400, 0.5);
        ((double)center.Y).Should().BeApproximately(300, 0.5);

        // 只适配一次：之后 resize 不再打断用户的视口
        var zoom = kernel.Viewport.Zoom;
        kernel.ViewWidthHostPx = 400;
        kernel.Viewport.Zoom.Should().Be(zoom);
    }

    [Fact]
    public void 默认不自动适配_无头测试赋尺寸视口不动()
    {
        var kernel = KernelFixture.Kernel();
        kernel.ViewWidthHostPx = 800;
        kernel.ViewHeightHostPx = 600;

        kernel.Viewport.Zoom.Should().Be(1);
        ((double)kernel.Viewport.Pan.Y).Should().Be(0, "构造台赋尺寸不该悄悄改视口，否则所有几何测试的 Pan=0 前提全塌");
    }
}
