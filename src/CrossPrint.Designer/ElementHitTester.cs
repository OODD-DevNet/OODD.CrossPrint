using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 命中测试：把页面 mm 坐标翻译成"用户点到了哪个元素 / 哪个手柄"。
/// 纯几何、无状态——缩放与设备像素的换算一律由调用方（<see cref="EditorKernel"/>）先做完。
/// </summary>
public static class ElementHitTester
{
    /// <summary>
    /// 线段命中框在描边之外的最小展开宽度（mm）。
    /// <para>
    /// 为什么必须加宽：0.3mm 发丝线在 96 DPI、zoom=1 下只占 <c>0.3 × 96/25.4 = 1.13</c> 个设备像素，
    /// 而渲染描边是居中于 <c>YMm</c>（水平线）/<c>XMm</c>（垂直线）这条轴的，抗锯齿会把这 1px 摊成
    /// 两道灰边——用户对着看得见的那条线点，命中的却是它旁边的空白。所以命中框按
    /// <c>max(strokeWidthMm, 0.5)</c> 的带宽取，0.5mm 在 96 DPI 下是 1.89px，至少保证线的可见宽度全在命中区内。
    /// </para>
    /// </summary>
    public const double MinLineHitWidthMm = 0.5;

    /// <summary>
    /// 退化盒（零高或零宽元素）在命中时补的最小抓取带宽度（mm），与 <see cref="MinLineHitWidthMm"/> 同源。
    /// <para>
    /// 为什么要补：发丝线之外，<c>HeightMm == 0</c>（手改 JSON、或导入把负高夹成 <c>Math.Max(0, …)</c>）
    /// 会让 <c>SKRect.Create</c> 出的命中盒只有一条零厚度线，而 <c>SKRect.Contains</c> 对零维只匹配
    /// 那一根精确坐标，结果这类元素永远点不中、选不中，也就再也打不开它的属性面板。
    /// 取 0.5mm：96 DPI 下 <c>0.5 × 96/25.4 = 1.89</c> 设备 px，跨 2 px 足够点中，又薄到绝不会侵入相邻正常盒。
    /// </para>
    /// </summary>
    public const double MinDegenerateHitMm = MinLineHitWidthMm;

    /// <summary>
    /// 绘制次序：<c>ZIndex</c> 升序 + 同值按数组顺序。
    /// 这条必须与 <see cref="CrossPrint.Engine.TemplateRenderer"/> 的 <c>Elements.OrderBy(e =&gt; e.ZIndex)</c>
    /// 逐字等价（LINQ to Objects 的 <c>OrderBy</c> 是稳定排序），否则"点到的"和"看见的"就不是同一个元素。
    /// </summary>
    /// <param name="elements">模板元素集合。</param>
    /// <returns>自下而上的绘制顺序。</returns>
    public static IEnumerable<PaperElement> DrawOrder(IEnumerable<PaperElement> elements)
        => elements.OrderBy(e => e.ZIndex);

    /// <summary>命中最上层元素（绘制最晚的那个在它上面）。无命中返回 null。</summary>
    /// <param name="elements">模板元素集合。</param>
    /// <param name="mmX">页面横坐标（mm）。</param>
    /// <param name="mmY">页面纵坐标（mm）。</param>
    /// <returns>被命中的元素，或 null。</returns>
    public static PaperElement? HitTest(IEnumerable<PaperElement> elements, double mmX, double mmY)
    {
        // 反着走绘制序 = 从最上层往下找，第一个包住点的就是用户看见的那个。
        foreach (var element in DrawOrder(elements).Reverse())
        {
            if (HitRectMm(element).Contains((float)mmX, (float)mmY))
                return element;
        }

        return null;
    }

    /// <summary>
    /// 元素的命中矩形（mm）。
    /// <para>
    /// v0.1 除线段外一律用包围盒：图片的透明区、二维码的静区、椭圆的四角、表格未画满的行
    /// 都会被"点到看不见的地方"，但反向误差（点在描边上却没命中）比实现按形状精确命中的代价更大，
    /// 所以这版统一按包围盒，逐类型收紧留到 P1。
    /// </para>
    /// </summary>
    /// <param name="element">元素。</param>
    /// <returns>命中矩形（mm）。</returns>
    public static SKRect HitRectMm(PaperElement element)
    {
        var rotation = EffectiveRotation(element);

        if (element is LineElement line)
        {
            // 渲染器画在 YMm（水平）/ XMm（垂直）这条轴上，所以命中带以该轴为中心展开。
            var half = Math.Max(line.Line.StrokeWidthMm, MinLineHitWidthMm) / 2;
            var band = line.Line.Direction == LineDirection.Horizontal
                ? SKRect.Create((float)(line.XMm - half), (float)(line.YMm - half), (float)(line.WidthMm + 2 * half), (float)(2 * half))
                : SKRect.Create((float)(line.XMm - half), (float)(line.YMm - half), (float)(2 * half), (float)(line.HeightMm + 2 * half));
            return RotationAabbMm(band, rotation);
        }

        var x = element.XMm;
        var y = element.YMm;
        var width = element.WidthMm;
        var height = element.HeightMm;

        // 退化盒按最小抓取带对称展开（中心不动），再交给同一套旋转外接盒，与叠加层框同源。
        if (width == 0)
        {
            x -= MinDegenerateHitMm / 2;
            width = MinDegenerateHitMm;
        }

        if (height == 0)
        {
            y -= MinDegenerateHitMm / 2;
            height = MinDegenerateHitMm;
        }

        return RotationAabbMm(SKRect.Create((float)x, (float)y, (float)width, (float)height), rotation);
    }

