using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Wpf;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using Microsoft.Win32;
using SkiaSharp;

namespace CrossPrint.Samples.Wpf;

/// <summary>示例主窗口：菜单 + 双条工具带 + 左侧纸张/工具箱（可拖宽）+ 中间设计画布 + 右侧属性面板（可拖宽）+ 可折叠模板 JSON 带 + 状态栏。</summary>
internal sealed class MainWindow : Window
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
    readonly ListBox _toolbox = new();
    readonly TextBlock _status = new();
    readonly TextBlock _zoomText = new();
    Button _undoButton = null!;
    Button _redoButton = null!;
    readonly ComboBox _paperCombo = new();
    readonly Button _rotateButton = new();
    readonly Button _customPaperButton = new();
    readonly CheckBox _continuousToggle = new();
    readonly TextBlock _paperInfo = new();
    readonly List<MenuItem> _selectionMenuItems = new();
    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;
    GridSplitter _jsonSplitter = null!;

    MenuItem _verticalCenterItem = null!;
    MenuItem _rotatePaperItem = null!;
    bool _syncingPaper;
    bool _customDialogPending;
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;

    // 阶段 E：解决方案持久化（共享存储 + 会话）。模板落库到 AppData/CrossPrint/templates.db（SQLite）。
    readonly SolutionSession _session = new();
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // 阶段 G：右上角 Toast 浮层（顶层 Grid，不拦截鼠标）。
    readonly Grid _toastLayer = new() { IsHitTestVisible = false };
    Border? _toastCard;

    public MainWindow()
    {
        Title = Localizer.T("App.Title.Short") + " — WPF";
        Width = 1320;
        Height = 880;
        MinWidth = 1024;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 从 I18n 嵌入资源加载窗口图标
        try
        {
            using var stream = LogoLoader.GetLogoStream();
            if (stream is not null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);
                Icon = BitmapFrame.Create(new MemoryStream(bytes));
            }
        }
        catch { }

        var template = LoadSampleTemplate(out var warnings);
        _designer = new DesignerControl(template);

        // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
        // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
        _designer.Data = SampleData.Create();

        _properties = new PropertyPanel(_designer.Kernel);
        // 纸张 + 连续纸 + 水印全部在右侧属性栏的无选中空白区（与 Avalonia 宿主一致）。
        // ⚠ 必须返回持久实例：属性面板随 ViewChanged/TemplateChanged 频繁 Rebuild（Clear 摘树后重挂同一棵树），
        //   BuildPaperPanel 里的字段级单例控件不能重复进视觉树，每次新建会抛 "already has a visual parent"
        //   （Avalonia 2026-11-05 崩溃日志实锤，WPF 同机制）。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();

        BuildCanvasContextMenu();
        PreviewKeyDown += OnPreviewKeyDown;

        var root = new DockPanel();

        var menuBar = BuildMenuBar();
        DockPanel.SetDock(menuBar, Dock.Top);
        root.Children.Add(menuBar);

        var toolbars = BuildToolBars();
        DockPanel.SetDock(toolbars, Dock.Top);
        root.Children.Add(toolbars);

        var statusBar = BuildStatusBar();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);

        // 状态栏先停靠到底部，JSON 面板随后停靠，就叠在状态栏上方。
        var jsonPanel = BuildTemplateJsonPanel();
        DockPanel.SetDock(jsonPanel, Dock.Bottom);
        root.Children.Add(jsonPanel);

        // 最后一个子元素填满剩余区域（DockPanel.LastChildFill 默认 true）。
        root.Children.Add(BuildBody());

        // 阶段 G：用顶层 Grid 包裹 DockPanel，使 Toast 浮层覆盖整窗且不抢占输入。
        var overlay = new Grid();
        overlay.Children.Add(root);
        overlay.Children.Add(_toastLayer);
        Content = overlay;

        BindKernel();

        if (warnings.Count > 0)
            SetStatus(Localizer.T("App.Status.Warnings", string.Join("；", warnings)), error: true);
        else
            SetStatus(Localizer.T("App.Status.Ready"));

        _autosaveTimer.Tick += (_, _) => { if (_session.IsDirty) SaveSolution(false, silent: true); };
        _autosaveTimer.Start();
        UpdateTitle();

        Loaded += (_, _) => _designer.Focus();
    }

    static PrintTemplate LoadSampleTemplate(out IReadOnlyList<string> warnings)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "kitchen-sink.template.json");
        if (!File.Exists(path))
        {
            warnings = new[] { Localizer.T("Status.KitchenSinkNotFound") };
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
            warnings = new[] { Localizer.T("Error.LoadFailed", ex.Message) };
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、复杂测试模板加载）。</summary>
    UIElement BuildMenuBar()
    {
        var fileMenu = new MenuItem { Header = Localizer.T("Menu.File") };
        var importItem = new MenuItem { Header = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => ImportHiprintDialog();
        fileMenu.Items.Add(importItem);

        var stylesItem = new MenuItem { Header = Localizer.T("Menu.File.StyleLibrary") };
        stylesItem.Click += (_, _) => OpenStyleLibrary();
        stylesItem.ToolTip = Localizer.T("Menu.File.StyleLibraryTip");
        fileMenu.Items.Add(stylesItem);

        fileMenu.Items.Add(new Separator());
        var templatesItem = new MenuItem { Header = Localizer.T("Menu.File.Templates") };
        templatesItem.Click += (_, _) => OpenTemplateLibrary();
        templatesItem.ToolTip = Localizer.T("Menu.File.TemplatesTip");
        fileMenu.Items.Add(templatesItem);

        fileMenu.Items.Add(new Separator());

        var newItem = new MenuItem { Header = Localizer.T("Menu.File.New"), InputGestureText = "Ctrl+N" };
        newItem.Click += (_, _) => NewSolution();
        fileMenu.Items.Add(newItem);

        var openItem = new MenuItem { Header = Localizer.T("Menu.File.Open") };
        openItem.Click += (_, _) => OpenSolutionDialog();
        fileMenu.Items.Add(openItem);

        var recentItem = new MenuItem { Header = Localizer.T("Menu.File.Recent") };
        recentItem.SubmenuOpened += (_, _) => RefreshRecentMenu(recentItem);
        fileMenu.Items.Add(recentItem);

        var saveItem = new MenuItem { Header = Localizer.T("Menu.File.Save"), InputGestureText = "Ctrl+S" };
        saveItem.Click += (_, _) => SaveSolution(false);
        fileMenu.Items.Add(saveItem);

        var saveAsItem = new MenuItem { Header = Localizer.T("Menu.File.SaveAs") };
        saveAsItem.Click += (_, _) => SaveSolution(true);
        fileMenu.Items.Add(saveAsItem);

        fileMenu.Items.Add(new Separator());

        var exportJsonItem = new MenuItem { Header = Localizer.T("Menu.File.ExportJson") };
        exportJsonItem.Click += (_, _) => ExportSolutionJson();
        fileMenu.Items.Add(exportJsonItem);

        var exportPdfItem = new MenuItem { Header = Localizer.T("Menu.File.ExportPdf") };
        exportPdfItem.Click += (_, _) => ExportPdf();
        fileMenu.Items.Add(exportPdfItem);

        var printItem = new MenuItem { Header = Localizer.T("Menu.Print.System") };
        printItem.Click += (_, _) => PrintToSystem();
        printItem.ToolTip = Localizer.T("Menu.Print.SystemTip");
        fileMenu.Items.Add(printItem);

        var menu = new Menu();
        menu.Items.Add(fileMenu);

        var helpMenu = new MenuItem { Header = Localizer.T("Menu.Help") };
        var aboutItem = new MenuItem { Header = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.Items.Add(aboutItem);
        helpMenu.Items.Add(new Separator());
        helpMenu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(helpMenu);

        return menu;
    }

    /// <summary>语言子菜单：跟随系统 / 中文 / English。选择后保存偏好并重启进程（界面文字在构造时已固定）。</summary>
    MenuItem BuildLanguageMenu()
    {
        var preferred = LocalizationSwitch.PreferredCulture();
        var languageMenu = new MenuItem { Header = Localizer.T("Menu.Language") };

        void AddChoice(string? culture, string label)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = preferred is null
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
                    Localizer.T("Menu.Language"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
        }
    }

    UIElement BuildToolBars()
    {
        var tray = new ToolBarTray { Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)) };

        // 两条带：元素按钮与编辑/视图按钮分开，窄窗口时不会把后面的按钮全吞进溢出菜单。
        var elementsBar = new ToolBar { Band = 0, BandIndex = 0 };
        var editBar = new ToolBar { Band = 1, BandIndex = 0 };
        ToolBarTray.SetIsLocked(elementsBar, true);
        ToolBarTray.SetIsLocked(editBar, true);

        foreach (var (label, key) in ElementButtons)
        {
            var typeKey = key;
            elementsBar.Items.Add(MakeButton(label, () => Run(() => _designer.Kernel.AddElementAtCenter(typeKey)), Localizer.T("Toolbar.AddElement.Tooltip", label)));
        }

        // 标签尽量短（完整说明在 tooltip 里）：编辑带项目多，窄窗口时要少折进溢出菜单。
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Import"), ImportHiprintDialog, Localizer.T("Toolbar.Import.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.StyleLibrary"), OpenStyleLibrary, Localizer.T("Toolbar.StyleLibraryTip")));
        editBar.Items.Add(new Separator());
        _undoButton = MakeButton(Localizer.T("Toolbar.Undo"), DoUndo, Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = MakeButton(Localizer.T("Toolbar.Redo"), DoRedo, Localizer.T("Toolbar.Redo.Tip"));
        editBar.Items.Add(_undoButton);
        editBar.Items.Add(_redoButton);
        editBar.Items.Add(new Separator());
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Delete"), () => Run(() => _designer.Kernel.DeleteSelection()), Localizer.T("Toolbar.Delete.Tip")));
        editBar.Items.Add(new Separator());
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单仍保留同名项。
        editBar.Items.Add(new Separator());

        var gridCheck = MakeOverlayCheck(Localizer.T("Toolbar.Grid"), true, value => _designer.Overlay.Options.ShowGrid = value);
        var rulerCheck = MakeOverlayCheck(Localizer.T("Toolbar.Ruler"), true, value => _designer.Overlay.Options.ShowRulers = value);
        var marginCheck = MakeOverlayCheck(Localizer.T("Toolbar.SafeArea"), true, value => _designer.Overlay.Options.ShowMarginGuides = value);
        editBar.Items.Add(gridCheck);
        editBar.Items.Add(rulerCheck);
        editBar.Items.Add(marginCheck);
        editBar.Items.Add(new Separator());

        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Print"), PrintToSystem, Localizer.T("Toolbar.Print.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.ExportPdf"), ExportPdf, Localizer.T("Toolbar.ExportPdf.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.Preview.Tip")));

        tray.ToolBars.Add(elementsBar);
        tray.ToolBars.Add(editBar);
        return tray;
    }

    CheckBox MakeOverlayCheck(string text, bool initial, Action<bool> apply)
    {
        var check = new CheckBox
        {
            Content = text,
            IsChecked = initial,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
        };

        check.Click += (_, _) =>
        {
            apply(check.IsChecked == true);
            _designer.InvalidateVisual();
            _designer.Focus();
        };

        return check;
    }

    UIElement BuildBody()
    {
        var sidebar = BuildLeftSidebar();
        var designerHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6),
            Child = _designer,
        };

        var propertyHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = _properties,
        };

        // 中间两道 GridSplitter：侧栏/属性面板宽度可拖，窄窗口时能腾出画布空间。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200), MinWidth = 150 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310), MinWidth = 240 });

        var leftSplitter = MakeColumnSplitter();
        var rightSplitter = MakeColumnSplitter();

        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(leftSplitter, 1);
        Grid.SetColumn(designerHost, 2);
        Grid.SetColumn(rightSplitter, 3);
        Grid.SetColumn(propertyHost, 4);
        grid.Children.Add(sidebar);
        grid.Children.Add(leftSplitter);
        grid.Children.Add(designerHost);
        grid.Children.Add(rightSplitter);
        grid.Children.Add(propertyHost);
        return grid;
    }

    /// <summary>透明背景的列分割条：透明画刷仍参与命中测试，悬停/拖拽时才有可见反馈。</summary>
    static GridSplitter MakeColumnSplitter() => new()
    {
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ResizeDirection = GridResizeDirection.Columns,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent,
    };

    UIElement BuildLeftSidebar()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 纸张/水印挪到右侧属性栏的空白区（用户钦定，与 Avalonia 宿主一致）；左栏只留工具箱。
        var toolbox = BuildToolbox();
        Grid.SetRow(toolbox, 0);
        grid.Children.Add(toolbox);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
    }

    UIElement BuildPaperPanel()
    {
        // 不用 GroupBox：省掉边框 + 标题占位，侧栏用节标题就够紧凑。
        var grid = new Grid { Margin = new Thickness(10, 10, 10, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = Localizer.T("Paper.Section"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(header, 0);
        Grid.SetColumn(header, 0);
        Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);

        _paperCombo.ItemsSource = Array.ConvertAll(PaperChoices, choice => choice.Label);
        _paperCombo.Margin = new Thickness(0, 0, 0, 6);
        _paperCombo.SelectionChanged += (_, _) => OnPaperChoiceChanged();
        Grid.SetRow(_paperCombo, 1);
        Grid.SetColumn(_paperCombo, 0);
        Grid.SetColumnSpan(_paperCombo, 2);
        grid.Children.Add(_paperCombo);

        _rotateButton.Content = Localizer.T("Paper.Rotate");
        _rotateButton.Margin = new Thickness(0, 0, 3, 0);
        _rotateButton.Padding = new Thickness(6, 3, 6, 3);
        _rotateButton.Click += (_, _) => TogglePaperOrientation();
        Grid.SetRow(_rotateButton, 2);
        Grid.SetColumn(_rotateButton, 0);
        grid.Children.Add(_rotateButton);

        _customPaperButton.Content = Localizer.T("Paper.CustomBtn");
        _customPaperButton.Margin = new Thickness(3, 0, 0, 0);
        _customPaperButton.Padding = new Thickness(6, 3, 6, 3);
        _customPaperButton.Click += (_, _) => ShowCustomPaperDialog();
        Grid.SetRow(_customPaperButton, 2);
        Grid.SetColumn(_customPaperButton, 1);
        grid.Children.Add(_customPaperButton);

        // 连续纸独立开关：与预设正交（选 A4 再勾上就是"A4 宽的连续纸"）。走内核命令可撤销。
        _continuousToggle.Content = "连续纸（打印长度随内容自适应）";
        _continuousToggle.Margin = new Thickness(0, 4, 0, 0);
        _continuousToggle.Checked += (_, _) => TogglePaperContinuous(true);
        _continuousToggle.Unchecked += (_, _) => TogglePaperContinuous(false);
        Grid.SetRow(_continuousToggle, 3);
        Grid.SetColumn(_continuousToggle, 0);
        Grid.SetColumnSpan(_continuousToggle, 2);
        grid.Children.Add(_continuousToggle);

        _paperInfo.Foreground = Brushes.Gray;
        _paperInfo.TextWrapping = TextWrapping.Wrap;
        _paperInfo.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_paperInfo, 4);
        Grid.SetColumn(_paperInfo, 0);
        Grid.SetColumnSpan(_paperInfo, 2);
        grid.Children.Add(_paperInfo);

        return grid;
    }

    /// <summary>页面区持久实例：属性面板每次 Rebuild 复用同一棵控件树（见 EmptyStateContentBuilder 挂接处注释）。</summary>
    UIElement? _pageSection;

    /// <summary>右属性栏无选中空白区 = 纸张设置（含连续纸开关）+ 水印，页面级设置集中在这里（与 Avalonia 宿主一致）。</summary>
    UIElement BuildPageSection()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(BuildPaperPanel());
        stack.Children.Add(BuildWatermarkSection());
        return stack;
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
    UIElement BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox
        {
            Content = "启用水印（文字平铺斜纹）",
            Margin = new Thickness(10, 6, 10, 2),
        };
        _watermarkEnabled.Checked += (_, _) => CommitIfNotSyncing(() => CommitWatermark("启用水印", w => w.Enabled = true, w => w.Enabled = false));
        _watermarkEnabled.Unchecked += (_, _) => CommitIfNotSyncing(() => CommitWatermark("启用水印", w => w.Enabled = false, w => w.Enabled = true));

        _watermarkText = new TextBox();
        _watermarkText.LostFocus += (_, _) => CommitIfNotSyncing(CommitWatermarkText);
        _watermarkText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitIfNotSyncing(CommitWatermarkText);
            }
        };

        _watermarkBinding = new TextBox();
        _watermarkBinding.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkField("水印字段绑定", _watermarkBinding, w => w.Binding, (w, v) => w.Binding = v));
        _watermarkBinding.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitIfNotSyncing(() => CommitWatermarkField("水印字段绑定", _watermarkBinding, w => w.Binding, (w, v) => w.Binding = v));
            }
        };

        _watermarkFontSize = new TextBox();
        _watermarkOpacity = new TextBox();
        _watermarkRotation = new TextBox();
        _watermarkSpacing = new TextBox();
        _watermarkFontSize.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印字号", "FontSizePt", 14, 4, 200, _watermarkFontSize));
        _watermarkOpacity.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印透明度", "Opacity", 0.2, 0, 1, _watermarkOpacity));
        _watermarkRotation.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印角度", "RotationDeg", -30, -90, 90, _watermarkRotation));
        _watermarkSpacing.LostFocus += (_, _) => CommitIfNotSyncing(() => CommitWatermarkNumber("水印间距", "SpacingMm", 60, 4, 400, _watermarkSpacing));
        _watermarkFontSize.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitIfNotSyncing(() => CommitWatermarkNumber("水印字号", "FontSizePt", 14, 4, 200, _watermarkFontSize)); } };
        _watermarkOpacity.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitIfNotSyncing(() => CommitWatermarkNumber("水印透明度", "Opacity", 0.2, 0, 1, _watermarkOpacity)); } };
        _watermarkRotation.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitIfNotSyncing(() => CommitWatermarkNumber("水印角度", "RotationDeg", -30, -90, 90, _watermarkRotation)); } };
        _watermarkSpacing.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitIfNotSyncing(() => CommitWatermarkNumber("水印间距", "SpacingMm", 60, 4, 400, _watermarkSpacing)); } };

        // 颜色用 hex 文本框（与元素属性颜色行同一口径），取色器等富交互后续按需补。
        _watermarkColor = new TextBox();
        _watermarkColor.LostFocus += (_, _) => CommitIfNotSyncing(CommitWatermarkColor);
        _watermarkColor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitIfNotSyncing(CommitWatermarkColor);
            }
        };

        UIElement FieldRow(string label, UIElement editor)
        {
            var grid = new Grid { Margin = new Thickness(10, 1, 10, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2),
            };
            grid.Children.Add(labelBlock);
            editor.SetValue(Grid.ColumnProperty, 1);
            editor.SetValue(MarginProperty, new Thickness(0, 2, 0, 2));
            grid.Children.Add(editor);
            return grid;
        }

        var pairRow = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        pairRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pairRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sizePair = FieldPair("字号", _watermarkFontSize);
        var opacityPair = FieldPair("透明度", _watermarkOpacity);
        sizePair.SetValue(Grid.ColumnProperty, 0);
        opacityPair.SetValue(Grid.ColumnProperty, 1);
        pairRow.Children.Add(sizePair);
        pairRow.Children.Add(opacityPair);

        var angleRow = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        angleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        angleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rotationPair = FieldPair("角度", _watermarkRotation);
        var spacingPair = FieldPair("间距", _watermarkSpacing);
        rotationPair.SetValue(Grid.ColumnProperty, 0);
        spacingPair.SetValue(Grid.ColumnProperty, 1);
        angleRow.Children.Add(rotationPair);
        angleRow.Children.Add(spacingPair);

        UIElement FieldPair(string label, UIElement editor)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) });
            editor.SetValue(Grid.ColumnProperty, 1);
            grid.Children.Add(editor);
            return grid;
        }

        var section = new StackPanel();
        section.Children.Add(new TextBlock
        {
            Text = "水印",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 8, 10, 2),
        });
        section.Children.Add(_watermarkEnabled);
        section.Children.Add(FieldRow("文字", _watermarkText));
        section.Children.Add(FieldRow("字段绑定", _watermarkBinding));
        section.Children.Add(pairRow);
        section.Children.Add(angleRow);
        section.Children.Add(FieldRow("颜色", _watermarkColor));

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
            _watermarkEnabled.IsChecked = w.Enabled;
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

    UIElement BuildToolbox()
    {
        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = Localizer.T("Toolbox.Section"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // WPF 的 TextBox 没有内置占位符：用一块 IsHitTestVisible=false 的灰字盖在输入框上，
        // 有内容时收起（各平台占位实现不同，这里只服务 WPF）。
        var search = new TextBox();
        var searchPlaceholder = new TextBlock
        {
            Text = Localizer.T("Toolbox.Search"),
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        search.TextChanged += (_, _) =>
        {
            searchPlaceholder.Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyToolboxFilter(search.Text ?? string.Empty);
        };
        var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        searchHost.Children.Add(search);
        searchHost.Children.Add(searchPlaceholder);
        Grid.SetRow(searchHost, 1);
        grid.Children.Add(searchHost);

        ApplyToolboxFilter(string.Empty);
        _toolbox.DisplayMemberPath = nameof(ToolboxItem.Label);
        _toolbox.MouseDoubleClick += (_, _) => AddFromToolbox();
        _toolbox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddFromToolbox();
                e.Handled = true;
            }
        };
        Grid.SetRow(_toolbox, 2);
        grid.Children.Add(_toolbox);

        HookToolboxDragDrop();

        var add = new Button
        {
            Content = Localizer.T("Toolbox.AddToCanvas"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 3);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = Localizer.T("Toolbox.HintDrag"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(hint, 4);
        grid.Children.Add(hint);

        return grid;
    }

    /// <summary>工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。重设 ItemsSource 会清选择，可接受。</summary>
    void ApplyToolboxFilter(string query)
        => _toolbox.ItemsSource = Array.FindAll(
            Array.ConvertAll(ElementButtons, item => new ToolboxItem(item.Label, item.Key)),
            item => ToolboxNames.Matches(item.TypeKey, query));

    /// <summary>
    /// 工具箱条目拖入画布：自定格式载荷只认自己的拖拽，别家拖文本进来不会误触发放置。
    /// 落点经视口换算成页面 mm，元素中心落在松手处（与 Avalonia 基准的"松手位置"口径一致）。
    /// </summary>
    const string ToolboxDragFormat = "CrossPrint.Toolbox.TypeKey";

    void HookToolboxDragDrop()
    {
        _toolbox.PreviewMouseLeftButtonDown += (_, e) => _toolboxDragStart = e.GetPosition(_toolbox);
        _toolbox.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _toolbox.SelectedItem is not ToolboxItem item)
                return;

            // 越过 4px 阈值才算拖拽：单纯点击仍走双击/按钮的"加到中心"。
            var pos = e.GetPosition(_toolbox);
            if (Math.Abs(pos.X - _toolboxDragStart.X) < 4 && Math.Abs(pos.Y - _toolboxDragStart.Y) < 4)
                return;

            _toolboxDragStart = pos; // 拖拽被取消（Esc）时别立刻又触发一次
            DragDrop.DoDragDrop(_toolbox, new DataObject(ToolboxDragFormat, item.TypeKey), DragDropEffects.Copy);
        };

        _designer.AllowDrop = true;
        _designer.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(ToolboxDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _designer.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(ToolboxDragFormat) || e.Data.GetData(ToolboxDragFormat) is not string typeKey)
                return;

            var dpi = _designer.Dpi;
            var dip = e.GetPosition(_designer);
            var mm = _designer.Kernel.Viewport.ToMm(dip.X * dpi / 96.0, dip.Y * dpi / 96.0, dpi);
            Run(() => _designer.Kernel.AddElementAt(typeKey, mm.X, mm.Y));
            e.Handled = true;
        };
    }

    Point _toolboxDragStart;

    UIElement BuildStatusBar()
    {
        _status.Foreground = Brushes.DimGray;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(10, 3, 10, 3);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Foreground = Brushes.DimGray;
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Margin = new Thickness(8, 3, 4, 3);

        Button ZoomBtn(string text, Action action, string tip)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 26,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(2, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tip,
            };
            b.Click += (_, _) => action();
            return b;
        }

        var zoomBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zoomBox.Children.Add(ZoomBtn(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip")));
        zoomBox.Children.Add(ZoomBtn(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip")));
        zoomBox.Children.Add(ZoomBtn(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip")));
        zoomBox.Children.Add(ZoomBtn(Localizer.T("Toolbar.FitToPage"), FitToPageFromStatus, Localizer.T("Toolbar.FitToPage.Tip")));

        // DockPanel：缩放组与比例固定左侧，状态文本占剩余宽度才能正确省略号截断。
        var dock = new DockPanel();
        DockPanel.SetDock(zoomBox, Dock.Left);
        DockPanel.SetDock(_zoomText, Dock.Left);
        dock.Children.Add(zoomBox);
        dock.Children.Add(_zoomText);
        dock.Children.Add(_status);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = dock,
        };
    }

    /// <summary>「适合窗口」用宿主真实绘制表面尺寸；它不发 ViewChanged，读数要手动刷一次。</summary>
    void FitToPageFromStatus()
    {
        _designer.FitToPage();
        UpdateZoomText();
        FocusDesigner();
    }

    void DoUndo()
    {
        if (!EditorActions.Undo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToUndo"));
            return;
        }

        FocusDesigner();
        SetStatus(Localizer.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!EditorActions.Redo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToRedo"));
            return;
        }

        FocusDesigner();
        SetStatus(Localizer.T("Status.Redone"));
    }

    /// <summary>撤销栈变化由内核在 UI 线程触发，直接刷按钮可用态即可。</summary>
    void UpdateUndoButtons()
    {
        if (_undoButton is null)
            return;

        _undoButton.IsEnabled = _designer.Kernel.CanUndo;
        _redoButton.IsEnabled = _designer.Kernel.CanRedo;
    }

    /// <summary>缩放比例读数：−/+/100% 都走内核 KeyAction，会发 ViewChanged 自动刷新。</summary>
    void UpdateZoomText()
        => _zoomText.Text = Localizer.T("Status.Zoom", EditorActions.ZoomPercent(_designer.Kernel));

    void BuildCanvasContextMenu()
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = _designer,
        };

        MenuItem AddItem(string header, Action action, string? gesture = null, bool selectionItem = false)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
                item.InputGestureText = gesture;

            item.Click += (_, _) => action();
            if (selectionItem)
                _selectionMenuItems.Add(item);

            menu.Items.Add(item);
            return item;
        }

        AddItem(Localizer.T("Canvas.Context.Duplicate"), DuplicateSelection, "Ctrl+D", selectionItem: true);
        AddItem(Localizer.T("Canvas.Context.Delete"), DeleteSelection, "Delete", selectionItem: true);
        menu.Items.Add(new Separator());

        AddItem(Localizer.T("Canvas.Context.BringForward"), () => RunKernel(kernel => kernel.BringForward()), selectionItem: true);
        AddItem(Localizer.T("Canvas.Context.SendBackward"), () => RunKernel(kernel => kernel.SendBackward()), selectionItem: true);
        AddItem(Localizer.T("Canvas.Context.BringToFront"), () => RunKernel(kernel => kernel.BringToFront()), selectionItem: true);
        AddItem(Localizer.T("Canvas.Context.SendToBack"), () => RunKernel(kernel => kernel.SendToBack()), selectionItem: true);
        menu.Items.Add(new Separator());

        AddItem(Localizer.T("Canvas.Context.AlignCenter"), () =>
        {
            RunKernel(kernel => kernel.CenterSelectionHorizontally());
            SetStatus(Localizer.T("Status.HAlignCenter"));
        }, selectionItem: true);

        _verticalCenterItem = AddItem(Localizer.T("Canvas.Context.AlignMiddle"), () =>
        {
            RunKernel(kernel => kernel.CenterSelectionVertically());
            SetStatus(Localizer.T("Status.VAlignCenter"));
        }, selectionItem: true);
        menu.Items.Add(new Separator());

        AddItem(Localizer.T("Canvas.Context.FitToPage"), () =>
        {
            _designer.FitToPage();
            FocusDesigner();
        });
        AddItem(Localizer.T("Canvas.Context.ActualSize"), SetActualZoom);
        menu.Items.Add(new Separator());

        _rotatePaperItem = AddItem(Localizer.T("Canvas.Context.RotateLandscape"), TogglePaperOrientation);
        AddItem(Localizer.T("Canvas.Context.CustomPaper"), ShowCustomPaperDialog);

        menu.Opened += OnCanvasMenuOpened;
        _designer.ContextMenu = menu;

        // ContextMenu 打开前先按右键位置命中/清选，菜单里的"副本/层次/居中"才有明确对象。
        _designer.ContextMenuOpening += OnCanvasContextMenuOpening;
    }

    void OnCanvasContextMenuOpening(object sender, ContextMenuEventArgs e) => SelectElementUnderMouse();

    void OnCanvasMenuOpened(object? sender, RoutedEventArgs e)
    {
        RefreshCanvasMenuState();
        SyncPaperControls();
    }

    void SelectElementUnderMouse()
    {
        var dpi = _designer.Dpi;
        var dip = Mouse.GetPosition(_designer);
        if (dip.X < 0 || dip.Y < 0 || dip.X > _designer.ActualWidth || dip.Y > _designer.ActualHeight)
            return;

        _designer.Kernel.SelectAt(new PointerPoint(
            dip.X * dpi / 96.0,
            dip.Y * dpi / 96.0,
            dpi,
            PointerButton.Secondary));
    }

    void RefreshCanvasMenuState()
    {
        var hasSelection = !_designer.Kernel.Selection.IsEmpty;
        foreach (var item in _selectionMenuItems)
            item.IsEnabled = hasSelection;

        var paper = _designer.Kernel.Template.Paper;
        _verticalCenterItem.IsEnabled = hasSelection && !paper.IsContinuous;
        _rotatePaperItem.IsEnabled = !paper.IsContinuous;
        _rotatePaperItem.Header = paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Canvas.Context.RotatePortrait")
            : Localizer.T("Canvas.Context.RotateLandscape");
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
            return;

        // 阶段 E：Ctrl+S 立即保存（不抢文本控件的撤销栈——PreviewKeyDown 是隧道事件，全局生效）。
        if (e.Key == Key.S)
        {
            e.Handled = true;
            SaveSolution(false);
            return;
        }

        // 文本框（属性面板 / JSON 面板）有焦点时把 Ctrl+Z/Y 留给文本自己的撤销栈。
        // PreviewKeyDown 是隧道事件，全局都要能撤销，不能限定画布焦点。
        if (Keyboard.FocusedElement is not TextBoxBase && e.Key is Key.Z or Key.Y)
        {
            e.Handled = true;
            if (e.Key == Key.Y || (modifiers & ModifierKeys.Shift) != 0)
                DoRedo();
            else
                DoUndo();
            return;
        }

        // Ctrl+D 只在设计画布有焦点时生效：属性面板的文本框里按键不该被抢。
        if (!_designer.IsKeyboardFocusWithin)
            return;

        if (e.Key != Key.D || (modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) != 0)
            return;

        e.Handled = true;
        DuplicateSelection();
    }

    void AddFromToolbox()
    {
        if (_toolbox.SelectedItem is not ToolboxItem item)
        {
            SetStatus(Localizer.T("Status.SelectFirst"), error: true);
            return;
        }

        _designer.Kernel.AddElementAtCenter(item.TypeKey);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ElementAddedCenter", item.Label));
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"), error: true);
            FocusDesigner();
            return;
        }

        _designer.Kernel.DuplicateSelection();
        FocusDesigner();
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void DeleteSelection()
    {
        _designer.Kernel.DeleteSelection();
        FocusDesigner();
    }

    void RunKernel(Action<EditorKernel> action)
    {
        action(_designer.Kernel);
        FocusDesigner();
    }

    void SetActualZoom()
    {
        _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        FocusDesigner();
    }

    void FocusDesigner() => Keyboard.Focus(_designer);

    void Run(Action action)
    {
        action();
        _designer.Focus();
    }

    void OnPaperChoiceChanged()
    {
        if (_syncingPaper || _paperCombo.SelectedIndex < 0)
            return;

        var choice = PaperChoices[_paperCombo.SelectedIndex];
        if (choice.Key is null)
        {
            // "自定义…"不是纸张状态：弹窗取消时由 SyncPaperControls 把下拉恢复成当前纸张。
            // 延后到下拉关闭后再弹模态框，避免在 ComboBox.SelectionChanged 里开窗把下拉卡在打开态。
            if (_customDialogPending)
                return;

            _customDialogPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _customDialogPending = false;
                ShowCustomPaperDialog();
            }));
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
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SyncPaperControls();
        FocusDesigner();
    }

    void ShowCustomPaperDialog()
    {
        var dialog = new PaperSizeDialog(_designer.Kernel.Template.Paper) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            SyncPaperControls();
            FocusDesigner();
            return;
        }

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
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SyncPaperControls();
        FocusDesigner();
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
        FocusDesigner();
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus(Localizer.T("Status.ContinuousCannotRotate"), error: true);
            return;
        }

        try
        {
            kernel.RotatePaper();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.RotateFailed", ex.Message), error: true);
            FocusDesigner();
            return;
        }

        SyncPaperControls();
        SetStatus(kernel.Template.Paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Status.PaperLandscape")
            : Localizer.T("Status.PaperPortrait"));
        FocusDesigner();
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
            _continuousToggle.IsChecked = continuous;
            _rotateButton.IsEnabled = !continuous;
            _rotateButton.Content = paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Paper.RotatePortrait")
                : Localizer.T("Paper.Rotate");

            // 菜单项状态由 RefreshCanvasMenuState 维护；这里只同步纸张相关的文字与可用性。
            if (_rotatePaperItem is not null)
            {
                _rotatePaperItem.IsEnabled = !continuous;
                _rotatePaperItem.Header = paper.Orientation == PaperOrientation.Landscape
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
        var name = paper.Preset is null || paper.Preset == PaperTypePresets.Custom ? Localizer.T("Paper.Custom") : paper.Preset;
        var orientation = paper.Orientation == PaperOrientation.Landscape
            ? Localizer.T("Paper.Orientation.Landscape")
            : Localizer.T("Paper.Orientation.Portrait");
        return paper.IsContinuous
            ? $"{name}：{paper.PageWidthMm:0.##} mm 宽，长度随内容（{orientation}）"
            : $"{name}：{paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm（{orientation}）";
    }

    UIElement BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起只留一条标题带，把竖向空间还给画布。
        _jsonToggle.Content = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.FontWeight = FontWeights.SemiBold;
        _jsonToggle.VerticalAlignment = VerticalAlignment.Center;
        _jsonToggle.Margin = new Thickness(0, 0, 10, 0);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(_jsonToggle.IsChecked == true);

        var apply = new Button { Content = Localizer.T("JsonPanel.Apply"), Padding = new Thickness(8, 2, 8, 2) };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Content = Localizer.T("JsonPanel.Reload"),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
        };
        reload.Click += (_, _) => RefreshTemplateJson(Localizer.T("Status.JsonReloaded"));

        _jsonState.FontSize = 12;
        _jsonState.Foreground = Brushes.Gray;
        _jsonState.VerticalAlignment = VerticalAlignment.Center;
        _jsonState.TextTrimming = TextTrimming.CharacterEllipsis;
        _jsonState.Margin = new Thickness(10, 0, 0, 0);

        var header = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        header.Children.Add(_jsonToggle);
        Grid.SetColumn(apply, 1);
        header.Children.Add(apply);
        Grid.SetColumn(reload, 2);
        header.Children.Add(reload);
        Grid.SetColumn(_jsonState, 3);
        header.Children.Add(_jsonState);

        _templateJson.AcceptsReturn = true;
        _templateJson.TextWrapping = TextWrapping.NoWrap;
        _templateJson.FontFamily = new FontFamily("Consolas");
        _templateJson.FontSize = 12;
        _templateJson.Margin = new Thickness(8, 0, 8, 6);
        _templateJson.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _templateJson.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();
        _templateJson.PreviewKeyDown += OnTemplateJsonKeyDown;

        // 展开时中间夹一道行分割条，高度可拖；收起时内容和分割条一起隐藏。
        _jsonContentRow = new RowDefinition { Height = new GridLength(0) };
        _jsonSplitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Rows,
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(_jsonContentRow);
        grid.Children.Add(header);
        Grid.SetRow(_jsonSplitter, 1);
        grid.Children.Add(_jsonSplitter);
        Grid.SetRow(_templateJson, 2);
        grid.Children.Add(_templateJson);

        SetJsonPanelExpanded(false);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    void SetJsonPanelExpanded(bool expanded)
    {
        _jsonToggle.IsChecked = expanded;
        _jsonToggle.Content = (expanded ? "▾ " : "▸ ") + Localizer.T("JsonPanel.Title");
        _jsonContentRow.Height = new GridLength(expanded ? 230 : 0);
        _jsonSplitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
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

    void OnTemplateJsonKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
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
        _templateJson.ScrollToHome();
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
                FocusDesigner();
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
            FocusDesigner();
        }
        catch (Exception ex)
        {
            SetJsonState(Localizer.T("Status.JsonNotApplied", ex.Message), error: true);
        }
    }

    void SetJsonState(string text, bool error)
    {
        _jsonState.Text = text;
        _jsonState.Foreground = error ? Brushes.Firebrick : Brushes.Gray;
    }

    void OpenStyleLibrary() => new StyleLibraryDialog(_designer.Kernel) { Owner = this }.ShowDialog();

    /// <summary>模板库对话框：分类列表 + 模板卡片网格（缩略图即用真实引擎渲染），点击卡片套用模板。</summary>
    void OpenTemplateLibrary()
    {
        var entries = BuiltinTemplateCatalog.BuildEntries();
        Window? window = null;
        var byCategory = new Dictionary<string, List<BuiltinTemplateCatalog.Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byCategory.TryGetValue(entry.Category, out var list))
                byCategory[entry.Category] = list = new List<BuiltinTemplateCatalog.Entry>();
            list.Add(entry);
        }

        var categoryList = new ListBox { Width = 150, Margin = new Thickness(8) };
        categoryList.ItemsSource = BuiltinTemplateCatalog.Categories;
        categoryList.SelectedIndex = 0;

        var wrap = new WrapPanel { Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = wrap, Margin = new Thickness(8) };

        void ApplyFromLibrary(BuiltinTemplateCatalog.Entry entry)
        {
            _designer.LoadTemplate(entry.Create());
            BindKernel(entry.Name);
            SetStatus(Localizer.T("Status.TemplateApplied", entry.Name));
            _designer.Focus();
        }

        UIElement BuildCard(BuiltinTemplateCatalog.Entry entry)
        {
            var border = new Border
            {
                Width = 220,
                Height = 150,
                Margin = new Thickness(6),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Cursor = Cursors.Hand,
            };

            var title = new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(title, Dock.Top);
            var thumb = new Image { Width = 200, Height = 96, Stretch = Stretch.Uniform };
            DockPanel.SetDock(thumb, Dock.Top);
            var desc = new TextBlock { Text = entry.Description, FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(desc, Dock.Top);

            var dock = new DockPanel();
            dock.Children.Add(title);
            dock.Children.Add(thumb);
            dock.Children.Add(desc);
            border.Child = dock;

            try
            {
                thumb.Source = PngToImageSource(BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create()));
            }
            catch
            {
                // 缩略图渲染失败不阻断选单本身。
            }

            border.MouseLeftButtonUp += (_, _) =>
            {
                window!.Close();
                ApplyFromLibrary(entry);
            };
            return border;
        }

        void ShowCards(string category)
        {
            wrap.Children.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            foreach (var entry in list)
                wrap.Children.Add(BuildCard(entry));
        }

        categoryList.SelectionChanged += (_, _) =>
        {
            if (categoryList.SelectedItem is string category)
                ShowCards(category);
        };
        ShowCards("全部");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(categoryList, 0);
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(categoryList);
        grid.Children.Add(scroll);

        var hint = new TextBlock { Text = Localizer.T("TemplateLibrary.Hint"), Margin = new Thickness(4), Foreground = Brushes.Gray };
        var dock = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);
        dock.Children.Add(grid);

        window = new Window
        {
            Title = Localizer.T("TemplateLibrary.Title"),
            Width = 1080,
            Height = 720,
            Content = dock,
            Owner = this,
        };
        window.ShowDialog();
    }

    /// <summary>把共享层渲染出的 PNG 字节转成 WPF 图像源（冻结以便跨线程/缓存）。</summary>
    static ImageSource PngToImageSource(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    void ImportHiprintDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.T("Dialog.Import.Title"),
            Filter = $"{Localizer.T("Dialog.Import.HiprintFilter")} (*.js;*.json;*.txt)|*.js;*.json;*.txt|{Localizer.T("Dialog.Import.AllFiles")} (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ImportHiprint(File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.HiprintImportFailed", ex.Message), error: true);
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Import.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
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

        FocusDesigner();
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
        Title = $"{Localizer.T("App.Title.Short")} — WPF — {_session.CurrentName}{mark}";
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
        var listBox = new ListBox { DisplayMemberPath = "Name", ItemsSource = records };

        var openButton = new Button { Content = Localizer.T("Dialog.Open"), Width = 90, Margin = new Thickness(4), IsDefault = true };
        var cancelButton = new Button { Content = Localizer.T("Dialog.Cancel"), Width = 90, Margin = new Thickness(4), IsCancel = true };
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        buttonPanel.Children.Add(openButton);
        buttonPanel.Children.Add(cancelButton);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(listBox, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(listBox);
        grid.Children.Add(buttonPanel);

        var window = new Window
        {
            Title = Localizer.T("Dialog.OpenSolution"),
            Width = 460,
            Height = 380,
            Owner = this,
            Content = grid,
        };

        void OpenSelected()
        {
            if (listBox.SelectedItem is TemplateRecord rec)
            {
                window.Close();
                OpenSolution(rec.Id);
            }
        }

        openButton.Click += (_, _) => OpenSelected();
        listBox.MouseDoubleClick += (_, _) => OpenSelected();
        if (records.Count > 0)
            listBox.SelectedIndex = 0;

        window.ShowDialog();
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
    }

    void RefreshRecentMenu(MenuItem recentItem)
    {
        recentItem.Items.Clear();
        foreach (var record in _session.Recent(8))
        {
            var item = new MenuItem { Header = record.Name, ToolTip = record.UpdatedAt.ToString("g", CultureInfo.CurrentCulture) };
            item.Click += (_, _) => OpenSolution(record.Id);
            recentItem.Items.Add(item);
        }

        if (recentItem.Items.Count == 0)
            recentItem.Items.Add(new MenuItem { Header = Localizer.T("Menu.File.NoRecent"), IsEnabled = false });
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
            SetStatus(Localizer.T("Error.SaveFailed", ex.Message), error: true);
            return false;
        }

        UpdateTitle();
        return true;
    }

    string? PromptName(string defaultName)
    {
        var textBox = new TextBox { Text = defaultName, Width = 320, Margin = new Thickness(4) };
        var okButton = new Button { Content = Localizer.T("Dialog.OK"), Width = 90, Margin = new Thickness(4), IsDefault = true };
        var cancelButton = new Button { Content = Localizer.T("Dialog.Cancel"), Width = 90, Margin = new Thickness(4), IsCancel = true };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(textBox, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(textBox);
        grid.Children.Add(buttonPanel);

        var window = new Window
        {
            Title = Localizer.T("Dialog.SaveAs"),
            Width = 380,
            Height = 150,
            Owner = this,
            Content = grid,
        };

        string? result = null;
        void Accept()
        {
            result = textBox.Text.Trim();
            if (string.IsNullOrEmpty(result))
                result = null;
            window.DialogResult = true;
            window.Close();
        }

        okButton.Click += (_, _) => Accept();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Accept();
        };

        window.ShowDialog();
        return result;
    }

    void ExportSolutionJson()
    {
        var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "crossprint-template.json",
        };

        if (dialog.ShowDialog(this) != true)
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
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Export.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ExportPdf()
    {
        var dialog = new SaveFileDialog
        {
            Title = Localizer.T("Dialog.Export.Title"),
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = "crossprint-sample.pdf",
        };

        if (dialog.ShowDialog(this) != true)
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
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
            MessageBox.Show(this, ex.Message, Localizer.T("Dialog.Export.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
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

        var window = new PreviewWindow(preview) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>显示关于对话框窗口。</summary>
    void ShowAboutDialog()
    {
        // Logo：从 I18n 嵌入资源加载，失败则静默跳过
        Image? logoImage = null;
        System.Windows.Media.ImageSource? logoIcon = null;
        try
        {
            using var stream = LogoLoader.GetLogoStream();
            if (stream is not null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                logoImage = new Image { Source = bmp, Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
                logoIcon = BitmapFrame.Create(new MemoryStream(bytes));
            }
        }
        catch { }

        var productTitle = new TextBlock
        {
            Text = AboutInfo.ProductName,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 4),
        };

        var version = new TextBlock
        {
            Text = Localizer.T("About.Version", AboutInfo.Version),
            FontSize = 14,
            Foreground = Brushes.DimGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var description = new TextBlock
        {
            Text = Localizer.T("About.Description"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 4),
            Foreground = Brushes.DimGray,
        };

        // 第三方依赖列表：名称 + 描述 + License
        var thirdPartyPanel = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };
        thirdPartyPanel.Children.Add(new TextBlock
        {
            Text = Localizer.T("About.ThirdParty"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var tp in AboutInfo.ThirdParty)
        {
            thirdPartyPanel.Children.Add(new TextBlock
            {
                Text = $"  • {tp.Name}  ({tp.License}) — {tp.Description}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }

        var closeButton = new Button
        {
            Content = Localizer.T("About.Close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 12, 16, 12),
            Padding = new Thickness(16, 4, 16, 4),
        };

        var root = new StackPanel
        {
            Children =
            {
                productTitle,
                version,
                description,
                new Separator(),
                thirdPartyPanel,
                closeButton,
            },
        };
        if (logoImage is not null)
            root.Children.Insert(0, logoImage);

        var window = new Window
        {
            Title = Localizer.T("About.Title"),
            Width = 560,
            Height = 520,
            MinWidth = 420,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Content = new Border
            {
                Padding = new Thickness(4),
                Child = root,
            },
            Icon = logoIcon,
        };

        closeButton.Click += (_, _) => window.Close();

        window.ShowDialog();
    }

    /// <summary>防连点：一次作业没回来之前不发起第二次系统打印。</summary>
    bool _printing;

    /// <summary>
    /// 调起系统打印（SystemPrintTarget）：渲染一份临时 PDF 后交给 OS 打印管线。
    /// 不指定打印机——Windows 走 ShellExecute「print」动词，关联应用弹打印框让用户选打印机/确认份数；
    /// macOS/Linux 交给 CUPS 默认队列。真实打印由系统与应用完成，本层只等「交付受理」结果。
    /// </summary>
    void PrintToSystem()
    {
        if (_printing)
        {
            SetStatus(Localizer.T("Status.PrintingBusy"), error: true);
            return;
        }

        _printing = true;
        SetStatus(Localizer.T("Status.PrintingStarting"));
        var issues = new RenderReport();
        var document = new PrintDocument(_designer.Template, SampleData.Create());
        _ = PrintToSystemAsync(document, issues);
    }

    async Task PrintToSystemAsync(PrintDocument document, RenderReport issues)
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

    void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

    // 阶段 G：右上角 Toast（成功/提示浮层），3 秒自动消失，不拦截鼠标；同一时刻只保留最新一条。
    void ShowToast(string title, string? message = null)
    {
        if (_toastCard is not null)
            _toastLayer.Children.Remove(_toastCard);

        var icon = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Colors.SeaGreen),
            Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var textStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        textStack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(message))
            textStack.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.DimGray),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
            });

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE9, 0xF0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.25 },
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, textStack } },
        };
        card.HorizontalAlignment = HorizontalAlignment.Right;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Margin = new Thickness(0, 14, 14, 0);

        _toastCard = card;
        _toastLayer.Children.Add(card);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastLayer.Children.Remove(card);
            if (ReferenceEquals(_toastCard, card))
                _toastCard = null;
        };
        timer.Start();
    }

    static Button MakeButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(1, 0, 1, 0),
        };

        button.Click += (_, _) => action();
        if (tooltip is not null)
            button.ToolTip = tooltip;

        return button;
    }

    sealed record ToolboxItem(string Label, string TypeKey);

    sealed record PaperChoice(string? Key, string Label);
}
