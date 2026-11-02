# 渲染规范：坐标系、单位与 DPI

本文档约束 `CrossPrint.Core` 渲染引擎的一切数值换算。**"所见即所印"成立与否，完全取决于这里的规定是否被严格遵守。**

---

## 1. 分层单位模型

| 层 | 单位 | 出现位置 | 不变式 |
|---|---|---|---|
| 模板（存储） | **毫米 mm** | `xMm` / `yMm` / `widthMm` / `heightMm` / `strokeWidthMm` / `quietZoneMm` … | 与设备无关，永不含像素 |
| 模板（字体） | **磅 pt** | `fontSizePt` | 1 pt = 1/72 英寸，印刷业定义 |
| 渲染（瞬时） | **画布单位** | `SKCanvas` 调用参数 | 由 DPI 从 mm/pt 换算而来，不落盘 |

换算公式（`UnitConverter`，`src/CrossPrint.Core/Units/PaperSpec.cs`）：

```
px  = mm / 25.4 * dpi
px  = pt / 72  * dpi
mm  = pt / 72  * 25.4        // 与 dpi 无关
```

`MmPerInch = 25.4`、`PtPerInch = 72.0` 是全仓唯一常量源。任何地方写 `25.4` 或 `72` 字面量都是缺陷。

---

## 2. 坐标系

- 原点在**页面左上角**，x 向右、y 向下（SkiaSharp 原生方向，不做翻转）。
- 元素坐标相对**页面左上角**，不是相对 margin 内区。
  margin 只用于：设计器安全区虚线提示、物理打印机边距参考。**引擎在计算元素位置时完全不使用 margin。**
- 元素旋转（`rotationDegrees`）以**元素中心**为锚点、顺时针为正（与 CSS `rotate()` 同向），不改变 `xMm/yMm/widthMm/heightMm` 的语义——
  命中框与属性面板仍按未旋转的包围盒工作，只有绘制期做旋转。旋转与不透明度同属公共绘制路径，背景、内容、边框一起转、一起合成。

---

## 3. 引擎入口的单一缩放

`TemplateRenderer` 在每个页面绘制前施加一次

```csharp
canvas.Scale((float)ctx.UnitsPerMm);   // UnitsPerMm = Dpi / 25.4
```

此后**所有元素渲染器一律以毫米作图**：`drawRect(xMm, yMm, widthMm, heightMm)`。
渲染器内部禁止出现任何 `* dpi` / `/ 25.4` 换算——那是入口的职责。这条规则是"设计器与 PDF 同源"的技术保证：同一份渲染器代码，入口缩放不同而已。

### 3.1 字号为什么必须转成 mm

`SKFont.Size` 会被当前 CTM 再次缩放。引擎入口已经 `Scale(UnitsPerMm)`，所以：

```csharp
font.Size = (float)ctx.PtToUnits(fontSizePt);   // = UnitConverter.PtToMm(pt)
```

`PtToUnits` 返回**毫米值**，不是像素值。给它像素值会造成双重放大（此 bug 曾真实出现过，由
`UnitAndPaperTests.RenderContext_font_size_is_expressed_in_mm_space` 锁定：72pt 必须得到 25.4mm）。

线宽、圆角、虚线段同理：模板里已经是 mm，直接传给 `SKPaint.StrokeWidth`（mm 空间下的宽度）。

### 3.2 行盒基线（为什么不用 ascent 直接顶格）

多行文本按 CSS 行盒模型落基线，而不是"每行从行顶画起"：字号内容区（ascent+descent）在行高里居中，
上下各分一半 leading，基线落在内容区底部往上 `AscentMm` 处——即
`TextStyle.BaselineOffset(lineHeight) = (lineHeight − ascent − descent) / 2 + ascent`。
行高小于内容区时 leading 为负，基线上移。hiprint 的固定 9.75pt 行高配 13pt 字号就是这种负 leading 场景，
按"顶格 + ascent"画会让整行比浏览器里低约 1.7mm。文本、二维码标题、一维码可读文本、表格单元格共用这一个入口。

文本元素**不按元素框裁剪**：hiprint 的文本没有 `overflow:hidden`，标点下沿和字形降部照常打印
（实测 13pt 表头的"、"整个落在元素框下沿之外）。图片、表格仍按元素框裁剪。

---

## 4. 目标介质的 DPI

| 场景 | `RenderOptions.Dpi` | 画布 | 说明 |
|---|---|---|---|
| 设计器预览 | 96 | 位图 | 屏幕密度；布局必须与 300 DPI 结果在 mm 上一致 |
| PNG/JPG 导出 | 用户指定（默认 300） | 位图 | `px = mm / 25.4 * dpi` |
| PDF 导出 | **72（固定）** | `SKDocument` 页面 | 见 §5 |

