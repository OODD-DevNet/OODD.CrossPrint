# CrossPrint 任务计划文档

> 排期以"会话批次"为粒度（一个代理批次 = 一次连续自主推进），不承诺日历周。
> 状态：`待办 / 进行中 / 完成(已实证) / 完成(待用户GUI复核)`。
> 实证纪律：验收列写什么，完成状态就必须核过什么；GUI 运行类验收归用户复核，代理只交付到编译+无头测试+产物文件。

---

## 1. 里程碑总览

| 里程碑 | 目标 | 关键验收 |
|---|---|---|
| M0 | 解决方案骨架 + 四份文档 + schema/rendering 规范 | `dotnet build` 空骨架全绿；文档评审通过 |
| M1 | Core：模型/JSON/纸型/单位 | 序列化往返、异常、换算测试全绿 |
| M2 | Engine：8 元素渲染 + PDF/PNG + 绑定/批量 | 快照基线建立；96/300mm 一致测试绿；50 元素<200ms 基准 |
| M3 | Designer 内核：状态机/命中/手柄/网格/属性通道 | 内核单测绿（纯无头，可全实证） |
| M4 | 宿主第一批：Avalonia + WinForms + WPF 适配 + Samples | 三宿主编译绿；无头渲染冒烟出 PNG 产物 |
| M5 | 宿主第二批：Eto + WinUI + Uno | 编译绿；运行归真机清单 |
| M6 | Printing 接口 + PdfFilePrintTarget + 批量链路 | 100 条数据→100 页 PDF 测试绿 |
| M7 | 开源整备：README/LICENSE/CI/发布 | 本机全量构建/测试/打包/裁剪绿；CI 流水线配置就位（双 OS 执行在托管侧） |

依赖链：M0→M1→M2→M3→（M4 ∥ M5）→M6→M7。M4/M5 可并行派发。

## 2. 任务分解

### M0 骨架（本次会话）
| # | 任务 | 产出 | 验收 |
|---|---|---|---|
| 0.1 | sln/props/global.json/slnf | 根文件 | build 通过 |
| 0.2 | 工程占位 ×（4 库 +6 宿主 +6 示例 +测试） | csproj 图 | build 通过、依赖方向测试可跑 |
| 0.3 | `docs/template-schema.md`、`docs/rendering-spec.md` | 规范 | 字段与模型代码一致 |
| 0.4 | Apache-2.0 LICENSE / CONTRIBUTING / .gitignore | 仓库件 | — |

### M1 Core
| # | 任务 | 要点 |
|---|---|---|
| 1.1 | 模型 POCO：`PrintTemplate/PaperSpec/ThicknessMm/8 元素子类` | 公共字段基类 + Clone |
| 1.2 | `UnitConverter`（mm/pt↔px 唯一换算点） | 测试精确 0.01 |
| 1.3 | `TemplateJson` 源生成上下文 + 手写多态转换器 | 往返无损、未知字段忽略 |
| 1.4 | 校验与异常（含降级开关） | 异常消息含 id+字段 |
| 1.5 | `PaperType` 预设表 | A3–A5/80/58/100×150 |
| 1.6 | Core.Tests | 全绿 |

### M2 Engine
| # | 任务 | 要点 |
|---|---|---|
| 2.1 | `RenderOptions/RenderContext/RenderReport` | issue 结构 |
| 2.2 | `IFontResolver` 默认实现（SKFontManager） | 回退链、LRU |
| 2.3 | 渲染器 ×8 + `ElementRendererRegistry` | 入口统一 mm 缩放 |
| 2.4 | `TemplateRenderer` 主循环（zIndex 稳定排序） | 不抛契约 |
| 2.5 | `PdfExporter`/`PngExporter`/`BatchPdfExporter` | CreatePdf 2.88.x 尺寸折算封装 |
| 2.6 | 数据绑定：占位符解析 + 3 种 `IDataSource` | 缺字段空串+issue |
| 2.7 | 样张模板跨密度一致性（`docs/samples/kitchen-sink.template.json`，8 类元素） | 交付形式改为环境无关判据：逐元素 96/300 DPI 墨迹包围盒 mm 一致 + 留白带零非白像素反向对照。**不建** `tests/Baselines/` 栅格基线（PNG 差多少取决于本机字体与 hinting，换机器即飘），理由记在 `docs/rendering-spec.md` §4.1 |
| 2.8 | 性能基准（50 元素 A4 → PDF） | NFR-01 按渲染吞吐度量：西文矢量版面实测中位 44ms（预算 200ms）。中文矢量版面 739ms/12,168KB 的超额全部归因于整份内嵌 CJK 字库（`PdfTextAsOutlines` 实测 15ms/75KB），三条断言固化在 `ExporterTests` |

### M3 Designer 内核
| # | 任务 |
|---|---|
| 3.1 | `Viewport`（zoom/pan/坐标换算） |
| 3.2 | 命中测试 + `Selection` |
| 3.3 | 状态机：move/resize(8 柄 min1mm Shift 等比)/pan；Delete；滚轮缩放以鼠标为中心 |
| 3.4 | 命令对象化（为撤销栈预留） |
| 3.5 | `ElementProperties` 描述符通道（8 类型全属性） |
| 3.6 | `EditorRenderer` 叠加层（页框/安全区/网格/标尺/选中/手柄/幽灵） |
| 3.7 | 工具箱放置 API；zIndex 四操作 |
| 3.8 | Designer.Tests（无头：feed  PointerPoint 序列断言 mm 结果） |

**M3 交付记录（实测）**

- 3.1–3.4：坐标三层链 `宿主设备px →(×96/dpi) 视口px →(−Pan,÷zoom) 页面px →(÷96/25.4) mm`，`EffectiveRenderDpi = Dpi×Zoom` 是"预览即导出"的唯一缩放入口。命中优先级手柄→元素→空白，交叠区取绘制序末位；线段命中框按 `max(线宽, 0.5mm)` 加宽。缩放下限 1mm、Shift 等比、Esc 回滚（回滚不算撤销，不发命令）。所有变更过 `EditorCommand` 并广播 `CommandExecuted`；滚轮以光标为锚，到极限返回 false 交还宿主翻页。DPI 换算实测：120 DPI 下按 96 算会偏 25%（31.25→41.67mm 的对照）。
- 3.5：8 类型描述符共 6 个公共字段 + 载荷字段（文本 16 / 线 10 / 矩形 11 / 椭圆 11 / 图 10 / 二维码 11 / 条码 14 / 表格 29，表格列按下标展开）。完备性门用反射 + `TemplateJson.PayloadOptions` 真实吐出的字段名反推应有键，漏一条即红（反证：注释掉 `text.lineSpacing` → 门报缺失）。列描述符是快照：列删掉读回 null、写抛 `InvalidOperationException`。`kernel.SetSelectedProperty` 把面板提交规范化后包成 `PropertyChangeCommand`（反证：绕过命令直接改模型 → "一次写回只该产生一条命令" 红）。
- 3.6：拆成 `RenderBackdrop`（工作区/页影/纸/网格/安全区）与 `RenderForeground`（选中框/8 手柄/幽灵/标尺）两遍夹住文档；网格在屏幕上不足 6px 时步长按 2 倍翻，标尺步长自适应（≥8px 一格），手柄固定 8 设备 px 不随缩放变。顺带修掉 `Viewport.FitToPage` 的真实缺陷：按"去掉 padding 的可用区"居中会让整页偏到左上，实测纸心 376 而非 400（差 24px = padding），`ViewportFitTests` 钉住 96/120 DPI 两档。
- 3.8：Designer.Tests 83 条（内核 67 + 属性 5 + 叠加层 8 + 视口 3）。新增断言全部做过"关机制必须红"的反证：相机去掉 Pan → 96/144 DPI 分别差 36.5/55.3px；相机 K 写死 1 → 仅 144 DPI 档差 18.3px；强制开网格 → 关闭态仍出像素；手柄乘 zoom → 2.5 倍下锚点无描边；幽灵不画 → 像素仍纸白；`FitToPage` 旧公式 → 三个视口测试同时红。

**M4 交付记录（实测）**

