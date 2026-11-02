using SkiaSharp;
using CrossPrint.Core;

namespace CrossPrint.Engine;

/// <summary>
/// 单类元素的渲染器。实现必须只依赖 mm 作图（画布已由 <see cref="TemplateRenderer"/> 施加 UnitsPerMm 缩放），
/// 不得自己乘 DPI，否则预览与导出会分叉。
/// </summary>
public interface IElementRenderer
{
    /// <summary>对应的元素类型键，见 <see cref="ElementTypeKeys"/>。</summary>
    string TypeKey { get; }

    /// <summary>
    /// 绘制元素。允许抛异常——<see cref="TemplateRenderer"/> 会捕获并记为
    /// <see cref="RenderIssueKind.ElementSkipped"/>，保证整页不中断。
    /// </summary>
    /// <param name="canvas">已缩放到 mm 用户空间的画布。</param>
    /// <param name="element">元素。</param>
    /// <param name="context">渲染上下文。</param>
    void Render(SKCanvas canvas, PaperElement element, RenderContext context);
}

/// <summary>
/// 泛型基类：负责强转与"是否裁到元素框内"，子类只画内容。
/// </summary>
/// <typeparam name="TElement">处理的元素类型。</typeparam>
public abstract class ElementRenderer<TElement> : IElementRenderer where TElement : PaperElement
{
    /// <inheritdoc/>
    public abstract string TypeKey { get; }

    /// <summary>
    /// 是否把绘制裁剪到元素矩形。图片/表格应当裁剪（溢出即物理裁切，所见即所得）；
    /// 文本取 false，因为 hiprint 的文本元素不裁剪，标点下沿与字形降部会落到框外；
    /// 线段与形状也取 false，否则半宽描边会被切掉外侧一半，线宽看起来缩水。
    /// </summary>
    protected virtual bool ClipsToElement => true;

    /// <inheritdoc/>
    public void Render(SKCanvas canvas, PaperElement element, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(context);

        if (element is not TElement typed)
            throw new InvalidOperationException($"渲染器 {GetType().Name} 处理 {typeof(TElement).Name}，却收到 {element.GetType().Name}。");

        var clip = new SKRect((float)element.XMm, (float)element.YMm, (float)element.RightMm, (float)element.BottomMm);
        var saved = canvas.Save();
        if (ClipsToElement && clip.Width > 0 && clip.Height > 0)
            canvas.ClipRect(clip);
        try
        {
            Draw(canvas, typed, context);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>在 mm 用户空间绘制。</summary>
    protected abstract void Draw(SKCanvas canvas, TElement element, RenderContext context);
}
