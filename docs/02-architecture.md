# CrossPrint 架构设计文档

> 版本：v1.0 基线
> 关联：[01-requirements.md](01-requirements.md)（需求）、[03-development.md](03-development.md)（工程约定）
> 本文是"为什么这样分层"的唯一权威解释；改架构先改本文。

---

## 1. 架构总览

### 1.1 分层与依赖方向

```
                        ┌─────────────────────────────────────────┐
  宿主层 (Hosts)        │ .Avalonia  .WinForms  .Wpf  .Eto  .WinUI │  只做画布桥接与输入翻译
                        │ .Uno      （全部只依赖 Designer）        │  不含任何元素绘制逻辑
                        └───────────────────┬─────────────────────┘
                                            │
  设计器内核 (无 UI 依赖)  ┌──────────────────┴─────────────────────┐
                        │ CrossPrint.Designer                    │  EditorKernel 状态机
                        │  EditorKernel / EditorRenderer /       │  + 编辑态叠加绘制
                        │  ElementProperties / 命中测试 / mm 坐标 │
                        └───────────────────┬─────────────────────┘
                                            │
  渲染引擎 (无 UI 依赖)   ┌──────────────────┴─────────────────────┐
                        │ CrossPrint.Engine                      │  TemplateRenderer
                        │  IElementRenderer×8 / DataBinder /     │  PdfExporter/PngExporter
                        │  PdfExporter / PngExporter / FontResolver│ BatchPdfExporter
                        └───────────────────┬─────────────────────┘
                                            │
  模型与序列化 (零依赖)   ┌──────────────────┴─────────────────────┐
                        │ CrossPrint.Core                        │  PrintTemplate/元素 POCO
                        │  JSON 源生成 / 校验 / PaperType / 单位   │  SkiaSharp 仅此层进入
                        │  Hiprint 导入（JS 对象归一化 → 模型）    │
                        └─────────────────────────────────────────┘

  旁挂：CrossPrint.Printing（依赖 Engine；PDF 文件目标 v0.1，Spooler/lp v0.2）
```

**铁律**（违反即架构走偏，停下重议）：

1. `CrossPrint.Core` / `Engine` / `Designer` 三个包**不引用任何宿主 UI 框架**。
2. SkiaSharp 允许出现在 Core（图片解码工具）、Engine、Designer、Hosts；不允许出现在测试之外的地方做"第二套渲染"。
3. 宿主适配包**只做三件事**：把宿主画布变成 `SKCanvas`、把宿主输入事件变成内核命令、把宿主失效请求接到 `Invalidate`。出现第四件事（如自绘选中框、自己算缩放）就是内核职责错位，回内核修。
4. 设计器画布 = `Engine 渲染` + `Designer 叠加层`，导出 PDF = `Engine 渲染`（无叠加层）。两者共用同一 `TemplateRenderer` 调用栈——**这是"所见即所印"的实现定义**。

### 1.2 包清单与目标框架

| 包 | TFM | 关键依赖 | 职责 |
|---|---|---|---|
| `OODD.CrossPrint.Core` | net8.0 | SkiaSharp 2.88.9（仅图片像素工具)；System.Text.Json | 模板模型、JSON 读写校验、纸型、单位换算、`RenderIssue` |
| `OODD.CrossPrint.Engine` | net8.0 | Core、SkiaSharp 2.88.9、ZXing.Net 0.16.11（纯编码器，零传递依赖） | 元素渲染器、`TemplateRenderer`、数据绑定、PDF/PNG 导出、字体解析 |
| `OODD.CrossPrint.Designer` | net8.0 | Engine | 编辑状态机、命中测试、手柄、吸附、网格、属性描述符、`EditorRenderer` |
| `CrossPrint.Hosts.Avalonia` | net8.0 | Avalonia 11.3.6 + `Avalonia.Skia`（`ICustomDrawOperation` → `ISkiaSharpApiLeaseFeature`） | `DesignerControl` / `PreviewControl` + 输入桥 |
| `CrossPrint.Hosts.WinForms` | net8.0-windows | SkiaSharp.Views.Desktop.Common 2.88.9（`SKPaintSurfaceEventArgs`；仓内 `SkiaCanvasControl` 以 `Bitmap.LockBits`+`SKSurface` 等价实现） | `DesignerControl` / `PreviewControl` |
| `CrossPrint.Hosts.Wpf` | net8.0-windows | SkiaSharp.Views.WPF 2.88.9（`SKElement`） | `DesignerControl` / `PreviewControl` |
| `CrossPrint.Hosts.Eto` | net8.0 | Eto.Forms 2.10.0（`Eto.Drawing.Bitmap` + `Lock()` → `BitmapData` 直写） | `DesignerControl` / `PreviewControl`（`Drawable`） |
| `CrossPrint.Hosts.WinUi` | net8.0-windows10.0.19041.0 | SkiaSharp.Views.WinUI 2.88.9（实测只导出 `SKXamlCanvas`，无 `SKSwapChainPanel`） | `DesignerControl` / `PreviewControl` |
| `CrossPrint.Hosts.Uno` | net8.0-desktop（Uno 5.6 单投影） | `SkiaSharp.Views.Uno.WinUI` 2.88.9（`SKSwapChainPanel` 直绘；Uno 5.6 也提供 `SKXamlCanvas`） | `DesignerControl` / `PreviewControl` |
| `CrossPrint.Printing` | net8.0 / net8.0-windows | Engine | `IPrintTarget`；v0.1 仅 `PdfFilePrintTarget` |
| Samples ×6 | 对应宿主 | 对应 Host 包 | 设计器 + 预览两场景 |
| tests | net8.0 | xUnit、FluentAssertions | 见 §8 |

