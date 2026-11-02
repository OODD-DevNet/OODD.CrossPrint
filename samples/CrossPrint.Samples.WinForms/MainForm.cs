using System.ComponentModel;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.WinForms;
using CrossPrint.I18n;

namespace CrossPrint.Samples.WinForms;

/// <summary主窗口：顶部菜单/工具栏，左侧纸张 + 工具箱（可拖宽），中间设计画布，右侧属性面板（可拖宽），底部可折叠模板 JSON 带，状态栏。</summary>
public sealed class MainForm : Form
{
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
    readonly ToolStripStatusLabel _status;
    readonly SplitContainer _leftSplit;
    readonly SplitContainer _rightSplit;
    readonly ListBox _toolbox = new();
    readonly ComboBox _paperCombo = new();
    readonly Button _rotateButton = new();
    readonly Button _customPaperButton = new();
    readonly Label _paperInfo = new();
    readonly ContextMenuStrip _canvasMenu = new();
    readonly List<ToolStripMenuItem> _selectionMenuItems = [];
    readonly TextBox _templateJson = new();
    readonly Label _jsonState = new();
    readonly Button _jsonToggle = new();
    Panel _jsonPanel = null!;
    bool _jsonExpanded;

    // 底部 JSON 面板：折叠后只留 JsonBarHeight 一条标题带；展开总高约 230px（含标题带），与 WPF 版基准一致。
    const int JsonBarHeight = 32;
    const int JsonExpandedHeight = 230;
    ToolStripMenuItem _verticalCenterItem = null!;
    ToolStripMenuItem _rotatePaperItem = null!;

    bool _syncingPaper;
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;

    /// <summary>创建主窗口并加载 kitchen-sink 示例模板。</summary>
    public MainForm()
    {
        Text = Localizer.T("App.Title.Short") + " — WinForms";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1320, 880);
        MinimumSize = new Size(1024, 640);
        KeyPreview = true;

        var template = LoadInitialTemplate(out var loadMessages);

        _status = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _designer = new DesignerControl(template) { Dock = DockStyle.Fill };

        // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
        // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
        _designer.Data = SampleData.Create();

        _properties = new PropertyPanel(_designer.Kernel) { Dock = DockStyle.Fill };

