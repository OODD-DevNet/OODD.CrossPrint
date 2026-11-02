namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 与 kitchen-sink.template.json 的占位符一一对应的样例数据。
/// 属性名用 PascalCase（<c>Title</c>/<c>OrderNo</c>/<c>Sku</c>）：数据绑定按名称大小写不敏感匹配，
/// 模板里的 <c>{{title}}</c> 照样取得到。
/// </summary>
public sealed class SampleOrder
{
    /// <summary>标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>订单号。</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>商品编码。</summary>
    public string Sku { get; init; } = string.Empty;
}

/// <summary>样例数据工厂。</summary>
public static class SampleData
{
    /// <summary>创建样例数据：Title / OrderNo / Sku 三个占位符字段。</summary>
    /// <returns>可交给 <c>PreviewControl.Data</c> 或 <c>PdfExporter</c> 的数据源。</returns>
    public static object Create() => new SampleOrder
    {
        Title = "厨房订单",
        OrderNo = "ORD-20261030-0001",
        Sku = "SKU-88-A",
    };
}
