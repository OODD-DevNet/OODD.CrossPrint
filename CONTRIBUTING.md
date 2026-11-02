# 贡献指南

欢迎提 Issue 和 Pull Request。开工前请读完本页，本项目有几条**架构铁律**，违反它们的改动无法合并。

## 开发环境

- .NET SDK **8.0.420**（`global.json` 锁定，`rollForward: latestFeature`）。
- Windows / Linux / macOS 均可构建 `CrossPrint.Core`；宿主适配项目按各自平台限制。
- 首次拉取后：`dotnet build OODD.CrossPrint.sln` → `dotnet test OODD.CrossPrint.sln`。

## 四条铁律

1. **`CrossPrint.Core` 不引用任何 UI 框架。** 出现 Avalonia/WinForms/WPF 引用即架构走偏，停下来讨论。
2. **模板里只有毫米和磅。** 几何字段以 `Mm` 结尾、字体以 `Pt` 结尾，像素只在渲染瞬间存在。单位换算只允许写在 `UnitConverter`。
3. **渲染同源。** 设计器画布和 PDF 导出必须调用同一个 `TemplateRenderer`。禁止用宿主控件（`TextBlock`/`PictureBox` 等）"模拟"元素外观。
4. **依赖许可证。** 只接受 MIT / BSD / Apache-2.0。**明确禁止 AGPL 与商业授权依赖**（含 QuestPDF）。新增 NuGet 包必须在 PR 里列出传递依赖树。

## 代码风格

- `Directory.Build.props` 打开了 `Nullable` + `TreatWarningsAsErrors` 子集（CS1591 等），公开成员必须有 XML 文档注释。
- 命名：私有字段 `_camelCase`；毫米量属性 `XxxMm`；磅量 `XxxPt`；颜色 `XxxColor`（类型 `SKColor` / `SKColor?`）。
- Skia 对象必须 `using`，不依赖 GC 终结。
- 异常语义：**编程错误抛异常，用户数据问题写进 `RenderReport`**。渲染引擎对合法模板永不抛异常——一张坏图不能让整页失败。
- PRD/设计未覆盖的细节：选最简单的可行方案，并在代码处标注 `// PRD-GAP: <说明>`，不要自行扩大范围。

## 序列化字段同步清单

给元素载荷类（`TextOptions` 等）加一个属性，必须同步 5 处，漏一处就会有静默丢字段：

1. 属性本身（含 XML 注释与必要的 `[JsonConverter]`）。
2. 该类的 `Clone()`。
3. `docs/template-schema.md` 的字段表。
4. `PayloadRoundTripTests` 的 `SampleValue`（未覆盖的类型会抛 `NotSupportedException` 强制你补）。
5. 对应元素的默认值测试。

## 测试

- 每个里程碑先跑通测试再进下一个。
- **基线文件必须提交进仓库**（`tests/Baselines/`）。禁止"运行时生成基线"——那样测试永远通过。
- 断言里的期望值必须来自实测，不接受凭记忆推算（例：PDF MediaBox 尺寸、`MmToPt` 结果）。
- 本地：`dotnet test`。CI：Windows + Linux 构建与测试。

## 提交

- 一个 PR 一个主题；改动 `Core` 公共 API 时在描述里写清影响面与迁移方式。
- 提交前本地 `dotnet build` + `dotnet test` 全绿。
- Commit message 用祈使句，说"为什么"多于"做了什么"。

## 许可证

提交即同意你的贡献以 Apache License 2.0 许可证发布。
