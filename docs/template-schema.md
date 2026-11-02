# 模板 JSON 规范（schemaVersion 0.1）

本文档是 `CrossPrint.Core` 序列化实现的**权威字段说明**，与代码保持一致由
`tests/CrossPrint.Core.Tests` 的往返测试保证；hiprint 导入口径由
`tests/CrossPrint.Core.Tests/HiprintImporterTests.cs` 与 `tests/CrossPrint.Engine.Tests/HiprintSemanticsRendererTests.cs` 双端钉住。
任何字段变更必须同时改 `docs/template-schema.md` 与测试基线。

---

## 1. 设计约定

| 约定 | 内容 |
|---|---|
| 几何单位 | **毫米（mm）**，浮点数。模板里不出现像素。 |
| 字体单位 | **磅（pt）**，字段名一律以 `Pt` 结尾。 |
| 颜色 | 十六进制字符串：`#RRGGBB`（不透明）或 `#AARRGGBB`（透明在前，CSS 惯例）。 |
| 枚举 | **字符串**，首字母大写的 .NET 成员名，如 `"Portrait"`、`"Horizontal"`、`"Center"`。 |
| 命名 | 全部 camelCase。 |
| 原点 | 页面**左上角**，x 向右、y 向下。 |
| 未知字段 | **忽略**（向前兼容）。缺失可选字段 → 取默认值。 |
| 缺失必填字段 | 抛 `TemplateValidationException`，`ElementId` + `FieldName` 可定位。 |
| 坐标基准 | 元素坐标相对**页面左上角**，不是相对 margin 内区。margin 只作安全区提示与物理边距参考。 |

字段名后缀是硬约束（见 `docs/03-development.md` 命名规范）：`Mm` 结尾 = 毫米，`Pt` 结尾 = 磅，`Hex`/颜色属性 = `SKColor`。

---

## 2. 顶层结构

```jsonc
{
  "schemaVersion": "0.1",     // 必填。主版本不匹配 → 异常；次版本高于本实现 → 异常
  "paper": { ... },           // 必填（兼容旧字段名 "page"）
  "margins": { ... },         // 可选，默认四边 10mm（兼容旧字段名 "marginMm"）
  "designData": { ... },      // 可选，设计/预览态兜底数据（路径 → 样例值），见 §2.5
  "elements": [ ... ]         // 可选，默认空数组
}
```

### 2.1 `paper`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `preset` | string \| null | `null` | 纸张预设名，见 §2.3。写非法名称 → 异常；`"Custom"` 合法。 |
| `widthMm` | number | `210` | **纵向存储**的纸宽。必须 > 0。 |
| `heightMm` | number | `297` | **纵向存储**的纸高。`0` 表示连续纸（小票）。 |
| `orientation` | string | `"Portrait"` | `Portrait` / `Landscape`。横向时渲染引擎交换宽高；连续纸（`heightMm = 0`）不交换，宽度恒为 `widthMm`（0 是自适应长度，不是可旋转的维度）。 |

> `widthMm`/`heightMm` 与 `preset` 同时出现时，**显式宽高优先**，`preset` 只作标签保留（往返序列化不丢）。

### 2.2 `margins`

`top` / `right` / `bottom` / `left`，均为 mm，默认 `10`。不参与元素坐标计算。

### 2.3 纸张预设

| 名称 | 宽 × 高（mm） |
|---|---|
| `A3` | 297 × 420 |
| `A4` | 210 × 297 |
| `A5` | 148 × 210 |
| `B5` | 176 × 250 |
| `Receipt80` | 80 × 0（连续） |
| `Receipt58` | 58 × 0（连续） |
| `Label100x150` | 100 × 150 |
| `Custom` | 由 `widthMm`/`heightMm` 决定 |

### 2.4 连续纸

`heightMm = 0` 时页高在渲染期按内容计算：
`页高 = max(margins.bottom, 所有元素最大底边) + 2mm`。
连续纸没有"固定页高"可回退，所以**逐页自适应是恒为真的行为，没有开关**
（曾设想的 `RenderOptions.AutoPageHeight` 因取了值也不起作用，已按"禁止假功能"删除）。

### 2.5 `designData`

设计态兜底数据：**扁平路径 → 字符串值**。设计器/预览渲染时如果没有宿主数据，
`{{barcode}}` 这类占位符就从这里取值，而不是绑成空串（空串会让条码画成一截黑砖、文本整块消失）。

```jsonc
"designData": { "order.no": "CP-2026", "barcode": "8801234567890", "items[0].sku": "A-1" }
```