- 4.1 Avalonia：绘制走 `DrawingContext.Custom` → `ISkiaSharpApiLeaseFeature.Lease().SkCanvas`。租到的画布坐标是 DIP——11.3.6 的 `DrawingContextImpl` 把 `Dpi/96` 折进后置矩阵；适配层 `Scale(1/scaling)` 还原设备 px 后再交给 `EditorCanvas.Draw`，输入 `DIP×scaling`、`Dpi=96×scaling`。按住期间只记一个键；捕获意外丢失时按最后位置补发一次松开（否则内核停在拖拽态）。滚轮 `e.Delta.Y`，按键 Delete/Back/Esc/方向/OemPlus/OemMinus/D0/F；首次拿到有效尺寸整页适配一次。
- 4.2 WinForms：`SkiaSharp.Views.Desktop.Common` 2.88.9 实测**不含** `SKControl`（只有 `SKPaintSurfaceEventArgs`），官方 `SkiaSharp.Views.WindowsForms` 2.88.9 会强拉 OpenTK 3.1 + GLControl；故仓内 `SkiaCanvasControl` 按官方同一套 `Bitmap.LockBits` + `SKSurface.Create(info, scan0, stride)` 实现（约 60 行，零额外原生依赖）。DPI 取 `DeviceDpi`，滚轮把原始 `e.Delta`（±120 一档）交给内核换算。首轮编译踩到 `Keys.OemPlus` 不存在（WinForms 拼写是 `Keys.Oemplus`，CS0117），已修正。
- 4.3 WPF：`SKElement.PaintSurface` 直绘，DPI = `DpiScaleX×96`（横纵恒等，共用一个值），坐标 `DIP×dpi/96`。WPF 自动捕获鼠标；`OnLostMouseCapture` 用 Esc 语义回滚，避免无键移动继续改模型；已有按键拖拽时忽略第二键，防止先按的那个收不到松开。
- 4.4/5.4 样例 ×3（Avalonia/WinForms/WPF）：工具箱 8 类型 + 删除 + z 序四操作 + 适合窗口/100% + 网格/标尺/安全区开关 + PDF 导出 + 模态预览窗；属性面板由 `ElementProperties` 描述符驱动（文本/多行/数字/整数/布尔/枚举/颜色/虚线），提交统一走 `SetSelectedProperty`，解析失败就地显示错误。三份样例都把 `docs/samples/kitchen-sink.template.json` 拷到输出目录（实测三个 `bin` 下均存在），模板用 `TemplateJsonLoadOptions.Tolerant` 加载，失败回退 A4 并写状态栏。
- 4.5 无头冒烟：`EditorCanvasTests` 新增一条 PNG 冒烟——同一模板在 96/144 DPI 各渲染一次，矩形与文字两条墨迹都落在按 `dpi/25.4` 独立算出的位置；PNG 签名/体积/解码回读尺寸全对齐；空模板在同一管道下零墨迹（负对照）。反证：编码切成 JPEG → 签名断言红。产物在 `tests/CrossPrint.Designer.Tests/bin/Debug/net8.0/artifacts/editor-canvas-{96,144}.png`（814×1143 / 1211×1704）。
- 汇总：`dotnet build OODD.CrossPrint.slnf` → 14 个项目 0 警告 0 错误；测试 118 + 98 + 88 + 28 = 332 全绿。GUI 交互（拖动、打印对话框）按 §5 清单归用户复核。

**M5 交付记录（实测）**

- 5.1 Eto：`Eto.Forms` 2.10.0 实测无 `Eto.Platforms.Windows`，可用平台入口是 `Eto.Platforms.WinForms`（样例里因命名空间以 `.Eto` 结尾，必须写 `global::Eto.Platforms.WinForms`）；键名实测为 `Keys.Equal`（`Plus` 已过时）、`Keys.Keypad0`、`Keys.Backspace`，无 `OemPlus`/`NumPad0`/`Back`；`MouseEventArgs.Delta` 是 `SizeF`，滚轮取 `Delta.Height` 再把原始值交给内核；`Screen.PrimaryScreen.DPI` 是 `float`。绘帧按设备 px 分配 `Bitmap(w,h,Format32bppRgba)`，`Lock()` 后 `SKSurface.Create(Bgra8888, Unpremul, data.Data, ScanWidth)` 直写；Skia 不接受非预乘时退到预乘 `SKBitmap` + `SKPixmap.ReadPixels` 按 `ScanWidth`/`Flipped` 逐行转写（样例与控件共用）。`Format32bppRgba` 实测语义为"A 在 32 位最高位"，小端内存即 B,G,R,A，与 `SKColorType.Bgra8888` 对齐。
- 5.2 WinUI：`SkiaSharp.Views.WinUI` 2.88.9 实测只导出 `SKXamlCanvas`（命名空间 `SkiaSharp.Views.Windows`），**没有** `SKSwapChainPanel`；`RasterizationScale` 从 `XamlRoot` 取（未加载时按 1.0 兜底），指针 DIP×scale 交内核。`SKXamlCanvas` 继承 `Panel` 拿不到焦点，控件本身是 `UserControl` 并 `IsTabStop`，按下时 `Focus(FocusState.Pointer)`；`PointerCaptureLost` → Esc 回滚。本机仅有 dotnet CLI、无 VS 的 AppxPackage 任务，`MrtCore.PriGen.targets` 会把 `GetMrtPackagingOutputs`/`_GetDefaultResourceLanguage` 无条件下挂到 `PrepareForRun` 导致 MSB4062；两个工程无 PRI 资源输入，故在项目级 `Directory.Build.targets` 里置空这两个目标（构建语义不变，本记录与 `docs/03-development.md` 坑位表已登记）。
- 5.4 样例：Eto（`net8.0-windows7.0`，`Eto.Platform.Windows` 只提供 net6.0-windows 资产，向下兼容）与 WinUI（`WindowsPackageType=None`，纯 C# 组合 UI）各含设计器 + 预览两页、工具箱 8 类型、属性面板、PDF 导出，同样把 `kitchen-sink.template.json` 拷到输出目录。
- 5.3 Uno：`Uno.Sdk/5.6.54` + `net8.0-desktop` 实测 0 警告 0 错误，解析到 `Uno.WinUI 5.6.99` / `SkiaSharp 2.88.9` / `SkiaSharp.Views.Uno.WinUI 2.88.9`。画布用该包实测存在的 `SKSwapChainPanel`（`SKXamlCanvas` 也在包内，这里取交换链面板），DPI 取 `XamlRoot.RasterizationScale×96`。样例 `NETSDK1136` 的根因是 `Uno.WinUI.Runtime.Skia.Wpf` 传递引入 WPF 框架引用，加 `<UnoSingleProject>true</UnoSingleProject>` 后由 `_UnoRemoveTransitiveWPFDependency` 移除；两个 Uno 工程关闭 CPM（隐式包与 `Directory.Packages.props` 冲突），`SkiaSharpVersion` 钉 2.88.9。`Uno.Sdk` 默认 `IsPackable=false`，适配层显式打开后产出 `CrossPrint.Hosts.Uno.0.1.0.nupkg`（lib 组 `net8.0-desktop1.0`，依赖含 Core/Engine/Designer、`SkiaSharp.Views.Uno.WinUI 2.88.9`、`Uno.WinUI 5.6.99`，README 已内嵌）。Uno 6.7 需 SkiaSharp 3.119 + net9，与仓库锁冲突，暂不跟进。
- 汇总（M5 完成）：`dotnet build OODD.CrossPrint.slnf` → 20 个项目 0 警告 0 错误；`dotnet pack` 10 个 src 包全部成功且内嵌 README；四个测试项目 125 + 98 + 88 + 28 = 339 全绿。GUI 运行与手感仍归用户复核。

### M4/M5 宿主（模板化四件套，见架构 §5）
| # | 宿主 | 特殊点 |
|---|---|---|
| 4.1 | Avalonia | `dc→SKCanvas`；`RenderScaling`；焦点控件 `OnKeyDown/OnKeyUp`（KeyDown 已冒泡到控件，无需 Tunnel） |
| 4.2 | WinForms | `SkiaCanvasControl`（`Bitmap.LockBits`+`SKSurface`，等价官方 `SKControl`）；`DeviceDpi`；滚轮 Delta/120 |
| 4.3 | WPF | `SKElement`；`DpiScale`；鼠标自动捕获 |
| 5.1 | Eto | `Bitmap`+`Lock()`（`BitmapData`）桥；平台名是 `Eto.Platforms.WinForms`（无 `Eto.Platforms.Windows`）；键名 `Keys.Equal`/`Keypad0`/`Backspace`；滚轮 `Delta.Height`；macOS 风险标注 |
| 5.2 | WinUI | `SKXamlCanvas`（2.88.9 无 `SKSwapChainPanel`）；`XamlRoot.RasterizationScale×96`；焦点放在 `UserControl`；dotnet CLI 下置空 MrtCore 两个 PRI 目标（无资源输入） |
| 5.3 | Uno | `Uno.Sdk/5.6.54` + `net8.0-desktop`；`SKSwapChainPanel` 直绘（与 WinUI 的 `SKXamlCanvas` 不同）；`XamlRoot.RasterizationScale×96`；样例 `UnoSingleProject=true` 解 NETSDK1136；SkiaSharp 钉 2.88.9 |
| 4.4/5.4 | Samples ×6 | 设计器页 + 预览页；属性面板数据驱动表单 |

