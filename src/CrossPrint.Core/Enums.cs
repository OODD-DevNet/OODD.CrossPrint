namespace CrossPrint.Core;

/// <summary>页面方向。Landscape 时渲染引擎交换纸张宽高。</summary>
public enum PaperOrientation
{
    /// <summary>纵向：纸张宽 &lt; 高。</summary>
    Portrait,

    /// <summary>横向：渲染时交换宽高。</summary>
    Landscape,
}

/// <summary>线段方向。</summary>
public enum LineDirection
{
    /// <summary>水平线，长度取元素宽度。</summary>
    Horizontal,

    /// <summary>垂直线，长度取元素高度。</summary>
    Vertical,
}

/// <summary>文本水平对齐。</summary>
public enum HorizontalAlign
{
    /// <summary>左对齐。</summary>
    Left,

    /// <summary>居中。</summary>
    Center,

    /// <summary>右对齐。</summary>
    Right,

    /// <summary>两端对齐：除最后一行外把多余宽度均摊到字间距（hiprint <c>text-align: justify</c>）。</summary>
    Justify,
}

/// <summary>元素框四边边框的开关位。默认四边全画。</summary>
[System.Flags]
public enum BorderSide
{
    /// <summary>不画任何边框。</summary>
    None = 0,

    /// <summary>上边。</summary>
    Top = 1,

    /// <summary>右边。</summary>
    Right = 2,

    /// <summary>下边。</summary>
    Bottom = 4,

    /// <summary>左边。</summary>
    Left = 8,

    /// <summary>四边全画。</summary>
    All = Top | Right | Bottom | Left,
}

/// <summary>文本垂直对齐。</summary>
public enum VerticalAlign
{
    /// <summary>顶部对齐。</summary>
    Top,

    /// <summary>垂直居中。</summary>
    Middle,

    /// <summary>底部对齐。</summary>
    Bottom,
}

/// <summary>位图在元素矩形内的适配方式。</summary>
public enum ImageFitMode
{
    /// <summary>等比缩放至完全可见，居中留白。</summary>
    Contain,

    /// <summary>等比缩放至完全覆盖，超出部分裁切。</summary>
    Cover,

    /// <summary>非等比拉伸填满。</summary>
    Fill,

    /// <summary>按原始像素尺寸（换算为 mm 后）居中，不缩放。</summary>
    None,
}

/// <summary>图片数据来源类型。</summary>
public enum ImageSourceType
{
    /// <summary>Base64 内嵌于模板 JSON。</summary>
    EmbeddedBase64,

    /// <summary>由宿主注入的 <c>IImageResolver</c> 按 key 取字节；宿主没提供解析器时降级为占位框。</summary>
    ResolverKey,
}

/// <summary>二维码纠错等级，等级越高可修复比例越大、密度越高。</summary>
public enum QrErrorCorrection
{
    /// <summary>约 7% 容错。</summary>
    L,

    /// <summary>约 15% 容错。</summary>
    M,

    /// <summary>约 25% 容错。</summary>
    Q,

    /// <summary>约 30% 容错。</summary>
    H,
}

/// <summary>一维码制式。</summary>
public enum BarcodeSymbology
{
    /// <summary>Code 128，支持全 ASCII。</summary>
    Code128,

    /// <summary>EAN-13，需 12 或 13 位数字。</summary>
    Ean13,

    /// <summary>Code 39，支持大写字母、数字与少量符号。</summary>
    Code39,

    /// <summary>EAN-8，需 7 或 8 位数字。</summary>
    Ean8,

    /// <summary>UPC-A，需 11 或 12 位数字。</summary>
    UpcA,

    /// <summary>UPC-E，需 6、7 或 8 位数字。</summary>
    UpcE,

    /// <summary>交叉 25 码（ITF），需偶数位数字。</summary>
    Itf,

    /// <summary>Codabar。</summary>
    Codabar,

    /// <summary>Code 93。</summary>
    Code93,

    /// <summary>MSI（Modified Plessey）。</summary>
    Msi,
}

/// <summary>表格列尾汇总方式，对应 hiprint 的 <c>tableSummary</c>。</summary>
public enum TableSummary
{
    /// <summary>不汇总。</summary>
    None,

    /// <summary>计数：非空行数。</summary>
    Count,

    /// <summary>求和。</summary>
    Sum,

    /// <summary>平均值。</summary>
    Average,

    /// <summary>最小值。</summary>
    Min,

    /// <summary>最大值。</summary>
    Max,

    /// <summary>固定文本（<see cref="TableColumn.SummaryLabel"/>）。</summary>
    Text,
}
