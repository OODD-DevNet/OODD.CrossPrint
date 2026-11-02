using System.Runtime.InteropServices.JavaScript;

namespace CrossPrint.Samples.Avalonia.Browser;

/// <summary>
/// 启动期诊断：把关键步骤/异常直接写进 DOM（页面左上角红字），不走 Console——
/// WASM 崩溃时 stdout 可能整段丢失，而 DOM 内容一定可见，浏览器测试也能直接断言。
/// </summary>
internal static partial class WebDiagnostics
{
    [JSImport("globalThis.eval")]
    private static partial void Eval(string code);

    public static void Report(string message)
    {
        // 模板字符串注入前转义反引号/反斜杠/$，避免诊断文本破坏 JS 字面量。
        var safe = message.Replace("\\", "\\\\").Replace("`", "\\`").Replace("$", "\\$");
        Eval(
            "(window.__diag = (function () { var el = document.getElementById('diag');" +
            " if (!el) { el = document.createElement('pre'); el.id = 'diag';" +
            " el.style.cssText = 'position:fixed;top:0;left:0;z-index:99999;background:#fff;color:#c00;" +
            "font-size:12px;max-width:100vw;white-space:pre-wrap;margin:0;padding:4px;" +
            "box-shadow:0 0 4px rgba(0,0,0,.4);'; document.body.appendChild(el); } return el; })())" +
            $".textContent += `{safe}` + '\\n';");
    }

    public static void Report(Exception ex) => Report(ex.ToString());
}
