using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Eto;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using Eto.Drawing;
using Eto.Forms;
using System.IO;
using System.Threading.Tasks;

namespace CrossPrint.Samples.Eto;

/// <summary>示例主窗口：顶部命令栏 + 左侧纸张/工具箱 + 中间设计画布 + 右侧属性面板 + 底部可折叠模板 JSON 与状态栏。</summary>
internal sealed class MainForm : Form
{
    // 布局基准与 WPF 版一致：左栏 200px、属性栏 310px，分隔条压窄到 4px。
    const int LeftPanelWidth = 200;
    const int PropertyPanelWidth = 310;
    const int SplitterThickness = 4;
    // JSON 编辑区 190px，加上标题带后展开整块约 230px；折叠时只留标题带。
    const int JsonEditorHeight = 190;

    static readonly (string Label, string Key)[] ElementButtons =
    [
        (Localizer.T("Element.Text"), ElementTypeKeys.Text),
        (Localizer.T("Element.Line"), ElementTypeKeys.Line),
        (Localizer.T("Element.Rectangle"), ElementTypeKeys.Rectangle),
        (Localizer.T("Element.Ellipse"), ElementTypeKeys.Ellipse),
        (Localizer.T("Element.Image"), ElementTypeKeys.Image),
        (Localizer.T("Element.QrCode"), ElementTypeKeys.QrCode),
        (Localizer.T("Element.Barcode"), ElementTypeKeys.Barcode),
        (Localizer.T("Element.Table"), ElementTypeKeys.Table),
    ];

    static readonly PaperChoice[] PaperChoices =
    [
        new(PaperTypePresets.A3, Localizer.T("Paper.A3")),
        new(PaperTypePresets.A4, Localizer.T("Paper.A4")),
        new(PaperTypePresets.A5, Localizer.T("Paper.A5")),
        new(PaperTypePresets.B5, Localizer.T("Paper.B5")),
        new(PaperTypePresets.Receipt80, Localizer.T("Paper.Receipt80")),
        new(PaperTypePresets.Receipt58, Localizer.T("Paper.Receipt58")),
        new(PaperTypePresets.Label100x150, Localizer.T("Paper.Label100x150")),
        new(null, Localizer.T("Paper.Custom")),
    ];

    readonly DesignerControl _designer;

    // 阶段 E：解决方案持久化（共享存储 + 会话）。模板落库到 AppData/CrossPrint/templates.db（SQLite）。
    readonly SolutionSession _session = new();
    readonly UITimer _autosaveTimer = new() { Interval = 30 };
    ButtonMenuItem _recentItem = null!;
    readonly PropertyPanel _properties;
    readonly Label _status = new() { TextColor = Colors.DimGray };
    readonly Label _zoomText = new() { TextColor = Colors.DimGray };
    Button _undoButton = null!;
    Button _redoButton = null!;
    // 阶段 G：右上角 Toast 浮层（PixelLayout 绝对定位覆盖整窗，不拦截鼠标）。
    readonly PixelLayout _toastLayer = new();
    Panel? _toastCard;
    readonly UITimer _toastTimer = new() { Interval = 3 };

