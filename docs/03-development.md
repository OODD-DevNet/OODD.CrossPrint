# CrossPrint 开发文档

> 面向贡献者与会话代理（agent）。环境坑清单来自本机实测，照做即可避坑。

---

## 1. 环境要求

| 项 | 要求  | 本机实测（2026-10-29） |
|---|----|---|
| SDK | .NET 8 SDK（构建锚定 `global.json` → 8.0.420）  | 已装 8.0.420 / 9 / 10 ✅ |
| OS | Windows 10+（开发机）；Linux/macOS 由 CI 覆盖  | Win11 26200 ✅ |
| workload | Core/Engine/Designer/Avalonia/WinForms/WPF/Eto/Uno 都不需要额外 workload（Uno 走 `Uno.Sdk` NuGet SDK）；WinUI 示例需 Windows App SDK 运行时 | android/ios/maui 已装；uno 未装，Uno 5.6 编译实测通过 ✅ |
| NuGet 源 | nuget.org 可达（本机直连验证通过）；若不可达换 `https://nuget.cdn.azure.cn` 镜像  | ✅ |

## 2. 仓库结构

```
OODD.CrossPrint/
├── global.json  Directory.Build.props  Directory.Packages.props  OODD.CrossPrint.sln
├── src/
│   ├── CrossPrint.Core/            ├── CrossPrint.Engine/
│   │   └── Hiprint/                （模板导入：归一化器 + 映射器）
│   ├── CrossPrint.Designer/        ├── CrossPrint.Printing/
│   └── CrossPrint.Hosts.<Avalonia|WinForms|Wpf|Eto|WinUi|Uno>/
├── samples/CrossPrint.Samples.<宿主>×6
├── tests/CrossPrint.{Core,Engine,Designer,Printing}.Tests/ + CrossPrint.Hosts.Avalonia.Tests/
└── docs/  (01需求 02架构 03开发 04计划 + template-schema.md rendering-spec.md)
```

依赖方向规则见 [02-architecture.md §1.1](02-architecture.md)，由 `tests/CrossPrint.Core.Tests/DependencyDirectionTests.cs` 兜底（扫描 csproj 的 ProjectReference，宿主越界或内核反向依赖即红）。

## 3. 构建与测试命令

> Windows + Git Bash 实测坑，务必照做：
> - **单项目构建**用 `dotnet build "src/X/X.csproj"`。Windows 上全量构建用 `OODD.CrossPrint.slnf`（含 WinUI/Uno 等 Windows 限定宿主）；跨平台构建用 `OODD.CrossPrint.Portable.slnf`；完整 sln 与 slnf 内容等价，留给 IDE/CI。
> - MSBuild 属性必须加引号：`dotnet build -p:Configuration=Release` 在 Git Bash 要写成 `"-p:Configuration=Release"`。
> - dotnet 日志输出 UTF-8，Bash 终端可能按 GBK 显示成乱码——**看日志用 `-warnaserror` 退出码判定，不靠肉眼看中文输出**。
> - 后台任务退出码会被吞：需要判定成败的命令不要 `run_in_background`，落盘日志再 `tail`。
> - CRLF 文件禁用 `sed -i`（会把整个文件行尾重写）；改文件用 Edit 工具。
> - VS 打开解决方案时 `obj/` 被占用会产生假 `CS2012/CS0246`——先关 VS 或等其索引完重试。