        // 左右两道分割条都可拖：外层分「左栏 | 其余」（左栏钉 Panel1），内层分「画布 | 属性」（属性栏钉 Panel2），
        // 默认左栏 200px、属性栏 310px（与 WPF 基准一致），中间画布吃掉剩余宽度。
        // Panel2MinSize 不能在控件还没有真实宽度时设：初始宽度只有 150 左右，内部会把 SplitterDistance
        // 调到 Panel1MinSize 以下直接抛异常，所以统一放到 OnShown 再收紧最小宽度、设分割位置。
        _leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 150,
            FixedPanel = FixedPanel.Panel1,
        };
        _leftSplit.Panel1.Controls.Add(BuildLeftPanel());

        _rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
        };
        _rightSplit.Panel1.Controls.Add(_designer);
        _rightSplit.Panel2.Controls.Add(BuildPropertyPanel());

        _leftSplit.Panel2.Controls.Add(_rightSplit);

        BuildCanvasContextMenu();

        Controls.Add(_leftSplit);
        Controls.Add(BuildToolbar());
        // 状态条后加会先停靠到底部，JSON 面板随后停靠，就叠在状态条上方。
        Controls.Add(BuildTemplateJsonPanel());
        Controls.Add(BuildStatusStrip());

        // 顶部菜单最后加入：停靠按逆序处理，菜单条才会贴在工具栏上方。
        MainMenuStrip = BuildMenuStrip();
        Controls.Add(MainMenuStrip);

        KeyDown += OnMainKeyDown;
        BindKernel();

        SetStatus(loadMessages.Count == 0
            ? Localizer.T("App.Status.Ready")
            : Localizer.T("App.Status.Warnings", string.Join("；", loadMessages)));
    }

    /// <inheritdoc/>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // SplitterDistance 必须等首次布局（控件有真实宽度）后再设，过早设会抛异常或被夹成一条缝；
        // 先定左栏 200px，内层宽度才稳定；属性栏再按「内层宽 - 310 - 分割条宽」算，SplitterWidth 本身也占 4px。
        _leftSplit.SplitterDistance = 200;
        _rightSplit.Panel2MinSize = 240;
        _rightSplit.SplitterDistance = _leftSplit.Panel2.Width - 310 - _rightSplit.SplitterWidth;
    }

    sealed record PaperChoice(string? Key, string Label);

    sealed record ToolboxItem(string Label, string TypeKey);

    static PrintTemplate LoadInitialTemplate(out List<string> messages)
    {
        messages = new List<string>();
        var path = Path.Combine(AppContext.BaseDirectory, "kitchen-sink.template.json");
        if (!File.Exists(path))
        {
            messages.Add($"未找到 {Path.GetFileName(path)}，已使用 A4 默认模板。");
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            messages.AddRange(result.Warnings);
            return result.Template;
        }
        catch (Exception ex)
        {
            messages.Add("模板加载失败，已使用 A4 默认模板：" + ex.Message);
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>左栏（默认 200px，可拖宽）：上方纸张节，下方工具箱；属性面板在右栏。</summary>
    Control BuildLeftPanel()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 纸张节按内容高度固定（节标题 + 下拉 + 按钮行 + 描述），工具箱吃掉剩余全部。
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(BuildPaperPanel(), 0, 0);
        left.Controls.Add(BuildToolbox(), 0, 1);
        return left;
    }

    /// <summary>右栏（默认 310px，可拖宽）：选中元素的属性面板。</summary>
    Control BuildPropertyPanel()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1, 0, 0, 0),
        };
        host.Controls.Add(_properties);
        return host;
    }

    Control BuildPaperPanel()
    {
        // 不用 GroupBox：边框 + 标题条占竖向空间，侧栏改用节标题 Label 更紧凑。
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Label
        {
            Text = Localizer.T("Paper.Section"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 2),
        };
        panel.Controls.Add(header, 0, 0);
        panel.SetColumnSpan(header, 2);

        _paperCombo.Dock = DockStyle.Fill;
        _paperCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _paperCombo.Margin = new Padding(0, 2, 0, 4);
        foreach (var choice in PaperChoices)
            _paperCombo.Items.Add(choice.Label);
        _paperCombo.SelectedIndexChanged += (_, _) => OnPaperChoiceChanged();
        panel.Controls.Add(_paperCombo, 0, 1);
        panel.SetColumnSpan(_paperCombo, 2);

        _rotateButton.Text = Localizer.T("Paper.Rotate");
        _rotateButton.Dock = DockStyle.Fill;
        _rotateButton.Margin = new Padding(0, 2, 3, 2);
        _rotateButton.Click += (_, _) => TogglePaperOrientation();
        panel.Controls.Add(_rotateButton, 0, 2);

        _customPaperButton.Text = Localizer.T("Paper.CustomBtn");
        _customPaperButton.Dock = DockStyle.Fill;
        _customPaperButton.Margin = new Padding(3, 2, 0, 2);
        _customPaperButton.Click += (_, _) => ShowCustomPaperDialog();
        panel.Controls.Add(_customPaperButton, 1, 2);

        _paperInfo.AutoSize = false;
        _paperInfo.Dock = DockStyle.Fill;
        _paperInfo.ForeColor = SystemColors.GrayText;
        _paperInfo.Margin = new Padding(0);
        _paperInfo.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_paperInfo, 0, 3);
        panel.SetColumnSpan(_paperInfo, 2);

        return panel;
    }

    /// <summary>工具箱：ListBox 列出元素类型，双击或回车添加到画布中心。</summary>
    Control BuildToolbox()
    {
        // 不用 GroupBox：与纸张节一致，用节标题 Label，省掉边框与标题条占位。
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
        };

        var header = new Label
        {
            Text = Localizer.T("Toolbox.Section"),
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 2),
        };

        _toolbox.Dock = DockStyle.Fill;
        _toolbox.DisplayMember = nameof(ToolboxItem.Label);
        foreach (var (label, key) in ElementButtons)
            _toolbox.Items.Add(new ToolboxItem(label, key));
        _toolbox.DoubleClick += (_, _) => AddFromToolbox();
        _toolbox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddFromToolbox();
                e.Handled = true;
            }
        };

        var addButton = new Button
        {
            Text = Localizer.T("Toolbox.AddToCanvas"),
            Dock = DockStyle.Bottom,
            Height = 26,
        };
        addButton.Click += (_, _) => AddFromToolbox();

        var hint = new Label
        {
            Text = Localizer.T("Status.DefaultTip"),
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        };

        // 停靠按逆序处理：标题带最后加入才先贴顶，提示与按钮依次压到底部，ListBox 最后处理才占满剩余区域。
        panel.Controls.Add(_toolbox);
        panel.Controls.Add(addButton);
        panel.Controls.Add(hint);
        panel.Controls.Add(header);
        return panel;
    }

    void AddFromToolbox()
    {
        if (_toolbox.SelectedItem is not ToolboxItem item)
        {
            SetStatus(Localizer.T("Status.SelectFirst"));
            return;
        }

        _designer.Kernel.AddElementAtCenter(item.TypeKey);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ElementAddedCenter", item.Label));
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、复杂测试模板加载）。</summary>
    MenuStrip BuildMenuStrip()
    {
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem(Localizer.T("Menu.File"));
        var importItem = new ToolStripMenuItem(Localizer.T("Menu.File.ImportHiprint"));
        importItem.Click += (_, _) => ImportHiprintDialog();
        fileMenu.DropDownItems.Add(importItem);

        var loadComplexItem = new ToolStripMenuItem(Localizer.T("Menu.File.LoadComplex"));
        loadComplexItem.Click += (_, _) => LoadComplexSampleTemplate();
        fileMenu.DropDownItems.Add(loadComplexItem);

        menu.Items.Add(fileMenu);

        var helpMenu = new ToolStripMenuItem(Localizer.T("Menu.Help"));
        var aboutItem = new ToolStripMenuItem(Localizer.T("Menu.Help.About"));
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.DropDownItems.Add(aboutItem);
        menu.Items.Add(helpMenu);

        return menu;
    }

    ToolStrip BuildToolbar()
    {
        // 紧凑工具带：短标签 + ToolTip 承载完整说明，功能组之间用分隔符，窄窗口时尽量少折进溢出菜单。
        // 事件绑定与业务逻辑保持不变。
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(4, 1, 4, 1),
            ShowItemToolTips = true,
        };

        void AddAction(string text, Action action, string? tooltip = null)
        {
            var button = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = true,
                ToolTipText = tooltip ?? string.Empty,
            };
            button.Click += (_, _) => action();
            toolbar.Items.Add(button);
        }

        void AddSeparator() => toolbar.Items.Add(new ToolStripSeparator());

        // 导入按钮放最前面：ToolStrip 右侧会折进溢出菜单，放末尾等于藏起来。
        AddAction(Localizer.T("Toolbar.Import"), ImportHiprintDialog, Localizer.T("Toolbar.Import.Tip"));
        AddSeparator();

        foreach (var (label, key) in ElementButtons)
            AddAction(label, () => AddElement(key), Localizer.T("Toolbar.AddElement.Tooltip", label));

        AddSeparator();
        AddAction(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip"));
        AddAction(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.Delete.Tip"));
        AddSeparator();
        AddAction(Localizer.T("Toolbar.BringForward"), () => RunKernel(kernel => kernel.BringForward()), Localizer.T("Toolbar.BringForward.Tip"));
        AddAction(Localizer.T("Toolbar.SendBackward"), () => RunKernel(kernel => kernel.SendBackward()), Localizer.T("Toolbar.SendBackward.Tip"));
        AddAction(Localizer.T("Toolbar.BringToFront"), () => RunKernel(kernel => kernel.BringToFront()), Localizer.T("Toolbar.BringToFront.Tip"));
        AddAction(Localizer.T("Toolbar.SendToBack"), () => RunKernel(kernel => kernel.SendToBack()), Localizer.T("Toolbar.SendToBack.Tip"));

        AddSeparator();
        AddAction(Localizer.T("Toolbar.FitToPage"), () =>
        {
            _designer.FitToPage();
            _designer.Focus();
        }, Localizer.T("Toolbar.FitToPage.Tip"));
        AddAction(Localizer.T("Toolbar.ZoomActual"), () =>
        {
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
            _designer.Focus();
        }, Localizer.T("Toolbar.ZoomActual.Tip"));

        AddSeparator();
        toolbar.Items.Add(BuildToggle(Localizer.T("Toolbar.Grid"), true, value => _designer.Overlay.Options.ShowGrid = value, Localizer.T("Toolbar.Grid.Tip")));
        toolbar.Items.Add(BuildToggle(Localizer.T("Toolbar.Ruler"), true, value => _designer.Overlay.Options.ShowRulers = value, Localizer.T("Toolbar.Ruler.Tip")));
        toolbar.Items.Add(BuildToggle(Localizer.T("Toolbar.SafeArea"), true, value => _designer.Overlay.Options.ShowMarginGuides = value, Localizer.T("Toolbar.SafeArea.Tip")));

        AddSeparator();
        AddAction(Localizer.T("Toolbar.ExportPdf"), ExportPdf, Localizer.T("Toolbar.ExportPdf.Tip"));
        AddAction(Localizer.T("Toolbar.Preview"), ShowPreview, Localizer.T("Toolbar.Preview.Tip"));

        return toolbar;
    }

    ToolStripButton BuildToggle(string text, bool initial, Action<bool> apply, string? tooltip = null)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = true,
            Checked = initial,
            AutoSize = true,
            ToolTipText = tooltip ?? string.Empty,
        };
        button.CheckedChanged += (_, _) =>
        {
            apply(button.Checked);
            _designer.Invalidate();
        };
        return button;
    }

    StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);
        return strip;
    }

    void AddElement(string typeKey)
    {
        _designer.Kernel.AddElementAtCenter(typeKey);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ElementAdded", typeKey));
    }

    void DeleteSelection()
    {
        _designer.Kernel.DeleteSelection();
        _designer.Focus();
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"));
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.Focus();
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void RunKernel(Action<EditorKernel> action)
    {
        action(_designer.Kernel);
        _designer.Focus();
    }

    void OnMainKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+D 只在设计画布有焦点时生效：属性面板的文本框里按键不该被抢。
        if (!_designer.Focused || !e.Control || e.Shift || e.Alt || e.KeyCode != Keys.D)
            return;

        e.SuppressKeyPress = true;
        e.Handled = true;
        DuplicateSelection();
    }

    void BuildCanvasContextMenu()
    {
        void Add(string text, string? shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
            item.Click += (_, _) => action();
            _canvasMenu.Items.Add(item);
        }

        void AddSelection(string text, string? shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
            item.Click += (_, _) => action();
            _selectionMenuItems.Add(item);
            _canvasMenu.Items.Add(item);
        }

        AddSelection(Localizer.T("Canvas.Context.Duplicate"), "Ctrl+D", DuplicateSelection);
        AddSelection(Localizer.T("Canvas.Context.Delete"), "Delete", DeleteSelection);
        _canvasMenu.Items.Add(new ToolStripSeparator());
        AddSelection(Localizer.T("Canvas.Context.BringForward"), null, () => RunKernel(kernel => kernel.BringForward()));
        AddSelection(Localizer.T("Canvas.Context.SendBackward"), null, () => RunKernel(kernel => kernel.SendBackward()));
        AddSelection(Localizer.T("Canvas.Context.BringToFront"), null, () => RunKernel(kernel => kernel.BringToFront()));
        AddSelection(Localizer.T("Canvas.Context.SendToBack"), null, () => RunKernel(kernel => kernel.SendToBack()));
        _canvasMenu.Items.Add(new ToolStripSeparator());
        AddSelection(Localizer.T("Canvas.Context.AlignCenter"), null, () =>
        {
            RunKernel(kernel => kernel.CenterSelectionHorizontally());
            SetStatus(Localizer.T("Status.HAlignCenter"));
        });
        _verticalCenterItem = new ToolStripMenuItem(Localizer.T("Canvas.Context.AlignMiddle"));
        _verticalCenterItem.Click += (_, _) =>
        {
            RunKernel(kernel => kernel.CenterSelectionVertically());
            SetStatus(Localizer.T("Status.VAlignCenter"));
        };
        _selectionMenuItems.Add(_verticalCenterItem);
        _canvasMenu.Items.Add(_verticalCenterItem);
        _canvasMenu.Items.Add(new ToolStripSeparator());
        Add(Localizer.T("Toolbar.FitToPage"), null, () =>
        {
            _designer.FitToPage();
            _designer.Focus();
        });
        Add(Localizer.T("Canvas.Context.ActualSize"), null, () =>
        {
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
            _designer.Focus();
        });
        _canvasMenu.Items.Add(new ToolStripSeparator());
        _rotatePaperItem = new ToolStripMenuItem(Localizer.T("Canvas.Context.RotateLandscape"));
        _rotatePaperItem.Click += (_, _) => TogglePaperOrientation();
        _canvasMenu.Items.Add(_rotatePaperItem);
        Add(Localizer.T("Canvas.Context.CustomPaper"), null, ShowCustomPaperDialog);

        _canvasMenu.Opening += OnCanvasMenuOpening;
        _designer.ContextMenuStrip = _canvasMenu;
    }

    void OnCanvasMenuOpening(object? sender, CancelEventArgs e)
    {
        // 右键位置命中元素即单选它、命中空白则清选：菜单里的"副本/层次"才有作用对象。
        var client = _designer.PointToClient(Cursor.Position);
        if (_designer.ClientRectangle.Contains(client))
            _designer.Kernel.SelectAt(new PointerPoint(client.X, client.Y, _designer.Dpi, PointerButton.Secondary));

        var hasSelection = !_designer.Kernel.Selection.IsEmpty;
        foreach (var item in _selectionMenuItems)
            item.Enabled = hasSelection;

        var paper = _designer.Kernel.Template.Paper;
        _verticalCenterItem.Enabled = hasSelection && !paper.IsContinuous;
        _rotatePaperItem.Enabled = !paper.IsContinuous;
        _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Canvas.Context.RotatePortrait")
            : Localizer.T("Canvas.Context.RotateLandscape");

        SyncPaperControls();
    }

    void OnPaperChoiceChanged()
    {
        if (_syncingPaper || _paperCombo.SelectedIndex < 0)
            return;

        var choice = PaperChoices[_paperCombo.SelectedIndex];
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
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        SyncPaperControls();
        _designer.Focus();
    }

    void ShowCustomPaperDialog()
    {
        using var dialog = new PaperSizeDialog(_designer.Kernel.Template.Paper);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm);
            SetStatus(dialog.HeightMm > 0
                ? Localizer.T("Status.CustomPaperSet", dialog.WidthMm, dialog.HeightMm)
                : Localizer.T("Status.CustomContinuousPaperSet", dialog.WidthMm));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        SyncPaperControls();
        _designer.Focus();
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus(Localizer.T("Status.ContinuousCannotRotate"));
            return;
        }

        kernel.RotatePaper();
        SyncPaperControls();
        SetStatus(kernel.Template.Paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Status.PaperLandscape")
            : Localizer.T("Status.PaperPortrait"));
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
            _paperCombo.SelectedIndex = index >= 0 ? index : PaperChoices.Length - 1;

            var continuous = paper.IsContinuous;
            _rotateButton.Enabled = !continuous;
            _rotateButton.Text = paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Paper.RotatePortrait")
                : Localizer.T("Paper.Rotate");

            if (_rotatePaperItem is not null)
            {
                _rotatePaperItem.Enabled = !continuous;
                _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape
                    ? Localizer.T("Canvas.Context.RotatePortrait")
                    : Localizer.T("Canvas.Context.RotateLandscape");
            }
            _paperInfo.Text = DescribePaper(paper);
        }
        finally
        {
            _syncingPaper = false;
        }
    }

    static string DescribePaper(PaperSpec paper)
    {
        var name = paper.Preset is null || paper.Preset == PaperTypePresets.Custom
            ? Localizer.T("Paper.Custom")
            : paper.Preset;
        var orientation = paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Paper.Orientation.Landscape")
            : Localizer.T("Paper.Orientation.Portrait");
        return paper.IsContinuous
            ? $"{name}：{paper.PageWidthMm:0.##} mm 宽，长度随内容（{orientation}）"
            : $"{name}：{paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm（{orientation}）";
    }

    /// <summary>底部模板 JSON 面板：默认折叠成一条标题带（切换按钮 + 应用/重载 + 状态文字），展开后显示 JSON 编辑框。</summary>
    Control BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起把竖向空间还给画布（对齐 WPF 版）。
        _jsonToggle.Text = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.AutoSize = true;
        _jsonToggle.Anchor = AnchorStyles.Left;
        _jsonToggle.FlatStyle = FlatStyle.Flat;
        _jsonToggle.FlatAppearance.BorderSize = 0;
        _jsonToggle.Font = new Font(Font, FontStyle.Bold);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(!_jsonExpanded);

        var apply = new Button
        {
            Text = Localizer.T("JsonPanel.Apply"),
            AutoSize = true,
            Padding = new Padding(6, 1, 6, 1),
            Anchor = AnchorStyles.Left,
        };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Text = Localizer.T("JsonPanel.Reload"),
            AutoSize = true,
            Padding = new Padding(6, 1, 6, 1),
            Margin = new Padding(4, 0, 0, 0),
            Anchor = AnchorStyles.Left,
        };
        reload.Click += (_, _) => RefreshTemplateJson(Localizer.T("Status.JsonReloaded"));

        _jsonState.AutoSize = false;
        _jsonState.Dock = DockStyle.Fill;
        _jsonState.TextAlign = ContentAlignment.MiddleLeft;
        _jsonState.ForeColor = Color.DimGray;
        _jsonState.AutoEllipsis = true;
        _jsonState.Padding = new Padding(10, 0, 0, 0);

        // 标题带固定高度：折叠态它就是面板的全部，展开态它是面板顶部的一条；收起时应用/重载仍可用。
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = JsonBarHeight,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        header.Controls.Add(_jsonToggle, 0, 0);
        header.Controls.Add(apply, 1, 0);
        header.Controls.Add(reload, 2, 0);
        header.Controls.Add(_jsonState, 3, 0);

        _templateJson.Multiline = true;
        _templateJson.AcceptsReturn = true;
        _templateJson.WordWrap = false;
        _templateJson.ScrollBars = ScrollBars.Both;
        _templateJson.Font = new Font("Consolas", 9F);
        _templateJson.Dock = DockStyle.Fill;
        // 底部留白走编辑框自己的 Margin：面板 Padding 若给 bottom，折叠态 32px 客户区装不下 32px 标题带，按钮会被裁。
        _templateJson.Margin = new Padding(0, 0, 0, 6);
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();
        _templateJson.KeyDown += OnTemplateJsonKeyDown;

        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = JsonBarHeight,
            Padding = new Padding(8, 0, 8, 0),
            BackColor = Color.White,
        };
        panel.Controls.Add(_templateJson);
        panel.Controls.Add(header);

        _jsonPanel = panel;
        SetJsonPanelExpanded(false);
        return panel;
    }

    void SetJsonPanelExpanded(bool expanded)
    {
        _jsonExpanded = expanded;
        _jsonToggle.Text = (expanded ? "▾ " : "▸ ") + Localizer.T("JsonPanel.Title");
        _templateJson.Visible = expanded;
        // 收起只留标题带（32px）；展开总高约 230px，编辑区占余下全部。
        _jsonPanel.Height = expanded ? JsonExpandedHeight : JsonBarHeight;
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
                SetJsonState(Localizer.T("JsonPanel.Sync", _designer.Kernel.Template.Elements.Count), error: false);
            }
            return;
        }

        _jsonDirty = true;
        SetJsonState(Localizer.T("JsonPanel.Dirty"), error: false);
    }

    void OnTemplateJsonKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Enter)
        {
            ApplyTemplateJson();
            e.SuppressKeyPress = true;
        }
    }

    void RefreshTemplateJson(string? note = null)
    {
        var text = TemplateJson.Serialize(_designer.Kernel.Template);
        _jsonSnapshot = NormalizeJsonText(text);
        _jsonDirty = false;
        _templateJson.Text = text;
        _templateJson.SelectionStart = 0;
        _templateJson.SelectionLength = 0;
        if (_templateJson.IsHandleCreated)
            _templateJson.ScrollToCaret();
        SetJsonState(note ?? Localizer.T("JsonPanel.Sync", _designer.Kernel.Template.Elements.Count), error: false);
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
                var summary = Localizer.T("Status.HiprintImportedSummary", imported.Template.Elements.Count);
                if (imported.Panels.Count > 1)
                    summary += Localizer.T("Status.HiprintImportedPanels", imported.Panels.Count, imported.Panels.First(p => p.Imported).Index);
                if (imported.Template.DesignData.Count > 0)
                    summary += Localizer.T("Status.HiprintImportedDesignData", imported.Template.DesignData.Count);
                if (notes.Count > 0)
                    summary += "；" + string.Join("；", notes);

                // 摘要必须交给 BindKernel：换内核时 JSON 面板会被重写，直接 SetJsonState 会被覆盖。
                _designer.LoadTemplate(imported.Template);
                BindKernel(summary);
                SetJsonState(summary, error: imported.HasErrors);
                SetStatus(Localizer.T("Status.HiprintImported"));
                _designer.Focus();
                return;
            }

            var load = TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant);
            var loadNote = load.Warnings.Count == 0
                ? Localizer.T("Status.JsonApplied", load.Template.Elements.Count)
                : Localizer.T("Status.JsonAppliedWithWarnings", load.Template.Elements.Count, string.Join("；", load.Warnings));
            _designer.LoadTemplate(load.Template);
            BindKernel(loadNote);
            SetJsonState(loadNote, error: load.Warnings.Count > 0);
            SetStatus(Localizer.T("Status.JsonAppliedFromPanel"));
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetJsonState(Localizer.T("Status.JsonNotApplied", ex.Message), error: true);
        }
    }

    void SetJsonState(string text, bool error)
    {
        _jsonState.Text = text;
        _jsonState.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    void ImportHiprintDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Localizer.T("Dialog.Import.Title"),
            Filter = "hiprint 模板 (*.js;*.json;*.txt)|*.js;*.json;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            ImportHiprint(File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.HiprintImportFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Localizer.T("Error.HiprintImportFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            ? Localizer.T("Status.HiprintImportedDesignData", result.Template.DesignData.Count)
            : string.Empty;
        if (result.Issues.Count == 0)
            return Localizer.T("Status.ImportedNoWarnings", sourceName, elements) + designData + "。";

        var skipped = result.Issues.Count(i => i.Severity == HiprintImportSeverity.Error);
        return Localizer.T("Status.ImportedWithWarnings", sourceName, elements, result.Issues.Count, skipped)
            + designData + "。"
            + string.Join("；", result.Issues.Take(3));
    }

    /// <summary>读取输出目录下的复杂小票模板（80mm 连续纸长小票），替换当前设计器模板。</summary>
    void LoadComplexSampleTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "complex-receipt.template.json");
        if (!File.Exists(path))
        {
            SetStatus(Localizer.T("Status.ComplexNotFound"));
            return;
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            var note = Localizer.T("Status.ComplexLoadedInfo", result.Template.Elements.Count);
            if (result.Warnings.Count > 0)
                note += "；" + string.Join("；", result.Warnings);

            // LoadTemplate 会换内核：属性面板、纸张同步和 JSON 面板都要接到新内核上（同 hiprint 导入）。
            _designer.LoadTemplate(result.Template);
            BindKernel(note);
            SetStatus(note);
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Status.ComplexLoadFailed", ex.Message));
        }
    }

    void ExportPdf()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            FileName = "crossprint-sample.pdf",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
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
                ? Localizer.T("Status.ExportedWithWarnings", dialog.FileName, report.Issues.Count)
                : Localizer.T("Status.Exported", dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Localizer.T("Error.ExportFailed", ex.Message),
                Localizer.T("Toolbar.ExportPdf"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    void ShowPreview()
    {
        var preview = new PreviewForm(_designer.Template, SampleData.Create());
        preview.Show(this);
    }

    /// <summary>显示关于对话框。</summary>
    void ShowAboutDialog()
    {
        using var dlg = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            Text = Localizer.T("About.Title"),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 480),
        };

        // 主内容面板（顶部标题/版本/描述 + 中部第三方列表 + 底部关闭按钮占位）
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 10),
            AutoScroll = true,
        };

        var productName = new Label
        {
            Text = AboutInfo.ProductName,
            Font = new Font(dlg.Font.FontFamily, 16f, FontStyle.Bold),
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 28,
            Margin = new Padding(0, 4, 0, 2),
        };

        var versionLabel = new Label
        {
            Text = Localizer.T("About.Version", AboutInfo.Version),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Dock = DockStyle.Top,
            Height = 20,
            Margin = new Padding(0, 0, 0, 6),
        };

        var description = new Label
        {
            Text = Localizer.T("About.Description"),
            AutoSize = true,
            MaximumSize = new Size(472, 0),
            ForeColor = Color.DimGray,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 10),
        };

        var thirdPartyLabel = new Label
        {
            Text = Localizer.T("About.ThirdParty"),
            Font = new Font(dlg.Font, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Top,
            Height = 22,
            Margin = new Padding(0, 6, 0, 4),
        };

        var thirdPartyPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
        };

        foreach (var tp in AboutInfo.ThirdParty)
        {
            var line = new Label
            {
                Text = $"  • {tp.Name}  ({tp.License}) — {tp.Description}",
                AutoSize = true,
                MaximumSize = new Size(472, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 1, 0, 1),
            };
            thirdPartyPanel.Controls.Add(line);
        }

        body.Controls.Add(thirdPartyPanel);
        body.Controls.Add(thirdPartyLabel);
        body.Controls.Add(description);
        body.Controls.Add(versionLabel);
        body.Controls.Add(productName);

        // 底部关闭按钮带
        var buttonBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
        };
        var closeButton = new Button
        {
            Text = Localizer.T("About.Close"),
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Width = 88,
            Height = 30,
            Location = new Point(buttonBar.ClientSize.Width - 88 - 12, 8),
        };
        buttonBar.Controls.Add(closeButton);

        dlg.Controls.Add(body);
        dlg.Controls.Add(buttonBar);
        dlg.AcceptButton = closeButton;

        dlg.Shown += (_, _) =>
        {
            closeButton.Location = new Point(buttonBar.ClientSize.Width - closeButton.Width - 12, 7);
        };

        dlg.ShowDialog(this);
    }

    void SetStatus(string text) => _status.Text = text;
}