| 规则 | 说明 |
|---|---|
| 键 | 按 `.` 钻取、`[下标]` 索引的路径，与 §5 占位符同一套解析规则；首尾空白剥掉，空键忽略。 |
| 下标上限 | 列表只补齐到 **下标 512**（`PrintTemplate.MaxDesignListIndex`）：超过上限的键整条跳过、不扩容也不写入，绑定按未命中走"缺失字段"回落。畸形 `designData`（如 `items[2000000]`）否则会让设计器/预览每次重绘都分配百万级列表。 |
| 值 | 只承载标量：字符串原样；数字取原文（`2` → `"2"`）；布尔 `true`/`false` 转小写文本。对象、数组、显式 `null` **不写入**（设计数据不表达结构，结构由路径自身表达）。 |
| 合并 | 宿主显式传入的数据优先：设计数据只补宿主持有数据里缺失的根字段，宿主字段值为 `null` 也算"有"，不回落。 |
| 序列化 | 只在非空时写出；加载器对非对象形态直接忽略（不报错）。 |
| 用途 | 只有 `EditorCanvas.Draw`（设计器与打印预览）会合并它；`TemplateRenderer`、PDF 导出与打印链路不读 `designData`，那些通路必须传真实数据，样例值不会被印到单子上。 |

---

## 3. 元素公共字段

每个元素对象都带下列字段，`type` 决定其下**同名载荷子对象**的结构。
公共外观（背景/边框/旋转/不透明度）由 `TemplateRenderer` 在每个元素的公共路径上统一绘制，
八个渲染器不各写一遍——绘制次序固定为「背景 → 内容 → 边框」，三者包在同一次旋转与同一层 alpha 里。

| 字段 | 类型 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| `id` | string | 严格模式必填 | — | 模板内唯一。缺失时宽容模式自动补 `el-N` 并记警告。序列化器写出前会补好空/重复 id（见 §7），所以导出的文件一定能严格读回。 |
| `type` | string | 是 | — | 类型判别符，见下表。同时是载荷子对象的属性名。 |
| `xMm` | number | 是 | — | 左上角 x（相对页面）。 |
| `yMm` | number | 是 | — | 左上角 y（相对页面）。 |
| `widthMm` | number | 是 | — | 宽，≥ 0。 |
| `heightMm` | number | 是 | — | 高，≥ 0。 |
| `zIndex` | number | 否 | 0 | 升序绘制；同值按数组顺序。 |
| `backgroundColor` | 颜色 \| null | 否 | `null` | 元素框底色，覆盖整个包围盒；`null` 透明。 |
| `borderColor` | 颜色 \| null | 否 | `null` | 元素框边框色；`null` 不画边框。 |
| `borderWidthMm` | number | 否 | `0.2` | 边框宽（mm），仅 `borderColor` 非空时生效。 |
| `borderSides` | string | 否 | `"All"` | 边框边：`None`/`Top`/`Right`/`Bottom`/`Left`/`All`，可组合（如 `"Top, Right"`）。hiprint 的 `borderTop/borderLeft/...` 映射到这里。 |
| `borderDashPatternMm` | number[] \| null | 否 | `null` | 边框虚线段长循环（mm）。 |
| `rotationDegrees` | number | 否 | `0` | 绕**元素中心**顺时针旋转的角度；负值逆时针，超过 ±360 照常生效。 |
| `opacity` | number | 否 | `1` | 0..1；越界或非有限值在渲染期夹取。整组（背景+内容+边框）一次合成。 |

| `type` | 载荷键 | 类 |
|---|---|---|
| `text` | `text` | `TextElement` |
| `line` | `line` | `LineElement` |
| `rectangle` | `rectangle` | `RectangleElement` |
| `ellipse` | `ellipse` | `EllipseElement` |
| `image` | `image` | `ImageElement` |
| `qrcode` | `qrcode`（兼容 `qrCode`） | `QrCodeElement` |
| `barcode` | `barcode` | `BarcodeElement` |
| `table` | `table` | `TableElement` |

载荷子对象缺失时，元素仍合法，取该类型的默认样式。

---

## 4. 元素载荷字段

### 4.1 `text`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `content` | string | `""` | 文本，可含 `{{占位符}}`。 |
| `fontFamily` | string | `"Microsoft YaHei"` | 找不到时回退系统字体，**不抛异常**。 |
| `fontSizePt` | number | `12` | 字号（磅）。 |
| `bold` | bool | `false` | 加粗。 |
| `italic` | bool | `false` | 倾斜。 |
| `underline` | bool | `false` | 下划线。 |
| `strikethrough` | bool | `false` | 删除线。 |
| `color` | 颜色 | `"#000000"` | 字色。 |
| `horizontalAlign` | `Left`/`Center`/`Right`/`Justify` | `"Left"` | 水平对齐；`Justify` 把除末行外的多余宽度均摊到字间距。 |
| `verticalAlign` | `Top`/`Middle`/`Bottom` | `"Top"` | 垂直对齐。 |
| `wrap` | bool | `true` | 按 `widthMm` 换行。 |
| `ellipsis` | bool | `false` | `wrap=false` 且超宽时用 `…` 截断（hiprint `textContentWrap: ellipsis`）；为假则直接裁切。 |
| `lineSpacing` | number | `1.2` | 行高 = 字号 × 该倍数。 |
| `letterSpacingPt` | number | `0` | 字间距（磅）；0 为字体默认字距。 |
| `paddingLeftMm` / `paddingTopMm` / `paddingRightMm` / `paddingBottomMm` | number | `0` | 内容区内边距（mm），文本排版在扣除内边距后的区域内进行。 |

