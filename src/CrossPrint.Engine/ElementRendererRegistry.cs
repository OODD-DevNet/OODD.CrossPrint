using CrossPrint.Core;
using CrossPrint.Engine.Rendering;

namespace CrossPrint.Engine;

/// <summary>
/// 元素类型键 → 渲染器。宿主可 <see cref="Register"/> 同名键覆盖内置实现，或注册自定义类型键。
/// 实例化使用（非全局静态），这样多租户/插件场景互不污染；<see cref="Default"/> 提供开箱即用的一份。
/// </summary>
public sealed class ElementRendererRegistry
{
    readonly Dictionary<string, IElementRenderer> _map = new(StringComparer.Ordinal);

    /// <summary>创建空注册表。</summary>
    public ElementRendererRegistry()
    {
    }

    /// <summary>创建并填满 8 个内置渲染器。</summary>
    public static ElementRendererRegistry CreateDefault()
    {
        var registry = new ElementRendererRegistry();
        registry.Register(new TextRenderer());
        registry.Register(new LineRenderer());
        registry.Register(new RectangleRenderer());
        registry.Register(new EllipseRenderer());
        registry.Register(new ImageRenderer());
        registry.Register(new QrCodeRenderer());
        registry.Register(new BarcodeRenderer());
        registry.Register(new TableRenderer());
        return registry;
    }

    /// <summary>进程级共享默认注册表。渲染器无状态，可并发使用。</summary>
    public static ElementRendererRegistry Default { get; } = CreateDefault();

    /// <summary>注册渲染器；同一 <see cref="IElementRenderer.TypeKey"/> 后注册者生效。</summary>
    public ElementRendererRegistry Register(IElementRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (string.IsNullOrEmpty(renderer.TypeKey))
            throw new ArgumentException("渲染器必须提供非空 TypeKey。", nameof(renderer));

        _map[renderer.TypeKey] = renderer;
        return this;
    }

    /// <summary>注销某类型键，返回是否移除成功。</summary>
    public bool Unregister(string typeKey) => _map.Remove(typeKey);

    /// <summary>查找渲染器，未注册返回 null。</summary>
    public IElementRenderer? Find(string typeKey) => _map.TryGetValue(typeKey, out var r) ? r : null;

    /// <summary>已注册的类型键（排序后）。</summary>
    public IReadOnlyCollection<string> RegisteredTypeKeys => _map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>内置 8 类元素是否全部具备渲染器。<see cref="TemplateRenderer"/> 用它自检。</summary>
    public bool CoversBuiltinTypes()
    {
        foreach (var key in ElementTypeKeys.All)
        {
            if (Find(key) is null)
                return false;
        }

        return true;
    }
}