### M6 Printing
| # | 任务 |
|---|---|
| 6.1 | `IPrintTarget`/`PrintSettings`/`PrintResult`（接口冻结） |
| 6.2 | `PdfFilePrintTarget` |
| 6.3 | 批量端到端测试（100 条→100 页） |
| 6.4 | （v0.2）GDI Spooler / lp 实现——本次只留空实现类与文档 |

**M6 交付记录（实测）**

- 6.1：`PrintDocument(Template, Data, Copies)` 构造期校验（`Copies ≥ 1`），`with` 绕过的二次校验放在展开页序时；`PrintSettings` 的六个旋钮（Options/Overwrite/FontResolver/Images/Renderer/OutputPath）各有一条专门测试证明"真的被读到"——挂上不读的旋钮等于假功能。`PrintResult` 的成功/失败不变式在构造期强制（成功⇒无 Error 且页数≥1；失败⇒页数 0 且给出原因），属性 `private init` 连 `with` 都写不出来。
- 6.2：写盘 = 同目录临时名 `{名}.crossprint-{guid}.tmp{扩展名}` + `Flush(true)` + `File.Move(…, true)`；取消检查两次前置并折成 `Failed`（不向 `await` 抛）；批量入口 `PrintBatchAsync` 只在具体类上，是否上提到接口留给 v0.2 的两个真实打印目标定了再说。
- 6.3：100 条→100 页、`/FontFile2` 出现 **1** 次；实测 Arial 614,397B / 56–65ms（页增量 523B），YaHei 12,511,991B / 0.86–1.06s ≈ 单页同价——字库是每文档一次，这是 100 页面单成立的前提。反证：把批量改成逐页 `PdfExporter` 后 6 条测试红（页数 100→1；CJK 版从 3 秒涨到 1 分 19 秒），证明"只嵌一份"不是橡皮章。

### M7 整备
README（截图位/GIF 位/快速开始/已知限制如实标注）、`license-check`、依赖方向检查、CI yml、`dotnet pack` 冒烟。

**M7 交付记录（实测，本机）**

- README：六宿主状态矩阵（Avalonia/WinForms/WPF 标注无头冒烟，Eto/WinUI/Uno 标注"编译，运行归人工"）、六条样例运行命令、嵌入与导出片段、模板格式/绑定、Trim/AOT 说明、已知限制（含 Uno 5.6 基线与 Uno 6.x 未支持）。截图位保留占位，不伪称已附截图。
- 许可：`LicenseInventoryTests` 对 `Directory.Packages.props` 与 `docs/third-party-licenses.md` 做双向精确对账（多一个/少一个都红），许可证限制在宽松集合；QuestPDF/QRCoder 禁止项有专门断言。Uno.Sdk/Uno.WinUI 不经 CPM 引入，已在清单"边界说明"里写明 Apache-2.0 与来源，不计入 CPM 对账表（否则会对账出假条目）。
- 依赖方向：`DependencyDirectionTests` 直接读 `src/**/*.csproj` 的 ProjectReference——Core 无引用、Engine 只依赖 Core、Designer 只依赖 Core+Engine、各宿主只依赖内核四层且互不引用，内核层 csproj 文本不得出现任何宿主 UI 框架名。Uno 宿主纳入扫描后仍全绿。
- CI：`.github/workflows/ci.yml` 分 `portable`（ubuntu + windows，Portable.slnf + 四套测试）与 `windows-hosts`（全量 slnf、10 个包、README 内嵌门、win-x64 partial trim 门）。trim 门正则收紧为 `warning IL\d+:.*\bCrossPrint\.(Core|Engine|Designer|Printing|Hosts)\.`，只拦本仓程序集，Avalonia 自身 IL2026/IL2104 不误杀（用含两类告警的合成日志验证过匹配数=1）。
- 打包冒烟：本机 `artifacts/nupkgs/` 实测 10 个 nupkg（Core/Engine/Designer/Printing + Avalonia/WinForms/Wpf/Eto/WinUi/Uno），逐个检查均含 `README.md`；Uno 包 nuspec 依赖为 Core/Engine/Designer + `SkiaSharp.Views.Uno.WinUI 2.88.9` + `Uno.WinUI 5.6.99`。
- 裁剪：Avalonia 样例 win-x64 `PublishTrimmed=true`/`TrimMode=partial` 退出码 0，产出约 42MB，本仓程序集零 IL 警告；剩余告警均来自 `Avalonia.DesignerSupport`/`Avalonia.Remote.Bson`。
- 终态数字：`dotnet build OODD.CrossPrint.slnf -c Release` → 20 个项目 0 警告 0 错误；测试 125 + 98 + 88 + 28 = 339 全绿；`dotnet test ... -c Release --no-build` 同数。GUI 启动、手感与真机打印仍归用户复核（§4）。

### M8 设计器交互补齐（对照 hiprint，2026-10-30）
| # | 任务 |
|---|---|
| 8.1 | 内核：`SelectAt`（右键命中）、`DuplicateSelection`、`CenterSelection*`、`SetPaperPreset/SetCustomPaper/SetPaperOrientation/RotatePaper`，配套 `PaperCommand`/`DuplicateElementsCommand` |
| 8.2 | WinForms 样例：纸张栏（预设/自定义/纵横切换）、画布右键菜单、Ctrl+D、属性面板可见性修复 |
| 8.3 | 其余五宿主样例移植同一套交互（菜单/纸张栏/快捷键/空选提示） |

**M8 交付记录（实测，本机）**

- 8.1：新增 8 条内核测试（`KernelFeatureTests`），断言的是**按纸张预设表与页面算术独立写出的期望值**，不是拿被测实现反推：A3=297×420；A4 横向页面=297×210；Receipt80=80×0 且强制纵向、旋转抛 `InvalidOperationException`；自定义 58×0 记为连续纸；副本偏移 5mm 且夹在页内（195→190、290→282）；右键命中选中/空白清选；水平/垂直居中=(210−20)/2=95、(297−15)/2=141，且每条都能 `Revert`。
- 8.1 反证（关机制必须红）：`CommitPaper` 直接 return → 4 条纸张测试红；只关 `SetPaperPreset` → 预设与连续纸 2 条红；去掉副本的 `Math.Clamp` → 越界副本 1 条红；`SelectAt` 不再清选 → 右键空白 1 条红；水平居中退回 `before.X` → 居中 1 条红。恢复后 Designer 96 条全绿。
- 8.2（WinForms）：属性编辑器"只剩标签"的根因是 `SplitContainer.SplitterDistance=340` 在控件最终宽度之前设置、被静默夹到 ~110px；改到 `OnShown` 后标签列 132、编辑器列占满剩余。纸张栏与右键菜单的每一项都调用真实内核方法（无占位），菜单打开前用右键位置调 `SelectAt`，连续纸禁用旋转与垂直居中。
- 8.3：Avalonia/WPF/Eto/WinUI/Uno 五份样例复用同一语义；宿主只做菜单皮肤与坐标/DPI 转发（DPI 分别取 `96×RenderScaling`、`DpiScale×96`、`DesignerControl.Dpi`、`XamlRoot.RasterizationScale×96`）。五个宿主适配层统一改了一处**必要**行为：右键按下只取焦点，不 `Capture`、不进内核、不置 Handled——否则菜单弹出/失焦会触发各自的 Esc 回滚，把刚右键选中的元素清掉（Eto 还会让 `MouseUp` 的按键位在某些平台为空，样例改在 `MouseDown` 判定右键）。
- 8.3 验证：六份样例逐个 `dotnet build` 0 警告 0 错误；全量 `OODD.CrossPrint.slnf` 20 个项目 0 警告 0 错误；四套测试 125 + 98 + 96 + 28 = **347** 全绿。菜单项与纸张栏逐项核对为真实内核调用（`SetPaperPreset`/`SetCustomPaper`/`RotatePaper`/`DuplicateSelection`/`CenterSelection*` 等），样例内无 TODO/占位实现。

### M9 画布选中修复 + 属性面板加厚（2026-10-30）

| # | 任务 |
|---|---|
| 9.1 | Avalonia：修复自定义绘制操作命中，恢复左键选中与右键菜单 |
| 9.2 | 模型/JSON/渲染：八种元素公共背景/边框/不透明度，文本下划线/删除线 |
| 9.3 | 属性通道：新增公共字段与文本装饰描述符，加 `Group` 分组 |
| 9.4 | 六宿主属性面板：元素标题 + 分组标题；新增 Avalonia 命中回归测试与全量验证 |