TFM 说明：Core/Engine/Designer 刻意不 `-windows` 限定，保证 Linux/macOS 可测；Hosts 按包能力实测（探测记录见 04-task-plan §7）。

## 2. 核心数据模型

### 2.1 对象图

```
PrintTemplate
├── SchemaVersion : string            // "0.1"，迁移钩子
├── Paper : PaperSpec                 // 预设名或自定义 w/h mm；Orientation
├── Margins : ThicknessMm             // 仅安全区/物理边距参考，不参与元素坐标
└── Elements : List<PaperElement>     // 顺序 = 同 zIndex 的绘制次序

PaperElement（抽象基类，公共字段）
├── Id : string                       // 模板内唯一，内核维护
├── Type : string                     // 判别符："text" 等，序列化用
├── X, Y, Width, Height : double      // mm，相对页面左上角
├── ZIndex : int
└── 子类专属 payload（TextOptions / LineOptions / ... / TableOptions）
```

设计要点：

- **子类继承而非组合 payload**：C# 侧写起来自然（`el.Text.Content`）；JSON 侧由自定义转换器把专属字段收敛到 `"text": {...}` 命名空间下，兼顾 hiprint 风格的清晰分组。
- `PaperElement` 可变（设计器直接编辑模型，不另设平行 DTO 层）；`Clone()` 深拷贝供撤销栈与批量绑定复用。
- 元素 id 由 `EditorKernel.NextElementId()` 生成（`el-N` 递增 + 去重），删除不回收。

### 2.2 JSON 多态方案（源生成器约束下的选择）

STJ 内置 `[JsonPolymorphic]/[JsonDerivedType]` 与本需求有 3 处不匹配：判别符需固定 `"type"`、专属字段需嵌在 `"text":{}` 等子对象里、未知 type 需可控降级。因此：

- `TemplateJson` 提供 `Serialize(template)` / `Deserialize(json)`；
- 手写顶层 `JsonConverter<PaperElement>`（读 `type` 判别符 → `JsonElement` 定位子对象 → `jsonElement.Deserialize(具体类型, options)`，全部走 `JsonSerializerContext` 源生成，无反射）；
- 枚举一律字符串（源生成上下文注册 `JsonStringEnumConverter`）；
- 校验分两档：结构性缺失（无 id/type/几何）→ `TemplateValidationException`；未知 type → `UnknownElementTypeException`，`TemplateJsonOptions.AllowUnknownElementsSkip=true` 时降级为跳过 + `TemplateLoadResult.Warnings`。

### 2.3 单位与坐标约定（全文唯一真源，渲染侧照此实现）

| 量 | 模板存储 | 换算 |
|---|---|---|
| 几何 | mm（double） | `px = mm / 25.4 * dpi` |
| 字体 | pt | `px = pt / 72 * dpi` |
| 画布原点 | 页面左上角，y 向下（Skia 默认） | 设计器缩放/平移是视口变换，不改数据 |
| margin | mm | 仅绘制安全区提示；元素坐标永远相对页角 |
| 连续纸（小票） | heightMm = 0 表示按内容自适应 | 引擎按最底部元素 + 余量定高 |

`RenderOptions`：`Dpi`（预览 96 / PDF 由导出器强制 72 / 导出可配；必须是有限正数，0/NaN 在构造选项时就拒）、`PageBackgroundColor`（默认白）、`PdfRasterDpi`（PDF MediaBox 量化网格，默认 2540 = 0.01mm）、`PdfTextAsOutlines`（不嵌字体、改画轮廓）、`AllowTablePagination`（表格溢出自动续页）。

**没有** `Zoom`/`Scale` 这类视口字段：设计器把缩放折进 `Dpi`（`Viewport.EffectiveRenderDpi = Dpi × Zoom`）再传给引擎，渲染器只认一个密度。早先的 `RenderOptions.Zoom`（以及本文旧版写的 `Scale`）渲染器从未读过，属死旋钮，已按"禁止假功能"删除。

### 2.4 hiprint 导入（Core 层，不依赖 UI）

导入分两步，都放在 `CrossPrint.Core.Hiprint`，六个宿主与 Web 侧共用：

1. `HiprintJsonNormalizer`：把"不是严格 JSON"的输入（JS 对象字面量、单引号、尾逗号、注释、整个 `export const json` 模块、`getJson()` 片段）归一化成 `JsonDocument`。做法是逐个顶层对象枚举，字符串与反引号模板串整体跳过——**不执行任何 JS**。
2. `HiprintTemplateImporter`：按 hiprint 的元素配置映射到模型（元素 pt→mm、面板 mm、`panelPageRule`→连续纸、`paperHeader/Footer`→上下留白、`field`→`{{field}}`、二维表头→`HeaderRows`、`tableSummary`→`TableColumn.Summary` 等），返回 `HiprintImportResult{ Template, Issues, Panels }`。

- 一个 hiprint 模板可含多个面板（多联单），一次导入一个 `PanelIndex`；`Panels` 概要供宿主做选择。
- **降级不丢弃**：JS 函数回调、水印、页码、HTML、跨页长文本等无等价能力，逐条写进 `Issues`（`Severity/Code/Message/ElementId/SourcePath`），单元素失败不影响其余元素。
- **设计数据**：元素上的 `field + testData` 收进模板的 `DesignData`（路径→样例值），设计器/预览缺宿主数据时用它兜底，条码/二维码不会绑成空串。
- **表格兼容**：旧类型 `tableCustom` 与 `table` 等价导入（Info）；`columns` 的对象映射与单个扁平列对象都认；缺列定义时保留表格框并占 1 列（Warning），不再整张消失。
- 导入器只产出模型，不渲染；渲染仍走同一条 `TemplateRenderer`，因此导入、设计、预览、导出、打印天然一致。

