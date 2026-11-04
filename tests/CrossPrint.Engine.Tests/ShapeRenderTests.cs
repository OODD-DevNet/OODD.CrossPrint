using System;
using System.Collections.Generic;
using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 形状元素渲染：全 Kind 出墨、填充/描边语义、缺省可见性、非有限几何整元素跳过。
/// </summary>
public sealed class ShapeRenderTests
{
    static long Ink(ShapeElement element) => Ink((PaperElement)element);

    static long Ink(PaperElement element)
    {
        using var bitmap = Render(element);
        var ink = 0L;
        for (var y = 0; y < 240; y++)
            for (var x = 0; x < 240; x++)
                // 半覆盖阈值计数：路径与 DrawRect 的亚像素 AA 残留（如 7% 灰的 #ededed 行）
                // 不算墨，否则面积比较会被整像素计权放大成 ±200 的噪声。
                if (bitmap.GetPixel(x, y) is { } p && p.Red + p.Green + p.Blue < 384)
                    ink++;
        return ink;
    }

    static bool IsWhite(PaperElement element, int x, int y) =>
        Render(element).GetPixel(x, y) is { Red: 255, Green: 255, Blue: 255 };

    static SKBitmap Render(PaperElement element)
    {
        var bitmap = new SKBitmap(240, 240);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var template = new PrintTemplate();
        template.Elements.Add(element);
        new TemplateRenderer().Render(template, canvas, new RenderOptions { Dpi = 96 }, null);
        canvas.Flush();
        return bitmap;
    }

    static ShapeElement Shape(ShapeKind kind, SKColor? fill = null, double x = 20, double y = 20, double w = 80, double h = 60, double[]? radii = null) =>
        new() { XMm = x, YMm = y, WidthMm = w, HeightMm = h, Shape = new PolygonOptions { Kind = kind, FillColor = fill, StrokeWidthMm = 0.3, CornerRadiiMm = radii } };

