# Avalonia WASM 在线示例：构建与 GitHub Pages 部署

`samples/CrossPrint.Samples.Avalonia.Browser` 把桌面 Avalonia 样例的同一套源码编译成 WebAssembly，
产物是**纯静态文件**，可部署到 GitHub Pages 或任意静态托管。本文说明构建、本地验证、Pages 部署，
以及浏览器环境的特殊处理（单视图生命周期、内嵌字体、无文件系统）。

## 1. 构建要求

| 依赖 | 说明 |
|---|---|
| .NET SDK 8.0.x | 建议 8.0.4xx 及以上 |
| `wasm-tools` workload | emcc 链接 SkiaSharp/HarfBuzz 原生静态库所必需 |

```
dotnet workload install wasm-tools
```

> Windows MSI/VS 安装的 SDK 装 workload 可能需要管理员权限；便携版（zip 解压）SDK 可免提权安装。
> 本仓库的 CI（`.github/workflows/pages-demo.yml`）在 ubuntu runner 上用 `setup-dotnet` +
> `dotnet workload install wasm-tools`，正常 SDK 即可，无特殊配置。

## 2. 构建

```
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# 产物：samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

产物结构：

```
wwwroot/
├── index.html            # 容器页（div#out），全部相对路径引用
├── app.css
├── main.js               # Avalonia.Browser 标准引导：起 .NET wasm 运行时后跑 Main
└── _framework/           # dotnet.native.wasm + 各程序集（*.wasm）+ icu 数据
                          # 同时带 .br/.gz 预压缩变体（服务器按 Accept-Encoding 协商）
```

几个关键工程配置（见 csproj 内注释）：

- **`WasmBuildNative=true` 必须显式设置**：Avalonia.Browser 的 targets 设默认值的时机晚于 SDK 判定，
  缺失时 `NativeFileReference`（libSkiaSharp/libHarfBuzz 静态库）不参与链接，画布租借 `SKCanvas` 直接崩。
- **`net8.0-browser` 目标 + `Microsoft.NET.Sdk.WebAssembly` SDK**。
- 桌面样例源码通过 `<Compile Include>` 共享（排除桌面版 `Program.cs`）；`#if BROWSER` 分流平台差异。

## 3. 本地验证

任意静态服务器都可以，但要注意两点：

1. **MIME 必须正确**：`.wasm` 必须 `application/wasm`，ES module 必须 `text/javascript`——
   浏览器对二者强校验 Content-Type，错一个就白屏（主流服务器默认行为都正确，自己写脚本时注意）。
2. **发布产物同时含原始文件与 `.br`/`.gz` 变体**，不协商压缩直接回原始文件即可运行。

```
# 例：Python
python -m http.server 8080 --directory samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
# 打开 http://localhost:8080/
```

## 4. GitHub Pages 部署

仓库自带 `.github/workflows/pages-demo.yml`，一次配置后推送到 main 自动部署：

1. **仓库设置（一次性）**：Settings → Pages → Build and deployment → Source 选 **GitHub Actions**。
2. 推送到 main（或手动 Run workflow）→ workflow 完成 → 在线地址为
   `https://<user>.github.io/<repo>/`（workflow 的 environment URL 会直接给出）。

部署要点（已内置，改结构时注意）：

- **子路径兼容**：`index.html`/`main.js` 全部使用相对路径（`./_framework/...`），
  Pages 的 `/repo/` 子路径部署无需任何 `<base href>` 或路径改写。
- **缓存失效**：csproj 的 `StaticWebAssetFingerprintPattern` 给 JS 产物加内容指纹，
  浏览器强缓存也能在发版后立即拿到新文件。
- **压缩**：产物自带 `.br`/`.gz` 变体；Pages CDN 会自行协商，无需服务器配置。

## 5. 浏览器环境的特殊处理

与桌面版的差异全部在 `#if BROWSER` 分流，核心是三件事：

### 5.1 单视图生命周期

Avalonia.Browser **没有 windowing platform**——连 `new Window()` 都会抛异常。因此：

- 共享 UI 拆成 `MainView : UserControl`（`MainWindow.cs`），桌面由 `MainWindow.desktop.cs`
  的 `Window` 薄包装持有，浏览器走 `ISingleViewApplicationLifetime.MainView` 直接挂进 `div#out`。
- 涉及窗口的交互（预览弹窗、自定义纸张弹窗）用 `TopLevel.GetTopLevel(this) is Window` 判别，
  浏览器上降级为状态栏提示（"请使用导出 PDF"）。
- 文件对话框走 `TopLevel.StorageProvider`（浏览器上映射为 File System Access / 下载）。

### 5.2 内嵌字体（WASM 没有系统字体）

浏览器里 UI 文本和画布文本都会变"豆腐块"，样例内嵌 Noto Sans SC 可变字体（SIL OFL 1.1，
许可证见 `samples/CrossPrint.Samples.Avalonia/Assets/Fonts/LICENSE-NotoSansSC.txt`）：

- csproj 里用 **`<AvaloniaResource>`**（不是 `EmbeddedResource`）嵌入——`EmbeddedFontCollection`
  走 avares 协议枚举资源目录，`EmbeddedResource` 不进 Avalonia 资源清单（实测集合计数为 0）。
- `EmbeddedFonts.RegisterAvaloniaFont()` 把字体注册进 `FontManager`，**必须在 `App` 构造函数里调用**——
  Avalonia.Browser 在 `AfterApplicationSetup` 阶段（早于 `OnFrameworkInitializationCompleted`）
  就构造 `AvaloniaView` → TopLevel/Compositor 初始化会解析默认字体族，集合未注册会直接抛
  `Could not create glyphTypeface. Font family: $Default`。
- `Program.cs` 的 `FontManagerOptions.DefaultFamilyName` 指向注册键
  `fonts:CrossPrintSample#Noto Sans SC`。
- 画布文字走 CrossPrint 自己的 `IFontResolver`，浏览器版由
  `EmbeddedFonts.CreateCanvasResolver()` 注入恒返回内嵌字体的实现。

### 5.3 无文件系统

样例模板资产（kitchen-sink / complex-receipt / hiprint 导入样例）通过 `<EmbeddedResource>` +
`LogicalName` 内嵌，共享代码 `SampleAssets` 按 `#if BROWSER` 读内嵌资源。

## 6. 故障排查

| 症状 | 原因与处理 |
|---|---|
| 白屏，控制台报 MIME / "module script failed" | 服务器 Content-Type 不对：`.wasm` → `application/wasm`，`.js` → `text/javascript` |
| 白屏，`Could not create glyphTypeface. Font family: $Default` | 字体集合注册太晚（见 5.2）或字体没进 AvaloniaResource 清单 |
| 界面正常但中文是方块 | 内嵌字体缺失/未注册（见 5.2）；确认 csproj 用 `AvaloniaResource` |
| `NETSDK1147: must install workload wasm-tools` | 未装 workload（见第 1 节） |
| 画布租借 SKCanvas 崩溃 / 渲染黑块 | `WasmBuildNative=true` 缺失，SkiaSharp 原生库没链进 wasm（见第 2 节） |
| 发布后白屏但本地正常 | 资源路径走了绝对路径——检查自定义 HTML/JS 是否保持 `./` 相对引用 |
