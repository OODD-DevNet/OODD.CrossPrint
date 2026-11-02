using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 视口：缩放 + 平移 + 坐标换算。<b>内核里唯一知道"屏幕"的地方</b>——
/// 元素几何、命中结果、命令参数一律用 mm，宿主像素只在本类进出。
/// </summary>
/// <remarks>
/// 三层坐标，换算链固定为 <c>宿主设备px → 96基准px → 缩放后页面px → mm</c>：
/// <list type="bullet">
/// <item><description>宿主设备 px：鼠标事件原始坐标，随 <see cref="PointerPoint.Dpi"/> 变。</description></item>
/// <item><description>96 基准 px（下称"视口 px"）：<c>hostPx × 96 / dpi</c>。<see cref="Pan"/> 就活在这一层，
/// 所以外接屏换了 DPI 平移量不会自己漂。</description></item>
/// <item><description>页面 px：视口 px 去掉平移再除以缩放，1mm = <see cref="MmToBasePx"/> px。</description></item>
/// </list>
/// 实测锚点：96 DPI、zoom=1 时 1mm = 3.7795 视口 px；zoom=2 时同一 mm 落在 7.559 px 处。
/// </remarks>
public sealed class Viewport
{
    /// <summary>基准逻辑 DPI：96 基准 px 与宿主设备 px 的换算桥。</summary>
    public const double BaseDpi = 96.0;

    /// <summary>最小缩放（5%），低于此值页面已经缩成一个点，继续放大会丢失整数像素对齐。</summary>
    public const double MinZoom = 0.05;

    /// <summary>最大缩放（400%）。</summary>
    public const double MaxZoom = 4.0;

    /// <summary>1mm 在 zoom=1、96 DPI 下占多少 px（= 96 / 25.4）。像素换算只允许出现在这里。</summary>
    public const double MmToBasePx = BaseDpi / UnitConverter.MmPerInch;

    double _zoom = 1.0;
    double _dpi = BaseDpi;

    /// <summary>缩放倍数，赋值即夹紧到 <see cref="MinZoom"/>..<see cref="MaxZoom"/>。</summary>
    public double Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    /// <summary>平移量，单位是<b>视口 px</b>（96 基准、未缩放），不是设备像素。</summary>
    public Point2D Pan { get; set; }

