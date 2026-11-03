using System.Collections.Concurrent;
using SkiaSharp;
using HBBuffer = HarfBuzzSharp.Buffer;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// HarfBuzz OpenType shaping 封装。给阿拉伯文/天城文等复杂脚本做连字、合字、方向反转。
/// 西文 + CJK 走 Skia 快路径，不经过这里。
/// </summary>
/// <remarks>
/// 生命周期：
///   Blob / Face 被缓存（fontBytes 不变就复用），Font 每次 shape 新建（因为 SetScale 要按字号改）。
///   Buffer 每次 shape 新建用完就丢，Dispose 走正常路径。
/// 
/// Thread-safe：ConcurrentDictionary 锁 blob/face 级别的缓存，Buffer/Font 是调用栈上的局部变量。
/// </remarks>
internal static class HarfBuzzShaper
{
    /// <summary>
    /// shape 后单个 glyph 的位置 + 度量，单位 mm。
    /// <c>OffsetXMm / OffsetYMm</c> 沿用 HarfBuzz 的 text space 约定：Y 以**基线以上为正**，
    /// 落到 Skia 的 y-down 画布要按 <c>baselineY - OffsetYMm</c> 取位。
    /// </summary>
    public readonly record struct ShapedGlyph(
        ushort GlyphId,
        double AdvanceMm,
        double OffsetXMm,
        double OffsetYMm,
        int Utf16Cluster);

    /// <summary>
    /// shape 后的整行 glyph 序列。
    /// 顺序一律是**视觉从左到右**、<c>AdvanceMm</c> 一律为正：
    /// RTL 时 HarfBuzz 在 shape 末尾自己做了缓冲反转（实测 "السلام" 的 cluster 依次 5,4,3,2,1,0），
    /// 绘制方按数组顺序推 cursor 即可，再反转一次等于把整行镜像。
    /// </summary>
    public sealed record ShapedLine(double TotalAdvanceMm, IReadOnlyList<ShapedGlyph> Glyphs);

    // 缓存：fontBytes hash → (Blob, Face)。Blob/Face 生命周期等于进程，Dispose 交给 finalizer。
    // 进程级缓存是合理的——CrossPrint 单次渲染里涉及的字体种类通常 < 10。
    static readonly ConcurrentDictionary<int, Lazy<(HarfBuzzSharp.Blob, HarfBuzzSharp.Face)>> _faceCache = new();