### 4.2 `line`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `strokeColor` | 颜色 | `"#000000"` | 线色。 |
| `strokeWidthMm` | number | `0.2` | 线宽（mm）。 |
| `direction` | `Horizontal`/`Vertical` | `"Horizontal"` | 水平线长度取 `widthMm`，垂直线取 `heightMm`。 |
| `dashPatternMm` | number[] \| null | `null` | 虚线段长循环（mm），`null` 为实线。 |

### 4.3 `rectangle` / `ellipse`（同结构 `ShapeOptions`）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `strokeColor` | 颜色 \| null | `"#000000"` | `null` 表示不描边。 |
| `strokeWidthMm` | number | `0.2` | 描边宽（mm）。 |
| `fillColor` | 颜色 \| null | `null` | `null` 表示不填充。 |
| `cornerRadiusMm` | number | `0` | 圆角，仅矩形生效，渲染时夹到短边一半。 |
| `dashPatternMm` | number[] \| null | `null` | 虚线段长循环（mm）。 |

椭圆内切于元素矩形。

### 4.4 `image`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `sourceType` | `EmbeddedBase64`/`ResolverKey` | `"EmbeddedBase64"` | 图片来源。 |
| `data` | string \| null | `null` | Base64 数据（不含 `data:` 前缀）。 |
| `resolverKey` | string \| null | `null` | `ResolverKey` 时由宿主 `IImageResolver` 解释的键。 |
| `fitMode` | `Contain`/`Cover`/`Fill`/`None` | `"Contain"` | 适配方式。`None` = 按原始像素尺寸换算成 mm 后居中，不缩放。 |
| `cornerRadiusMm` | number | `0` | 圆角半径（mm），对位图做圆角裁切；0 为直角。 |

解码失败 → 画红色虚线框 + `IMG ERR`，**不中断整页渲染**。

### 4.5 `qrcode`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `content` | string | `"https://example.com"` | 编码内容，可含占位符。 |
| `errorCorrection` | `L`/`M`/`Q`/`H` | `"M"` | 纠错等级。 |
| `foregroundColor` | 颜色 | `"#000000"` | 前景（模块）色。 |
| `backgroundColor` | 颜色 \| null | `"#FFFFFF"` | `null` 表示透明底。 |
| `quietZoneMm` | number | `1` | 静区宽度（mm）。取的就是这个毫米值本身，模块边长 = (边长 − 2×静区) ÷ 模块数；放不下（算出 ≤ 0）记 `QrCodeFailed`。 |
| `caption` | string | `""` | 码下标题，可含 `{{占位符}}`。 |
| `showCaption` | bool | `false` | 是否在码下方绘制标题（hiprint 的 `hideTitle=false`）。 |
| `captionFontSizePt` | number | `10.5` | 标题字号（磅）。 |
| `captionFontFamily` | string | `"Microsoft YaHei"` | 标题字体。 |
| `captionColor` | 颜色 | `"#000000"` | 标题颜色。 |
| `captionAlign` | `Left`/`Center`/`Right`/`Justify` | `"Center"` | 标题水平对齐。 |
| `captionLineSpacing` | number | `1.2` | 标题行距倍数。 |

码区按 `min(元素宽, 元素高 − 标题占高)` 生成正方形并水平居中；开启 `showCaption` 时标题占掉码下方按元素宽折行后的高度。
内容非法或为空 → 降级为占位框 + 警告，不抛异常。

> 与规范的差距（已登记 PRD-GAP，见 `docs/04-task-plan.md`）：ISO/IEC 18004 要求静区 ≥ 4 倍模块尺寸，
> 本实现**不会**为了凑够 4 模块而自动缩小符号，静区给多少就画多少。300 DPI/40mm 实测能解回，
> 但现场用低档扫描枪或远焦距时可能拒读——需要宽静区的模板请自己把 `quietZoneMm` 写够。

### 4.6 `barcode`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `content` | string | `"ABC-123"` | 编码内容，可含占位符。 |
| `symbology` | 见下表 | `"Code128"` | 码制。写成员名原样（`Ean13` 不是 `EAN13`）。 |
| `barColor` | 颜色 | `"#000000"` | 条色。 |
| `backgroundColor` | 颜色 \| null | `"#FFFFFF"` | `null` 表示透明底。 |
| `showText` | bool | `true` | 条下方绘制可读文本；这一行的厚度（`fontSizePt × 1.2` 换算成 mm）**从条高里扣出来**，条码不会盖住文字。 |
| `fontSizePt` | number | `7` | 可读文本字号（磅）。 |
| `fontFamily` | string | `"Microsoft YaHei"` | 可读文本字体。 |
| `quietZoneMm` | number | `2.5` | 左右静区（mm）。条宽 = (元素宽 − 2×静区) ÷ 模块数；算出 ≤ 0 记 `BarcodeFailed`。 |

可用码制（`BarcodeSymbology` 全部成员，写错会在加载期被枚举解析拒绝，不会静默画成 Code128）：

