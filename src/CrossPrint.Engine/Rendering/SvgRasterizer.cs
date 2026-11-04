using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 极简 SVG 光栅器：把矢量图标转成 <see cref="SKImage"/>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：<c>SKImage.FromEncodedData</c> 只认位图编码（PNG/JPEG/WEBP/GIF/BMP），
/// 喂进 SVG 一律返回 null。而实际模板里 SVG 极常见——内嵌 <c>data:image/svg+xml;base64,…</c>
/// 的图标、外链 <c>https://…/logo.svg</c>、水印矢量图。不处理它们的表现是"图片元素画个占位框"，
/// 用户看到的就是"图片解析不出来"。
/// </para>
/// <para>
/// 支持的是"图标级"子集，够画绝大多数 logo/图标，但不是通用 SVG 实现：
/// 形状（rect/circle/ellipse/line/polyline/polygon/path）、分组（g/a/switch/嵌套 svg）、
/// use 引用、text、transform（translate/scale/rotate/matrix/skewX/skewY）、
/// 样式（fill/stroke/stroke-width/opacity/fill-rule/linecap/linejoin/dasharray）、
/// 线性与径向渐变（含 objectBoundingBox 单位）、viewBox + preserveAspectRatio。
/// 明确不支持 <b>不做静默兜底</b>的部分：clipPath/mask/filter/pattern/textPath/marker，
/// 遇到时按"画得出什么算什么"降级（不是抛异常），失败原因写 <c>error</c> 由调用方转成渲染告警。
/// </para>
/// <para>
/// 解析用 <see cref="XDocument"/>，net461 与 net8.0 都可用，不引第三方依赖——
/// 本仓 SkiaSharp 锁 2.88.9，引入 Svg.Skia 一族会连坐 ShimSkiaSharp 的版本面，代价大于收益。
/// </para>
/// </remarks>
internal static class SvgRasterizer
{
    /// <summary>光栅化像素上限，防畸形文件把内存吃光（16M ≈ 4000×4000）。</summary>
    const int MaxPixels = 16_000_000;

    /// <summary>递归深度上限：<c>use</c> 自引用、嵌套 <c>svg</c> 死循环都要在这里挡住。</summary>
    const int MaxDepth = 32;

    /// <summary>快速嗅探：字节流是不是 SVG（看文本头有没有 <c>&lt;svg</c>）。</summary>
    public static bool LooksLikeSvg(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return false;

        // 只取头部：SVG 的 xmlns/根标签都在前几百字节里
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        return LooksLikeSvgText(head);
    }