```bash
# 日常（全部宿主与样例 + 测试；Windows）
dotnet build OODD.CrossPrint.slnf

# 全量测试（按项目跑，单项目快且失败好定位）
dotnet test tests/CrossPrint.Core.Tests
dotnet test tests/CrossPrint.Engine.Tests
dotnet test tests/CrossPrint.Designer.Tests
dotnet test tests/CrossPrint.Printing.Tests
dotnet test tests/CrossPrint.Hosts.Avalonia.Tests

# 注意：本仓没有栅格快照基线。渲染一致性由"跨 DPI 毫米墨迹包围盒 + 码制解码 + 模块网格"这类
# 环境无关判据验证（理由见 docs/rendering-spec.md §4.1），不存在"再生成基线"的命令。

# AOT/裁剪验证（headless，不起 GUI）
dotnet publish samples/CrossPrint.Samples.Avalonia -c Release -r win-x64 \
  "-p:PublishTrimmed=true" "-p:TrimMode=partial" -p:EnableAotSupport=false

# 实测（2026-10-30，SDK 8.0.420）：本仓库程序集零 IL 警告。
# 模板 JSON 的 Read/Write 已改走 PayloadOptions.GetTypeInfo(typeof(T)) 返回的 JsonTypeInfo<T>
# （保留十六进制颜色/字符串枚举转换器，同时让裁剪器看得见类型，消除 IL2026）。
# DataBinder 的 POCO 反射是兜底路径，已在 TryMember/BuildProperties 就地
# [UnconditionalSuppressMessage] 并写明理由：裁剪后成员缺失 → TryMember 返回 false →
# BindText 记 BindingMissingField 并替换为空串（有测试覆盖该降级行为），不会崩溃。
# Trim/AOT 应用需确定绑定行为时，应使用 IDictionary/IDictionaryProvider 或保留模型类型。
# 剩余 IL2026/IL2104 均来自 Avalonia.DesignerSupport（设计期 BSON 远程协议），与本组件无关。
```

## 4. 代码规范

1. **C# 12 / net8.0，`Nullable=enable`，`ImplicitUsings=enable`。**
2. `SkiaSharp` 对象（`SKPaint/SKFont/SKPath/SKBitmap/SKImage/SKData/SKSurface/SKDocument`）必须 `using` 或 `Dispose`，禁止依赖 GC；渲染热路径允许 `thread static`/字段级复用（字体、画笔缓存），缓存必须有 LRU 上限。
3. 单位纪律：模型与内核 API 一律 `mm`/`pt`；**像素换算只允许出现在 `UnitConverter`、`RenderOptions` 应用点、`Designer.Viewport` 与宿主适配层 DPI 桥四处**。命名后缀强制：`WidthMm`、`FontSizePt`、`ToPixels(dpi)`。
4. JSON：新增元素类型必须同步——`PrintTemplateJsonContext` 注册、转换器分派表、`ElementTypeRegistry`、schema 文档字段表、样张模板用例（`docs/samples/kitchen-sink.template.json` 覆盖全类型，由 `TemplateRendererTests.样张模板逐元素在两种密度下毫米位置一致` 逐元素验墨迹；不建 `tests/Baselines` 栅格基线，理由见 `docs/rendering-spec.md` §4.1）。若 hiprint 有对应元素/属性，还要补导入器映射与 §4.1 的两层测试。缺一视为未完成，评审检查点。
5. 异常语义：调用方编程错误（null 模板等）→ 直接抛；用户数据问题（坏图/缺字体/坏条码内容）→ `RenderReport` issue，**禁止抛**。
6. `// PRD-GAP: <说明>` 只允许出现在设计文档未覆盖的微观决策点，且当句给出所选最简方案；禁止用它掩盖架构偏离。
7. XML 文档注释：`src/*` 公开成员 100% 覆盖（`GenerateDocumentationFile=true` + `TreatWarningsAsErrors` 对 CS1591）。
8. 提交即绿：批量编辑完再统一编译（禁止写一个文件编一次）；禁止提交 `dotnet build` 失败或测试红状态的 commit。
9. `dotnet format` 作为 pre-commit 检查；命名空间与目录一致。
10. 预览/叠加层分工：缩放只走 `RenderOptions.Dpi = Viewport.EffectiveRenderDpi`，相机只平移；叠加层一律在宿主设备 px 空间用 `Viewport.ToHostPx/ToHostRect` 绘制。任何"再乘一个缩放系数"的补偿都会让叠加层与内容在换 DPI 或换缩放时错开（`EditorRendererTests.引擎内容与视口换算落在同一处` 是这条的判据）。

### 4.1 hiprint 导入维护