### 4.1 DPI 无关性（快照测试的核心断言）

同一模板在 96 DPI 与 300 DPI 下渲染，任意元素的**毫米边界**必须一致：

```
elemXmm(pixelX) = pixelX / dpi * 25.4
```

即两种 DPI 的渲染结果只差一个均匀缩放因子。允许抗锯齿造成的像素级差异，**不允许位置/尺寸漂移**。
该性质由两条测试固化：
`ExporterTests.同一模板在不同密度下毫米位置一致`（单元素）与
`TemplateRendererTests.样张模板逐元素在两种密度下毫米位置一致`（`docs/samples/kitchen-sink.template.json`
八类元素逐个比对包围盒，并带一条"180–250mm 留白带必须一个非白像素都没有"的反向对照，
防止判据本身被背景噪声喂饱）。

> 快照基线的选型：仓库**不提交栅格 PNG 基线**。位图差多少取决于这台机器装了哪些字体、
> Skia 怎么 hinting、抗锯齿开关——换台机器（或换到 Linux）就飘，那种基线只能天天红灯。
> 提交的是同一份**输入**样张 JSON，加上在 mm 空间成立的判据、逐模块网格比对与 ZXing 解回，
> 三条都不依赖环境。原先计划的 `tests/Baselines/` 目录因此取消。

---

## 5. PDF 输出（实测结论，勿凭直觉改写）

`SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { ... })` 的行为，在 SkiaSharp 2.88.9 上实测：

1. **页面尺寸单位是 PostScript point（1/72 英寸）**。`BeginPage(doc, width, height)` 收到的宽高直接成为 MediaBox。
2. `SKDocument.CreatePdf(Stream, ...)` 上并不存在"按 300 DPI 建页"的语义——传 DPI 参数**不改变 MediaBox**。
   早期 PRD 里"PDF 内部按 300 DPI 创建页面"的说法是错的，已按实测更正。
3. 因此 PDF 路径：
   - 页面尺寸：`SKDocument.CreatePdf` 的 `BeginPage` 用 `UnitConverter.MmToPt(mm)`；
   - 画布缩放：`UnitsPerMm = 72 / 25.4`，即入口缩放换成"每毫米 72/25.4 点"；
   - 文字与线条是**矢量**，与 DPI 无关，高精度打印机上按 mm 复现，不存在"300 DPI 才清晰"的问题；
   - 元素全部以矢量落地：矩形/圆/线是 PDF 路径指令，二维码与一维码是逐模块 `DrawRect`，
     只有 `image` 元素带位图，其重采样密度由 `SKDocumentPdfMetadata.RasterDpi` = `RenderOptions.PdfRasterDpi` 控制。
4. **页面网格精度**：`PdfRasterDpi` 默认 **2540**（不是 300），实测关系为
   `MediaBox = round(mm × rasterDpi / 25.4) × 72 / rasterDpi`，即页框落在 **0.01mm** 网格上。
   取证见 `ExporterTests.PDF页面毫米精度落在0_01mm网格上`（210×297 / 100×150 / 58×90 三档都验）。
5. **字体是整份内嵌的，SkiaSharp 2.88.9 不做子集化**（探针 §23 实测，50 个中文元素 A4，Release）：

   | 模式 | 中位耗时 | 产物体积 |
   |---|---|---|
   | 矢量 + 雅黑（默认） | 739ms | 12,168KB |
   | 轮廓 + 雅黑（`PdfTextAsOutlines`） | 15ms | 75KB |
   | 矢量 + Arial | 44ms | 549KB |

   同一段文字栅格成 300 DPI 位图只要 15ms。所以 NFR-01 的 200ms 预算测的是**渲染吞吐**，
   用西文向量版面（`ExporterTests.五十元素A4导出PDF的耗时符合预算`，实测 44ms）；
   中文版面多出来的近 700ms 与 12MB 全是"把整份字库塞进文件"的代价，与元素数量无关，
   归档场景走 `PdfTextAsOutlines`（`ExporterTests.中日韩矢量文本的代价是字体内嵌不是排版` 钉住这个归因）。
6. **字体流是文档级对象，多页只嵌一次**（探针 §24 + 引擎实测）：1 页 12,459,936B → 5 页 12,461,985B，
   `/FontFile2` 都只出现 1 次，4 个额外页合计 2,049B（约 512B/页）。
   这是 M6"100 条记录 → 100 页"成立的前提；取证见 `ExporterTests.批量多页PDF只嵌一次字体流`。
