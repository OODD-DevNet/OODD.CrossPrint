using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 叠加层的绘制参数。默认值就是"能直接用的样子"：浅灰工作区、白纸、5mm 网格、
/// 蓝色选中框与安全区虚线。宿主只改它想改的。
/// </summary>
public sealed class EditorRendererOptions
{
    /// <summary>是否画页内网格。</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>网格基准步长（mm）。缩放很小时按整数倍放大到屏幕上看不清为止，见 <see cref="EditorRenderer"/>。</summary>
    public double GridStepMm { get; set; } = 5;

    /// <summary>是否画标尺。</summary>
    public bool ShowRulers { get; set; } = true;

    /// <summary>标尺条宽度（宿主设备 px）。</summary>
    public double RulerSizeHostPx { get; set; } = 18;

    /// <summary>是否画页边距安全区虚线。</summary>
    public bool ShowMarginGuides { get; set; } = true;

    /// <summary>手柄边长（宿主设备 px）。不随缩放变——放大后手柄跟着变大就点不准了。</summary>
    public double HandleSizeHostPx { get; set; } = 8;

    /// <summary>页外工作区底色。</summary>
    public SKColor WorkspaceColor { get; set; } = new(0xB4, 0xB4, 0xB4);

    /// <summary>纸张底色。叠加层先铺它，文档再画在上面——预览的"纸白"由此保证。</summary>
    public SKColor PageColor { get; set; } = SKColors.White;

    /// <summary>页框颜色。</summary>
    public SKColor PageBorderColor { get; set; } = new(0x77, 0x77, 0x77);

    /// <summary>网格线颜色，必须足够浅才不干扰看内容。</summary>
    public SKColor GridColor { get; set; } = new(0xE4, 0xE4, 0xE4);

    /// <summary>安全区虚线颜色。</summary>
    public SKColor MarginGuideColor { get; set; } = new(0x3A, 0x9B, 0xFF);

    /// <summary>选中框与手柄描边颜色。</summary>
    public SKColor SelectionColor { get; set; } = new(0x1E, 0x88, 0xE5);

    /// <summary>放置幽灵框的填充色（半透明）。</summary>
    public SKColor GhostColor { get; set; } = new(0x1E, 0x88, 0xE5, 0x44);

    /// <summary>标尺底色。</summary>
    public SKColor RulerColor { get; set; } = new(0xF3, 0xF3, 0xF3);

    /// <summary>标尺刻度与数字颜色。</summary>
    public SKColor RulerInkColor { get; set; } = new(0x55, 0x55, 0x55);

    /// <summary>是否画滚动条（内容超出视口的轴才画）。内核按同一套几何做命中，见 <see cref="EditorScrollBars"/>。</summary>
    public bool ShowScrollBars { get; set; } = true;

    /// <summary>滚动条厚度（宿主设备 px）。绘制与命中判定共用这一个数——<see cref="EditorCanvas.Draw"/>
    /// 每帧把它写进 <see cref="EditorKernel.ScrollBarThicknessHostPx"/>，改它两边一起跟着变。</summary>
    public double ScrollBarThicknessHostPx { get; set; } = EditorScrollBars.DefaultThicknessHostPx;

    /// <summary>滚动条轨道底色。</summary>
    public SKColor ScrollBarTrackColor { get; set; } = new(0xE2, 0xE2, 0xE2);

    /// <summary>滚动条滑块颜色。</summary>
    public SKColor ScrollBarThumbColor { get; set; } = new(0x9B, 0x9B, 0x9B);
}

/// <summary>
/// 编辑器叠加层：页框、安全区、网格、标尺、选中框、八手柄、放置幽灵。
/// <b>只画"编辑器自己的东西"</b>，文档内容由宿主编排（见下面的绘制契约）。
/// </summary>
/// <remarks>
/// <para><b>绘制契约（宿主照抄这段，预览与导出才会是同一张图）：</b></para>
/// <code>
/// var overlay = new EditorRenderer();
/// overlay.RenderBackdrop(kernel, canvas);            // 宿主设备 px 空间
///
/// EditorRenderer.PushCamera(canvas, kernel);        // 平移 Pan
/// TemplateRenderer.Default.Render(kernel.Template, canvas, new RenderContext(
///     new RenderOptions { Dpi = kernel.Viewport.EffectiveRenderDpi }, report, images, data));
/// canvas.Restore();                                 // 回到宿主设备 px 空间
///
/// overlay.RenderForeground(kernel, canvas);
/// </code>
/// <para>
/// 关键在于 <c>Dpi = EffectiveRenderDpi = Dpi × Zoom</c> 配合只平移不缩放的相机：
/// mm 点映射到宿主像素的结果与 <see cref="Viewport.ToHostPx"/> 完全一致，
/// 所以叠加层在设备 px 空间画的框与引擎画出来的内容天然重合，不需要任何"补偿系数"。
/// </para>
/// <para>
/// 分两次画是因为文档必须夹在中间：页纸底色与网格在文档下面，选中框与手柄在文档上面。
/// </para>
/// </remarks>
public sealed class EditorRenderer
{
    /// <summary>绘制参数。</summary>
    public EditorRendererOptions Options { get; init; } = new();