| 值 | 内容要求 | 备注 |
|---|---|---|
| `Code128` | 全 ASCII | 默认码制。 |
| `Ean13` | 12 或 13 位数字 | 校验位由 ZXing 计算。 |
| `Ean8` | 7 或 8 位数字 | |
| `UpcA` | 11 或 12 位数字 | 编码时按"首位补 0 的 EAN-13"处理，可读文本仍画原始 UPC 内容。 |
| `UpcE` | 6、7 或 8 位数字 | |
| `Code39` | 大写字母、数字与少量符号 | |
| `Code93` | 数字与 ASCII 大写子集 | |
| `Itf` | 偶数位数字 | 交叉 25 码。 |
| `Codabar` | `A`/`B`/`C`/`D` 起止符包裹的数字与少量符号 | 读码器通常不返回起止符。 |
| `Msi` | 数字 | Modified Plessey；`MultiFormatReader` 不注册 MSI，验证需用 `MSIReader`。 |

内容不符合码制约束（如 `Ean13` 位数错误）→ 降级占位框 + 警告，不抛异常。
十种码制都有"渲染 → ZXing 解回原内容"的往返测试（`CodeRendererTests`）。

EAN-13/EAN-8/UPC-A/UPC-E 的起止护栏与中间护栏按 ISO/IEC 15420 向下超出数据条 **5 个模块**
（按实际模块宽换算，条码拉宽/缩窄时比例跟着变；上限为条高的 25%，防止矮条把数据条压没）。
其余码制的标准不要求长护栏，所有条同高——`BarcodeGuardBarTests` 用逐列墨迹高度把这四种码制
（95/67/95/51 模块）的护栏区间钉死，并留了 Code128 的"同高"正对照。

### 4.7 `table`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `columns` | `TableColumn[]` | `[]` | 列定义，顺序即从左到右。 |
| `field` | string | `""` | 数据行的集合路径（如 `items`）；空表示由列字段的公共首段推断。列字段不共享首段时必须显式给出。 |
| `rows` | `string[][]` | `[]` | 静态数据行；绑定产生的行追加其后。 |
| `headerRows` | `TableHeaderCell[]` | `[]` | 多行表头单元格；为空时用 `columns[].title` 渲染单行表头。 |
| `headerRowHeightMm` | number | `-1` | 表头行高（mm）；`-1` 按表头字号/折行自适应。 |
| `headerFontSizePt` | number \| null | `null` | 表头字号，`null` 继承 `fontSizePt`。 |
| `gridColor` | 颜色 \| null | `"#000000"` | `null` 表示不画内线。 |
| `gridWidthMm` | number | `0.15` | 线宽（mm）。 |
| `showBorder` | bool | `true` | 外框。 |
| `showHeader` | bool | `true` | 表头行。 |
| `headerFillColor` | 颜色 \| null | `"#EEEEEE"` | 表头底色。 |
| `headerBold` | bool | `true` | 表头加粗。 |
| `stripeColor` | 颜色 \| null | `null` | 斑马纹偶数行底色，`null` 关闭。 |
| `fontSizePt` | number | `9` | 正文字号（磅）。 |
| `fontFamily` | string | `"Microsoft YaHei"` | 字体。 |
| `rowHeightMm` | number | `-1` | 固定行高（mm）；`-1` 按内容自适应。 |
| `cellPaddingMm` | number | `1` | 单元格内边距（mm）。 |
| `lineSpacing` | number | `1.2` | 自适应行高的行距倍数。 |

`TableColumn`：

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `title` | string | `""` | 表头标题。 |
| `widthMm` | number | `20` | 列宽；`-1` 表示均分剩余宽度。 |
| `field` | string | `""` | 数据路径（如 `items.name`）；空表示该列不参与绑定。 |
| `align` | `Left`/`Center`/`Right`/`Justify` | `"Left"` | 该列水平对齐。 |
| `fontSizePt` | number \| null | `null` | 列字号，`null` 继承表格字号。 |
| `sequence` | bool | `false` | 该列输出 1 起的行序号而非字段值（hiprint `tableTextType: "sequence"`）。 |
| `summary` | `None`/`Count`/`Sum`/`Average`/`Min`/`Max`/`Text` | `"None"` | 正文之后追加一行汇总；`Text` 固定输出 `summaryLabel`。 |
| `summaryLabel` | string | `""` | 汇总行前缀，如 `"合计："`。 |
| `summaryDecimals` | integer | `2` | 汇总数值的小数位数。 |

`TableHeaderCell`（多行表头的单元格，支持合并）：

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `row` | number | `0` | 所在表头行（0 起）。 |
| `column` | number | `0` | 起始列（0 起）。 |
| `title` | string | `""` | 标题文本。 |
| `colSpan` | number | `1` | 横向跨列数。 |
| `rowSpan` | number | `1` | 纵向跨行数。 |
| `align` | `Left`/`Center`/`Right`/`Justify` | `"Center"` | 水平对齐。 |
| `fontSizePt` | number \| null | `null` | 字号，`null` 继承表头字号。 |

多行表头的网格线按单元格边界绘制：跨列/跨行单元格内部**不画分隔线**，外部边界照常。
越界的 `row`/`column`/跨度会被夹到网格内，不会画出表格外。

---

## 5. 数据绑定占位符

`text.content`、`qrcode.content`、`barcode.content` 与 `table` 的列绑定支持 `{{路径}}`：

