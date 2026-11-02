using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 内核测试的构造台：纸张、元素、指针事件。
/// </summary>
/// <remarks>
/// 坐标换算在这里也只走 <see cref="Viewport"/> 一条链，但<b>凡是要断言数值的地方</b>，
/// 测试里都会把算术单独写出来（不靠本台反推），否则"用同一个函数造输入又用同一个函数算期望"
/// 等于什么都没验。
/// </remarks>
internal static class KernelFixture
{
    /// <summary>100% 缩放下的逻辑 DPI。</summary>
    internal const double Dpi96 = 96.0;

    /// <summary>125% 缩放的逻辑 DPI（外接屏常见档）。</summary>
    internal const double Dpi120 = 120.0;

    /// <summary>宿主控件宽（设备 px）。</summary>
    internal const double ViewWidthPx = 800;

    /// <summary>宿主控件高（设备 px）。</summary>
    internal const double ViewHeightPx = 600;

    /// <summary>页宽（mm）。</summary>
    internal const double PageWidthMm = 210;

    /// <summary>页高（mm）。</summary>
    internal const double PageHeightMm = 297;

    /// <summary>自定义 A4 尺寸模板 + 5mm 留白（spec 指定的无头测试台）。</summary>
    /// <returns>空模板。</returns>
    internal static PrintTemplate Page()
        => new()
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.Custom, WidthMm = PageWidthMm, HeightMm = PageHeightMm },
            Margins = new ThicknessMm(5, 5, 5, 5),
        };

    /// <summary>建内核并给出宿主尺寸（居中放置与整页适配需要知道"看得见多大一块"）。</summary>
    /// <param name="template">模板；null 用 <see cref="Page"/>。</param>
    /// <returns>配好视口的内核。</returns>
    internal static EditorKernel Kernel(PrintTemplate? template = null)
    {
        var kernel = new EditorKernel(template ?? Page());
        kernel.ViewWidthHostPx = ViewWidthPx;
        kernel.ViewHeightHostPx = ViewHeightPx;
        return kernel;
    }

    /// <summary>加一个矩形元素。</summary>
    /// <returns>元素实例（id 由模板分配）。</returns>
    internal static RectangleElement AddRect(PrintTemplate template, double x, double y, double width, double height, int zIndex = 0)
    {
        var element = new RectangleElement { XMm = x, YMm = y, WidthMm = width, HeightMm = height, ZIndex = zIndex };
        template.Add(element);
        return element;
    }

    /// <summary>加一个线段元素。</summary>
    /// <returns>元素实例。</returns>
    internal static LineElement AddLine(PrintTemplate template, LineDirection direction, double x, double y, double lengthMm, double strokeWidthMm = 0.3)
    {
        var element = new LineElement
        {
            XMm = x,
            YMm = y,
            WidthMm = direction == LineDirection.Horizontal ? lengthMm : 0,
            HeightMm = direction == LineDirection.Horizontal ? 0 : lengthMm,
            Line = new LineOptions { Direction = direction, StrokeWidthMm = strokeWidthMm },
        };
        template.Add(element);
        return element;
    }

    /// <summary>页面 mm 坐标 → 指针事件（走 <see cref="Viewport.ToHostPx"/> 正向链）。</summary>
    /// <param name="kernel">内核。</param>
    /// <param name="mmX">页面横坐标（mm）。</param>
    /// <param name="mmY">页面纵坐标（mm）。</param>
    /// <param name="dpi">事件 DPI。</param>
    /// <param name="button">按键。</param>
    /// <returns>指针事件。</returns>
    internal static PointerPoint At(EditorKernel kernel, double mmX, double mmY, double dpi = Dpi96, PointerButton button = PointerButton.Primary)
    {
        var host = kernel.Viewport.ToHostPx(mmX, mmY, dpi);
        return new PointerPoint(host.X, host.Y, dpi, button);
    }

    /// <summary>宿主设备 px → 指针事件（不做任何换算，专供"算术写在注释里"的测试）。</summary>
    /// <param name="x">设备横坐标。</param>
    /// <param name="y">设备纵坐标。</param>
    /// <param name="dpi">事件 DPI。</param>
    /// <param name="button">按键。</param>
    /// <returns>指针事件。</returns>
    internal static PointerPoint Px(double x, double y, double dpi = Dpi96, PointerButton button = PointerButton.Primary)
        => new(x, y, dpi, button);

    /// <summary>按下 → 移动 → 松开的一次拖拽。</summary>
    /// <param name="kernel">内核。</param>
    /// <param name="start">起点事件。</param>
    /// <param name="end">终点事件。</param>
    internal static void Drag(EditorKernel kernel, PointerPoint start, PointerPoint end)
    {
        kernel.OnPointerPressed(start);
        kernel.OnPointerMoved(end);
        kernel.OnPointerReleased(end);
    }

    /// <summary>元素当前的命中矩形（mm），便于把断言写成框而不是四个数。</summary>
    /// <param name="element">元素。</param>
    /// <returns>命中矩形。</returns>
    internal static SKRect Box(this PaperElement element) => ElementHitTester.HitRectMm(element);
}