- 入口：`HiprintTemplateImporter.Import(text, options)`。能接受严格 JSON、`getJson()` 片段、JS 对象字面量与整个模块源码；归一化在 `HiprintJsonNormalizer`，**绝不执行 JS**。
- 新增 hiprint 属性时：先在模型里加字段（JSON + `CloneDeep` + 属性描述符 + schema 文档），再在导入器里映射；无法等价表达的写进 `Issues`（`Severity.Warning` + 稳定 `Code`），不允许静默丢弃。
- 测试分三层，缺一不可：
  - `CrossPrint.Core.Tests/HiprintImporterTests.cs`：导入结果（几何 mm、字段值、问题清单）与归一化机制的反证。真实夹具放 `Fixtures/Hiprint/`，来自设计器导出，不改写成"顺手好读"的样子。
  - `CrossPrint.Engine.Tests/HiprintSemanticsRendererTests.cs`：导入语义的**渲染后果**（像素窗口断言），每个机制配"关掉它必须不同"的对照。
  - `CrossPrint.Engine.Tests/HiprintFixtureRenderTests.cs`：跨层回归——真实夹具导入后直接整页渲染，断言无元素被跳过且页面确有墨迹（防"字段读对了但根本画不出来"）。
- 审查口径：任何"导入了但渲染不出来"的字段都算未完成；只有模型没有消费者的属性，一律视为假功能。

## 5. 版本与发布

- 语义化版本 `vMAJOR.MINOR.PATCH`；`schemaVersion` 独立跟随模板格式大版本。
- NuGet 包 ID：`OODD.CrossPrint.Core` / `.Engine` / `.Designer` / `.Printing` / `CrossPrint.Hosts.*`（宿主包不带 OODD 前缀，降低用户输入成本；README 说明）。
- 打包属性集中在 `Directory.Build.props`（`Authors/RepositoryUrl/PackageLicenseExpression=Apache-2.0`），`dotnet pack -c Release -o ./artifacts`。
- CHANGELOG 按 Keep-a-Changelog 格式；破坏性变更标 `!`。

## 6. CI（GitHub Actions，`.github/workflows/ci.yml`）

- `portable` job：ubuntu-latest + windows-latest，构建 `OODD.CrossPrint.Portable.slnf`（Core/Engine/Designer/Printing + 四个测试项目），`dotnet test --no-build` 跑全部测试；**不跑 GUI 冒烟**（无头环境跑不了桌面宿主，UI 验证归人工）。
- `windows-hosts` job：构建 `OODD.CrossPrint.slnf`（全部宿主与样例），`dotnet pack` 10 个 src 包并校验每个 nupkg 内嵌 `README.md`；再做 win-x64 `PublishTrimmed` 冒烟，日志里出现本仓库程序集的 `warning ILxxxx` 即失败（第三方 Avalonia 告警允许保留）。
- Uno 宿主：`Uno.Sdk/5.6.54` + `net8.0-desktop`（Uno 5.6 的 Skia 桌面单投影，`Uno.WinUI` 解析为 5.6.99）。样例开 `<UnoSingleProject>true</UnoSingleProject>`，以触发 `_UnoRemoveTransitiveWPFDependency` 卸掉 `Uno.WinUI.Runtime.Skia.Wpf` 传递的 WPF 框架引用（否则 NETSDK1136）；两个 Uno 工程关闭集中包管理（Uno.Sdk 隐式包与 `Directory.Packages.props` 冲突），用 `<SkiaSharpVersion>2.88.9</SkiaSharpVersion>` 把隐式 SkiaSharp.* 钉回仓库锁。Uno 6.7 的 Skia 栈要求 SkiaSharp 3.119 + net9，暂不跟进。

## 7. 已知环境坑登记（持续追加）

