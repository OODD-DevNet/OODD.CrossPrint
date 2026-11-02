# CrossPrint

<p align="center">
  <img src="assets/oodd-crossprint-logo.png" width="128" alt="OODD CrossPrint Logo" />
</p>

跨平台 .NET 打印模板组件：一份模板、一个渲染内核，嵌入 Avalonia / WPF / WinForms / Eto.Forms / WinUI / Uno / .NET MAUI，设计、预览、PDF 导出与打印共享同一条 SkiaSharp 渲染路径。可直接导入 hiprint（vue-plugin-hiprint / Vue-Print-Designer）设计器导出的模板。

> 模板只写毫米与磅；元素外观由引擎画，宿主控件只负责"把画布放到屏幕上"。设计器里看到的就是导出的、导出的就是打印的。

**中文** | [English](README.en.md)

## 特性一览

- **八种元素**：文本、线段、矩形、椭圆、图片、二维码、一维码（10 种编码）、表格（多行表头 / 列序号 / 尾行汇总）。
- **所见即所得**：设计器、打印预览、PDF 导出走同一个 `TemplateRenderer`，不存在"设计器一个样、打出来另一个样"。
- **数据绑定**：`{{order.customer.name}}` 式路径占位符，表格按路径自动展开行；POCO / 字典 / `JsonElement` 都能当数据源；绑定失败降级为空串 + 告警，不炸整页。
- **formatter / styler 脚本**（hiprint 同款语义）：内嵌 Jint 沙箱执行 JS，改文本、改样式；单次 250ms 超时 + 语句/递归上限，失败一律降级为"没有脚本"，渲染永不中断。
- **样式库**：模板内的命名样式（字体 / 颜色 / 对齐 / 边框…），元素按引用取样式，改一处全模板生效。
- **hiprint 导入**：`getJson()` 输出、JS 模块源码、严格 JSON 都能导；无法等价表达的特性逐条留痕（稳定 `Code` + 源路径），不静默丢弃。
- **打印目标可插拔**：`PdfFilePrintTarget`（打印到 PDF 文件）、`SystemPrintTarget`（交给操作系统打印管线：Windows 弹关联应用打印框，macOS / Linux 交 CUPS）。
- **可裁剪 / 可 AOT**：模板 JSON 走 System.Text.Json 源生成，Core 零 IL 警告。
- **单位即语义**：页面与几何一律毫米（`WidthMm` / `Xmm`…），字体一律磅（`SizePt`），没有像素换算陷阱。

## 设计器与打印预览

<p align="center">
  <img src="assets/print-design-screenshot.png" width="48%" alt="设计器画布" />
  <img src="assets/print-preview-screenshot.png" width="48%" alt="打印预览" />
</p>

左：Avalonia 宿主设计器（厨房水槽模板，含全部八种元素 + 脚本）；右：同模板的打印预览（或导出 PDF 渲染结果）。两者走同一条 `TemplateRenderer`，所见即所得。

## 在线示例（WebAssembly）

Avalonia 样例可编译为 WebAssembly 跑在浏览器里（同一份画布代码、同一条 SkiaSharp 渲染路径），构建产物是纯静态文件，可直接部署到 GitHub Pages 等任意静态托管：

