namespace CrossPrint.Samples.WinForms;

/// <summary>与 kitchen-sink.template.json 的占位符一一对应的样例数据。</summary>
public static class SampleData
{
    /// <summary>创建样例数据：title / orderNo / sku 三个占位符字段。</summary>
    public static object Create() => new
    {
        title = "厨房订单",
        orderNo = "ORD-20261030-0001",
        sku = "SKU-88-A",
    };
}
