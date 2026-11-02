using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CrossPrint.Core.Hiprint;

/// <summary>
/// hiprint 设计器导出的文本不一定是严格 JSON：
/// <list type="bullet">
/// <item><description>单引号字符串、未加引号的键、尾逗号（demo 模板与 <c>getJson()</c> 的常见形态）。</description></item>
/// <item><description>整个 <c>.js</c> 模块（<c>export const json = {...}</c>，前后还有 import 与别的导出）。</description></item>
/// <item><description>反引号模板字符串里存着 <c>rowsColumnsMerge</c>/<c>renderFormatter</c> 这类 JS 函数源码。</description></item>
/// <item><description>注释、<c>undefined</c>、<c>...</c> 展开、字符串 <c>+</c> 拼接。</description></item>
/// </list>
/// 这里把上述写法归一化成严格 JSON，并自动从模块文本里挑出含 <c>panels</c> 的那个对象。
/// 只做语法搬运，不做语义求值——JS 函数体原样保留成字符串，由导入器识别并降级告警。
/// </summary>
internal static class HiprintJsonNormalizer
{
    /// <summary>
    /// 从任意 hiprint 导出文本中提取模板对象并归一化为严格 JSON。
    /// </summary>
    /// <param name="source">用户粘贴/文件读入的文本。</param>
    /// <param name="json">归一化后的 JSON（含 panels 的根对象）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否成功。</returns>
    public static bool TryExtractTemplate(string source, out string json, out string? error)
    {
        json = string.Empty;
        error = null;

        var text = source.Trim().TrimStart('\uFEFF');
        if (text.Length == 0)
        {
            error = "模板文本为空。";
            return false;
        }

        // 1) 整段就是模板对象（严格 JSON 或 JS 对象字面量）
        if (TryCandidate(text, out json))
            return true;

        // 2) 形如 `"panels": [...]` 的片段（复制 JSON 的一部分时很常见，尾部可能还跟着别的代码）
        if (TryFragment(text, out json))
            return true;

        // 3) 整个 JS 模块：逐个尝试顶层对象，取第一个含 panels 的
        foreach (var candidate in EnumerateTopLevelObjects(text))
        {
            if (TryCandidate(candidate, out json))
                return true;
        }

        error = "无法在文本中找到 hiprint 模板对象（需要包含 panels 数组的严格 JSON、JS 对象字面量或 getJson() 输出）。";
        return false;
    }

