# CrossPrint 需求分析文档

> 版本：v1.0（对应产品版本 v0.1/v0.2/v0.3 规划）
> 状态：已评审基线
> 关联文档：[02-architecture.md](02-architecture.md)、[03-development.md](03-development.md)、[04-task-plan.md](04-task-plan.md)

---

## 1. 项目定位

**CrossPrint** 是一款纯 .NET 原生、类 hiprint 的**组件式跨平台打印套件**：

- 模板以 JSON 描述（毫米坐标 + 磅字体），一份模板多处运行；
- 渲染、编辑内核与宿主 UI 框架**完全解耦**——同一颗内核嵌入 Avalonia、WinForms、WPF、Uno Platform、Eto.Forms、WinUI 3 等任意宿主；
- 设计器预览与打印/PDF 输出走**同一套渲染代码**（SkiaSharp），保证"所见即所印"；
- 以 NuGet 组件形式交付，宿主方 20 行代码内完成嵌入（不是独立设计器软件）。

### 1.1 与参考 PRD 的差异（重要）

附件 PRD 将设计器锁定为 Avalonia 单宿主（其 7.4 节用 Avalonia Canvas + WriteableBitmap）。
本需求按最新目标升级为**多宿主组件式**：

| 维度 | 附件 PRD | 本项目 |
|---|---|---|
| 设计器宿主 | 仅 Avalonia | 宿主无关内核 + 6 个薄适配包 |
| 交互逻辑位置 | Avalonia ViewModel | `CrossPrint.Designer`（无 UI 依赖的内核状态机） |
| 画布渲染 | WriteableBitmap 贴图 | 内核 `EditorRenderer` 直绘各宿主 SKCanvas |
| 元素范围 v0.1 | 5 类 | 8 类（补表格、条形码、椭圆，见 5.4 取舍说明） |
| 数据绑定 | v0.3 | 提前到 v0.1（hiprint 核心卖点，成本极低） |

"同源渲染"这一 PRD 最高原则**保留并强化**：不是"Avalonia 画布与 PDF 同源"，而是"**所有宿主画布**与 PDF/位图/打印机同源"。

## 2. 目标用户与场景

| 用户 | 场景 | 关键诉求 |
|---|---|---|
| 工业上位机开发者 | WPF/WinForms 设备软件内嵌报告/标签设计 | 嵌入组件、离线可用、毫米精度 |
| 医疗设备软件 | 检验报告单，护士自改模板 | 所见即所印、模板 JSON 可分发 |
| 零售 POS / 电子秤 | 小票打印（80mm/58mm 连续纸） | 小票纸型、热敏黑白、快速直打 |
| 物流电商 | 面单、发货标签批量打印 | 数据绑定、批量合成多页 PDF |
| 测试测量 | 曲线报告、校准证书 | 表格、图片、条码、PDF 存档 |
| Avalonia/Uno/Eto 跨平台开发者 | 同一设计能力带到 Linux/macOS | 宿主适配包、纯托管依赖 |

## 3. 对标分析（hiprint → CrossPrint 概念映射）

| hiprint（Web） | CrossPrint 对应物 | 说明 |
|---|---|---|
| `printElementType` 元素定义 | `ElementDefinition` + `ElementTypeRegistry` | 工具箱可增删自定义元素类型 |
| `providers` / 元素面板 | `EditorKernel.Toolbox`（定义目录） | 宿主工具箱 UI 从注册表动态生成 |
| 拖拽设计器 pop/design | `EditorKernel` 状态机 + 宿主手势桥接 | 选中/拖动/缩放/删除/层级 |
| JSON / JS 模板（`printElements`） | `PrintTemplate`（mm 坐标）+ `HiprintTemplateImporter` | 毫米优先于 px；hiprint 导出可直接导入，映射见 schema §8 |
| `getHtml(data)` / 批量打印 | `TemplateRenderer` + `DataBinder` + `BatchPdfExporter` | 一份模板 × N 条数据 → 多页 PDF |
| `paperTypes` 纸张 | `PaperType` 预设 + 自定义宽高 | A 系列 / B 系列 / 热敏小票 / 自定义 |
| 打印客户端（静默直打） | `IPrintTarget`（Windows Spooler / lp / SumatraPDF） | v0.2 交付；v0.1 以 PDF 导出为基线 |
| CSS 定制外观 | 元素样式属性（无 CSS 概念，直接建模） | 不做 CSS 兼容 |