**M9 交付记录（实测，本机）**

- 9.1 根因：`SkiaDrawOperation.HitTest` 恒返回 `false`。Avalonia 的 `CompositionDrawListVisual.HitTest` 把 `ICustomDrawOperation.HitTest` 当作该视觉是否可命中的唯一依据，因此 `DesignerControl` 收不到任何 `PointerPressed`——画布显示正常但左键选不中、右键菜单也不弹。改为 `_bounds.Contains(p)` 后事件恢复；元素级命中仍由内核 `SelectAt` 做二级判定。新增 `tests/CrossPrint.Hosts.Avalonia.Tests/SkiaDrawOperationTests.cs` 锁住契约。
- 9.1 反证：把 `HitTest` 改回 `=> false`，`命中区覆盖整块绘制区` 立即红（`Expected operation.HitTest(new Point(10, 10)) to be True, but found False`）；恢复后绿。
- 9.2：`PaperElement` 增加 `BackgroundColor`/`BorderColor`/`BorderWidthMm`/`BorderDashPatternMm`/`Opacity`，`TextOptions` 增加 `Underline`/`Strikethrough`；JSON 读写、`CloneDeep` 全部同步（8 个 `CloneDeep` 统一改走 `CopyCommonFrom`，避免漏字段）。`TemplateRenderer` 在公共路径按"背景→内容→边框"绘制，整组包在一次 `SaveLayer` 里做不透明度合成；默认值下渲染结果不变。
- 9.2 反证：临时摘掉 `DrawBoxBackground`/`DrawBoxBorder`、强制 `opacity = 1.0`、把文字装饰画笔置空后，`ElementBoxStyleTests` 4 条中 3 条红（背景/边框 0 像素、50% 黑读出纯黑、下划线/删除线无新增墨量）；克隆用例保持绿，恢复后 4 条全绿。
- 9.3：公共描述符 6 → 11 条；文本描述符 12 条（含新增下划线/删除线），单元素最多可见 23 条。`PropertyDescriptor.Group` 按键前缀推断分组（基本/位置与尺寸/外观/文字/线/形状/图片/二维码/一维码/表格/表格列）。
- 9.4：六个样例的属性面板统一加入元素标题与分组标题（Avalonia/WinForms/WPF/Eto/WinUI/Uno），分组顺序由描述符顺序决定，未改任何写入语义。六个样例逐个 `dotnet build` 0 警告 0 错误；全量 `OODD.CrossPrint.slnf` 21 个项目 0 警告 0 错误；测试 125 + 96 + 102 + 28 + 1 = **352** 全绿。

### M10 hiprint 模板导入 + 渲染语义补齐（2026-10-30）

| # | 任务 |
|---|---|
| 10.1 | Core 模型承载 hiprint 语义：公共 `borderSides`/`rotationDegrees`；文本内边距/字间距/省略号/两端对齐；二维码码下标题；图片圆角；表格行集合字段/多行表头/列序号/尾行汇总 |
| 10.2 | `HiprintJsonNormalizer` + `HiprintTemplateImporter`：严格 JSON / `getJson()` 片段 / JS 对象字面量 / 整个模块源码 → `PrintTemplate`，问题清单不静默丢弃 |
| 10.3 | Engine 渲染：公共路径（背景→内容→边框、旋转、alpha）；文本排版补内边距/字间距/省略号/两端对齐；二维码标题；图片圆角；表格多行表头合并线、行序号、汇总行、行集合字段推断 |
| 10.4 | Designer 属性通道：公共字段 11→13，补文本/二维码/图片/表格列/表头单元格描述符；六宿主面板随描述符自动生效 |
| 10.5 | 两层测试：`HiprintImporterTests`（真实夹具 + 合成场景 + 归一化反证）与 `HiprintSemanticsRendererTests`（渲染后果 + 关机制反证） |

**M10 交付记录（实测，本机）**

- 10.2 导入器：元素 `left/top/width/height` 按 pt 换算 mm（容差 0.01 实测），面板按 mm；`panelPageRule:"none"` → 高度 0 连续纸；`paperHeader/paperFooter` → 上下留白；`field` → `{{field}}`（`hideTitle=false` 拼标题前缀）；`textContentWrap` 三态 → `wrap`/`ellipsis`；二维表头按 colspan/rowspan 展开，`checked:false` 单元格跳过（template1 实测 6 列网格导入 5 列）；`tableTextType:"sequence"`、`tableSummary` 五种 + 文本汇总映射到列属性；10 种一维码制全映射。归一化器以"逐个顶层对象枚举 + 反引号模板串整体跳过"处理完整 JS 模块（template7 含 import/lodash/注释），以"补外层花括号再片段提取"处理 `getJson` 输出。
- 10.2 反证：夹具注释里写明每条机制关掉后哪个用例必红——不枚举顶层对象或把反引号里的 `{}` 当边界 → template7 解析失败；去掉隐藏列判断 → 列数与"不出现性别列"断言红；关掉补花括号 → getjson 用例抛异常。
- 10.3 Engine 语义：`HiprintSemanticsRendererTests` 12 条逐像素取证，每条都有关机制对照（旋转 90° 后包围盒 40×10→10×40 且中心不动；`borderSides=Top` 时下/左窗口零墨；内边距把墨迹整体推 +10/+6mm；字间距 3pt 增宽约 9.5mm 而左沿不动；省略号 vs 裁切的右沿墨量差；`Justify` 右端 >88mm vs `Left` <50mm；二维码标题只出现在下带；图片圆角清空角窗；显式 `Table.Field` 出 2 行 vs 推断失败 0 行；序号列有墨 vs 空列；汇总行多一条带；多行表头 colspan 内部无线、rowspan 边界有线）。
- **10.3 发现的真实缺陷（已修）**：
  1. `TryBuildSidesPath` 用 `drawing` 标志判断"是否画过"，末尾未选中的边会把它重置为 false，导致**单边/部分边边框从不绘制**。改为独立 `drew` 标志；反证（旧实现下分边测试红）。
  2. `PaintHeaderGrid` 忽略元素 `yMm`，多行表头的横线全部画在页面顶部 y≈0。改为传入 `headerTop`，横线逐列分段并按 rowspan 跳过；同一修复也暴露出样张里表格与二维码重叠——把 `kitchen-sink` 的表格 `yMm` 从 92 移到 100（schema 文档同步），恢复"逐元素墨迹窗口"判据的有效性。
- 10.4 属性通道：公共描述符 11→13（`borderSides`、`rotationDegrees`）；文本补 padding×4/letterSpacing/ellipsis；二维码补标题 7 项；图片补圆角；表格补 `field`/`headerRowHeightMm`/`headerFontSizePt`；列补 sequence/summary/summaryLabel/summaryDecimals；表头单元格按 `table.headerRows[i].*` 快照展开。六个宿主样例不改一行即获得新字段（面板完全数据驱动）。
- 10.5 终态：`dotnet build OODD.CrossPrint.slnf` 21 个项目 **0 警告 0 错误**；测试 Core 181 + Engine 127 + Designer 98 + Printing 28 + Avalonia 1 = **435** 全绿（`dotnet test OODD.CrossPrint.slnf --no-build` 同数）。真实夹具 4 份 + 导入器 56 个用例覆盖多面板、连续纸、隐藏列、多行表头与问题清单；跨层回归把 4 份夹具直接导入并整页渲染，断言无元素被跳过且页面确有墨迹（150 DPI 非白像素实测 111,464 / 91,155 / 40,110，下限取 20,000）。另有一条断言证明同一模板的 JS 模块与 `getJson` 片段导入结果逐字节一致（问题清单顺序也一致）。

### M11 hiprint 导入入口进六宿主（2026-10-30）

| # | 任务 |
|---|---|
| 11.1 | 六个样例加 **文件 → 导入 hiprint 模板…** 菜单 + 工具栏按钮，共用一条导入流程 |
| 11.2 | 换模板后的重绑：属性面板/纸张栏 `Attach(EditorKernel)`，重订 `TemplateChanged` |
| 11.3 | 随样例分发 `docs/samples/hiprint-sample.js`，并加"这份示例必须真的能导入"的回归 |
| 11.4 | 六宿主底部模板 JSON 面板：实时显示、可直接编辑并应用；工具栏导入按钮移到首位 |

**M11 交付记录（实测，本机）**

