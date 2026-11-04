using System;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 多边形逐顶点圆角：把每个顶点替换为一段内切圆弧（真圆弧，不是贝塞尔近似）。
/// 半径语义与 Skia 的 AddRoundRect（矩形 90° 角）一致＝切点到顶点的边距；
/// 非 90° 角自动换算（切距 = r / tan(θ/2)，θ 为两邻边夹角），凸角削角、凹角内凹圆角都成立。
/// 半径逐顶点夹取到相邻半边长，超大输入不会让 Skia 静默画空（矩形圆角的既有教训）。
/// </summary>
internal static class CornerRounding
{
    /// <summary>
    /// 按顶点序构建圆角多边形路径。<paramref name="radii"/> 与顶点一一对应，
    /// 不足的顶点补 0；全部 ≤0 时返回 null（调用方走直角快路径）。
    /// 调用方负责 disposing 返回的路径。
    /// </summary>
    public static SKPath? Build((float X, float Y)[] points, double[] radii)
    {
        var n = points.Length;
        if (n < 3)
            return null;

        var cut = new double[n];    // 每个顶点沿两条邻边的切点边距
        var hasArc = false;
        for (var i = 0; i < n; i++)
        {
            var r = i < radii.Length ? radii[i] : 0;
            if (!(r > 0) || !Finite(r))
            {
                cut[i] = 0;
                continue;
            }

            var prev = points[(i + n - 1) % n];
            var next = points[(i + 1) % n];
            var v = points[i];
            var lenPrev = Dist(v, prev);
            var lenNext = Dist(v, next);
            if (lenPrev < Epsilon || lenNext < Epsilon)
            {
                cut[i] = 0;
                continue;
            }

            // 夹角 θ：两邻边单位向量夹角。θ→180°（共线）时 tan(θ/2)→∞，切距→0，自然退化为直线。
            var angle = AngleBetween(prev, v, next);
            var tanHalf = Math.Tan(angle / 2);
            var ideal = tanHalf <= Epsilon ? 0 : r / tanHalf;
            // 夹取到相邻半边长，再按实际切距反算有效半径（保持圆弧与两边严格相切）。
            var clamped = Math.Min(ideal, 0.49 * Math.Min(lenPrev, lenNext));
            cut[i] = clamped;
            if (clamped > Epsilon)
                hasArc = true;
        }

        if (!hasArc)
            return null;

        var path = new SKPath();
        // 起点取顶点 0（若顶点 0 有圆弧，起点取它在前一条边上的切点，避免 ArcTo 从顶点本身起弧）。
        var start = cut[0] > Epsilon
            ? TangentPoint(points[0], points[n - 1], cut[0])
            : points[0];
        path.MoveTo(start.X, start.Y);

        for (var i = 0; i < n; i++)
        {
            var v = points[i];
            if (cut[i] <= Epsilon)
            {
                path.LineTo(v.X, v.Y);
                continue;
            }

            var prev = points[(i + n - 1) % n];
            var next = points[(i + 1) % n];
            // 切点在距顶点 v 沿邻边 cut 处（TangentPoint 的距离从第一参数起量，第一参数必须是顶点）。
            var p1 = TangentPoint(v, prev, cut[i]);
            var p2 = TangentPoint(v, next, cut[i]);
            var angle = AngleBetween(prev, v, next);

            // 有效半径 = 实际切距 × tan(θ/2)（≤ 请求半径，夹取后仍与两边相切）。
            var effective = cut[i] * Math.Tan(angle / 2);
            var sinHalf = Math.Sin(angle / 2);
            if (effective <= Epsilon || sinHalf <= Epsilon)
            {
                path.LineTo(v.X, v.Y);
                continue;
            }

            // 内切圆心：沿角平分线（指向多边形内侧/凹口侧）距顶点 r / sin(θ/2)。
            var b = Bisector(prev, v, next);
            var center = (v.X + (float)(b.X * effective / sinHalf), v.Y + (float)(b.Y * effective / sinHalf));

            var startDeg = Degrees(p1, center);
            var endDeg = Degrees(p2, center);
            var sweep = endDeg - startDeg;
            while (sweep > 180)
                sweep -= 360;
            while (sweep <= -180)
                sweep += 360;

            path.LineTo(p1.X, p1.Y);
            var oval = new SKRect(center.Item1 - (float)effective, center.Item2 - (float)effective,
                                  center.Item1 + (float)effective, center.Item2 + (float)effective);
            path.ArcTo(oval, (float)startDeg, (float)sweep, false);
        }

        path.Close();
        return path;
    }