- `{{name}}` — 取数据字典的 `name`。
- `{{order.customer.city}}` — 点号钻取嵌套对象。
- `{{items[0].sku}}` — 方括号索引。
- 路径不存在 → 输出空串并记 `RenderIssue`（`BindingMissingField`），**不抛异常**。
- 路径里的手写空格不参与取值：`{{ customer . name }}` 与 `{{customer.name}}` 等价，
  表格判定"各列共用的首段"时也同样剥掉空白。
- `table` 的行集合由 `table.field` 指向的数组展开；`field` 为空时取各列 `field` 的**公共首段**（`items.name`/`items.qty` → `items`）。
  列字段不共享首段（如 `a.name` 与 `b.qty`）时必须显式写 `table.field`；推断失败时引擎不会把两个不相干的集合按行号硬配对，
  数据行不输出（静态 `rows` 照常渲染）。静态 `rows` 里的 `{{}}` 也参与绑定；行数超过页高时按引擎的分页策略处理。

占位符是纯字符串替换，发生在渲染前的 `DataBinder` 阶段；模板模型本身不含绑定元数据。
设计器与打印预览在没有宿主数据时，会先用 §2.5 的 `designData` 兜底
（宿主数据优先，设计数据只补缺失的根字段）；绑定后的值为空时，文本保持空白，
二维码/一维码改画"缺少数据"占位框而不是交给编码器——空串喂给 ZXing 的 Code128 会产出
一段 24 模块的图案，拉宽后就是几块纯黑砖（见 `docs/rendering-spec.md` §7）。

---

## 6. 完整示例

以下模板含全部 8 种元素，可直接被 `TemplateJson.Deserialize` 读取（往返无损）。
同一份内容作为文件存在于 `docs/samples/kitchen-sink.template.json`，
`DocumentedSampleTests` 会在 CI 里加载它并做无损往返，保证本文档的示例不会与实现脱节。

```json
{
  "schemaVersion": "0.1",
  "paper": { "preset": "A4", "widthMm": 210, "heightMm": 297, "orientation": "Portrait" },
  "margins": { "top": 10, "right": 10, "bottom": 10, "left": 10 },
  "elements": [
    {
      "id": "el-1", "type": "text", "xMm": 12, "yMm": 12, "widthMm": 120, "heightMm": 12, "zIndex": 0,
      "text": {
        "content": "{{title}}", "fontFamily": "Microsoft YaHei", "fontSizePt": 18,
        "bold": true, "italic": false, "underline": true, "strikethrough": false, "color": "#112233",
        "horizontalAlign": "Left", "verticalAlign": "Middle", "wrap": false, "lineSpacing": 1.2
      }
    },
    {
      "id": "el-2", "type": "line", "xMm": 12, "yMm": 26, "widthMm": 186, "heightMm": 0, "zIndex": 1,
      "line": { "strokeColor": "#8899AA", "strokeWidthMm": 0.3, "direction": "Horizontal", "dashPatternMm": null }
    },
    {
      "id": "el-3", "type": "rectangle", "xMm": 12, "yMm": 30, "widthMm": 60, "heightMm": 30, "zIndex": 2,
      "backgroundColor": null, "borderColor": "#2F5597", "borderWidthMm": 0.4, "borderDashPatternMm": null, "opacity": 1,
      "rectangle": {
        "strokeColor": "#000000", "strokeWidthMm": 0.2, "fillColor": "#F0F4F8",
        "cornerRadiusMm": 2, "dashPatternMm": [1.5, 1]
      }
    },
    {
      "id": "el-4", "type": "ellipse", "xMm": 78, "yMm": 30, "widthMm": 40, "heightMm": 30, "zIndex": 3,
      "ellipse": { "strokeColor": null, "strokeWidthMm": 0.2, "fillColor": "#7F3DFF", "cornerRadiusMm": 0, "dashPatternMm": null }
    },
    {
      "id": "el-5", "type": "image", "xMm": 124, "yMm": 30, "widthMm": 74, "heightMm": 30, "zIndex": 4,
      "image": { "sourceType": "EmbeddedBase64", "data": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "resolverKey": null, "fitMode": "Contain" }
    },
    {
      "id": "el-6", "type": "qrcode", "xMm": 12, "yMm": 66, "widthMm": 30, "heightMm": 30, "zIndex": 5,
      "qrcode": {
        "content": "{{orderNo}}", "errorCorrection": "Q",
        "foregroundColor": "#000000", "backgroundColor": "#FFFFFF", "quietZoneMm": 1.5
      }
    },
    {
      "id": "el-7", "type": "barcode", "xMm": 48, "yMm": 66, "widthMm": 80, "heightMm": 20, "zIndex": 6,
      "barcode": {
        "content": "{{sku}}", "symbology": "Code128",
        "barColor": "#000000", "backgroundColor": null,
        "showText": true, "fontSizePt": 7, "fontFamily": "Consolas"
      }
    },
    {
      "id": "el-8", "type": "table", "xMm": 12, "yMm": 100, "widthMm": 186, "heightMm": 60, "zIndex": 7,
      "table": {
        "columns": [
          { "title": "品名", "widthMm": 90, "field": "items.name", "align": "Left", "fontSizePt": null },
          { "title": "数量", "widthMm": 30, "field": "items.qty", "align": "Right", "fontSizePt": 8 },
          { "title": "金额", "widthMm": -1, "field": "items.amount", "align": "Right", "fontSizePt": null }
        ],
        "rows": [ [ "静态行示例", "1", "0.00" ] ],
        "gridColor": "#333333", "gridWidthMm": 0.15,
        "showBorder": true, "showHeader": true,
        "headerFillColor": "#EEEEEE", "headerBold": true,
        "stripeColor": "#FAFAFA",
        "fontSizePt": 9, "fontFamily": "Microsoft YaHei",
        "rowHeightMm": -1, "cellPaddingMm": 1, "lineSpacing": 1.2
      }
    }
  ]
}
```