## 3. 渲染引擎（Engine）

### 3.1 调用形态

```csharp
public sealed class TemplateRenderer
{
    public RenderReport Render(SKCanvas canvas, PrintTemplate template, RenderOptions options);
    // 内部：canvas.Save() → Scale(dpi/96 与 mm→px 统一为一次 CanvasScale) → 逐元素分派 → Restore
}

public interface IElementRenderer
{
    string ElementTypeKey { get; }                      // "text"
    void Render(SKCanvas canvas, PaperElement element, RenderContext ctx); // ctx 含 dpi/字体解析器/报告
}
```

- 分派：`ElementRendererRegistry`（`ElementTypeKey → IElementRenderer`），内置 8 个渲染器在模块初始化注册；第三方 `Register()` 可覆盖（自定义/实验渲染器）。
- **绘制单位统一**：引擎入口对 canvas 施加一次 `Scale(mm→px)` 后，各 `IElementRenderer` 内部一律用 mm 数值绘制——消除各渲染器重复换算、保证 FR-ENG-05 一致性由结构保证而非测试兜底。字体例外（pt→mm 换算在 `FontResolver` 内完成）。

### 3.2 输出目标

| 目标 | 实现 | 备注 |
|---|---|---|
| PDF | `SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata{ RasterDpi = rasterDpi })`，页面尺寸 `BeginPage(widthPt, heightPt)`，`pt = mm / 25.4 * 72`（MediaBox 即点数，见 A4c） | `PdfExporter.Export(template, stream)`；矢量内容分辨率无关，`RasterDpi` 只影响内嵌位图采样 |
| PNG/位图 | `SKSurface.Create(new SKImageInfo(wPx, hPx, ColorType))`，ColorType 用 `SKImageInfo.PlatformColorType` → 渲染 → `SKImage.Encode` | `PngExporter`，任意 DPI |
| 宿主画布 | 宿主给 `SKCanvas`，`TemplateRenderer` 直绘 | 零拷贝路径见 §6 |
| 批量 | `BatchPdfExporter.Export(template, IEnumerable<IRenderDataSource>, stream)` 循环 `document.NewPage` | 一数据一页 |

### 3.3 各元素渲染规则

- **公共外观（所有元素）**：绘制次序固定「背景 → 内容 → 边框」，整组包在一次 `SaveLayer`（`opacity`）与一次绕元素中心旋转（`rotationDegrees`，顺时针为正，与 CSS 同向）里；边框按 `borderSides` 逐边判是否画，虚线 `borderDashPatternMm`。`opacity` 越界/非有限值夹到 0..1。这保证"旋转的是整块元素"而不是内容单独转、框不转。
- **text**：`SKFont`（Family 经 `IFontResolver` 解析为 `SKTypeface`）；排版区先扣四向 `padding*Mm`；`wrap=true` 按 `SKFont.MeasureText` 贪心断词逐行排版，`wrap=false` 超宽时按 `ellipsis` 决定省略号截断或硬裁；行高 = `fontSizePt * LineSpacing（默认1.2）`；`letterSpacingPt` 按字符间距绘制；`Justify` 把除末行外的剩余宽度均摊到字间；对齐 3×3；下划线/删除线按整行绘制。字体缺失回退 `SKFontManager.Default.MatchCharacter(首字符)`，仍失败→默认字体 + RenderIssue。
- **line/rectangle/ellipse**：描边宽度 mm 化；虚线 `dashPatternMm`；矩形圆角 `cornerRadiusMm` 夹取到 `min(w,h)/2`。
- **image**：Base64→`SKData`→`SKBitmap.Decode`（解码在 Core 的 `ImagePayload` 工具里完成，带 LRU 缓存 key=contentHash）；fit 四模式；`cornerRadiusMm` 用圆角路径裁切；失败画 1px 红虚线框 + "IMG ERR"，不抛。
- **qrcode**：ZXing `QRCodeWriter` → `BitMatrix` → **逐模块 `canvas.DrawRect`**（不经 PNG 编解码，矢量级锐利）；纠错级 L/M/Q/H 经 `EncodeHintType.ERROR_CORRECTION` 传入（探针 §20：解码元数据能读回 `"L"/"M"/"Q"/"H"`，映射写错不会让渲染失败，只有解码器能揭穿）；静区 `quietZoneMm`（默认 1mm，照用户给的毫米值画，不自动补到规范的 4 模块——见登记簿）；`showCaption` 时先按元素宽折行算标题占高，码区取 `min(元素宽, 元素高−标题高)` 的正方形居中，标题画在下带；内容超长编码失败 → 占位框 + issue。**模块填充必须关抗锯齿**：探针 §21 实测 40mm 码在 300 DPI 开 AA 后 `QRCodeReader`/`MultiFormatReader` 双双返回 null，同一张图关掉 AA 立刻解回。
- **barcode**：ZXing 的 `Code128Writer`/`EAN13Writer`/`EAN8Writer`/`UPCEWriter`/`ITFWriter`/`CodaBarWriter`/`Code39Writer`/`Code93Writer`/`MSIWriter` 的 `encode(content)` → **`bool[]` 模块序列**（不是 `BitMatrix`，别按二维网格索引）→ 连续等色模块合并成矩形绘竖条，条宽 = (元素宽 − 2×静区) ÷ 模块数；UPC-A 按"首位补 0 的 EAN-13"编码（ZXing 的 `UPCAWriter` 是包装器，不暴露裸模块序列）；`showText` 时从元素高里**扣掉一行行高（`fontSizePt × 1.2` 换算成 mm）**给可读文本，条只占剩下的部分（探针实测：无文本时条铺满元素高，有文本时条底边上移约一行）；内容不合法（ZXing 抛 `ArgumentException`，如 `Ean13` 位数错）→ 占位框 + issue。EAN-13/EAN-8/UPC-A/UPC-E 的起止/中间护栏按 ISO/IEC 15420 再向下延长 5 个模块（上限条高 25%），其余码制所有条同高。条与模块同样**关抗锯齿**。十种码制都有 ZXing 解回原内容的往返测试。
- **table**：列宽按 `widthMm`（-1=均分剩余）；行集合优先 `TableOptions.Field`，为空时取列字段公共首段（首段不一致就不硬配对，数据行不输出）；`Sequence` 列输出 1 起行号；`Summary` 列（count/sum/avg/min/max/text）在正文后追加汇总行；`HeaderRows` 非空时按 colspan/rowspan 渲染多行表头（跨行/跨列单元格内部不画分隔线，网格线逐列分段判定，起点对齐元素 `yMm`），为空时用列标题渲染单行表头；行高 `rowHeightMm` 或按单元格文本自适应（取最高）；斑马纹 `stripeColor`；单元格超宽按列换行、超裁切。**v0.1 分页不做**（FR-BND-05 在 P2），超页高内容裁切并在 `RenderReport` 给出"表格溢出 N mm"提示。

