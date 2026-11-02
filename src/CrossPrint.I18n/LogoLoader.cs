using System.IO;
using System.Reflection;

namespace CrossPrint.I18n;

/// <summary>
/// 产品 logo 加载器：从 I18n 程序集的 EmbeddedResource 读出 OODD CrossPrint logo，
/// 各宿主 AboutDialog 和窗口图标共用这一份 PNG（1024×1024，深蓝科技风）。
/// </summary>
public static class LogoLoader
{
    /// <summary>嵌入资源全名：CrossPrint.I18n.Assets.oodd-crossprint-logo.png</summary>
    public const string ResourceName = "CrossPrint.I18n.Assets.oodd-crossprint-logo.png";

    static readonly Assembly _asm = typeof(LogoLoader).Assembly;
    static byte[]? _cachedBytes;

    /// <summary>
    /// 打开 logo 的只读流；调用方负责 Dispose。找不到返回 null。
    /// </summary>
    public static Stream? GetLogoStream() => _asm.GetManifestResourceStream(ResourceName);

    /// <summary>
    /// 把 logo 整个读进内存，返回 byte 数组；找不到返回 null。内部带缓存。
    /// 适合用 SkiaSharp / System.Drawing / Avalonia 图片解码器直接 LoadFrom(bytes)。
    /// </summary>
    public static byte[]? GetLogoBytes()
    {
        if (_cachedBytes is not null) return _cachedBytes;
        using var stream = GetLogoStream();
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _cachedBytes = ms.ToArray();
        return _cachedBytes;
    }

    /// <summary>清掉内存缓存（热重载或单元测试切换资源时用）。</summary>
    public static void ClearCache() => _cachedBytes = null;
}
