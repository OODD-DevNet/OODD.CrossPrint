using System.Text.Json.Serialization;
using SkiaSharp;

namespace CrossPrint.Core;

/// <summary>
/// 页面文字水印：启用后按网格平铺斜纹文字，编辑态画布与打印/导出（走
/// <c>CrossPrint.Engine.TemplateRenderer</c> 的所有链路）表现一致。
/// </summary>
public sealed class WatermarkOptions
{
    /// <summary>是否启用水印。</summary>
    public bool Enabled { get; set; }

    /// <summary>水印文字；空白时即使启用也不绘制。支持 <c>{{字段}}</c> 占位符。</summary>
    public string Text { get; set; } = "内部资料";

    /// <summary>
    /// 字段绑定（hiprint field 语义）：纯字段名或 <c>{{路径}}</c> 皆可，绑定值优先于文字内容，
    /// 与元素的 <c>Binding</c> 同一套引擎端 <c>BindContent</c> 口径；null = 未绑定。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Binding { get; set; }

    /// <summary>
    /// 测试数据：设计画布（preferTestData）优先显示；真实打印时仅作"数据缺字段"的兜底值，
    /// 与元素级 <c>TestData</c> 同语义；null = 未设置。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestData { get; set; }

    /// <summary>字号（磅）。</summary>
    public double FontSizePt { get; set; } = 14;

    /// <summary>水印颜色（含透明度分量时与 <see cref="Opacity"/> 相乘）。</summary>
    [JsonConverter(typeof(SkColorJsonConverter))]
    public SKColor Color { get; set; } = new(0x33, 0x33, 0x33);

    /// <summary>整体不透明度（0-1，默认 0.2）。曾默认 0.1——单层绘制后白纸上肉眼几乎不可见，被当成"水印坏了"。</summary>
    public double Opacity { get; set; } = 0.2;

    /// <summary>旋转角（度，-90~90，默认 -30 斜纹）。</summary>
    public double RotationDeg { get; set; } = -30;

    /// <summary>平铺间距（mm，相邻两枚水印文字之间的空隙）。</summary>
    public double SpacingMm { get; set; } = 60;

    /// <summary>深拷贝。</summary>
    public WatermarkOptions Clone() => new()
    {
        Enabled = Enabled,
        Text = Text,
        Binding = Binding,
        TestData = TestData,
        FontSizePt = FontSizePt,
        Color = Color,
        Opacity = Opacity,
        RotationDeg = RotationDeg,
        SpacingMm = SpacingMm,
    };
}