### 3.4 降级与报告

每个渲染器 catch 元素级异常 → `RenderReport.Add(elementId, RenderIssueKind, message)`，继续下一元素。**引擎对宿主承诺：`Render` 不抛，除非模板对象图为 null 这类调用方编程错误。**

## 4. 设计器内核（Designer）

### 4.1 内核是唯一交互大脑

```csharp
public sealed class EditorKernel
{
    public PrintTemplate Template { get; }
    public Selection Selection { get; }             // v0.1 单选，接口按多选设计（Ids/PrimaryId，存 id 不存引用）
    public Viewport Viewport { get; }               // Zoom(0.05–4.0) / Pan(96 基准 px) / Dpi / EffectiveRenderDpi
    public IReadOnlyList<PaperElement> SelectedElements { get; }  // 叠加层与面板的活引用视图
    public double PageHeightMm { get; }             // 连续纸走引擎自适应判据，与导出同一张页
    public InteractionState State { get; } public ResizeHandle ActiveHandle { get; }
    public SKRect? PlacingGhostMm { get; }
    // 输入（坐标=宿主逻辑像素 + 该事件 DPI，内核换算；修饰键由宿主同步 ShiftPressed）
    void OnPointerPressed(PointerPoint p); void OnPointerMoved(PointerPoint p); void OnPointerReleased(PointerPoint p);
    bool OnMouseWheel(double delta, PointerPoint p); bool OnKeyDown(KeyInfo k);
    void AddElementAtCenter(string typeKey); bool BeginPlacing(string typeKey, PointerPoint start);
    void BringForward(); void SendBackward(); void BringToFront(); void SendToBack();
    void DeleteSelection(); void RunCommand(EditorCommand command);
    PaperElement? SelectAt(PointerPoint p);                      // 右键：命中即单选，空白清选
    void DuplicateSelection();                                   // 深拷贝 + 新 id + 偏移 5mm + 选中副本
    void CenterSelectionHorizontally(); void CenterSelectionVertically();
    void SetPaperPreset(string preset); void SetCustomPaper(double widthMm, double heightMm);
    void SetPaperOrientation(PaperOrientation orientation); void RotatePaper();
    ElementProperties? SelectedProperties { get; }               // 属性面板通道（单选才有）
    void SetSelectedProperty(string key, object? value);         // 面板提交一格 → 一条命令
    event Action? ViewChanged; event Action? TemplateChanged;    // 宿主据此 Invalidate
    event Action<EditorCommand>? CommandExecuted;                // v0.1：撤销能力交给宿主
}
```

内部状态机（`InteractionState`）：`Idle → DraggingMove | DraggingResize(handle) | Panning`；`PointerPressed` 顺序：命中手柄→缩放；命中元素→选中+移动；命中空白→清选中（v0.1 不做框选）。