    /// <summary>
    /// 渲染器实际生效的旋转角：<c>NaN</c>/<c>Infinity</c> 一律按 0 处理，与
    /// <see cref="CrossPrint.Engine.TemplateRenderer"/> 的 <c>double.IsFinite</c> 守卫逐字一致。
    /// </summary>
    /// <param name="element">元素。</param>
    /// <returns>旋转角（度），非有限数时返回 0。</returns>
    static double EffectiveRotation(PaperElement element)
        => double.IsFinite(element.RotationDegrees) ? element.RotationDegrees : 0;

    /// <summary>
    /// 把矩形绕其中心旋转 <paramref name="angleDeg"/> 度后的轴对齐外接盒（AABB）。
    /// <para>
    /// 渲染器绕元素中心 <c>RotateDegrees</c>，所以"点到的"必须按同一中心取旋转后的外接盒；
    ///  extent 用 <c>|w·cosθ|+|h·sinθ| × |w·sinθ|+|h·cosθ|</c>，v0.1 维持 bbox 契约不做按形状精确命中。
    /// 正弦 ~0 时（0°/180°/360°）外接盒恒等于原盒，直接返回原盒以躲开三角噪声、保住精确断言；
    /// 90°/270° 时正弦为 ±1，必须真正交换宽高，不能走这条捷径。
    /// </para>
    /// </summary>
    /// <param name="rect">未旋转的盒（mm）。</param>
    /// <param name="angleDeg">绕中心的旋转角（度，顺时针为正）。</param>
    /// <returns>旋转后的外接盒（mm）。</returns>
    static SKRect RotationAabbMm(SKRect rect, double angleDeg)
    {
        if (angleDeg == 0)
            return rect;

        var rad = angleDeg * Math.PI / 180;
        var sin = Math.Sin(rad);
        if (Math.Abs(sin) < 1e-12)
            return rect;

        var cos = Math.Cos(rad);
        var centerX = ((double)rect.Left + rect.Right) / 2;
        var centerY = ((double)rect.Top + rect.Bottom) / 2;
        var width = Math.Abs(rect.Width * cos) + Math.Abs(rect.Height * sin);
        var height = Math.Abs(rect.Width * sin) + Math.Abs(rect.Height * cos);
        return SKRect.Create(
            (float)(centerX - width / 2),
            (float)(centerY - height / 2),
            (float)width,
            (float)height);
    }

    /// <summary>若干元素的外接框（mm）。空集合返回零尺寸盒。</summary>
    /// <param name="elements">元素集合。</param>
    /// <returns>外接框。</returns>
    public static ElementBoxMm UnionBox(IEnumerable<PaperElement> elements)
    {
        // 手柄与叠加层选中框都从这里取，故必须走与 HitRectMm 同一套旋转外接盒，
        // 否则旋转元素的框压在空白纸、手柄点偏——"所见"与"所点"再次分家。
        // 但线段的抓取带、退化盒的最小带宽只用于"点得到"，不参与此处几何：
        // 若把它们并进来，拖拽缩放会把这层薄 padding 写回元素宽高，破坏线段长度语义。
        var found = false;
        var left = 0.0;
        var top = 0.0;
        var right = 0.0;
        var bottom = 0.0;

        foreach (var element in elements)
        {
            var rect = RotationAabbMm(
                SKRect.Create((float)element.XMm, (float)element.YMm, (float)element.WidthMm, (float)element.HeightMm),
                EffectiveRotation(element));

            if (!found)
            {
                left = rect.Left;
                top = rect.Top;
                right = rect.Right;
                bottom = rect.Bottom;
                found = true;
                continue;
            }

            left = Math.Min(left, rect.Left);
            top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        return found ? new ElementBoxMm(left, top, right - left, bottom - top) : default;
    }

    /// <summary>
    /// 八向手柄的锚点（mm）。
    /// <see cref="ResizeHandle"/> 的文档规定"角柄先于边柄"，所以这里的枚举顺序是
    /// 左上→右上→右下→左下→上→右→下→左：角上是两条边的交汇处，先判角才不会让角柄被边柄抢走。
    /// </summary>
    /// <param name="box">选中元素的外接框（mm）。</param>
    /// <returns>手柄与锚点，按命中判定顺序排列。</returns>
    public static IReadOnlyList<(ResizeHandle Handle, double XMm, double YMm)> HandleAnchorsMm(ElementBoxMm box)
    {
        var midX = box.X + box.Width / 2;
        var midY = box.Y + box.Height / 2;
        return
        [
            (ResizeHandle.TopLeft, box.X, box.Y),
            (ResizeHandle.TopRight, box.RightMm, box.Y),
            (ResizeHandle.BottomRight, box.RightMm, box.BottomMm),
            (ResizeHandle.BottomLeft, box.X, box.BottomMm),
            (ResizeHandle.Top, midX, box.Y),
            (ResizeHandle.Right, box.RightMm, midY),
            (ResizeHandle.Bottom, midX, box.BottomMm),
            (ResizeHandle.Left, box.X, midY),
        ];
    }
}