    /// <summary>矩形四顶点（顺时针 左上→右上→右下→左下）的半径表：用户序「左上,右上,左下,右下」。</summary>
    public static (float X, float Y)[] RectPoints(SKRect rect) =>
    [
        (rect.Left, rect.Top),
        (rect.Right, rect.Top),
        (rect.Right, rect.Bottom),
        (rect.Left, rect.Bottom),
    ];

    /// <summary>用户序「左上,右上,左下,右下」→ 顶点序「左上,右上,右下,左下」；
    /// 缺省角重复最后一个给定值（只填一个值＝四角相同），非有限值按 0。</summary>
    public static double[] RectRadii(double fallback, double[]? perCorner)
    {
        double Last()
        {
            if (perCorner is not { Length: > 0 })
                return fallback;
            for (var i = perCorner.Length - 1; i >= 0; i--)
                if (Finite(perCorner[i]))
                    return perCorner[i];
            return 0;
        }

        var last = Last();
        double At(int i) => perCorner is { Length: > 0 } && i < perCorner.Length && Finite(perCorner[i]) ? perCorner[i] : last;
        return [At(0), At(1), At(3), At(2)]; // 顶点序 TL,TR,BR,BL
    }

    /// <summary>逐顶点半径：缺省顶点重复最后一个给定值（只填一个值＝全部相同），非有限值按 0。</summary>
    public static double[] VertexRadii(int count, double fallback, double[]? perCorner)
    {
        var last = fallback;
        if (perCorner is { Length: > 0 })
        {
            for (var i = perCorner.Length - 1; i >= 0; i--)
            {
                if (Finite(perCorner[i]))
                {
                    last = perCorner[i];
                    break;
                }
            }
        }

        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            var r = perCorner is { Length: > 0 } && i < perCorner.Length ? perCorner[i] : last;
            result[i] = Finite(r) && r > 0 ? r : 0;
        }

        return result;
    }

    const double Epsilon = 1e-6;

    static double Dist((float X, float Y) a, (float X, float Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>顶点 <paramref name="v"/> 处两邻边 (prev→v)/(v→next) 的夹角 (0..π)。</summary>
    static double AngleBetween((float X, float Y) prev, (float X, float Y) v, (float X, float Y) next)
    {
        var u = Unit(prev.X - v.X, prev.Y - v.Y);
        var w = Unit(next.X - v.X, next.Y - v.Y);
        var dot = u.X * w.X + u.Y * w.Y;
        if (dot > 1)
            dot = 1;
        else if (dot < -1)
            dot = -1;
        return Math.Acos(dot);
    }

    /// <summary>角平分线方向（两邻边单位向量之和归一化）；共线退化返回零向量。</summary>
    static (double X, double Y) Bisector((float X, float Y) prev, (float X, float Y) v, (float X, float Y) next)
    {
        var u = Unit(prev.X - v.X, prev.Y - v.Y);
        var w = Unit(next.X - v.X, next.Y - v.Y);
        return Unit(u.X + w.X, u.Y + w.Y);
    }

    static (float X, float Y) Unit(double x, double y)
    {
        var len = Math.Sqrt(x * x + y * y);
        return len < Epsilon ? (0f, 0f) : ((float)(x / len), (float)(y / len));
    }

    /// <summary>从 <paramref name="from"/> 朝 <paramref name="to"/> 走 <paramref name="distance"/>（from==to 返回 from）。</summary>
    static (float X, float Y) TangentPoint((float X, float Y) from, (float X, float Y) to, double distance)
    {
        var u = Unit(to.X - from.X, to.Y - from.Y);
        return (from.X + (float)(u.X * distance), from.Y + (float)(u.Y * distance));
    }

    /// <summary>点相对圆心的角度（度）；Skia Y 向下、正向顺时针（与 Cylinder 分支同口径）。</summary>
    static double Degrees((float X, float Y) p, (double X, double Y) center) =>
        Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI;

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