7. **码制绘制一律关抗锯齿**（探针 §21 实测）：40mm 二维码在 300 DPI 下开 AA，模块边缘变灰，
   **连 ZXing 自己的读码器都返回 null**（不是抛 `NotFoundException`），同一张图关掉 AA 立刻解回。
   等于印出一张"看着像二维码但扫不出来"的面单，所以 `Paints.Fill(..., antialias: false)` 是给码制的硬约束。
8. **字体族存在性有两个口径**（探针 §22 实测）：中文 Windows 的 `FontFamilies` 把雅黑列成"微软雅黑"，
   而按名匹配时 Skia 用的是字体内部英文名 "Microsoft YaHei"；`FamilyExists` 两个都得认，
   否则装得好好的字体会被报成"不可用"，`FontFallback` 告警通道当场变成噪音。
9. **批量 PDF 的输入下限是 1 页**：空模板列表过去既不走渲染也不报错，实测产出一个 **0 字节**文件
   （返回值 `PageCount == 0`、流里连 PDF 头都没有），调用方拿到"印了 0 页"只能回头猜原因。
   `BatchPdfExporter.Export` / `ExportPages` 现在在写流之前抛 `ArgumentException`，
   由 `ExporterTests.批量导出空列表在入口拒绝且不留下半截文件` 钉住（拆掉守卫时该用例红）。
10. **EAN/UPC 护栏条比数据条长 5 个模块**（ISO/IEC 15420；模块区间按 ZXing 实际序列实测）：
    EAN-13/UPC-A 是 95 模块、EAN-8 是 67、UPC-E 是 51，起止/中间护栏分别落在 `101`、
    `01010`（UPC-E 结束护栏 `010101`）上。护栏高 = 数据高 + 5×模块宽（上限条高的 25%），
    总高不变，可读文本位置不受影响；模块宽随元素宽缩放，不写死毫米。
    `BarcodeGuardBarTests` 用逐列墨迹高度钉住四种码制，并以 Code128"所有条同高"作正对照。

验证锚点：A4（210×297mm）的 MediaBox 必须是 `595.28 × 841.89` pt。该断言在
`UnitAndPaperTests.A4_maps_to_the_standard_pdf_media_box`。

---

## 6. 绘制顺序

1. 解析页面尺寸：应用 `orientation`（Landscape 交换宽高，但连续纸 `heightMm = 0` **不交换**——0 是"长度自适应"而不是一个可旋转的维度，交换后纸宽会变成 0，页面底色与越界检查都失效）；连续纸（`heightMm = 0`）页高恒按内容算
   （`max(内容底边, 下留白) + 2mm`，实测 40mm 底边 + 5mm 留白 → 42mm）——连续纸没有"固定页高"可回退，
   所以这里**没有任何开关**，原先设想的 `AutoPageHeight` 因是空旋钮已删除。
2. 填充页面背景 `PageBackgroundColor`。
3. 元素按 `zIndex` 升序；同 `zIndex` 按数组顺序。
4. 每个元素分派到 `IElementRenderer`（按 `type` 键注册的策略）。
5. 单个元素内部固定次序：`Save` →（`opacity < 1` 时 `SaveLayer`）→ 绕元素中心旋转 → **背景** → 内容 → **边框**（按 `borderSides` 逐边）→ `Restore`。
   背景不盖内容、内容不盖边框；旋转/不透明度对三者同时生效。
6. 单个元素抛出的任何异常被捕获，记 `RenderIssueKind.ElementSkipped`，继续画下一个元素。

**引擎对合法模板永不抛异常。** 降级路径见 §7。

---

## 7. 降级规则（用户数据问题 → `RenderReport`，不是异常）

| 情况 | 行为 | `RenderIssueKind` |
|---|---|---|
| 字体族不存在 | `SKFontManager.MatchCharacter(family, weight, width, slant, null, probeChar)` 回退；`probeChar` 用文本首字符，保证中文模板落到真正含汉字字形、而非只能画西文的字体 | `FontFallback` |
| 图片 Base64 非法 / 解码失败 | 画红色虚线框 + `IMG ERR` 文字 | `ImageDecodeFailed` |
| 图片数据缺失 | 画灰色占位框 | `ImageMissing` |
| 二维码内容为空（绑定后是空串/空白） | 不进 ZXing：画浅灰虚线占位框 + 中灰"缺少数据 {{表达式}}"，同时记告警 | `QrCodeFailed` |
| 一维码内容为空 | 同上。空串交给 Code128 会编码出 24 模块的图案（实测），拉宽后就是几块纯黑砖；必须挡在编码器之前 | `BarcodeFailed` |
| 二维码内容无法编码 | 占位框 + 提示 | `QrCodeFailed` |
| 一维码不符合码制约束 | 占位框 + 提示 | `BarcodeFailed` |
| `type` 没有注册渲染器 | 跳过该元素 | `RendererMissing` |
| 渲染元素时抛异常 | 跳过该元素 | `ElementSkipped` |
| 绑定路径在数据里不存在 | 输出空串 | `BindingMissingField` |
| 表格超出元素高度 | 裁切 | `TableOverflow` |
| 元素越出页面 | 照常绘制（不裁） | `ElementOutOfBounds` |

