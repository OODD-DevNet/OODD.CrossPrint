using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// 载荷类型往返无损守卫：用反射把每个可写字段填成有辨识度的非默认值，
/// 序列化 → 反序列化 → 再序列化，断言两次 JSON 文本完全相同。
/// 新增字段若忘了登记到源生成上下文或转换器，本测试即失败。
/// </summary>
public class PayloadRoundTripTests
{
    public static TheoryData<Type> PayloadTypes => new()
    {
        typeof(TextOptions),
        typeof(LineOptions),
        typeof(ShapeOptions),
        typeof(ImageOptions),
        typeof(QrCodeOptions),
        typeof(BarcodeOptions),
        typeof(TableOptions),
        typeof(TableColumn),
    };

    [Theory]
    [MemberData(nameof(PayloadTypes))]
    public void Every_field_survives_round_trip(Type payloadType)
    {
        var instance = Fill(payloadType);
        var info = (JsonTypeInfo)PrintTemplateJsonContext.Default.GetTypeInfo(payloadType)!;
        var options = TemplateJson.PayloadOptions;

        var first = JsonSerializer.Serialize(instance, info);
        var restored = JsonSerializer.Deserialize(first, info);
        var second = JsonSerializer.Serialize(restored, info);

        second.Should().Be(first, $"字段集或取值在往返中发生变化（{payloadType.Name}）");
        first.Should().NotBe("{}");
    }

    [Theory]
    [MemberData(nameof(PayloadTypes))]
    public void Round_trip_uses_camel_case_names(Type payloadType)
    {
        var json = JsonSerializer.Serialize(Fill(payloadType), PrintTemplateJsonContext.Default.GetTypeInfo(payloadType)!);
        foreach (var prop in payloadType.GetProperties())
            json.Should().Contain($"\"{Camel(prop.Name)}\"", $"{payloadType.Name} 的属性 {prop.Name} 未按 camelCase 写出");
    }

    [Fact]
    public void Enums_are_written_as_strings()
    {
        var text = new TextOptions { HorizontalAlign = HorizontalAlign.Center, VerticalAlign = VerticalAlign.Bottom };
        var json = JsonSerializer.Serialize(text, PrintTemplateJsonContext.Default.TextOptions);
        json.Should().Contain("\"horizontalAlign\":\"Center\"");
        json.Should().Contain("\"verticalAlign\":\"Bottom\"");
    }

    [Fact]
    public void Colors_are_written_as_hex_and_read_back()
    {
        var text = new TextOptions { Color = new SKColor(0x12, 0x34, 0x56) };
        var json = JsonSerializer.Serialize(text, PrintTemplateJsonContext.Default.TextOptions);
        json.Should().Contain("\"color\":\"#123456\"");

        var back = JsonSerializer.Deserialize(json, PrintTemplateJsonContext.Default.TextOptions)!.Should().BeOfType<TextOptions>().Subject;
        back.Color.Should().Be(new SKColor(0x12, 0x34, 0x56));
    }

    [Fact]
    public void Opaque_color_omits_alpha_channel()
    {
        var line = new LineOptions { StrokeColor = new SKColor(1, 2, 3, 0xFF) };
        JsonSerializer.Serialize(line, PrintTemplateJsonContext.Default.LineOptions)
            .Should().Contain("\"strokeColor\":\"#010203\"");
    }

    [Fact]
    public void Transparent_color_keeps_alpha()
    {
        var line = new LineOptions { StrokeColor = new SKColor(1, 2, 3, 0x7F) };
        JsonSerializer.Serialize(line, PrintTemplateJsonContext.Default.LineOptions)
            .Should().Contain("\"strokeColor\":\"#7F010203\"");
    }

    [Fact]
    public void Null_color_is_written_as_null_not_empty()
    {
        var shape = new ShapeOptions { FillColor = null };
        JsonSerializer.Serialize(shape, PrintTemplateJsonContext.Default.ShapeOptions)
            .Should().Contain("\"fillColor\":null");
    }

    static object Fill(Type type)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var prop in type.GetProperties())
        {
            if (!prop.CanWrite)
                continue;

            prop.SetValue(instance, SampleValue(prop.PropertyType, prop.Name));
        }

        return instance;
    }

    static object? SampleValue(Type type, string propName)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string)) return $"v-{propName}";
        if (target == typeof(double)) return 7.25;
        if (target == typeof(int)) return 3;
        if (target == typeof(bool)) return true;
        if (target == typeof(SKColor)) return new SKColor(0xAA, 0xBB, 0xCC, 0xFF);
        if (target == typeof(double[])) return new double[] { 1.5, 0.5 };
        if (target.IsEnum)
        {
            var values = Enum.GetValues(target);
            return values.GetValue(values.Length > 1 ? 1 : 0)!;
        }

        if (target == typeof(List<TableColumn>))
            return new List<TableColumn> { (TableColumn)Fill(typeof(TableColumn)), (TableColumn)Fill(typeof(TableColumn)) };

        if (target == typeof(List<TableHeaderCell>))
            return new List<TableHeaderCell> { (TableHeaderCell)Fill(typeof(TableHeaderCell)) };

        if (target == typeof(List<TableConditionalStyle>))
            return new List<TableConditionalStyle> { (TableConditionalStyle)Fill(typeof(TableConditionalStyle)) };

        if (target == typeof(List<TableHighlightRule>))
            return new List<TableHighlightRule> { (TableHighlightRule)Fill(typeof(TableHighlightRule)) };

        if (target == typeof(List<TableRowSpan>))
            return new List<TableRowSpan> { (TableRowSpan)Fill(typeof(TableRowSpan)) };

        if (target == typeof(List<List<string>>))
            return new List<List<string>> { new() { "a", "b" }, new() { "c", "d" } };

        throw new NotSupportedException($"测试未覆盖的属性类型 {target.Name}（{propName}）——新增字段时请同步 SampleValue。");
    }

    static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
