using System;

namespace CrossPrint.Samples.Wpf;

/// <summary>
/// 预览/导出用的样例数据。属性名与 kitchen-sink.template.json 的占位符一一对应：
/// <c>{{title}}</c>、<c>{{orderNo}}</c>、<c>{{sku}}</c>（引擎按属性名匹配，忽略大小写）。
/// </summary>
internal sealed class SampleData
{
    public string Title { get; init; } = "厨房订单";

    public string OrderNo { get; init; } = "ORD-20261030-0001";

    public string Sku { get; init; } = "SKU-88-A";

    public static SampleData Create() => new();
}