- 纸张与画布菜单的命令化：预设/自定义/纵横都先克隆 `PaperSpec` 再包成 `PaperCommand`（前后各一份 spec），提交后自动 `FitToPage`；连续纸（`PageHeightMm <= 0`）强制纵向，旋转直接拒绝。`DuplicateElementsCommand` 按元素实例记录副本，Revert 移除副本并恢复选中。右键命中由 `SelectAt` 统一完成，宿主只负责把菜单项接到这些方法上——菜单本身是宿主皮肤，内核不引用任何 UI 框架。
- 坐标换算集中在内核：`宿主设备px →(×96/dpi) 视口px →(−Pan, ÷zoom) 页面px →(÷96/25.4) mm`（`Viewport.ToMm`）；反向 `ToHostPx/ToHostRect` 供叠加层与命中框用。宿主不碰 mm。
- **渲染（两遍夹一次）**：宿主只调 `EditorCanvas.Draw(canvas, kernel, overlay, widthHostPx, heightHostPx, dpi, data, …)`——它按 `EditorRenderer.RenderBackdrop(...)` → `EditorRenderer.PushCamera(canvas, kernel)` + `TemplateRenderer.Render(..., Dpi = Viewport.EffectiveRenderDpi)` + `canvas.Restore()` → `EditorRenderer.RenderForeground(...)` 编排，并顺手写回 `kernel.ViewWidthHostPx/ViewHeightHostPx/Viewport.Dpi`。顺序写进了一个入口，六个宿主才不可能各自漂移（`EditorCanvasTests` 逐像素钉住底/文档/顶三层的存在，且去掉任一遍立即变红）。相机只平移（`Pan × dpi/96`），缩放全部由 `EffectiveRenderDpi = Dpi × Zoom` 承担——两条链各管一件事，叠加层在设备 px 空间画的框与引擎内容就天然重合（`EditorRendererTests.引擎内容与视口换算落在同一处` 逐像素钉住，96/144 两档 DPI）。叠加色与页框/网格/标尺/手柄/幽灵都不进模板。
- 属性通道：`ElementProperties.For(element)` 给出 `PropertyDescriptor` 列表（键/显示名/类型/候选项/取值提示/分组 `Group`），宿主属性面板据此数据驱动生成表单并按组插入标题；提交走 `kernel.SetSelectedProperty(key, value)`——先经描述符校验并规范化（`"12.5"`→`12.5`、枚举名→枚举值），再包成 `PropertyChangeCommand` 广播，因此面板改一格也是可撤销的一条命令。八种元素共享 13 个公共字段：`id`、四个几何量、`zIndex`、`backgroundColor`、`borderColor`/`borderWidthMm`/`borderSides`/`borderDashPatternMm`、`rotationDegrees`、`opacity`；表格列按 `table.columns[i].*`、多行表头按 `table.headerRows[i].*` 展开成快照描述符。背景/边框/不透明度由 `TemplateRenderer` 在每个元素的公共路径上绘制（背景→内容→边框，整组包在一次 `SaveLayer` 里做 alpha），元素渲染器不各写一遍。

### 4.2 撤销/重做预留（P1）

内核所有变更走 `EditorCommand`（`Apply/Revert`）；v0.1 命令已按此结构写（移动/缩放/删除/属性变更都是内部命令对象），只是不暴露栈——P1 加 `UndoStack` 即插即用，不返工。

## 5. 宿主适配层

每宿主适配包的四件套（模板化）：

```
PrintCanvas : 宿主控件
 ├─ OnRender/Draw → EditorCanvas.Draw(...)  // 底 → 相机+文档 → 顶，唯一编排入口
 ├─ 输入事件 → PointerPoint/KeyInfo → kernel 命令
 ├─ kernel.ViewChanged/TemplateChanged → Invalidate()
 └─ DPI 来源：宿主逻辑 DPI（Avalonia RenderScaling / WPF DpiScale / WinForms DeviceDpi…）
```

| 宿主 | 画布路线 | 输入 | 已实测约束 |
|---|---|---|---|
| Avalonia 11 | `Control.OnRender(dc)` → `dc.Custom(new SkiaDrawOperation(...))`；op 内 `ImmediateDrawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>()` → `Lease().SkCanvas` 直绘，**零拷贝**（`ISkiaSharpApiLease : IDisposable`） | `PointerPressed/…` + `OnKeyDown` | Avalonia 11.3.6 还原 OK，且**其自身引用 SkiaSharp 2.88**，与本仓版本锁一致（Avalonia 12 才升到 3.x）；无需 SkiaSharp.Views 包。**租到的 SKCanvas 坐标是 DIP**（`DrawingContextImpl` 把 `Dpi/96` 藏进后置矩阵），适配层先 `Scale(1/scaling)` 回到设备 px 再交给内核，与其它宿主的 `PointerPoint.Dpi` 契约完全一致。**自定义绘制操作必须把 `ICustomDrawOperation.HitTest` 实现为"点在 `Bounds` 内返回 true"**：Avalonia 的 `CompositionDrawListVisual.HitTest` 直接把它当作该视觉可命中的唯一依据，返回 false 会让控件完全收不到指针事件（画布正常显示但点不中任何元素），元素级命中仍由内核做二级判定 |
| WinForms | 自实现 `SkiaCanvasControl`（与官方 `SKControl` 同一套 `Bitmap.LockBits` + `SKSurface.Create(info, scan0, stride)`）→ `PaintSurface` 直绘 | `MouseDown/Move/Up/Wheel/KeyDown`；`Capture` 手工 | Desktop.Common 2.88.9 **不含** `SKControl`；官方 `SkiaSharp.Views.WindowsForms` 2.88.9 会强拉 OpenTK 3.1 + GLControl（仅为 `SKGLControl`），故仓内保留 60 行等价基类，零额外原生依赖 |
| WPF | `SkiaSharp.Views.WPF.SKElement.PaintSurface` 直绘 | `Mouse* `（WPF 自动捕获） | Views.WPF 2.88.9 OK |
| Eto.Forms | `Drawable` 的 `Graphics` → `Eto.Drawing.Bitmap(w,h,Format32bppRgba)` + `Lock()` 直写 `BitmapData`（按 `ScanWidth`/`Flipped` 逐行拷 `SKBitmap`）→ 每帧 `DrawImage`；输入 `MouseDown/Move/Up/Wheel`、`KeyDown/KeyUp`，拖拽态靠 `LostFocus` → Esc 回滚 | macOS/Cocoa 下 `BitmapData` 行跨距/翻转待真机验证（列入真机清单） | Eto.Forms 2.10.0 OK（2.10 无 `WriteableBitmap` 类型，`Bitmap+Lock` 才是公开写入面；`Format32bppRgba` 的 32 位值语义是 A 在最高位，小端内存即 B,G,R,A，与 Skia `Bgra8888` 一致）；`MouseEventArgs.Delta` 是 `SizeF`，滚轮取 `Delta.Height` |
| WinUI 3 | `SKXamlCanvas.PaintSurface` 直绘（`e.Info` 已是设备 px）；DPI 取 `XamlRoot.RasterizationScale × 96` | `Pointer*` + `KeyDown`（焦点在 `UserControl` 上，`SKXamlCanvas` 是 `Panel` 拿不到焦点）；`PointerCaptureLost` → Esc 回滚 | Views.WinUI 2.88.9 **只导出 `SKXamlCanvas`**（探测确认无 `SKSwapChainPanel`）；仅 win TFM；需 WindowsAppSDK 运行时 |
| Uno | `SkiaSharp.Views.Uno.WinUI` 2.88.9 的 `SKSwapChainPanel.PaintSurface` 直绘（该包同时提供 `SKXamlCanvas`，这里取交换链面板）；`net8.0-desktop` 单投影 | 与 WinUI 同构：`Pointer*` + `KeyDown`，`PointerCaptureLost` → Esc 回滚 | Uno.Sdk 5.6.54 实测**不需要 workload**（NuGet SDK）；`UnoSingleProject=true` 用于卸掉 Skia 运行时传递的 WPF 框架引用（否则 NETSDK1136）；Uno 6.7 要求 SkiaSharp 3.119 + net9，与仓库 2.88.9 锁冲突，暂不跟进 |