---

## 7. 校验规则速查

| 情况 | 严格模式 | 宽容模式（`TemplateJsonLoadOptions.Tolerant`） |
|---|---|---|
| 根节点不是对象 | `TemplateValidationException(root)` | 同左（不可恢复） |
| 缺 `schemaVersion` / 非字符串 | 异常 | 同左 |
| 主版本不匹配 / 次版本更高 | 异常 | 同左 |
| 缺 `paper` / `widthMm <= 0` | 异常 | 同左 |
| 未知纸张预设名 | 异常 | 同左 |
| `elements` 不是数组 | 异常 | 同左 |
| 未知 `type` | `UnknownElementTypeException` | 跳过 + 警告 |
| 缺几何字段 / 类型不是数字 | 异常（含 `ElementId`） | 保留元素、几何按 `0` 导入 + 警告（点名 `ElementId` 与字段） |
| 宽高为负 | 异常 | 跳过 + 警告 |
| 非法颜色串 | 异常（含 `ElementId`） | 跳过 + 警告 |
| 元素 `id` 缺失 | 异常 | 自动补 `el-N` + 警告 |
| 元素 `id` 重复 | 异常 | 重新分配 + 警告 |
| 样式 `id` 缺失 / 重复 | 自动补 / 重新分配 + 警告 | 同左（样式 id 只是被引用，两种模式都不因此拒文件） |
| 载荷子对象缺失 | 取默认样式 | 同左 |
| `designData` 存在但不是对象 | 忽略 | 同左 |
| `designData` 里对象/数组/null 值 | 忽略该键 | 同左 |
| 未知字段 | 忽略 | 忽略 |

`TemplateLoadResult.Warnings` 收集所有降级项，宿主应当在打开模板时展示给用户，而不是静默吞掉。

**序列化（`TemplateJson.Serialize` / `WriteTo`）的 id 契约**：写出前按 `PrintTemplate.Add` 的同一条规则补齐——
空 id 与后来的重复 id 重新分配为 `el-N` / `st-N`（N = 现有最大序号 + 1 递增），第一个出现的合法 id 原样保留。
因此「写出 → 严格读回 → 再写出」逐字节一致，且不会产出一份严格模式读不回来的文档。
补齐只作用于输出文本：输入模板与其中的元素/样式对象不会被改动（设计器的撤销栈还持有原对象）；
需要改 id 的样式按值克隆后再写，原样式保持不动。

---

## 8. hiprint 模板导入

`CrossPrint.Core.Hiprint.HiprintTemplateImporter` 把 hiprint（vue-plugin-hiprint / Vue-Print-Designer）
设计器导出的内容直接映射成上面的 `PrintTemplate`。六个桌面宿主与 Web 服务端可引用同一个 Core 包共用这份导入代码；
导入产物就是本文定义的模板 JSON，浏览器端按同一份 schema 消费，桌面端与 Web 端的模板语义因此一致。

```csharp
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;

// getJson() 的字符串、打印函数里的 JS 对象字面量、整个 .js 模块源码都能喂进来
var result = HiprintTemplateImporter.Import(File.ReadAllText("hiprint-template.js"));

foreach (var issue in result.Issues)
    Console.WriteLine(issue);          // [Warning] hiprint.js-formatter: formatter 是 JavaScript 函数…

var template = result.Template;        // 可直接交给设计器 / 渲染 / PDF / 打印链路
var page = result.Panels[0];           // 每个面板的宽高与元素数，宿主可让用户选择导入哪一联
```

宿主要区分"CrossPrint 模板"与"hiprint 模板"时用 `HiprintTemplateImporter.LooksLikeHiprintJson(text)`。
它和 `Import` 用的是**同一个结构判据**：归一化后真能取出至少一个面板对象才算 hiprint 模板。
所以谓词说"是"的文件 `Import` 一定能导（不会因为 `panels: []`、面板全是非对象而抛"没有 panels 面板"），
谓词说"不是"的文件交给模板 JSON 加载器也不会漏掉真正能导的写法（`[5, {…}]`、`[{…}]` 这类数组形态）。

### 8.1 输入形态