    readonly ContextMenu _canvasMenu = new();
    readonly List<MenuItem> _selectionMenuItems = [];
    readonly DropDown _paperDropDown = new();
    readonly ListBox _toolbox = new();
    readonly Button _rotatePaperButton = new() { Text = Localizer.T("Paper.Rotate") };
    readonly Button _customPaperButton = new() { Text = Localizer.T("Paper.CustomBtn") };
    readonly CheckBox _continuousToggle = new() { Text = "连续纸（打印长度随内容自适应）" };
    readonly Label _paperInfo = new() { TextColor = Colors.DimGray, Wrap = WrapMode.Word };
    readonly TextArea _templateJson = new();
    readonly Label _jsonState = new() { TextColor = Colors.DimGray, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _jsonToggleButton = new() { Text = Localizer.T("JsonPanel.Expand") };
    Panel _jsonEditorArea = null!;

    MenuItem _verticalCenterItem = null!;
    MenuItem _rotatePaperItem = null!;
    bool _syncingPaper;
    // 右栏空白区（页面级水印）：持久实例，见 PropertyPanel.EmptyStateContentBuilder 挂接处注释。
    Control? _pageSection;
    bool _syncingWatermark;
    CheckBox _watermarkEnabled = null!;
    TextBox _watermarkText = null!;
    TextBox _watermarkBinding = null!;
    TextBox _watermarkFontSize = null!;
    TextBox _watermarkOpacity = null!;
    TextBox _watermarkRotation = null!;
    TextBox _watermarkSpacing = null!;
    TextBox _watermarkColor = null!;
    bool _jsonDirty;
    bool _jsonExpanded;
    string _jsonSnapshot = string.Empty;

    public MainForm()
    {
        Title = Localizer.T("App.Title.Short") + " — Eto";
        // 布局基准与 WPF 版一致：窗口 1320×880，最小 1024×640。
        ClientSize = new Size(1320, 880);
        MinimumSize = new Size(1024, 640);

        var template = LoadSampleTemplate(out var warnings);
        _designer = new DesignerControl(template)
        {
            Data = SampleData.Create(),
        };
        _properties = new PropertyPanel(_designer.Kernel);
        // 空白区必须返回懒构建的持久实例：属性面板每次 Rebuild 都会整表替换并 Dispose 旧行控件，
        // 新建控件树会让字段级单例控件重复进视觉树（Eto 平台下还会被 Dispose 掉）。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();
        var root = new TableLayout { Spacing = new Size(0, 0) };
        root.Rows.Add(new TableRow(new TableCell(BuildToolBars()) { ScaleWidth = true }));
        var midRow = new TableRow(new TableCell(BuildCenterSplitter()) { ScaleWidth = true }) { ScaleHeight = true };
        root.Rows.Add(midRow);
        root.Rows.Add(new TableRow(new TableCell(BuildTemplateJsonPanel()) { ScaleWidth = true }));
        root.Rows.Add(new TableRow(new TableCell(BuildStatusBar()) { ScaleWidth = true }));
        // 阶段 G：用 PixelLayout 包裹 TableLayout，使 Toast 浮层覆盖整窗且不抢占输入。
        _toastLayer.Add(root, 0, 0);
        Content = _toastLayer;
        SizeChanged += (_, _) =>
        {
            root.Size = ClientSize;
            PositionToast();
        };
        Menu = BuildMenuBar();

        BuildCanvasContextMenu();
        // 用 MouseDown 而不是 MouseUp：Eto 的 MouseUp 在部分平台不带按键位，
        // 右键判定会落空；按下时的 Buttons 一定含 Alternate。
        _designer.MouseDown += OnDesignerMouseDown;
        _designer.KeyDown += OnDesignerKeyDown;
        BindKernel();

        _autosaveTimer.Elapsed += (_, _) => { if (_session.IsDirty) _ = SaveSolution(false, silent: true); };
        _autosaveTimer.Start();
        // 阶段 G：Toast 3 秒后自动消失。
        _toastTimer.Elapsed += (_, _) =>
        {
            _toastTimer.Stop();
            if (_toastCard is not null)
                _toastLayer.Remove(_toastCard);
        };
        UpdateTitle();
        RefreshRecentMenu();

        Load += (_, _) => _designer.Focus();

        if (warnings.Count > 0)
            SetStatus(Localizer.T("App.Status.Warnings", string.Join("；", warnings)), error: true);
        else
            SetStatus(Localizer.T("App.Status.Ready"));
    }

    sealed record PaperChoice(string? Key, string Label);

    static PrintTemplate LoadSampleTemplate(out IReadOnlyList<string> warnings)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "kitchen-sink.template.json");
        if (!File.Exists(path))
        {
            warnings = new[] { "未找到 kitchen-sink.template.json，已回退到默认 A4 空模板。" };
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            warnings = result.Warnings;
            return result.Template;
        }
        catch (Exception ex)
        {
            warnings = new[] { "加载 kitchen-sink.template.json 失败，已回退到默认 A4 空模板：" + ex.Message };
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>顶部菜单栏：文档级操作（加载复杂测试模板、hiprint 导入）。</summary>
    MenuBar BuildMenuBar()
    {
        var fileMenu = new ButtonMenuItem { Text = Localizer.T("Menu.File") };

        var importItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => ImportHiprintDialog();
        fileMenu.Items.Add(importItem);

        var templatesItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.Templates"), ToolTip = Localizer.T("Menu.File.TemplatesTip") };
        templatesItem.Click += (_, _) => OpenTemplateLibrary();
        fileMenu.Items.Add(templatesItem);

        var stylesItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.StyleLibrary"), ToolTip = Localizer.T("Menu.File.StyleLibraryTip") };
        stylesItem.Click += (_, _) => StyleLibraryDialog.Show(_designer.Kernel, this);
        fileMenu.Items.Add(stylesItem);

        fileMenu.Items.Add(new SeparatorMenuItem());

        var newItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.New") };
        newItem.Click += (_, _) => NewSolution();
        fileMenu.Items.Add(newItem);

        var openItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.Open") };
        openItem.Click += (_, _) => OpenSolutionDialog();
        fileMenu.Items.Add(openItem);

        _recentItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.Recent") };
        fileMenu.Items.Add(_recentItem);

        var saveItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.Save") };
        saveItem.Click += (_, _) => _ = SaveSolution(false);
        fileMenu.Items.Add(saveItem);

        var saveAsItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.SaveAs") };
        saveAsItem.Click += (_, _) => _ = SaveSolution(true);
        fileMenu.Items.Add(saveAsItem);

        fileMenu.Items.Add(new SeparatorMenuItem());

        var exportJsonItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.ExportJson") };
        exportJsonItem.Click += (_, _) => ExportSolutionJson();
        fileMenu.Items.Add(exportJsonItem);

        var exportPdfItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.ExportPdf") };
        exportPdfItem.Click += (_, _) => ExportPdf();
        fileMenu.Items.Add(exportPdfItem);

        var printItem = new ButtonMenuItem { Text = Localizer.T("Menu.Print.System") };
        printItem.Click += (_, _) => PrintSolution();
        fileMenu.Items.Add(printItem);

        var helpMenu = new ButtonMenuItem { Text = Localizer.T("Menu.Help") };
        var aboutItem = new ButtonMenuItem { Text = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.Items.Add(aboutItem);
        helpMenu.Items.Add(new SeparatorMenuItem());
        helpMenu.Items.Add(BuildLanguageMenu());

        var menu = new MenuBar();
        menu.Items.Add(fileMenu);
        menu.Items.Add(helpMenu);
        return menu;
    }

    /// <summary>语言子菜单：跟随系统 / 中文 / English。选择后保存偏好并重启进程（界面文字在构造时已固定）。</summary>
    ButtonMenuItem BuildLanguageMenu()
    {
        var preferred = LocalizationSwitch.PreferredCulture();
        var languageMenu = new ButtonMenuItem { Text = Localizer.T("Menu.Language") };

        void AddChoice(string? culture, string label)
        {
            var item = new CheckMenuItem
            {
                Text = label,
                Checked = preferred is null
                    ? culture is null
                    : string.Equals(preferred, culture, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ChangeLanguage(culture);
            languageMenu.Items.Add(item);
        }

        AddChoice(null, Localizer.T("Language.System"));
        AddChoice("zh-CN", Localizer.T("Language.Chinese"));
        AddChoice("en-US", Localizer.T("Language.English"));
        return languageMenu;
    }

    void ChangeLanguage(string? culture)
    {
        switch (LocalizationSwitch.Change(culture))
        {
            case LanguageChangeOutcome.Restarted:
                Close();
                break;
            case LanguageChangeOutcome.SaveFailed:
                SetStatus(Localizer.T("Language.SaveFailed"), error: true);
                break;
            default:
                MessageBox.Show(
                    this,
                    Localizer.T("Language.SavedRestartFailed", LocalizationSwitch.DisplayName(culture)),
                    Localizer.T("Menu.Language"));
                break;
        }
    }

    Control BuildToolBars()
    {
        var elements = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (label, key) in ElementButtons)
        {
            var typeKey = key;
            elements.Items.Add(MakeButton(label, () => Run(() => _designer.Kernel.AddElementAtCenter(typeKey)), Localizer.T("Toolbar.AddElement.Tooltip", label)));
        }

        var edit = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2 };
        // 按钮一律用短标签、完整说明放 ToolTip：一行能放下更多功能，分组之间插分隔线。
        // 导入按钮放最前面：工具条一行放不下时末尾的按钮会先被裁掉。
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Import"), ImportHiprintDialog, Localizer.T("Toolbar.Import.Tip")));
        edit.Items.Add(ToolbarSeparator());
        _undoButton = MakeButton(Localizer.T("Toolbar.Undo"), DoUndo, Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = MakeButton(Localizer.T("Toolbar.Redo"), DoRedo, Localizer.T("Toolbar.Redo.Tip"));
        edit.Items.Add(_undoButton);
        edit.Items.Add(_redoButton);
        edit.Items.Add(ToolbarSeparator());
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.Delete.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单仍保留同名项。
        edit.Items.Add(ToolbarSeparator());
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.ExportPdf"), ExportPdf, Localizer.T("Toolbar.ExportPdf.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.Preview.Tip")));

        var toggles = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6 };
        toggles.Items.Add(MakeOverlayCheck(Localizer.T("Toolbar.Grid"), value => _designer.Overlay.Options.ShowGrid = value));
        toggles.Items.Add(MakeOverlayCheck(Localizer.T("Toolbar.Ruler"), value => _designer.Overlay.Options.ShowRulers = value));
        toggles.Items.Add(MakeOverlayCheck(Localizer.T("Toolbar.SafeArea"), value => _designer.Overlay.Options.ShowMarginGuides = value));

        var tray = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Padding = new Padding(6, 2),
        };
        tray.Items.Add(elements);
        tray.Items.Add(edit);
        tray.Items.Add(toggles);
        return tray;
    }

    /// <summary>工具条分组分隔线：Eto 没有通用的 Separator 控件，用 1px 灰面板代替。</summary>
    static StackLayoutItem ToolbarSeparator() => new()
    {
        Control = new Panel
        {
            Size = new Size(1, 18),
            BackgroundColor = Colors.Silver,
        },
        VerticalAlignment = VerticalAlignment.Center,
    };

    CheckBox MakeOverlayCheck(string text, Action<bool> apply)
    {
        var check = new CheckBox { Text = text, Checked = true };
        check.CheckedChanged += (_, _) =>
        {
            apply(check.Checked == true);
            _designer.Invalidate();
            _designer.Focus();
        };

        return check;
    }

    /// <summary>
    /// 中部布局：嵌套两个 Splitter 实现「左栏 | 画布 | 属性栏」三块（Eto 的 Splitter 只有两栏），
    /// 左栏与属性栏宽度都能拖动。Orientation.Horizontal 表示两栏左右排布（分割线是竖线）。
    /// </summary>
    Control BuildCenterSplitter()
    {
        var canvasSplitter = new Splitter
        {
            Orientation = Orientation.Horizontal,
            FixedPanel = SplitterFixedPanel.Panel2, // 缩放窗口时固定属性栏宽度，画布吸收伸缩量
            SplitterWidth = SplitterThickness,
            Panel1 = _designer,
            Panel2 = _properties,
        };
        var sideSplitter = new Splitter
        {
            Orientation = Orientation.Horizontal,
            FixedPanel = SplitterFixedPanel.Panel1, // 缩放窗口时固定左栏宽度
            SplitterWidth = SplitterThickness,
            Panel1 = BuildLeftPanel(),
            Panel2 = canvasSplitter,
        };
        // Position 要等窗口显示、控件拿到真实宽度后再设：布局完成前分隔条还是默认尺寸，
        // WinForms 对 SplitterDistance 有区间校验，过早设置会被拒。
        // FixedPanel=Panel2 时 Position 仍从左边量，右栏 310px 对应的距离要用总宽反推。
        Shown += (_, _) =>
        {
            sideSplitter.Position = LeftPanelWidth;
            canvasSplitter.Position = Math.Max(
                canvasSplitter.Panel1MinimumSize,
                canvasSplitter.Width - PropertyPanelWidth);
        };
        return sideSplitter;
    }

    Control BuildLeftPanel()
    {
        var left = new TableLayout
        {
            Spacing = new Size(0, 4),
            Padding = new Padding(4, 2, 4, 2),
        };
        left.Rows.Add(new TableRow(new TableCell(SectionTitle(Localizer.T("Paper.Section"))) { ScaleWidth = true }));
        left.Rows.Add(new TableRow(new TableCell(BuildPaperPanel()) { ScaleWidth = true }));
        left.Rows.Add(new TableRow(new TableCell(SectionTitle(Localizer.T("Toolbox.Section"))) { ScaleWidth = true }));
        // 工具箱行撑满剩余高度：列表可用高度随窗口伸缩。
        left.Rows.Add(new TableRow(new TableCell(BuildToolbox()) { ScaleWidth = true }) { ScaleHeight = true });
        return left;
    }

    /// <summary>侧栏节标题：替代 GroupBox 外框，省掉边框和内边距占用的水平空间。</summary>
    static Label SectionTitle(string text) => new()
    {
        Text = text,
        Font = new Font(SystemFont.Bold),
    };

    Control BuildPaperPanel()
    {
        foreach (var choice in PaperChoices)
            _paperDropDown.Items.Add(choice.Label);

        _paperDropDown.SelectedIndexChanged += (_, _) => OnPaperChoiceChanged();

        _rotatePaperButton.Click += (_, _) => TogglePaperOrientation();
        _customPaperButton.Click += (_, _) => ShowCustomPaperDialog();

        // 连续纸独立开关：与预设正交（选 A4 再勾上就是"A4 宽的连续纸"）。走内核命令可撤销。
        _continuousToggle.CheckedChanged += (_, _) => TogglePaperContinuous(_continuousToggle.Checked == true);

        var buttons = new TableLayout { Spacing = new Size(4, 0) };
        buttons.Rows.Add(new TableRow(
            new TableCell(_rotatePaperButton) { ScaleWidth = true },
            new TableCell(_customPaperButton) { ScaleWidth = true }));

        var layout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(0, 0, 0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        layout.Items.Add(_paperDropDown);
        layout.Items.Add(buttons);
        layout.Items.Add(_continuousToggle);
        layout.Items.Add(_paperInfo);

        return layout;
    }

    Control BuildToolbox()
    {
        // 列表条目来自过滤后的 _toolboxEntries：搜索框按中英文名（或类型键）过滤后，
        // 选中项下标不再等于全量 ElementButtons 下标；双击（Activated）/回车或点「添加到画布」都加到画布中心。
        ApplyToolboxFilter(string.Empty);
        _toolbox.Activated += (_, _) => AddFromToolbox();

        var search = new TextBox { PlaceholderText = Localizer.T("Toolbox.Search") };
        search.TextChanged += (_, _) => ApplyToolboxFilter(search.Text);

        var add = MakeButton(Localizer.T("Toolbox.AddToCanvas"), AddFromToolbox, "把选中的元素添加到画布中心");
        var hint = new Label
        {
            Text = Localizer.T("Toolbox.HintDrag"),
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        var layout = new TableLayout
        {
            Spacing = new Size(4, 2),
            Padding = new Padding(0, 0, 0, 2),
        };
        layout.Rows.Add(new TableRow(search));
        layout.Rows.Add(new TableRow(new TableCell(_toolbox) { ScaleWidth = true }) { ScaleHeight = true });
        layout.Rows.Add(new TableRow(add));
        layout.Rows.Add(new TableRow(hint));

        HookToolboxDragDrop();
        return layout;
    }

    /// <summary>过滤后的工具箱条目：与 _toolbox.Items 一一对应（搜索过滤后的子集）。</summary>
    readonly List<(string Label, string Key)> _toolboxEntries = new();

    /// <summary>工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。</summary>
    void ApplyToolboxFilter(string? query)
    {
        _toolbox.Items.Clear();
        _toolboxEntries.Clear();
        foreach (var (label, key) in ElementButtons)
        {
            if (!ToolboxNames.Matches(key, query))
                continue;

            _toolbox.Items.Add(label);
            _toolboxEntries.Add((label, key));
        }
    }

    /// <summary>
    /// 工具箱条目拖入画布。载荷走 Text + 内置类型键白名单校验：只认 ElementTypeKeys.All 里的精确值，
    /// 别家拖普通文本进来不会误触发放置，也避开 Eto 各平台自定义格式支持不一的坑。
    /// </summary>
    void HookToolboxDragDrop()
    {
        _toolbox.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                _toolboxDragStart = e.Location;
        };
        _toolbox.MouseMove += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary || _toolbox.SelectedIndex < 0)
                return;

            // 越过 4px 阈值才算拖拽：单纯点击仍走双击/按钮的"加到中心"。
            if (Math.Abs(e.Location.X - _toolboxDragStart.X) < 4 && Math.Abs(e.Location.Y - _toolboxDragStart.Y) < 4)
                return;

            var entry = _toolboxEntries[_toolbox.SelectedIndex];
            _toolbox.DoDragDrop(new DataObject { Text = entry.Key }, DragEffects.Copy);
        };

        _designer.AllowDrop = true;
        _designer.DragOver += (_, e) =>
        {
            var key = e.Data?.Text;
            e.Effects = key is not null && ElementTypeKeys.All.Contains(key)
                ? DragEffects.Copy
                : DragEffects.None;
        };
        _designer.DragDrop += (_, e) =>
        {
            var key = e.Data?.Text;
            if (key is null || !ElementTypeKeys.All.Contains(key))
                return;

            // 从屏幕坐标换算控件内位置；逻辑 px × Dpi/96 → 设备 px（与右键命中同一口径）。
            var local = _designer.PointFromScreen(Mouse.Position);
            var scale = _designer.Dpi / 96.0;
            var mm = _designer.Kernel.Viewport.ToMm(local.X * scale, local.Y * scale, _designer.Dpi);
            Run(() => _designer.Kernel.AddElementAt(key, mm.X, mm.Y));
            SetStatus(Localizer.T("Status.ElementAddedCenter", Localizer.T(ToolboxNames.I18nKeyOf(key))));
        };
    }