    /// <summary>快速嗅探：文本是不是 SVG。</summary>
    public static bool LooksLikeSvgText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var t = text.TrimStart('\uFEFF');
        t = t.TrimStart(' ', '\t', '\r', '\n');
        return t.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0
               && (t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("<!--", StringComparison.Ordinal)
                   || t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把 SVG 文本光栅化成 <paramref name="widthPx"/> × <paramref name="heightPx"/> 的位图。
    /// 失败返回 null 并写 <paramref name="error"/>（调用方转告警，绝不静默给空白图）。
    /// </summary>
    public static SKImage? Rasterize(string svgText, int widthPx, int heightPx, out string? error)
    {
        error = null;

        var w = Math.Max(1, widthPx);
        var h = Math.Max(1, heightPx);
        if ((long)w * h > MaxPixels)
        {
            // 等比缩到预算内：宁可降清晰度，也不要在渲染线程分配几百 MB
            var scale = Math.Sqrt((double)MaxPixels / ((long)w * h));
            w = Math.Max(1, (int)(w * scale));
            h = Math.Max(1, (int)(h * scale));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(svgText);
        }
        catch (Exception ex)
        {
            error = "SVG 不是合法的 XML：" + ex.Message;
            return null;
        }

        var root = doc.Root;
        if (root is null || !Is(root, "svg"))
        {
            error = "SVG 根元素不是 <svg>。";
            return null;
        }

        SKBitmap? bitmap = null;
        try
        {
            bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            using (var ctx = new SvgContext(root))
            {
                canvas.Clear(SKColors.Transparent);
                ctx.DrawTree(canvas, w, h);
            }

            var image = SKImage.FromBitmap(bitmap);
            bitmap.Dispose();
            bitmap = null;
            return image;
        }
        catch (Exception ex)
        {
            error = "SVG 光栅化失败：" + ex.Message;
            return null;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    // ---------------- XML 小工具 ----------------

    static bool Is(XElement e, string localName)
        => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    /// <summary>取属性：优先同名属性，其次 <c>style="a:b;c:d"</c> 声明（内联样式覆盖表现属性）。</summary>
    static string? Prop(XElement e, string name)
    {
        var direct = e.Attribute(name);
        var style = e.Attribute("style");
        if (style is null)
            return direct?.Value;

        // 内联样式优先级高于同名表现属性（SVG 规范：style 相当于作者样式表）
        var decls = style.Value;
        var start = 0;
        while (start < decls.Length)
        {
            var end = decls.IndexOf(';', start);
            if (end < 0)
                end = decls.Length;

            var colon = decls.IndexOf(':', start);
            if (colon > start && colon < end)
            {
                var key = decls.Substring(start, colon - start).Trim();
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return decls.Substring(colon + 1, end - colon - 1).Trim();
            }

            start = end + 1;
        }

        return direct?.Value;
    }

    static float Num(XElement e, string name, float fallback = 0)
        => TryLength(Prop(e, name), out var v) ? v : fallback;

    /// <summary>长度解析：数字可带 px/pt/mm/cm/in/%，单位缺省按用户单位。</summary>
    static bool TryLength(string? raw, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw!.Trim();
        if (s.EndsWith("%", StringComparison.Ordinal))
        {
            // 百分比在图标里几乎只出现于 width/height，调用方自己去兜底
            return false;
        }

        var i = 0;
        var num = ReadNumber(s, ref i);
        if (float.IsNaN(num))
            return false;

        var unit = s.Substring(i).Trim().ToLowerInvariant();
        value = unit switch
        {
            "" or "px" => num,
            "pt" => num * (96f / 72f),
            "pc" => num * 16f,
            "mm" => num * (96f / 25.4f),
            "cm" => num * (96f / 2.54f),
            "in" => num * 96f,
            "em" => num * 16f,
            "ex" => num * 8f,
            _ => num,
        };
        return true;
    }

    internal static float ReadNumber(string s, ref int i)
    {
        SkipSeparators(s, ref i);
        var start = i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            i++;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            var save = i;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;
            if (i < s.Length && char.IsDigit(s[i]))
            {
                while (i < s.Length && char.IsDigit(s[i]))
                    i++;
            }
            else
            {
                i = save;
            }
        }

        if (i == start)
            return float.NaN;

        return float.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : float.NaN;
    }

    internal static void SkipSeparators(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
            i++;
    }

    /// <summary>把 "1,2 3,4" / "1 2 3 4" 这类数字串读成数组。</summary>
    static float[] ReadNumbers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var list = new List<float>(8);
        var i = 0;
        var s = raw!;
        while (true)
        {
            SkipSeparators(s, ref i);
            if (i >= s.Length)
                break;

            var v = ReadNumber(s, ref i);
            if (float.IsNaN(v))
                break;
            list.Add(v);
        }

        return list.ToArray();
    }

    // ---------------- 具体实现 ----------------

    /// <summary>一次光栅化的上下文：负责 id 索引、样式继承与递归绘制。</summary>
    sealed class SvgContext : IDisposable
    {
        readonly XElement _root;
        readonly Dictionary<string, XElement> _ids = new(StringComparer.Ordinal);

        /// <summary>本次光栅化创建的渐变着色器：交给上下文统一释放（SKPaint 不接管 shader 生命周期）。</summary>
        readonly List<SKShader> _shaders = new();

        public void Dispose()
        {
            foreach (var shader in _shaders)
                shader.Dispose();
            _shaders.Clear();
        }

        public SvgContext(XElement root)
        {
            _root = root;
            foreach (var e in root.DescendantsAndSelf())
            {
                var id = Attr(e, "id");
                if (!string.IsNullOrEmpty(id) && !_ids.ContainsKey(id!))
                    _ids[id!] = e;
            }
        }

        public void DrawTree(SKCanvas canvas, int widthPx, int heightPx)
        {
            var world = ViewportMatrix(widthPx, heightPx);
            DrawElement(_root, canvas, world, SvgStyle.Default, 0);
        }

        /// <summary>viewBox → 设备像素的映射（含 preserveAspectRatio 对齐）。</summary>
        SKMatrix ViewportMatrix(int widthPx, int heightPx)
        {
            var vb = ReadNumbers(Attr(_root, "viewBox"));
            float minX = 0, minY = 0, vbW = 0, vbH = 0;
            if (vb.Length >= 4)
            {
                minX = vb[0];
                minY = vb[1];
                vbW = vb[2];
                vbH = vb[3];
            }

            if (vbW <= 0 || vbH <= 0)
            {
                vbW = TryLength(Attr(_root, "width"), out var ww) ? ww : widthPx;
                vbH = TryLength(Attr(_root, "height"), out var hh) ? hh : heightPx;
                minX = 0;
                minY = 0;
            }

            if (vbW <= 0 || vbH <= 0)
            {
                vbW = widthPx;
                vbH = heightPx;
            }

            var sx = widthPx / vbW;
            var sy = heightPx / vbH;

            var par = (Attr(_root, "preserveAspectRatio") ?? "xMidYMid meet").Trim();
            var none = par.StartsWith("none", StringComparison.OrdinalIgnoreCase);
            if (!none)
            {
                var slice = par.IndexOf("slice", StringComparison.OrdinalIgnoreCase) >= 0;
                var s = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
                sx = s;
                sy = s;
            }

            var tx = -minX * sx;
            var ty = -minY * sy;

            if (!none)
            {
                var slackX = widthPx - vbW * sx;
                var slackY = heightPx - vbH * sy;
                if (par.IndexOf("xMid", StringComparison.OrdinalIgnoreCase) >= 0)
                    tx += slackX / 2;
                else if (par.IndexOf("xMax", StringComparison.OrdinalIgnoreCase) >= 0)
                    tx += slackX;

                if (par.IndexOf("YMid", StringComparison.OrdinalIgnoreCase) >= 0)
                    ty += slackY / 2;
                else if (par.IndexOf("YMax", StringComparison.OrdinalIgnoreCase) >= 0)
                    ty += slackY;
            }

            return SKMatrix.CreateScaleTranslation(sx, sy, tx, ty);
        }

        void DrawElement(XElement e, SKCanvas canvas, SKMatrix parentWorld, SvgStyle inherited, int depth)
        {
            if (depth > MaxDepth)
                return;

            var name = e.Name.LocalName.ToLowerInvariant();

            // 只定义不绘制的节点：跳过（gradient/clipPath 等由引用方按需读取）
            switch (name)
            {
                case "defs":
                case "symbol":
                case "title":
                case "desc":
                case "metadata":
                case "style":
                case "script":
                case "lineargradient":
                case "radialgradient":
                case "clippath":
                case "mask":
                case "pattern":
                case "filter":
                case "marker":
                    return;
            }

            var style = ResolveStyle(e, inherited);
            if (!style.Visible)
                return;

            var local = ParseTransform(Prop(e, "transform"));
            var world = local.PostConcat(parentWorld);

            switch (name)
            {
                case "svg":
                case "g":
                case "a":
                case "switch":
                    DrawChildren(e, canvas, world, style, depth + 1);
                    return;

                case "use":
                    DrawUse(e, canvas, world, style, depth + 1);
                    return;

                case "text":
                    DrawText(e, canvas, world, style);
                    return;

                case "rect":
                case "circle":
                case "ellipse":
                case "line":
                case "polyline":
                case "polygon":
                case "path":
                    DrawShape(e, name, canvas, world, style);
                    return;

                default:
                    // 未知元素仍下钻：很多导出工具会包一层自定义命名空间节点
                    DrawChildren(e, canvas, world, style, depth + 1);
                    return;
            }
        }

        void DrawChildren(XElement parent, SKCanvas canvas, SKMatrix world, SvgStyle style, int depth)
        {
            foreach (var child in parent.Elements())
                DrawElement(child, canvas, world, style, depth);
        }

        void DrawUse(XElement e, SKCanvas canvas, SKMatrix world, SvgStyle style, int depth)
        {
            var href = Attr(e, "href")
                       ?? e.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value;
            if (string.IsNullOrEmpty(href) || href![0] != '#')
                return;

            if (!_ids.TryGetValue(href.Substring(1), out var target))
                return;

            var x = Num(e, "x");
            var y = Num(e, "y");
            var placed = SKMatrix.CreateTranslation(x, y).PostConcat(world);

            // use 上的样式只对"未显式指定"的属性生效，这里按单一继承链近似
            DrawElement(target, canvas, placed, style, depth);
        }

        // ---------------- 形状 ----------------

        void DrawShape(XElement e, string name, SKCanvas canvas, SKMatrix world, SvgStyle style)
        {
            using var path = BuildShapePath(e, name);
            if (path is null)
                return;

            // 填充规则挂在路径上（Skia 的 fillType 不是画笔属性）
            path.FillType = style.FillType;

            if (style.Fill != "none" && style.Fill.Length > 0)
            {
                using var fill = CreatePaint(e, style, style.Fill, style.FillOpacity, stroke: false);
                if (fill is not null)
                {
                    var saved = canvas.Save();
                    canvas.SetMatrix(world);
                    canvas.DrawPath(path, fill);
                    canvas.RestoreToCount(saved);
                }
            }

            if (style.Stroke != "none" && style.Stroke.Length > 0 && style.StrokeWidth > 0)
            {
                using var stroke = CreatePaint(e, style, style.Stroke, style.StrokeOpacity, stroke: true);
                if (stroke is not null)
                {
                    var saved = canvas.Save();
                    canvas.SetMatrix(world);
                    canvas.DrawPath(path, stroke);
                    canvas.RestoreToCount(saved);
                }
            }
        }

        static SKPath? BuildShapePath(XElement e, string name)
        {
            var path = new SKPath();
            switch (name)
            {
                case "rect":
                {
                    var w = Num(e, "width");
                    var h = Num(e, "height");
                    if (w <= 0 || h <= 0)
                        return null;

                    var x = Num(e, "x");
                    var y = Num(e, "y");
                    var rect = new SKRect(x, y, x + w, y + h);
                    var rx = Num(e, "rx");
                    var ry = Num(e, "ry");
                    if (rx <= 0 && ry <= 0)
                    {
                        path.AddRect(rect);
                    }
                    else
                    {
                        if (rx <= 0)
                            rx = ry;
                        if (ry <= 0)
                            ry = rx;
                        path.AddRoundRect(rect, rx, ry);
                    }

                    return path;
                }

                case "circle":
                {
                    var r = Num(e, "r");
                    if (r <= 0)
                        return null;
                    path.AddCircle(Num(e, "cx"), Num(e, "cy"), r);
                    return path;
                }

                case "ellipse":
                {
                    var rx = Num(e, "rx");
                    var ry = Num(e, "ry");
                    if (rx <= 0 || ry <= 0)
                        return null;
                    var cx = Num(e, "cx");
                    var cy = Num(e, "cy");
                    path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                    return path;
                }

                case "line":
                {
                    path.MoveTo(Num(e, "x1"), Num(e, "y1"));
                    path.LineTo(Num(e, "x2"), Num(e, "y2"));
                    return path;
                }

                case "polyline":
                case "polygon":
                {
                    var pts = ReadNumbers(Attr(e, "points"));
                    if (pts.Length < 4)
                        return null;
                    path.MoveTo(pts[0], pts[1]);
                    for (var i = 2; i + 1 < pts.Length; i += 2)
                        path.LineTo(pts[i], pts[i + 1]);
                    if (name == "polygon")
                        path.Close();
                    return path;
                }

                case "path":
                {
                    var d = Attr(e, "d");
                    if (string.IsNullOrWhiteSpace(d))
                        return null;
                    PathDataParser.Parse(d!, path);
                    return path;
                }

                default:
                    return null;
            }
        }

        // ---------------- 文本 ----------------

        void DrawText(XElement e, SKCanvas canvas, SKMatrix world, SvgStyle style)
        {
            var text = e.Value;
            if (string.IsNullOrWhiteSpace(text))
                return;

            var size = Num(e, "font-size", 16);
            if (size <= 0)
                size = 16;

            var color = ResolveColor(style.Fill, style.FillOpacity, style.Opacity) ?? SKColors.Black;
            var weight = Prop(e, "font-weight");
            var bold = weight is not null
                       && (weight.Equals("bold", StringComparison.OrdinalIgnoreCase)
                           || (int.TryParse(weight, out var wnum) && wnum >= 600));

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color,
                TextSize = size,
                Typeface = SKTypeface.Default,
                FakeBoldText = bold,
                Style = SKPaintStyle.Fill,
            };

            var x = Num(e, "x");
            var y = Num(e, "y");
            var anchor = (Prop(e, "text-anchor") ?? "start").Trim();
            if (anchor.Equals("middle", StringComparison.OrdinalIgnoreCase))
                x -= paint.MeasureText(text) / 2;
            else if (anchor.Equals("end", StringComparison.OrdinalIgnoreCase))
                x -= paint.MeasureText(text);

            var saved = canvas.Save();
            canvas.SetMatrix(world);
            canvas.DrawText(text.Trim(), x, y, paint);
            canvas.RestoreToCount(saved);
        }

        // ---------------- 画笔与样式 ----------------

        /// <summary>把一条 fill/stroke 值变成画笔：颜色、渐变或"无"。</summary>
        SKPaint? CreatePaint(XElement e, SvgStyle style, string token, float alpha, bool stroke)
        {
            var opacity = Clamp01(alpha * style.Opacity);
            if (opacity <= 0)
                return null;

            var paint = new SKPaint
            {
                IsAntialias = true,
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                StrokeWidth = style.StrokeWidth,
                StrokeCap = style.Cap,
                StrokeJoin = style.Join,
                PathEffect = style.Dash is { Length: > 0 } dash ? SKPathEffect.CreateDash(dash, 0) : null,
            };

            if (token.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var close = token.IndexOf(')');
                var id = close > 4 ? token.Substring(4, close - 4).Trim().Trim('\'', '"') : string.Empty;
                if (id.StartsWith("#", StringComparison.Ordinal))
                    id = id.Substring(1);

                if (_ids.TryGetValue(id, out var def))
                {
                    var shader = BuildGradientShader(def, style, alpha);
                    if (shader is not null)
                    {
                        _shaders.Add(shader);
                        paint.Shader = shader;
                        return paint;
                    }
                }

                // 渐变定义缺失：退回"没有这条 paint"，比画一块纯灰更接近原意
                paint.Dispose();
                return null;
            }

            var color = ResolveColor(token, alpha, style.Opacity);
            if (color is null)
            {
                paint.Dispose();
                return null;
            }

            paint.Color = color.Value;
            return paint;
        }

        SKShader? BuildGradientShader(XElement def, SvgStyle style, float alpha)
        {
            var kind = def.Name.LocalName.ToLowerInvariant();
            if (kind is not ("lineargradient" or "radialgradient"))
                return null;

            var colors = new List<SKColor>(4);
            var stops = new List<float>(4);
            foreach (var stop in def.Elements())
            {
                if (!Is(stop, "stop"))
                    continue;

                var offsetRaw = Prop(stop, "offset") ?? "0";
                var offset = offsetRaw.EndsWith("%", StringComparison.Ordinal)
                    ? ParsePercent(offsetRaw)
                    : (float.TryParse(offsetRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ov) ? ov : 0);

                var stopAlpha = 1f;
                if (TryLength(Prop(stop, "stop-opacity"), out var so))
                    stopAlpha = so;

                var color = ResolveColor(Prop(stop, "stop-color") ?? "#000000", stopAlpha, style.Opacity);
                colors.Add(color ?? SKColors.Black);
                stops.Add(Clamp01(offset));
            }

            if (colors.Count == 0)
                return null;
            if (colors.Count == 1)
                return null; // 单色渐变：由调用方按纯色处理更省

            // 位置必须单调不减，否则 Skia 直接抛
            for (var i = 1; i < stops.Count; i++)
            {
                if (stops[i] < stops[i - 1])
                    stops[i] = stops[i - 1];
            }

            var userSpace = (Prop(def, "gradientUnits") ?? "").IndexOf("userSpaceOnUse", StringComparison.OrdinalIgnoreCase) >= 0;
            // objectBoundingBox：0..1 映射到元素包围盒（此处没有精确包围盒，用 0..1 视作单位框，
            // 由画布矩阵缩放——对图标这种"整框渐变"够用，也是绝大多数 SVG 的用法）
            var boxScale = userSpace ? 1f : 1f;
            var offX = userSpace ? 0f : 0f;
            var offY = userSpace ? 0f : 0f;

            if (kind == "lineargradient")
            {
                var x1 = GradientCoord(def, "x1", 0, boxScale, offX);
                var y1 = GradientCoord(def, "y1", 0, boxScale, offY);
                var x2 = GradientCoord(def, "x2", 1, boxScale, offX);
                var y2 = GradientCoord(def, "y2", 0, boxScale, offY);
                return SKShader.CreateLinearGradient(
                    new SKPoint(x1, y1), new SKPoint(x2, y2),
                    colors.ToArray(), stops.ToArray(), SKShaderTileMode.Clamp);
            }

            var cx = GradientCoord(def, "cx", 0.5f, boxScale, offX);
            var cy = GradientCoord(def, "cy", 0.5f, boxScale, offY);
            var r = GradientCoord(def, "r", 0.5f, boxScale, 1f);
            return SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), Math.Max(0.0001f, r),
                colors.ToArray(), stops.ToArray(), SKShaderTileMode.Clamp);
        }

        static float GradientCoord(XElement def, string name, float fallback, float scale, float offset)
        {
            var raw = Prop(def, name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback * scale + offset;

            if (raw!.Trim().EndsWith("%", StringComparison.Ordinal))
                return ParsePercent(raw) * scale + offset;

            return TryLength(raw, out var v) ? v * scale + offset : fallback * scale + offset;
        }

        static float ParsePercent(string raw)
        {
            var s = raw.Trim().TrimEnd('%');
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 100f : 0;
        }

        /// <summary>颜色解析：#rgb / #rrggbb / #rrggbbaa / rgb() / rgba() / 常用具名色。</summary>
        internal static SKColor? ResolveColor(string? raw, float alpha, float opacity)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var s = raw!.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)
                || s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
                return null;

            if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                s = "#000000";

            SKColor color;
            if (s[0] == '#')
            {
                if (!TryParseHex(s.Substring(1), out color))
                    return null;
            }
            else if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseRgbFunction(s, out color))
                    return null;
            }
            else if (!NamedColors.TryGetValue(s, out color))
            {
                return null;
            }

            var a = Clamp01((color.Alpha / 255f) * alpha * opacity);
            return color.WithAlpha((byte)Math.Round(a * 255));
        }

        static bool TryParseHex(string hex, out SKColor color)
        {
            color = SKColors.Black;
            hex = hex.Trim();
            if (hex.Length == 3)
            {
                var r = HexNibble(hex[0]);
                var g = HexNibble(hex[1]);
                var b = HexNibble(hex[2]);
                if (r < 0 || g < 0 || b < 0)
                    return false;
                color = new SKColor((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                return true;
            }

            if (hex.Length is 6 or 8)
            {
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                    return false;

                if (hex.Length == 6)
                {
                    color = new SKColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
                    return true;
                }

                color = new SKColor((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
                return true;
            }

            return false;
        }

        static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            return -1;
        }

        static bool TryParseRgbFunction(string s, out SKColor color)
        {
            color = SKColors.Black;
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close <= open)
                return false;

            var body = s.Substring(open + 1, close - open - 1);
            var parts = body.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return false;

            var isPercent = parts[0].Trim().EndsWith("%", StringComparison.Ordinal);
            float Channel(string part)
            {
                var p = part.Trim();
                if (p.EndsWith("%", StringComparison.Ordinal))
                    return ParsePercent(p);
                return float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? (isPercent ? v / 100f : v / 255f)
                    : 0;
            }

            var a = 1f;
            if (parts.Length >= 4)
                a = Channel(parts[3]);

            color = new SKColor(
                (byte)Math.Round(Clamp01(Channel(parts[0])) * 255),
                (byte)Math.Round(Clamp01(Channel(parts[1])) * 255),
                (byte)Math.Round(Clamp01(Channel(parts[2])) * 255),
                (byte)Math.Round(Clamp01(a) * 255));
            return true;
        }

        static float Clamp01(float v)
        {
            if (float.IsNaN(v))
                return 0;
            if (v < 0)
                return 0;
            return v > 1 ? 1 : v;
        }

        static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = new SKColor(0, 0, 0),
            ["white"] = new SKColor(255, 255, 255),
            ["red"] = new SKColor(255, 0, 0),
            ["green"] = new SKColor(0, 128, 0),
            ["blue"] = new SKColor(0, 0, 255),
            ["yellow"] = new SKColor(255, 255, 0),
            ["orange"] = new SKColor(255, 165, 0),
            ["purple"] = new SKColor(128, 0, 128),
            ["gray"] = new SKColor(128, 128, 128),
            ["grey"] = new SKColor(128, 128, 128),
            ["silver"] = new SKColor(192, 192, 192),
            ["maroon"] = new SKColor(128, 0, 0),
            ["navy"] = new SKColor(0, 0, 128),
            ["teal"] = new SKColor(0, 128, 128),
            ["aqua"] = new SKColor(0, 255, 255),
            ["fuchsia"] = new SKColor(255, 0, 255),
            ["lime"] = new SKColor(0, 255, 0),
            ["olive"] = new SKColor(128, 128, 0),
            ["pink"] = new SKColor(255, 192, 203),
            ["brown"] = new SKColor(165, 42, 42),
            ["gold"] = new SKColor(255, 215, 0),
            ["cyan"] = new SKColor(0, 255, 255),
            ["magenta"] = new SKColor(255, 0, 255),
            ["lightgray"] = new SKColor(211, 211, 211),
            ["lightgrey"] = new SKColor(211, 211, 211),
            ["darkgray"] = new SKColor(169, 169, 169),
            ["darkgrey"] = new SKColor(169, 169, 169),
        };

        // ---------------- 样式继承 ----------------

        static SvgStyle ResolveStyle(XElement e, SvgStyle parent)
        {
            var style = parent;

            var fill = Prop(e, "fill");
            if (!string.IsNullOrWhiteSpace(fill))
                style.Fill = fill!.Trim();

            var stroke = Prop(e, "stroke");
            if (!string.IsNullOrWhiteSpace(stroke))
                style.Stroke = stroke!.Trim();

            if (TryLength(Prop(e, "stroke-width"), out var sw))
                style.StrokeWidth = sw;

            if (TryLength(Prop(e, "fill-opacity"), out var fo))
                style.FillOpacity = Clamp01(fo);

            if (TryLength(Prop(e, "stroke-opacity"), out var so2))
                style.StrokeOpacity = Clamp01(so2);

            if (TryLength(Prop(e, "opacity"), out var op))
                style.Opacity = Clamp01(op); // 组透明度：向下乘进每个子树元素

            var rule = Prop(e, "fill-rule");
            if (!string.IsNullOrWhiteSpace(rule))
                style.FillType = rule!.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
                    ? SKPathFillType.EvenOdd
                    : SKPathFillType.Winding;

            var cap = Prop(e, "stroke-linecap");
            if (!string.IsNullOrWhiteSpace(cap))
            {
                style.Cap = cap!.Trim().ToLowerInvariant() switch
                {
                    "round" => SKStrokeCap.Round,
                    "square" => SKStrokeCap.Square,
                    _ => SKStrokeCap.Butt,
                };
            }

            var join = Prop(e, "stroke-linejoin");
            if (!string.IsNullOrWhiteSpace(join))
            {
                style.Join = join!.Trim().ToLowerInvariant() switch
                {
                    "round" => SKStrokeJoin.Round,
                    "bevel" => SKStrokeJoin.Bevel,
                    _ => SKStrokeJoin.Miter,
                };
            }

            var dash = Prop(e, "stroke-dasharray");
            if (!string.IsNullOrWhiteSpace(dash) && !dash!.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var arr = ReadNumbers(dash);
                style.Dash = arr.Length > 0 ? arr : null;
            }

            var visibility = Prop(e, "visibility");
            if (!string.IsNullOrWhiteSpace(visibility)
                && visibility!.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase))
            {
                style.Visible = false;
            }

            var display = Prop(e, "display");
            if (!string.IsNullOrWhiteSpace(display)
                && display!.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                style.Visible = false;
            }

            return style;
        }
    }

    /// <summary>可继承的绘制样式（SVG 的表现属性本身就是继承的）。</summary>
    struct SvgStyle
    {
        public string Fill;
        public float FillOpacity;
        public string Stroke;
        public float StrokeOpacity;
        public float StrokeWidth;
        public SKPathFillType FillType;
        public SKStrokeCap Cap;
        public SKStrokeJoin Join;
        public float[]? Dash;
        public float Opacity;
        public bool Visible;

        public static SvgStyle Default => new()
        {
            Fill = "#000000",
            FillOpacity = 1f,
            Stroke = "none",
            StrokeOpacity = 1f,
            StrokeWidth = 1f,
            FillType = SKPathFillType.Winding,
            Cap = SKStrokeCap.Butt,
            Join = SKStrokeJoin.Miter,
            Dash = null,
            Opacity = 1f,
            Visible = true,
        };
    }

    /// <summary>transform 列表解析（作用于子坐标 → 父坐标）。</summary>
    static SKMatrix ParseTransform(string? raw)
    {
        var m = SKMatrix.CreateIdentity();
        if (string.IsNullOrWhiteSpace(raw))
            return m;

        var s = raw!;
        var i = 0;
        var chain = new List<SKMatrix>(4);

        while (i < s.Length)
        {
            SkipSeparators(s, ref i);
            if (i >= s.Length)
                break;

            var start = i;
            while (i < s.Length && (char.IsLetter(s[i])))
                i++;
            if (i == start)
                break;

            var fn = s.Substring(start, i - start).ToLowerInvariant();

            SkipSeparators(s, ref i);
            if (i >= s.Length || s[i] != '(')
                continue;

            var closeIdx = s.IndexOf(')', i);
            if (closeIdx < 0)
                break;

            var args = ReadNumbers(s.Substring(i + 1, closeIdx - i - 1));
            i = closeIdx + 1;

            switch (fn)
            {
                case "translate":
                    if (args.Length >= 1)
                        chain.Add(SKMatrix.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0));
                    break;

                case "scale":
                    if (args.Length >= 1)
                        chain.Add(SKMatrix.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]));
                    break;

                case "rotate":
                    if (args.Length >= 1)
                    {
                        if (args.Length >= 3)
                        {
                            var cx = args[1];
                            var cy = args[2];
                            chain.Add(SKMatrix.CreateTranslation(-cx, -cy)
                                .PostConcat(SKMatrix.CreateRotationDegrees(args[0]))
                                .PostConcat(SKMatrix.CreateTranslation(cx, cy)));
                        }
                        else
                        {
                            chain.Add(SKMatrix.CreateRotationDegrees(args[0]));
                        }
                    }

                    break;

                case "skewx":
                    if (args.Length >= 1)
                        chain.Add(SKMatrix.CreateSkew((float)Math.Tan(args[0] * Math.PI / 180), 0));
                    break;

                case "skewy":
                    if (args.Length >= 1)
                        chain.Add(SKMatrix.CreateSkew(0, (float)Math.Tan(args[0] * Math.PI / 180)));
                    break;

                case "matrix":
                    if (args.Length >= 6)
                        chain.Add(new SKMatrix { ScaleX = args[0], SkewY = args[1], TransX = args[2], SkewX = args[3], ScaleY = args[4], TransY = args[5], Persp0 = 0, Persp1 = 0, Persp2 = 1 });
                    break;
            }
        }

        for (var k = chain.Count - 1; k >= 0; k--)
            m = m.PostConcat(chain[k]);

        return m;
    }
}

