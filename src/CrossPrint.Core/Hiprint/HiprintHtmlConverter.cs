using System.Globalization;
using System.Text;

namespace CrossPrint.Core.Hiprint;

/// <summary>HTML 片段转出来的块类型。</summary>
internal enum HiprintHtmlPieceKind
{
    /// <summary>一段（多行）文本。</summary>
    Text,

    /// <summary>一张图片。</summary>
    Image,

    /// <summary>一张表格。</summary>
    Table,
}

/// <summary>HTML 转换后的一个排版块（由导入器负责落成具体元素）。</summary>
internal sealed class HiprintHtmlPiece
{
    /// <summary>块类型。</summary>
    public HiprintHtmlPieceKind Kind { get; init; }

    /// <summary>文本内容；表格块为空。</summary>
    public string Text { get; init; } = "";

    /// <summary>块级字号（pt）。</summary>
    public double FontSizePt { get; init; } = 12;

    /// <summary>粗体。</summary>
    public bool Bold { get; init; }

    /// <summary>斜体。</summary>
    public bool Italic { get; init; }

    /// <summary>下划线。</summary>
    public bool Underline { get; init; }

    /// <summary>水平对齐。</summary>
    public string TextAlign { get; init; } = "left";

    /// <summary>字色（HTML 写法解析失败时为 null）。</summary>
    public string? Color { get; init; }

    /// <summary>图片地址（<c>src</c>）。</summary>
    public string? Src { get; init; }

    /// <summary>图片宽（pt，缺省 null 表示按盒子宽度）。</summary>
    public double? WidthPt { get; init; }

    /// <summary>图片高（pt，缺省 null 表示按宽度等比/方形）。</summary>
    public double? HeightPt { get; init; }

    /// <summary>表格行（每行是单元格文本）。</summary>
    public List<List<string>> Rows { get; } = [];

    /// <summary>首行是否为表头（<c>th</c> 或 <c>thead</c>）。</summary>
    public bool FirstRowIsHeader { get; init; }
}

/// <summary>
/// hiprint <c>html</c> 元素的 HTML/CSS 子集解析器：把标记拆成"文本 / 图片 / 表格"三类块，
/// 由导入器落成内置元素（CrossPrint 没有浏览器内核，不渲染任意 HTML/CSS）。
/// </summary>
/// <remarks>
/// 支持：<c>p/div/h1-h6/li/section/article/header/footer/center/pre</c> 等块级容器、
/// <c>br</c> 换行、<c>b/strong/i/em/u/ins/span/font</c> 内联样式、
/// <c>img[src,width,height,style]</c>、<c>table/tr/th/td</c>（表头取 <c>th</c> 或首行）、
/// 以及 <c>style</c> 里的 <c>font-size/font-weight/font-style/text-decoration/color/text-align</c>。
/// 其它标签按透明容器处理（内容照排），<c>script/style</c> 整段丢弃。
/// </remarks>
internal static class HiprintHtmlConverter
{
    /// <summary>默认字号（pt），与文本元素缺省一致。</summary>
    const double DefaultFontSizePt = 12;

    /// <summary>把 HTML 转成排版块。空内容返回空列表；任何畸形输入都不抛异常。</summary>
    public static List<HiprintHtmlPiece> Convert(string? html)
    {
        var pieces = new List<HiprintHtmlPiece>();
        if (string.IsNullOrWhiteSpace(html))
            return pieces;

        try
        {
            ConvertCore(html!, pieces);
        }
        catch (Exception)
        {
            // 解析器必须"尽力而为"：畸形 HTML 不能把整份模板导入拖挂。
        }

        return pieces;
    }