    PointF _toolboxDragStart;

    Control BuildStatusBar()
    {
        var zoomBox = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2 };
        zoomBox.Items.Add(ZoomButton(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip")));
        zoomBox.Items.Add(ZoomButton(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip")));
        zoomBox.Items.Add(ZoomButton(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip")));
        zoomBox.Items.Add(ZoomButton(Localizer.T("Toolbar.FitToPage"), FitToPageFromStatus, Localizer.T("Toolbar.FitToPage.Tip")));

        // TableLayout 第三列 ScaleWidth=true：状态文本吃掉剩余宽度，缩放组不会被挤动。
        var row = new TableLayout { Spacing = new Size(8, 0) };
        row.Rows.Add(new TableRow(
            new TableCell(zoomBox),
            new TableCell(_zoomText),
            new TableCell(_status) { ScaleWidth = true }));
        return new Panel { Content = row, Padding = new Padding(10, 4) };
    }

    /// <summary>状态栏缩放按钮：显式固定宽度，短标签才不会被 Eto 的默认内边距吃掉。</summary>
    static Button ZoomButton(string text, Action action, string tip)
    {
        var button = new Button { Text = text, Width = 34, ToolTip = tip };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>「适合窗口」用宿主真实绘制表面尺寸；它不发 ViewChanged，读数要手动刷一次。</summary>
    void FitToPageFromStatus()
    {
        _designer.FitToPage();
        UpdateZoomText();
        _designer.Focus();
    }

    void DoUndo()
    {
        if (!EditorActions.Undo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToUndo"));
            return;
        }

        _designer.Focus();
        SetStatus(Localizer.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!EditorActions.Redo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToRedo"));
            return;
        }

        _designer.Focus();
        SetStatus(Localizer.T("Status.Redone"));
    }

    void UpdateUndoButtons()
    {
        if (_undoButton is null)
            return;

        _undoButton.Enabled = _designer.Kernel.CanUndo;
        _redoButton.Enabled = _designer.Kernel.CanRedo;
    }

    /// <summary>缩放比例读数：−/+/100% 都走内核 KeyAction，会发 ViewChanged 自动刷新。</summary>
    void UpdateZoomText()
        => _zoomText.Text = Localizer.T("Status.Zoom", EditorActions.ZoomPercent(_designer.Kernel));

    void BuildCanvasContextMenu()
    {
        void Add(string text, Keys shortcut, Action action, bool selectionItem)
        {
            var item = new ButtonMenuItem { Text = text };
            if (shortcut != Keys.None)
                item.Shortcut = shortcut;
            item.Click += (_, _) => action();
            if (selectionItem)
                _selectionMenuItems.Add(item);
            _canvasMenu.Items.Add(item);
        }

        Add(Localizer.T("Canvas.Context.Duplicate"), Keys.Control | Keys.D, DuplicateSelection, true);
        Add(Localizer.T("Canvas.Context.Delete"), Keys.Delete, DeleteSelection, true);
        _canvasMenu.Items.Add(new SeparatorMenuItem());
        Add(Localizer.T("Canvas.Context.BringForward"), Keys.None, () => Run(_designer.Kernel.BringForward), true);
        Add(Localizer.T("Canvas.Context.SendBackward"), Keys.None, () => Run(_designer.Kernel.SendBackward), true);
        Add("置于顶层", Keys.None, () => Run(_designer.Kernel.BringToFront), true);
        Add("置于底层", Keys.None, () => Run(_designer.Kernel.SendToBack), true);
        _canvasMenu.Items.Add(new SeparatorMenuItem());
        Add(Localizer.T("Canvas.Context.AlignCenter"), Keys.None, () =>
        {
            Run(_designer.Kernel.CenterSelectionHorizontally);
            SetStatus(Localizer.T("Status.HAlignCenter"));
        }, true);
        Add(Localizer.T("Canvas.Context.AlignMiddle"), Keys.None, () =>
        {
            Run(_designer.Kernel.CenterSelectionVertically);
            SetStatus(Localizer.T("Status.VAlignCenter"));
        }, true);
        _verticalCenterItem = _selectionMenuItems[^1];
        _canvasMenu.Items.Add(new SeparatorMenuItem());
        Add(Localizer.T("Toolbar.FitToPage"), Keys.None, () => Run(_designer.FitToPage), false);
        Add(Localizer.T("Canvas.Context.ActualSize"), Keys.None, ZoomActual, false);
        _canvasMenu.Items.Add(new SeparatorMenuItem());
        _rotatePaperItem = new ButtonMenuItem { Text = Localizer.T("Canvas.Context.RotateLandscape") };
        _rotatePaperItem.Click += (_, _) => TogglePaperOrientation();
        _canvasMenu.Items.Add(_rotatePaperItem);
        Add(Localizer.T("Canvas.Context.CustomPaper"), Keys.None, ShowCustomPaperDialog, false);

        // 菜单显示前同步可用状态；右键的命中/选中在 OnDesignerMouseUp 里完成。
        _canvasMenu.Opening += (_, _) => UpdateCanvasMenuState();
    }

    void OnDesignerMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Buttons != MouseButtons.Alternate)
            return;

        // e.Location 是控件内逻辑 px，内核要设备 px：与宿主 ToPointer 同一口径先按 Dpi/96 换算，
        // 否则非 100% 缩放下命中错位、菜单作用到错误的元素上。
        var scale = _designer.Dpi / 96.0;
        _designer.Kernel.SelectAt(new PointerPoint(e.Location.X * scale, e.Location.Y * scale, _designer.Dpi, PointerButton.Secondary));
        UpdateCanvasMenuState();
        _designer.Focus();
        _canvasMenu.Show(_designer, e.Location);
    }

    void OnDesignerKeyDown(object? sender, KeyEventArgs e)
    {
        // 撤销/重做与 Ctrl+D 同口径：画布有焦点时才生效，属性面板文本框里的按键不抢。
        // Ctrl+Shift+Z 与 Ctrl+Y 都算重做（与 Avalonia 基准一致）。
        if (e.Control && !e.Alt && (e.Key == Keys.Z || e.Key == Keys.Y))
        {
            if (e.Key == Keys.Y || e.Shift)
                DoRedo();
            else
                DoUndo();

            e.Handled = true;
            return;
        }

        // 阶段 E：Ctrl+S 立即保存。
        if (e.Control && !e.Alt && e.Key == Keys.S)
        {
            SaveSolution(false);
            e.Handled = true;
            return;
        }

        // Ctrl+D 只在设计画布有焦点时生效：属性面板的文本框里按键不该被抢。
        if (e.Key != Keys.D || !e.Control || e.Shift || e.Alt)
            return;

        DuplicateSelection();
        e.Handled = true;
    }

    void OnPaperChoiceChanged()
    {
        if (_syncingPaper || _paperDropDown.SelectedIndex < 0)
            return;

        var choice = PaperChoices[_paperDropDown.SelectedIndex];
        if (choice.Key is null)
        {
            ShowCustomPaperDialog();
            SyncPaperControls();
            return;
        }

        try
        {
            _designer.Kernel.SetPaperPreset(choice.Key);
            var paper = _designer.Kernel.Template.Paper;
            SetStatus(Localizer.T("Status.PaperSet", paper.PageWidthMm, paper.PageHeightMm));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxType.Warning);
        }

        SyncPaperControls();
        _designer.Focus();
    }

    void ShowCustomPaperDialog()
    {
        using var dialog = new PaperSizeDialog(_designer.Kernel.Template.Paper);
        dialog.ShowModal(this);
        if (!dialog.Accepted)
        {
            // 取消时把下拉恢复到当前纸张（用户可能刚选了“自定义…”项）。
            SyncPaperControls();
            _designer.Focus();
            return;
        }

        try
        {
            // 自定义纸张只改尺寸：是否连续纸由纸张属性里的独立开关决定，此处保持现状。
            var continuous = _designer.Kernel.Template.Paper.IsContinuous;
            _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm, continuous);
            SetStatus(continuous
                ? $"自定义纸张：宽 {dialog.WidthMm:0.##} mm，设计高度 {dialog.HeightMm:0.##} mm，打印长度随内容。"
                : $"自定义纸张：{dialog.WidthMm:0.##} × {dialog.HeightMm:0.##} mm。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxType.Warning);
        }

        SyncPaperControls();
        _designer.Focus();
    }

    /// <summary>连续纸独立开关：走内核命令（可撤销），处理器全程拉起重入保护挡住回填触发的嵌套事件。</summary>
    void TogglePaperContinuous(bool value)
    {
        if (_syncingPaper)
            return;
        _syncingPaper = true;
        try
        {
            _designer.Kernel.SetPaperContinuous(value);
            SetStatus(value
                ? "连续纸：打印长度随内容自适应（纸高只决定画布多高）。"
                : "固定纸高：按纸高分页打印。");
        }
        catch (Exception ex)
        {
            SetStatus("切换连续纸失败：" + ex.Message);
        }
        finally
        {
            _syncingPaper = false;
        }
        SyncPaperControls();
        _designer.Focus();
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus("连续纸没有固定页高，不能旋转。");
            _designer.Focus();
            return;
        }

        try
        {
            kernel.RotatePaper();
            SyncPaperControls();
            SetStatus(kernel.Template.Paper.Orientation == PaperOrientation.Landscape
                ? "纸张已转为横向。"
                : "纸张已转为纵向。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.RotateFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxType.Warning);
        }

        _designer.Focus();
    }

    void SyncPaperControls()
    {
        if (_syncingPaper)
            return;

        _syncingPaper = true;
        try
        {
            var paper = _designer.Kernel.Template.Paper;
            var index = Array.FindIndex(
                PaperChoices,
                choice => choice.Key is not null
                    && string.Equals(choice.Key, paper.Preset, StringComparison.OrdinalIgnoreCase));
            _paperDropDown.SelectedIndex = index >= 0 ? index : PaperChoices.Length - 1;

            var continuous = paper.IsContinuous;
            if (_continuousToggle.Checked != continuous)
                _continuousToggle.Checked = continuous;
            _rotatePaperButton.Enabled = !continuous;
            _rotatePaperButton.Text = paper.Orientation == PaperOrientation.Landscape ? Localizer.T("Canvas.Context.RotatePortrait") : Localizer.T("Paper.Rotate");

            _rotatePaperItem.Enabled = !continuous;
            _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape ? Localizer.T("Canvas.Context.RotatePortrait") : Localizer.T("Canvas.Context.RotateLandscape");

            _paperInfo.Text = DescribePaper(paper);
        }
        finally
        {
            _syncingPaper = false;
        }
    }

    /// <summary>右栏空白区持久实例：属性面板每次 Rebuild 复用同一棵控件树。</summary>
    Control BuildPageSection()
    {
        var stack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        stack.Items.Add(BuildWatermarkSection());
        return stack;
    }

    /// <summary>
    /// 水印设置区：启用/文字/字段绑定/字号/透明度/角度/间距/颜色。
    /// 校验、夹取、可撤销提交、回填统一走共享层 <see cref="WatermarkEdits"/>。
    /// </summary>
    Control BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox { Text = Localizer.T("Watermark.Enabled") };
        _watermarkEnabled.CheckedChanged += (_, _) =>
            CommitIfNotSyncing(() => WatermarkEdits.SetEnabled(_designer.Kernel, _watermarkEnabled.Checked == true));

        _watermarkText = WatermarkBox(() => WatermarkEdits.SetText(_designer.Kernel, _watermarkText.Text));
        _watermarkBinding = WatermarkBox(() => WatermarkEdits.SetBinding(_designer.Kernel, _watermarkBinding.Text));
        _watermarkFontSize = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.FontSizePt, _watermarkFontSize));
        _watermarkOpacity = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.Opacity, _watermarkOpacity));
        _watermarkRotation = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.RotationDeg, _watermarkRotation));
        _watermarkSpacing = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.SpacingMm, _watermarkSpacing));
        _watermarkColor = WatermarkBox(CommitWatermarkColor);

        var section = new TableLayout { Spacing = new Size(6, 4) };
        section.Rows.Add(new TableRow(new TableCell(new Label
        {
            Text = Localizer.T("Watermark.Section"),
            Font = new Font(SystemFont.Bold),
        })
        { ScaleWidth = true }));
        section.Rows.Add(new TableRow(new TableCell(_watermarkEnabled) { ScaleWidth = true }));
        AddWatermarkRow(section, Localizer.T("Watermark.Text"), _watermarkText);
        AddWatermarkRow(section, Localizer.T("Watermark.Binding"), _watermarkBinding);
        AddWatermarkRow(section, Localizer.T("Watermark.FontSize"), _watermarkFontSize);
        AddWatermarkRow(section, Localizer.T("Watermark.Opacity"), _watermarkOpacity);
        AddWatermarkRow(section, Localizer.T("Watermark.Rotation"), _watermarkRotation);
        AddWatermarkRow(section, Localizer.T("Watermark.Spacing"), _watermarkSpacing);
        AddWatermarkRow(section, Localizer.T("Watermark.Color"), _watermarkColor);

        RefreshWatermarkControls();
        return section;
    }

    static void AddWatermarkRow(TableLayout section, string label, Control editor)
        => section.Rows.Add(new TableRow(
            new TableCell(new Label { Text = label, TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center }),
            new TableCell(editor) { ScaleWidth = true }));

    /// <summary>水印输入框：失焦或回车提交；回填期间的事件由重入守卫挡掉。</summary>
    TextBox WatermarkBox(Action commit)
    {
        var box = new TextBox();
        box.LostFocus += (_, _) => CommitIfNotSyncing(commit);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            e.Handled = true;
            CommitIfNotSyncing(commit);
        };
        return box;
    }

    void CommitIfNotSyncing(Action commit)
    {
        if (!_syncingWatermark)
            commit();
    }

    void CommitWatermarkNumber(WatermarkNumber field, TextBox box)
    {
        if (!WatermarkEdits.SetNumber(_designer.Kernel, field, box.Text))
            RefreshWatermarkControls();
    }

    void CommitWatermarkColor()
    {
        if (!WatermarkEdits.SetColor(_designer.Kernel, _watermarkColor.Text))
            RefreshWatermarkControls();
    }

    /// <summary>换模板 / 撤销 / 重做后一次性把水印控件同步回模型值。</summary>
    void RefreshWatermarkControls()
    {
        if (_syncingWatermark || _watermarkEnabled is null)
            return;

        _syncingWatermark = true;
        try
        {
            var w = WatermarkEdits.Read(_designer.Kernel);
            _watermarkEnabled.Checked = w.Enabled;
            _watermarkText.Text = w.Text;
            _watermarkBinding.Text = w.Binding;
            _watermarkFontSize.Text = w.FontSizePt;
            _watermarkOpacity.Text = w.Opacity;
            _watermarkRotation.Text = w.RotationDeg;
            _watermarkSpacing.Text = w.SpacingMm;
            _watermarkColor.Text = w.Color;
        }
        finally
        {
            _syncingWatermark = false;
        }
    }

    void UpdateCanvasMenuState()
    {
        var paper = _designer.Kernel.Template.Paper;
        var hasSelection = !_designer.Kernel.Selection.IsEmpty;

        foreach (var item in _selectionMenuItems)
            item.Enabled = hasSelection;

        _verticalCenterItem.Enabled = hasSelection && !paper.IsContinuous;
        _rotatePaperItem.Enabled = !paper.IsContinuous;
        _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape ? Localizer.T("Canvas.Context.RotatePortrait") : Localizer.T("Canvas.Context.RotateLandscape");

        SyncPaperControls();
    }

    static string DescribePaper(PaperSpec paper)
    {
        var name = paper.Preset is null || paper.Preset == PaperTypePresets.Custom ? "自定义" : paper.Preset;
        return paper.IsContinuous
            ? $"{name}：{paper.PageWidthMm:0.##} mm 宽，长度随内容（固定纵向）"
            : $"{name}：{paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm（{(paper.Orientation == PaperOrientation.Landscape ? "横向" : "纵向")}）";
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"));
            _designer.Focus();
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.Focus();
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void DeleteSelection()
    {
        _designer.Kernel.DeleteSelection();
        _designer.Focus();
        SetStatus("已删除选中元素。");
    }

    /// <summary>把工具箱当前选中的元素添加到画布中心。</summary>
    void AddFromToolbox()
    {
        var index = _toolbox.SelectedIndex;
        if (index < 0 || index >= _toolboxEntries.Count)
        {
            SetStatus(Localizer.T("Status.SelectFirst"), error: true);
            return;
        }

        var (label, key) = _toolboxEntries[index];
        Run(() => _designer.Kernel.AddElementAtCenter(key));
        SetStatus(Localizer.T("Status.ElementAddedCenter", label));
    }

    void ZoomActual()
    {
        _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        _designer.Focus();
    }

    void Run(Action action)
    {
        action();
        _designer.Focus();
    }

    Control BuildTemplateJsonPanel()
    {
        var apply = MakeButton(Localizer.T("JsonPanel.Apply"), ApplyTemplateJson, Localizer.T("JsonPanel.ApplyTip"));
        var reload = MakeButton(
            Localizer.T("JsonPanel.Reload"),
            () => RefreshTemplateJson(Localizer.T("Status.JsonReloaded")),
            Localizer.T("JsonPanel.ReloadTip"));
        _jsonToggleButton.ToolTip = Localizer.T("JsonPanel.ToggleTip");
        _jsonToggleButton.Click += (_, _) => ToggleTemplateJsonPanel();

        // 标题带永远可见：切换按钮 + 应用/重新载入 + 状态文字，折叠时操作和提示也不缺位。
        var header = new TableLayout
        {
            Padding = new Padding(8, 2),
            Spacing = new Size(8, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(_jsonToggleButton),
                    new TableCell(new Label
                    {
                        Text = Localizer.T("JsonPanel.Title"),
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center,
                    }),
                    new TableCell(apply),
                    new TableCell(reload),
                    new TableCell(_jsonState) { ScaleWidth = true }),
            },
        };

        _templateJson.Wrap = false;
        _templateJson.Height = JsonEditorHeight;
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();
        _templateJson.KeyDown += OnTemplateJsonKeyDown;

        // 默认折叠成一条窄标题带，给画布让出竖向空间：
        // 控件 Visible=false 时不占布局空间，隐藏编辑区即可回收高度。
        // 显式给高度：Eto 的 Panel 有 UserPreferredSize 时按它参与 AutoSize 行的高度计算，
        // 否则嵌套的 TextArea 的 PreferredSize 在未挂载时可能是 0。
        _jsonEditorArea = new Panel
        {
            Content = _templateJson,
            Padding = new Padding(8, 2, 8, 4),
            Visible = false,
            Height = 0,
        };

        var layout = new TableLayout { Spacing = new Size(0, 0) };
        layout.Rows.Add(new TableRow(new TableCell(header) { ScaleWidth = true }));
        layout.Rows.Add(new TableRow(new TableCell(_jsonEditorArea) { ScaleWidth = true }));
        // Eto 的 TableLayout 总是把最后一行当作 Percent(1f)，AutoSize 的编辑区排最后会被算成 0 高。
        // 尾巴留一个空行占住拉伸位，编辑区这一行才按 PreferredSize 拿到真实高度。
        layout.Rows.Add(new TableRow(new TableCell(new Panel()) { ScaleWidth = true }));

        return new Panel { Content = layout, Padding = new Padding(0, 0, 0, 4) };
    }

    /// <summary>展开/收起底部 JSON 编辑区；「应用 JSON」「重新载入」留在标题带上，折叠时照样可用。</summary>
    void ToggleTemplateJsonPanel()
    {
        _jsonExpanded = !_jsonExpanded;
        _jsonEditorArea.Visible = _jsonExpanded;
        // 高度必须跟着一起变：行是 AutoSize，Visible 只是不参与布局，
        // 折叠后再展开时高度仍要由这里显式给出（TextArea 高度 + 上下内边距 2+4）。
        _jsonEditorArea.Height = _jsonExpanded ? JsonEditorHeight + 6 : 0;
        _jsonToggleButton.Text = Localizer.T(_jsonExpanded ? "JsonPanel.Collapse" : "JsonPanel.Expand");
    }

    /// <summary>把属性面板/纸张栏/JSON 面板全部接到当前内核上；LoadTemplate 换内核后必须重调。</summary>
    void BindKernel(string? jsonNote = null)
    {
        _designer.Kernel.TemplateChanged += SyncPaperControls;
        _designer.Kernel.TemplateChanged += OnKernelTemplateChanged;
        // 阶段 E：任何编辑都标记脏位（LoadTemplate 已换内核，这里重绑到新内核）。
        _designer.Kernel.TemplateChanged += MarkSolutionDirty;
        // 提交、撤销、重做、换模板都会发 TemplateChanged：水印控件统一从这里回读模型值。
        _designer.Kernel.TemplateChanged += RefreshWatermarkControls;
        // LoadTemplate 会换内核：撤销态与缩放读数的订阅必须每次重绑（与 Avalonia 基准一致）。
        _designer.Kernel.UndoStateChanged += UpdateUndoButtons;
        _designer.Kernel.ViewChanged += UpdateZoomText;
        _properties.Attach(_designer.Kernel);
        SyncPaperControls();
        RefreshWatermarkControls();
        UpdateUndoButtons();
        UpdateZoomText();
        RefreshTemplateJson(jsonNote);
    }

    void OnKernelTemplateChanged()
    {
        if (!_jsonDirty)
            RefreshTemplateJson();
    }

    void OnTemplateJsonEdited()
    {
        // 程序化赋值也会触发 TextChanged；和同步时留下的快照比对，避免误判成用户编辑。
        if (string.Equals(NormalizeJsonText(_templateJson.Text), _jsonSnapshot, StringComparison.Ordinal))
        {
            if (_jsonDirty)
            {
                _jsonDirty = false;
                SetJsonState($"已同步当前模板（{_designer.Kernel.Template.Elements.Count} 个元素）。", error: false);
            }
            return;
        }

        _jsonDirty = true;
        SetJsonState("有未应用的修改：点「应用 JSON」或按 Ctrl+Enter 生效。", error: false);
    }

    void OnTemplateJsonKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyData & Keys.KeyMask) == Keys.Enter && e.Modifiers.HasFlag(Keys.Control))
        {
            ApplyTemplateJson();
            e.Handled = true;
        }
    }

    void RefreshTemplateJson(string? note = null)
    {
        var text = TemplateJson.Serialize(_designer.Kernel.Template);
        _jsonSnapshot = NormalizeJsonText(text);
        _jsonDirty = false;
        _templateJson.Text = text;
        _templateJson.CaretIndex = 0;
        _templateJson.ScrollToStart();
        SetJsonState(note ?? $"已同步当前模板（{_designer.Kernel.Template.Elements.Count} 个元素）。", error: false);
    }

    static string NormalizeJsonText(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    void ApplyTemplateJson()
    {
        var text = _templateJson.Text ?? string.Empty;
        try
        {
            // 用户从 hiprint 设计器复制过来的 getJson 也是"模板 JSON"，直接在这里认掉。
            if (HiprintTemplateImporter.LooksLikeHiprintJson(text))
            {
                var imported = HiprintTemplateImporter.Import(text);
                var notes = imported.Issues
                    .Where(i => i.Severity != HiprintImportSeverity.Info)
                    .Select(i => i.Message)
                    .ToList();
                var summary = $"已按 hiprint 模板导入：{imported.Template.Elements.Count} 个元素";
                if (imported.Panels.Count > 1)
                    summary += $"（共 {imported.Panels.Count} 个面板，只导入第 {imported.Panels.First(p => p.Imported).Index} 个）";
                if (imported.Template.DesignData.Count > 0)
                    summary += $"；已保留 {imported.Template.DesignData.Count} 项测试数据（设计/预览时用于占位）";
                if (notes.Count > 0)
                    summary += "；" + string.Join("；", notes);

                // 摘要必须交给 BindKernel：换内核时 JSON 面板会被重写，直接 SetJsonState 会被覆盖。
                _designer.LoadTemplate(imported.Template);
                BindKernel(summary);
                SetJsonState(summary, error: imported.HasErrors);
                SetStatus("已把 hiprint 模板转换为 CrossPrint 模板。");
                _designer.Focus();
                return;
            }

            var load = TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant);
            var loadNote = load.Warnings.Count == 0
                ? $"已应用 JSON：{load.Template.Elements.Count} 个元素。"
                : $"已应用 JSON：{load.Template.Elements.Count} 个元素；{string.Join("；", load.Warnings)}";
            _designer.LoadTemplate(load.Template);
            BindKernel(loadNote);
            SetJsonState(loadNote, error: load.Warnings.Count > 0);
            SetStatus("已从底部 JSON 面板应用模板。");
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetJsonState("JSON 未应用：" + ex.Message, error: true);
        }
    }

    void SetJsonState(string text, bool error)
    {
        _jsonState.Text = text;
        _jsonState.TextColor = error ? Colors.Firebrick : Colors.DimGray;
    }

    void ImportHiprintDialog()
    {
        using var dialog = new OpenFileDialog { Title = Localizer.T("Dialog.Import.Title"), MultiSelect = false };
        dialog.Filters.Add(new FileFilter("hiprint 模板", ".js", ".json", ".txt"));
        dialog.Filters.Add(new FileFilter("所有文件", "*"));

        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            ImportHiprint(File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.HiprintImportFailed", ex.Message), error: true);
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Import.Title"), MessageBoxType.Error);
        }
    }

    void ImportHiprint(string text, string sourceName)
    {
        var result = HiprintTemplateImporter.Import(text);
        var note = DescribeImport(sourceName, result);

        // LoadTemplate 会换内核：属性面板、纸张同步和 JSON 面板都要接到新内核上。
        // 导入摘要交给 BindKernel，否则会被"已同步当前模板"覆盖，用户看不到跳过/降级原因。
        _designer.LoadTemplate(result.Template);
        BindKernel(note);

        _designer.Focus();
        SetStatus(note);
    }

    static string DescribeImport(string sourceName, HiprintImportResult result)
    {
        var elements = result.Template.Elements.Count;
        var designData = result.Template.DesignData.Count > 0
            ? $"；已保留 {result.Template.DesignData.Count} 项测试数据（设计/预览时用于占位）"
            : string.Empty;
        if (result.Issues.Count == 0)
            return $"已导入 {sourceName}：{elements} 个元素，无降级告警{designData}。";

        var skipped = result.Issues.Count(i => i.Severity == HiprintImportSeverity.Error);
        return $"已导入 {sourceName}：{elements} 个元素，{result.Issues.Count} 条导入告警（{skipped} 条跳过）{designData}。"
            + string.Join("；", result.Issues.Take(3));
    }

    // ===== 阶段 E/F：解决方案持久化 + 导出/打印 =====

    void MarkSolutionDirty()
    {
        _session.IsDirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        var mark = _session.IsDirty ? "*" : string.Empty;
        Title = $"{Localizer.T("App.Title.Short")} — Eto — {_session.CurrentName}{mark}";
    }

    void NewSolution()
    {
        _designer.LoadTemplate(PrintTemplate.CreateDefault(PaperTypePresets.A4));
        BindKernel("未命名模板");
        _session.New();
        SetStatus(Localizer.T("Status.NewSolution"));
        UpdateTitle();
    }

    void OpenSolutionDialog()
    {
        var records = _session.List();
        var listBox = new ListBox();
        foreach (var record in records)
            listBox.Items.Add(record.Name);

        var openButton = new Button { Text = Localizer.T("Dialog.Open") };
        var cancelButton = new Button { Text = Localizer.T("Dialog.Cancel") };
        var buttonPanel = new StackLayout
        {
            Orientation = Orientation.Horizontal,

            Spacing = 6,
            Padding = new Padding(6),
            Items = { cancelButton, openButton },
        };

        var layout = new DynamicLayout();
        layout.BeginVertical();
        layout.Add(listBox, yscale: true);
        layout.Add(buttonPanel);
        layout.EndVertical();

        var dialog = new Dialog
        {
            Title = Localizer.T("Dialog.OpenSolution"),
            Content = layout,
            Owner = this,
        };
        dialog.DefaultButton = openButton;
        dialog.AbortButton = cancelButton;

        void OpenSelected()
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < records.Count)
            {
                var rec = records[listBox.SelectedIndex];
                dialog.Close();
                OpenSolution(rec.Id);
            }
        }

        openButton.Click += (_, _) => OpenSelected();
        cancelButton.Click += (_, _) => dialog.Close();
        listBox.MouseDoubleClick += (_, _) => OpenSelected();

        dialog.ShowModal(this);
    }

    void OpenSolution(string id)
    {
        var record = _session.Load(id);
        if (record is null)
        {
            SetStatus(Localizer.T("Error.OpenFailed"), error: true);
            return;
        }

        var template = SolutionSession.Parse(record.Json);
        if (template is null)
        {
            SetStatus(Localizer.T("Error.OpenFailed"), error: true);
            return;
        }

        _designer.LoadTemplate(template);
        BindKernel(record.Name);
        _session.OpenRecord(record);
        SetStatus(Localizer.T("Status.Opened", record.Name));
        UpdateTitle();
        RefreshRecentMenu();
    }

    void RefreshRecentMenu()
    {
        _recentItem.Items.Clear();
        foreach (var record in _session.Recent(8))
        {
            var item = new ButtonMenuItem { Text = record.Name };
            item.Click += (_, _) => OpenSolution(record.Id);
            _recentItem.Items.Add(item);
        }

        if (_recentItem.Items.Count == 0)
            _recentItem.Items.Add(new ButtonMenuItem { Text = Localizer.T("Menu.File.NoRecent"), Enabled = false });
    }

    async Task<bool> SaveSolution(bool saveAs, bool silent = false)
    {
        string? name = null;
        if (saveAs || (_session.CurrentId is null && !silent))
        {
            name = PromptName(_session.CurrentName ?? "未命名模板");
            if (name is null)
                return false;
        }

        try
        {
            _session.SaveCurrent(_designer.Template, name);
            SetStatus(Localizer.T("Status.Saved", _session.CurrentName));
            ShowToast("保存成功", Localizer.T("Status.Saved", _session.CurrentName));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.SaveFailed", ex.Message), error: true);
            return false;
        }

        UpdateTitle();
        RefreshRecentMenu();
        return true;
    }

    string? PromptName(string defaultName)
    {
        var textBox = new TextBox { Text = defaultName };
        var okButton = new Button { Text = Localizer.T("Dialog.OK") };
        var cancelButton = new Button { Text = Localizer.T("Dialog.Cancel") };
        var buttonPanel = new StackLayout
        {
            Orientation = Orientation.Horizontal,

            Spacing = 6,
            Padding = new Padding(6),
            Items = { cancelButton, okButton },
        };

        var layout = new DynamicLayout();
        layout.BeginVertical();
        layout.Add(textBox);
        layout.Add(buttonPanel);
        layout.EndVertical();

        var dialog = new Dialog
        {
            Title = Localizer.T("Dialog.SaveAs"),
            Content = layout,
            Owner = this,
        };
        dialog.DefaultButton = okButton;
        dialog.AbortButton = cancelButton;

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = textBox.Text.Trim();
            if (string.IsNullOrEmpty(result))
                result = null;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.ShowModal(this);
        return result;
    }

    void ExportSolutionJson()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            FileName = "crossprint-template.json",
        };
        dialog.Filters.Add(new FileFilter("JSON 文件", ".json"));

        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, TemplateJson.Serialize(_designer.Template, indented: true));
            SetStatus(Localizer.T("Status.Exported", dialog.FileName));
            ShowToast("已导出模板", Localizer.T("Status.Exported", dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Export.Title"), MessageBoxType.Error);
        }
    }

    // 阶段 F：系统打印（SystemPrintTarget）：渲染临时 PDF 后交 OS 打印管线。
    bool _printing;

    void PrintSolution()
    {
        if (_printing)
        {
            SetStatus(Localizer.T("Status.PrintingBusy"), error: true);
            return;
        }

        _printing = true;
        SetStatus(Localizer.T("Status.PrintingStarting"));
        var issues = new RenderReport();
        var document = new CrossPrint.Printing.PrintDocument(_designer.Template, SampleData.Create());
        _ = PrintSolutionAsync(document, issues);
    }

    async Task PrintSolutionAsync(CrossPrint.Printing.PrintDocument document, RenderReport issues)
    {
        try
        {
            var result = await new SystemPrintTarget().PrintBatchAsync(new[] { document }, new SystemPrintOptions(), issues);
            SetStatus(result.Success
                ? (issues.HasIssues
                    ? Localizer.T("Status.PrintedWithWarnings", result.PrintedPages, issues.Issues.Count)
                    : Localizer.T("Status.Printed", result.PrintedPages))
                : Localizer.T("Error.PrintFailed", result.Message), error: !result.Success);
            if (result.Success)
                ShowToast("打印完成", issues.HasIssues
                    ? Localizer.T("Status.PrintedWithWarnings", result.PrintedPages, issues.Issues.Count)
                    : Localizer.T("Status.Printed", result.PrintedPages));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.PrintFailed", ex.Message), error: true);
        }
        finally
        {
            _printing = false;
        }
    }

    void ExportPdf()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            FileName = "crossprint-sample.pdf",
        };
        dialog.Filters.Add(new FileFilter("PDF 文件", ".pdf"));

        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            using var stream = File.Create(dialog.FileName);
            var report = PdfExporter.Export(
                _designer.Template,
                stream,
                data: SampleData.Create(),
                fontResolver: _designer.FontResolver,
                images: _designer.Images,
                renderer: _designer.Renderer);

            SetStatus(report.HasIssues
                ? $"已导出 PDF（{report.Issues.Count} 条渲染降级）：{dialog.FileName}"
                : $"已导出 PDF：{dialog.FileName}");
            ShowToast("已导出 PDF", dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Export.Title"), MessageBoxType.Error);
        }
    }

    void OpenPreview()
    {
        var preview = new PreviewControl
        {
            Template = _designer.Template.Clone(),
            Data = SampleData.Create(),
            FontResolver = _designer.FontResolver,
            Images = _designer.Images,
            Renderer = _designer.Renderer,
        };

        var buttons = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Items.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), preview.FitToPage));
        buttons.Items.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), () =>
        {
            preview.Kernel.Viewport.Reset();
            preview.Invalidate();
        }));

        var info = new Label
        {
            Text = "示例数据：title / orderNo / sku。预览与 PDF 导出共用同一条渲染路径。",
            TextColor = Colors.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = new TableLayout { Spacing = new Size(8, 0), Padding = new Padding(10, 5) };
        bar.Rows.Add(new TableRow(
            new TableCell(info) { ScaleWidth = true },
            new TableCell(buttons) { ScaleWidth = false }));

        var root = new TableLayout { Spacing = new Size(0, 0) };
        root.Rows.Add(new TableRow(new TableCell(bar) { ScaleWidth = true }));
        root.Rows.Add(new TableRow(new TableCell(preview) { ScaleWidth = true }) { ScaleHeight = true });

        using var dialog = new Dialog
        {
            Title = Localizer.T("Dialog.Preview.Title"),
            ClientSize = new Size(920, 1000),
            Content = root,
        };
        dialog.ShowModal(this);
    }

    /// <summary>显示关于对话框：产品名、版本、描述、第三方依赖列表。</summary>
    void ShowAboutDialog()
    {
        var productLabel = new Label
        {
            Text = AboutInfo.ProductName,
            Font = new Font(SystemFont.Bold, 18),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var versionLabel = new Label
        {
            Text = Localizer.T("About.Version", AboutInfo.Version),
            TextColor = Colors.DimGray,
        };

        var descriptionLabel = new Label
        {
            Text = Localizer.T("About.Description"),
            Wrap = WrapMode.Word,
            TextColor = Colors.DimGray,
        };

        var thirdPartyTitle = new Label
        {
            Text = Localizer.T("About.ThirdParty"),
            Font = new Font(SystemFont.Bold),
        };

        var thirdPartyLines = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
        foreach (var tp in AboutInfo.ThirdParty)
        {
            thirdPartyLines.Items.Add(new Label
            {
                Text = $"{tp.Name} — {tp.Description}（{tp.License}）",
                TextColor = Colors.DimGray,
                Wrap = WrapMode.Word,
            });
        }

        var closeButton = new Button { Text = Localizer.T("About.Close") };

        // 行必须在构造 TableLayout 之前备齐：Eto 的表布局按行数一次性建原生控件，事后追加的行不显示。
        var rows = new List<TableRow>();
        if (LogoLoader.GetLogoBytes() is { } logoBytes)
        {
            rows.Add(new TableRow(new TableCell(new ImageView
            {
                Image = new Bitmap(logoBytes),
                Size = new Size(96, 96),
            })));
        }

        rows.Add(new TableRow(new TableCell(productLabel)));
        rows.Add(new TableRow(new TableCell(versionLabel)));
        rows.Add(new TableRow(new TableCell(descriptionLabel)));
        rows.Add(new TableRow(new TableCell(thirdPartyTitle)));
        rows.Add(new TableRow(new TableCell(thirdPartyLines)) { ScaleHeight = true });
        rows.Add(new TableRow(
            new TableCell(null) { ScaleWidth = true },
            new TableCell(closeButton) { ScaleWidth = false }
        ));

        var body = new TableLayout(rows.ToArray()) { Spacing = new Size(0, 8), Padding = new Padding(16) };

        var dialog = new Dialog
        {
            Title = Localizer.T("About.Title"),
            ClientSize = new Size(520, 480),
            MinimumSize = new Size(400, 360),
            Content = body,
        };

        closeButton.Click += (_, _) => dialog.Close();
        dialog.ShowModal(this);
    }

    void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.TextColor = error ? Colors.Firebrick : Colors.DimGray;
    }

    // 阶段 G：右上角 Toast（成功/提示浮层），3 秒自动消失，不拦截鼠标；同一时刻只保留最新一条。
    void ShowToast(string title, string? message = null)
    {
        var stack = new StackLayout { Padding = new Padding(12, 8) };
        stack.Items.Add(new Label { Text = title, Font = Fonts.Sans(11, FontStyle.Bold), TextColor = Colors.Black });
        if (!string.IsNullOrEmpty(message))
            stack.Items.Add(new Label { Text = message, Font = Fonts.Sans(10), TextColor = Colors.DimGray, Wrap = WrapMode.Word });

        _toastCard = new Panel
        {
            Content = stack,
            BackgroundColor = Colors.White,
            Width = 320,
        };

        PositionToast();
        if (!_toastLayer.Controls.Contains(_toastCard))
            _toastLayer.Add(_toastCard, ClientSize.Width - 320 - 16, 56);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void PositionToast()
    {
        if (_toastCard is null)
            return;
        _toastLayer.Remove(_toastCard);
        _toastLayer.Add(_toastCard, ClientSize.Width - 320 - 16, 56);
    }

    static Button MakeButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Text = text };
        if (tooltip is not null)
            button.ToolTip = tooltip;

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>模板库对话框：左侧分类 + 右侧缩略图卡片网格，点击卡片套用模板。</summary>
    void OpenTemplateLibrary()
    {
        var entries = BuiltinTemplateCatalog.BuildEntries();
        var byCategory = new Dictionary<string, List<BuiltinTemplateCatalog.Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byCategory.TryGetValue(entry.Category, out var list))
                byCategory[entry.Category] = list = new List<BuiltinTemplateCatalog.Entry>();
            list.Add(entry);
        }

        var categoryList = new ListBox { Width = 160 };
        categoryList.Items.Add("全部");
        foreach (var c in BuiltinTemplateCatalog.Categories)
            categoryList.Items.Add(c);
        categoryList.SelectedIndex = 0;

        const int Columns = 3;
        var cardTable = new TableLayout { Spacing = new Size(8, 8), Padding = new Padding(8) };
        var scroll = new Scrollable { Content = cardTable };

        Dialog? dialog = null;

        void ApplyFromLibrary(BuiltinTemplateCatalog.Entry entry)
        {
            _designer.LoadTemplate(entry.Create());
            BindKernel(entry.Name);
            SetStatus(Localizer.T("Status.TemplateApplied", entry.Name));
        }

        TableLayout BuildCard(BuiltinTemplateCatalog.Entry entry)
        {
            var card = new TableLayout { Spacing = new Size(0, 4), Padding = new Padding(8) };
            try
            {
                var bmp = new Bitmap(new MemoryStream(BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create())));
                card.Rows.Add(new TableRow(new TableCell(new ImageView { Image = bmp, Size = new Size(200, 96) }) { ScaleWidth = true }));
            }
            catch
            {
                // 缩略图渲染失败不阻断选单本身。
            }

            card.Rows.Add(new TableRow(new TableCell(new Label
            {
                Text = entry.Name,
                Font = new Font(SystemFont.Bold),
                Wrap = WrapMode.None,
            })));
            card.Rows.Add(new TableRow(new TableCell(new Label
            {
                Text = entry.Description,
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
            })));
            card.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                {
                    dialog!.Close();
                    ApplyFromLibrary(entry);
                }
            };
            return card;
        }

        void ShowCards(string category)
        {
            cardTable.Rows.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            for (var i = 0; i < list.Count; i += Columns)
            {
                var cells = new List<TableCell>();
                for (var j = 0; j < Columns && i + j < list.Count; j++)
                    cells.Add(new TableCell(BuildCard(list[i + j])) { ScaleWidth = true });
                cardTable.Rows.Add(new TableRow(cells.ToArray()));
            }
        }

        categoryList.SelectedIndexChanged += (_, _) =>
        {
            if (categoryList.SelectedIndex >= 0)
                ShowCards(categoryList.Items[categoryList.SelectedIndex]?.ToString() ?? "全部");
        };
        ShowCards("全部");

        var body = new TableLayout();
        body.Rows.Add(new TableRow(
            new TableCell(categoryList) { ScaleWidth = false },
            new TableCell(scroll) { ScaleWidth = true }) { ScaleHeight = true });

        var hint = new Label { Text = Localizer.T("TemplateLibrary.Hint"), TextColor = Colors.Gray };
        var root = new TableLayout();
        root.Padding = new Padding(4);
        root.Spacing = new Size(0, 4);
        root.Rows.Add(new TableRow(new TableCell(hint) { ScaleWidth = true }));
        root.Rows.Add(new TableRow(new TableCell(body) { ScaleWidth = true }) { ScaleHeight = true });

        dialog = new Dialog
        {
            Title = Localizer.T("TemplateLibrary.Title"),
            ClientSize = new Size(1080, 720),
            Content = root,
            Owner = this,
        };
        dialog.ShowModal(this);
    }
}
