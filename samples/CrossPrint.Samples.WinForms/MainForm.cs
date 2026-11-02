using System.ComponentModel;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.WinForms;

namespace CrossPrint.Samples.WinForms;

/// <summary>主窗口：顶部菜单/工具栏，左侧纸张 + 工具箱（可拖宽），中间设计画布，右侧属性面板（可拖宽），底部可折叠模板 JSON 带，状态栏。</summary>
public sealed class MainForm : Form
{
    static readonly (string Label, string Key)[] ElementButtons =
    [
        ("文字", ElementTypeKeys.Text),
        ("线段", ElementTypeKeys.Line),
        ("矩形", ElementTypeKeys.Rectangle),
        ("椭圆", ElementTypeKeys.Ellipse),
        ("图片", ElementTypeKeys.Image),
        ("二维码", ElementTypeKeys.QrCode),
        ("一维码", ElementTypeKeys.Barcode),
        ("表格", ElementTypeKeys.Table),
    ];

    static readonly PaperChoice[] PaperChoices =
    [
        new(PaperTypePresets.A3, "A3（297×420）"),
        new(PaperTypePresets.A4, "A4（210×297）"),
        new(PaperTypePresets.A5, "A5（148×210）"),
        new(PaperTypePresets.B5, "B5（176×250）"),
        new(PaperTypePresets.Receipt80, "80mm 连续纸"),
        new(PaperTypePresets.Receipt58, "58mm 连续纸"),
        new(PaperTypePresets.Label100x150, "100×150 面单"),
        new(null, "自定义…"),
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
        Text = "CrossPrint WinForms 示例";
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
            ? "已加载 kitchen-sink.template.json；点击元素可选中，拖动或方向键调整位置。"
            : "模板告警：" + string.Join("；", loadMessages));
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
            Text = "纸张",
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

        _rotateButton.Text = "转横向";
        _rotateButton.Dock = DockStyle.Fill;
        _rotateButton.Margin = new Padding(0, 2, 3, 2);
        _rotateButton.Click += (_, _) => TogglePaperOrientation();
        panel.Controls.Add(_rotateButton, 0, 2);

