namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 预览样例数据。字段名与 kitchen-sink.template.json 的占位符一一对应（title/orderNo/sku），
/// DataBinder 按属性名取值。
/// </summary>
public sealed class SampleData
{
    public string title { get; init; } = string.Empty;

    public string orderNo { get; init; } = string.Empty;

    public string sku { get; init; } = string.Empty;

    public static SampleData Create() => new()
    {
        title = "厨房订单",
        orderNo = "ORD-20261030-0001",
        sku = "SKU-88-A",
    };
}