**DPI 纪律**：内核收到的 `PointerPoint.Dpi` 一律为宿主逻辑 DPI（Avalonia/WPF/Uno 用 96×scaling 还原后的值，WinForms 用 DeviceDpi，Eto/WinUI 用缩放因子×96）。各适配层只允许做一次换算，测试基线为"缩放到 150% 屏上拖动元素落点误差 ≤0.5mm"。

**右键纪律**：六个宿主对右键一律**只取焦点**——不 `Capture`、不进内核、不置 Handled。上下文菜单是宿主皮肤，命中与选中由宿主的菜单打开路径调用 `Kernel.SelectAt` 完成。若右键也进捕获/按压态，菜单弹出或焦点转移会触发各宿主的"捕获丢失 → Esc 回滚"，把刚右键选中的元素清掉，菜单里的副本/层次操作随即失去作用对象。

## 6. 打印抽象（Printing）

```csharp
public interface IPrintTarget
{
    string Name { get; }
    Task<PrintResult> PrintAsync(PrintDocument document, PrintSettings settings, CancellationToken cancellationToken = default);
}
```

```csharp
// 印什么（内容）与往哪儿写（通道）分家：份数属于文档，不属于通道
public sealed record PrintDocument { PrintTemplate Template; object? Data; int Copies /* ≥1，构造期校验 */ }
public sealed record PrintSettings { string OutputPath; RenderOptions? Options; bool Overwrite /* 默认 true */;
                                     IFontResolver? FontResolver; IImageResolver? Images; TemplateRenderer? Renderer }
public sealed record PrintResult   // 不变式在构造期强制，属性是 private init ⇒ 宿主连 with 都写不出来
{
    bool Success; int PrintedPages; string? Message; Exception? Error;
    // Success=true ⇒ Error==null && PrintedPages>=1 ；Success=false ⇒ PrintedPages==0 && (Message|Error 非空)
    static PrintResult Ok(int printedPages, string? message = null);
    static PrintResult Failed(string message, Exception? error = null);
}
```

```csharp
public sealed class PdfFilePrintTarget : IPrintTarget
{
    Task<PrintResult> PrintAsync(PrintDocument d, PrintSettings s, CancellationToken ct = default);
    Task<PrintResult> PrintAsync(PrintDocument d, PrintSettings s, RenderReport? issues, CancellationToken ct);
    Task<PrintResult> PrintBatchAsync(IReadOnlyList<PrintDocument> docs, PrintSettings s, CancellationToken ct = default);
    Task<PrintResult> PrintBatchAsync(IReadOnlyList<PrintDocument> docs, PrintSettings s, RenderReport? issues, CancellationToken ct);
}
```

- v0.1：`PdfFilePrintTarget`（FR-PRT-01 "PDF 导出即打印"）。单文档一份文件；批量 = **一份**多页文件，一次 `BatchPdfExporter.Export`（一个 `SKDocument` 才有一份共享字体池）。100 条数据 → 100 页实测（Release，本机，多次跑取区间）：西文（Arial）614,397B / 56–65ms（每页 ≤0.65ms，页增量 523B），`/FontFile2` 出现 1 次；中文（Microsoft YaHei）12,511,991B / 0.86–1.06s，与单页 12,459,964B / 0.85–1.01s 同价——**整份 CJK 字库是每文档一次，不是每页一次**，这就是 100 页面单在 M6 成立的前提（见 `tests/CrossPrint.Printing.Tests`，数字由测试当场写进 `%TEMP%/crossprint-printing-tests/bench.log`）。NFR-01 的 200ms/页预算在西文版面上余量两个数量级；CJK 版面要么走 `PdfTextAsOutlines`，要么认下这份 12MB 字库。
- 失败纪律：写盘走"同目录临时名 `{名}.crossprint-{guid}.tmp{扩展名}` + `Flush(true)` + `File.Move(…, true)`"。直接把正式路径打开的写法在第 37 页抛异常时会留下一份"看着完整"的半截 PDF，归档系统当成功件收走；改名是同目录项替换，读者要么见旧件要么见新件。跨卷不行：那不是改名是复制。
- 取消纪律：Skia 的 PDF 导出没有协作式取消点，`BeginPage` 之后只能整份写完，所以取消检查放在展开页序、建目录、建临时文件之前（两次）。取消与 IO 故障一律折成 `Failed` 并把 `OperationCanceledException`/真异常登记进 `Error`，**不**抛出 `await`——批量循环里一个取消不该打断剩下的作业。`PrintResult` 的不变式保证宿主的"已印 N 页"日志不是假账。
- `PrintSettings` 里**没有**打印机名/双面/页码范围：v0.1 没有实现读它们，挂上去就是死旋钮（同仓先例：不起作用的 `AutoPageHeight` 被整条删掉）。也**没有**"每条一个文件"的路径模板：那会让每份都整嵌一次字库。
- v0.2：`WindowsGdiPrintTarget`（P/Invoke `StartDoc/StartPage/EndPage`，把 SKSurface 像素 `StretchDIBits` 给 DC；驱动无关性最好）、`CommandLineLpPrintTarget`（`lp file.pdf`）。接口 v0.1 冻结，实现不动内核；批量入口目前只在具体类上（`PrintBatchAsync`），要不要提进接口由 v0.2 的两个目标各自能否兑现来决定。

