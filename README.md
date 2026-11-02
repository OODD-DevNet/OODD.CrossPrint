<p align="center">
  <img src="assets/oodd-crossprint-logo.png" width="128" alt="OODD CrossPrint Logo" />
</p>

# OODD CrossPrint

跨平台 .NET 打印模板组件：**一份模板、一个渲染内核**，嵌入 Avalonia / WPF / WinForms / Eto.Forms / WinUI / Uno / .NET MAUI。设计、预览、PDF 导出、系统打印共享同一条 SkiaSharp 渲染路径——所见即所得。可直接导入 hiprint（vue-plugin-hiprint / Vue-Print-Designer）设计器导出的模板。

> 模板只写毫米与磅；元素外观由引擎画，宿主控件只负责把画布放到屏幕上。设计器里看到的就是导出的，导出的就是打印的。

**中文** | [English](README.en.md) · [在线示例（WebAssembly）](https://oodd-devnet.github.io/OODD.CrossPrint/)

---

## 设计器与打印预览

<p align="center">
  <img src="assets/print-design-screenshot.png" width="48%" alt="设计器画布" />
  <img src="assets/print-preview-screenshot.png" width="48%" alt="打印预览" />
</p>

左：Avalonia 宿主设计器（厨房水槽模板，覆盖全部八种元素 + formatter/styler 脚本）。右：同模板的打印预览——两者走同一条渲染路径，所见即所得。想自己打开试试？上面的 **在线示例** 链接直接跑在浏览器里，不需要装任何东西。

---

## 快速开始

**两条路**：想先用起来 → 跑样例；想装进自己项目 → NuGet。

### 跑样例看看效果

需要 .NET SDK **8.0+**。任选一个宿主：

```bash
# Avalonia 跨平台（推荐，Windows / Linux / macOS 都能跑）
dotnet run --project samples/CrossPrint.Samples.Avalonia

# 其他宿主（Windows 专属）
dotnet run --project samples/CrossPrint.Samples.Wpf
dotnet run --project samples/CrossPrint.Samples.WinForms
dotnet run --project samples/CrossPrint.Samples.Eto
dotnet run --project samples/CrossPrint.Samples.WinUi
dotnet run --project samples/CrossPrint.Samples.Uno
dotnet run --project samples/CrossPrint.Samples.Maui
```

样例自带 `kitchen-sink.template.json`（覆盖全部八种元素）和 `hiprint-sample.js`（hiprint 模板，直接「文件 → 导入 hiprint 模板…」就能打开）。

### NuGet 嵌入你的应用

七个宿主各自有一个 NuGet 包，加上四个内核包——按需引用，**宿主 + 内核层不依赖任何特定 UI 框架**（除了宿主适配层自己）。

| 包 | 用途 |
|---|---|
| `CrossPrint.Core` | 模板模型、单位、JSON、hiprint 导入 |
| `CrossPrint.Engine` | SkiaSharp 渲染、数据绑定、脚本、PDF/PNG 导出 |
| `CrossPrint.Designer` | 设计器内核（选择、拖动、属性面板生成） |
| `CrossPrint.Printing` | 打印目标（PDF / 系统打印管线） |
| `CrossPrint.Hosts.Avalonia` | Avalonia 宿主适配 |
| `CrossPrint.Hosts.Wpf` | WPF 宿主适配 |
| `CrossPrint.Hosts.WinForms` | WinForms 宿主适配 |
| `CrossPrint.Hosts.Eto` | Eto.Forms 宿主适配 |
| `CrossPrint.Hosts.WinUi` | WinUI 3 宿主适配 |
| `CrossPrint.Hosts.Uno` | Uno 宿主适配 |
| `CrossPrint.Hosts.Maui` | .NET MAUI 宿主适配 |

最简单的嵌入（以 Avalonia 为例）：

```bash
dotnet add package CrossPrint.Core
dotnet add package CrossPrint.Engine
dotnet add package CrossPrint.Hosts.Avalonia
```

```csharp
using CrossPrint.Core;
using CrossPrint.Hosts.Avalonia;

// 加载模板
var load = TemplateJson.Load(File.ReadAllText("template.json"),
                             TemplateJsonLoadOptions.Tolerant);

// 设计画布
var designer = new DesignerControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001", customer = "张三" },
};

// 只读预览
var preview = new PreviewControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001" },
};
```

导出 PDF：

```csharp
using CrossPrint.Engine;

using var stream = File.Create("out.pdf");
var report = PdfExporter.Export(
    designer.Kernel.Template,
    stream,
    data: designer.Data,
    renderer: designer.Renderer);

if (report.HasIssues) Console.WriteLine(report); // 缺字段、坏图、脚本异常都在这，不炸整页
```

打印：

```csharp
using CrossPrint.Printing;

var document = new PrintDocument(designer.Kernel.Template, designer.Data);
var report = new RenderReport();

// 打印到 PDF 文件
await new PdfFilePrintTarget()
    .PrintBatchAsync([document], new PrintSettings { OutputPath = "out.pdf" }, report);

// 交给操作系统打印管线
await new SystemPrintTarget()
    .PrintBatchAsync([document], new SystemPrintOptions(), report);
```

---

## 设计器使用指南

各样例交互完全一致：

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

### 添加 / 编辑元素

- **工具箱**：左侧选中元素类型后点「添加到画布」（或双击/回车），元素落在页面中心；工具带按钮一键添加。
- **属性面板**：选中元素后按类型生成表单（基本 / 位置与尺寸 / 外观 / 文字 / 线 / 形状 / 图片 / 二维码 / 一维码 / 表格 / 表格列 / 脚本）。拖拽/缩放只回填值，不会拆掉正在输入的控件。
- **样式引用**：外观字段旁的「样式引用」下拉列出模板命名样式，改一处全模板生效。
- **右键菜单**：创建副本（Ctrl+D）、删除、z 序四操作、水平/垂直居中、适合窗口 / 100%、旋转纸张、自定义纸张。

### 纸张

左侧纸张栏：A3 / A4 / A5 / B5 / 80mm / 58mm 连续纸 / 100×150 面单 / 自定义；可切横向/纵向。每次变更都可撤销，并自动整页适配。

### 模板 JSON 面板（底部）

展开后实时显示当前模板 JSON：改完点「应用 JSON」（Ctrl+Enter）替换模板；「重新载入」丢弃编辑。从 hiprint 设计器复制来的 `getJson`（`{"panels":[…]}`）粘进来也能直接应用，会自动走导入器转换。

### 快捷键

| 按键 | 作用 |
|---|---|
| 方向键 | 1mm 微调（Shift 10mm、Ctrl 0.1mm） |
| Ctrl+D | 创建副本 |
| Delete | 删除 |
| Esc | 取消拖拽 / 清除选择 |
| F | 整页适配 |
| 0 | 100% 缩放 |
| Ctrl+滚轮 | 缩放 |
| 中键拖动 | 平移画布 |
| Ctrl+Enter | 应用模板 JSON（文本框内） |

---

## hiprint 模板导入

如果你已经在用 hiprint 设计器（vue-plugin-hiprint / Vue-Print-Designer），CrossPrint 可以直接吃掉它的导出：

```csharp
using CrossPrint.Core.Hiprint;

// 严格 JSON、getJson() 输出片段、JS 对象字面量、整个 .js 模块源码都接受
var result = HiprintTemplateImporter.Import(File.ReadAllText("hiprint-template.js"));

var template = result.Template;                  // 直接交给设计器 / 渲染 / 打印
foreach (var issue in result.Issues) Console.WriteLine(issue);
```

- **几何**：hiprint 的 pt 自动换算成 mm，无需手动改。
- **元素**：文本、横竖线、矩形椭圆、图片、二维码（纠错级/码下标题）、十种一维码、表格（二维表头、隐藏列、行序号、尾行汇总）全部支持。
- **脚本**：`formatter` / `styler` 原样保留，渲染期由 Jint 沙箱执行。
- **降级留痕**：水印、页码、HTML、跨页长文本、单元格内嵌码这些 hiprint 特性没有等价能力，导入器会按最接近的静态结果导入，并在 `Issues` 里列出，不静默丢弃。

---

## 模板格式与数据绑定

### 单位

页面与几何一律**毫米**（`WidthMm` / `Xmm`…），字体一律**磅**（`SizePt`）。没有像素换算陷阱。

### 数据绑定

文本字段支持 `{{路径}}` 占位符：

```json
{ "text": "{{order.customer.name}} 先生/女士您好" }
{ "table": { "columns": [{ "field": "price" }] } }
```

表格按路径自动展开数据行。数据源可以是：

- `IDictionary<string, object?>` / `IDictionary`
- 任意 POCO（属性/字段，大小写不敏感）
- `System.Text.Json` 的 `JsonElement` / `JsonNode`

绑定失败**不抛异常**，值替换为空串，并在 `RenderReport` 里记一条告警。

### 脚本（formatter / styler）

hiprint 同款语义。文本元素和表格列都支持：

```json
{
  "typeKey": "table",
  "table": {
    "columns": [
      {
        "field": "price",
        "title": "单价",
        "formatter": "function(value){ return '¥' + (value || 0).toFixed(2); }",
        "styler": "function(value){ return value > 1000 ? { color:'#c0392b', fontWeight:'bold' } : ''; }"
      }
    ]
  }
}
```

- **formatter** 返回字符串替换显示文本；返回 `null` / `undefined` 保留原值。
- **styler** 返回 JS 对象（camelCase）或 CSS 字符串（kebab-case）都认。
- **安全约束**：Jint 沙箱不开放 CLR 访问；单次 250ms 超时、10 万语句、64 层递归上限。**永远不会让渲染中断**。
- **设计器可见**：设计态脚本照常执行，画布上直接看效果。

字段表与完整示例见 [`docs/template-schema.md`](docs/template-schema.md)。

---

## 宿主支持

| 宿主 | 最低框架 | 样例 |
|---|---|---|
| Avalonia 11.3 | .NET 8 | Windows / Linux / macOS + Browser/WASM |
| WPF | .NET 8（Windows） | Windows |
| WinForms | .NET 8（Windows） | Windows |
| Eto.Forms 2.10 | .NET 8 | Windows（可移植，Linux/macOS 需自行打包） |
| WinUI 3（WindowsAppSDK 1.6） | .NET 8（Windows） | Windows |
| Uno 5.6（Skia Desktop） | .NET 8 | Windows |
| .NET MAUI 8 | .NET 8 | Windows |

内核层（CrossPrint.Core / Engine / Designer / Printing）**不依赖任何 UI 框架**，可在 Windows / Linux / macOS 上构建与测试。

---

## 已知限制

- **二维码静区**按 4 模块绘制，个别扫码器的宽松/严格口径可能不同。
- **hiprint 导入**不执行导入期 JavaScript（水印、页码、HTML、跨页长文本没有等价能力）；多面板模板一次导入一联。
- **撤销/重做栈**内核每次变更都产出可 `Revert` 的命令，宿主可自行接栈。
- **Uno 6.x** 要求 SkiaSharp 3.x 和 net9，本仓库跟随 SkiaSharp 2.88.9 暂不跟进。
- **WASM 版**文件对话框不可用（浏览器沙箱），hiprint 导入走模板 JSON 面板粘贴。

---

## 在线示例的部署

Avalonia Browser 样例编译为 WebAssembly，产物是纯静态文件（~40MB，首次加载），可直接部署到 GitHub Pages / Cloudflare Pages / Vercel 等任意静态托管：

```bash
dotnet workload install wasm-tools
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# 产物：samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

子路径部署（如 GitHub Pages 的 `/OODD.CrossPrint/`）已在 `index.html` 里设好 `<base href="./">`。仓库自带 GitHub Actions workflow（[`.github/workflows/pages-demo.yml`](.github/workflows/pages-demo.yml)），push 到 main 即自动发布。

---

## 文档

- [`docs/template-schema.md`](docs/template-schema.md) — 模板 JSON 完整字段表与 hiprint 导入映射
- [`docs/rendering-spec.md`](docs/rendering-spec.md) — 逐元素渲染规格
- [`docs/webassembly-demo.md`](docs/webassembly-demo.md) — WASM 在线示例构建与部署
- [`docs/third-party-licenses.md`](docs/third-party-licenses.md) — 第三方依赖与许可证清单
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — 贡献指南

## 许可

Apache License 2.0。排除 AGPL 与商业授权依赖。