| 坑 | 现象 | 处置 |
|---|---|---|
| `SkiaSharp.Views.WinUI` 2.88.9 需 `net8.0-windows10.0.19041.0` 且 `EnableMsixTooling` 才能跑示例 | 默认模板 `dotnet new` 不支持该 TFM | WinUI Sample 手写 csproj；TFM 探测已记录 |
| Avalonia 无官方 SkiaSharp.Views 包 | 找包会扑空 | 走 `DrawingContext→SKCanvas` 隐式转换（`Avalonia.Skia` 自带） |
| `SkiaSharp.Views.*` 默认浮动到 4.154 | NU1605 降级报错 | `Directory.Packages.props` 钉 2.88.9 |
| WinUI/WindowsAppSDK 1.6 在 dotnet CLI 下 MSB4062 | `MrtCore.PriGen.targets` 把 `GetMrtPackagingOutputs`/`_GetDefaultResourceLanguage` 无条件下挂到 `PrepareForRun`，而 `Microsoft.Build.AppxPackage.dll`/`Microsoft.Build.Packaging.Pri.Tasks.dll` 随 VS 安装 | 宿主与样例均无 .resw/XAML/PRI 资源，项目级 `Directory.Build.targets` 置空这两个目标；有 VS 的机器同样安全（无输入） |
| `SkiaSharp.Views.WinUI` 2.88.9 无 `SKSwapChainPanel` | 文档/旧教程按 UWP 路线写 | 用 `SKXamlCanvas`（命名空间 `SkiaSharp.Views.Windows`），`e.Info` 已是设备 px；DPI 取 `XamlRoot.RasterizationScale×96` |
| Eto.Forms 键名与平台名和想象不同 | `Keys.OemPlus`/`NumPad0`/`Back`、`Eto.Platforms.Windows` 都不存在 | 用 `Keys.Equal`/`Keypad0`/`Backspace`、`global::Eto.Platforms.WinForms`；滚轮 `Delta.Height`（`Delta` 是 `SizeF`） |
| Uno 样例 NETSDK1136 | `Uno.WinUI.Runtime.Skia.Wpf` 传递引入 `Microsoft.WindowsDesktop.App.WPF`，与 `net8.0-desktop` 冲突 | 样例设 `<UnoSingleProject>true</UnoSingleProject>`，SDK 目标 `_UnoRemoveTransitiveWPFDependency` 会移除该框架引用 |
| Uno 适配层 `dotnet pack` 静默不出包 | `Uno.Sdk` 默认 `IsPackable=false`（按应用看待） | 适配层 csproj 显式 `<IsPackable>true</IsPackable>`；产物 TFM 组为 `net8.0-desktop1.0`（Uno 把平台版本固定为 1.0），消费方须同为 Uno 5.6 desktop |
| Uno 6.7 与仓库 SkiaSharp 锁冲突 | 6.7 的 Skia 栈要求 SkiaSharp 3.119、TFM 至少 net9.0-desktop | 以 Uno 5.6.54/net8.0-desktop 为基线；工程关闭 CPM 并用 `SkiaSharpVersion` 钉 2.88.9 |
| WinForms `SplitContainer.SplitterDistance` 提前设置被静默夹紧 | 控件还没最终宽度时设 340，会被夹到默认宽（约 110），左侧属性面板只剩标签列，表现为"属性编辑器不见了" | 在 `OnShown` 里、首次布局完成后再设 `SplitterDistance`；属性面板的标签列固定 132、编辑器列 `Percent 100` |
| 宿主右键与上下文菜单互相干扰 | 右键按下时 `Capture/HitTest` 抢占，菜单弹出时捕获被系统释放，`MouseUp` 收不到，内部按键状态悬挂 | 右键按下只 `Focus()`，不算作画布按键；`SelectAt` 放到菜单打开事件里（各宿主见样例实现），菜单项直接调内核方法 |
| Avalonia 自绘控件"画得出来但点不中" | `ICustomDrawOperation.HitTest` 返回 `false` 时，`CompositionDrawListVisual.HitTest` 直接判定该视觉不可命中，控件收不到任何 `PointerPressed`，表现为画布正常显示、左键选不中元素、右键菜单也不弹 | 绘制操作的 `HitTest` 必须返回 `_bounds.Contains(p)`；元素级命中由 `EditorKernel.SelectAt` 做二级判定。回归测试 `SkiaDrawOperationTests.命中区覆盖整块绘制区`（改成 `false` 立即变红） |
| Git Bash `mkdir -P` 撞盘符 | unknown option | 路径一律正斜杠盘符 `/g/...` |
| 本机 GUI 由用户负责启动 | 代理不起 Avalonia/WinForms 窗口、不要真打印机 | 验证到"编译+无头测试+产物文件"为止，冒烟清单交用户 |