差异化主张：**通用文档模板（非仅小票）× 纯 .NET 原生 × 多宿主组件嵌入 × 设计/渲染/打印三同源**。

## 4. 功能需求（FR）

编号规则：FR-<模块>-<序>。优先级 P0=v0.1 必须，P1=v0.2，P2=v0.3。

### 4.1 模板与序列化（Core）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-CORE-01 | JSON 模板：`schemaVersion`/`paper`/`margins`/`elements`，几何一律毫米（double），字体一律磅 | P0 |
| FR-CORE-02 | System.Text.Json 源生成器序列化，无反射、AOT/Trimmed 零警告 | P0 |
| FR-CORE-03 |  polymorphic `type` 字段分派元素反序列化；未知字段忽略（向前兼容） | P0 |
| FR-CORE-04 | 校验：缺必填（id/type/几何）抛 `TemplateValidationException`（含元素 id 与字段名）；未知 type 抛 `UnknownElementTypeException`（可配置降级为跳过） | P0 |
| FR-CORE-05 | 模板对象图可变（供内核编辑），支持深拷贝；元素 id 模板内唯一性由内核维护 | P0 |
| FR-CORE-06 | 图片以 Base64 内嵌模板；v0.3 增加 `FilePath`/资源解析器 | P0（内嵌）/P2（路径） |
| FR-CORE-07 | hiprint 模板导入：接受严格 JSON / `getJson()` 片段 / JS 对象字面量 / 整个模块源码，映射为 `PrintTemplate`；无法等价表达的特性进 `Issues` 不静默丢弃；多面板一次导入一联 | P0 |

### 4.2 渲染引擎（Engine）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-ENG-01 | `TemplateRenderer.Render(canvas, template, options)` 与宿主无关，输入为任意 `SKCanvas` + DPI/缩放 | P0 |
| FR-ENG-02 | PNG/位图输出（任意 DPI）；PDF 输出（`SKDocument.CreatePdf`，DPI 可配，默认 300） | P0 |
| FR-ENG-03 | 元素渲染器策略模式（`IElementRenderer`），第三方可为自定义类型注册渲染器 | P0 |
| FR-ENG-04 | 降级不崩溃：字体缺失回退默认字体；图片解码失败画红框占位；二维码内容超长截断告警——整页不失败，问题计入 `RenderReport` | P0 |
| FR-ENG-05 | 同一模板在 96 DPI 预览位图与 300 DPI 位图（及按 mm 复现的矢量 PDF）中，任意元素的毫米包围盒一致（跨密度 mm 判据可证，不提交栅格快照基线） | P0 |
| FR-ENG-06 | 批量渲染：一份模板 × `IEnumerable<IDataSource>` → 多页 PDF / 逐条任务 | P0 |
| FR-ENG-07 | 渲染资源（SKPaint/SKFont/SKImage）内部 `using`，字体与位图解码结果带 LRU 缓存 | P0 |

### 4.3 元素类型（v0.1 全量）

| 编号 | 元素 | 关键属性 | 优先级 |
|---|---|---|---|
| FR-ELM-01 | text | content、fontFamily、fontSizePt、bold/italic、underline/strikethrough、color、水平/垂直对齐（含两端对齐）、wrap/省略号、行高倍数、字间距、四向内边距 | P0 |
| FR-ELM-02 | line | direction、strokeColor、strokeWidthMm、虚线样式 | P0 |
| FR-ELM-03 | rectangle | stroke/fill、strokeWidthMm、cornerRadiusMm、虚线样式 | P0 |
| FR-ELM-04 | ellipse | 同 rectangle（椭圆/圆） | P0 |
| FR-ELM-05 | image | Base64、fitMode（Contain/Cover/Fill/None）、圆角 | P0 |
| FR-ELM-06 | qrcode | content、纠错级 L/M/Q/H、前景/背景色、静区模块数、码下标题（文本/字号/字体/颜色/对齐/行距） | P0 |
| FR-ELM-07 | barcode | content、十种码制（Code128/EAN13/EAN8/UPC-A/UPC-E/Code39/Code93/ITF/Codabar/MSI）、是否显示可读文本、条色/背景色 | P0 |
| FR-ELM-08 | table | 列定义（title/widthMm/field/align/fontSize/序号/尾行汇总）、行集合字段、多行表头（colspan/rowspan）、数据行（静态+绑定）、网格线样式、表头样式、斑马纹、行高 | P0 |
| FR-ELM-10 | 公共外观 | 背景色、边框色/边（可多选）/线宽/虚线、绕中心旋转、不透明度；背景→内容→边框统一合成 | P0 |
| FR-ELM-09 | 自定义元素扩展点 | 第三方实现 `IElementRenderer` + 注册即可进工具箱 | P0（机制） |