- 11.1：Avalonia/WPF/WinForms/Eto 用系统文件对话框，WinUI 用 `FileOpenPicker`（`InitializeWithWindow`），Uno 因 Skia 桌面文件选择器依赖平台实现改用路径输入框（与它的 PDF 导出同一口径）。导入状态栏统一报"已导入 X：N 个元素，M 条告警"，Avalonia/WPF/WinForms/Eto/WinUI 附带前 3 条告警文本。
- 11.2 真实缺陷：`DesignerControl.LoadTemplate` 会**替换** `EditorKernel` 实例，原来 `Attach` 过旧内核的属性面板与 Uno 纸张栏会继续盯着旧模板。五个非 Avalonia 属性面板与 Uno `PaperPanel` 新增 `Attach(EditorKernel)`（先退订旧内核事件再重建），各宿主导入后重新 `Attach` 并重订 `TemplateChanged`。
- 11.2 连带修复（归一化器）：裸 `function(){}` / 箭头函数据此前被折叠成 `null`，`formatter`/`styler` 字段随之消失，`hiprint.js-formatter` 降级告警也不发——手写 JS 字面量会静默丢回调。改为保留占位字符串 `[JavaScript function]`，告警恢复；真实 hiprint 导出的反引号源码字符串行为不变。
- 11.3：新增 `DocumentedSampleTests.Shipped_hiprint_sample_imports_with_only_the_declared_formatter_warning`——断言 8 个元素按序导入、`HasErrors=false`、问题清单恰为一条 `hiprint.js-formatter`。修复前该用例红（问题清单为空），可直接充当"归一化器折叠函数 → 演示入口失真"的反证。
- 11.4：用户反馈"没看到导入按钮"——根因是 Avalonia 的工具栏把 `WrapPanel` 放进了横向自动滚动的 `ScrollViewer`，`WrapPanel` 拿到无限宽不换行，靠右的导入按钮直接被滚出可视区。改为禁横向滚动让其换行；WPF/WinForms 的溢出菜单同类问题用"导入放工具条最前面"解决。底部新增模板 JSON 面板（高度 210）：默认缩进序列化实时回写，编辑后「应用 JSON」走 `TemplateJson.Load(Tolerant)` + `LoadTemplate` + `BindKernel` 重绑属性面板/纸张栏；脏状态时不覆盖文本框；解析失败只在面板内报错、当前模板不受影响。
- 11.4 后续：脏状态判定从布尔抑制开关改为**与上次同步文本做归一化快照比对**（Avalonia 的 `TextChanged` 可能晚于赋值返回触发，开关会误报"有未应用的修改"）；程序化回写后光标归位到首行，面板总从 `{` 开始显示。Avalonia 实机截图核对：启动即显示缩进 JSON 与"已同步当前模板（8 个元素）"。
- 11.5：用户把 hiprint 设计器导出的 `getJson`（根节点 `{"panels":[…]}`）粘进"模板 JSON"面板报解析失败——面板此前只认 CrossPrint 模板。新增 `HiprintTemplateImporter.LooksLikeHiprintJson`（**M14 更正**：早期版本单独判 `panels`/`json.panels`/面板数组三种形状，与 `Import` 的实际判据不同源，实测 6 处分歧——`{"panels":[]}` 判真却导入失败、`[5,{…}]` 判假却导得进来；现在两边共用"归一化后能收集到非空面板数组"这一条判据，见 §M14 D），六个宿主的「应用 JSON」先分流：hiprint 结构走导入器转换并列出降级项，其余仍走 `TemplateJson.Load`。真实样本 `invoice-getjson.json`（51 个元素：33 文本 / 13 线 / 3 椭圆 / 1 矩形 / 1 二维码）无错误导入，仅一条 `hiprint.page-number-unsupported` 警告；Avalonia 实机用 UIA 粘贴并点「应用 JSON」，画布渲染出发票、文本框换成 A5 横向的 CrossPrint JSON。回归：`发票getJson_导入全部51个元素且无错误`、`LooksLikeHiprintJson_只认panels结构`。
- 终态：`dotnet build OODD.CrossPrint.slnf` 21 个项目 **0 警告 0 错误**；测试 Core 187 + Engine 129 + Designer 98 + Printing 28 + Avalonia 1 = **443** 全绿。六个样例的实际点击、文件对话框与 Uno 路径输入框归用户复核（§4）。

### M12 发票导入后的排版口径校准（2026-10-30）

用户反馈"导入后字体大小不对劲"。以 `invoice-getjson.json` 为样本，在真实 hiprint bundle + print-lock.css
里逐元素读 computed style，把六处与浏览器不一致的口径按实测改正：

| # | 实测结论 | 改法 |
|---|---|---|
| 12.1 | 二维码标题继承元素 `font-size`（默认 9pt），不是 10.5pt；标题 `white-space:nowrap` | `CaptionFontSizePt` 默认 9；标题单行排（折行会从码区高度再扣一行，把二维码压小 6pt） |
| 12.2 | 文本默认垂直对齐是 `top`，只有显式 `middle`/`bottom` 才加对齐类 | `ParseVertical` 默认 `Top` |
| 12.3 | 未写 `lineHeight` 时 line-height 由 CSS 固定为 9.75pt，**不随字号缩放**（13pt 表头仍是 9.75pt） | `LineSpacing = 9.75 / fontSizePt` |
| 12.4 | 基线按 CSS 行盒模型（内容区居中于行高，负 leading 上移基线） | `TextStyle.BaselineOffset`，文本/二维码标题/条码可读文本/表格单元格统一使用 |
| 12.5 | 文本元素没有 `overflow:hidden`：13pt 表头的"、"整个在框外，18pt "NO" 降部出框 | `TextRenderer.ClipsToElement = false`（图片/表格仍裁剪） |
| 12.6 | 模板宽度按 print-lock.css 的 SimSun 量（ASCII 0.5em）；雅黑 ASCII 0.55–0.59em 会让"2019年05月09日""NO" 折行 | 未写 `font-family`（hiprint 为 `inherit`）时导入为 `SimSun` |

- 取证：`HiprintImporterTests` 新增"未写 lineHeight 时行距按固定 9.75pt 换算"与"发票 getJson 字号行距字体按 hiprint 量得的默认值呈现"；
  `TextAndShapeRendererTests` 新增"行高按 CSS 行盒模型把基线推下半行距"（20pt 字号行距 1.0→2.0，墨迹顶下移 ≈3.53mm）与"文本溢出元素框时不被裁掉"。
- 反证：把 `ClipsToElement` 改回 `true`、"文本溢出"用例立刻红（墨迹底被截在 12mm 框内）；把 `BaselineOffset` 退回 `AscentMm` 后基线用例超差。
- 终态：21 个项目 0 警告 0 错误，测试 **443** 全绿；发票 96dpi 重渲染确认二维码恢复 45pt 级、标题单行、日期与 NO 不折行、"、"与购买方/密码区完整可见（`artifacts/inv-ours-96.png`）。

### M13 表格识别与码制占位（用户反馈"表格没识别出来 / 条码预览是黑块"，2026-10-30）

用户导入自己的 hiprint 模板（界面提示"已同步当前模板（32 个元素）"，而源模板 33 个元素）
后，表格区域空白；预览里条码是几块巨大黑块、看不到任何内容。

| # | 任务 |
|---|---|
| 13.1 | 导入器：`tableCustom` 等价映射为 `table`（Info）；`columns` 的对象映射与单个扁平列对象都读出列；缺列定义时保留表格框、占 1 列占位并警告 |
| 13.2 | Core：模板级 `designData`（扁平路径→标量），元素 `field + testData` 导入时自动收集；JSON 读写、`Clone` 深拷贝 |
| 13.3 | Engine：空内容不交给 ZXing，二维码/一维码画浅灰虚线占位框 + "缺少数据 {{表达式}}"，记 `QrCodeFailed`/`BarcodeFailed` |
| 13.4 | Engine 绑定 + Designer：`DataBinder.WithDesignData` 宿主优先、设计数据只补缺；`EditorCanvas.Draw` 合并设计数据；核心模型测试同步 |
| 13.5 | 六宿主：导入摘要统一走 `BindKernel(note)`，换内核后不再被"已同步当前模板"覆盖；摘要含降级项与"已保留 N 项测试数据" |

**M13 交付记录（实测，本机）**