## 7. 关键决策记录（ADR 摘要）

| # | 决策 | 备选 | 理由 |
|---|---|---|---|
| A1 | 内核+薄适配，而非"每宿主一套 ViewModel" | PRD 原方案（Avalonia 专属 VM） | 需求已升级为多宿主；交互逻辑只写一遍，6 宿主手感一致 |
| A2 | SkiaSharp 锁 2.88.9 全仓集中管理 | 3.x/4.x（API 迁移大、`SKImage.FromPicture` 有崩溃前科、Views 包 TFM 分裂） | PRD 约束 + 本机实测兼容面最宽；`Directory.Packages.props` 钉死 |
| A3 | STJ 手写多态转换器 + 源生成 | `JsonPolymorphic` 内置属性（判别符/子对象布局不满足）、Newtonsoft（反射、AOT 差） | AOT 承诺 + JSON 形状即规范 |
| A4 | 条码/二维码逐模块绘制（BitMatrix→`DrawRect`），不走图片管线 | QRCode PNG→SKBitmap | 免编码依赖、矢量锐利、绕开 2.88.x 图片解码边角 |
| A4b | **实测后改选 ZXing.Net 0.16.11，弃用 QRCoder**（偏离 PRD 第 3 节技术栈表，理由如下） | QRCoder 1.8.1（PRD 指定）；自研编码器 | 实测：`QRCoder` 1.8.1 传递依赖 `System.Drawing.Common`（Linux 上抛 `PlatformNotSupportedException`，毁掉 NFR-03 跨平台承诺）；`QRCoder.Core` 2.0.0 又把 SkiaSharp 抬到 3.119.0，与全仓 2.88.9 锁冲突。`ZXing.Net` 0.16.11 net8.0 **零传递依赖**，`QRCodeWriter`/`Code128Writer`/`EAN13Writer` 纯托管出 `BitMatrix`，非法内容抛 `ArgumentException`（可捕获转 RenderIssue），Apache-2.0 合规 |
| A4c | PDF 用 72dpi 点单位建模，`RasterDpi` 元数据承载高分辨率意图（默认 **2540**） | PRD 6.2 的"按 300 DPI 创建页面" | 实测 2.88.9：`SKDocument.CreatePdf(stream, dpi)` 的 dpi 参数**不改变 MediaBox**（dpi=300 下 BeginPage(248,354) 仍得 MediaBox 247.92x354），MediaBox 恒等于 BeginPage 传入的点数。PDF 是矢量格式，文字/线条清晰度与 DPI 无关；把 dpi 当作页面尺度会引入无谓的换算歧义。故页面尺寸按 `mm/25.4*72` 点直接建模，嵌入位图的采样密度走 `SKDocumentPdfMetadata.RasterDpi`。`RasterDpi` 取 2540 是让页框落到 0.01mm 网格：实测 `MediaBox = round(mm×rasterDpi/25.4)×72/rasterDpi`（探针 §12，`ExporterTests.PDF页面毫米精度落在0_01mm网格上` 三档纸张验过） |
| A5 | 元素渲染入口统一施加 mm 缩放 | 各渲染器自行换算 | 一致性由结构保证；测试只需验一个换算点 |
| A6 | 表格进 v0.1 | PRD 推到 v0.3 | 技术上限内（纯排版循环）；hiprint 场景无表格不成立；砍它属于"按省事裁剪"，禁止 |
| A7 | margin 不参与元素坐标 | 内容区坐标 | 打印惯例（hiprint/Word 一致）；PRD 6.3 同款规定，写进 schema 文档 |
| A8 | `RenderToPdf` 无 DPI 参数，内部恒以 72（点）建模；`PdfRasterDpi` 默认 2540，只管位图采样与页框网格 | PRD 的"dpi 参数控制 PDF 清晰度" | 见 A4c：矢量内容不存在"DPI 不够则模糊"；把 300 当页面尺度反而会让 mm→页面尺寸算错 4 倍（A4 会印成 210mm×72/300）。验收标准"PDF 上量尺子等于 mm 值"在本方案下直接成立。文字同样是矢量：实测 50 元素 A4 的 PDF 里文本走 `Tj` 指令并内嵌 `/FontFile2`，不是贴上去的位图（`ExporterTests.PDF里的文本是矢量并内嵌字体`） |
| A9 | hiprint 导入放在 **Core**（归一化器 + 导入器），导入结果为原生 `PrintTemplate` | 宿主各写一套映射；在 Designer 层做；运行时执行 JS（ClearScript/Jint） | 模板语义只定义一次，六个宿主与 Web 侧共用；导入后走同一条渲染/导出链路，天然"所见即所印"。不执行 JS 是安全与可移植约束：`formatter` 等函数降级为静态值并进 `Issues`，绝不静默丢弃。 |