| 输入 | 处理 |
|---|---|
| 严格 JSON（`{"panels": [...]}`） | 直接解析。 |
| `getJson()` 输出（`"panels": [...]` 片段，前后带杂项代码） | 补外层花括号后按片段提取。 |
| JS 对象字面量（单引号、无引号键、尾逗号、注释） | 归一化器逐个顶层对象枚举，字符串与反引号内容整体跳过。 |
| 整个模块源码（`import`、`export const json = {...}`、lodash 调用、反引号函数体） | 从源码中提取模板对象；函数体不执行。 |
| 函数值（`function(){}`、箭头函数、或设计器导出的反引号源码字符串） | 不执行。源码字符串原样保留；裸函数表达式归一化为占位字符串 `[JavaScript function]`，字段保持非空，导入器照常发 `hiprint.js-formatter` 告警，而不是静默折叠成 `null`。 |
| 多面板模板（多联单） | 一次导入一个面板（`HiprintImportOptions.PanelIndex`，默认 0）；越界回落到第一个面板并记警告。一个面板对应一个 `PrintTemplate`。 |

### 8.2 映射口径

| hiprint | CrossPrint |
|---|---|
| 元素 `left/top/width/height`（pt，72dpi） | `xMm/yMm/widthMm/heightMm`（mm）；未写的宽高按元素类型取 hiprint 默认尺寸。写成 `0`/负数的维度钳成 0mm（**不**替用户猜一个值）并记 `hiprint.element-degenerate-size` 警告，点名是哪一维、原值多少 |
| 面板 `width/height`（mm） | 纸张宽高；`panelPageRule: "none"` → 高度 0 的连续纸 |
| `paperHeader/paperFooter`（pt） | 上/下留白提示（`margins`） |
| `borderTop/Left/Right/Bottom`（文本/表格等元素框） | 公共 `borderSides`（可组合）；矩形/椭圆内部线型不支持分边，四边样式不同时按统一样式绘制并记警告 |
| `rotate` | `rotationDegrees`（绕元素中心顺时针） |
| `text` 的 `field` | `{{field}}` 占位符；`hideTitle=false` 时拼成 `标题：{{field}}` |
| `textContentWrap: nowrap/clip/ellipsis` | `wrap=false`；`ellipsis` 同时置 `ellipsis=true` |
| `lineHeight`、`letterSpacing`、`padding` | `lineSpacing`、`letterSpacingPt`、四向 `padding*Mm`；未写 `lineHeight` 时 `lineSpacing = 9.75 / fontSizePt`（print-lock.css 的固定 9.75pt 行高，不随字号缩放） |
| 元素 `font-size`、`font-family` | `fontSizePt`；未写 `font-family`（hiprint 实为 `inherit`）时导入为 `SimSun`——模板宽度是按宋体 ASCII 半角 / CJK 全角度量设计的，换成雅黑会折行 |
| 垂直对齐 | 未写为 `Top`；只有显式 `middle`/`bottom` 才映射成 `Middle`/`Bottom`（hiprint 默认顶对齐），大小写与首尾空白的写法都能接住。认不出的取值回落 `Top` 并记 `hiprint.valign-unknown` 警告 |
| 文本溢出元素框 | 不裁剪（hiprint 文本无 `overflow:hidden`，标点下沿/降部照常打印；图片、表格仍裁剪） |
| 二维码标题字号 | 继承元素 `fontSize`，未写时 9pt；标题单行不折行（hiprint 的 `white-space:nowrap`） |
| `textAlign: justify` | `horizontalAlign: "Justify"` |
| `hline`/`vline` | `line`（方向 + 线宽 + 虚线） |
| `rect`/`oval` | `rectangle`/`ellipse`（填充/描边/圆角） |
| `image` 的 `src`（data URL） | `image`（`EmbeddedBase64`）；`fit: scale-down` 按 `Contain` |
| `qrCode` 的 `qrCodeLevel`、`hideTitle` | `errorCorrection`、`caption/showCaption`；等级认 `0/1/2/3`（数字串 `"1"`、`"2 "` 同样接住，不算丢弃），写成别的值（`"H"`、`4`）按默认 `M` 导入并记 `hiprint.qrcode-level-unknown` 警告 |
| `barCode` 码制 | `symbology`（Code128/EAN13/EAN8/UPC-A/UPC-E/Code39/Code93/ITF/Codabar/MSI） |
| 表格 `columns` 二维表头网格 | `columns` + `headerRows`（colspan/rowspan 展开）；`checked:false` 的单元格跳过 |
| `tableTextType: "sequence"` | `TableColumn.Sequence` |
| `tableSummary`（count/sum/avg/min/max/text） | `TableColumn.Summary` + `SummaryLabel` + `SummaryDecimals` |
| 表格 `field` | `TableOptions.Field`（行集合；行列字段关系据此展开） |
| 元素 `field` + `testData` | `designData[field] = testData`（设计态预览值；同名字段先到先得，空 `testData` 不写） |
| `columns` 写成 `{"0": {...}, "1": {...}}` 或单个扁平列对象 | 都认：前者按列顺序读出，后者当成一列（以前会被读成空列） |
| `columns` 写成扁平列清单 `[{title,field,width}, {…}, …]` | 认作**一行多列**：按数组顺序逐列建 `columns` + 单行 `headerRows`，与写成二维 `[[…]]` 的模板导入结果一致（以前一个对象算一行，6 列的表最后只剩 1 列，设计器再也删不掉列） |
| 扁平清单或二维表头行里混着分组列对象 `{title:"分组", columns:[…]}` | 子列在原位置逐个展开，一列不丢；分组标题本身在现有表头模型里留不住，整张表记一条 `hiprint.table-group-header`（Info，列出被丢掉的分组标题）。行内分组展开后各行格数可能不等，按固定网格归一化（列数取各行之和的最大值，列的标题/字段取该列自下而上第一个有值的单元格） |
| 表格缺 `columns` 且无 `fields` | 保留表格框、占 1 列（标题"列1"）并记 `hiprint.table-no-columns` 警告，不再整张丢弃 |
| 旧类型 `tableCustom` | 与 `table` 等价导入，记 `hiprint.table-custom-legacy`（Info） |