    /// <summary>
    /// 对复杂脚本文本做 shape，返回 glyph 序列。
    /// </summary>
    /// <param name="text">要 shape 的 UTF-16 文本。</param>
    /// <param name="typeface">字体。</param>
    /// <param name="sizeMm">字号（mm）。</param>
    /// <param name="rtl">是否 RTL 方向。</param>
    /// <returns>shape 结果，失败降级为 null。</returns>
    public static ShapedLine? Shape(string text, SKTypeface typeface, double sizeMm, bool rtl)
    {
        if (string.IsNullOrEmpty(text) || typeface is null || sizeMm <= 0)
            return null;

        try
        {
            // 1. 拿 font bytes → 构造 cached Blob/Face
            var face = GetOrCreateFace(typeface);
            if (face is null) return null;

            // 2. Font（按字号 scale）+ Buffer
            using var font = new HarfBuzzSharp.Font(face);
            font.SetScale(face.UnitsPerEm, 1); // 把所有度量基准调到 font units，下面乘 SizeMm/upem 转 mm

            using var buf = new HBBuffer();
            buf.AddUtf16(text);
            // 必须先猜段落属性：Font.Shape 走的是 hb_shape_full，它不会替 hb_shape 那样补 guess。
            // 不 guess 时 buffer 的 script 是 INVALID，ot 布局器一条 GSUB 特性都不套——
            // 实测 "السلام" 只拿到 cmap 字形、连字与词首/词中形式全丢，shaping 等于白做。
            buf.GuessSegmentProperties();
            // guess 按首个强字符定方向，这里再用分类器的结论覆盖：混合串里方向必须由宿主说了算。
            buf.Direction = rtl ? HarfBuzzSharp.Direction.RightToLeft : HarfBuzzSharp.Direction.LeftToRight;

            // 3. Shape
            font.Shape(buf);

            // 4. 读结果
            var infos = buf.GlyphInfos;
            var positions = buf.GlyphPositions;
            if (infos.Length == 0 || infos.Length != positions.Length)
                return null;

            double upem = face.UnitsPerEm;
            double scaleToMm = sizeMm / upem; // font unit × scaleToMm = mm

            var glyphs = new List<ShapedGlyph>(infos.Length);
            double totalAdvance = 0;

            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                var pos = positions[i];

                var advanceMm = pos.XAdvance * scaleToMm;
                var offsetXmm = pos.XOffset * scaleToMm;
                var offsetYmm = pos.YOffset * scaleToMm;

                glyphs.Add(new ShapedGlyph(
                    (ushort)info.Codepoint, // shape 后 Codepoint 就是 glyph ID
                    (double)advanceMm,
                    (double)offsetXmm,
                    (double)offsetYmm,
                    (int)info.Cluster));

                totalAdvance += advanceMm;
            }

            return new ShapedLine(totalAdvance, glyphs);
        }
        catch
        {
            // HarfBuzzSharp 在特殊字体（如某些 WOFF2 子集）上可能抛异常。
            // 吞掉降级，TextStyle 回退到 Skia 路径总比崩掉好。
            return null;
        }
    }

    /// <summary>
    /// 从 SKTypeface 拿到 HarfBuzz Face。优先走缓存，缓存失败就现建。
    /// </summary>
    static HarfBuzzSharp.Face? GetOrCreateFace(SKTypeface typeface)
    {
        var cacheKey = HashFontIdentity(typeface);
        if (_faceCache.TryGetValue(cacheKey, out var lazy))
            return lazy.Value.Item2;

        // 我们只尝试一次创建；失败就别再重试了（免得每次 shape 都卡在同一个坏字体上）
        var newLazy = new Lazy<(HarfBuzzSharp.Blob, HarfBuzzSharp.Face)>(() => CreateFace(typeface));
        if (_faceCache.TryAdd(cacheKey, newLazy))
        {
            try { return newLazy.Value.Item2; }
            catch
            {
                _faceCache.TryRemove(cacheKey, out _);
                return null;
            }
        }

        // 别的线程先建好了
        return _faceCache.TryGetValue(cacheKey, out var existing) ? existing.Value.Item2 : null;
    }

    static (HarfBuzzSharp.Blob, HarfBuzzSharp.Face) CreateFace(SKTypeface typeface)
    {
        var bytes = ReadFontBytes(typeface);
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException("Cannot read font bytes from SKTypeface");

        // 绝不能用 Blob.FromStream：它把 blob 指到一个 fixed 块里的托管数组，出了 fixed 就没人钉着，
        // GC 一压缩原生侧读到的就是旧地址。实测两轮 Full GC 后同一 Face 的 GSUB 结果静默退回 cmap 字形，
        // 不抛异常、只是画错。MemoryMode.Duplicate 让 HarfBuzz 自己 malloc 拷一份，指针随即失效也无所谓。
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var blob = new HarfBuzzSharp.Blob(handle.AddrOfPinnedObject(), bytes.Length, HarfBuzzSharp.MemoryMode.Duplicate);
            return (blob, new HarfBuzzSharp.Face(blob, 0));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 尝试从 SKTypeface 读取原始 font file bytes。
    /// SkiaSharp 的 <see cref="M:SkiaSharp.SKTypeface.OpenStream">OpenStream</see> 不保证返回完整原始文件，
    /// 某些系统字体（.ttc、隐藏字体）可能返回空。此时 Fallback 找磁盘上的同名文件。
    /// </summary>
    static byte[]? ReadFontBytes(SKTypeface typeface)
    {
        using var streamAsset = typeface.OpenStream();
        if (streamAsset is not null)
        {
            using var data = SKData.Create(streamAsset);
            if (data is not null && data.Size > 0)
                return data.ToArray();
        }

        // Fallback：SKTypeface.OpenStream 在某些 SkiaSharp 版本上可能返回空，
        // 此时尝试从 SKFontManager 按 family name 找磁盘文件
        try
        {
            using var manager = SKFontManager.CreateDefault();
            using var match = manager.MatchFamily(typeface.FamilyName, SKFontStyle.Normal);
            if (match is not null)
            {
                using var data = SKData.Create(match.OpenStream());
                if (data is not null && data.Size > 0)
                    return data.ToArray();
            }
        }
        catch { /* ignore fallback errors */ }

        return null;
    }

    static int HashFontIdentity(SKTypeface typeface)
    {
        // 手动实现 Combine 以兼容 net461（无 System.HashCode）。
        // 光靠族名+字重/字宽/倾斜会把"同名不同文件"的字体并到同一个 Face 上，
        // 所以再混进字形数与设计单位——这两项来自字体本体，碰撞面小得多。
        var hash = typeface.FamilyName?.GetHashCode() ?? 0;
        hash = (hash * 397) ^ typeface.FontWeight;
        hash = (hash * 397) ^ (int)typeface.FontWidth;
        hash = (hash * 397) ^ (int)typeface.FontSlant;
        hash = (hash * 397) ^ typeface.GlyphCount;
        hash = (hash * 397) ^ typeface.UnitsPerEm;
        return hash;
    }
}