    /// <summary>
    /// 压入"相机"变换：只平移 <see cref="Viewport.Pan"/>（96 基准 px → 宿主设备 px），不缩放。
    /// 缩放交给引擎的 <c>RenderOptions.Dpi</c>——两条链必须只承担一件事，否则叠加层与内容会慢慢错开。
    /// </summary>
    /// <param name="canvas">宿主设备 px 空间的画布。</param>
    /// <param name="kernel">内核。</param>
    /// <remarks>必须与 <c>canvas.Restore()</c> 成对使用。</remarks>
    public static void PushCamera(SKCanvas canvas, EditorKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(kernel);

        var k = kernel.Viewport.Dpi / Viewport.BaseDpi;
        canvas.Save();
        canvas.Translate((float)(kernel.Viewport.Pan.X * k), (float)(kernel.Viewport.Pan.Y * k));
    }

    /// <summary>画页外工作区、页纸、页框、网格、安全区。必须在文档之前调用。</summary>
    /// <param name="kernel">内核。</param>
    /// <param name="canvas">宿主设备 px 空间的画布。</param>
    public void RenderBackdrop(EditorKernel kernel, SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(canvas);

        var page = PageRectHost(kernel);
        var bounds = canvas.LocalClipBounds;

        using (var paint = new SKPaint { Color = Options.WorkspaceColor, IsAntialias = false, Style = SKPaintStyle.Fill })
            canvas.DrawRect(bounds, paint);

        // 页影：白纸在灰底上自带一点投影，用户一眼能看出纸的边界在哪（只有纸才"是"输出）。
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 36), IsAntialias = false, Style = SKPaintStyle.Fill })
            canvas.DrawRect(SKRect.Create(page.Left + 2, page.Top + 2, page.Width, page.Height), shadow);

        using (var paper = new SKPaint { Color = Options.PageColor, IsAntialias = false, Style = SKPaintStyle.Fill })
            canvas.DrawRect(page, paper);

        if (Options.ShowGrid)
            DrawGrid(kernel, canvas, page);

        if (Options.ShowMarginGuides)
            DrawMarginGuides(kernel, canvas);

        using (var border = new SKPaint
               {
                   Color = Options.PageBorderColor,
                   IsAntialias = false,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
               })
            canvas.DrawRect(page, border);
    }

    /// <summary>画选中框、八手柄、放置幽灵、标尺。必须在文档之后调用（标尺要盖在内容上）。</summary>
    /// <param name="kernel">内核。</param>
    /// <param name="canvas">宿主设备 px 空间的画布。</param>
    public void RenderForeground(EditorKernel kernel, SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(canvas);

        var dpi = kernel.Viewport.Dpi;

        if (kernel.PlacingGhostMm is { } ghostMm)
            DrawGhost(kernel, canvas, ghostMm);

        var selected = kernel.SelectedElements;
        if (selected.Count > 0)
            DrawSelection(kernel, canvas, selected, dpi);

        if (Options.ShowRulers)
            DrawRulers(kernel, canvas);

        if (Options.ShowScrollBars)
            DrawScrollBars(kernel, canvas);
    }

    /// <summary>滚动条：画在标尺之上（贴视口右缘/底边的窄条），几何由 <see cref="EditorScrollBars"/> 统一计算。</summary>
    void DrawScrollBars(EditorKernel kernel, SKCanvas canvas)
    {
        var metrics = EditorScrollBars.Compute(kernel, Options.ScrollBarThicknessHostPx);

        using var track = new SKPaint { Color = Options.ScrollBarTrackColor, IsAntialias = false, Style = SKPaintStyle.Fill };
        using var thumb = new SKPaint { Color = Options.ScrollBarThumbColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        var radius = (float)(Options.ScrollBarThicknessHostPx / 2 - 1);

        if (metrics.Horizontal is { } horizontal)
        {
            canvas.DrawRect(horizontal.Track, track);
            canvas.DrawRoundRect(horizontal.Thumb, radius, radius, thumb);
        }

        if (metrics.Vertical is { } vertical)
        {
            canvas.DrawRect(vertical.Track, track);
            canvas.DrawRoundRect(vertical.Thumb, radius, radius, thumb);
        }
    }

    /// <summary>页面矩形（宿主设备 px）。</summary>
    /// <param name="kernel">内核。</param>
    /// <returns>页框矩形。</returns>
    public static SKRect PageRectHost(EditorKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var mm = SKRect.Create(
            0,
            0,
            (float)kernel.Template.Paper.PageWidthMm,
            (float)kernel.PageHeightMm);
        return kernel.Viewport.ToHostRect(mm, kernel.Viewport.Dpi);
    }

    void DrawGrid(EditorKernel kernel, SKCanvas canvas, SKRect page)
    {
        var pageWidth = kernel.Template.Paper.PageWidthMm;
        var pageHeight = kernel.PageHeightMm;
        var step = EffectiveGridStepMm(kernel, Options.GridStepMm);
        if (step <= 0 || pageWidth <= 0 || pageHeight <= 0)
            return;

        canvas.Save();
        canvas.ClipRect(page);
        using var paint = new SKPaint
        {
            Color = Options.GridColor,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };

        var vp = kernel.Viewport;
        for (var x = step; x < pageWidth - 1e-9; x += step)
        {
            var hostX = (float)vp.ToHostPx(x, 0, vp.Dpi).X;
            canvas.DrawLine(hostX, page.Top, hostX, page.Bottom, paint);
        }

        for (var y = step; y < pageHeight - 1e-9; y += step)
        {
            var hostY = (float)vp.ToHostPx(0, y, vp.Dpi).Y;
            canvas.DrawLine(page.Left, hostY, page.Right, hostY, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// 网格实际步长：缩到 5mm 不足 6 个宿主像素时按 2 倍往上翻。
    /// 不翻的话 5% 缩放下一整页会刷成实心灰，网格反而成了噪声。
    /// </summary>
    static double EffectiveGridStepMm(EditorKernel kernel, double baseStepMm)
    {
        if (baseStepMm <= 0)
            return 0;

        var pxPerMm = kernel.Viewport.EffectiveRenderDpi / UnitConverter.MmPerInch;
        var step = baseStepMm;
        while (step * pxPerMm < 6 && step < 10_000)
            step *= 2;

        return step;
    }

    void DrawMarginGuides(EditorKernel kernel, SKCanvas canvas)
    {
        var margins = kernel.Template.Margins;
        var pageWidth = kernel.Template.Paper.PageWidthMm;
        var pageHeight = kernel.PageHeightMm;

        var inner = SKRect.Create(
            (float)margins.Left,
            (float)margins.Top,
            (float)Math.Max(0, pageWidth - margins.Left - margins.Right),
            (float)Math.Max(0, pageHeight - margins.Top - margins.Bottom));
        if (inner.Width <= 0 || inner.Height <= 0)
            return;

        var host = kernel.Viewport.ToHostRect(inner, kernel.Viewport.Dpi);
        using var dash = SKPathEffect.CreateDash([4f, 3f], 0);
        using var paint = new SKPaint
        {
            Color = Options.MarginGuideColor,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            PathEffect = dash,
        };
        canvas.DrawRect(host, paint);
    }

    void DrawGhost(EditorKernel kernel, SKCanvas canvas, SKRect ghostMm)
    {
        var host = kernel.Viewport.ToHostRect(ghostMm, kernel.Viewport.Dpi);
        using (var fill = new SKPaint { Color = Options.GhostColor, IsAntialias = false, Style = SKPaintStyle.Fill })
            canvas.DrawRect(host, fill);

        using var dash = SKPathEffect.CreateDash([4f, 3f], 0);
        using var stroke = new SKPaint
        {
            Color = Options.SelectionColor,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            PathEffect = dash,
        };
        canvas.DrawRect(host, stroke);
    }

    void DrawSelection(EditorKernel kernel, SKCanvas canvas, IReadOnlyList<PaperElement> selected, double dpi)
    {
        var vp = kernel.Viewport;

        // 多选时每个元素画细虚线框，外接框画实线——用户得能分清"选了哪几个"与"整体多大"。
        if (selected.Count > 1)
        {
            using var dash = SKPathEffect.CreateDash([3f, 3f], 0);
            using var thin = new SKPaint
            {
                Color = Options.SelectionColor.WithAlpha(0xA0),
                IsAntialias = false,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                PathEffect = dash,
            };
            foreach (var element in selected)
                canvas.DrawRect(vp.ToHostRect(ElementHitTester.HitRectMm(element), dpi), thin);
        }

        var box = ElementHitTester.UnionBox(selected);
        var hostBox = vp.ToHostRect(box.ToSkRect(), dpi);

        using (var outline = new SKPaint
               {
                   Color = Options.SelectionColor,
                   IsAntialias = false,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
               })
            canvas.DrawRect(hostBox, outline);

        var size = (float)Options.HandleSizeHostPx;
        var half = size / 2;
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        using var handleStroke = new SKPaint
        {
            Color = Options.SelectionColor,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };

        foreach (var (_, xMm, yMm) in ElementHitTester.HandleAnchorsMm(box))
        {
            var anchor = vp.ToHostPx(xMm, yMm, dpi);
            var rect = SKRect.Create((float)(anchor.X - half), (float)(anchor.Y - half), size, size);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, handleStroke);
        }
    }

    void DrawRulers(EditorKernel kernel, SKCanvas canvas)
    {
        var bounds = canvas.LocalClipBounds;
        var band = (float)Options.RulerSizeHostPx;
        if (band <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using (var bg = new SKPaint { Color = Options.RulerColor, IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(SKRect.Create(bounds.Left, bounds.Top, bounds.Width, band), bg);
            canvas.DrawRect(SKRect.Create(bounds.Left, bounds.Top, band, bounds.Height), bg);
        }

        var step = RulerStepMm(kernel);
        var pageWidth = kernel.Template.Paper.PageWidthMm;
        var pageHeight = kernel.PageHeightMm;
        var vp = kernel.Viewport;

        using var tick = new SKPaint { Color = Options.RulerInkColor, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var label = new SKPaint
        {
            Color = Options.RulerInkColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Typeface = SKTypeface.Default,
            TextSize = 8f,
            TextAlign = SKTextAlign.Left,
        };

        var major = step * 5;

        // 横向标尺：刻度跟着相机动，数字每 5 小格标一次。
        for (var mm = 0.0; mm <= pageWidth + 1e-9; mm += step)
        {
            var x = (float)vp.ToHostPx(mm, 0, vp.Dpi).X;
            if (x < band)
                continue;

            var isMajor = Math.Abs(mm / major - Math.Round(mm / major)) < 1e-9;
            var height = isMajor ? band * 0.6f : band * 0.3f;
            canvas.DrawLine(x, band - height, x, band, tick);

            if (isMajor)
                canvas.DrawText(FormatMm(mm), x + 2, band * 0.55f, label);
        }

        // 纵向标尺：数字旋转 90° 横着排，跟主流设计器一致。
        for (var mm = 0.0; mm <= pageHeight + 1e-9; mm += step)
        {
            var y = (float)vp.ToHostPx(0, mm, vp.Dpi).Y;
            if (y < band)
                continue;

            var isMajor = Math.Abs(mm / major - Math.Round(mm / major)) < 1e-9;
            var width = isMajor ? band * 0.6f : band * 0.3f;
            canvas.DrawLine(band - width, y, band, y, tick);

            if (isMajor)
            {
                canvas.Save();
                canvas.Translate(band * 0.55f, y + 2);
                canvas.RotateDegrees(90);
                canvas.DrawText(FormatMm(mm), 0, 0, label);
                canvas.Restore();
            }
        }

        using var separator = new SKPaint
        {
            Color = Options.PageBorderColor,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        canvas.DrawLine(bounds.Left, bounds.Top + band, bounds.Right, bounds.Top + band, separator);
        canvas.DrawLine(bounds.Left + band, bounds.Top, bounds.Left + band, bounds.Bottom, separator);

        // 左上角的"原点块"：两条标尺在这个方块上汇合，否则会互相压掉一截刻度。
        using var corner = new SKPaint { Color = Options.RulerColor, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawRect(SKRect.Create(bounds.Left, bounds.Top, band, band), corner);
        canvas.DrawLine(bounds.Left, bounds.Top + band, bounds.Left + band, bounds.Top + band, separator);
        canvas.DrawLine(bounds.Left + band, bounds.Top, bounds.Left + band, bounds.Top + band, separator);
    }

    /// <summary>标尺步长：屏幕上至少 8 个宿主像素一格，否则毫米刻度会糊成一条实线。</summary>
    static double RulerStepMm(EditorKernel kernel)
    {
        var pxPerMm = kernel.Viewport.EffectiveRenderDpi / UnitConverter.MmPerInch;
        foreach (var candidate in new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0, 200.0, 500.0 })
        {
            if (candidate * pxPerMm >= 8)
                return candidate;
        }

        return 1000.0;
    }

    static string FormatMm(double mm) => mm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