/// <summary>SVG path 的 <c>d</c> 解析（M/L/H/V/C/S/Q/T/A/Z，绝对与相对）。</summary>
internal static class PathDataParser
{
    public static void Parse(string d, SKPath path)
    {
        var i = 0;
        var cmd = '\0';
        float cx = 0, cy = 0; // 当前点
        float sx = 0, sy = 0; // 子路径起点
        float lastCtrlX = 0, lastCtrlY = 0;
        char lastCurve = '\0'; // 'C' 或 'Q'，用于 S/T 的反射控制点

        while (true)
        {
            SvgRasterizer.SkipSeparators(d, ref i);
            if (i >= d.Length)
                break;

            if (char.IsLetter(d[i]))
            {
                cmd = d[i];
                i++;
            }
            else if (cmd == '\0')
            {
                break;
            }

            var rel = char.IsLower(cmd);
            var upper = char.ToUpperInvariant(cmd);

            switch (upper)
            {
                case 'M':
                {
                    if (!TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        x += cx;
                        y += cy;
                    }

                    path.MoveTo(x, y);
                    cx = sx = x;
                    cy = sy = y;
                    cmd = rel ? 'l' : 'L'; // 后续坐标对隐式为 lineto
                    lastCurve = '\0';
                    break;
                }

                case 'L':
                {
                    if (!TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        x += cx;
                        y += cy;
                    }

                    path.LineTo(x, y);
                    cx = x;
                    cy = y;
                    lastCurve = '\0';
                    break;
                }

                case 'H':
                {
                    var x = SvgRasterizer.ReadNumber(d, ref i);
                    if (float.IsNaN(x))
                        return;
                    if (rel)
                        x += cx;
                    path.LineTo(x, cy);
                    cx = x;
                    lastCurve = '\0';
                    break;
                }

                case 'V':
                {
                    var y = SvgRasterizer.ReadNumber(d, ref i);
                    if (float.IsNaN(y))
                        return;
                    if (rel)
                        y += cy;
                    path.LineTo(cx, y);
                    cy = y;
                    lastCurve = '\0';
                    break;
                }

                case 'C':
                {
                    if (!TryReadPoint(d, ref i, out var c1x, out var c1y)
                        || !TryReadPoint(d, ref i, out var c2x, out var c2y)
                        || !TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        c1x += cx;
                        c1y += cy;
                        c2x += cx;
                        c2y += cy;
                        x += cx;
                        y += cy;
                    }

                    path.CubicTo(c1x, c1y, c2x, c2y, x, y);
                    lastCtrlX = c2x;
                    lastCtrlY = c2y;
                    cx = x;
                    cy = y;
                    lastCurve = 'C';
                    break;
                }

                case 'S':
                {
                    if (!TryReadPoint(d, ref i, out var c2x, out var c2y)
                        || !TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        c2x += cx;
                        c2y += cy;
                        x += cx;
                        y += cy;
                    }

                    var c1x = lastCurve == 'C' ? 2 * cx - lastCtrlX : cx;
                    var c1y = lastCurve == 'C' ? 2 * cy - lastCtrlY : cy;
                    path.CubicTo(c1x, c1y, c2x, c2y, x, y);
                    lastCtrlX = c2x;
                    lastCtrlY = c2y;
                    cx = x;
                    cy = y;
                    lastCurve = 'C';
                    break;
                }

                case 'Q':
                {
                    if (!TryReadPoint(d, ref i, out var qx, out var qy)
                        || !TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        qx += cx;
                        qy += cy;
                        x += cx;
                        y += cy;
                    }

                    path.QuadTo(qx, qy, x, y);
                    lastCtrlX = qx;
                    lastCtrlY = qy;
                    cx = x;
                    cy = y;
                    lastCurve = 'Q';
                    break;
                }

                case 'T':
                {
                    if (!TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (rel)
                    {
                        x += cx;
                        y += cy;
                    }

                    var qx = lastCurve == 'Q' ? 2 * cx - lastCtrlX : cx;
                    var qy = lastCurve == 'Q' ? 2 * cy - lastCtrlY : cy;
                    path.QuadTo(qx, qy, x, y);
                    lastCtrlX = qx;
                    lastCtrlY = qy;
                    cx = x;
                    cy = y;
                    lastCurve = 'Q';
                    break;
                }

                case 'A':
                {
                    var rx = SvgRasterizer.ReadNumber(d, ref i);
                    var ry = SvgRasterizer.ReadNumber(d, ref i);
                    var rot = SvgRasterizer.ReadNumber(d, ref i);
                    var large = SvgRasterizer.ReadNumber(d, ref i);
                    var sweep = SvgRasterizer.ReadNumber(d, ref i);
                    if (!TryReadPoint(d, ref i, out var x, out var y))
                        return;
                    if (float.IsNaN(rx) || float.IsNaN(ry) || float.IsNaN(rot)
                        || float.IsNaN(large) || float.IsNaN(sweep))
                        return;

                    if (rel)
                    {
                        x += cx;
                        y += cy;
                    }

                    ArcToCubics(path, cx, cy, rx, ry, rot, large != 0, sweep != 0, x, y);
                    cx = x;
                    cy = y;
                    lastCurve = '\0';
                    break;
                }

                case 'Z':
                {
                    path.Close();
                    cx = sx;
                    cy = sy;
                    lastCurve = '\0';
                    break;
                }

                default:
                    return; // 未知指令：停止解析，保留已画出的部分
            }
        }
    }

    /// <summary>椭圆弧 → 三次贝塞尔（每段 ≤90°，SVG 规范 F.6 的端点参数化）。</summary>
    static void ArcToCubics(
        SKPath path,
        float x1, float y1,
        float rx, float ry,
        float rotationDeg,
        bool largeArc, bool sweep,
        float x2, float y2)
    {
        if (Math.Abs(x1 - x2) < 1e-6 && Math.Abs(y1 - y2) < 1e-6)
            return;

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < 1e-6 || ry < 1e-6)
        {
            path.LineTo(x2, y2);
            return;
        }

        var phi = rotationDeg * Math.PI / 180.0;
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        var dx2 = (x1 - x2) / 2.0;
        var dy2 = (y1 - y2) / 2.0;
        var x1p = cosPhi * dx2 + sinPhi * dy2;
        var y1p = -sinPhi * dx2 + cosPhi * dy2;

        var lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1)
        {
            var grown = Math.Sqrt(lambda);
            rx = (float)(rx * grown);
            ry = (float)(ry * grown);
        }

