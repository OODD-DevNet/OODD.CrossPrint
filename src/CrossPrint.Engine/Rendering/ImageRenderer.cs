using System.Runtime.CompilerServices;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 位图缓存：同一段 base64（或同一个资源键）在多页批量导出里只解码一次，资源键还只向宿主解析器取一次。
/// 上限按张数而非字节数——批量模板通常复用同一张 logo，容量 <see cref="Capacity"/> 足够。
/// </summary>
/// <remarks>
/// <para>
/// 淘汰（从索引与 LRU 摘掉）不等于释放：<see cref="SKImage"/> 这个托管引用是渲染器正往画布上递的东西，
/// 并发批量导出里另一个线程可能刚把它拿到手还没画完。实测（SkiaSharp 2.88.9）：
/// 把下面这条租约纪律拆掉，再去读一个已被淘汰条目的 <see cref="SKImage.Width"/>，
/// 拿到的不是能 catch 的 <see cref="ObjectDisposedException"/>，而是 <c>AccessViolationException</c>
/// （原生层 use-after-free，宿主进程直接崩），打印服务线上就是当场没了。
/// 这条纪律由 <c>ImageCacheLeaseTests</c> 钉住。
/// </para>
/// <para>
/// 改法：条目带租约计数。取图要还租约（<see cref="Lease"/>），有租约在手的条目只退役不释放，
/// 最后一个租约归还时才真正 <c>Dispose</c>；LRU 顺序与索引上限照旧。
/// 全部条目都在租约里时宁可让"欠释放"的条目暂时超容，也不放掉还在用的图
/// （峰值受限于单页不同图片张数，归还即回落，见 <see cref="PendingReleaseCount"/>）。
/// </para>
/// <para>
/// 录制型介质（PDF/图片回放）另有一条实测口径：Skia 的录制对象自己持有原生引用，
/// 托管包装被放掉之后回放仍画得出墨。租约袋（<see cref="ImageLeaseBag"/>）不靠这条活着——
/// 生命周期该由持有介质的一方明确管，而不是押在 Skia 的内部实现上。
/// </para>
/// </remarks>
internal sealed class ImageCache
{
    /// <summary>进程级共享实例。</summary>
    public static ImageCache Shared { get; } = new();

    /// <summary>缓存张数上限（超出按最近最少使用退役）。</summary>
    public const int Capacity = 32;

    readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    readonly LinkedList<Entry> _lru = new();
    readonly object _gate = new();
    int _pendingRelease;

    internal sealed class Entry(string key, SKImage image)
    {
        public string Key { get; } = key;

        public SKImage Image { get; } = image;

        /// <summary>在用的租约数；大于 0 时不许释放 <see cref="Image"/>。</summary>
        public int Leases;

        /// <summary>已因超容量（或 <see cref="Clear"/>）退役：索引里没有了，等最后一个租约归还再释放。</summary>
        public bool Retired;
    }

    /// <summary>
    /// 一次取用的租约：归还以前缓存保证这张 <see cref="SKImage"/> 不被释放。
    /// 谁还能用到这张图，谁就是租约的持有者（见 <see cref="ImageLeaseBag"/>）。
    /// </summary>
    internal sealed class Lease : IDisposable
    {
        readonly ImageCache _cache;
        readonly Entry _entry;
        int _returned;

        internal Lease(ImageCache cache, Entry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        /// <summary>借出的图片，可直接交给 <c>canvas.DrawImage</c>；租约归还后不要再留着用。</summary>
        public SKImage Image => _entry.Image;

        /// <summary>归还租约。重复归还是空操作（导出器的 finally 与渲染器都可能归还）。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 1)
                return;

            _cache.Return(_entry);
        }
    }

    /// <summary>取缓存并借出租约；未命中时用 <paramref name="decode"/> 解码一次并存入。解码失败返回 null 且不缓存失败。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="decode">返回解码后的图片；返回 null 表示数据非法。</param>
    /// <returns>租约（用完必须归还），或 null 表示没有可用图片。</returns>
    public Lease? GetOrAdd(string key, Func<SKImage?> decode)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // 命中即前移，保证淘汰的是真正久未使用的
                _lru.Remove(node);
                _lru.AddFirst(node);
                node.Value.Leases++;
                return new Lease(this, node.Value);
            }
        }

        // 解码在锁外：宿主解析器可能是读盘或查库，抱着锁等它会把并发作业全串起来
        var image = decode();
        if (image is null)
            return null;

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                // 解码期间别的线程已填了同一条：用它那份，自己解的这张就地释放
                image.Dispose();
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                existing.Value.Leases++;
                return new Lease(this, existing.Value);
            }

            var entry = new Entry(key, image) { Leases = 1 };
            _index[key] = _lru.AddFirst(entry);

            while (_index.Count > Capacity)
            {
                var tail = _lru.Last!;
                _lru.RemoveLast();
                _index.Remove(tail.Value.Key);
                FreeIfUnleased(tail.Value);
            }

            return new Lease(this, entry);
        }
    }

    /// <summary>归还租约：还有人在用就什么都不做，最后一个还回来且条目已退役时才释放。</summary>
    void Return(Entry entry)
    {
        lock (_gate)
        {
            entry.Leases--;
            if (entry.Retired)
                FreeIfUnleased(entry);
        }
    }

    void FreeIfUnleased(Entry entry)
    {
        // 有租约在手就先挂着：放掉别人还在递（或还躺在未关闭的录制里）的引用就是原生层 use-after-free
        if (entry.Leases > 0)
        {
            if (!entry.Retired)
            {
                entry.Retired = true;
                _pendingRelease++;
            }

            return;
        }

        if (entry.Retired)
        {
            entry.Retired = false;
            _pendingRelease--;
        }

        entry.Image.Dispose();
    }

    /// <summary>清空并释放全部缓存（宿主释放非托管内存、或单测隔离时用）；仍在租约里的条目等归还时才放。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _lru)
                FreeIfUnleased(entry);
            _lru.Clear();
            _index.Clear();
        }
    }

    /// <summary>当前缓存条目数（不含已退役、只等归还的条目）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _index.Count;
        }
    }

    /// <summary>
    /// 已退役、还欠着释放的条目数，即被在用的租约钉住的图片张数。
    /// 常驻服务的诊断口径（"这批作业还有多少张图没画完"），也是淘汰纪律的取证点：随租约归还应回落到 0。
    /// </summary>
    public int PendingReleaseCount
    {
        get
        {
            lock (_gate)
                return _pendingRelease;
        }
    }
}

