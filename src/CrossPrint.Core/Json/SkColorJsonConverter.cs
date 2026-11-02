using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace CrossPrint.Core;

/// <summary>
/// SKColor 与 <c>#RRGGBB</c> / <c>#AARRGGBB</c> 十六进制串的双向转换。
/// </summary>
public sealed class SkColorJsonConverter : JsonConverter<SKColor>
{
    /// <inheritdoc/>
    public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 可空包装器已先行处理 null，这里只会收到非 null 的字符串令牌。
        if (reader.TokenType == JsonTokenType.Null)
            return default;

        var text = reader.GetString();
        try
        {
            return Parse(text, throwOnError: true, defaultValue: default);
        }
        catch (FormatException ex)
        {
            // 序列化层的异常契约是 JsonException；否则 TemplateJson.Read<T> 抓不到，
            // 非法颜色会裸抛 FormatException，丢失「哪个元素哪个字段」的定位信息。
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToHex(value));

    /// <summary>解析十六进制色串；失败时按 <paramref name="throwOnError"/> 抛异常或返回默认值。</summary>
    public static SKColor Parse(string? text, bool throwOnError, SKColor defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (throwOnError)
                throw new FormatException($"颜色值 '{text}' 为空，期望形如 #RRGGBB 或 #AARRGGBB。");
            return defaultValue;
        }

        var s = text.Trim();
        if (s[0] == '#')
            s = s[1..];

        uint rgba;
        string alphaHex;
        string rgbHex;

        switch (s.Length)
        {
            case 6:
                rgbHex = s;
                alphaHex = "FF";
                break;
            case 8:
                // 采用 CSS/前端惯例：#AARRGGBB
                alphaHex = s[..2];
                rgbHex = s[2..];
                break;
            default:
                if (throwOnError)
                    throw new FormatException($"颜色值 '#{s}' 长度非法，期望 6 位（RRGGBB）或 8 位（AARRGGBB）。");
                return defaultValue;
        }

        if (!uint.TryParse(rgbHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb)
            || !byte.TryParse(alphaHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var alpha))
        {
            if (throwOnError)
                throw new FormatException($"颜色值 '#{s}' 含非十六进制字符。");
            return defaultValue;
        }

        rgba = ((uint)alpha << 24) | (rgb & 0xFFFFFF);
        return new SKColor(rgba);
    }

    /// <summary>输出 <c>#RRGGBB</c>（不透明）或 <c>#AARRGGBB</c>（含透明度）。</summary>
    public static string ToHex(SKColor color)
        => color.Alpha == 0xFF
            ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            : $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