    static void ConvertCore(string html, List<HiprintHtmlPiece> pieces)
    {
        var state = new StyleState();
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            var text = Normalize(buffer.ToString());
            buffer.Clear();
            if (text.Length == 0)
                return;

            pieces.Add(new HiprintHtmlPiece
            {
                Kind = HiprintHtmlPieceKind.Text,
                Text = text,
                FontSizePt = state.FontSizePt,
                Bold = state.Bold,
                Italic = state.Italic,
                Underline = state.Underline,
                TextAlign = state.TextAlign,
                Color = state.Color,
            });
        }

        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AppendText(buffer, html.Substring(i));
                break;
            }

            if (lt > i)
                AppendText(buffer, html.Substring(i, lt - i));

            if (IsAt(html, lt, "<!--"))
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var gt = FindTagEnd(html, lt);
            if (gt < 0)
            {
                AppendText(buffer, html.Substring(lt));
                break;
            }

            var raw = html.Substring(lt + 1, gt - lt - 1);
            i = gt + 1;

            var (name, closing, selfClosing, attributes) = ParseTag(raw);
            if (name.Length == 0)
            {
                AppendText(buffer, "<");
                continue;
            }

            switch (name)
            {
                case "script":
                case "style":
                case "head":
                case "title":
                {
                    if (closing)
                        break;
                    var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    var closeEnd = close < 0 ? html.Length : html.IndexOf('>', close);
                    i = closeEnd < 0 ? html.Length : closeEnd + 1;
                    break;
                }

                case "br":
                    buffer.Append('\n');
                    break;

                case "img":
                {
                    Flush();
                    if (!closing)
                        AddImage(pieces, attributes);
                    break;
                }

                case "table":
                {
                    Flush();
                    if (closing)
                        break;
                    var table = ReadTable(html, ref i);
                    if (table is not null)
                        pieces.Add(table);
                    break;
                }

                case "tr":
                case "td":
                case "th":
                case "thead":
                case "tbody":
                case "tfoot":
                    // 游离的行/单元格（不在 table 里）：内容照排，不特殊处理
                    if (!closing)
                        buffer.Append(' ');
                    break;

                default:
                {
                    var isBlock = BlockTags.Contains(name);
                    if (closing)
                    {
                        if (isBlock)
                            Flush();
                        state.Pop(name);
                        break;
                    }

                    if (isBlock)
                        Flush();

                    var style = attributes.TryGetValue("style", out var css) ? css : null;
                    state.Push(name, style, attributes);

                    // 显式对齐属性（老模板常用 align="center"）
                    if (attributes.TryGetValue("align", out var align) && !string.IsNullOrWhiteSpace(align))
                        state.TextAlign = align!.Trim().ToLowerInvariant();

                    if (selfClosing)
                    {
                        state.Pop(name);
                        if (isBlock)
                            Flush();
                    }

                    break;
                }
            }
        }

        Flush();
    }

    // ---------------- 标签解析 ----------------

    /// <summary>块级标签：进出时断开当前文本块。</summary>
    static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "dl", "dt", "dd",
        "section", "article", "header", "footer", "center", "pre", "blockquote", "figure",
        "figcaption", "main", "aside", "nav", "form", "fieldset", "address", "hr",
    };

    static bool IsAt(string html, int index, string token)
        => index + token.Length <= html.Length
           && string.CompareOrdinal(html, index, token, 0, token.Length) == 0;

    /// <summary>找标签的结束 <c>&gt;</c>，跳过节内引号里的 <c>&gt;</c>（属性值里常有）。</summary>
    static int FindTagEnd(string html, int lt)
    {
        var quote = '\0';
        for (var k = lt + 1; k < html.Length; k++)
        {
            var c = html[k];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return k;
        }

        return -1;
    }

    static (string Name, bool Closing, bool SelfClosing, Dictionary<string, string> Attributes) ParseTag(string raw)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = raw.Trim();
        var closing = text.StartsWith("/", StringComparison.Ordinal);
        if (closing)
            text = text[1..].TrimStart();

        var selfClosing = text.EndsWith("/", StringComparison.Ordinal);
        if (selfClosing)
            text = text[..^1].TrimEnd();

        var name = ReadName(text, 0, out var next);
        name = name.ToLowerInvariant();

        while (next < text.Length)
        {
            while (next < text.Length && char.IsWhiteSpace(text[next]))
                next++;
            if (next >= text.Length)
                break;

            var key = ReadName(text, next, out var afterKey).ToLowerInvariant();
            next = afterKey;
            if (key.Length == 0)
            {
                next++;
                continue;
            }

            while (next < text.Length && char.IsWhiteSpace(text[next]))
                next++;

            string value;
            if (next < text.Length && text[next] == '=')
            {
                next++;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                    next++;

                if (next < text.Length && text[next] is '"' or '\'')
                {
                    var quote = text[next++];
                    var begin = next;
                    while (next < text.Length && text[next] != quote)
                        next++;
                    value = text[begin..Math.Min(next, text.Length)];
                    if (next < text.Length)
                        next++;
                }
                else
                {
                    var begin = next;
                    while (next < text.Length && !char.IsWhiteSpace(text[next]))
                        next++;
                    value = text[begin..next];
                }
            }
            else
            {
                value = "true";
            }

            attributes[key] = Decode(value);
        }

        return (name, closing, selfClosing, attributes);
    }

    static string ReadName(string text, int start, out int next)
    {
        var k = start;
        while (k < text.Length && !char.IsWhiteSpace(text[k]) && text[k] != '=' && text[k] != '/')
            k++;
        next = k;
        return text[start..k];
    }

    // ---------------- 图片 ----------------

    static void AddImage(List<HiprintHtmlPiece> pieces, Dictionary<string, string> attributes)
    {
        attributes.TryGetValue("src", out var src);
        if (string.IsNullOrWhiteSpace(src))
            return;

        double? width = LengthToPt(attributes.TryGetValue("width", out var rawWidth) ? rawWidth : null);
        double? height = LengthToPt(attributes.TryGetValue("height", out var rawHeight) ? rawHeight : null);
        if (attributes.TryGetValue("style", out var css))
        {
            var style = ParseStyle(css);
            width ??= LengthToPt(style.TryGetValue("width", out var styleWidth) ? styleWidth : null);
            height ??= LengthToPt(style.TryGetValue("height", out var styleHeight) ? styleHeight : null);
        }

        pieces.Add(new HiprintHtmlPiece
        {
            Kind = HiprintHtmlPieceKind.Image,
            Src = src!.Trim(),
            WidthPt = width,
            HeightPt = height,
        });
    }

    // ---------------- 表格 ----------------

    /// <summary>就地读一张表（<paramref name="i"/> 指向 <c>&lt;table&gt;</c> 之后），返回表格块。</summary>
    static HiprintHtmlPiece? ReadTable(string html, ref int i)
    {
        var depth = 1;
        var end = html.Length;
        var cursor = i;
        while (cursor < html.Length)
        {
            var lt = html.IndexOf('<', cursor);
            if (lt < 0)
                break;
            var gt = FindTagEnd(html, lt);
            if (gt < 0)
                break;

            var (name, closing, selfClosing, _) = ParseTag(html.Substring(lt + 1, gt - lt - 1));
            if (name == "table" && !selfClosing)
            {
                depth += closing ? -1 : 1;
                if (depth == 0)
                {
                    end = lt;
                    cursor = gt + 1;
                    break;
                }
            }

            cursor = gt + 1;
        }

        var body = html.Substring(i, Math.Max(0, end - i));
        i = cursor;

        var rows = new List<List<string>>();
        var firstRowHeader = false;

        foreach (var rowMarkup in SplitTop(body, "tr", out var rowSelfClosing))
        {
            _ = rowSelfClosing;
            var cells = new List<string>();
            var header = false;
            foreach (var (isHeader, cellMarkup) in SplitCells(rowMarkup))
            {
                header |= isHeader;
                cells.Add(ToPlainText(cellMarkup));
            }

            if (cells.Count == 0)
                continue;

            if (rows.Count == 0)
                firstRowHeader = header;
            rows.Add(cells);
        }

        if (rows.Count == 0)
            return null;

        var piece = new HiprintHtmlPiece
        {
            Kind = HiprintHtmlPieceKind.Table,
            FirstRowIsHeader = firstRowHeader,
        };
        foreach (var row in rows)
            piece.Rows.Add(row);
        return piece;
    }

    /// <summary>按顶层标签切分（跳过嵌套同名标签）。</summary>
    static List<string> SplitTop(string html, string tag, out bool anySelfClosing)
    {
        anySelfClosing = false;
        var result = new List<string>();
        var depth = 0;
        var start = -1;
        var cursor = 0;

        while (cursor < html.Length)
        {
            var lt = html.IndexOf('<', cursor);
            if (lt < 0)
                break;
            var gt = FindTagEnd(html, lt);
            if (gt < 0)
                break;

            var (name, closing, selfClosing, _) = ParseTag(html.Substring(lt + 1, gt - lt - 1));
            if (name == tag)
            {
                anySelfClosing |= selfClosing;
                if (selfClosing)
                {
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(html.Substring(start, gt + 1 - start));
                        start = -1;
                    }
                }
                else if (!closing)
                {
                    if (depth == 0)
                        start = lt;
                    depth++;
                }
                else
                {
                    depth--;
                    if (depth <= 0)
                    {
                        depth = 0;
                        if (start >= 0)
                            result.Add(html.Substring(start, gt + 1 - start));
                        start = -1;
                    }
                }
            }

            cursor = gt + 1;
        }

        if (start >= 0)
            result.Add(html[start..]);

        return result;
    }

    static List<(bool IsHeader, string Content)> SplitCells(string rowMarkup) => SplitCellTags(rowMarkup);

    static List<(bool IsHeader, string Content)> SplitCellTags(string markup)
    {
        var result = new List<(bool, string)>();
        var cursor = 0;
        while (cursor < markup.Length)
        {
            var lt = markup.IndexOf('<', cursor);
            if (lt < 0)
                break;
            var gt = FindTagEnd(markup, lt);
            if (gt < 0)
                break;

            var (name, closing, _, _) = ParseTag(markup.Substring(lt + 1, gt - lt - 1));
            if (!closing && (name == "td" || name == "th"))
            {
                var closeTag = "</" + name;
                var close = markup.IndexOf(closeTag, gt + 1, StringComparison.OrdinalIgnoreCase);
                var cellEnd = close < 0 ? markup.Length : close;
                result.Add((name == "th", markup.Substring(gt + 1, Math.Max(0, cellEnd - gt - 1))));
                cursor = close < 0 ? markup.Length : close;
                continue;
            }

            cursor = gt + 1;
        }

        return result;
    }

    // ---------------- 文本工具 ----------------

    /// <summary>去标签、解实体、把块级边界与 <c>br</c> 变成换行，得到纯文本。</summary>
    static string ToPlainText(string markup)
    {
        var sb = new StringBuilder();
        var cursor = 0;
        while (cursor < markup.Length)
        {
            var lt = markup.IndexOf('<', cursor);
            if (lt < 0)
            {
                sb.Append(Decode(markup[cursor..]));
                break;
            }

            if (lt > cursor)
                sb.Append(Decode(markup[cursor..lt]));

            var gt = FindTagEnd(markup, lt);
            if (gt < 0)
            {
                sb.Append(Decode(markup[lt..]));
                break;
            }

            var (name, closing, _, _) = ParseTag(markup.Substring(lt + 1, gt - lt - 1));
            if (name is "br" || (BlockTags.Contains(name) && !closing))
                sb.Append('\n');

            cursor = gt + 1;
        }

        return Normalize(sb.ToString());
    }

    static void AppendText(StringBuilder buffer, string raw) => buffer.Append(Decode(raw));

    /// <summary>HTML 空白折叠：连续空白（含换行）压成一个空格，<c>&lt;br&gt;</c> 产生的换行保留。</summary>
    static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                // 去掉行尾空格再写换行
                while (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.Append('\n');
                lastWasSpace = true;
                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\u00A0')
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\n'))
            sb.Length--;

        return sb.ToString();
    }

    /// <summary>解 HTML 实体（命名实体取常见集合，数字实体按十进制/十六进制）。</summary>
    static string Decode(string text)
    {
        if (!text.Contains('&'))
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            var semi = text.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 10)
            {
                sb.Append(text[i]);
                continue;
            }

            var entity = text[(i + 1)..semi];
            var decoded = DecodeEntity(entity);
            if (decoded is null)
            {
                sb.Append(text[i]);
                continue;
            }

            sb.Append(decoded);
            i = semi;
        }

        return sb.ToString();
    }

    static string? DecodeEntity(string entity)
    {
        if (entity.Length == 0)
            return null;

        if (entity[0] == '#')
        {
            var number = entity.Length > 1 && entity[1] is 'x' or 'X'
                ? (int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0)
                : (int.TryParse(entity[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0);
            return number is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(number) : null;
        }

        return entity switch
        {
            "nbsp" => " ",
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "ensp" => " ",
            "emsp" => " ",
            "middot" => "·",
            "times" => "×",
            "divide" => "÷",
            "deg" => "°",
            "plusmn" => "±",
            "yen" => "￥",
            "copy" => "©",
            "reg" => "®",
            "trade" => "™",
            "mdash" => "—",
            "ndash" => "–",
            "hellip" => "…",
            _ => null,
        };
    }

    // ---------------- 样式 ----------------

    /// <summary>内联样式状态的栈式继承（进入标签压栈、离开标签出栈）。</summary>
    sealed class StyleState
    {
        readonly List<(string Tag, Snapshot Snapshot)> _stack = [];

        public double FontSizePt { get; set; } = DefaultFontSizePt;

        public bool Bold { get; set; }

        public bool Italic { get; set; }

        public bool Underline { get; set; }

        public string TextAlign { get; set; } = "left";

        public string? Color { get; set; }

        public void Push(string tag, string? css, Dictionary<string, string> attributes)
        {
            _stack.Add((tag, new Snapshot(FontSizePt, Bold, Italic, Underline, TextAlign, Color)));

            switch (tag)
            {
                case "b" or "strong":
                    Bold = true;
                    break;
                case "i" or "em":
                    Italic = true;
                    break;
                case "u" or "ins":
                    Underline = true;
                    break;
                case "center":
                    TextAlign = "center";
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Bold = true;
                    FontSizePt = tag switch
                    {
                        "h1" => 24,
                        "h2" => 20,
                        "h3" => 17,
                        "h4" => 15,
                        "h5" => 13,
                        _ => 12,
                    };
                    break;
            }

            if (attributes.TryGetValue("color", out var color) && !string.IsNullOrWhiteSpace(color))
                Color = color!.Trim();

            if (attributes.TryGetValue("size", out var size) && double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var legacySize))
            {
                // <font size="1..7"> 是浏览器旧式字号：3 = 默认 12pt
                FontSizePt = legacySize switch
                {
                    <= 1 => 8,
                    <= 2 => 10,
                    <= 3 => 12,
                    <= 4 => 14,
                    <= 5 => 18,
                    <= 6 => 24,
                    _ => 32,
                };
            }

            if (css is null)
                return;

            var style = ParseStyle(css);
            if (style.TryGetValue("font-size", out var fontSizeRaw) && LengthToPt(fontSizeRaw) is { } fontSize)
                FontSizePt = fontSize;
            if (style.TryGetValue("font-weight", out var weight)
                && (weight.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)
                    || (int.TryParse(weight, out var w) && w >= 600)))
                Bold = true;
            if (style.TryGetValue("font-style", out var fontStyle)
                && fontStyle.Trim().StartsWith("italic", StringComparison.OrdinalIgnoreCase))
                Italic = true;
            if (style.TryGetValue("text-decoration", out var decoration)
                && decoration.IndexOf("underline", StringComparison.OrdinalIgnoreCase) >= 0)
                Underline = true;
            if (style.TryGetValue("text-align", out var align) && !string.IsNullOrWhiteSpace(align))
                TextAlign = align.Trim().ToLowerInvariant();
            if (style.TryGetValue("color", out var cssColor) && !string.IsNullOrWhiteSpace(cssColor))
                Color = cssColor.Trim();
        }

        public void Pop(string tag)
        {
            for (var i = _stack.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_stack[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
                    continue;

                var snapshot = _stack[i].Snapshot;
                FontSizePt = snapshot.FontSizePt;
                Bold = snapshot.Bold;
                Italic = snapshot.Italic;
                Underline = snapshot.Underline;
                TextAlign = snapshot.TextAlign;
                Color = snapshot.Color;
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }
        }

        readonly record struct Snapshot(double FontSizePt, bool Bold, bool Italic, bool Underline, string TextAlign, string? Color);
    }

    /// <summary>解析 <c>style="a:b;c:d"</c>。</summary>
    static Dictionary<string, string> ParseStyle(string css)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in css.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
                result[key] = value;
        }

        return result;
    }

    /// <summary>CSS 长度转 pt：<c>12pt / 16px / 1.2em / 100%</c>；纯数字按 px 处理。</summary>
    static double? LengthToPt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim().ToLowerInvariant();
        var number = 0.0;
        var unit = "";

        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+'))
            end++;
        if (!double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return null;
        unit = text[end..].Trim();

        var pt = unit switch
        {
            "pt" => number,
            "px" or "" => number * 0.75,
            "em" or "rem" => number * DefaultFontSizePt,
            "%" => number * DefaultFontSizePt / 100,
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "in" => number * 72,
            "pc" => number * 12,
            _ => double.NaN,
        };

        return double.IsNaN(pt) || pt <= 0 ? null : pt;
    }
}