异常语义的边界（`docs/03-development.md` 第 6 条）：
**编程错误 → 抛异常**（如 `TemplateJson` 收到缺 `schemaVersion` 的模板）；
**用户数据问题 → 进 `RenderReport`**（模板是用户资产，一张坏图不能让整页渲染失败）。

---

## 8. 资源生命周期

`SKPaint`、`SKFont`、`SKPath`、`SKBitmap`、`SKImage`、`SKCanvas`、`SKDocument`、`SKTypeface` 都是 `IDisposable`，
渲染引擎内部一律 `using`，不依赖 GC 终结器。
例外：`SKTypeface` 由 `DefaultFontResolver` 的字典持有并缓存（跨绘制帧复用），进程生命周期内不释放；
`SKFontManager.Default` 同理。

### 8.1 图片解码缓存：淘汰 ≠ 释放（租约纪律）

解码结果进进程级 LRU 缓存（`ImageCache`，上限 32 张，按张数不按字节）。**取出的 `SKImage` 是租约（lease），用完要还**：

- 有租约在手的条目被淘汰时只"退役"（离开索引与 LRU），最后一个租约归还时才真正 `Dispose`；
  索引上限始终守住，"欠释放"的条目数由 `ImageCache.PendingReleaseCount` 报出来，随归还回落。
- 为什么不是"摘掉就 Dispose"：实测（SkiaSharp 2.88.9）动一个已释放的 `SKImage`——连 `SKImage.Width` 都算——
  不是能 catch 的 `ObjectDisposedException`，而是 `AccessViolationException`，测试宿主进程直接崩，
  对应到线上就是常驻打印服务当场没了。并发批量导出里另一个线程刚把同一个引用拿到手，就是这个窗口。
- 录制型介质（PDF/`SKPicture`）另有一条实测口径：录制对象自己持有原生引用，托管包装先被放掉仍回放得出墨
  （40 张不同图片录进 picture、缓存被挤爆后回放，第一格照样有墨；45 页批量 PDF → 45 个图片 XObject，一张不丢，
  两条都在 `ImageCacheLeaseTests` 里钉着）。生命周期不押在 Skia 的内部实现上：`PdfExporter`/`BatchPdfExporter` 给渲染上下文挂租约袋（`ImageLeaseBag`），
  页面录制结束、`SKCanvas` 释放之后才统一归还，峰值钉在"一页取用的图"，不随页数涨。
- 即时绘制且没有袋的路径（`RenderToBitmap`、设计器画布、宿主自己调 `TemplateRenderer.Render`）
  按"元素画完即还"处理，缓存峰值与淘汰纪律不变。
- `ImageCache.Clear()` 同样受这条纪律约束：它只摘账，在用的条目等归还时才释放。

### 8.2 预编译脚本缓存：有上限的进程级 LRU

`ScriptRunner` 按脚本源码字符串缓存 Jint 的 `Prepared<Script>` AST，跨引擎实例、跨线程复用，
上限 256 条（`PreparedCacheCapacity`），超出按最近最少使用回收。
上限存在的理由不是性能而是内存：常驻打印服务加载的是"很多模板"，每个模板、每个租户的脚本源码都不同，
无上限就是无上限涨且谁也无法释放。AST 不可变，所以回收没有正确性风险，代价只是下一次重解析；
宿主可在空闲时调 `ScriptRunner.ClearPreparedCache()` 主动收回，`PreparedCacheCount` 读当前条数。

---

## 9. 设计器与导出的同源链路

```
                    ┌── 设计器预览：SKCanvas(Avalonia lease / SKControl / SKElement / WriteableBitmap)
PrintTemplate ──► TemplateRenderer ──┤
                    └── PDF 导出：    SKCanvas(SKDocument.BeginPage)
```

两条分支调用**同一个** `TemplateRenderer.Render`，只有入口画布与 `UnitsPerMm` 不同。
任何"设计器用宿主控件画元素"的实现都会破坏这个等式，属于架构违规（见 `docs/02-architecture.md` 铁律 4）。

设计器与打印预览在这条链路之前多做一步：`DataBinder.WithDesignData(宿主数据, 模板.designData)`
——宿主字段优先，模板里的样例值只补缺。PDF 导出与打印目标不合并设计数据：
样例值是给版面预览看的，印到真实单据上就是错数据。
