namespace CrossPrint.Core;

using System.Text.Json.Serialization;
using SkiaSharp;

/// <summary>
/// 页面元素基类。几何量一律毫米（mm），相对页面左上角。
/// </summary>
public abstract class PaperElement
{
    /// <summary>模板内唯一标识，由编辑器内核生成并维护。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>序列化判别符，如 <c>"text"</c>。由各具体类型固定。</summary>
    public abstract string TypeKey { get; }

    /// <summary>元素左上角横坐标（mm，相对页面左边）。</summary>
    public double XMm { get; set; }

    /// <summary>元素左上角纵坐标（mm，相对页面顶边）。</summary>
    public double YMm { get; set; }

    /// <summary>元素宽度（mm）。线段水平时即为线长。</summary>
    public double WidthMm { get; set; }

    /// <summary>元素高度（mm）。线段垂直时即为线长。</summary>
    public double HeightMm { get; set; }

    /// <summary>绘制次序，升序；同值按模板数组顺序。</summary>
    public int ZIndex { get; set; }

    /// <summary>元素框底色，null 表示透明。先于元素内容绘制，覆盖整个包围盒。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BackgroundColor { get; set; }

    /// <summary>
    /// 引用模板样式库（<see cref="PrintTemplate.Styles"/>）里的命名样式 id。
    /// 样式为底色/边框族提供默认值：内联字段非 null 时优先（CSS 同款覆盖口径），
    /// 引用的样式不存在时静默忽略（只影响外观，不报错）。
    /// </summary>
    public string? StyleId { get; set; }

    /// <summary>元素框边框色，null 表示不画边框。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor? BorderColor { get; set; }

    /// <summary>元素框边框宽（mm），仅 <see cref="BorderColor"/> 非空时生效。</summary>
    public double BorderWidthMm { get; set; } = 0.2;

    /// <summary>元素框四边中需要描边的边。hiprint 的 borderTop/Left/Right/Bottom 映射到这里。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BorderSide>))]
    public BorderSide BorderSides { get; set; } = BorderSide.All;

    /// <summary>元素框边框虚线段长（mm）循环，null 为实线。</summary>
    public double[]? BorderDashPatternMm { get; set; }

    /// <summary>绕元素中心旋转的角度（度，顺时针为正）。0 表示不旋转。</summary>
    public double RotationDegrees { get; set; }

    /// <summary>整体不透明度（0..1）；越界值在渲染期夹取，1 表示完全不透明。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    /// 格式化脚本源码（JavaScript，Jint 执行），hiprint <c>formatter</c> 同款签名：
    /// <c>function(value, options, target, templateData)</c>。value 为元素主绑定字段的
    /// 原始值（无绑定为 null），返回值转成字符串替换显示内容：文本/表格列改单元格文本、
    /// 码类改编码内容、图片改 src。null/空白表示没有脚本。
    /// </summary>
    public string? Formatter { get; set; }

    /// <summary>
    /// 样式脚本源码（JavaScript，Jint 执行），hiprint <c>styler</c> 同款签名与语义：
    /// 返回样式对象（<c>{ color: 'red', backgroundColor: '#f5f5f5' }</c>）或 CSS 字符串，
    /// 解析出的样式以最高优先级并入该元素（脚本 &gt; 内联 &gt; 样式库）。解析失败或脚本报错
    /// 一律降级为"没有脚本"，渲染不中断。null/空白表示没有脚本。
    /// </summary>
    public string? Styler { get; set; }

    /// <summary>
    /// 元素级测试数据（设计/预览兜底值）：绑定占位符解析不到、模板设计数据也没有时，
    /// 用它替换该元素的占位符看排版效果（一个值对应元素内全部占位符）。表格元素按
    /// 「换行分行、制表符分列」解析成测试数据行。打印链路传入真实数据时本字段不参与。
    /// </summary>
    public string? TestData { get; set; }

    /// <summary>元素包围盒右下角横坐标（mm）。</summary>
    public double RightMm => XMm + WidthMm;

    /// <summary>元素包围盒右下角纵坐标（mm）。</summary>
    public double BottomMm => YMm + HeightMm;

    /// <summary>深拷贝，供撤销栈与批量渲染复用。</summary>
    public abstract PaperElement CloneDeep();

    /// <summary>把 <paramref name="source"/> 的公共字段复制给自身。</summary>
    protected void CopyCommonFrom(PaperElement source)
    {
        Id = source.Id;
        XMm = source.XMm;
        YMm = source.YMm;
        WidthMm = source.WidthMm;
        HeightMm = source.HeightMm;
        ZIndex = source.ZIndex;
        StyleId = source.StyleId;
        BackgroundColor = source.BackgroundColor;
        BorderColor = source.BorderColor;
        BorderWidthMm = source.BorderWidthMm;
        BorderSides = source.BorderSides;
        BorderDashPatternMm = source.BorderDashPatternMm is null ? null : (double[])source.BorderDashPatternMm.Clone();
        RotationDegrees = source.RotationDegrees;
        Formatter = source.Formatter;
        Styler = source.Styler;
        Opacity = source.Opacity;
        TestData = source.TestData;
    }
}
