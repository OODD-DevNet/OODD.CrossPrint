using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Engine.Rendering;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 宿主画布的一次完整绘制：<b>叠加层底 → 相机 + 文档 → 叠加层顶</b>。
/// </summary>
/// <remarks>
/// <para>
/// 这段顺序就是架构 §4.1 的两遍叠加契约。六个宿主各自抄一遍必然漂移（漏掉 <c>Restore</c>、
/// 把 <c>Dpi</c> 写成控件 DPI 而不是 <see cref="Viewport.EffectiveRenderDpi"/>），
/// 所以把它固化成内核里唯一的编排入口：宿主只负责给出<b>宿主设备 px 尺寸</b>与 DPI，
/// 剩下的换算全部在这里发生。
/// </para>
/// <para>
/// 这个方法会写 <see cref="EditorKernel.ViewWidthHostPx"/>、
/// <see cref="EditorKernel.ViewHeightHostPx"/>、<see cref="Viewport.Dpi"/> 与
/// <see cref="EditorKernel.ScrollBarThicknessHostPx"/>——
/// 这几个值本来就只能由"当前这次布局"回答，而绘制是唯一知道它们的时刻。
/// </para>
/// </remarks>
public static class EditorCanvas
{
    /// <summary>绘制一帧。</summary>
    /// <param name="canvas">目标画布，单位必须是<b>宿主设备 px</b>。</param>
    /// <param name="kernel">编辑器内核。</param>
    /// <param name="overlay">叠加层；<c>null</c> 时只画文档（预览用）。</param>
    /// <param name="widthHostPx">控件宽（宿主设备 px）。</param>
    /// <param name="heightHostPx">控件高（宿主设备 px）。</param>
    /// <param name="dpi">宿主逻辑 DPI（96×缩放），必须是有限正数：<see cref="Viewport.Dpi"/> 在入口拒收
    /// 0/NaN/Infinity，因为每次 mm 换算都要除以它，放进视口会把拖拽算出的坐标一路带进命令与 JSON。</param>
    /// <param name="data">数据绑定源；设计态传 <c>null</c>，预览态传样例数据。</param>
    /// <param name="fontResolver">字体解析器；<c>null</c> 用系统默认。</param>
    /// <param name="images">图片解析器；<c>null</c> 表示只有内嵌图片可用。</param>
    /// <param name="renderer">渲染器；<c>null</c> 用共享默认实例。</param>
    /// <returns>本次绘制产生的降级告警。</returns>
    public static RenderReport Draw(
        SKCanvas canvas,
        EditorKernel kernel,
        EditorRenderer? overlay,
        double widthHostPx,
        double heightHostPx,
        double dpi,
        object? data = null,
        IFontResolver? fontResolver = null,
        IImageResolver? images = null,
        TemplateRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(kernel);

        kernel.ViewWidthHostPx = widthHostPx;
        kernel.ViewHeightHostPx = heightHostPx;
        kernel.Viewport.Dpi = dpi;

        // 滚动条画多粗就由这一帧定：内核按同一个厚度做命中，否则画出来的滑块抓不住、
        // 画窄了的隐形窄条还在抢元素的点击（见 EditorScrollBars 的共用几何契约）。
        if (overlay is { } layer)
            kernel.ScrollBarThicknessHostPx = layer.Options.ScrollBarThicknessHostPx;

        overlay?.RenderBackdrop(kernel, canvas);

        // 设计画布的取值优先级：元素「测试数据」> 宿主数据 > 模板设计数据。
        // 用户在属性面板输入测试值就是要在画布上看到它，所以测试数据最优先（PreferTestData）；
        // 没输测试值时宿主显式传入的数据优先，模板内嵌设计数据只补缺——
        // 否则 {{字段}} 全部绑定成空串，条码会画成一小段无意义图案、文本整块消失。
        var effectiveData = DataBinder.WithDesignData(data, kernel.Template.BuildDesignBindingData());

        var saved = canvas.Save();
        var report = new RenderReport();
        try
        {
            EditorRenderer.PushCamera(canvas, kernel);
            (renderer ?? TemplateRenderer.Default).Render(kernel.Template, canvas, new RenderContext(
                new RenderOptions { Dpi = kernel.Viewport.EffectiveRenderDpi },
                report,
                fontResolver,
                images,
                effectiveData,
                preferTestData: true));
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }

        overlay?.RenderForeground(kernel, canvas);
        return report;
    }
}