## 8. 可测试性设计

| 测试类型 | 位置 | 断言要点 |
|---|---|---|
| 序列化往返 | Core.Tests | JSON→模型→JSON 字段无损；未知字段忽略；缺必填异常消息含 id+字段 |
| 单位换算 | Core.Tests | mm/pt→px 精确 0.01 |
| 元素渲染一致性 | Engine.Tests | 提交的是**输入**样张 `docs/samples/kitchen-sink.template.json`，不是栅格基线。断言：96/300 DPI 下逐元素墨迹包围盒 mm 一致（含"留白带必须零非白像素"的反向对照）；不依赖环境 |
| 降级 | Engine.Tests | 坏 base64/缺字体/超长 QR → Render 不抛、issue 命中 |
| hiprint 导入 | Core.Tests | 4 份真实夹具（完整 JS 模块 / `getJson` 片段 / 隐藏列 / 多表格）+ 合成场景：多面板选择、连续纸、pt→mm、文本/图片/条码/二维码/多行表头映射、`tableCustom`、表格缺列占位、`columns` 对象映射·扁平列、`field+testData`→`DesignData`、问题清单、序列化往返；各机制均配"关掉就红"的对照（不枚举顶层对象、把反引号里的 `{}` 当对象边界、不补外层花括号、`tableCustom` 落进未知分支、扁平列返回空、缺列时丢表） |
| hiprint 渲染语义 | Engine.Tests | 旋转/分边边框/内边距/字间距/省略号/两端对齐/二维码标题/图片圆角/表格行集合·序号·汇总·多行表头合并逐条像素取证，每条都配"关掉机制必须不同"的反证 |
| 设计数据兜底 | Engine.Tests / Designer.Tests | 空内容码制画占位框（暗像素 0、非白像素 >0；反证：去掉守卫后 Code128 空串画出 146,792 暗像素）；宿主数据优先、`null` 不回落；`EditorCanvas` 合并设计数据后无宿主数据也出真条码（反证：去掉合并即退化成占位框） |
| hiprint 跨层回归 | Engine.Tests | 4 份真实夹具导入后整页渲染：无 `ElementSkipped`/`RendererMissing`，页面非白像素高于实测下限；同一模板的 JS 模块与 `getJson` 片段导入结果逐字节一致 |
| 码制可扫性 | Engine.Tests | 三档从弱到强：ZXing 解回原内容、逐模块比对 `BitMatrix`、条宽严格等于模块序列整数倍；十种码制全部往返（MSI 用 `MSIReader`，`MultiFormatReader` 不注册它）。码制绘制**必须关抗锯齿**——实测开 AA 后连 ZXing 自己的读码器都解不出（探针 §21） |
| 字体族口径 | Core.Tests | `FamilyExists` 同时认 `FontFamilies` 列表名与字体内部英文名（中文 Windows 列"微软雅黑"、按名匹到 "Microsoft YaHei"，探针 §22），否则 FontFallback 告警通道变噪音 |
| 批量字体代价 | Engine.Tests / Printing.Tests | 多页 PDF 的 `/FontFile2` 只出现 1 次，页增量按内容流算（实测 4 页 +2,049B）——100 页面单可行的前提；Printing.Tests 把同一取证推到 100 页（实测页增量 523B、`/FontFile2`=1） |
| 绑定/批量 | Engine.Tests / Printing.Tests | 100 条数据→100 页 PDF；缺字段空串。Printing.Tests 另加"逐页内容流两两不同"的字节级实证与半截文件/取消路径 |
| 内核状态机 | Designer.Tests | 命中/拖动/缩放的 mm 结果；min 1mm；zIndex 操作；命令可 Revert |
| 泄漏 | Engine.Tests | `SKDocument`/`SKSurface` 渲染后非存活（GC + 弱引用） |
| 许可 | CI 脚本 | `license-check.ps1` 扫 props 依赖图，禁清单外协议 |
| Trim | CI | `dotnet publish -p:PublishTrimmed` on Samples.Avalonia headless；实测本仓库程序集零 IL 警告，JSON 走源生成 `JsonTypeInfo`，POCO 反射兜底已就地抑制并降级为 `BindingMissingField`；剩余 IL2026/IL2104 来自 Avalonia.DesignerSupport |

GUI 冒烟清单（交用户真机复核，每宿主一份）：放置→拖动→缩放→属性刷新→Delete→Ctrl 滚轮缩放→保存→重开→导出 PDF→与画布叠图对比。

## 9. 扩展点汇总

1. `IElementRenderer` + `ElementTypeRegistry`：新增元素类型三步（子类、渲染器、注册），工具箱/属性面板/JSON 自动生效。
2. `IFontResolver`：宿主可接管字体源（内嵌字体文件/系统字体目录自定义）。
3. `IDataSource` 适配器：字典/POCO 元数据/JsonDocument；第三方可接 ORM。
4. `IPrintTarget`：任何"把渲染结果送去某处"都收口于此（打印机/PDF/图片/云打印网关）。
5. 叠加层绘制钩子 `EditorRenderer.Override`（P1）：宿主可注入自定义装饰（如 hiprint 的"页眉页脚分界线"）。