### 4.4 设计器内核（Designer，宿主无关）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-DES-01 | `EditorKernel`：命令面（PointerPressed/Moved/Released/Wheel/KeyDown，坐标为宿主 px + 逻辑 DPI）+ 状态面（模板、选中集、缩放、平移），内部全 mm 坐标 | P0 |
| FR-DES-02 | 放置：从工具箱拖入或点击落中央；坐标取鼠标点为元素左上角 | P0 |
| FR-DES-03 | 选中/命中测试（按 zIndex 逆序），显示选中框 + 8 缩放手柄（由 `EditorRenderer` 叠加绘制，不进模板） | P0 |
| FR-DES-04 | 拖动移动、手柄缩放（最小 1mm，等角比约束按 Shift）、Delete 删除 | P0 |
| FR-DES-05 | 属性通道：选中元素的属性读写走 `ElementProperties`（类型化描述符，宿主属性面板数据驱动绑定），修改即时反映到画布 | P0 |
| FR-DES-06 | 层级：上移/下移一层、置顶/置底（改 zIndex） | P0 |
| FR-DES-07 | Ctrl+滚轮缩放画布（5%–400%，以鼠标为中心）、空格/中键平移 | P0 |
| FR-DES-08 | 标尺与网格：画布标尺、mm 网格与网格吸附（吸附开关） | P0 |
| FR-DES-09 | 对齐辅助线（元素边缘/中线动态吸附） | P1 |
| FR-DES-10 | 撤销/重做（命令栈）、多选框选、复制/粘贴（创建副本 Ctrl+D 已提供，撤销栈仍未提供） | P1 |
| FR-DES-11 | 方向键微调（1mm，Shift=0.1mm）、Esc 取消、Ctrl+A 全选 | P1 |
| FR-DES-12 | 纸张设置：预设（A3/A4/A5/B5/连续纸/面单）+ 自定义宽高 + 纵横切换；全部走命令，连续纸强制纵向 | P0 |
| FR-DES-13 | 公共外观：八种元素统一支持背景色、边框（色/线宽/虚线）、不透明度；属性描述符带 `Group` 供宿主分组显示；修饰只进渲染公共路径，不各写一遍 | P0 |
| FR-DES-13 | 画布右键菜单：命中选中、创建副本、删除、z 序四操作、水平/垂直居中、适合窗口/100%、旋转/自定义纸张 | P0 |