/// <summary>
/// 租约袋：把"这次渲染取过的图"收拢到一个袋里，由持有介质的一方在介质用完之后统一归还。
/// 为什么需要它：PDF 走 <c>SKDocument</c>，<c>DrawImage</c> 只是把绘制记进页面，页面结束、文档关闭时才落到文件里。
/// 实测（SkiaSharp 2.88.9）：录制对象自己持有原生引用，托管包装先被放掉也照样回放得出来——
/// 但那是 Skia 的内部实现，不是本引擎能押上的契约，所以生命周期由"开着文档的那一方"明确接管：
/// 袋由 <see cref="CrossPrint.Engine.PdfExporter"/>/<see cref="CrossPrint.Engine.BatchPdfExporter"/> 开，
/// 页面（或文档）关闭后 <see cref="Complete"/>，峰值钉在"一页取用的图"，不随页数涨。
/// 即时绘制且没有袋的路径（位图、设计器画布、宿主自己调渲染入口）按"画完即还"处理，内存口径与改动前一致。
/// </summary>
internal sealed class ImageLeaseBag : IDisposable
{
    static readonly ConditionalWeakTable<RenderContext, ImageLeaseBag> _byContext = new();

    readonly List<ImageCache.Lease> _leases = new();
    bool _completed;

    /// <summary>为 <paramref name="context"/> 开一个袋；调用方必须在介质关闭后 <see cref="Complete"/>。</summary>
    public static ImageLeaseBag Open(RenderContext context)
    {
        var bag = new ImageLeaseBag();
        // 弱表按上下文挂袋：渲染器只拿得到上下文，拿不到"谁开着文档"
        _byContext.Add(context, bag);
        return bag;
    }

    /// <summary>
    /// 渲染器把租约交给袋。<paramref name="context"/> 没挂袋时返回 false，
    /// 表示这条路径是即时绘制，租约由渲染器自己画完就还。
    /// </summary>
    public static bool TryHold(RenderContext context, ImageCache.Lease lease)
    {
        if (!_byContext.TryGetValue(context, out var bag))
            return false;

        lock (bag._leases)
        {
            if (bag._completed)
                return false;

            bag._leases.Add(lease);
        }

        return true;
    }