        _customPaperButton.Text = "自定义…";
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
            Text = "工具箱",
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
            Text = "添加到画布",
            Dock = DockStyle.Bottom,
            Height = 26,
        };
        addButton.Click += (_, _) => AddFromToolbox();

        var hint = new Label
        {
            Text = "双击工具添加到画布中心。",
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
            SetStatus("请先在工具箱里选择一个元素类型。");
            return;
        }

        _designer.Kernel.AddElementAtCenter(item.TypeKey);
        _designer.Focus();
        SetStatus($"已添加{item.Label}元素；右侧属性面板可直接编辑。");
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、复杂测试模板加载）。</summary>
    MenuStrip BuildMenuStrip()
    {
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("文件(&F)");
        var importItem = new ToolStripMenuItem("导入 hiprint 模板…");
        importItem.Click += (_, _) => ImportHiprintDialog();
        fileMenu.DropDownItems.Add(importItem);

        var loadComplexItem = new ToolStripMenuItem("加载复杂测试模板");
        loadComplexItem.Click += (_, _) => LoadComplexSampleTemplate();
        fileMenu.DropDownItems.Add(loadComplexItem);

        menu.Items.Add(fileMenu);
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
        AddAction("导入", ImportHiprintDialog, "导入 hiprint / vue-plugin-hiprint 导出的模板");
        AddSeparator();

        foreach (var (label, key) in ElementButtons)
            AddAction(label, () => AddElement(key), "添加" + label + "元素");

        AddSeparator();
        AddAction("副本", DuplicateSelection, "创建选中元素的副本（Ctrl+D）");
        AddAction("删除", DeleteSelection, "删除选中元素（Delete）");
        AddSeparator();
        AddAction("上移", () => RunKernel(kernel => kernel.BringForward()), "Z 序上移一层");
        AddAction("下移", () => RunKernel(kernel => kernel.SendBackward()), "Z 序下移一层");
        AddAction("置顶", () => RunKernel(kernel => kernel.BringToFront()), "置于顶层");
        AddAction("置底", () => RunKernel(kernel => kernel.SendToBack()), "置于底层");

        AddSeparator();
        AddAction("适合窗口", () =>
        {
            _designer.FitToPage();
            _designer.Focus();
        }, "整页适配到窗口");
        AddAction("100%", () =>
        {
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
            _designer.Focus();
        }, "实际大小显示");

        AddSeparator();
        toolbar.Items.Add(BuildToggle("网格", true, value => _designer.Overlay.Options.ShowGrid = value, "显示设计网格"));
        toolbar.Items.Add(BuildToggle("标尺", true, value => _designer.Overlay.Options.ShowRulers = value, "显示标尺"));
        toolbar.Items.Add(BuildToggle("安全区", true, value => _designer.Overlay.Options.ShowMarginGuides = value, "显示页边距安全参考线"));

        AddSeparator();
        AddAction("导出 PDF", ExportPdf, "导出当前模板为 PDF（绑定示例数据）");
        AddAction("预览", ShowPreview, "打开打印预览窗口");

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
        SetStatus($"已添加元素（{typeKey}）：拖动移动，方向键微调，Delete 删除。");
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
            SetStatus("先选中元素再创建副本。");
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.Focus();
        SetStatus($"已创建副本：向页内偏移 {EditorKernel.DuplicateOffsetMm:0.#} mm 并选中副本。");
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

        AddSelection("创建副本", "Ctrl+D", DuplicateSelection);
        AddSelection("删除", "Delete", DeleteSelection);
        _canvasMenu.Items.Add(new ToolStripSeparator());
        AddSelection("上移一层", null, () => RunKernel(kernel => kernel.BringForward()));
        AddSelection("下移一层", null, () => RunKernel(kernel => kernel.SendBackward()));
        AddSelection("置于顶层", null, () => RunKernel(kernel => kernel.BringToFront()));
        AddSelection("置于底层", null, () => RunKernel(kernel => kernel.SendToBack()));
        _canvasMenu.Items.Add(new ToolStripSeparator());
        AddSelection("水平居中", null, () =>
        {
            RunKernel(kernel => kernel.CenterSelectionHorizontally());
            SetStatus("选中元素已在页面上水平居中。");
        });
        _verticalCenterItem = new ToolStripMenuItem("垂直居中");
        _verticalCenterItem.Click += (_, _) =>
        {
            RunKernel(kernel => kernel.CenterSelectionVertically());
            SetStatus("选中元素已在页面上垂直居中。");
        };
        _selectionMenuItems.Add(_verticalCenterItem);
        _canvasMenu.Items.Add(_verticalCenterItem);
        _canvasMenu.Items.Add(new ToolStripSeparator());
        Add("适合窗口", null, () =>
        {
            _designer.FitToPage();
            _designer.Focus();
        });
        Add("实际大小（100%）", null, () =>
        {
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
            _designer.Focus();
        });
        _canvasMenu.Items.Add(new ToolStripSeparator());
        _rotatePaperItem = new ToolStripMenuItem("旋转为横向");
        _rotatePaperItem.Click += (_, _) => TogglePaperOrientation();
        _canvasMenu.Items.Add(_rotatePaperItem);
        Add("自定义纸张…", null, ShowCustomPaperDialog);

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
        _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape ? "旋转为纵向" : "旋转为横向";

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
            SetStatus($"纸张已设为 {_designer.Kernel.Template.Paper.PageWidthMm:0.##} × {_designer.Kernel.Template.Paper.PageHeightMm:0.##} mm。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "设置纸张失败：" + ex.Message, "纸张", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                ? $"自定义纸张：{dialog.WidthMm:0.##} × {dialog.HeightMm:0.##} mm。"
                : $"自定义连续纸：宽 {dialog.WidthMm:0.##} mm，长度随内容自适应。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "设置纸张失败：" + ex.Message, "纸张", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            return;
        }

        kernel.RotatePaper();
        SyncPaperControls();
        SetStatus(kernel.Template.Paper.Orientation == PaperOrientation.Landscape
            ? "纸张已转为横向。"
            : "纸张已转为纵向。");
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
            _rotateButton.Text = paper.Orientation == PaperOrientation.Landscape ? "转纵向" : "转横向";

            if (_rotatePaperItem is not null)
            {
                _rotatePaperItem.Enabled = !continuous;
                _rotatePaperItem.Text = paper.Orientation == PaperOrientation.Landscape ? "旋转为纵向" : "旋转为横向";
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
        var name = paper.Preset is null || paper.Preset == PaperTypePresets.Custom ? "自定义" : paper.Preset;
        return paper.IsContinuous
            ? $"{name}：{paper.PageWidthMm:0.##} mm 宽，长度随内容（固定纵向）"
            : $"{name}：{paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm（{(paper.Orientation == PaperOrientation.Landscape ? "横向" : "纵向")}）";
    }

    /// <summary>底部模板 JSON 面板：默认折叠成一条标题带（切换按钮 + 应用/重载 + 状态文字），展开后显示 JSON 编辑框。</summary>
    Control BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起把竖向空间还给画布（对齐 WPF 版）。
        _jsonToggle.Text = "▸ 模板 JSON";
        _jsonToggle.AutoSize = true;
        _jsonToggle.Anchor = AnchorStyles.Left;
        _jsonToggle.FlatStyle = FlatStyle.Flat;
        _jsonToggle.FlatAppearance.BorderSize = 0;
        _jsonToggle.Font = new Font(Font, FontStyle.Bold);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(!_jsonExpanded);

        var apply = new Button
        {
            Text = "应用 JSON",
            AutoSize = true,
            Padding = new Padding(6, 1, 6, 1),
            Anchor = AnchorStyles.Left,
        };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Text = "重新载入",
            AutoSize = true,
            Padding = new Padding(6, 1, 6, 1),
            Margin = new Padding(4, 0, 0, 0),
            Anchor = AnchorStyles.Left,
        };
        reload.Click += (_, _) => RefreshTemplateJson("已重新载入当前模板，未应用的编辑已丢弃。");

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
        _jsonToggle.Text = expanded ? "▾ 模板 JSON" : "▸ 模板 JSON";
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
                SetJsonState($"已同步当前模板（{_designer.Kernel.Template.Elements.Count} 个元素）。", error: false);
            }
            return;
        }

        _jsonDirty = true;
        SetJsonState("有未应用的修改：点「应用 JSON」或按 Ctrl+Enter 生效。", error: false);
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
        _jsonState.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    void ImportHiprintDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入 hiprint 模板",
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
            SetStatus("导入 hiprint 模板失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "导入 hiprint 模板失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>读取输出目录下的复杂小票模板（80mm 连续纸长小票），替换当前设计器模板。</summary>
    void LoadComplexSampleTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "complex-receipt.template.json");
        if (!File.Exists(path))
        {
            SetStatus("未找到 complex-receipt.template.json。");
            return;
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            var note = $"已加载 complex-receipt.template.json（80mm 连续纸长小票，{result.Template.Elements.Count} 个元素）：滚轮/滚动条查看全页。";
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
            SetStatus("加载 complex-receipt.template.json 失败：" + ex.Message);
        }
    }

    void ExportPdf()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出 PDF",
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
                ? $"已导出 PDF：{dialog.FileName}（{report.Issues.Count} 条渲染告警）"
                : $"已导出 PDF：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "导出 PDF 失败：" + ex.Message,
                "导出失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    void ShowPreview()
    {
        var preview = new PreviewForm(_designer.Template, SampleData.Create());
        preview.Show(this);
    }

    void SetStatus(string text) => _status.Text = text;
}