```
dotnet workload install wasm-tools
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# 产物：samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

部署步骤与注意事项（子路径、MIME/压缩、内嵌字体）见 [docs/webassembly-demo.md](docs/webassembly-demo.md)；仓库自带 GitHub Actions workflow，推送到 main 即自动发布到 Pages。

## 状态矩阵

| 宿主 | 适配层 | 样例 | 验证方式 |
|---|---|---|---|
| Avalonia 11.3（桌面） | `CrossPrint.Hosts.Avalonia` | `samples/CrossPrint.Samples.Avalonia` | 编译 + 无头渲染冒烟（96/144 DPI 像素断言）；交互归人工复核 |
| Avalonia 11.3（Browser / WASM） | 同上（`net8.0-browser`） | `samples/CrossPrint.Samples.Avalonia.Browser` | 编译 + 浏览器人工冒烟（渲染/中文已验证，交互归人工复核） |
| WPF (net8.0-windows) | `CrossPrint.Hosts.Wpf` | `samples/CrossPrint.Samples.Wpf` | 编译 + 无头渲染冒烟；交互归人工复核 |
| WinForms (net8.0-windows) | `CrossPrint.Hosts.WinForms` | `samples/CrossPrint.Samples.WinForms` | 编译 + 无头渲染冒烟；交互归人工复核 |
| Eto.Forms 2.10 | `CrossPrint.Hosts.Eto` | `samples/CrossPrint.Samples.Eto` | 编译（Windows 平台）；运行冒烟归人工 |
| WinUI 3 (WindowsAppSDK 1.6) | `CrossPrint.Hosts.WinUi` | `samples/CrossPrint.Samples.WinUi` | 编译；运行冒烟归人工 |
| Uno 5.6（Skia 桌面单投影，net8.0-desktop） | `CrossPrint.Hosts.Uno` | `samples/CrossPrint.Samples.Uno` | 编译（`SKSwapChainPanel`，SkiaSharp 2.88.9）；运行冒烟归人工 |
| .NET MAUI 8（Windows） | `CrossPrint.Hosts.Maui` | `samples/CrossPrint.Samples.Maui` | 编译；运行冒烟归人工 |

内核层（`CrossPrint.Core` / `CrossPrint.Engine` / `CrossPrint.Designer` / `CrossPrint.Printing`）不依赖任何 UI 框架，可在 Windows/Linux/macOS 上构建与测试。

## 仓库结构

```
src/
  CrossPrint.Core       模型、毫米/磅单位、JSON（源生成）、hiprint 模板导入、字体与图片解析抽象
  CrossPrint.Engine     渲染器、数据绑定、formatter/styler 脚本执行、PNG/PDF/批量导出
  CrossPrint.Designer   编辑器内核、命中测试、属性通道、叠加层绘制（无 UI 依赖）
  CrossPrint.Printing   打印目标抽象、PDF 文件目标、系统打印目标、批量打印
  CrossPrint.Hosts.*    各宿主薄适配层（画布桥接 + 输入转发，约 300–400 行/宿主）