    /// <summary>归还袋里全部租约（幂等）。</summary>
    public void Complete()
    {
        ImageCache.Lease[] pending;
        lock (_leases)
        {
            if (_completed)
                return;

            _completed = true;
            pending = [.. _leases];
            _leases.Clear();
        }

        foreach (var lease in pending)
            lease.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => Complete();
}

/// <summary>图片元素渲染器。</summary>
public sealed class ImageRenderer : ElementRenderer<ImageElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Image;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, ImageElement element, RenderContext context)
    {
        var o = element.Image;
        // formatter 返回值替换图片源：EmbeddedBase64 → base64 数据（允许 data URI）；ResolverKey → 新资源键
        var scriptSrc = ScriptSupport.FormatterResult(context, element) as string;
        var lease = Resolve(element, o, context, scriptSrc);
        if (lease is null)
        {
            DrawPlaceholder(canvas, element);
            return;
        }

        // 租约在手期间这张图不会被缓存放掉。导出器给这次渲染挂了袋（PDF 是录制的，页面/文档关闭才读图），
        // 就交给袋延到那时候；没有袋说明这条路径即时绘制，画完即还。
        var held = ImageLeaseBag.TryHold(context, lease);
        try
        {
            DrawImage(canvas, element, o, lease.Image);
        }
        finally
        {
            if (!held)
                lease.Dispose();
        }
    }

    void DrawImage(SKCanvas canvas, ImageElement element, ImageOptions o, SKImage image)
    {
        var dst = Geometry.Fit(image.Width, image.Height, Geometry.Bounds(element), o.FitMode);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };

        // 圆角按元素框裁：图片自身缩放后可能小于元素框（Contain），只在图片实际绘制区域上圆角
        if (o.CornerRadiusMm > 0)
        {
            var bounds = Geometry.Bounds(element);
            var radius = (float)Math.Clamp(o.CornerRadiusMm, 0, Math.Min(bounds.Width, bounds.Height) / 2);
            var saved = canvas.Save();
            using (var path = new SKPath())
            {
                path.AddRoundRect(bounds, radius, radius, SKPathDirection.Clockwise);
                canvas.ClipPath(path, antialias: true);
            }

            canvas.DrawImage(image, dst, paint);
            canvas.RestoreToCount(saved);
            return;
        }

        canvas.DrawImage(image, dst, paint);
    }

    ImageCache.Lease? Resolve(ImageElement element, ImageOptions o, RenderContext context, string? scriptSrc)
    {
        switch (o.SourceType)
        {
            case ImageSourceType.EmbeddedBase64:
            {
                // formatter 返回值优先（脚本动态出图），否则用模板内嵌数据
                var data = scriptSrc is { Length: > 0 } ? scriptSrc.Trim() : o.Data?.Trim();
                if (string.IsNullOrWhiteSpace(data))
                {
                    context.Report.Add(element.Id, RenderIssueKind.ImageMissing, "图片来源为 EmbeddedBase64，但 data 字段为空。");
                    return null;
                }

                if (data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    context.Report.Add(element.Id, RenderIssueKind.ImageMissing,
                        "图片源是网络地址，当前只支持 base64 内嵌数据；请用 ResolverKey 由宿主解析或让 formatter 返回 base64。");
                    return null;
                }

                // data URI 前缀剥离：data:image/png;base64,xxxx
                if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = data.IndexOf(',');
                    if (comma < 0 || !data[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Report.Add(element.Id, RenderIssueKind.ImageDecodeFailed, "data URI 缺少 base64 载荷。");
                        return null;
                    }

                    data = data[(comma + 1)..];
                }

                byte[] bytes;
                try
                {
                    bytes = System.Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    context.Report.Add(element.Id, RenderIssueKind.ImageDecodeFailed, "data 字段不是合法 Base64。");
                    return null;
                }

                return ImageCache.Shared.GetOrAdd("b64:" + CacheKeyOf(data), () => Decode(bytes, element, context));
            }

            case ImageSourceType.ResolverKey:
            {
                // formatter 返回值替换资源键（脚本按数据挑图）
                var key = scriptSrc is { Length: > 0 } ? scriptSrc.Trim() : o.ResolverKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    context.Report.Add(element.Id, RenderIssueKind.ImageMissing, "图片来源为 ResolverKey，但 resolverKey 字段为空。");
                    return null;
                }

                if (context.Images is null)
                {
                    context.Report.Add(element.Id, RenderIssueKind.ImageMissing,
                        $"宿主未提供 IImageResolver，无法解析 resolverKey '{key}'。");
                    return null;
                }

                var resolver = context.Images;

                // 缓存命中时连宿主都不该再问：宿主实现里往往是读盘或查库，
                // 批量导出 1000 张面单就是 1000 次同样的 IO。
                return ImageCache.Shared.GetOrAdd("key:" + key, () =>
                {
                    var resolved = resolver.Resolve(key);
                    if (resolved is null or { Length: 0 })
                    {
                        context.Report.Add(element.Id, RenderIssueKind.ImageMissing, $"IImageResolver 未返回键 '{key}' 的图片数据。");
                        return null;
                    }

                    return Decode(resolved, element, context);
                });
            }

            default:
                context.Report.Add(element.Id, RenderIssueKind.ImageMissing, $"不支持的图片来源类型 {o.SourceType}。");
                return null;
        }
    }

    /// <summary>
    /// 字节 → <see cref="SKImage"/>。<c>FromEncodedData</c> 对"字节合法但不是图片"只返回 null，不抛异常，
    /// 所以这里必须自己补一条告警，否则用户只会看到纸上一个莫名其妙的占位框。
    /// </summary>
    static SKImage? Decode(byte[] bytes, ImageElement element, RenderContext context)
    {
        var decoded = SKImage.FromEncodedData(bytes);
        if (decoded is null)
            context.Report.Add(element.Id, RenderIssueKind.ImageDecodeFailed, "图片数据不是可解码的图片格式。");

        return decoded;
    }

    static string CacheKeyOf(string base64)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(base64));
        return System.Convert.ToHexString(hash);
    }

    /// <summary>缺图时画占位框：浅灰虚线 + 对角叉，让设计者一眼看出这里空了。</summary>
    static void DrawPlaceholder(SKCanvas canvas, ImageElement element)
    {
        var rect = Geometry.Bounds(element);
        using var paint = Paints.Stroke(new SKColor(200, 200, 200), Math.Min(0.3, Math.Max(0.1, element.WidthMm / 100)), [1.2, 0.8]);
        canvas.DrawRect(rect, paint);
        canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, paint);
        canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, paint);
    }
}
