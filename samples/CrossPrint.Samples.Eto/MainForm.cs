using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Eto;
using CrossPrint.I18n;
using Eto.Drawing;
using Eto.Forms;

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
    readonly PropertyPanel _properties;
    readonly Label _status = new() { TextColor = Colors.DimGray };

    readonly ContextMenu _canvasMenu = new();
    readonly List<MenuItem> _selectionMenuItems = [];
    readonly DropDown _paperDropDown = new();
    readonly ListBox _toolbox = new();
    readonly Button _rotatePaperButton = new() { Text = Localizer.T("Paper.Rotate") };
    readonly Button _customPaperButton = new() { Text = Localizer.T("Paper.CustomBtn") };
    readonly Label _paperInfo = new() { TextColor = Colors.DimGray, Wrap = WrapMode.Word };
    readonly TextArea _templateJson = new();
    readonly Label _jsonState = new() { TextColor = Colors.DimGray, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _jsonToggleButton = new() { Text = "展开" };
    Panel _jsonEditorArea = null!;

    MenuItem _verticalCenterItem = null!;
    MenuItem _rotatePaperItem = null!;
    bool _syncingPaper;
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
        var root = new TableLayout { Spacing = new Size(0, 0) };
        root.Rows.Add(new TableRow(new TableCell(BuildToolBars()) { ScaleWidth = true }));
        var midRow = new TableRow(new TableCell(BuildCenterSplitter()) { ScaleWidth = true }) { ScaleHeight = true };
        root.Rows.Add(midRow);
        root.Rows.Add(new TableRow(new TableCell(BuildTemplateJsonPanel()) { ScaleWidth = true }));
        root.Rows.Add(new TableRow(new TableCell(BuildStatusBar()) { ScaleWidth = true }));
        Content = root;
        Menu = BuildMenuBar();

        BuildCanvasContextMenu();
        // 用 MouseDown 而不是 MouseUp：Eto 的 MouseUp 在部分平台不带按键位，
        // 右键判定会落空；按下时的 Buttons 一定含 Alternate。
        _designer.MouseDown += OnDesignerMouseDown;
        _designer.KeyDown += OnDesignerKeyDown;
        BindKernel();

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

        var loadComplexItem = new ButtonMenuItem { Text = "加载复杂测试模板" };
        loadComplexItem.Click += (_, _) => LoadComplexReceiptTemplate();
        fileMenu.Items.Add(loadComplexItem);

        var importItem = new ButtonMenuItem { Text = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => ImportHiprintDialog();
        fileMenu.Items.Add(importItem);

        var helpMenu = new ButtonMenuItem { Text = Localizer.T("Menu.Help") };
        var aboutItem = new ButtonMenuItem { Text = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.Items.Add(aboutItem);

        var menu = new MenuBar();
        menu.Items.Add(fileMenu);
        menu.Items.Add(helpMenu);
        return menu;
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
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.Delete.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        edit.Items.Add(ToolbarSeparator());
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), () => Run(_designer.FitToPage), Localizer.T("Toolbar.FitToPage.Tip")));
        edit.Items.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), ZoomActual, Localizer.T("Toolbar.ZoomActual.Tip")));
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
        layout.Items.Add(_paperInfo);

        return layout;
    }

    Control BuildToolbox()
    {
        // 工具箱列表与 ElementButtons 一一对应：选中项下标即元素类型，
        // 双击（Activated）/回车或点「添加到画布」都加到画布中心。
        foreach (var (label, _) in ElementButtons)
            _toolbox.Items.Add(label);

        _toolbox.Activated += (_, _) => AddFromToolbox();

        var add = MakeButton(Localizer.T("Toolbox.AddToCanvas"), AddFromToolbox, "把选中的元素添加到画布中心");
        var hint = new Label
        {
            Text = Localizer.T("Status.DefaultTip"),
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        var layout = new TableLayout
        {
            Spacing = new Size(4, 2),
            Padding = new Padding(0, 0, 0, 2),
        };
        layout.Rows.Add(new TableRow(new TableCell(_toolbox) { ScaleWidth = true }) { ScaleHeight = true });
        layout.Rows.Add(new TableRow(add));
        layout.Rows.Add(new TableRow(hint));

        return layout;
    }

    Control BuildStatusBar()
    {
        return new Panel
        {
            Content = _status,
            Padding = new Padding(10, 4),
        };
    }

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
            _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm);
            SetStatus(dialog.HeightMm > 0
                ? $"自定义纸张：{dialog.WidthMm:0.##} × {dialog.HeightMm:0.##} mm。"
                : $"自定义连续纸：宽 {dialog.WidthMm:0.##} mm，长度随内容自适应。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxType.Warning);
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
        if (index < 0)
        {
            SetStatus(Localizer.T("Status.SelectFirst"), error: true);
            return;
        }

        var (label, key) = ElementButtons[index];
        Run(() => _designer.Kernel.AddElementAtCenter(key));
        SetStatus(Localizer.T("Status.ElementAddedCenter", label));
    }

    /// <summary>加载 80mm 连续纸长小票复杂模板：写法照抄 kitchen-sink 的 LoadSampleTemplate，失败时状态栏提示。</summary>
    void LoadComplexReceiptTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "complex-receipt.template.json");
        if (!File.Exists(path))
        {
            SetStatus(Localizer.T("Status.ComplexNotFound"), error: true);
            return;
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            // LoadTemplate 会换内核：属性面板、纸张同步和 JSON 面板都要接到新内核上。
            _designer.LoadTemplate(result.Template);
            BindKernel(Localizer.T("Status.ComplexLoaded"));

            var status = Localizer.T("Status.ComplexLoadedInfo", result.Template.Elements.Count);
            if (result.Warnings.Count > 0)
                status += "；" + string.Join("；", result.Warnings);
            SetStatus(status);
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Status.ComplexLoadFailed", ex.Message), error: true);
        }
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
        var apply = MakeButton("应用 JSON", ApplyTemplateJson, "解析文本框里的模板 JSON 并替换当前模板");
        var reload = MakeButton("重新载入", () => RefreshTemplateJson("已重新载入当前模板，未应用的编辑已丢弃。"), "丢弃编辑，重新显示当前模板");
        _jsonToggleButton.ToolTip = "展开或收起模板 JSON 编辑区";
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
        _jsonEditorArea = new Panel
        {
            Content = _templateJson,
            Padding = new Padding(8, 2, 8, 4),
            Visible = false,
        };

        var layout = new TableLayout { Spacing = new Size(0, 0) };
        layout.Rows.Add(new TableRow(new TableCell(header) { ScaleWidth = true }));
        layout.Rows.Add(new TableRow(new TableCell(_jsonEditorArea) { ScaleWidth = true }));

        return new Panel { Content = layout, Padding = new Padding(0, 0, 0, 4) };
    }

    /// <summary>展开/收起底部 JSON 编辑区；「应用 JSON」「重新载入」留在标题带上，折叠时照样可用。</summary>
    void ToggleTemplateJsonPanel()
    {
        _jsonExpanded = !_jsonExpanded;
        _jsonEditorArea.Visible = _jsonExpanded;
        _jsonToggleButton.Text = _jsonExpanded ? "收起" : "展开";
    }

    /// <summary>把属性面板/纸张栏/JSON 面板全部接到当前内核上；LoadTemplate 换内核后必须重调。</summary>
    void BindKernel(string? jsonNote = null)
    {
        _designer.Kernel.TemplateChanged += SyncPaperControls;
        _designer.Kernel.TemplateChanged += OnKernelTemplateChanged;
        _properties.Attach(_designer.Kernel);
        SyncPaperControls();
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

        var body = new TableLayout { Spacing = new Size(0, 8), Padding = new Padding(16) };
        body.Rows.Add(new TableRow(new TableCell(productLabel)));
        body.Rows.Add(new TableRow(new TableCell(versionLabel)));
        body.Rows.Add(new TableRow(new TableCell(descriptionLabel)));
        body.Rows.Add(new TableRow(new TableCell(thirdPartyTitle)));
        body.Rows.Add(new TableRow(new TableCell(thirdPartyLines)) { ScaleHeight = true });
        body.Rows.Add(new TableRow(
            new TableCell(null) { ScaleWidth = true },
            new TableCell(closeButton) { ScaleWidth = false }
        ));

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

    static Button MakeButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Text = text };
        if (tooltip is not null)
            button.ToolTip = tooltip;

        button.Click += (_, _) => action();
        return button;
    }
}