samples/                七个可运行样例（同一套工具箱/纸张栏/属性面板/右键菜单/脚本/样式库/hiprint 导入/预览/打印）＋ Avalonia 浏览器版（CrossPrint.Samples.Avalonia.Browser，见「在线示例」）
tests/                  Core / Engine / Designer / Printing / Avalonia 五套测试
docs/                   需求、架构、开发、任务计划、模板格式、渲染规格、WASM 部署、许可清单
```

## 快速开始

要求 .NET SDK **8.0.420**（`global.json` 已锁定）。

```bash
dotnet build OODD.CrossPrint.sln
dotnet test OODD.CrossPrint.sln
```

Linux/macOS 只构建/测试内核层：

```bash
dotnet build OODD.CrossPrint.Portable.slnf
dotnet test OODD.CrossPrint.Portable.slnf
```

运行样例（Windows）：

```bash
dotnet run --project samples/CrossPrint.Samples.Avalonia
dotnet run --project samples/CrossPrint.Samples.Wpf
dotnet run --project samples/CrossPrint.Samples.WinForms
dotnet run --project samples/CrossPrint.Samples.Eto
dotnet run --project samples/CrossPrint.Samples.WinUi
dotnet run --project samples/CrossPrint.Samples.Uno
dotnet run --project samples/CrossPrint.Samples.Maui
```

Uno 样例目标为 `net8.0-desktop`（Uno 5.6 的 Skia 桌面单投影，`Uno.Sdk/5.6.54`），不需要额外 workload；Uno 6.7 要求 SkiaSharp 3.119 与 net9，与仓库的 SkiaSharp 2.88.9 锁不兼容，暂不跟进。

样例自带 `kitchen-sink.template.json`（构建时拷贝到输出目录），覆盖全部八种元素；还带 `hiprint-sample.js`（hiprint 模块源码示例），供「文件 → 导入 hiprint 模板…」直接打开。

## 设计器使用指南（各样例交互一致）

### 界面布局

```
┌──────────────────────────────────────────────────────────┐
│ 菜单栏（文件：导入 hiprint / 复杂模板 / 样式库 / 打印）      │
├──────────────────────────────────────────────────────────┤
│ 工具带 1：文字 线段 矩形 椭圆 图片 二维码 一维码 表格        │
│ 工具带 2：导入 样式库 | 副本 删除 | 上移 下移 置顶 置底      │
│           | 适合窗口 100% | □网格 □标尺 □安全区             │
│           | 打印 导出 PDF 预览                             │
├────────┬───────────────────────────────┬─────────────────┤
│ 纸张    │                               │ 属性面板         │
│ 工具箱  │         设计画布               │ （按选中元素     │
│ (可拖宽)│      （滚轮缩放/中键平移）      │   生成表单）     │
│        │                               │   (可拖宽)       │
├────────┴───────────────────────────────┴─────────────────┤
│ ▸ 模板 JSON（默认折叠；展开后可拖高）                       │
├──────────────────────────────────────────────────────────┤
│ 状态栏                                                    │
└──────────────────────────────────────────────────────────┘
```

左右侧栏与属性面板的宽度都能拖分割条调整；底部「模板 JSON」带默认折叠，点「▸ 模板 JSON」展开（可拖高），把竖向空间留给画布。

### 添加与编辑元素

- **工具箱**：左侧选中元素类型后点「添加到画布」（或双击/回车），元素落在页面中心；工具带按钮一键添加。
- **属性面板**：选中一个元素后按类型生成表单，按「基本 / 位置与尺寸 / 外观 / 文字 / 线 / 形状 / 图片 / 二维码 / 一维码 / 表格 / 表格列 / 脚本」分组。提交即写回模板并重绘；同一选中下拖拽/缩放只回填值，不会拆掉正在输入的控件。
- **脚本字段**：文本/表格等元素带 `formatter` / `styler` 输入框（多行），语义见[下文](#脚本formatter--styler)。
- **样式引用**：外观字段旁的「样式引用」下拉列出模板命名样式，来自「文件 → 样式库…」。
- **右键菜单**：创建副本（Ctrl+D）、删除、z 序四操作、水平/垂直居中、适合窗口 / 100%、旋转纸张、自定义纸张。右键命中元素即选中、命中空白即清选。

### 纸张

左侧纸张栏：A3 / A4 / A5 / B5 / 80mm、58mm 连续纸 / 100×150 面单 / 自定义；可切横向/纵向，连续纸固定纵向。每次变更都走 `PaperCommand`，可撤销，并自动整页适配。

### 模板 JSON 面板

底部「模板 JSON」带展开后实时显示当前模板 JSON：可直接改文本后点「应用 JSON」（多数宿主支持 Ctrl+Enter）替换模板；「重新载入」丢弃编辑。从 hiprint 设计器复制来的 `getJson`（`{"panels":[…]}`）粘进来也能直接应用，会自动走导入器转换并列出降级项。画布/属性面板的修改会自动同步进文本框，文本框里有未应用的编辑时不会被覆盖；改坏了只报错、不动当前模板。

### 预览 / 导出 / 打印

- **预览**：只读 `PreviewControl` 弹窗，与设计器共用渲染路径。
- **导出 PDF**：绑定示例数据后经 `PdfExporter` 落盘。
- **打印（系统）**：渲染一份临时 PDF 后交给 OS 打印管线（`SystemPrintTarget`）——Windows 走 ShellExecute「print」动词，关联应用（如 WPS / Adobe / Edge）弹打印框选打印机与份数；macOS / Linux 交 CUPS 默认队列。防连点：一次作业没回来之前不发起第二次。

### 快捷键

| 按键 | 作用 |
|---|---|
| 方向键 | 1mm 微调（Shift 10mm、Ctrl 0.1mm） |
| Ctrl+D | 创建选中元素副本 |
| Delete | 删除选中元素 |
| Esc | 取消拖拽 / 清除选择 |
| F | 整页适配 |
| 0 | 100% 缩放 |
| Ctrl+滚轮 | 缩放 |
| 中键拖动 | 平移画布 |
| Ctrl+Enter | 应用模板 JSON（文本框内） |

## 嵌入到你的应用

各宿主的控件 API 同名同形：`DesignerControl`（设计画布）与 `PreviewControl`（只读预览）。以 Avalonia 为例：

```csharp
using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Hosts.Avalonia;