### 4.5 宿主适配包（Hosts）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-HST-01 | Avalonia 11：自定义 `Control.OnRender` 直接把 `DrawingContext` 当 `SKCanvas` 使用，零位图拷贝；输入事件桥接内核 | P0 |
| FR-HST-02 | WinForms：`Bitmap.LockBits` + `SKSurface` 直绘基类（与官方 `SKControl` 同法；官方包强拉 OpenTK，不引入）+ 事件桥接 | P0 |
| FR-HST-03 | WPF：`SKElement`（SkiaSharp.Views.WPF）直绘 + 事件桥接；属性面板可用原生绑定 | P0 |
| FR-HST-04 | Eto.Forms：`Drawable` + `Eto.Drawing.Bitmap(…, Format32bppRgba)` + `Lock()`（`BitmapData` 直写像素，无 GDI/WriteableBitmap 依赖）| P0 |
| FR-HST-05 | WinUI 3：`SKXamlCanvas`（SkiaSharp.Views.WinUI 2.88.9 实际导出面；无 `SKSwapChainPanel`）直绘 + 指针/键盘桥接；DPI 取 `XamlRoot.RasterizationScale` | P0 |
| FR-HST-06 | Uno Platform：Skia 桌面单投影 `net8.0-desktop`（`Uno.Sdk/5.6.54`）；画布用 `SkiaSharp.Views.Uno.WinUI` 2.88.9 的 `SKSwapChainPanel` 直绘，DPI 取 `XamlRoot.RasterizationScale×96` | P0（编译就位），GUI 运行归用户复核（见 04-task-plan §4） |
| FR-HST-07 | 每个适配包附带最小 Sample（设计器页 + 只读预览页两个场景） | P0 |
| FR-HST-08 | 适配包代码预算：每宿主 ≤300 行；发现超出即视为内核设计缺陷，回到内核解决而非在适配层打补丁 | P0（纪律） |

### 4.6 数据绑定（Binding）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-BND-01 | 占位符 `{{path}}` / `{{a.b.c}}` / `{{items[0].name}}`，数据源 `IDictionary<string,object>` / POCO（反射元数据由调用方注入，内核不反射）/ `JsonDocument` | P0 |
| FR-BND-02 | 作用于 text/qrcode/barcode 的 content 与 table 的列 `field` | P0 |
| FR-BND-03 | 缺失字段渲染为空串并计入 `RenderReport`，不抛异常 | P0 |
| FR-BND-04 | 批量：`BatchPdfExporter.Export(template, dataSource, stream)` 每条数据一页（表格超页自动分页见 FR-BND-05） | P0 |
| FR-BND-05 | 表格数据超出一页时自动续页（表头重复、页脚汇总行） | P2 |

### 4.7 打印输出（Printing）

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-PRT-01 | v0.1：PDF 导出即"打印"，`IPrintTarget` 接口 + `PdfFilePrintTarget` 实现 | P0 |
| FR-PRT-02 | Windows 静默打印：GDI `StartDoc/EndDoc` 直发 Spooler（渲染帧 → 兼容打印机驱动光栅模式；PDF 假脱机需用户确认路径），打印队列回调 | P1 |
| FR-PRT-03 | Linux/macOS：`lp -o page-range…` 子进程打印 PDF；错误透传 | P1 |
| FR-PRT-04 | 打印机枚举、纸张/份数/双面设置 | P1 |
| FR-PRT-05 | 热敏小票直打（位图单色抖动输出） | P2 |

### 4.8 纸型与页面

| 编号 | 需求 | 优先级 |
|---|---|---|
| FR-PAP-01 | 预设纸型：A3–A5、B5、80/58mm 连续纸（无限长或定高）、100×150 面单、自定义 | P0 |
| FR-PAP-02 | orientation 自动交换宽高；margins 仅作安全区提示与物理边距参考，**不参与元素坐标** | P0 |

## 5. 非功能需求（NFR）

| 编号 | 需求 |
|---|---|
| NFR-01 | 性能：50 元素 A4 模板 → PDF < 200ms（Release，中端开发机，基准测试固化）。度量口径是**渲染吞吐**，所以版面用西文矢量文字（实测中位 44ms）。中文版面另有 ~700ms / 12MB 的开销来自 Skia 2.88.9 整份内嵌 CJK 字库、与元素数量无关；小体积归档走 `PdfTextAsOutlines`（实测 15ms / 75KB）。归因与取证见 `docs/rendering-spec.md` §5、`ExporterTests` |
| NFR-02 | 内存：渲染路径零 `SK*` 泄漏（`using` + Finalizer 测试）；字体/位图缓存有上限（LRU，默认 32 项） |
| NFR-03 | 可移植：Core/Engine/Designer 在 win-x64 / linux-x64 / osx-arm64 构建并跑通测试；CI 矩阵 Windows+Linux |
| NFR-04 | AOT/Trimmed：Core/Engine/Designer 经 `PublishTrimmed` 零 IL2xxx/IL3xxx 警告（源生成 JSON、无反射热路径） |
| NFR-05 | API 稳定：v0.1 公开面以 XML 文档注释全覆盖；破坏性变更需 `CHANGELOG` 声明 |
| NFR-06 | 许可：Apache-2.0；全部依赖为宽松许可（Apache-2.0/MIT/BSD）。禁用 AGPL/GPL/商业双许可组件（如 QuestPDF）。许可证清单脚本校验 |
| NFR-07 | 可观测：渲染降级/绑定缺失汇总为 `RenderReport`（结构化列表，宿主自行决定弹窗或忽略） |
| NFR-08 | 无障碍/本地化：错误消息集中资源文件，中文基线 + en 预留（P1） |