- 13.1 根因：旧类型 `tableCustom` 落进未知元素分支整条跳过（32 vs 33），同时表格 `columns` 的两种对象写法会被读成空列/空行，最终触发"无列定义"丢表。现在三条退路都堵上；缺列时表格框与位置尺寸原样保留，用户可在属性面板补列。
- 13.3 根因实测：`new Code128Writer().encode("")`（渲染器单参路径）返回 **24 模块、14 个黑模块**的"合法"图案；90mm 宽的元素每模块 3.75mm，在 300dpi 下画出 **146,792 个暗像素（≈1052 mm² 纯黑）**，而报告里只有一条 `BindingMissingField`，没有任何"条码失败"告警——用户看到的就是这几块黑砖。二维码空内容则直接抛 `Found empty contents`。现在两者都挡在编码器之前。
- 13.2/13.4：`designData` 只承载标量（数字取原文、布尔转小写文本，对象/数组/null 不写入）；设计数据只补宿主持有数据里缺失的根字段，宿主字段值为 `null` 也算命中，不回落；`TemplateRenderer`/PDF 导出/打印链路不读设计数据，样例值不会被印到真实单据上。
- 反证（每条都实跑并确认变红后已还原）：去掉空内容守卫 → 两个占位框用例红（条码用例找不到 `BarcodeFailed`，二维码用例报 `Found empty contents`）；去掉 `EditorCanvas` 的设计数据合并 → "设计数据兜底"用例红并退回"缺少数据"占位；`tableCustom` 改回未知分支、扁平列分支返回空、缺列时 `return null`、`columns` 映射判断写死 false → 对应四个用例分别红。
- 新增/扩充测试：Core 9（表格兼容 4、`field+testData`→设计数据 3、`designData` 往返与 `Clone` 独立 2）、Engine 7（空内容占位 2、设计数据绑定规则 5）、Designer 1（设计数据兜底出真条码）。
- 终态：`dotnet build OODD.CrossPrint.slnf` 21 个项目 **0 警告 0 错误**；测试 Core 196 + Engine 136 + Designer 99 + Printing 28 + Avalonia 1 = **460** 全绿。六样例的菜单点击与真实导入（含文件对话框）归用户复核。

### M14 全库缺陷清扫（用户反馈"编辑器再操作 table 时无法删减列" + "扫描整个库找 bug"，2026-11-01）

用户报的"删不掉列"是两段缺陷叠出来的：导入端把 `columns` 读成 1 列，界面端按钮被容器裁掉。清扫按层分波并行，每波都先写红用例再改，报告里的数字全部来自实跑日志。

**A. 导入端（Core）**

| # | 缺陷 | 实测形态 |
|---|---|---|
| 14.1 | 扁平 `columns: [{title,field}, …]` 被当成"每个对象一行"读 | 整张表只剩 1 列，`TableColumnCommand.Remove` 的"至少保留一列"守卫当场生效，界面上就是删不了列 |
| 14.2 | 二维表头行里再套分组对象 `[[{title, columns:[…]}]]` 只读 1 列 | 红字：`Expected table.Table.Columns to contain 2 item(s) … but found 1`；分组子列全丢 |
| 14.3 | `RenderOptions` 的 `Dpi`/`PdfRasterDpi` 无入口校验 | `UnitsPerMm` 直接除以它们，0/NaN/Infinity 会把整页换算成 0 或 NaN 尺寸（零墨或空页），且没有任何告警 |
| 14.4 | `RenderOptions.Zoom` 是从没被渲染器读过的死旋钮 | 文档里的 `Scale`/`Zoom` 都只是"写在纸上"，已删除，改由设计器把缩放折进 `Dpi` |

14.1/14.2 的修法是让三条 `columns` 写法共用一次"形状判定 + 递归展开"：扁平清单、二维行、下标映射行都走同一个 `CollectFlatColumns`，分组标题累加成一条 `hiprint.table-group-header`（Info）。14.3 把校验落在 `init` 访问器里——`record` 的 `with` 会重跑 init，所以 `new` 与 `WithDpi(...)` 走同一道闸。
**判定口径必须如实记录**：14.2 修完后，`[[分组(品名,数量)], [备注]]` 这种"两行格数不同"的形状按现有 `PlaceGrid` 归一化后列数=2、表头 3 格，子列"品名"保住表头格但不再占一列的 `field` 绑定。这是既有归一化行为，用例按实测结果断言并在 `docs/template-schema.md` 写明，没有另造跨列模型。

**B. 引擎（Engine）**

| # | 缺陷 | 实测形态 / 修法 |
|---|---|---|
| 14.5 | `ImageCache` 淘汰或 `Clear()` 时直接 `Dispose` 正在被别的线程画的 `SKImage` | 实测不是可 catch 的 `ObjectDisposedException`，而是 **`Fatal error. System.AccessViolationException`，测试宿主进程当场中止**。改为"租约 + LRU"：条目带租约计数，淘汰只退役不释放，最后一个租约归还才 `Dispose`；`PendingReleaseCount` 是可观察的欠释放账 |
| 14.6 | `ScriptRunner._prepared` 静态 AST 缓存无上限 | 常驻服务按源码字符串无限涨；改为容量 256 的 LRU，淘汰即下次重解析（AST 不可变，无正确性风险），并暴露 `PreparedCacheCount`/`ClearPreparedCache()` |
| 14.7 | 批量 PDF 导出空列表既不走渲染也不报错 | 实测产出一个 **0 字节**文件（`PageCount == 0`、连 PDF 头都没有），调用方只看到"印了 0 页"；现在写流之前抛 `ArgumentException` |

图片租约的反证口径值得留档：**断言顺序刻意"先看账再看墨"**——机制被拆时 `PendingReleaseCount` 先红，测试宿主不会被 AV 带走；早先纯墨断言的版本真把整个测试进程崩了。45 页批量 PDF → 45 个 `/Subtype /Image` XObject 一张不丢，是"租约没把图提前放掉"的护栏。

**C. 设计器（Designer）**

旋转元素命中测试、拖拽中途松键、`RowSpans` 与列增删联动、零尺寸元素不可选、追加列把元素撑出纸边（并补齐宽度/行/表头/合并单元格的撤销快照）等 10 项。其中"追加列越框"在 `TableColumnCommand.GrownWidthMm` 里按 `Math.Min(needed, Math.Max(maxWidthMm, element.WidthMm))` 收口——上限低于当前宽（表格本来就挂出纸外）时不加宽、也不缩表。

**D. 导入识别口径**

`LooksLikeHiprintJson` 早期单独判三种形状，与 `Import` 的实际判据不同源，实测 6 处分歧（`{"panels":[]}` 判真却导不进、`[5,{…}]` 判假却导得进）。现在两边共用"归一化后能收集到非空面板数组"这一条判据，`docs/04-task-plan.md:201` 的旧描述已就地更正。

**E. 样例层（宿主面板）**

用户说的"删不了列"后半段在这里：WinUI/Uno 的 `StackPanel` 与 MAUI 的横向 `StackLayout` 都不换行，而外层滚动条横向是 `Disabled`——列数一多，右侧的"删除列N"被裁在可视区外且滚不出来，界面上等于没有这个功能。改法：WinUI/Uno 用 `VariableSizedWrapGrid`（注意默认 `ItemWidth = NaN` 按**第一个**子项定格宽，而第一个按钮"追加列"是最短标签，所以先量出最宽按钮再写回 `ItemWidth`，否则长标签仍被裁）；MAUI 用 `FlexLayout` + `FlexWrap.Wrap`；Eto 的 `TableLayout` 没有换行容器，改成每行两块保持两列表格形状。WPF/WinForms 不受影响。

**F. 构建期发现、未改的两条**

- `Viewport.Dpi` 严格化后逐个核对了六个宿主的 DPI 来源：WinForms `DeviceDpi`（框架保证 ≥96）、WPF `DpiScale×96`、Avalonia `RenderScaling`、WinUI/Uno `XamlRoot.RasterizationScale`、Eto `Screen.DPI`（自身已对 ≤0 回落 96 并校验）。所有生产者结构上不可能产出 0/NaN/Infinity，**不加防御代码**，只在 `EditorCanvas` 的 `dpi` 参数上补一句"入口拒收"的说明。
- `ArtifactsPath` 指向仓库内相对路径时，测试 `DocumentedSampleTests.SamplePath` 的 8 级向上查找会够不到仓库根（输出层级被压深）。仓库文档与 CI 都不使用该参数（全仓无引用），这是本次多 agent 并行时的取证手法问题，不是产品缺陷，故不改测试。

**实测反证清单**（每条都先确认变红、再还原，仓库里已无 `MUTATION`/`if (false)` 残留）