var load = TemplateJson.Load(File.ReadAllText("kitchen-sink.template.json"),
                             TemplateJsonLoadOptions.Tolerant);
var designer = new DesignerControl(load.Template)
{
    Data = new { title = "示例标题", orderNo = "SO-2026-0001", sku = "SKU-42" },
};
// designer.Kernel 是编辑器内核：选择、拖动、属性提交都在它上面
```

导出走与画布同一个渲染器：

```csharp
using var stream = File.Create("out.pdf");
var report = PdfExporter.Export(
    designer.Kernel.Template,
    stream,
    data: designer.Data,
    fontResolver: designer.FontResolver,
    images: designer.Images,
    renderer: designer.Renderer);

if (report.HasIssues)
    Console.WriteLine(report); // 坏图、缺字段、脚本异常都在这，不会让整页失败
```

`PngExporter`（单页 PNG）、`BatchPdfExporter`（多模板/多数据合成一份多页 PDF）见 `docs/03-development.md`。

### 打印

打印入口统一为 `IPrintTarget.PrintBatchAsync(documents, settings, report)`，目标可插拔：

```csharp
using CrossPrint.Printing;

var document = new PrintDocument(designer.Kernel.Template, designer.Data);
var report = new RenderReport();

// 1) 打印到 PDF 文件（可自选输出路径、字体/图片解析器、渲染选项）
var result = await new PdfFilePrintTarget()
    .PrintBatchAsync([document], new PrintSettings { OutputPath = "out.pdf" }, report);

// 2) 交给操作系统打印管线：Windows 弹关联应用打印框（可自选打印机与份数），
//    macOS / Linux 交 CUPS 默认队列。真实出纸由系统完成，这里只等「交付受理」结果。
var result2 = await new SystemPrintTarget()
    .PrintBatchAsync([document], new SystemPrintOptions(), report);

