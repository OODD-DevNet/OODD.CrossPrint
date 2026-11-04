using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 形状元素渲染器：参数化多边形（Office「插入形状」语义）内切于元素矩形。
/// 顶点表按归一化 (0..1)² 定义，与宽高比无关（拉伸跟随元素框，与矩形/椭圆同一口径）；
/// 描边/填充级联与 RectangleRenderer 完全同源：脚本 styler 最高、内联次之、库样式打底。
/// </summary>
public sealed class ShapeRenderer : ElementRenderer<ShapeElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Shape;

    /// <inheritdoc/>
    protected override bool ClipsToElement => false;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, ShapeElement element, RenderContext context)
    {
        if (!Finite(element.XMm) || !Finite(element.YMm) || !Finite(element.WidthMm) || !Finite(element.HeightMm))
            return; // 非有限几何整元素跳过（与既有护栏同口径，绝不抛进渲染管线）

        var o = element.Shape;
        // 填充走样式的底色族、描边走边框族（与矩形/椭圆同款级联）
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element);
        var stroke = script?.BorderColor ?? script?.Color ?? StyleCascade.PickNullable(o.StrokeColor, SKColors.Black, named?.BorderColor);
        var strokeWidth = script?.BorderWidthMm ?? StyleCascade.Pick(o.StrokeWidthMm, 0.2, named?.BorderWidthMm);
        var fill = script?.BackgroundColor ?? o.FillColor ?? named?.BackgroundColor;

        var rect = Geometry.InsetByStroke(
            new SKRect((float)element.XMm, (float)element.YMm, (float)element.RightMm, (float)element.BottomMm),
            strokeWidth, stroke is not null);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var path = BuildPath(o.Kind, rect, o.CornerRadiusMm, o.CornerRadiiMm);
        if (path is null)
            return;

        if (fill is { } fillColor)
        {
            using var fillPaint = Paints.Fill(fillColor);
            canvas.DrawPath(path, fillPaint);
        }

        // 描边与填充都不设时按默认黑描边画——拖进画布/印出来总得可见，静默空白比报错更难排查。
        if ((stroke is { } strokeColor && strokeWidth > 0) || (fill is null && stroke is null))
        {
            var effectiveStroke = stroke ?? SKColors.Black;
            var effectiveWidth = strokeWidth > 0 ? strokeWidth : 0.2;
            var dash = script?.BorderDashPatternMm ?? o.DashPatternMm ?? named?.BorderDashPatternMm;
            using var strokePaint = Paints.Stroke(effectiveStroke, effectiveWidth, dash);
            canvas.DrawPath(path, strokePaint);
        }
    }

    /// <summary>net461 无 double.IsFinite：NaN 与 ±∞ 都算非有限。</summary>
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>按种类构造元素矩形内的路径；未实现的种类返回 null（渲染期静默跳过）。</summary>
    internal static SKPath? BuildPath(ShapeKind kind, SKRect rect, double cornerRadiusMm)
        => BuildPath(kind, rect, cornerRadiusMm, null);

    /// <summary>同上，支持逐顶点圆角（<c>PolygonOptions.CornerRadiiMm</c>）。</summary>
    internal static SKPath? BuildPath(ShapeKind kind, SKRect rect, double cornerRadiusMm, double[]? cornerRadiiMm)
    {
        var path = new SKPath();
        switch (kind)
        {
            case ShapeKind.RoundRect:
            {
                if (cornerRadiiMm is { Length: > 0 })
                {
                    // 逐角模式：序「左上,右上,左下,右下」，缺省角沿用标量；显式 0 = 直角（压过标量）。
                    path.Dispose();
                    var radii = CornerRounding.RectRadii(cornerRadiusMm, cornerRadiiMm);
                    if (radii[0] <= 0 && radii[1] <= 0 && radii[2] <= 0 && radii[3] <= 0)
                        return SharpRect(rect);
                    return CornerRounding.Build(CornerRounding.RectPoints(rect), radii) ?? SharpRect(rect);
                }

                // 圆角夹到短边一半，否则 Skia 会把整条路径判空（画不出也不报错，属静默失败）
                var radius = (float)Math.Max(0, Math.Min(Math.Min(rect.Width, rect.Height) / 2, cornerRadiusMm));
                path.AddRoundRect(rect, Math.Max(radius, 0.01f), Math.Max(radius, 0.01f), SKPathDirection.Clockwise);
                return path;
            }

            case ShapeKind.Cylinder:
            {
                // 圆柱：上下椭圆盖（顶盖前缘进路径）+ 两竖边。椭圆高固定占元素高 1/4。
                // Skia 角度 0°=右、正向顺时针（Y 向下）：底弧走 180°→-180°（经 90° 底部），
                // 顶盖前缘走 0°→180°（同样经底部）。
                var e = rect.Height * 0.25f;
                var e2 = e / 2;
                path.MoveTo(rect.Left, rect.Top + e2);
                path.LineTo(rect.Left, rect.Bottom - e2);
                path.AddArc(new SKRect(rect.Left, rect.Bottom - e, rect.Right, rect.Bottom), 180, -180);
                path.LineTo(rect.Right, rect.Top + e2);
                path.AddArc(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + e), 0, 180);
                path.Close();
                return path;
            }

            default:
            {
                var points = Vertices(kind);
                if (points is null || points.Length < 3)
                {
                    path.Dispose();
                    return null;
                }

                if (cornerRadiiMm is { Length: > 0 })
                {
                    // 逐顶点模式：矩形/圆角矩形序「左上,右上,左下,右下」，三角形序「上,右下,左下」＝顶点表顺序；
                    // 缺省顶点沿用标量（通常 0）。⚠ 顶点表是归一化 (0..1)²，必须先按元素矩形缩放成 mm 坐标。
                    var radii = CornerRounding.VertexRadii(points.Length, cornerRadiusMm, cornerRadiiMm);
                    foreach (var r in radii)
                    {
                        if (r > 0)
                        {
                            path.Dispose();
                            var scaled = new (float X, float Y)[points.Length];
                            for (var i = 0; i < points.Length; i++)
                                scaled[i] = (rect.Left + points[i].X * rect.Width, rect.Top + points[i].Y * rect.Height);
                            return CornerRounding.Build(scaled, radii) ?? PlainPolygon(points, rect);
                        }
                    }
                    // 逐顶点全零 → 落回直角多边形（显式 0 压过标量）。
                }

                for (var i = 0; i < points.Length; i++)
                {
                    var (nx, ny) = points[i];
                    var x = rect.Left + nx * rect.Width;
                    var y = rect.Top + ny * rect.Height;
                    if (i == 0)
                        path.MoveTo(x, y);
                    else
                        path.LineTo(x, y);
                }

                path.Close();
                return path;
            }
        }
    }

    /// <summary>直角矩形路径（逐角显式全零时压过标量圆角）。</summary>
    static SKPath SharpRect(SKRect rect)
    {
        var path = new SKPath();
        path.AddRect(rect);
        return path;
    }

    /// <summary>无圆角多边形路径（逐顶点全零时回落）。</summary>
    static SKPath PlainPolygon((float X, float Y)[] points, SKRect rect)
    {
        var path = new SKPath();
        for (var i = 0; i < points.Length; i++)
        {
            var x = rect.Left + points[i].X * rect.Width;
            var y = rect.Top + points[i].Y * rect.Height;
            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }

        path.Close();
        return path;
    }

    /// <summary>归一化顶点表 (0..1)²；箭头头部长占 1/2、杆宽占 3/10，Excel 同款比例。
    /// Rectangle 顶点序＝「左上,右上,右下,左下」，逐角圆角输入序「左上,右上,左下,右下」可直接映射。</summary>
    internal static (float X, float Y)[]? Vertices(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => [(0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f)],
        ShapeKind.Triangle => [(0.5f, 0f), (1f, 1f), (0f, 1f)],
        ShapeKind.RightTriangle => [(0f, 0f), (0f, 1f), (1f, 1f)],
        ShapeKind.Diamond => [(0.5f, 0f), (1f, 0.5f), (0.5f, 1f), (0f, 0.5f)],
        ShapeKind.Parallelogram => [(0.2f, 0f), (1f, 0f), (0.8f, 1f), (0f, 1f)],
        ShapeKind.Trapezoid => [(0.2f, 0f), (0.8f, 0f), (1f, 1f), (0f, 1f)],
        ShapeKind.Pentagon => Regular(5),
        ShapeKind.Hexagon => Regular(6),
        ShapeKind.Octagon => Regular(8),
        ShapeKind.Star5 => Star5(),
        ShapeKind.ArrowRight => [(0f, 0.3f), (0.5f, 0.3f), (0.5f, 0f), (1f, 0.5f), (0.5f, 1f), (0.5f, 0.7f), (0f, 0.7f)],
        ShapeKind.ArrowLeft => [(1f, 0.3f), (0.5f, 0.3f), (0.5f, 0f), (0f, 0.5f), (0.5f, 1f), (0.5f, 0.7f), (1f, 0.7f)],
        ShapeKind.ArrowUp => [(0.3f, 1f), (0.3f, 0.5f), (0f, 0.5f), (0.5f, 0f), (1f, 0.5f), (0.7f, 0.5f), (0.7f, 1f)],
        ShapeKind.ArrowDown => [(0.3f, 0f), (0.3f, 0.5f), (0f, 0.5f), (0.5f, 1f), (1f, 0.5f), (0.7f, 0.5f), (0.7f, 0f)],
        ShapeKind.Plus => [(0.35f, 0f), (0.65f, 0f), (0.65f, 0.35f), (1f, 0.35f), (1f, 0.65f), (0.65f, 0.65f),
                           (0.65f, 1f), (0.35f, 1f), (0.35f, 0.65f), (0f, 0.65f), (0f, 0.35f), (0.35f, 0.35f)],
        _ => null,
    };

    /// <summary>正 n 边形：中心 (0.5, 0.5)、外接圆半径 0.5、顶点朝上。</summary>
    static (float, float)[] Regular(int sides)
    {
        var points = new (float, float)[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * i / sides;
            points[i] = ((float)(0.5 + 0.5 * Math.Cos(angle)), (float)(0.5 + 0.5 * Math.Sin(angle)));
        }

        return points;
    }

    /// <summary>五角星：外半径 0.5 / 内半径 0.19 交替 10 点，顶点朝上。</summary>
    static (float, float)[] Star5()
    {
        const double outer = 0.5;
        const double inner = 0.19;
        var points = new (float, float)[10];
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = -Math.PI / 2 + Math.PI * i / 5;
            points[i] = ((float)(0.5 + radius * Math.Cos(angle)), (float)(0.5 + radius * Math.Sin(angle)));
        }

        return points;
    }
}