### 5.4 范围取舍（只按技术上限，不按省事）

**必须进 v0.1**（技术上限内，成本已核实）：8 类元素、数据绑定、批量导出、标尺网格、Avalonia/WinForms/WPF 三宿主完整可用。

**v0.1 不做且有技术原因**：

| 项 | 原因 |
|---|---|
| Web/WASM 纯托管内渲染 | SkiaSharp 原生库在浏览器 WASM 内不可用（需 SkiaSharp 社区 WASM 实验链路，不在宽松许可可控范围）。Uno 适配层只覆盖 Skia 桌面头（net8.0-desktop），不覆盖浏览器/WASM；真浏览器内画布为远期研究项 |
| Windows 静默直打 | 不阻断，但涉及真机打印机联调，按分工原则归 v0.2（架构接口 v0.1 就位） |
| 模板版本迁移 | 只有 v0.1 一个版本，迁移器无对象；`schemaVersion` 字段已预留 |
| 撤销/重做、多选、对齐吸附 | 状态机改造量大，与"同源渲染验证"里程碑并行会稀释 M2 质量；P1 |

## 6. 验收标准

### 6.1 v0.1（本次开发范围）总验收

1. **同源一致性**（项目生命线）：同一模板 JSON，Avalonia/WinForms/WPF 画布截图与 PDF 在毫米级对齐（自动化：引擎跨密度 mm 包围盒判据 + 逐模块网格比对，见 `docs/rendering-spec.md` §4.1；人工：抽样 5 模板叠图核对）。
2. 手写含 8 类元素各≥1 的示例 JSON，`PdfExporter` 输出后打印测量：元素位置/尺寸与 mm 值误差 ≤0.3mm。
3. 数据绑定：给 100 条订单数据，批量导出 100 页 PDF，文本/条码/表格列全部正确填充，缺字段页不失败。
4. 三宿主 Sample 均可：拖入 8 类元素→编辑属性→保存 JSON→重开无损→导出 PDF。
5. 测试：单测全绿（序列化往返、单位换算 0.01mm 精度、异常路径、渲染快照、许可清单、泄漏检查）；CI Windows+Linux 通过。
6. Eto/WinUI/Uno 适配包：编译通过 + API 文档；GUI 运行实证归用户复核清单（见 04-task-plan §4），不得在 README 宣称已实机验证。

### 6.2 质量门槛

- 渲染路径 0 未处理异常逃逸（全部降级进 `RenderReport`）。
- `PublishTrimmed` 零警告；`dotnet format` 零违规。

## 7. 风险清单（需求层面）

| 风险 | 影响 | 缓解 |
|---|---|---|
| 宿主事件语义差异（指针捕获、滚轮方向、DPI 报告） | 各宿主手感不一致 | 内核只吃"逻辑 px+DPI"抽象；每宿主适配层写 5 项交互冒烟清单 |
| SkiaSharp 2.88.x 与 3.x/4.x 原生库混装 | 运行时加载错版本崩溃 | 全仓 `Directory.Packages.props` 集中锁定 2.88.9，禁止浮动 |
| GDI 直打对 PDF 假脱机的兼容性问题（v0.2） | 静默打印失败 | v0.2 先行 PoC 于 3 类主流驱动；失败则退 lp/SumatraPDF 路线 |
| hiprint 用户期望"CSS 级定制" | 迁移落差 | 文档明示样式建模差异，提供元素扩展点承接高级定制 |

---

附：完整 JSON 字段规范见 [docs/template-schema.md](template-schema.md)（随 M0 交付）。