Console.WriteLine(result.Success ? $"{result.PrintedPages} 页" : result.Message);
```

`PrintDocument` 的第三个参数是份数（`Copies`）；`PrintResult.Success` 表示作业已交付/落盘，出纸问题（缺纸、卡纸）由打印机与系统负责。

## 模板格式与数据绑定

- 页面与元素几何一律毫米（`WidthMm` / `Xmm` …），字体一律磅（`SizePt`）。
- 模板是 JSON，多态元素载荷由 System.Text.Json 源生成器序列化（无反射、可裁剪）。
- 字段表与示例见 [`docs/template-schema.md`](docs/template-schema.md)，渲染细节见 [`docs/rendering-spec.md`](docs/rendering-spec.md)。
- 文本支持 `{{路径}}` 占位符：`{{order.customer.name}}`、`{{items[0].sku}}`，表格按路径自动展开数据行。
- 数据源可以是 `IDictionary<string, object?>`、`IDictionary`、实现 `IDictionaryProvider` 的对象、任意 POCO（属性/字段，大小写不敏感），或 `System.Text.Json` 的 `JsonElement` / `JsonNode`（JSON 属性名区分大小写，数字保留原文）。
- 绑定失败不会抛异常：值替换为空串，并在 `RenderReport` 里记一条 `BindingMissingField`。

## 脚本（formatter / styler）

hiprint 的 `formatter`（改内容）与 `styler`（改样式）语义在本仓一等公民化：元素级与表格列级都支持，字段直接存在模板 JSON 里（`CrossPrint.Core` 建模、导入器保留原文），执行在渲染期的 Jint 沙箱里。

```json
{
  "typeKey": "table",
  "table": {
    "columns": [
      {
        "field": "price",
        "title": "单价",
        "formatter": "function(value, row, index){ return '¥' + (value || 0).toFixed(2); }",
        "styler": "function(value, row, index){ return value > 1000 ? { color:'#c0392b', fontWeight:'bold' } : ''; }"
      }
    ]
  }
}
```

调用签名（与 hiprint 对齐）：

| 位置 | 元素级 | 表格列级 |
|---|---|---|
| 1 | `value` —— 主绑定字段原始值 | `value` —— 单元格原始值 |
| 2 | `options` —— `{ id, type }` | `row` —— 当前行对象 |
| 3 | `options`（同上，占位对齐） | `index` —— 行号 |
| 4 | `options`（同上） | `options` —— `{ id, type }` |
| 5 | `templateData` —— 整份模板数据 | `templateData` —— 整份模板数据 |

- **formatter** 返回字符串替换显示文本；返回 `null` / `undefined` 保留原值。
- **styler** 返回样式：JS 对象（camelCase，如 `{ color:'#c0392b', fontWeight:'bold' }`）或 CSS 字符串（kebab-case，如 `'color:#c0392b;font-weight:bold'`）都认。支持字段：`color`、`backgroundColor`、`fontSize`、`fontWeight`、`fontStyle`、`textDecoration`、`letterSpacing`、`lineHeight`、`textAlign`、`verticalAlign`、`padding`（四向）、`borderColor`、`borderWidth`、`borderStyle`；未认识的字段按 CSS 惯例静默忽略。
- **沙箱与约束**：Jint 默认不开放任何 CLR 访问，脚本碰不到文件与网络；单次执行 250ms 超时、10 万语句上限、64 层递归上限。
- **降级纪律**：解析失败、执行异常、超时一律降级为"没有脚本"（原值原样式），并在 `RenderReport` 记 `ScriptIssue` 警告，渲染永不中断。
- **性能**：同一份源码全局预编译（AST 缓存 256 条、LRU 回收）；无脚本的模板零开销（不创建执行器）。
- **设计器可见**：设计态脚本照常执行（`value` 解析不到时回落元素测试数据），画布上直接看效果。

## hiprint 模板导入

`CrossPrint.Core.Hiprint.HiprintTemplateImporter` 把 hiprint 设计器导出的内容映射成 `PrintTemplate`：

```csharp
using CrossPrint.Core.Hiprint;

// 严格 JSON、getJson() 输出片段、JS 对象字面量、整个 .js 模块源码都接受
var result = HiprintTemplateImporter.Import(File.ReadAllText("hiprint-template.js"));

var template = result.Template;   // 直接交给设计器 / 渲染 / PDF / 打印链路
foreach (var issue in result.Issues)
    Console.WriteLine(issue);     // 无法等价表达的特性逐条留痕，不静默丢弃
