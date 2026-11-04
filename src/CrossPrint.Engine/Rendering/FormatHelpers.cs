using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 脚本引擎（Jint）暴露给 formatter 的内置助手：日期/布尔/数字格式化与中文金额大写。
/// 通过 <see cref="ScriptRunner"/> 的 <c>SetValue</c> 注入为全局函数 <c>fmt</c> 与 <c>rmbUpper</c>，
/// 导入器可把 hiprint 的 dataType/format、upperCase 翻译成调用它们的 formatter 脚本。
/// </summary>
internal static class FormatHelpers
{
    static readonly string[] CnDigits = { "零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖" };
    static readonly string[] CnUnit = { "", "拾", "佰", "仟" };
    static readonly string[] CnGroup = { "", "万", "亿", "兆" };

    /// <summary>按类型与格式字符串格式化原始值；无法解析时回退原值字符串。</summary>
    public static string? Fmt(object? value, string? type, string? format)
    {
        if (value is null)
            return null;

        var kind = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is "bool" or "boolean")
        {
            var b = value switch
            {
                bool bb => bb,
                string s => bool.TryParse(s, out var parsed) && parsed,
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            };
            // 布尔：format 当作自定义文本，默认"是/否"
            if (string.IsNullOrWhiteSpace(format))
                return b ? "是" : "否";
            var parts = format.Split('|');
            return b ? (parts.Length > 0 ? parts[0] : "是") : (parts.Length > 1 ? parts[1] : "否");
        }

        if (kind is "date" or "datetime" or "time")
        {
            if (!TryToDateTime(value, out var dt))
                return value.ToString();
            if (string.IsNullOrWhiteSpace(format))
                return kind == "time" ? dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return FormatDateTime(dt, format);
        }

        // 数字：尝试按 format 用不变文化格式化（N2 / 0.00 / 自定义均走 .NET）
        if (!string.IsNullOrWhiteSpace(format)
            && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            try { return num.ToString(format, CultureInfo.InvariantCulture); }
            catch { return num.ToString(CultureInfo.InvariantCulture); }
        }

        return value.ToString();
    }

    /// <summary>中文金额大写（人民币）。支持负数与小数（角/分），零值返回"零元整"。</summary>
    public static string RmbUpper(object? value)
    {
        if (value is null)
            return string.Empty;
        if (!TryToDecimal(value, out var amount))
            return value.ToString() ?? string.Empty;
        if (amount == 0)
            return "零元整";

        var negative = amount < 0;
        amount = Math.Abs(amount);

        var integer = Math.Floor(amount);
        var dec = Math.Round((amount - integer) * 100);
        if (dec >= 100) { integer += 1; dec = 0; } // 四舍五入进位
        var jiao = (int)(dec / 10);
        var fen = (int)(dec % 10);

        var sb = new StringBuilder();
        if (negative)
            sb.Append("负");
        sb.Append(IntegerToChinese((long)integer));
        sb.Append("元");

        if (jiao == 0 && fen == 0)
        {
            sb.Append("整");
        }
        else
        {
            if (jiao > 0)
                sb.Append(CnDigits[jiao] + "角");
            if (fen > 0)
                sb.Append(CnDigits[fen] + "分");
            else if (jiao == 0)
                sb.Append("零");
        }

        // 清理多余零：连续零压成一个，并去掉"零元/零整"里的孤立零
        var result = Regex.Replace(sb.ToString(), "零+", "零");
        if (result.StartsWith("零", StringComparison.Ordinal) && !result.StartsWith("零元", StringComparison.Ordinal))
            result = result.TrimStart('零');
        result = result.Replace("零元", "元").Replace("零整", "整").Replace("元整", "元整");
        if (result.EndsWith("零", StringComparison.Ordinal) && !result.EndsWith("元零", StringComparison.Ordinal))
            result = result.TrimEnd('零');
        return result == string.Empty ? "零元整" : result;
    }

    static string IntegerToChinese(long n)
    {
        if (n == 0)
            return "零";

        var result = new StringBuilder();
        var groupIndex = 0;
        while (n > 0)
        {
            var group = (int)(n % 10000);
            if (group != 0)
            {
                result.Insert(0, FourToChinese(group) + CnGroup[groupIndex]);
            }
            else if (result.Length > 0 && !result.ToString().StartsWith("零", StringComparison.Ordinal))
            {
                result.Insert(0, "零");
            }

            n /= 10000;
            groupIndex++;
        }

        return result.ToString();
    }

    static string FourToChinese(int g)
    {
        var sb = new StringBuilder();
        var pos = 0;
        var trailingZero = false;
        while (g > 0)
        {
            var d = g % 10;
            if (d == 0)
            {
                if (!trailingZero && sb.Length > 0)
                {
                    sb.Insert(0, "零");
                    trailingZero = true;
                }
            }
            else
            {
                sb.Insert(0, CnDigits[d] + CnUnit[pos]);
                trailingZero = false;
            }

            g /= 10;
            pos++;
        }

        return sb.ToString();
    }

    static bool TryToDateTime(object value, out DateTime dt)
    {
        dt = default;
        return value switch
        {
            DateTime d => (dt = d).GetType() == typeof(DateTime),
            string s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt),
            double d => (dt = DateTime.FromOADate(d)).Year > 1,
            _ => DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt),
        };
    }

    static bool TryToDecimal(object value, out decimal amount)
    {
        amount = 0;
        return value switch
        {
            decimal m => (amount = m) == m,
            double d => (amount = (decimal)d) == amount,
            float f => (amount = (decimal)f) == amount,
            int or long or short or byte => (amount = Convert.ToDecimal(value)) == amount,
            string s => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out amount),
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out amount),
        };
    }

    static string FormatDateTime(DateTime dt, string format)
    {
        // 支持常见 .NET 风格占位符：yyyy MM dd HH mm ss
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["yyyy"] = dt.ToString("yyyy", CultureInfo.InvariantCulture),
            ["MM"] = dt.ToString("MM", CultureInfo.InvariantCulture),
            ["dd"] = dt.ToString("dd", CultureInfo.InvariantCulture),
            ["HH"] = dt.ToString("HH", CultureInfo.InvariantCulture),
            ["mm"] = dt.ToString("mm", CultureInfo.InvariantCulture),
            ["ss"] = dt.ToString("ss", CultureInfo.InvariantCulture),
        };
        var result = format;
        // 先替换 4 位年，避免 yyyy 被 yyyy→ 先替换长 token
        result = result.Replace("yyyy", map["yyyy"]);
        result = result.Replace("MM", map["MM"]);
        result = result.Replace("dd", map["dd"]);
        result = result.Replace("HH", map["HH"]);
        result = result.Replace("mm", map["mm"]);
        result = result.Replace("ss", map["ss"]);
        return result;
    }
}