### 8.3 无法等价表达的 hiprint 能力

导入器**不静默丢弃**：每一项都进 `HiprintImportResult.Issues`，`Code` 稳定可分类，`SourcePath` 指回源位置。
`Severity` 分三档：`Info`（已等价处理）、`Warning`（降级，打印结果会与 hiprint 有差异）、`Error`（元素/面板被跳过）。

| 能力 | 处理 |
|---|---|
| `formatter` / `styler` / 表格 JS 回调 | 不执行函数，按原始字段值与静态样式打印（`hiprint.js-formatter`）。 |
| `dataType`/`format` 日期格式化、`upperCase`（金额大写） | 按原值打印。 |
| `pageBreak`、`showInPage`/`unShowInPage`、`fixed`（每页重复） | 未实现，元素只按普通元素导入。 |
| 水印、页码、单元格内嵌码、表格卡片网格 | 无对应能力，记警告并尽量给出最接近的替代。 |
| `html` 元素 | 依赖浏览器排版 HTML/CSS，导入为虚线占位框并记警告（改用文本/图片/表格）。 |
| 图片 `field` 绑定（数据驱动图片） | 无对应数据通道，忽略该字段并记警告。 |
| 跨页长文本 / 长表格分页 | v0.1 不分页，超页内容裁切（渲染期记 `TableOverflow`）。 |

### 8.4 验证

- `HiprintImporterTests`：4 份真实夹具（完整 JS 模块、`getJson` 片段、带隐藏列的模板、多表格模板）+ 合成场景，
  覆盖多面板选择、连续纸、文本/图片/条码/二维码/表格/多行表头映射、`tableCustom` 兼容、表格缺列占位、
  `columns` 的对象映射/扁平列清单/分组列几种写法、`field + testData` 进入 `designData`、问题清单与序列化往返；
  另有断言证明同一模板的 JS 模块与 `getJson` 片段导入结果逐字节一致。
- `HiprintSemanticsRendererTests`：把导入后的语义（旋转、分边边框、内边距、字间距、省略号、两端对齐、二维码标题、
  图片圆角、表格行集合/序号/汇总/多行表头）逐条渲染成像素并配"关掉机制必须不同"的反证。
- `HiprintFixtureRenderTests`：真实夹具导入后直接整页渲染，断言无元素被跳过且页面确有墨迹（跨层回归）。
- 真实夹具放在 `tests/CrossPrint.Core.Tests/Fixtures/Hiprint/`，均来自 hiprint 设计器导出，不是手写样例。
- `DocumentedSampleTests`：断言随样例分发的 `docs/samples/hiprint-sample.js` 真的能导入、元素顺序正确、
  只留一条声明的 `hiprint.js-formatter` 告警——六个宿主的导入入口默认就指向它，演示入口不能是摆设。

### 8.5 在样例宿主里试用

六个样例（Avalonia / WPF / WinForms / Eto / WinUI / Uno）都有 **文件 → 导入 hiprint 模板…** 菜单，
工具栏上有同样的按钮，两者走同一个导入流程：

- **选文件**：Avalonia、WPF、WinForms、Eto 用系统文件对话框；WinUI 用 `FileOpenPicker`（先 `InitializeWithWindow`）；
  Uno 的 Skia 桌面文件选择器依赖平台实现，改为路径输入框，默认指向输出目录里的 `hiprint-sample.js`。
- **换模板**：导入后 `DesignerControl.LoadTemplate` 会替换内核实例，样例随即把属性面板、纸张栏重新
  `Attach` 到新内核并重订 `TemplateChanged`，否则面板会继续盯着旧模板。
- **示例文件**：[`docs/samples/hiprint-sample.js`](samples/hiprint-sample.js) 随六个样例一起复制到输出目录，
  6 类元素 + 8 个元素实例，字段对齐样例数据，并故意留一个 JS `formatter` 展示降级告警长什么样。
- **直接粘 hiprint JSON**：底部“模板 JSON”面板会先用 `HiprintTemplateImporter.LooksLikeHiprintJson`
  判断根节点是否带 `panels`，是就走导入器转换（`getJson()` 输出、`{"json":{"panels":…}}`、面板数组、
  整个 JS 模块都认），转换结果显示在面板状态行；不是才按 CrossPrint 模板解析。导入后文本框会换成
  转换后的 CrossPrint JSON。