```

- **输入**：单引号/无引号键/尾逗号/注释的 JS 对象、`export const json = {...}` 模块、`getJson()` 片段；函数体不执行。
- **几何**：元素 `left/top/width/height` 是 pt，统一换算成 mm；面板尺寸是 mm；`panelPageRule: "none"` 导入为连续纸。
- **元素**：文本（字段占位符、标题前缀、换行策略、内边距、字间距、两端对齐）、横/竖线、矩形/椭圆、图片（data URL）、二维码（纠错级、码下标题）、十种一维码、表格（二维表头 colspan/rowspan、隐藏列、行序号、尾行汇总、行集合字段）。旧类型 `tableCustom` 与 `table` 等价导入；`columns` 写成对象映射或单个扁平列对象也认；缺列定义时保留表格框并占一列，不整张消失。
- **脚本**：元素的 `formatter` / `styler` 与表格列的同名字段原样保留入库，交给渲染期的脚本执行器（见上节）；无法静态化的字段在 `Issues` 里留痕。
- **测试数据**：元素上的 `field + testData` 收进模板的 `designData`，设计器与打印预览在没有宿主数据时用它显示样例内容（条码/二维码不会绑成空串），真实打印与导出不读它。
- **多面板**：一个 hiprint 模板可含多个面板（多联单），`HiprintImportOptions.PanelIndex` 选择导入哪一联；`result.Panels` 给出各联的宽高与元素数供宿主做选择界面。
- **降级留痕**：水印、页码、HTML、跨页长文本、单元格内嵌码等无法表达的能力，按最接近的静态结果导入，并在 `Issues` 里给出稳定 `Code` 与源路径。`Severity=Error` 表示该元素被跳过，其余元素照常导入。

逐字段映射与限制清单见 [`docs/template-schema.md` §8](docs/template-schema.md#8-hiprint-模板导入)；渲染语义（旋转、分边边框、省略号、表头合并等）由 `HiprintSemanticsRendererTests` 逐像素验证。

## Trim / AOT

- 模板 JSON 走源生成的 `JsonTypeInfo`，Core 不产生 IL2026。
- 数据绑定对 POCO 使用反射作为兜底（Engine 里已就地抑制并注明理由）。裁剪后模型成员若被裁掉，绑定会退化为"缺字段告警 + 空串"，不会崩溃；需要确定行为的 Trim/AOT 应用请把数据源换成 `IDictionary<string, object?>` 或实现 `IDictionaryProvider`，并
  `[DynamicallyAccessedMembers]` / TrimmerRootDescriptor 保留自己的模型类型。
- `samples/CrossPrint.Samples.Avalonia` 的 `PublishTrimmed` 冒烟中，本仓库程序集零 IL 警告；剩余 `Avalonia.DesignerSupport` 的 IL2026/IL2104 来自 Avalonia 自身（设计期 BSON 远程协议），与本组件无关。

## 已知限制（v0.2）

- 二维码静区按 4 模块绘制，与个别扫码器的宽松/严格口径可能不同。
- hiprint 导入不执行导入期 JavaScript：`formatter`/`styler` 保留到渲染期由沙箱执行，水印、页码、HTML、跨页长文本、单元格内嵌码没有等价能力。多面板模板一次导入一联。
- 尚无对齐参考线/吸附、多选批量改属性；撤销/重做栈未接（每次变更都产出可 `Revert` 的命令，宿主可自行接栈）。
- `SystemPrintTarget` 依赖系统关联应用：Windows 上若关联的是 WPS 等带自家打印队列的应用，出纸路径由该应用决定；服务端无人值守打印请用 `PdfFilePrintTarget` + 自行投递。
- Uno 适配层基线是 Uno 5.6（`net8.0-desktop`，包 TFM 为 `net8.0-desktop1.0`）；Uno 6.x 需跟随 SkiaSharp 3.x，尚未提供。
- WASM 版样例的文件对话框不可用（浏览器沙箱），hiprint 导入走模板 JSON 面板粘贴。
- 设计画布交互（拖动、缩放、快捷键、打印对话框）只做了编译与像素级冒烟，手感与真机打印需要人工复核。

## 文档

- [`docs/01-requirements.md`](docs/01-requirements.md) — 需求与验收口径
- [`docs/02-architecture.md`](docs/02-architecture.md) — 分层、坐标契约、各宿主桥接方式
- [`docs/03-development.md`](docs/03-development.md) — 开发环境、命令、调试与裁剪验证
- [`docs/04-task-plan.md`](docs/04-task-plan.md) — 里程碑、交付记录、真机复核清单
- [`docs/template-schema.md`](docs/template-schema.md) — 模板 JSON 字段表与 hiprint 导入映射
- [`docs/rendering-spec.md`](docs/rendering-spec.md) — 逐元素渲染规格
- [`docs/webassembly-demo.md`](docs/webassembly-demo.md) — Avalonia WASM 在线示例构建与 GitHub Pages 部署
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — 四条架构铁律与提交要求

## 许可

Apache License 2.0。第三方依赖与许可证清单见 [`docs/third-party-licenses.md`](docs/third-party-licenses.md)（CI 会校验清单与实际包一致）。明确排除 AGPL 与商业授权依赖（含 QuestPDF）。
