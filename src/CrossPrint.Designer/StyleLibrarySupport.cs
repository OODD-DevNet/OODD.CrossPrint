#pragma warning disable CS1591 // 本文件为内部辅助层，省略公共成员的 XML 文档注释。
using System;
using System.Collections.Generic;
using System.Globalization;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 样式库对话框的框架无关数据层：集中描述样式字段、可空值读写与解析。
/// 各宿主 UI 只负责按 <see cref="StyleFieldDef"/> 把控件摆出来、把输入按 <see cref="StyleFieldKind"/> 解析后
/// 交给 <see cref="EditorKernel.UpdateStyle"/>，可空值与字段映射全在这里一处维护，避免 5 个宿主各自
/// 写一份易错的 bool?/double?/SKColor? 转换。
/// </summary>
public enum StyleFieldKind
{
    Text,
    Number,
    Color,
    Dash,
    Bool,
    Enum,
}

/// <summary>单个样式字段的元数据（键、标签、控件类型、枚举类型）。</summary>
public sealed class StyleFieldDef
{
    public string Key = string.Empty;
    public string LabelZh = string.Empty;
    public StyleFieldKind Kind = StyleFieldKind.Text;
    public Type? EnumType;
}

public static class StyleLibrarySupport
{
    /// <summary>样式支持的全部字段（顺序即表单顺序）。</summary>
    public static readonly StyleFieldDef[] Fields =
    [
        F("name", "名称"),
        F("fontFamily", "字体"),
        F("fontSizePt", "字号 (pt)", StyleFieldKind.Number),
        F("letterSpacingPt", "字间距 (pt)", StyleFieldKind.Number),
        F("bold", "加粗", StyleFieldKind.Bool),
        F("italic", "倾斜", StyleFieldKind.Bool),
        F("underline", "下划线", StyleFieldKind.Bool),
        F("strikethrough", "删除线", StyleFieldKind.Bool),
        F("overline", "上划线", StyleFieldKind.Bool),
        F("lineSpacing", "行距倍数", StyleFieldKind.Number),
        F("indentMm", "左缩进 (mm)", StyleFieldKind.Number),
        F("firstLineIndentMm", "首行缩进 (mm)", StyleFieldKind.Number),
        F("textColor", "字色", StyleFieldKind.Color),
        F("backgroundColor", "底色", StyleFieldKind.Color),
        F("borderColor", "边框色", StyleFieldKind.Color),
        F("borderWidthMm", "边框宽 (mm)", StyleFieldKind.Number),
        F("borderDashPatternMm", "边框虚线 (mm)", StyleFieldKind.Dash),
        F("horizontalAlign", "水平对齐", StyleFieldKind.Enum, typeof(HorizontalAlign)),
        F("verticalAlign", "垂直对齐", StyleFieldKind.Enum, typeof(VerticalAlign)),
        F("paddingLeftMm", "内边距·左 (mm)", StyleFieldKind.Number),
        F("paddingRightMm", "内边距·右 (mm)", StyleFieldKind.Number),
        F("paddingTopMm", "内边距·上 (mm)", StyleFieldKind.Number),
        F("paddingBottomMm", "内边距·下 (mm)", StyleFieldKind.Number),
    ];

    public const string Unset = "（不设置）";

    static StyleFieldDef F(string key, string zh, StyleFieldKind kind = StyleFieldKind.Text, Type? enumType = null) =>
        new() { Key = key, LabelZh = zh, Kind = kind, EnumType = enumType };

    /// <summary>读样式字段当前值（控件回填用）。</summary>
    public static object? GetValue(NamedStyle s, string key) => key switch
    {
        "name" => s.Name,
        "fontFamily" => s.FontFamily,
        "fontSizePt" => s.FontSizePt,
        "letterSpacingPt" => s.LetterSpacingPt,
        "bold" => s.Bold,
        "italic" => s.Italic,
        "underline" => s.Underline,
        "strikethrough" => s.Strikethrough,
        "overline" => s.Overline,
        "lineSpacing" => s.LineSpacing,
        "indentMm" => s.IndentMm,
        "firstLineIndentMm" => s.FirstLineIndentMm,
        "textColor" => s.TextColor,
        "backgroundColor" => s.BackgroundColor,
        "borderColor" => s.BorderColor,
        "borderWidthMm" => s.BorderWidthMm,
        "borderDashPatternMm" => s.BorderDashPatternMm,
        "horizontalAlign" => s.HorizontalAlign,
        "verticalAlign" => s.VerticalAlign,
        "paddingLeftMm" => s.PaddingLeftMm,
        "paddingRightMm" => s.PaddingRightMm,
        "paddingTopMm" => s.PaddingTopMm,
        "paddingBottomMm" => s.PaddingBottomMm,
        _ => null,
    };

