# 第三方依赖许可清单

本文件是**人工评审过的依赖白名单**，不是自动生成的报告。`Directory.Packages.props` 里每新增或升级一个包，
都必须同步这张表；`LicenseInventoryTests` 会在两边对不上时直接红。

许可证名称取自各包 `.nuspec` 的 `license` 字段（`expression` 或 `file`），本机实测于
`F:\Installed\NuGetPackages\packages\<id>\<version>\`。

| 包 | 版本 | 许可证 |
|---|---|---|
| SkiaSharp | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Linux | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Linux.NoDependencies | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 2.88.9 | MIT |
| ZXing.Net | 0.16.11 | Apache-2.0 |
| System.Text.Json | 6.0.9 | MIT |
| Avalonia | 11.3.18 | MIT |
| Avalonia.Browser | 11.3.18 | MIT |
| Avalonia.Desktop | 11.3.18 | MIT |
| Avalonia.Headless | 11.3.18 | MIT |
| Avalonia.Skia | 11.3.18 | MIT |
| Avalonia.Themes.Fluent | 11.3.18 | MIT |
| Semi.Avalonia | 11.3.14 | MIT |
| Semi.Avalonia.ColorPicker | 11.3.14 | MIT |
| Avalonia.Controls.ColorPicker | 11.3.14 | MIT |
| FreeSql | 3.2.700 | MIT |
| FreeSql.Provider.Sqlite | 3.2.700 | MIT |
| HandyControl | 3.5.1 | MIT |
| PolySharp | 1.16.0 | MIT |
| Eto.Forms | 2.10.0 | BSD-3-Clause |
| Eto.Platform.Windows | 2.10.0 | BSD-3-Clause |
| SkiaSharp.Views.Desktop.Common | 2.88.9 | MIT |
| SkiaSharp.Views.WPF | 2.88.9 | MIT |
| SkiaSharp.Views.WinUI | 2.88.9 | MIT |
| SkiaSharp.Views.Uno.WinUI | 2.88.9 | MIT |
| Microsoft.WindowsAppSDK | 1.6.250205002 | Microsoft-EULA |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| FluentAssertions | 6.12.2 | Apache-2.0 |
| Jint | 3.1.6 | BSD-2-Clause |
| HarfBuzzSharp | 14.2.1.301 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 14.2.1.301 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 14.2.1.301 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 14.2.1.301 | MIT |
| System.Drawing.Common | 8.0.0 | MIT |

## 边界说明

- **FluentAssertions 锁 6.12.2**：7.0 起改为商业许可，不得升级。测试项目之外不得引用。
- **QRCoder 进不来**：1.8.1 传递引入 `System.Drawing.Common`（Linux 不可用），QRCoder.Core 2.0.0 强制 SkiaSharp 3.119；
  二维码/一维码统一用 ZXing.Net 的 `BitMatrix` 自绘。
- **Microsoft.WindowsAppSDK** 是微软自有许可（非 OSI），允许随应用再分发；仅在 WinUI 适配与样例中引用。
- **System.Drawing.Common 8.0.0（MIT）**：GDI 直打目标（`GdiPrintTarget` / `SystemPrintTarget` 的 GDI 回落）
  走 `System.Drawing.Printing.PrintDocument` 假脱机，属 Windows 专属 API；仅 `CrossPrint.Printing` 引用，
  运行入口都有 `OperatingSystem.IsWindows()` 守卫（非 Windows 抛 `PlatformNotSupportedException`）。
  注意与边界第二条的对照：二维码仍不用它（QRCoder 因传递引入它且 Linux 不可用被拒），
  这里是**显式、守卫过的 Windows 打印通道**，语义不同。
- **Jint 3.1.6（BSD-2-Clause）**：内嵌 JS 解释器，执行 hiprint 兼容的 formatter/styler 脚本。默认零 CLR 互操作
  （脚本碰不到文件/网络），引擎侧另有 250ms 超时、10 万语句、64 层递归约束；仅 `CrossPrint.Engine` 引用。
- **Uno.Sdk 5.6.54 / Uno.WinUI 5.6.99 及其 Skia 运行时**为 Apache-2.0。它们由 `Uno.Sdk` 的隐式包与 SDK 解析引入，
  不经过 `Directory.Packages.props`（Uno 工程关闭了 CPM，否则隐式包版本会与本表锁冲突），
  因此不在上面的 CPM 对账表内；`CrossPrint.Hosts.Uno` 的 nuspec 会把 `Uno.WinUI` 与
  `SkiaSharp.Views.Uno.WinUI 2.88.9` 作为依赖显式列出。
- 不引入 QuestPDF（AGPL）等 copyleft 组件；本库对外发布自身代码为 Apache-2.0。
- **Noto Sans SC 可变字体（samples/CrossPrint.Samples.Avalonia/Assets/Fonts/NotoSansSC-VF.ttf）**：
  SIL Open Font License 1.1（OFL-1.1），随浏览器版样例内嵌分发（WASM 无系统字体），
  许可证副本在同级 LICENSE-NotoSansSC.txt。仅样例资产，不进入任何库的程序集或 NuGet 包；
  OFL 允许再分发与嵌入文档，与 Apache-2.0 代码兼容。
- **Noto Naskh Arabic 可变字体（tests/CrossPrint.Engine.Tests/Assets/NotoNaskhArabic-VF.ttf，307,592B）**：
  同为 OFL-1.1，许可证副本 `Assets/OFL-NotoNaskhArabic.txt`。只作为**测试程序集的内嵌资源**存在——
  复杂脚本的"连字与词形式替换真的发生了"必须锚在一款确定含阿拉伯 GSUB 的字体上，
  否则换一台机器（CI 的 ubuntu 只有 DejaVu）断言就悄悄失效。不进产品、不进任何 NuGet 包。
