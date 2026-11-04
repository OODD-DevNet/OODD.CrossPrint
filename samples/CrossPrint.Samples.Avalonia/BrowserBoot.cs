#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 与 wwwroot/main.js / index.html 的桥：Avalonia 首帧就绪后由 MainView 调用收掉 HTML splash。
/// 之前由 main.js 在 WASM 运行时启动成功后立刻收——运行时就绪 ≠ 界面已渲染，用户看到一段空白。
/// </summary>
internal static partial class BrowserBoot
{
    [JSImport("crossprintReady", "globalThis")]
    internal static partial void MarkReady();
}
#else
namespace CrossPrint.Samples.Avalonia;

/// <summary>桌面构建的空实现（splash 只存在于浏览器宿主）。</summary>
internal static class BrowserBoot
{
    internal static void MarkReady()
    {
    }
}
#endif