| 拆掉的机制 | 确切红字（节选） |
|---|---|
| 二维行内分组展开 | `Expected table.Table.Columns to contain 2 item(s) because 行内分组必须展开成子列；当成一个单元格读会让整张表只剩 1 列, but found 1` |
| 图片租约（`FreeIfUnleased` 直接 Dispose） | `Expected cache.PendingReleaseCount to be 1 because 被淘汰但还欠着租约的条目只退役，不许释放, but found 0 (difference of -1)` |
| AST 缓存上限（`while (false && …)`） | `Expected ScriptRunner.PreparedCacheCount to be 256 because 超过上限必须按最近最少使用回收，而不是无限增长, but found 306 (difference of 50)` |
| `Dpi` 入口校验 | `Expected a <System.ArgumentOutOfRangeException> to be thrown, but no exception was thrown.`（5 种非法值 × new/with 两条路径，连 `PdfRasterDpi` 共 9 条红） |
| 空批次守卫 | 同上 no exception；探针实测 `PageCount=0 Length=0`（0 字节文件） |

**终态（第一轮实跑）**：`dotnet build OODD.CrossPrint.slnf` **21 个项目 0 警告 0 错误**（先清空全部 `bin/`、`obj/` 再重建）；测试 Core **260** + Engine **226** + Designer **175** + Printing **30** + Hosts.Avalonia **1** = **692** 全绿。六个样例的程序启动与点击路径仍归用户复核（本机不起 GUI）。

**G. 第二轮"继续修复"（14.8–14.12）**

| # | 缺陷 | 实测形态 / 修法 |
|---|---|---|
| 14.8 | EAN 家族护栏条与数据条同高 | ISO/IEC 15420 要求起止/中间护栏向下超出 **5 个模块**；实测 EAN-13 护栏列与数据列都是 20mm。改为护栏 = 数据高 + 5×模块宽（上限 25% 条高），总高不变；EAN-13/EAN-8/UPC-A/UPC-E 分别按 95/67/95/51 模块定位 |
| 14.9 | `JsonElement` 直接当数据源时全部字段缺失 | 反射兜底只能看到 `ValueKind`/`Length`；实测 `{{orderNo}}\|{{amount}}\|{{paid}}` 输出 `\|\|\|` 并记 3 条告警。补 Object/Array/值分支；随后按登记口径补 `JsonNode`（`JsonObject`/`JsonValue`），属性名 Ordinal 区分大小写、数字保原文 |
| 14.10 | 崩溃残留临时件永不清理 | 同名作业重跑时清"≥1 小时且能独占打开"的同名临时件；新鲜件、别的输出名、被占用件一律不动——并发作业正在写的文件拿不到独占句柄 |
| 14.11 | WPF 样例复制无人读取的 `script-demo.template.json` | 从 `samples/CrossPrint.Samples.Wpf/CrossPrint.Samples.Wpf.csproj` 摘掉；清重建后输出目录只剩 kitchen-sink / complex-receipt / hiprint-sample.js |
| 14.12 | 样例数据契约只有文档、没有断言 | 核对 README 与六个宿主的 `SampleData` 均提供 `title`/`orderNo`/`sku`；既有 `TemplateRendererTests.文档样例整页渲染零失败告警` 用同一组字段渲染并断言零绘制告警，登记项关闭 |

反证（同样先确认变红再还原，仓库里已无 `MUTATION` 残留）：

| 拆掉的机制 | 确切红字（节选） |
|---|---|
| EAN 护栏延长 | `Expected data.LengthMm to approximate 15.0 +/- 0.3 because 数据条比护栏短 5 个模块（1mm/模块）, but 20.0 differed by 5.0.` |
| `JsonObject` 分支 | `Expected text to be "CP-2026-0001\|19.90\|True\|" …, but "\|\|\|" has a length of 3` |
| `JsonNode` 文本分支 | `Expected text to be "CP-2026-0001\|19.90\|True\|", but "CP-2026-0001\|19.90\|true\|" differs near "tru"` |
| 陈旧临时件清理调用 | `Expected File.Exists(stale) to be False because 陈旧残留必须跟着同名作业一起清掉, but found True.` |

**终态（第二轮实跑）**：清空全部 `bin/`、`obj/` 后 `dotnet build OODD.CrossPrint.slnf` **21 个项目 0 警告 0 错误**；测试 Core **260** + Engine **241** + Designer **175** + Printing **33** + Hosts.Avalonia **1** = **710** 全绿。六个样例的程序启动与点击路径仍归用户复核（本机不起 GUI）。

## 3. 验收矩阵（对照需求 §6）

| 需求条目 | 验收任务 | 实证方式 |
|---|---|---|
| FR-ENG-05 同源一致 | 2.7 + 3.6 | 逐元素跨 DPI 毫米墨迹包围盒 + 相机/叠加层像素重合（96/144 两档）+ 用户叠图 |
| FR-HST-01..03 | 4.1-4.3 | 编译+无头冒烟 PNG；交互归用户 |
| FR-HST-04..06 | 5.1-5.3 | 编译；运行归真机清单 |
| FR-BND-04 | 6.3 | 自动化 PDF 页数（100 条→100 页）+ `/FontFile2 == 1` |
| FR-DES-12/13 纸张与右键菜单 | 8.1–8.3 | `KernelFeatureTests`（含关机制反证）+ 六样例编译；交互归用户 |
| FR-DES-05/13 属性通道与公共外观 | 9.2–9.4、10.1/10.4 | `ElementBoxStyleTests`（像素 + 关机制反证）+ `ElementPropertiesTests` 完备性门 + `SkiaDrawOperationTests`；交互归用户 |
| hiprint 模板通用导入 | 10.2/10.5 | `HiprintImporterTests`：4 份真实夹具 + 合成场景，多面板/连续纸/隐藏列/多行表头/问题清单；归一化机制各有关机制反证 |
| hiprint 语义渲染一致 | 10.3/10.5 | `HiprintSemanticsRendererTests` 12 条像素取证，每条配关机制对照；两个真实渲染缺陷（分边边框、表头网格偏移）由此发现并修复 |
| hiprint 导入入口进六宿主 | 11.1–11.3 | 六样例编译 0 警告 0 错误 + `DocumentedSampleTests` 锁住随附示例真能导入；菜单点击与文件对话框归用户 |
| hiprint 排版口径（字号/行距/基线/裁剪） | 12.1–12.6 | computed style 实测 + `HiprintImporterTests`/`TextAndShapeRendererTests` 像素与几何断言，各配关机制反证 |
| hiprint 表格兼容与码制占位 | 13.1–13.5 | `HiprintImporterTests` 四条表格形态、`CodeRendererTests` 空内容占位（暗像素 0/非白 >0）、`DataBinderTests` 设计数据优先级、`EditorCanvasTests` 无宿主数据仍出真条码；每条机制均有"关掉必红"实测 |
| NFR-01 性能 | 2.8 | Stopwatch 基准断言 |
| NFR-04 Trim | 7 | CI + 本机 partial trim |

## 4. 真机/人工复核清单（交付时随 README 提交）

1. 三宿主 Sample：拖放 8 元素全链路与画布-PDF 叠图对比（Avalonia/WinForms/WPF）。
2. WinUI/Eto/Uno Sample 运行冒烟（代理只做到编译与无头验证，GUI 启动与手感复核归用户）。
3. 150% DPI 屏拖动落点误差 ≤0.5mm。
4. 真实打印机直打（v0.2 后）。

## 5. 交付节奏与并行策略

- 批次 A：M0+M1+M2（串行，同仓不可抢 obj/）。
- 批次 B：M3 与宿主适配层脚手架可并行（不同目录）。
- 批次 C：M4 ∥ M5（不同 csproj，互不触碰）。
- 批次 D：M6+M7 收尾。
- 每批次结束：全量 `build slnf` + `dotnet test` 后再开下一批；清单外发现的缺陷只登记到 §7 不顺手修。

## 6. 风险登记

| 风险 | 等级 | 缓解 |
|---|---|---|
| Avalonia `dc→SKCanvas` 转换在 11.3.2 的确切 API（`SkiaSharpCanvas`） | 中 | M4 首日 spike 验证；失败则退 WriteableBitmap 路线（仍同源引擎，仅多一次拷贝） |
| Uno 版本线与仓库 SkiaSharp 锁冲突 | 中 | 实测 Uno 5.6.54 + net8.0-desktop 可编译且解析 SkiaSharp 2.88.9；Uno 6.7（SkiaSharp 3.119 + net9）暂不跟进，README 如实标注 |
| Uno 包 TFM 为 `net8.0-desktop1.0` | 低 | 仅供 Uno 5.6 desktop 工程消费；Uno 6.x 用户需自行升级适配层（已登记 docs/03 坑位表） |
| Eto macOS `BitmapData` 桥 | 低 | 本机 Windows 先证 Windows 平台；mac 归 CI |
| QRCoder 4.154 与 PRD 1.4.0 版本选择 | 低 | 用源格式 `ByteMatrix` 自绘，与包版本解耦 |
| WinUI 示例需 WindowsAppSDK 运行时/VS 组件 | 中 | 库项目照常编译；示例运行归用户环境 |