    /// <summary>把解析好的值写进样式（UpdateStyle 的 mutate 里调用）。</summary>
    public static void SetValue(NamedStyle s, string key, object? value)
    {
        switch (key)
        {
            case "name":
                s.Name = value as string is { Length: > 0 } name ? name : "未命名样式";
                break;
            case "fontFamily": s.FontFamily = (string?)value; break;
            case "fontSizePt": s.FontSizePt = (double?)value; break;
            case "letterSpacingPt": s.LetterSpacingPt = (double?)value; break;
            case "bold": s.Bold = (bool?)value; break;
            case "italic": s.Italic = (bool?)value; break;
            case "underline": s.Underline = (bool?)value; break;
            case "strikethrough": s.Strikethrough = (bool?)value; break;
            case "overline": s.Overline = (bool?)value; break;
            case "lineSpacing": s.LineSpacing = (double?)value; break;
            case "indentMm": s.IndentMm = (double?)value; break;
            case "firstLineIndentMm": s.FirstLineIndentMm = (double?)value; break;
            case "textColor": s.TextColor = (SKColor?)value; break;
            case "backgroundColor": s.BackgroundColor = (SKColor?)value; break;
            case "borderColor": s.BorderColor = (SKColor?)value; break;
            case "borderWidthMm": s.BorderWidthMm = (double?)value; break;
            case "borderDashPatternMm": s.BorderDashPatternMm = (double[]?)value; break;
            case "horizontalAlign": s.HorizontalAlign = (HorizontalAlign?)value; break;
            case "verticalAlign": s.VerticalAlign = (VerticalAlign?)value; break;
            case "paddingLeftMm": s.PaddingLeftMm = (double?)value; break;
            case "paddingRightMm": s.PaddingRightMm = (double?)value; break;
            case "paddingTopMm": s.PaddingTopMm = (double?)value; break;
            case "paddingBottomMm": s.PaddingBottomMm = (double?)value; break;
        }
    }

    /// <summary>把控件文本按字段类型解析为目标可空值；空串一律返回 null（不设置）。</summary>
    public static object? Parse(StyleFieldDef def, string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        switch (def.Kind)
        {
            case StyleFieldKind.Number:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new FormatException($"“{text}”不是合法数字（请用小数点，如 12.5）。");
                return number;

            case StyleFieldKind.Color:
                if (!SKColor.TryParse(trimmed, out var color))
                    throw new FormatException("颜色格式应为 #RRGGBB 或 #AARRGGBB。");
                return color;

            case StyleFieldKind.Dash:
                var parts = trimmed.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return null;
                var values = new double[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                        throw new FormatException($"“{parts[i]}”不是合法段长（用逗号分隔，如 1.5,1）。");
                }
                return values;

            case StyleFieldKind.Bool:
                return trimmed is "1" or "true" or "True" or "开" or "是";

            case StyleFieldKind.Enum:
                if (def.EnumType is { } t)
                {
                    foreach (var v in Enum.GetValues(t))
                    {
                        if (string.Equals(v.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                            return v;
                    }
                    throw new FormatException($"“{text}”不是合法的 {def.LabelZh}。");
                }
                return null;

            default:
                return trimmed;
        }
    }

    /// <summary>把当前值格式化为控件回填文本（空 = 不设置）。</summary>
    public static string Format(StyleFieldDef def, object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        SKColor c => ToHex(c),
        double[] a when a.Length > 0 => string.Join(",", Array.ConvertAll(a, v => v.ToString("0.###", CultureInfo.InvariantCulture))),
        _ => value.ToString() ?? string.Empty,
    };

    public static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
