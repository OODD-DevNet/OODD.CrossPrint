using CrossPrint.Core;

#if BROWSER
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using CrossPrint.Samples.Avalonia.Browser;
using SkiaSharp;
#endif

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 浏览器版字体供给：WASM 没有系统字体（Avalonia UI 文本与 CrossPrint 画布文本都会变豆腐块），
/// 从程序集内嵌的 Noto Sans SC（SIL OFL 1.1，见 Assets/Fonts/LICENSE-NotoSansSC.txt）加载。
/// 桌面版不受影响：系统字体照常解析，这一文件的内容整体被 #if BROWSER 排除。
/// </summary>
internal static class EmbeddedFonts
{
#if BROWSER
    /// <summary>字体的 Avalonia 注册键（FontManagerOptions 里写作 "fonts:CrossPrintSample#Noto Sans SC"）。</summary>
    public const string FamilyName = "Noto Sans SC";
    public const string FontKey = "fonts:CrossPrintSample";

    /// <summary>avares 根随程序集名走：桌面/Browser 共用这份代码，资源实际嵌在 Browser 程序集里。</summary>
    static string AssemblyName => typeof(EmbeddedFonts).Assembly.GetName().Name!;

    static SKTypeface? _typeface;

    /// <summary>把内嵌字体注册进 Avalonia FontManager，让 UI 控件的中文可渲染。</summary>
    public static void RegisterAvaloniaFont()
    {
        try
        {
            var key = new Uri(FontKey, UriKind.Absolute);
            var source = new Uri($"avares://{AssemblyName}/Assets/Fonts", UriKind.Absolute);
            FontManager.Current.AddFontCollection(new EmbeddedFontCollection(key, source));
        }
        catch (Exception ex)
        {
            // 仅失败时上报（WebDiagnostics 写进页面左上角，成功时保持界面干净）。
            WebDiagnostics.Report("[EmbeddedFonts] registration FAILED: " + ex);
        }
    }

    /// <summary>创建画布用字体解析器：模板里任何族名都落到内嵌字体（浏览器没有第二个选择）。</summary>
    public static IFontResolver CreateCanvasResolver() => new EmbeddedFontResolver();

    static SKTypeface Typeface => _typeface ??= LoadTypeface();

    static SKTypeface LoadTypeface()
    {
        // 字体在 AvaloniaResource（avares 清单）里，得用 Avalonia 的 AssetLoader 读，
        // 而不是 GetManifestResourceStream——EmbeddedResource 命名在资源清单里不存在。
        var uri = new Uri($"avares://{AssemblyName}/Assets/Fonts/NotoSansSC-VF.ttf", UriKind.Absolute);
        using var stream = AssetLoader.Open(uri);
        var typeface = SKTypeface.FromStream(stream);
        return typeface ?? throw new InvalidOperationException("内嵌字体解码失败：NotoSansSC-VF.ttf");
    }

    /// <summary>
    /// 画布字体兜底：Resolve 恒返回内嵌字体。可变字体的默认实例是 wght=400，
    /// 加粗请求暂按常规字重渲染（浏览器演示可接受，桌面不受影响）。
    /// </summary>
    sealed class EmbeddedFontResolver : IFontResolver
    {
        public SKTypeface Resolve(string fontFamily, bool bold, bool italic, char probeChar) => Typeface;

        // 恒 true：告知渲染管线"族存在"，避免每帧刷 FontFallback 告警——浏览器上本来就只有一个族。
        public bool FamilyExists(string fontFamily) => true;
    }
#endif
}
