using System.Globalization;
using System.Text;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>图片来源定位符的类别（公开以便测试与宿主诊断代码复用分类口径）。</summary>
public enum ImageLocatorKind
{
    /// <summary>空（没有可用的源）。</summary>
    Empty,

    /// <summary><c>data:</c> URI（base64 或百分号编码）。</summary>
    DataUri,

    /// <summary><c>http/https</c> 地址。</summary>
    Remote,

    /// <summary>裸 base64 载荷。</summary>
    Base64,

    /// <summary>交给宿主解析器的资源键（文件路径、业务键等）。</summary>
    Other,
}

/// <summary>
/// 图片字节的获取与解码：data URI、裸 base64、网络地址统一到一条管线。
/// </summary>
/// <remarks>
/// <para>
/// 历史行为是"只有 base64 能用，外链一律占位框"，实际模板里图片源却以三种形态出现：
/// hiprint 面板上传内嵌成 <c>data:image/png;base64,…</c>、直接写裸 base64、
/// 或者干脆存一个 <c>https://…</c> 外链。三种都不该在渲染期变成空框。
/// </para>
/// <para>
/// 网络下载按 URL 缓存在进程内（同一地址一页多张图/多页只下载一次），
/// 命中缓存后不再发起请求；下载失败只返回 null 并带原因，由渲染器转成告警，绝不抛给渲染主循环。
/// </para>
/// </remarks>
internal static class ImageAcquisition
{
    /// <summary>单张图片字节上限：超过按失败处理（防畸形模板把内存吃光）。</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>URL → 原始字节的缓存条数上限。</summary>
    const int ByteCacheCapacity = 32;

    static readonly object CacheGate = new();
    static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = new(StringComparer.Ordinal);
    static readonly LinkedList<CacheEntry> CacheOrder = new();

    sealed class CacheEntry(string url, byte[] bytes)
    {
        public string Url { get; } = url;

        public byte[] Bytes { get; } = bytes;
    }

    // ---------------- 分类 ----------------

    /// <summary>定位符分类（不发起任何 IO）。</summary>
    public static ImageLocatorKind Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ImageLocatorKind.Empty;

        var s = raw!.Trim();

        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ImageLocatorKind.DataUri;

        if (IsRemote(s))
            return ImageLocatorKind.Remote;

        if (LooksLikeBase64(s))
            return ImageLocatorKind.Base64;

