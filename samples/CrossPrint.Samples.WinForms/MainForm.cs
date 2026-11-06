using System.ComponentModel;
using System.Globalization;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.WinForms;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using SkiaSharp;
using System.IO;
using System.Threading.Tasks;
using System.Drawing;

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

    // 阶段 E：解决方案持久化（共享存储 + 会话）。模板落库到 AppData/CrossPrint/templates.db（SQLite）。
    readonly SolutionSession _session = new();
    readonly System.Windows.Forms.Timer _autosaveTimer = new() { Interval = 30000 };
    readonly ToolStripStatusLabel _status;
    readonly ToolStripStatusLabel _zoomText = new();
    ToolStripButton _undoButton = null!;
    ToolStripButton _redoButton = null!;
    readonly SplitContainer _leftSplit;
    readonly SplitContainer _rightSplit;
    readonly ListBox _toolbox = new();
    readonly ComboBox _paperCombo = new();
    readonly Button _rotateButton = new();
    readonly Button _customPaperButton = new();
    readonly CheckBox _continuousToggle = new();
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

    // 阶段 G：右上角 Toast 浮层（不拦截鼠标，3 秒自动消失）。
    readonly Panel _toastPanel;
    readonly Label _toastTitle;
    readonly Label _toastMessage;
    readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 3000 };

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
        // 纸张 + 连续纸 + 水印全部在右侧属性栏的无选中空白区（与 Avalonia 宿主一致）。
        // ⚠ 必须返回持久实例：属性面板随 ViewChanged/TemplateChanged 频繁 Rebuild（Clear 摘树后重挂同一棵树），
        //   BuildPaperPanel 里的字段级单例控件不能重复进视觉树（Avalonia 2026-11-05 崩溃实锤，WPF/WinUI 同机制）。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();

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

        // 阶段 G：Toast 浮层——最后加入以获得最高 Z 序，浮于所有停靠控件之上；鼠标命中靠 AutoSize/BackColor 也无碍操作。
        _toastTitle = new Label
        {
            Dock = DockStyle.Top,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x21, 0x21, 0x21),
            Padding = new Padding(10, 8, 10, 2),
            UseMnemonic = false,
        };
        _toastMessage = new Label
        {
            Dock = DockStyle.Top,
            Font = new Font(Font.FontFamily, 8.5f),
            ForeColor = Color.FromArgb(0x60, 0x62, 0x66),
            Padding = new Padding(10, 0, 10, 8),
            MaximumSize = new Size(300, 0),
            UseMnemonic = false,
        };
        _toastPanel = new Panel
        {
            Visible = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            MinimumSize = new Size(320, 1),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _toastPanel.Controls.Add(_toastMessage);
        _toastPanel.Controls.Add(_toastTitle);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastPanel.Visible = false; };
        Controls.Add(_toastPanel);
        _toastPanel.BringToFront();

        // 顶部菜单最后加入：停靠按逆序处理，菜单条才会贴在工具栏上方。
        MainMenuStrip = BuildMenuStrip();
        Controls.Add(MainMenuStrip);

        KeyDown += OnMainKeyDown;
        BindKernel();

        _autosaveTimer.Tick += (_, _) => { if (_session.IsDirty) SaveSolution(false, silent: true); };
        _autosaveTimer.Start();
        UpdateTitle();

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
        PositionToast();
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

    /// <summary>左栏（默认 200px，可拖宽）：工具箱吃满；纸张/水印挪到右属性栏空白区（与 Avalonia 宿主一致）。</summary>
    Control BuildLeftPanel()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(6),
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(BuildToolbox(), 0, 0);
        return left;
    }

    /// <summary>页面区持久实例：属性面板每次 Rebuild 复用同一棵控件树（见 EmptyStateContentBuilder 挂接处注释）。</summary>
    Control? _pageSection;

    /// <summary>右属性栏无选中空白区 = 纸张设置（含连续纸开关）+ 水印（与 Avalonia 宿主一致）。</summary>
    Control BuildPageSection()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var paper = BuildPaperPanel();
        paper.Dock = DockStyle.Fill;
        paper.AutoSize = true;
        host.Controls.Add(paper, 0, 0);

        var watermark = BuildWatermarkSection();
        watermark.Dock = DockStyle.Fill;
        watermark.AutoSize = true;
        host.Controls.Add(watermark, 0, 1);

        return host;
    }

    CheckBox _watermarkEnabled = null!;
    TextBox _watermarkText = null!;
    TextBox _watermarkBinding = null!;
    TextBox _watermarkFontSize = null!;
    TextBox _watermarkOpacity = null!;
    TextBox _watermarkRotation = null!;
    TextBox _watermarkSpacing = null!;
    TextBox _watermarkColor = null!;
    bool _syncingWatermark;

    /// <summary>水印设置区：启用/文字/字段绑定/字号/透明度/角度/间距/颜色，全部走内核命令可撤销。</summary>
    Control BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox { Text = "启用水印（文字平铺斜纹）", AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
        _watermarkEnabled.CheckedChanged += (_, _) =>
        {
            if (_syncingWatermark)
                return;
            var value = _watermarkEnabled.Checked;
            var old = _designer.Kernel.Template.Watermark.Enabled;
            CommitWatermark("启用水印", w => w.Enabled = value, w => w.Enabled = old);
        };

        _watermarkText = new TextBox();
        _watermarkText.LostFocus += (_, _) => CommitIfNotSyncing(CommitWatermarkText);

        _watermarkBinding = new TextBox();
        _watermarkBinding.LostFocus += (_, _) =>
            CommitIfNotSyncing(() => CommitWatermarkField("水印字段绑定", _watermarkBinding, w => w.Binding, (w, v) => w.Binding = v));

        _watermarkFontSize = new TextBox();
        _watermarkOpacity = new TextBox();
        _watermarkRotation = new TextBox();
        _watermarkSpacing = new TextBox();
        _watermarkFontSize.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印字号", "FontSizePt", 14, 4, 200, _watermarkFontSize));
        _watermarkOpacity.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印透明度", "Opacity", 0.2, 0, 1, _watermarkOpacity));
        _watermarkRotation.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印角度", "RotationDeg", -30, -90, 90, _watermarkRotation));
        _watermarkSpacing.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印间距", "SpacingMm", 60, 4, 400, _watermarkSpacing));

        // 颜色用 hex 文本框（与元素属性颜色行同一口径）。
        _watermarkColor = new TextBox();
        _watermarkColor.LostFocus += (_, _) => CommitIfNotSyncing(CommitWatermarkColor);

        Control FieldRow(string label, Control editor)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 6, 0) }, 0, 0);
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 1, 0, 1);
            grid.Controls.Add(editor, 1, 0);
            return grid;
        }

        Control FieldPair(string label, Control editor)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 6, 0) }, 0, 0);
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 1, 0, 1);
            grid.Controls.Add(editor, 1, 0);
            return grid;
        }

        var pairRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0),
        };
        pairRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pairRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pairRow.Controls.Add(FieldPair("字号", _watermarkFontSize), 0, 0);
        pairRow.Controls.Add(FieldPair("透明度", _watermarkOpacity), 1, 0);

        var angleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0),
        };
        angleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        angleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        angleRow.Controls.Add(FieldPair("角度", _watermarkRotation), 0, 0);
        angleRow.Controls.Add(FieldPair("间距", _watermarkSpacing), 1, 0);

        var section = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
            Margin = new Padding(0, 6, 0, 0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(new Label { Text = "水印", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 2, 0, 2) }, 0, 0);
        section.Controls.Add(_watermarkEnabled, 0, 1);
        section.Controls.Add(FieldRow("文字", _watermarkText), 0, 2);
        section.Controls.Add(FieldRow("字段绑定", _watermarkBinding), 0, 3);
        section.Controls.Add(pairRow, 0, 4);
        section.Controls.Add(angleRow, 0, 5);
        section.Controls.Add(FieldRow("颜色", _watermarkColor), 0, 6);
        section.Controls.Add(new Label { Text = string.Empty, AutoSize = true, Margin = new Padding(0) }, 0, 7);

        RefreshWatermarkControls();
        return section;
    }

    void CommitIfNotSyncing(Action commit)
    {
        if (!_syncingWatermark)
            commit();
    }

    void CommitWatermarkText()
    {
        var text = _watermarkText.Text ?? string.Empty;
        var old = _designer.Kernel.Template.Watermark.Text; // 撤销快照必须在提交前抓
        CommitWatermark("水印文字", w => w.Text = text, w => w.Text = old);
    }

    /// <summary>水印可选字符串字段（字段绑定）提交：空串归一为 null，值没变不推命令（不灌撤销栈）。</summary>
    void CommitWatermarkField(string label, TextBox box, Func<WatermarkOptions, string?> current, Action<WatermarkOptions, string?> apply)
    {
        var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        var old = current(_designer.Kernel.Template.Watermark);
        if (string.Equals(old, value, StringComparison.Ordinal))
            return;
        CommitWatermark(label, w => apply(w, value), w => apply(w, old));
    }

    void CommitWatermarkNumber(string label, string property, double fallback, double min, double max, TextBox box)
    {
        var text = (box.Text ?? string.Empty).Trim();
        if (text.Length == 0)
            return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            RefreshWatermarkControls();
            return;
        }

        var clamped = Math.Clamp(value, min, max);
        var before = _designer.Kernel.Template.Watermark;
        var old = property switch
        {
            "FontSizePt" => before.FontSizePt,
            "Opacity" => before.Opacity,
            "RotationDeg" => before.RotationDeg,
            _ => before.SpacingMm,
        };
        CommitWatermark(label,
            w => SetWatermarkNumber(w, property, clamped),
            w => SetWatermarkNumber(w, property, old));
    }

    void CommitWatermarkColor()
    {
        var text = (_watermarkColor.Text ?? string.Empty).Trim();
        var before = _designer.Kernel.Template.Watermark;
        if (!SKColor.TryParse(text, out var color))
        {
            RefreshWatermarkControls();
            return;
        }

        if (color.Red == before.Color.Red && color.Green == before.Color.Green && color.Blue == before.Color.Blue)
            return;
        var old = before.Color;
        CommitWatermark("水印颜色", w => w.Color = color, w => w.Color = old);
    }

    static void SetWatermarkNumber(WatermarkOptions w, string property, double value)
    {
        switch (property)
        {
            case "FontSizePt": w.FontSizePt = value; break;
            case "Opacity": w.Opacity = value; break;
            case "RotationDeg": w.RotationDeg = value; break;
            default: w.SpacingMm = value; break;
        }
    }

    void CommitWatermark(string label, Action<WatermarkOptions> apply, Action<WatermarkOptions> revert)
    {
        _designer.Kernel.CommitPropertyChange(
            label,
            () => apply(_designer.Kernel.Template.Watermark),
            () => revert(_designer.Kernel.Template.Watermark));
    }

    /// <summary>换模板/撤销后把水印控件同步回模型值。</summary>
    void RefreshWatermarkControls()
    {
        if (_syncingWatermark || _watermarkEnabled is null)
            return;

        _syncingWatermark = true;
        try
        {
            var w = _designer.Kernel.Template.Watermark;
            _watermarkEnabled.Checked = w.Enabled;
            _watermarkText.Text = w.Text;
            _watermarkBinding.Text = w.Binding ?? string.Empty;
            _watermarkFontSize.Text = w.FontSizePt.ToString("0.##", CultureInfo.InvariantCulture);
            _watermarkOpacity.Text = w.Opacity.ToString("0.##", CultureInfo.InvariantCulture);
            _watermarkRotation.Text = w.RotationDeg.ToString("0.##", CultureInfo.InvariantCulture);
            _watermarkSpacing.Text = w.SpacingMm.ToString("0.##", CultureInfo.InvariantCulture);
            _watermarkColor.Text = $"#{w.Color.Red:X2}{w.Color.Green:X2}{w.Color.Blue:X2}";
        }
        finally
        {
            _syncingWatermark = false;
        }
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
            RowCount = 5,
            Margin = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
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

        // 连续纸独立开关：与预设正交（选 A4 再勾上就是"A4 宽的连续纸"）。走内核命令可撤销。
        _continuousToggle.Text = "连续纸（打印长度随内容自适应）";
        _continuousToggle.AutoSize = true;
        _continuousToggle.Margin = new Padding(0, 2, 0, 0);
        _continuousToggle.CheckedChanged += (_, _) => TogglePaperContinuous(_continuousToggle.Checked);
        panel.Controls.Add(_continuousToggle, 0, 3);
        panel.SetColumnSpan(_continuousToggle, 2);

        _paperInfo.AutoSize = false;
        _paperInfo.Dock = DockStyle.Fill;
        _paperInfo.ForeColor = SystemColors.GrayText;
        _paperInfo.Margin = new Padding(0);
        _paperInfo.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_paperInfo, 0, 4);
        panel.SetColumnSpan(_paperInfo, 2);

        return panel;
    }

    /// <summary>工具箱：搜索框按中英文名过滤，ListBox 列出元素类型，双击或回车添加到画布中心。</summary>
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

        var search = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = Localizer.T("Toolbox.Search"),
        };
        search.TextChanged += (_, _) => ApplyToolboxFilter(search.Text);

        _toolbox.Dock = DockStyle.Fill;
        _toolbox.DisplayMember = nameof(ToolboxItem.Label);
        ApplyToolboxFilter(string.Empty);
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
            Text = Localizer.T("Toolbox.HintDrag"),
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        };

        // 停靠按逆序处理：标题带最后加入才先贴顶，提示与按钮依次压到底部，搜索框次之，ListBox 最后处理才占满剩余区域。
        panel.Controls.Add(_toolbox);
        panel.Controls.Add(search);
        panel.Controls.Add(addButton);
        panel.Controls.Add(hint);
        panel.Controls.Add(header);

        HookToolboxDragDrop();
        return panel;
    }

    /// <summary>工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。重设 Items 会清选择，可接受。</summary>
    void ApplyToolboxFilter(string? query)
    {
        _toolbox.Items.Clear();
        foreach (var (label, key) in ElementButtons)
            if (ToolboxNames.Matches(key, query))
                _toolbox.Items.Add(new ToolboxItem(label, key));
    }

    /// <summary>工具箱条目拖入画布：自定格式载荷只认自己的拖拽，别家拖文件/文本进来不会误触发放置。</summary>
    const string ToolboxDragFormat = "CrossPrint.Toolbox.TypeKey";

    void HookToolboxDragDrop()
    {
        _toolbox.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _toolboxDragStart = e.Location;
        };
        _toolbox.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _toolbox.SelectedItem is not ToolboxItem item)
                return;

            // 越过 4px 阈值才算拖拽：单纯点击仍走双击/按钮的"加到中心"。
            if (Math.Abs(e.X - _toolboxDragStart.X) < 4 && Math.Abs(e.Y - _toolboxDragStart.Y) < 4)
                return;

            _toolbox.DoDragDrop(new DataObject(ToolboxDragFormat, item.TypeKey), DragDropEffects.Copy);
        };

        _designer.AllowDrop = true;
        _designer.DragEnter += (_, e) =>
            e.Effect = e.Data is not null && e.Data.GetDataPresent(ToolboxDragFormat)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        _designer.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(ToolboxDragFormat) is not string typeKey)
                return;

            // WinForms 客户端坐标本身就是设备 px（DPI 感知下），直接交给视口换算 mm。
            var client = _designer.PointToClient(new Point(e.X, e.Y));
            var mm = _designer.Kernel.Viewport.ToMm(client.X, client.Y, _designer.Dpi);
            _designer.Kernel.AddElementAt(typeKey, mm.X, mm.Y);
            _designer.Focus();
            SetStatus(Localizer.T("Status.ElementAddedCenter", Localizer.T(ToolboxNames.I18nKeyOf(typeKey))));
        };
    }

    Point _toolboxDragStart;

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

        var templatesItem = new ToolStripMenuItem(Localizer.T("Menu.File.Templates"))
        {
            ToolTipText = Localizer.T("Menu.File.TemplatesTip"),
        };
        templatesItem.Click += (_, _) => OpenTemplateLibrary();
        fileMenu.DropDownItems.Add(templatesItem);

        var stylesItem = new ToolStripMenuItem(Localizer.T("Menu.File.StyleLibrary"))
        {
            ToolTipText = Localizer.T("Menu.File.StyleLibraryTip"),
        };
        stylesItem.Click += (_, _) => StyleLibraryDialog.Show(_designer.Kernel, this);
        fileMenu.DropDownItems.Add(stylesItem);

        fileMenu.DropDownItems.Add(new ToolStripSeparator());

        var newItem = new ToolStripMenuItem(Localizer.T("Menu.File.New")) { ShortcutKeyDisplayString = "Ctrl+N" };
        newItem.Click += (_, _) => NewSolution();
        fileMenu.DropDownItems.Add(newItem);

        var openItem = new ToolStripMenuItem(Localizer.T("Menu.File.Open"));
        openItem.Click += (_, _) => OpenSolutionDialog();
        fileMenu.DropDownItems.Add(openItem);

        var recentItem = new ToolStripMenuItem(Localizer.T("Menu.File.Recent"));
        recentItem.DropDownOpening += (_, _) => RefreshRecentMenu(recentItem);
        fileMenu.DropDownItems.Add(recentItem);

        var saveItem = new ToolStripMenuItem(Localizer.T("Menu.File.Save")) { ShortcutKeyDisplayString = "Ctrl+S" };
        saveItem.Click += (_, _) => SaveSolution(false);
        fileMenu.DropDownItems.Add(saveItem);

        var saveAsItem = new ToolStripMenuItem(Localizer.T("Menu.File.SaveAs"));
        saveAsItem.Click += (_, _) => SaveSolution(true);
        fileMenu.DropDownItems.Add(saveAsItem);

        fileMenu.DropDownItems.Add(new ToolStripSeparator());

        var exportJsonItem = new ToolStripMenuItem(Localizer.T("Menu.File.ExportJson"));
        exportJsonItem.Click += (_, _) => ExportSolutionJson();
        fileMenu.DropDownItems.Add(exportJsonItem);

        var exportPdfItem = new ToolStripMenuItem(Localizer.T("Menu.File.ExportPdf"));
        exportPdfItem.Click += (_, _) => ExportPdf();
        fileMenu.DropDownItems.Add(exportPdfItem);

        var printItem = new ToolStripMenuItem(Localizer.T("Menu.Print.System"));
        printItem.Click += (_, _) => PrintSolution();
        fileMenu.DropDownItems.Add(printItem);

        menu.Items.Add(fileMenu);

        var helpMenu = new ToolStripMenuItem(Localizer.T("Menu.Help"));
        var aboutItem = new ToolStripMenuItem(Localizer.T("Menu.Help.About"));
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.DropDownItems.Add(aboutItem);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(BuildLanguageMenu());
        menu.Items.Add(helpMenu);

        return menu;
    }

    /// <summary>语言子菜单：跟随系统 / 中文 / English。选择后保存偏好并重启进程（界面文字在构造时已固定）。</summary>
    ToolStripMenuItem BuildLanguageMenu()
    {
        var preferred = LocalizationSwitch.PreferredCulture();
        var languageMenu = new ToolStripMenuItem(Localizer.T("Menu.Language"));

        void AddChoice(string? culture, string label)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = preferred is null
                    ? culture is null
                    : string.Equals(preferred, culture, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ChangeLanguage(culture);
            languageMenu.DropDownItems.Add(item);
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
                SetStatus(Localizer.T("Language.SaveFailed"));
                break;
            default:
                MessageBox.Show(
                    this,
                    Localizer.T("Language.SavedRestartFailed", LocalizationSwitch.DisplayName(culture)),
                    Localizer.T("Menu.Language"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
        }
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

        ToolStripButton AddAction(string text, Action action, string? tooltip = null)
        {
            var button = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = true,
                ToolTipText = tooltip ?? string.Empty,
            };
            button.Click += (_, _) => action();
            toolbar.Items.Add(button);
            return button;
        }

        void AddSeparator() => toolbar.Items.Add(new ToolStripSeparator());

        // 导入按钮放最前面：ToolStrip 右侧会折进溢出菜单，放末尾等于藏起来。
        AddAction(Localizer.T("Toolbar.Import"), ImportHiprintDialog, Localizer.T("Toolbar.Import.Tip"));
        AddSeparator();

        foreach (var (label, key) in ElementButtons)
            AddAction(label, () => AddElement(key), Localizer.T("Toolbar.AddElement.Tooltip", label));

        AddSeparator();
        _undoButton = AddAction(Localizer.T("Toolbar.Undo"), DoUndo, Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = AddAction(Localizer.T("Toolbar.Redo"), DoRedo, Localizer.T("Toolbar.Redo.Tip"));
        AddSeparator();
        AddAction(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip"));
        AddAction(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.Delete.Tip"));
        AddSeparator();
        AddAction(Localizer.T("Toolbar.BringForward"), () => RunKernel(kernel => kernel.BringForward()), Localizer.T("Toolbar.BringForward.Tip"));
        AddAction(Localizer.T("Toolbar.SendBackward"), () => RunKernel(kernel => kernel.SendBackward()), Localizer.T("Toolbar.SendBackward.Tip"));
        AddAction(Localizer.T("Toolbar.BringToFront"), () => RunKernel(kernel => kernel.BringToFront()), Localizer.T("Toolbar.BringToFront.Tip"));
        AddAction(Localizer.T("Toolbar.SendToBack"), () => RunKernel(kernel => kernel.SendToBack()), Localizer.T("Toolbar.SendToBack.Tip"));

        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单仍保留同名项。
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
        var strip = new StatusStrip { SizingGrip = false, ShowItemToolTips = true };

        ToolStripButton ZoomBtn(string text, Action action, string tip)
        {
            var b = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = false,
                Width = 32,
                ToolTipText = tip,
            };
            b.Click += (_, _) => action();
            strip.Items.Add(b);
            return b;
        }

        ZoomBtn(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip"));
        ZoomBtn(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip"));
        ZoomBtn(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip"));
        ZoomBtn(Localizer.T("Toolbar.FitToPage"), FitToPageFromStatus, Localizer.T("Toolbar.FitToPage.Tip"));

        _zoomText.TextAlign = ContentAlignment.MiddleLeft;
        _zoomText.AutoSize = false;
        _zoomText.Width = 90;
        strip.Items.Add(_zoomText);
        strip.Items.Add(_status); // Spring=true，占据剩余宽度
        return strip;
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

    /// <summary>撤销栈变化由内核触发，只刷按钮可用态。</summary>
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
        // KeyPreview=true：全局都能撤销/重做；但文本框里要留给文本自己的撤销栈。
        // Ctrl+Shift+Z 与 Ctrl+Y 都算重做（与 Avalonia 基准一致）。
        if (e.Control && !e.Alt && e.KeyCode is Keys.Z or Keys.Y && !IsTextInputFocused(ActiveControl))
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Y || e.Shift)
                DoRedo();
            else
                DoUndo();
            return;
        }

        // 阶段 E：Ctrl+S 立即保存。
        if (e.Control && !e.Alt && e.KeyCode == Keys.S)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            SaveSolution(false);
            return;
        }

        // Ctrl+D 只在设计画布有焦点时生效：属性面板的文本框里按键不该被抢。
        if (!_designer.Focused || !e.Control || e.Shift || e.Alt || e.KeyCode != Keys.D)
            return;

        e.SuppressKeyPress = true;
        e.Handled = true;
        DuplicateSelection();
    }

    /// <summary>沿 ActiveControl 链找文本框：属性面板/JSON 面板常嵌在多层容器里。</summary>
    static bool IsTextInputFocused(Control? control)
    {
        var current = control;
        while (current is not null)
        {
            if (current is TextBoxBase)
                return true;

            current = current is ContainerControl container ? container.ActiveControl : null;
        }

        return false;
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
            // 自定义纸张只改尺寸：是否连续纸由纸张属性里的独立开关决定，此处保持现状。
            var continuous = _designer.Kernel.Template.Paper.IsContinuous;
            _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm, continuous);
            SetStatus(continuous
                ? $"自定义纸张：宽 {dialog.WidthMm:0.##} mm，设计高度 {dialog.HeightMm:0.##} mm，打印长度随内容。"
                : Localizer.T("Status.CustomPaperSet", dialog.WidthMm, dialog.HeightMm));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            RefreshWatermarkControls();
            var paper = _designer.Kernel.Template.Paper;
            var index = Array.FindIndex(
                PaperChoices,
                choice => choice.Key is not null
                    && string.Equals(choice.Key, paper.Preset, StringComparison.OrdinalIgnoreCase));
            _paperCombo.SelectedIndex = index >= 0 ? index : PaperChoices.Length - 1;

            var continuous = paper.IsContinuous;
            _continuousToggle.Checked = continuous;
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
        // 阶段 E：任何编辑都标记脏位（LoadTemplate 已换内核，这里重绑到新内核）。
        _designer.Kernel.TemplateChanged += MarkSolutionDirty;
        // LoadTemplate 会换内核：撤销态与缩放读数的订阅必须每次重绑（与 Avalonia 基准一致）。
        _designer.Kernel.UndoStateChanged += UpdateUndoButtons;
        _designer.Kernel.ViewChanged += UpdateZoomText;
        _properties.Attach(_designer.Kernel);
        SyncPaperControls();
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
                SetJsonState(Localizer.T("JsonPanel.Synced", _designer.Kernel.Template.Elements.Count), error: false);
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
        SetJsonState(note ?? Localizer.T("JsonPanel.Synced", _designer.Kernel.Template.Elements.Count), error: false);
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

    /// <summary>模板库对话框：分类列表 + 模板卡片网格（缩略图即用真实引擎渲染），点击卡片套用模板。</summary>
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

        var categoryList = new ListBox { Dock = DockStyle.Left, Width = 150 };
        categoryList.Items.AddRange(BuiltinTemplateCatalog.Categories);
        categoryList.SelectedIndex = 0;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(8),
        };

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(categoryList);
        split.Panel2.Controls.Add(flow);
        Form? dialog = null;

        void ApplyFromLibrary(BuiltinTemplateCatalog.Entry entry)
        {
            _designer.LoadTemplate(entry.Create());
            BindKernel(entry.Name);
            SetStatus(Localizer.T("Status.TemplateApplied", entry.Name));
        }

        void ShowCards(string category)
        {
            flow.Controls.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            foreach (var entry in list)
                flow.Controls.Add(BuildCard(entry));
        }

        Panel BuildCard(BuiltinTemplateCatalog.Entry entry)
        {
            var card = new Panel
            {
                Width = 220,
                Height = 150,
                Margin = new Padding(6),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand,
            };

            var picture = new PictureBox
            {
                Width = 200,
                Height = 96,
                Location = new Point(10, 22),
                SizeMode = PictureBoxSizeMode.Zoom,
            };
            try
            {
                picture.Image = PngToImage(BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create()));
            }
            catch
            {
                // 缩略图渲染失败不阻断选单本身。
            }

            var title = new Label
            {
                Text = entry.Name,
                Location = new Point(10, 2),
                Width = 200,
                AutoSize = false,
                Font = new Font(Font, FontStyle.Bold),
            };
            var desc = new Label
            {
                Text = entry.Description,
                Location = new Point(10, 122),
                Width = 200,
                Height = 26,
                AutoSize = false,
                ForeColor = SystemColors.GrayText,
            };

            card.Controls.Add(picture);
            card.Controls.Add(title);
            card.Controls.Add(desc);

            card.Click += (_, _) => { dialog!.Close(); ApplyFromLibrary(entry); };
            // 子控件点击也要触发卡片逻辑。
            picture.Click += (_, _) => { dialog!.Close(); ApplyFromLibrary(entry); };
            title.Click += (_, _) => { dialog!.Close(); ApplyFromLibrary(entry); };
            desc.Click += (_, _) => { dialog!.Close(); ApplyFromLibrary(entry); };
            return card;
        }

        categoryList.SelectedIndexChanged += (_, _) =>
        {
            if (categoryList.SelectedItem is string category)
                ShowCards(category);
        };
        ShowCards("全部");

        dialog = new Form
        {
            Text = Localizer.T("TemplateLibrary.Title"),
            Width = 1080,
            Height = 720,
            StartPosition = FormStartPosition.CenterParent,
        };
        var hint = new Label
        {
            Text = Localizer.T("TemplateLibrary.Hint"),
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(4),
        };
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(hint, 0, 0);
        outer.Controls.Add(split, 0, 1);
        dialog.Controls.Add(outer);

        dialog.ShowDialog(this);
    }

    /// <summary>把共享层渲染出的 PNG 字节转成 WinForms 图像。</summary>
    static Image PngToImage(byte[] png) => Image.FromStream(new MemoryStream(png));

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

    // ===== 阶段 E/F：解决方案持久化 + 导出/打印 =====

    void MarkSolutionDirty()
    {
        _session.IsDirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        var mark = _session.IsDirty ? "*" : string.Empty;
        Text = $"{Localizer.T("App.Title.Short")} — WinForms — {_session.CurrentName}{mark}";
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
        var listBox = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(TemplateRecord.Name) };
        foreach (var record in records)
            listBox.Items.Add(record);

        var openButton = new Button { Text = Localizer.T("Dialog.Open"), DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 90 };
        var cancelButton = new Button { Text = Localizer.T("Dialog.Cancel"), DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 90 };
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38, Padding = new Padding(4) };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(openButton);

        var form = new Form
        {
            Text = Localizer.T("Dialog.OpenSolution"),
            Width = 460,
            Height = 380,
            StartPosition = FormStartPosition.CenterParent,
            Owner = this,
        };
        form.AcceptButton = openButton;
        form.CancelButton = cancelButton;
        form.Controls.Add(buttonPanel);
        form.Controls.Add(listBox);

        listBox.DoubleClick += (_, _) =>
        {
            if (listBox.SelectedItem is TemplateRecord rec)
            {
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
        };

        if (form.ShowDialog(this) == DialogResult.OK && listBox.SelectedItem is TemplateRecord selected)
            OpenSolution(selected.Id);
    }

    void OpenSolution(string id)
    {
        var record = _session.Load(id);
        if (record is null)
        {
            SetStatus(Localizer.T("Error.OpenFailed"));
            return;
        }

        var template = SolutionSession.Parse(record.Json);
        if (template is null)
        {
            SetStatus(Localizer.T("Error.OpenFailed"));
            return;
        }

        _designer.LoadTemplate(template);
        BindKernel(record.Name);
        _session.OpenRecord(record);
        SetStatus(Localizer.T("Status.Opened", record.Name));
        UpdateTitle();
    }

    void RefreshRecentMenu(ToolStripMenuItem recentItem)
    {
        recentItem.DropDownItems.Clear();
        foreach (var record in _session.Recent(8))
        {
            var item = new ToolStripMenuItem(record.Name) { ToolTipText = record.UpdatedAt.ToString("g", CultureInfo.CurrentCulture) };
            item.Click += (_, _) => OpenSolution(record.Id);
            recentItem.DropDownItems.Add(item);
        }

        if (recentItem.DropDownItems.Count == 0)
            recentItem.DropDownItems.Add(new ToolStripMenuItem(Localizer.T("Menu.File.NoRecent")) { Enabled = false });
    }

    bool SaveSolution(bool saveAs, bool silent = false)
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
            SetStatus(Localizer.T("Error.SaveFailed", ex.Message));
            return false;
        }

        UpdateTitle();
        return true;
    }

    string? PromptName(string defaultName)
    {
        var textBox = new TextBox { Text = defaultName, Dock = DockStyle.Top };
        var okButton = new Button { Text = Localizer.T("Dialog.OK"), DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 90 };
        var cancelButton = new Button { Text = Localizer.T("Dialog.Cancel"), DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 90 };
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38, Padding = new Padding(4) };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(okButton);

        var form = new Form
        {
            Text = Localizer.T("Dialog.SaveAs"),
            Width = 380,
            Height = 150,
            StartPosition = FormStartPosition.CenterParent,
            Owner = this,
        };
        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;
        form.Controls.Add(buttonPanel);
        form.Controls.Add(textBox);

        return form.ShowDialog(this) == DialogResult.OK
            ? (string.IsNullOrWhiteSpace(textBox.Text) ? null : textBox.Text.Trim())
            : null;
    }

    void ExportSolutionJson()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            FileName = "crossprint-template.json",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, TemplateJson.Serialize(_designer.Template, indented: true));
            SetStatus(Localizer.T("Status.Exported", dialog.FileName));
            ShowToast("已导出模板", Localizer.T("Status.Exported", dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Localizer.T("Error.ExportFailed", ex.Message),
                Localizer.T("Dialog.Export.Title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // 阶段 F：系统打印（SystemPrintTarget）：渲染临时 PDF 后交 OS 打印管线。
    bool _printing;

    void PrintSolution()
    {
        if (_printing)
        {
            SetStatus(Localizer.T("Status.PrintingBusy"));
            return;
        }

        _printing = true;
        SetStatus(Localizer.T("Status.PrintingStarting"));
        var issues = new RenderReport();
        var document = new PrintDocument(_designer.Template, SampleData.Create());
        _ = PrintSolutionAsync(document, issues);
    }

    async Task PrintSolutionAsync(PrintDocument document, RenderReport issues)
    {
        try
        {
            var result = await new SystemPrintTarget().PrintBatchAsync(new[] { document }, new SystemPrintOptions(), issues);
            SetStatus(result.Success
                ? (issues.HasIssues
                    ? Localizer.T("Status.PrintedWithWarnings", result.PrintedPages, issues.Issues.Count)
                    : Localizer.T("Status.Printed", result.PrintedPages))
                : Localizer.T("Error.PrintFailed", result.Message));
            if (result.Success)
                ShowToast("打印完成", issues.HasIssues
                    ? Localizer.T("Status.PrintedWithWarnings", result.PrintedPages, issues.Issues.Count)
                    : Localizer.T("Status.Printed", result.PrintedPages));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.PrintFailed", ex.Message));
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
            ShowToast("已导出 PDF", dialog.FileName);
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

        var logoBytes = LogoLoader.GetLogoBytes();
        if (logoBytes is not null)
        {
            var logoPanel = new Panel { Dock = DockStyle.Top, Height = 100, Margin = new Padding(0, 2, 0, 4) };
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
            };

            // GDI+ 的 Image.FromStream 要求流在图像生命周期内保持打开，这里立刻复制成自有位图。
            using (var ms = new MemoryStream(logoBytes))
            using (var decoded = Image.FromStream(ms))
            {
                picture.Image = new Bitmap(decoded);
            }

            logoPanel.Controls.Add(picture);
            body.Controls.Add(logoPanel);
        }

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

    // 阶段 G：右上角 Toast（成功/提示浮层），3 秒自动消失，不拦截鼠标；同一时刻只保留最新一条。
    void ShowToast(string title, string? message = null)
    {
        _toastTitle.Text = title;
        _toastMessage.Text = message ?? string.Empty;
        _toastMessage.Visible = !string.IsNullOrEmpty(message);
        PositionToast();
        _toastPanel.Visible = true;
        _toastPanel.BringToFront();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void PositionToast() =>
        _toastPanel.Location = new Point(ClientSize.Width - _toastPanel.Width - 16, 56);
}
