# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 与 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式。
版本号唯一真源为根目录 `Directory.Build.props` 的 `VersionPrefix`；`v*` tag 推送后由
`.github/workflows/release.yml` 自动构建便携包、NuGet 包并创建 GitHub Release。
破坏性变更以 `!` 标注。

## [1.1.0] - 2026-11-09

### Added

- **六宿主对齐**：WPF / WinForms / WinUI / Eto / Uno / MAUI 补齐模板库（内置模板分类 + 缩略图卡片 + 点击套用）、
  样式库（21 字段可空值编辑，变更可撤销）、右上角 Toast 提示、工具箱搜索过滤与拖放入画布、
  撤销/重做按钮与快捷键、状态栏四档缩放、水印编辑面板、JSON 面板、Toast/分栏等交互细节。
- **Uno / MAUI 分栏可拖宽**：零依赖轻量列分隔条（Uno 用 grid 级指针追踪、MAUI 用 `PanGestureRecognizer`），
  限幅 140–560px，两侧栏宽可调、画布自适应。
- **连续纸表格自动增高**（`TemplateRenderer.AutoGrowContinuousTables`）：连续纸按表格内容增高、
  表格下方元素整体顺移、页高自适应；PDF / 批量导出 / GDI 直打三条出图路径均「先增高、再算纸」，
  修复小票只有固定几条无法加数据的问题；小票 `{{subtotal}}/{{total}}/{{points}}` 随明细实时求和。
- **GDI 直打、按元素类型裁剪属性、表格单元格图片、20 轮 × 10 万行压测入口**（DataStudio demo 与
  `BigDataStressTests` 双形态）。
- **设计器交互批**：内置模板库、水印、形状元素、颜色选择器 UX；模板预览对话框（卡片内联缩略图）。
- **CI / 测试**：`workflow_dispatch` 发布入口、NuGet OIDC 受信任发布、Trim smoke 修复、
  跨平台字体/PDF/culture 断言适配、端到端小票导出 MediaBox 回归（500 行复杂小票）。

### Changed

- **版本统一**：`CrossPrint.I18n` 移除 `VersionPrefix 0.1.0` 项目级覆盖，改继承产品版本 1.1.0。
- README 双语同步：NuGet 包名统一为 `OODD.CrossPrint.*`，补齐 WebAssembly 示例可用性说明。
- 长文本与图片引入固定护栏（`MaxContentChars=200,000` / `MaxLines=20,000` /
  `MaxCharsPerLine=8,000`；图片 32MB / 16,000,000 像素 / 单边 8192，超限拒收并告警），
  数值与语义写入 README 双语「已知限制」。

### Fixed

- **多宿主真机筛查修复**：Uno Skia 头应用级浅色主题锁、MAUI Windows 头元素级主题锁、
  WinUI 全部 7 处对话框 `ElementTheme.Light`；Uno culture 归一（zh 前缀回落 zh-CN）；
  补齐 `Dialog.OK` / `Dialog.Open` / `Menu.File.NoRecent` 词条缺口。
- **Uno 模板库缩略图全空白**：PNG 落临时文件 + `BitmapImage(Uri)` 文件 URI 解码，
  绕过 `DataWriter.DetachStream` 未实现、包装流 Dispose 关闭底层流、`SetSourceAsync(内存流)` 内部 NRE 三层坑。
- **Uno 分栏拖不动**：`CapturePointer` 后续 `PointerMoved` 不路由 + `e.Pointer` 实例逐事件不同，
  改 grid 级事件按 `active` 非空过滤。
- **打印/分页**：续页表格撑满内容区压住页码脚注；`{{pageCount}}` 占位元素被误归页脚；
  连续纸增高后 PDF MediaBox / GDI 纸盒仍按原模板计算导致截断。
- **设计器**：网格/安全区标线被文档白底覆盖（画布渲染改 Transparent 背景，
  打印/导出仍白底）；属性面板空白区持久实例（避免重复挂树崩溃）；表格列 `cellRender` 中文选项。
- **引擎 / CI**：GDI 直打按工作副本算纸；Trim smoke 全局 `PublishTrimmed` 泄漏进多目标引用；
  `#if BROWSER` 盲区、Linux 容器字体依赖（libfontconfig1 / fonts-noto-cjk）；
  netstandard2.0 / net461 的 trim 守卫。
- **阶段 H 告警清零**：CS1574（cref）、CS4014（fire-and-forget）、CS1998（假 async）；
  `slnf` 全量编译 0 错误。
- **发布前回归：整表非文本列行高塌成 0**：表格所有列都是图片/一维码/二维码且未声明
  `CellContentHeightMm` 时，行高为 0 → 内容框高 0 → 单元格不绘制、解码失败告警也不产生。
  给非文本列补与文本列同口径的字体行高下限；相应测试页高留出元素越界余量。
  （Release 全量测试抓出，修复后 908/908 通过。）

### Known Limitations

- 浏览器 WASM 仅为 Avalonia 平台特性；DataStudio / DataTest 是独立 demo 工程，非宿主功能。
- Uno / MAUI 工具箱以**点击放置**兜底（平台拖放无法无头实证）；MAUI 与 WinUI 的
  GUI 自动化合成输入不受响应，相关交互需真机人工验证。
- Uno 无障碍树为空（平台侧 P3）；Uno 的 `ListBox` 触发 Uno0001 分析器提示但 Skia 头实际可用。
- 图片固定尺寸、不自适应宽高、不做跨页；长文本按固定护栏截断（详见 README 双语「已知限制」）。

[1.1.0]: https://github.com/OODD-DevNet/OODD.CrossPrint/compare/v1.0.0...v1.1.0

## [1.0.0] - 2026-11-02

首个公开版本：一份模板 + 一个 SkiaSharp 渲染内核，跨 Avalonia / WPF / WinForms / Eto.Forms /
WinUI / Uno / .NET MAUI 七宿主；模板 JSON / hiprint 导入、PDF 导出、系统打印、数据绑定、
分页与表格、解决方案持久化，以及 10 个 NuGet 库包与多平台便携包。

[1.0.0]: https://github.com/OODD-DevNet/OODD.CrossPrint/releases/tag/v1.0.0
