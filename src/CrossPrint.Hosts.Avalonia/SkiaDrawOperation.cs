using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace CrossPrint.Hosts.Avalonia;

/// <summary>
/// 一次自定义绘制：把 Avalonia 的绘制上下文租成 <see cref="SKCanvas"/> 后交给宿主控件直接画。
/// </summary>
/// <remarks>
/// <see cref="Equals"/> 恒为 false 是刻意行为：每帧都重新执行一次绘制（否则内容变化后可能不重绘）。
/// 绘制委托由控件提供，控件负责把 DIP 坐标换算回宿主设备 px 再交给 <c>EditorCanvas.Draw</c>。
/// </remarks>
internal sealed class SkiaDrawOperation : ICustomDrawOperation
{
    readonly Action<SKCanvas, Rect, double> _draw;
    readonly Rect _bounds;
    readonly double _scaling;

    internal SkiaDrawOperation(Rect bounds, double scaling, Action<SKCanvas, Rect, double> draw)
    {
        _bounds = bounds;
        _scaling = scaling;
        _draw = draw;
    }

    /// <summary>绘制区域（DIP）。</summary>
    public Rect Bounds => _bounds;

    /// <summary>
    /// 命中区域就是整块绘制区。
    /// Avalonia 的合成器把 <see cref="ICustomDrawOperation.HitTest"/> 当作该视觉是否可命中的唯一依据
    /// （<c>CompositionDrawListVisual.HitTest</c> 直接转发给绘制操作），返回 false 会让控件
    /// 收不到任何指针事件——画布看似正常，点哪都没反应。控件自己再按元素做二级命中。
    /// </summary>
    /// <param name="p">测试点（控件局部坐标）。</param>
    /// <returns>点在绘制区内返回 true。</returns>
    public bool HitTest(Point p) => _bounds.Contains(p);

    /// <summary>恒为 false：强制每帧重绘。</summary>
    /// <param name="other">另一绘制操作。</param>
    /// <returns>恒为 false。</returns>
    public bool Equals(ICustomDrawOperation? other) => false;

    /// <summary>释放资源（本操作不持有任何资源）。</summary>
    public void Dispose()
    {
    }

    /// <summary>租出 <see cref="SKCanvas"/> 并交给控件绘制。</summary>
    /// <param name="context">Avalonia 的立即绘制上下文。</param>
    /// <remarks>
    /// ⚠ 本方法运行在渲染线程：<c>_draw</c> 内严禁访问 Avalonia 对象（属性读取会触发
    /// <c>Dispatcher.VerifyAccess</c> 抛 "Call from invalid thread"），也不得改动会回调
    /// <c>InvalidateVisual</c> 的内核状态。否则整帧被丢弃，窗口会停在上一次画面。
    /// </remarks>
    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
            return;

        using var lease = feature.Lease();
        _draw(lease.SkCanvas, _bounds, _scaling);
    }
}
