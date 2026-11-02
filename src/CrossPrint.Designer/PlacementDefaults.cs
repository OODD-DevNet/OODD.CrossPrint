using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 工具箱放置的默认尺寸（mm）。放置时用户只给一个点，尺寸必须由这张表补齐。
/// </summary>
/// <remarks>
/// 取值口径：都是"放下去就看得见、且不会被 80mm 热敏纸裁掉"的经验尺寸——
/// 连续纸（<c>Receipt80</c>）页宽 80mm，故最宽的表格取 60mm，留出两侧余量；
/// 码类一律正方形（二维码按短边居中绘制，非正方形只会浪费静区）；
/// 线的高度取 0（渲染器沿 <c>YMm</c> 那条边画线，高度本身没有视觉含义，见 <c>LineRenderer</c>）。
/// </remarks>
public static class PlacementDefaults
{
    /// <summary>未知类型键的兜底尺寸（mm），保证放置永不失败。</summary>
    public const double FallbackWidthMm = 30;

    /// <summary>未知类型键的兜底高度（mm）。</summary>
    public const double FallbackHeightMm = 20;

    static readonly Dictionary<string, ElementSizeMm> _sizes = new(StringComparer.Ordinal)
    {
        [ElementTypeKeys.Text] = new ElementSizeMm(50, 8),
        [ElementTypeKeys.Line] = new ElementSizeMm(50, 0),
        [ElementTypeKeys.Rectangle] = new ElementSizeMm(40, 20),
        [ElementTypeKeys.Ellipse] = new ElementSizeMm(30, 30),
        [ElementTypeKeys.Image] = new ElementSizeMm(30, 30),
        [ElementTypeKeys.QrCode] = new ElementSizeMm(30, 30),
        [ElementTypeKeys.Barcode] = new ElementSizeMm(60, 15),
        [ElementTypeKeys.Table] = new ElementSizeMm(60, 30),
    };

    /// <summary>某类型键的默认尺寸。未注册的类型键返回兜底值而不是抛——放置失败不该让宿主崩溃。</summary>
    /// <param name="typeKey">元素类型键。</param>
    /// <returns>默认宽与高（mm）。</returns>
    public static ElementSizeMm SizeFor(string typeKey)
        => _sizes.TryGetValue(typeKey, out var size) ? size : new ElementSizeMm(FallbackWidthMm, FallbackHeightMm);

    /// <summary>默认尺寸且以给定点为中心的矩形（mm），放置幽灵与落点共用这一个算式。</summary>
    /// <param name="typeKey">元素类型键。</param>
    /// <param name="centerXMm">中心横坐标（mm）。</param>
    /// <param name="centerYMm">中心纵坐标（mm）。</param>
    /// <returns>左上角 + 宽高（mm）。</returns>
    public static ElementBoxMm BoxCenteredAt(string typeKey, double centerXMm, double centerYMm)
    {
        var size = SizeFor(typeKey);
        return new ElementBoxMm(centerXMm - size.WidthMm / 2, centerYMm - size.HeightMm / 2, size.WidthMm, size.HeightMm);
    }
}

/// <summary>元素尺寸（mm）。</summary>
/// <param name="WidthMm">宽。</param>
/// <param name="HeightMm">高。</param>
public readonly record struct ElementSizeMm(double WidthMm, double HeightMm);

/// <summary>元素矩形（mm）。内核里所有"一个盒子"都用它传递，不用 <c>SKRect</c> 是因为它是 float，
/// 会把 0.001mm 级的放置误差吃掉。</summary>
/// <param name="X">左上角横坐标。</param>
/// <param name="Y">左上角纵坐标。</param>
/// <param name="Width">宽。</param>
/// <param name="Height">高。</param>
public readonly record struct ElementBoxMm(double X, double Y, double Width, double Height)
{
    /// <summary>右下角横坐标（mm）。</summary>
    public double RightMm => X + Width;

    /// <summary>右下角纵坐标（mm）。</summary>
    public double BottomMm => Y + Height;

    /// <summary>中心横坐标（mm）。</summary>
    public double CenterXMm => X + Width / 2;

    /// <summary>中心纵坐标（mm）。</summary>
    public double CenterYMm => Y + Height / 2;

    /// <summary>从 <see cref="CrossPrint.Core.PaperElement"/> 取包围盒。</summary>
    /// <param name="element">元素。</param>
    /// <returns>同一几何的矩形值。</returns>
    public static ElementBoxMm FromElement(PaperElement element)
        => new(element.XMm, element.YMm, element.WidthMm, element.HeightMm);

    /// <summary>转为 <c>SKRect</c> 供叠加层绘制与命中测试使用。</summary>
    /// <returns>像素无关的纯几何矩形（单位仍是 mm）。</returns>
    public SKRect ToSkRect() => SKRect.Create((float)X, (float)Y, (float)Width, (float)Height);
}
