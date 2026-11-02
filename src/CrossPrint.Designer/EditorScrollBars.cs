using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 编辑器滚动条的几何计算：叠加层（画轨道/滑块）与内核（命中/拖拽滑块）共用同一份算法，
/// 两边各算各的迟早漂移——滑块画在 A 处、点击判定在 B 处，这种 bug 用户一眼就能踩到。
/// </summary>
/// <remarks>
/// <para>
/// 内容盒定义为「纸张矩形外扩固定留白」（宿主设备 px）。留白钉在纸张坐标系上，
/// 所以滚动范围与 <see cref="Viewport.Pan"/> 无关，拖滑块时轨道不会自己抖；
/// 某轴「内容长 &gt; 视口长」才出现该轴的滚动条——内容没超出视口时不画也不命中，
/// 空滚动条只会误导用户以为还能滚。
/// </para>
/// <para>
/// 滚动位置 s = 视口左/顶边在内容盒里的偏移（px）。s 与 <see cref="Viewport.Pan"/>
/// 的换算关系：<c>纸张左/顶边（宿主 px）= 留白 − s</c>，而纸张左/顶边 = <c>Pan × dpi / 96</c>。
/// 内核的 <c>ScrollHorizontalTo/ScrollVerticalTo</c> 走的就是这条式子。
/// </para>
/// </remarks>
public static class EditorScrollBars
{
    /// <summary>默认滚动条厚度（宿主设备 px）。渲染器画多粗、内核就按多粗判定命中。</summary>
    public const double DefaultThicknessHostPx = 14;

    /// <summary>内容盒四周虚拟留白（宿主设备 px）：滚到头时纸张边缘也不贴死视口边，还留得住页影。</summary>
    public const double ContentPaddingHostPx = 48;

    /// <summary>滑块最小长度（宿主设备 px）：内容特别长时滑块也不能缩成一个点。</summary>
    public const double MinThumbLengthHostPx = 24;

    /// <summary>
    /// 单轴滚动条几何（宿主设备 px）。
    /// </summary>
    /// <param name="ContentLength">内容盒长度（px）。</param>
    /// <param name="ViewLength">视口长度（px）。</param>
    /// <param name="ScrollMax">最大滚动量（px）。</param>
    /// <param name="ScrollPos">当前滚动位置（px，已夹取到 0..<see cref="ScrollMax"/>）。</param>
    /// <param name="Track">轨道矩形。水平轴贴视口底边，垂直轴贴视口右缘。</param>
    /// <param name="Thumb">滑块矩形（含最小长度夹取）。</param>
    public readonly record struct Axis(
        double ContentLength,
        double ViewLength,
        double ScrollMax,
        double ScrollPos,
        SKRect Track,
        SKRect Thumb)
    {
        /// <summary>轨道是否纵向（纵向轨道高 &gt; 宽）。</summary>
        public bool IsVertical => Track.Width <= Track.Height;
    }

    /// <summary>双轴滚动条几何；null 表示该轴内容未超出视口，不画也不命中。</summary>
    /// <param name="Horizontal">水平轴。</param>
    /// <param name="Vertical">垂直轴。</param>
    public readonly record struct Metrics(Axis? Horizontal, Axis? Vertical);

    /// <summary>计算当前滚动条几何。视口尺寸未知（宿主还没画过第一帧）时返回空。</summary>
    /// <param name="kernel">内核（取纸张矩形、视口尺寸与 DPI）。</param>
    /// <param name="thicknessHostPx">滚动条厚度（宿主设备 px）。</param>
    public static Metrics Compute(EditorKernel kernel, double thicknessHostPx = DefaultThicknessHostPx)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var viewW = kernel.ViewWidthHostPx;
        var viewH = kernel.ViewHeightHostPx;
        if (viewW <= 0 || viewH <= 0 || thicknessHostPx <= 0)
            return default;

        // 内容盒 = 纸张矩形外扩留白（宿主 px）。
        var page = EditorRenderer.PageRectHost(kernel);
        var contentLeft = page.Left - ContentPaddingHostPx;
        var contentTop = page.Top - ContentPaddingHostPx;
        var contentWidth = page.Width + 2 * ContentPaddingHostPx;
        var contentHeight = page.Height + 2 * ContentPaddingHostPx;

        const double epsilon = 0.5;
        var hOverflows = contentWidth > viewW + epsilon;
        var vOverflows = contentHeight > viewH + epsilon;

        Axis? horizontal = null;
        Axis? vertical = null;

        // 先算水平轴（其轨道长度要看垂直轴是否出现），再算垂直轴（反过来）——
        // 两轴都出现时，右下角那块方形留给了垂直轴。
        if (hOverflows)
        {
            var trackLength = viewW - (vOverflows ? thicknessHostPx : 0);
            var scrollMax = contentWidth - viewW;
            var scrollPos = Math.Clamp(-contentLeft, 0, scrollMax);
            var thumbLength = Math.Max(MinThumbLengthHostPx, trackLength * viewW / contentWidth);
            var offset = ThumbOffset(scrollPos, scrollMax, trackLength - thumbLength);
            var track = SKRect.Create(0, (float)(viewH - thicknessHostPx), (float)trackLength, (float)thicknessHostPx);
            var thumb = SKRect.Create((float)offset, track.Top, (float)thumbLength, (float)thicknessHostPx);
            horizontal = new Axis(contentWidth, viewW, scrollMax, scrollPos, track, thumb);
        }

        if (vOverflows)
        {
            var trackLength = viewH - (hOverflows ? thicknessHostPx : 0);
            var scrollMax = contentHeight - viewH;
            var scrollPos = Math.Clamp(-contentTop, 0, scrollMax);
            var thumbLength = Math.Max(MinThumbLengthHostPx, trackLength * viewH / contentHeight);
            var offset = ThumbOffset(scrollPos, scrollMax, trackLength - thumbLength);
            var track = SKRect.Create((float)(viewW - thicknessHostPx), 0, (float)thicknessHostPx, (float)trackLength);
            var thumb = SKRect.Create(track.Left, (float)offset, (float)thicknessHostPx, (float)thumbLength);
            vertical = new Axis(contentHeight, viewH, scrollMax, scrollPos, track, thumb);
        }

        return new Metrics(horizontal, vertical);
    }

    static double ThumbOffset(double scrollPos, double scrollMax, double usable)
        => scrollMax <= 0 || usable <= 0 ? 0 : scrollPos / scrollMax * usable;
}