    /// <summary>以 <c>"key": value</c> 开头、但缺少外层花括号的片段。</summary>
    static bool TryFragment(string text, out string json)
    {
        json = string.Empty;
        if (text.Length == 0 || text[0] is '{' or '[')
            return false;

        var start = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`')
            {
                SkipQuoted(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                SkipLineComment(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                SkipBlockComment(text, ref i);
                continue;
            }

            if (c is '{' or '[')
            {
                start = i;
                break;
            }

            i++;
        }

        if (start < 0)
            return false;

        var prefix = text[..start].TrimEnd();
        if (prefix.Length == 0 || prefix[^1] is not (':' or ','))
            return false;

        var end = FindValueEnd(text, start);
        if (end < 0)
            return false;

        return TryCandidate("{" + text[..(end + 1)] + "}", out json);
    }

    static int FindValueEnd(string text, int start)
    {
        var stack = new Stack<char>();
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`')
            {
                SkipQuoted(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                SkipLineComment(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                SkipBlockComment(text, ref i);
                continue;
            }

            if (c is '{' or '[')
            {
                stack.Push(c);
            }
            else if (c is '}' or ']')
            {
                if (stack.Count == 0)
                    return -1;
                var open = stack.Pop();
                if ((open == '{') != (c == '}'))
                    return -1;
                if (stack.Count == 0)
                    return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>该 JSON 根节点是否像一个 hiprint 模板（panels 数组或 <c>{json:{panels}}</c> 信封）。</summary>
    /// <param name="root">归一化后的根节点。</param>
    public static bool LooksLikeTemplate(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return true;

        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (root.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array)
            return true;

        return root.TryGetProperty("json", out var inner)
            && inner.ValueKind == JsonValueKind.Object
            && inner.TryGetProperty("panels", out var nested)
            && nested.ValueKind == JsonValueKind.Array;
    }

    static bool TryCandidate(string raw, out string json)
    {
        json = string.Empty;
        string normalized;
        try
        {
            normalized = Normalize(raw);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            if (!LooksLikeTemplate(doc.RootElement))
                return false;

            json = normalized;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把一段 JS 对象/数组字面量归一化为严格 JSON。</summary>
    /// <param name="text">JS 字面量文本。</param>
    /// <exception cref="FormatException">文本里有无法搬运的语法残渣。</exception>
    public static string Normalize(string text)
    {
        var tokens = Tokenize(text);
        var sb = new StringBuilder(text.Length + 16);
        var pos = 0;
        WriteValue(tokens, ref pos, sb, text);
        if (pos < tokens.Count)
            throw new FormatException("JavaScript 字面量末尾还有无法解析的内容。");
        return sb.ToString();
    }

    // ---------------- 扫描：顶层对象 ----------------

    static IEnumerable<string> EnumerateTopLevelObjects(string text)
    {
        var depth = 0;
        var start = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`')
            {
                SkipQuoted(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                SkipLineComment(text, ref i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                SkipBlockComment(text, ref i);
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text[start..(i + 1)];
                    start = -1;
                }
            }

            i++;
        }
    }

    static void SkipLineComment(string s, ref int i)
    {
        i += 2;
        while (i < s.Length && s[i] != '\n')
            i++;
    }

    static void SkipBlockComment(string s, ref int i)
    {
        i += 2;
        while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/'))
            i++;
        i++;
    }

    static void SkipQuoted(string s, ref int i)
    {
        var quote = s[i++];
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (quote == '`' && c == '$' && i + 1 < s.Length && s[i + 1] == '{')
            {
                i += 2;
                SkipTemplateExpression(s, ref i);
                continue;
            }

            if (c == quote)
            {
                i++;
                return;
            }

            i++;
        }

        i = s.Length;
    }

    static void SkipTemplateExpression(string s, ref int i)
    {
        var depth = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"' or '`')
            {
                SkipQuoted(s, ref i);
                continue;
            }

            if (c == '/' && i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*'))
            {
                if (s[i + 1] == '/')
                    SkipLineComment(s, ref i);
                else
                    SkipBlockComment(s, ref i);
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                i++;
                if (depth == 0)
                    return;
                continue;
            }

            i++;
        }
    }

    // ---------------- 词法 ----------------

    enum TokenKind
    {
        String,
        Number,
        Identifier,
        Punctuation,
    }

    readonly record struct Token(TokenKind Kind, string Text, int Position);

    static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                SkipLineComment(s, ref i);
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                SkipBlockComment(s, ref i);
                continue;
            }

            var pos = i;

            if (c is '\'' or '"' or '`')
            {
                tokens.Add(new Token(TokenKind.String, ReadQuoted(s, ref i), pos));
                continue;
            }

            if (IsNumberStart(s, i))
            {
                tokens.Add(new Token(TokenKind.Number, ReadNumber(s, ref i), pos));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                tokens.Add(new Token(TokenKind.Identifier, ReadIdentifier(s, ref i), pos));
                continue;
            }

            if (c == '=' && i + 1 < s.Length && s[i + 1] == '>')
            {
                tokens.Add(new Token(TokenKind.Punctuation, "=>", pos));
                i += 2;
                continue;
            }

            tokens.Add(new Token(TokenKind.Punctuation, c.ToString(), pos));
            i++;
        }

        return tokens;
    }

    static bool IsNumberStart(string s, int i)
    {
        var c = s[i];
        if (char.IsDigit(c))
            return true;

        if (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            return true;

        if (c is '-' or '+')
        {
            if (i + 1 < s.Length && char.IsDigit(s[i + 1]))
                return true;
            if (i + 2 < s.Length && s[i + 1] == '.' && char.IsDigit(s[i + 2]))
                return true;
        }

        return false;
    }

    static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$';

    static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    static string ReadIdentifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && IsIdentifierPart(s[i]))
            i++;
        return s[start..i];
    }

    static string ReadNumber(string s, ref int i)
    {
        var start = i;
        if (s[i] is '-' or '+')
        {
            // 只有后面确实跟数字时才会走到这里（见 IsNumberStart）
            if (i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '.'))
                i++;
        }

        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            i += 2;
            var hexStart = i;
            while (i < s.Length && Uri.IsHexDigit(s[i]))
                i++;
            var hex = s[hexStart..i];
            return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : "0";
        }

        while (i < s.Length && char.IsDigit(s[i]))
            i++;

        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i]))
                i++;
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;
            while (i < s.Length && char.IsDigit(s[i]))
                i++;
        }

        var raw = s[start..i];
        if (raw.StartsWith('+'))
            raw = raw[1..];
        if (raw.StartsWith(".", StringComparison.Ordinal))
            raw = "0" + raw;
        else if (raw.StartsWith("-.", StringComparison.Ordinal) || raw.StartsWith("+.", StringComparison.Ordinal))
            raw = "-0" + raw[2..];
        if (raw.EndsWith(".", StringComparison.Ordinal))
            raw += "0";
        return raw;
    }

    static string ReadQuoted(string s, ref int i)
    {
        var quote = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\')
            {
                i++;
                if (i >= s.Length)
                    break;

                var e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '0': sb.Append('\0'); break;
                    case '\n': break;
                    case '\r':
                        if (i < s.Length && s[i] == '\n')
                            i++;
                        break;
                    case 'x':
                        AppendHex(s, ref i, 2, sb);
                        break;
                    case 'u':
                        if (i < s.Length && s[i] == '{')
                        {
                            i++;
                            var hexStart = i;
                            while (i < s.Length && s[i] != '}')
                                i++;
                            var hex = s[hexStart..i];
                            if (i < s.Length)
                                i++;
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                                && cp is >= 0 and <= 0x10FFFF)
                                sb.Append(char.ConvertFromUtf32(cp));
                        }
                        else
                        {
                            AppendHex(s, ref i, 4, sb);
                        }

                        break;
                    default: sb.Append(e); break;
                }

                continue;
            }

            if (quote == '`' && c == '$' && i + 1 < s.Length && s[i + 1] == '{')
            {
                var expr = ReadTemplateExpressionRaw(s, ref i);
                sb.Append("${").Append(expr).Append('}');
                continue;
            }

            if (c == quote)
            {
                i++;
                break;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    static string ReadTemplateExpressionRaw(string s, ref int i)
    {
        i += 2; // ${ 
        var start = i;
        var depth = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"' or '`')
            {
                SkipQuoted(s, ref i);
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] is '/' or '*')
            {
                if (s[i + 1] == '/')
                    SkipLineComment(s, ref i);
                else
                    SkipBlockComment(s, ref i);
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var raw = s[start..i];
                    i++;
                    return raw;
                }

                i++;
                continue;
            }

            i++;
        }

        return s[start..];
    }

    static void AppendHex(string s, ref int i, int digits, StringBuilder sb)
    {
        var start = i;
        var end = Math.Min(s.Length, i + digits);
        while (i < end && Uri.IsHexDigit(s[i]))
            i++;
        if (i - start != digits)
            return;
        if (int.TryParse(s[start..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            sb.Append((char)value);
    }

    // ---------------- 输出严格 JSON ----------------

    // JS 函数无法在 JSON 里求值，但 formatter/styler 必须保真：按 token 偏移切出**源码原文**
    // 存成 JSON 字符串，引擎（Jint）拿到的是与模板里一字不差的函数体；切片比逐 token 拼接更能保住换行与缩进。
    static void WriteValue(List<Token> tokens, ref int pos, StringBuilder sb, string source)
    {
        if (pos >= tokens.Count)
        {
            sb.Append("null");
            return;
        }

        var token = tokens[pos];
        switch (token.Kind)
        {
            case TokenKind.Punctuation:
                switch (token.Text)
                {
                    case "{":
                        WriteObject(tokens, ref pos, sb, source);
                        return;
                    case "[":
                        WriteArray(tokens, ref pos, sb, source);
                        return;
                    case "(":
                    {
                        var start = token.Position;
                        SkipBalanced(tokens, ref pos, "(", ")");
                        if (pos < tokens.Count && tokens[pos].Text == "=>")
                        {
                            SkipArrowBody(tokens, ref pos);
                            AppendSlice(sb, source, tokens, start, pos);
                            return;
                        }

                        sb.Append("null");
                        return;
                    }
                    default:
                        pos++;
                        sb.Append("null");
                        return;
                }

            case TokenKind.String:
                WriteStringExpression(tokens, ref pos, sb);
                return;

            case TokenKind.Number:
                pos++;
                sb.Append(token.Text);
                return;

            default:
            {
                var start = token.Position;
                pos++;
                switch (token.Text)
                {
                    case "true":
                        sb.Append("true");
                        return;
                    case "false":
                        sb.Append("false");
                        return;
                    case "null":
                    case "undefined":
                    case "NaN":
                    case "Infinity":
                        sb.Append("null");
                        return;
                    case "function":
                        SkipFunction(tokens, ref pos);
                        AppendSlice(sb, source, tokens, start, pos);
                        return;
                    default:
                        if (pos < tokens.Count && tokens[pos].Text == "=>")
                        {
                            SkipArrowBody(tokens, ref pos);
                            AppendSlice(sb, source, tokens, start, pos);
                            return;
                        }

                        AppendJsonString(sb, token.Text);
                        return;
                }
            }
        }
    }

    /// <summary>把 <c>source[start .. 消费终点)</c> 的函数源码原文切成 JSON 字符串输出。</summary>
    static void AppendSlice(StringBuilder sb, string source, List<Token> tokens, int start, int consumedTo)
    {
        var end = consumedTo < tokens.Count ? tokens[consumedTo].Position : source.Length;
        AppendJsonString(sb, source[start..end].TrimEnd());
    }

    static void WriteObject(List<Token> tokens, ref int pos, StringBuilder sb, string source)
    {
        pos++; // {
        sb.Append('{');
        var first = true;

        while (pos < tokens.Count)
        {
            if (tokens[pos].Kind == TokenKind.Punctuation && tokens[pos].Text == "}")
            {
                pos++;
                break;
            }

            // ...spread：展开表达式无法在 JSON 里表达，整段忽略
            if (pos + 2 < tokens.Count
                && tokens[pos].Text == "." && tokens[pos + 1].Text == "." && tokens[pos + 2].Text == ".")
            {
                pos += 3;
                if (pos < tokens.Count)
                    pos++;
                SkipToCommaOrClose(tokens, ref pos);
                ConsumeComma(tokens, ref pos);
                continue;
            }

            if (tokens[pos].Kind is TokenKind.String or TokenKind.Identifier or TokenKind.Number)
            {
                var key = tokens[pos].Text;
                pos++;

                if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Punctuation && tokens[pos].Text == ":")
                {
                    pos++;
                    if (!first)
                        sb.Append(',');
                    AppendJsonString(sb, key);
                    sb.Append(':');
                    WriteValue(tokens, ref pos, sb, source);
                    first = false;
                }
                else
                {
                    // 方法简写 / getter / setter：JSON 无法表达，跳过整段
                    SkipToCommaOrClose(tokens, ref pos);
                }
            }
            else
            {
                pos++;
            }

            ConsumeComma(tokens, ref pos);
        }

        sb.Append('}');
    }

    static void WriteArray(List<Token> tokens, ref int pos, StringBuilder sb, string source)
    {
        pos++; // [
        sb.Append('[');
        var first = true;

        while (pos < tokens.Count)
        {
            if (tokens[pos].Kind == TokenKind.Punctuation && tokens[pos].Text == "]")
            {
                pos++;
                break;
            }

            if (tokens[pos].Text == "." && pos + 2 < tokens.Count && tokens[pos + 1].Text == "." && tokens[pos + 2].Text == ".")
            {
                pos += 3;
                if (pos < tokens.Count)
                    pos++;
                SkipToCommaOrClose(tokens, ref pos);
                ConsumeComma(tokens, ref pos);
                continue;
            }

            if (!first)
                sb.Append(',');
            WriteValue(tokens, ref pos, sb, source);
            first = false;
            ConsumeComma(tokens, ref pos);
        }

        sb.Append(']');
    }

    static void WriteStringExpression(List<Token> tokens, ref int pos, StringBuilder sb)
    {
        var value = new StringBuilder(tokens[pos].Text);
        pos++;

        // JS 的 "a" + "b" 拼接：合并成一个字符串
        while (pos + 1 < tokens.Count
               && tokens[pos].Kind == TokenKind.Punctuation && tokens[pos].Text == "+"
               && tokens[pos + 1].Kind == TokenKind.String)
        {
            value.Append(tokens[pos + 1].Text);
            pos += 2;
        }

        AppendJsonString(sb, value.ToString());
    }

    static void SkipFunction(List<Token> tokens, ref int pos)
    {
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Identifier)
            pos++;

        if (pos < tokens.Count && tokens[pos].Text == "(")
            SkipBalanced(tokens, ref pos, "(", ")");

        if (pos < tokens.Count && tokens[pos].Text == "{")
        {
            SkipBalanced(tokens, ref pos, "{", "}");
            return;
        }

        SkipArrowBody(tokens, ref pos);
    }

    static void SkipArrowBody(List<Token> tokens, ref int pos)
    {
        if (pos < tokens.Count && tokens[pos].Text == "=>")
            pos++;

        if (pos >= tokens.Count)
            return;

        if (tokens[pos].Text == "{")
        {
            SkipBalanced(tokens, ref pos, "{", "}");
            return;
        }

        var depth = 0;
        while (pos < tokens.Count)
        {
            var token = tokens[pos];
            if (token.Kind == TokenKind.Punctuation)
            {
                switch (token.Text)
                {
                    case "(" or "[" or "{":
                        depth++;
                        break;
                    case ")" or "]" or "}":
                        if (depth == 0)
                            return;
                        depth--;
                        break;
                    case "," when depth == 0:
                        return;
                }
            }

            pos++;
        }
    }

    static void SkipBalanced(List<Token> tokens, ref int pos, string open, string close)
    {
        if (pos >= tokens.Count || tokens[pos].Text != open)
            return;

        var depth = 0;
        while (pos < tokens.Count)
        {
            if (tokens[pos].Text == open)
            {
                depth++;
            }
            else if (tokens[pos].Text == close)
            {
                depth--;
                pos++;
                if (depth == 0)
                    return;
                continue;
            }

            pos++;
        }
    }

    static void SkipToCommaOrClose(List<Token> tokens, ref int pos)
    {
        var depth = 0;
        while (pos < tokens.Count)
        {
            var token = tokens[pos];
            if (token.Kind == TokenKind.Punctuation)
            {
                switch (token.Text)
                {
                    case "(" or "[" or "{":
                        depth++;
                        break;
                    case ")" or "]" or "}":
                        if (depth == 0)
                            return;
                        depth--;
                        break;
                    case "," when depth == 0:
                        return;
                }
            }

            pos++;
        }
    }

    static void ConsumeComma(List<Token> tokens, ref int pos)
    {
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Punctuation && tokens[pos].Text == ",")
            pos++;
    }

    static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || char.IsSurrogate(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