    public static IEnumerable<object[]> AllKinds()
    {
        foreach (ShapeKind kind in Enum.GetValues(typeof(ShapeKind)))
            yield return new object[] { kind };
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void 全部种类_默认描边出墨(ShapeKind kind)
    {
        // 描边与填充都不设也要有默认黑描边——静默空白比报错更难排查。
        Assert.True(Ink(Shape(kind)) > 0, $"种类 {kind} 拖进画布/打印必须可见");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void 填充比仅描边出墨多(ShapeKind kind)
    {
        Assert.True(Ink(Shape(kind, SKColors.Black)) > Ink(Shape(kind)), $"种类 {kind} 填充后墨面积应显著大于仅描边");
    }

    [Fact]
    public void 非有限几何_整元素跳过不抛()
    {
        var element = Shape(ShapeKind.Star5);
        element.WidthMm = double.NaN;
        var exception = Record.Exception(() => Ink(element));
        Assert.Null(exception);
        Assert.Equal(0, Ink(element));
    }

    [Fact]
    public void 圆角矩形_圆角夹短边一半_不静默消失()
    {
        // 圆角半径超过短边一半被夹取，仍必须出墨。
        var element = Shape(ShapeKind.RoundRect);
        element.Shape.CornerRadiusMm = 1000;
        Assert.True(Ink(element) > 0);
    }

    [Fact]
    public void 顶点表_全部种类至少三个点()
    {
        foreach (ShapeKind kind in Enum.GetValues(typeof(ShapeKind)))
        {
            if (kind is ShapeKind.RoundRect or ShapeKind.Cylinder)
                continue; // 特殊路径分支
            var vertices = ShapeRenderer.Vertices(kind);
            Assert.NotNull(vertices);
            Assert.True(vertices!.Length >= 3, $"{kind} 至少是三角形");
        }
    }

    // ---------------- 逐角圆角 ----------------

    static RectangleElement Rect(double[]? cornerRadiiMm = null, double cornerRadiusMm = 0) =>
        new()
        {
            // 240px @96DPI ≈ 63mm：矩形必须完整落在位图内，镜像对称断言才成立（不能裁边）。
            XMm = 10, YMm = 20, WidthMm = 50, HeightMm = 30,
            Rectangle = new ShapeOptions { FillColor = SKColors.Black, CornerRadiusMm = cornerRadiusMm, CornerRadiiMm = cornerRadiiMm },
        };

    [Fact]
    public void 形状矩形种类_逐角圆角_与直角不同()
    {
        // 新增的 Rectangle 种类：顶点序＝左上,右上,右下,左下，逐角输入序「左上,右上,左下,右下」直接映射。
        // 面积下限防"路径缩水成小点"型 bug（曾把归一化顶点当 mm 坐标用，形状缩成 1mm 仍假绿）。
        var sharp = Ink(Shape(ShapeKind.Rectangle, SKColors.Black));
        var rounded = Ink(Shape(ShapeKind.Rectangle, SKColors.Black, radii: [5, 0, 0, 5])); // 左上 + 右下

        Assert.True(rounded > sharp * 0.8, $"逐角圆角不应大幅缩水，实际 {rounded} vs {sharp}");
        Assert.True(rounded < sharp);
    }

    [Fact]
    public void 矩形逐角_单角圆角_出墨少于直角()
    {
        // 左上圆角削掉填充角料，墨面积应严格小于全直角。
        var rounded = Ink(Rect([6, 0, 0, 0]));
        var sharp = Ink(Rect());
        Assert.True(rounded < sharp, $"单角圆角 {rounded} 应小于直角 {sharp}");
    }

    [Fact]
    public void 矩形逐角_镜像半径_削角位置互为镜像()
    {
        // 采样点取角内 3px 处（6mm 圆角≈22.7px，该点距圆心 >r，在削掉的角料区）。
        // 序「左上,右上,左下,右下」→「左上 6」只削左上、右下仍是填充；「右下 6」相反。
        var tl = Rect([6, 0, 0, 0]);
        var br = Rect([0, 0, 0, 6]);

        Assert.True(IsWhite(tl, 41, 79), "左上圆角应在左上角削出背景");
        Assert.False(IsWhite(br, 41, 79), "右下圆角时左上角应是填充");
        Assert.False(IsWhite(tl, 224, 186), "左上圆角时右下角应是填充");
        Assert.True(IsWhite(br, 224, 186), "右下圆角应在右下角削出背景");
    }

    [Fact]
    public void 矩形逐角_四角全圆_削角对称且面积接近直角()
    {
        // 切点/圆弧错位类 bug 的死守：四角全圆时墨面积必须接近直角版本（只削少量角料），
        // 且四个角削角处都是背景。曾有切点量距方向反了的 bug，单角像素恰好漏过、四角全圆才现形。
        var sharp = Ink(Rect());
        var rounded = Ink(Rect([6, 6, 6, 6]));

        Assert.True(rounded > sharp * 0.8, $"四角 6mm 圆角只应削掉少量角料，实际 {rounded} vs {sharp}");
        Assert.True(IsWhite(Rect([6, 6, 6, 6]), 41, 79), "左上");
        Assert.True(IsWhite(Rect([6, 6, 6, 6]), 224, 79), "右上");
        Assert.True(IsWhite(Rect([6, 6, 6, 6]), 41, 186), "左下");
        Assert.True(IsWhite(Rect([6, 6, 6, 6]), 224, 186), "右下");
    }

    [Fact]
    public void 矩形逐角_只填一个值_四角相同()
    {
        // 序「左上,右上,左下,右下」→ 顶点序 TL,TR,BR,BL；缺省角重复最后一个给定值。
        var radii = CornerRounding.RectRadii(0, [4]);
        Assert.Equal(new[] { 4d, 4, 4, 4 }, radii);

        // "2,0" = 左上圆角、其余直角（显式 0 生效，后续缺省角重复 0）。
        radii = CornerRounding.RectRadii(0, [2, 0]);
        Assert.Equal(new[] { 2d, 0, 0, 0 }, radii);

        // null = 全部标量（旧模板兼容路径）。
        Assert.Equal(new[] { 2d, 2, 2, 2 }, CornerRounding.RectRadii(2, null));
    }

    [Fact]
    public void 圆角矩形_逐角全零_压过标量圆角()
    {
        var scalar = Shape(ShapeKind.RoundRect, SKColors.Black);
        scalar.Shape.CornerRadiusMm = 6;

        var allZero = Shape(ShapeKind.RoundRect, SKColors.Black);
        allZero.Shape.CornerRadiusMm = 6;
        allZero.Shape.CornerRadiiMm = [0, 0, 0, 0];

        // 逐角显式全零是直角矩形，填充面积应大于带 6mm 圆角的版本。
        Assert.True(Ink(allZero) > Ink(scalar));
    }

    [Fact]
    public void 三角形_逐角圆角_出墨少于直角()
    {
        var sharp = Ink(Shape(ShapeKind.Triangle, SKColors.Black));
        var rounded = Ink(Shape(ShapeKind.Triangle, SKColors.Black, radii: [5, 3, 3])); // 顶点序＝上,右下,左下

        Assert.True(rounded > sharp * 0.7, $"三角形逐角圆角不应大幅缩水，实际 {rounded} vs {sharp}");
        Assert.True(rounded < sharp);
    }

    [Fact]
    public void 逐角超大半径_夹取不崩不消失()
    {
        // 半径远超短边一半：逐角夹到相邻半边长，绝不静默画空。
        var rect = Rect([1000, 0, 1000, 0]);
        Assert.True(Ink(rect) > 0);
        var exception = Record.Exception(() => Ink(rect));
        Assert.Null(exception);

        var triangle = Shape(ShapeKind.Triangle, SKColors.Black);
        triangle.Shape.CornerRadiiMm = [500, 500, 500];
        Assert.True(Ink(triangle) > 0);
    }

    [Fact]
    public void 逐角半径_只填一个值_全部顶点相同()
    {
        // 三角形顶点序＝上,右下,左下；只给一个值 → 全部顶点相同。
        var radii = CornerRounding.VertexRadii(3, 0, [4]);
        Assert.Equal(new[] { 4d, 4, 4 }, radii);
    }
}