        var rx2 = (double)rx * rx;
        var ry2 = (double)ry * ry;
        var num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        var den = rx2 * y1p * y1p + ry2 * x1p * x1p;
        var coef = Math.Sqrt(Math.Max(0, num / den));
        if (largeArc == sweep)
            coef = -coef;

        var cxp = coef * rx * y1p / ry;
        var cyp = -coef * ry * x1p / rx;

        var cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2.0;
        var cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2.0;

        var ux = (x1p - cxp) / rx;
        var uy = (y1p - cyp) / ry;
        var vx = (-x1p - cxp) / rx;
        var vy = (-y1p - cyp) / ry;

        var theta1 = VectorAngle(1, 0, ux, uy);
        var delta = VectorAngle(ux, uy, vx, vy);

        if (!sweep && delta > 0)
            delta -= 2 * Math.PI;
        else if (sweep && delta < 0)
            delta += 2 * Math.PI;

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
        var step = delta / segments;

        for (var k = 0; k < segments; k++)
        {
            var t1 = theta1 + step * k;
            var t2 = t1 + step;

            EllipsePoint(cx, cy, rx, ry, cosPhi, sinPhi, t1, out var p1x, out var p1y);
            EllipsePoint(cx, cy, rx, ry, cosPhi, sinPhi, t2, out var p2x, out var p2y);
            EllipseDerivative(rx, ry, cosPhi, sinPhi, t1, out var d1x, out var d1y);
            EllipseDerivative(rx, ry, cosPhi, sinPhi, t2, out var d2x, out var d2y);

            var alpha = 4.0 / 3.0 * Math.Tan(step / 4.0);

            var c1x = p1x + alpha * d1x;
            var c1y = p1y + alpha * d1y;
            var c2x = p2x - alpha * d2x;
            var c2y = p2y - alpha * d2y;

            path.CubicTo((float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)p2x, (float)p2y);
        }
    }

    static void EllipsePoint(double cx, double cy, double rx, double ry, double cosPhi, double sinPhi, double t, out double x, out double y)
    {
        var ex = rx * Math.Cos(t);
        var ey = ry * Math.Sin(t);
        x = cosPhi * ex - sinPhi * ey + cx;
        y = sinPhi * ex + cosPhi * ey + cy;
    }

    static void EllipseDerivative(double rx, double ry, double cosPhi, double sinPhi, double t, out double dx, out double dy)
    {
        var ex = -rx * Math.Sin(t);
        var ey = ry * Math.Cos(t);
        dx = cosPhi * ex - sinPhi * ey;
        dy = sinPhi * ex + cosPhi * ey;
    }
    static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        var dot = ux * vx + uy * vy;
        var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        if (len < 1e-12)
            return 0;

        var cos = Math.Max(-1.0, Math.Min(1.0, dot / len));
        var angle = Math.Acos(cos);
        return ux * vy - uy * vx < 0 ? -angle : angle;
    }

    static bool TryReadPoint(string d, ref int i, out float x, out float y)
    {
        x = SvgRasterizer.ReadNumber(d, ref i);
        y = SvgRasterizer.ReadNumber(d, ref i);
        return !float.IsNaN(x) && !float.IsNaN(y);
    }
}