        return ImageLocatorKind.Other;
    }

    /// <summary>是不是网络地址。</summary>
    public static bool IsRemote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw!.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 不取字节就判断"这条定位符是不是 SVG"：用于决定图片缓存键要不要带上目标像素尺寸。
    /// 判据是廉价的形态特征（data URI 的 mime、URL 后缀、base64 头部魔数），误判只影响缓存粒度，不影响正确性。
    /// </summary>
    public static bool LooksLikeSvgLocator(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator))
            return false;

        var s = locator!.Trim();
        switch (Classify(s))
        {
            case ImageLocatorKind.DataUri:
            {
                var comma = s.IndexOf(',');
                var header = comma < 0 ? s : s.Substring(0, comma);
                return header.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            case ImageLocatorKind.Remote:
            {
                var q = s.IndexOfAny(new[] { '?', '#' });
                var path = q < 0 ? s : s.Substring(0, q);
                return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            }

            case ImageLocatorKind.Base64:
            {
                // "<svg" → PHN2Z…，<?xml → PD94bW…，<!DOCTYPE → PCFET0…
                var head = s.Length > 12 ? s.Substring(0, 12) : s;
                return head.StartsWith("PHN2Z", StringComparison.Ordinal)
                       || head.StartsWith("PD94bW", StringComparison.Ordinal)
                       || head.StartsWith("PCFET0", StringComparison.Ordinal);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 粗判"看起来是 base64"：够长、只用 base64 字符表、不含空白与路径分隔符。
    /// 只用来分流，最终是否成立由 <see cref="TryFromBase64"/> 决定。
    /// </summary>
    internal static bool LooksLikeBase64(string raw)
    {
        if (raw.Length < 32)
            return false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')
                continue;

            // 文件路径/业务键里常见的一律判否
            return false;
        }

        return true;
    }

    // ---------------- 取字节 ----------------

    /// <summary>
    /// 按定位符取原始字节。网络地址受 <paramref name="allowRemote"/> 约束，其余形态总是允许。
    /// 失败返回 null 并写 <paramref name="error"/>（给渲染告警用的中文原因）。
    /// </summary>
    public static byte[]? Acquire(string? locator, bool allowRemote, int timeoutMs, out string? error)
    {
        error = null;

        switch (Classify(locator))
        {
            case ImageLocatorKind.Empty:
                error = "图片来源为空。";
                return null;

            case ImageLocatorKind.DataUri:
                return TryParseDataUri(locator!.Trim(), out error);

            case ImageLocatorKind.Base64:
                if (TryFromBase64(locator!.Trim(), out var bytes))
                    return bytes;
                error = "图片数据不是合法 Base64。";
                return null;

            case ImageLocatorKind.Remote:
                if (!allowRemote)
                {
                    error = "网络图片已被禁用（RenderOptions.AllowRemoteImages = false），请改用宿主 IImageResolver 或内嵌 base64。";
                    return null;
                }

                return FetchRemote(locator!.Trim(), timeoutMs, out error);

            default:
                error = null; // 交由调用方决定（资源键分支会去问宿主）
                return null;
        }
    }

    /// <summary>解码 data URI：<c>data:image/png;base64,xxx</c> 与 <c>data:image/svg+xml,&lt;svg…&gt;</c> 都支持。</summary>
    public static byte[]? TryParseDataUri(string uri, out string? error)
    {
        error = null;

        var comma = uri.IndexOf(',');
        if (comma < 0)
        {
            error = "data URI 缺少逗号分隔的载荷。";
            return null;
        }

        var header = uri.Substring(5, comma - 5); // 去掉 "data:"
        var payload = uri.Substring(comma + 1);

        var isBase64 = header.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isBase64)
        {
            // data URI 的 base64 载荷可能带换行（部分导出工具会折行）
            var clean = StripWhitespace(payload);
            if (TryFromBase64(clean, out var bytes))
                return bytes;

            error = "data URI 的 base64 载荷非法。";
            return null;
        }

        // 非 base64 的 data URI 是百分号编码文本（SVG 常见）
        try
        {
            var text = Uri.UnescapeDataString(payload);
            return Encoding.UTF8.GetBytes(text);
        }
        catch (Exception ex)
        {
            error = "data URI 载荷解码失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>严格 base64 解码（失败不抛）。</summary>
    public static bool TryFromBase64(string raw, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            bytes = Convert.FromBase64String(raw);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    // ---------------- 网络 ----------------

    /// <summary>下载网络图片（带进程内字节缓存）。</summary>
    public static byte[]? FetchRemote(string url, int timeoutMs, out string? error)
    {
        error = null;

        lock (CacheGate)
        {
            if (CacheIndex.TryGetValue(url, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        byte[]? bytes;
        try
        {
            bytes = Download(url, timeoutMs <= 0 ? 10000 : timeoutMs, out error);
        }
        catch (Exception ex)
        {
            error = "下载网络图片失败：" + ex.Message;
            return null;
        }

        if (bytes is null or { Length: 0 })
        {
            error ??= "下载网络图片得到空响应。";
            return null;
        }

        lock (CacheGate)
        {
            if (CacheIndex.TryGetValue(url, out var existing))
            {
                CacheOrder.Remove(existing);
                CacheOrder.AddFirst(existing);
                return existing.Value.Bytes;
            }

            var entry = new CacheEntry(url, bytes);
            CacheIndex[url] = CacheOrder.AddFirst(entry);
            while (CacheIndex.Count > ByteCacheCapacity)
            {
                var tail = CacheOrder.Last!;
                CacheOrder.RemoveLast();
                CacheIndex.Remove(tail.Value.Url);
            }
        }

        return bytes;
    }

    /// <summary>清空网络图片字节缓存（宿主换模板/释放内存时用）。</summary>
    public static void ClearRemoteCache()
    {
        lock (CacheGate)
        {
            CacheIndex.Clear();
            CacheOrder.Clear();
        }
    }

#if NET8_0_OR_GREATER
    static byte[]? Download(string url, int timeoutMs, out string? error)
    {
        error = null;
        using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true };
        using var client = new System.Net.Http.HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CrossPrint/1.0");

        using var response = client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            error = $"下载网络图片失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。";
            return null;
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared is > MaxBytes)
        {
            error = $"网络图片过大（{declared} 字节，上限 {MaxBytes}）。";
            return null;
        }

        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        CopyCapped(stream, buffer, MaxBytes, out error);
        return buffer.ToArray();
    }
#else
    static byte[]? Download(string url, int timeoutMs, out string? error)
    {
        error = null;
        var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.AllowAutoRedirect = true;
        request.UserAgent = "CrossPrint/1.0";

        using var response = (System.Net.HttpWebResponse)request.GetResponse();
        using var stream = response.GetResponseStream();
        using var buffer = new MemoryStream();
        CopyCapped(stream, buffer, MaxBytes, out error);
        return buffer.ToArray();
    }
#endif

    static void CopyCapped(Stream stream, MemoryStream buffer, int maxBytes, out string? error)
    {
        error = null;
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0)
                return;

            if (buffer.Length + read > maxBytes)
            {
                error = $"网络图片超过上限（{maxBytes} 字节），已截断为失败。";
                return;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    // ---------------- 解码 ----------------

    /// <summary>
    /// 字节 → <see cref="SKImage"/>。位图交给 Skia 解码；SVG 走内置光栅器（按目标像素尺寸出图，
    /// 保证在高 DPI 导出时不糊）。失败返回 null 并写原因。
    /// </summary>
    public static SKImage? Decode(byte[] bytes, int targetWidthPx, int targetHeightPx, out string? error)
    {
        error = null;

        if (bytes.Length == 0)
        {
            error = "图片数据为空。";
            return null;
        }

        if (SvgRasterizer.LooksLikeSvg(bytes))
        {
            string svgText;
            try
            {
                svgText = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                error = "SVG 文本解码失败：" + ex.Message;
                return null;
            }

            return SvgRasterizer.Rasterize(svgText, targetWidthPx, targetHeightPx, out error);
        }

        var image = SKImage.FromEncodedData(bytes);
        if (image is null)
            error = "图片数据不是可解码的图片格式。";

        return image;
    }

    /// <summary>是否 SVG（供渲染器决定缓存键要不要带目标像素尺寸）。</summary>
    public static bool IsSvg(byte[] bytes) => SvgRasterizer.LooksLikeSvg(bytes);

    /// <summary>把毫米尺寸折算成光栅化像素尺寸（带上限，避免超大元素生成巨图）。</summary>
    public static int TargetPixels(double sizeMm, double unitsPerMm, int maxPixels = 4096)
    {
        if (unitsPerMm <= 0 || double.IsNaN(unitsPerMm) || double.IsInfinity(unitsPerMm))
            return 256;

        var px = sizeMm * unitsPerMm;
        if (double.IsNaN(px) || px <= 0)
            return 256;

        return (int)Math.Min(maxPixels, Math.Max(8, Math.Round(px)));
    }
}
