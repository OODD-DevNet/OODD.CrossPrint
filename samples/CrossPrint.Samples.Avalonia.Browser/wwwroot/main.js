// Avalonia.Browser 标准引导（Avalonia 11 模板同款）：起 .NET wasm 运行时后跑主程序集的 Main。
// 额外：成功后把 <body class="ready"> 让启动遮罩淡出，失败时把错误写进遮罩（不要静默白屏）。
try {
    const { dotnet } = await import('./_framework/dotnet.js');

    const is_browser = typeof window != "undefined";
    if (!is_browser) throw new Error(`Expected to be running in a browser`);

    const dotnetRuntime = await dotnet
        .withDiagnosticTracing(false)
        .withApplicationArgumentsFromQuery()
        .create();

    const config = dotnetRuntime.getConfig();
    await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);

    // .NET WASM 启动成功，隐藏启动遮罩
    document.body.classList.add('ready');
} catch (err) {
    // 启动失败——把错误信息写进启动遮罩，让用户看到而不是白屏无提示
    console.error('CrossPrint WASM 启动失败:', err);
    try {
        const boot = document.getElementById('boot');
        if (boot) {
            boot.innerHTML = `
                <div class="logo" style="color:#E06C75;border-color:#E06C7533;">!</div>
                <div class="title" style="color:#E06C75;">启动失败</div>
                <div class="sub" style="white-space:pre-wrap;max-width:480px;text-align:center;">
                    浏览器可能不支持 WebAssembly / ES Modules。
                    请用最新版 Chrome / Edge / Firefox / Safari 打开。
                    错误：${err?.message || String(err)}
                </div>
                <div class="bar" style="opacity:.3;"><span></span></div>
            `;
        }
    } catch { /* 连遮罩都改不了就放弃 */ }
}