## 7. 缺陷/待办登记簿

（开发中发现但不属于当前里程碑的问题追加于此，只登记不顺手修）

- [x] `.tmp/pkgprobe` / `.tmp/propscheck` 等临时脚手架已清理（`rmdir .tmp` 成功，仓库根无 `.tmp`）。
- [x] **M10 实测发现并修复**：`BorderSides` 为单边/部分边时边框从不绘制（`TryBuildSidesPath` 末尾未选中的边重置了标志）；多行表头网格线画在页面顶部而不是元素 `yMm`；样张表格与二维码元素重叠导致逐元素墨迹窗口判据失真（表格下移 8mm）。三者都有对应测试或文档同步。

### M3 期间实测发现、不属于本里程碑的登记项

- [x] **表格列增删没有命令**（M14 前已补齐）：`InteractionCommands.cs` 的 `TableColumnCommand`/`TableListItemCommand<T>` 覆盖列与三类规则列表的增删，`EditorKernel.RunElementAction` 按动作键派发，面板按钮行由 `ElementProperties.TableActions` 声明，`TableColumnTests` 逐用例钉住联动与撤销。
- [ ] **设计器无对齐辅助线/吸附**。任务 3.6 只到"网格 + 标尺"，拖动时没有元素间对齐线，也没有网格吸附。属 P1 体验项，不是缺陷；实现时吸附必须在命令里存最终落点（不能是渲染期修正）。
- [ ] **属性面板的多选批量编辑**未实现（`SetSelectedProperty` 明确只服务单选）。对齐/分布/统一字号这类批量操作应以独立命令补齐，而不是放宽 `SelectedProperties` 的单一类型约束。

### M2 期间实测发现、不属于本里程碑的登记项

- [x] **PRD-GAP｜EAN 家族护栏条未拉长**（M14 已修）：`CodeRenderers.cs` 按 ISO/IEC 15420
      （原登记误写 13653）把 EAN-13/EAN-8/UPC-A/UPC-E 的起止/中间护栏向下延长 5 个模块
      （模块宽 × 5，上限条高 25%），数据条相应缩短、总高不变。`BarcodeGuardBarTests` 用逐列墨迹
      实测护栏 20mm / 数据 15mm（模块 1mm、条高 20mm），并以 Code128 同高做正对照。
      反证：去掉延长后 `Expected data.LengthMm to approximate 15.0 +/- 0.3 …, but 20.0 differed by 5.0.`
- [x] **PRD-GAP｜`JsonElement`/`JsonNode` 不能直接当数据源**（M14 已修）：`DataBinder` 显式处理
      `JsonElement`（Object/Array）与 `JsonNode`（`JsonObject`/`JsonValue`），属性名按 JSON 语义
      Ordinal 区分大小写，数字保留原文（`19.90` 不缩成 `19.9`）。`DataBinderTests` 各 4 条覆盖值/嵌套/
      展开/缺字段。反证：拆掉 `JsonObject` 分支 → `Expected text to be "CP-2026-0001|19.90|True|" … but "|||"`；
      拆掉 `JsonNode` 文本分支 → 布尔退化成小写 `true`。
- [ ] **PRD-GAP｜二维码静区不自动补足**。规范要求静区 ≥ 4 倍模块，实现照用户给的 `quietZoneMm` 画
      （`CodeRenderers.cs:54-66`）。实测 40mm/300DPI/2mm 静区能解回，低档扫描枪与远焦距未验证。
      已在 `docs/template-schema.md` §4.5 写明差距，不再宣称自动取大值。
- [x] **仓库卫生**：`probe-zx/` 取证脚手架已从仓库根摘除（M14 复核时根目录已无该目录）；文档里的"探针 §NN"引用作为结论出处保留，实测数据已抄录在各里程碑交付记录里。
- [x] **样例数据契约**（M14 已核）：`kitchen-sink.template.json` 的 `{{title}}`/`{{orderNo}}`/`{{sku}}`
      在 README 的 `Data = new { … }` 示例与六个宿主的 `SampleData` 里都给了；引擎侧
      `TemplateRendererTests.文档样例整页渲染零失败告警` 用同一组字段渲染并断言除字体回退/越界外零告警，
      新增占位符漏配数据会直接红。

### M14 期间实测发现、不属于本里程碑的登记项

- [ ] **WinUI/Uno/Maui 三宿主的"松开哪个键"拿不到**。WPF/Avalonia/WinForms/Eto 在释放事件参数里能读到
      本次变化的按键（`e.ChangedButton`/`e.Button`/`released`），拖拽中途松开别的键不会误收尾；
      WinUI 系的 `PointerRoutedEventArgs` 只有 `Properties.IsLeftButtonPressed` 这类"当前还按着哪些键"，
      没有"刚松开哪个"。补法：释放时按"该键是否仍在按下集合里"判定收尾，需要真机验证按键组合，
      本机无法实证，故先登记不改（改错的代价是把正常拖拽判成没结束）。
- [ ] **Avalonia 宿主在渲染线程写内核状态**。`DesignerControl.DrawFrame`（及 `PreviewControl`）在合成器线程写
      `Viewport.Dpi`、视图尺寸、`FitToPage`、`LastReport` 并触发 `Rendered`，其余五宿主都在 UI 线程画。
      与 UI 线程的指针事件并发读写内核即竞态。补法：像 Eto 宿主那样在 UI 线程 `EnsureFrame` 预渲染到
      `SKBitmap` 再贴，代价是多一次拷贝与一条"渲染完成后补失效"的链路。
- [ ] **条件/关键词样式的三态布尔在面板里压不成三态**。`ElementProperties` 的 `bold`/`italic` 写入
      `... is true ? true : null`，取消勾选一律落 `null`（继承样式库），显式 `false`（强制不加粗）无法从面板表达。
      引擎区分 `null` 与 `false`（`TableRenderer` 的级联）。补法：这三态要么在面板上换成
      "继承/是/否"三选下拉（七个样例都得同步），要么在文档里明确"面板只能设 true 或继承"。
- [x] **hiprint 分组表头嵌在二维行里仍丢列**（M14 已修）：`CollectFlatColumns` 递归展开分组对象，
      分组标题累加成一条 `hiprint.table-group-header`（Info）。"两行格数不同"的归一化结果按实测写进
      `docs/template-schema.md`：保头格、不保 `field` 绑定，不另造跨列模型。见 §M14 A / 14.2。
- [x] **`ImageCache.Shared` 淘汰即 Dispose**（M14 已修）：租约 + LRU，退役条目等最后一个租约归还才释放，
      `PendingReleaseCount` 可从外部观察。反证与 45 页 PDF 的 XObject 计数见 §M14 B / 14.5。
- [x] **`ScriptRunner._prepared` 静态 AST 缓存无上限**（M14 已修）：容量 256 的 LRU，
      淘汰即下次重解析；`PreparedCacheCount`/`ClearPreparedCache()` 供宿主收内存。见 §M14 B / 14.6。
- [x] **`RenderOptions` 的 `Dpi`/`PdfRasterDpi` 无入口校验、`Zoom` 是死旋钮**（M14 已修）：校验落在 init
      访问器上，`new` 与 `with` 共用一道闸；从未被渲染器读过的 `Zoom`/`WithZoom` 已删除。见 §M14 A / 14.3、14.4。
- [x] **批量 PDF 空列表产出 0 字节文件**（M14 已修）：`BatchPdfExporter.Export/ExportPages` 在写流前
      抛 `ArgumentException`，流保持空。见 §M14 B / 14.7。
- [ ] **Eto 宿主直写位图未 `Canvas.Flush()`**（`Hosts.Eto/DesignerControl.cs:385`、`PreviewControl.cs:278`），
      依赖 Skia 光栅后端的即时写入；WinForms 基类有 `Flush`。低风险，但口径应与 WinForms 一致。
- [x] **`PdfFilePrintTarget` 崩溃残留临时文件**（M14 已修）：同名作业重跑时清 `{名}.crossprint-*.tmp{扩展名}`，
      只删"够旧（1 小时）"且"能独占打开"的文件——并发作业正在写的临时件拿不到独占句柄，宁留勿删。
      反证：去掉清理调用 → `Expected File.Exists(stale) to be False … but found True.`；新鲜件/别的输出名残留/被占用件三条守边界。
- [x] **样例死资源**（M14 已修）：`samples/CrossPrint.Samples.Wpf.csproj` 已摘掉 `script-demo.template.json`
      的 `<None Include>`（全仓无读取入口，其余宿主也没有这条）；`docs/samples/script-demo.template.json` 与截图保留作文档素材。
