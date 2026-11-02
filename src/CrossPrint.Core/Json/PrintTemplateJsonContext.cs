using System.Text.Json.Serialization;

namespace CrossPrint.Core;

/// <summary>
/// System.Text.Json 源生成上下文：模板元素载荷类型清单，保证无反射、AOT/Trimmed 可用。
/// </summary>
[JsonSerializable(typeof(TextOptions))]
[JsonSerializable(typeof(LineOptions))]
[JsonSerializable(typeof(ShapeOptions))]
[JsonSerializable(typeof(ImageOptions))]
[JsonSerializable(typeof(QrCodeOptions))]
[JsonSerializable(typeof(BarcodeOptions))]
[JsonSerializable(typeof(TableOptions))]
[JsonSerializable(typeof(TableColumn))]
[JsonSerializable(typeof(List<TableColumn>))]
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
[JsonSerializable(typeof(List<List<string>>))]
[JsonSerializable(typeof(double[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PrintTemplateJsonContext : JsonSerializerContext;