    /// <summary>宿主逻辑 DPI（Avalonia/WPF 的 96×scaling、WinForms 的 DeviceDpi）。渲染期用；
    /// 事件坐标一律带自己的 DPI，见 <see cref="PointerPoint.Dpi"/>。</summary>
    /// <remarks>每次换算都除以它，所以 0/NaN/Infinity 必须在入口就拒掉——放进视口会把拖拽算出的 mm
    /// 一路带进命令与 JSON。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">值不是有限正数。</exception>
    public double Dpi
    {
        get => _dpi;
        set => _dpi = double.IsFinite(value) && value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "DPI 必须是有限正数。");
    }

    /// <summary>
    /// 交给 <see cref="CrossPrint.Engine.TemplateRenderer"/> 的 <c>RenderOptions.Dpi</c>：
    /// 引擎入口做 <c>canvas.Scale(Dpi/25.4)</c>，所以 <c>dpi×zoom</c> 正好把 mm 直接映射到宿主设备像素。
    /// 这就是"预览所见即导出所得"——同一套引擎、同一个缩放，只有 Dpi 参数不同。
    /// </summary>
    public double EffectiveRenderDpi => Dpi * Zoom;

    /// <summary>宿主设备 px → mm（页面坐标）。</summary>
    /// <param name="hostX">宿主设备横坐标。</param>
    /// <param name="hostY">宿主设备纵坐标。</param>
    /// <param name="dpi">事件携带的宿主逻辑 DPI。</param>
    /// <returns>页面毫米坐标。</returns>
    public Point2D ToMm(double hostX, double hostY, double dpi)
    {
        var baseX = ToBasePx(hostX, dpi);
        var baseY = ToBasePx(hostY, dpi);
        return new Point2D(
            (baseX - Pan.X) / Zoom / MmToBasePx,
            (baseY - Pan.Y) / Zoom / MmToBasePx);
    }

    /// <summary>mm → 宿主设备 px。叠加层与命中框都要走这条反向链。</summary>
    /// <param name="mmX">页面横坐标（mm）。</param>
    /// <param name="mmY">页面纵坐标（mm）。</param>
    /// <param name="dpi">宿主逻辑 DPI。</param>
    /// <returns>宿主设备坐标。</returns>
    public Point2D ToHostPx(double mmX, double mmY, double dpi)
        => new(
            FromBasePx(mmX * MmToBasePx * Zoom + Pan.X, dpi),
            FromBasePx(mmY * MmToBasePx * Zoom + Pan.Y, dpi));

    /// <summary>mm 矩形 → 宿主设备 px 矩形。</summary>
    /// <param name="rectMm">页面毫米矩形。</param>
    /// <param name="dpi">宿主逻辑 DPI。</param>
    /// <returns>设备像素矩形。</returns>
    public SKRect ToHostRect(SKRect rectMm, double dpi)
    {
        var a = ToHostPx(rectMm.Left, rectMm.Top, dpi);
        var b = ToHostPx(rectMm.Right, rectMm.Bottom, dpi);
        return SKRect.Create((float)a.X, (float)a.Y, (float)(b.X - a.X), (float)(b.Y - a.Y));
    }

    /// <summary>
    /// 以锚点为中心缩放：缩放前后锚定处的页面 mm 不变，只改 <see cref="Pan"/>。
    /// 滚轮直接调这条，光标底下的内容才不会跑掉。
    /// </summary>
    /// <param name="factor">缩放乘数（&gt;1 放大）。</param>
    /// <param name="anchorHostX">锚点宿主横坐标。</param>
    /// <param name="anchorHostY">锚点宿主纵坐标。</param>
    /// <param name="dpi">宿主逻辑 DPI。</param>
    /// <returns>夹紧后的新缩放值。</returns>
    public double ZoomAt(double factor, double anchorHostX, double anchorHostY, double dpi)
    {
        var next = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (next == Zoom)
            return Zoom;

        var anchorBaseX = ToBasePx(anchorHostX, dpi);
        var anchorBaseY = ToBasePx(anchorHostY, dpi);

        // pageBase 不变：anchorBase - Pan' = (anchorBase - Pan) * next/Zoom
        Pan = new Point2D(
            (float)(anchorBaseX - (anchorBaseX - Pan.X) * (next / Zoom)),
            (float)(anchorBaseY - (anchorBaseY - Pan.Y) * (next / Zoom)));
        Zoom = next;
        return Zoom;
    }

    /// <summary>整页适配：在视口里尽量大地放下整页并居中。</summary>
    /// <param name="pageWidthMm">页宽（mm）。</param>
    /// <param name="pageHeightMm">页高（mm）。</param>
    /// <param name="viewWidthHostPx">控件宽（宿主设备 px）。</param>
    /// <param name="viewHeightHostPx">控件高（宿主设备 px）。</param>
    /// <param name="dpi">宿主逻辑 DPI。</param>
    /// <param name="paddingHostPx">四周留白（宿主设备 px）。默认取滚动条内容盒的留白：
    /// 两个数不一致时，整页适配完内容盒仍比视口大出一截，白白挂着一根滚不动的幽灵滚动条。</param>
    public void FitToPage(double pageWidthMm, double pageHeightMm, double viewWidthHostPx, double viewHeightHostPx, double dpi, double paddingHostPx = EditorScrollBars.ContentPaddingHostPx)
    {
        if (pageWidthMm <= 0 || pageHeightMm <= 0 || viewWidthHostPx <= 0 || viewHeightHostPx <= 0)
            return;

        var pageBaseWidth = pageWidthMm * MmToBasePx;
        var pageBaseHeight = pageHeightMm * MmToBasePx;
        var viewBaseWidth = ToBasePx(viewWidthHostPx, dpi);
        var viewBaseHeight = ToBasePx(viewHeightHostPx, dpi);
        var availableBaseWidth = Math.Max(1, viewBaseWidth - 2 * ToBasePx(paddingHostPx, dpi));
        var availableBaseHeight = Math.Max(1, viewBaseHeight - 2 * ToBasePx(paddingHostPx, dpi));

        Zoom = Math.Min(availableBaseWidth / pageBaseWidth, availableBaseHeight / pageBaseHeight);

        // 居中按整个视口算，不是按"去掉 padding 后的可用区"算：Pan 是从视口原点量起的，
        // 用可用区算会整体偏向 padding 那个角（默认 24 视口 px），页面看着没居中。
        Pan = new Point2D(
            (float)((viewBaseWidth - pageBaseWidth * Zoom) / 2),
            (float)((viewBaseHeight - pageBaseHeight * Zoom) / 2));
    }

    /// <summary>恢复 100%（zoom=1，平移清零）。</summary>
    public void Reset()
    {
        Zoom = 1.0;
        Pan = default;
    }

    static double ToBasePx(double hostPx, double dpi) => hostPx * BaseDpi / dpi;

    static double FromBasePx(double basePx, double dpi) => basePx * dpi / BaseDpi;
}

/// <summary>
/// 二维点，单位由成员名与调用点自证。内核用它承载"宿主设备 px"与"页面 mm"两种语义，
/// 靠方法签名区分，不在类型里藏单位——避免为每种单位再造一个 record。
/// </summary>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
public readonly record struct Point2D(double X, double Y);
