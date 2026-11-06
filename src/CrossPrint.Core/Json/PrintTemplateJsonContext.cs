using System.Text.Json.Serialization;

namespace CrossPrint.Core;

/// <summary>
/// System.Text.Json 源生成上下文：模板元素载荷类型清单，保证无反射、AOT/Trimmed 可用。
/// </summary>
[JsonSerializable(typeof(TextOptions))]
[JsonSerializable(typeof(LineOptions))]
[JsonSerializable(typeof(ShapeOptions))]
[JsonSerializable(typeof(PolygonOptions))]
[JsonSerializable(typeof(ImageOptions))]
[JsonSerializable(typeof(QrCodeOptions))]
[JsonSerializable(typeof(BarcodeOptions))]
[JsonSerializable(typeof(TableOptions))]
[JsonSerializable(typeof(TableColumn))]
[JsonSerializable(typeof(List<TableColumn>))]
// 列图片数据类型：TableColumn 的枚举字段走源生成（AOT/Trimmed 下无反射兜底）。
[JsonSerializable(typeof(TableCellImageSource))]
[JsonSerializable(typeof(TableHeaderCell))]
[JsonSerializable(typeof(List<TableHeaderCell>))]
[JsonSerializable(typeof(TableConditionalStyle))]
[JsonSerializable(typeof(List<TableConditionalStyle>))]
[JsonSerializable(typeof(TableHighlightRule))]
[JsonSerializable(typeof(List<TableHighlightRule>))]
[JsonSerializable(typeof(TableRowSpan))]
[JsonSerializable(typeof(List<TableRowSpan>))]
[JsonSerializable(typeof(NamedStyle))]
[JsonSerializable(typeof(List<NamedStyle>))]
[JsonSerializable(typeof(WatermarkOptions))]
[JsonSerializable(typeof(List<List<string>>))]
[JsonSerializable(typeof(double[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PrintTemplateJsonContext : JsonSerializerContext;
