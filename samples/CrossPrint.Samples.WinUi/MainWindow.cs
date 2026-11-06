using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.WinUi;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace CrossPrint.Samples.WinUi;

/// <summary>
/// 主窗口：顶部菜单栏 + 紧凑工具栏（短标签 + ToolTip + 分组分隔线），中部三列
/// （左 200 纸张栏 + 工具箱、中间设计画布、右 310 属性面板），底部可折叠模板 JSON 带（默认收起）+ 状态栏。
/// 纸张、右键菜单、Ctrl+D 的语义与 WinForms 示例一致，只是把控件换成 WinUI 的对应物。
/// </summary>
public sealed class MainWindow : Window
{
    const double BaseDpi = 96.0;

    // 与 WPF 基准一致：初始 1320×880，最小 1024×640（WinAppSDK 1.6 的 OverlappedPresenter
    // 还没有 PreferredMinimumWidth，用 AppWindow.Changed 事件把窗口撑回最小尺寸）。
    const int InitialWindowWidth = 1320;
    const int InitialWindowHeight = 880;
    const int MinWindowWidth = 1024;
    const int MinWindowHeight = 640;

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

    static readonly (string Label, string Key)[] ToolboxItems =
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

    readonly DesignerControl _designer;

    // 阶段 E：解决方案持久化（共享存储 + 会话）。模板落库到 AppData/CrossPrint/templates.db（SQLite）。
    readonly SolutionSession _session = new();
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // 阶段 G：右上角 Toast 浮层（顶层 Grid，不拦截鼠标）。
    readonly Grid _toastLayer = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    MenuFlyoutSubItem _recentSub = null!;
    readonly PropertyPanel _properties;
    readonly ListView _toolbox = new();
    readonly TextBlock _status;
    readonly TextBlock _zoomText = new();
    Button _undoButton = null!;
    Button _redoButton = null!;
    readonly ComboBox _paperCombo = new();
    readonly Button _rotatePaperButton = new();
    readonly CheckBox _continuousToggle = new();
    readonly TextBlock _paperInfo = new();
    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    // JSON 面板的折叠开关与内容行：它是低频的调试/导入入口，默认收起只留一条标题带。
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;

    bool _syncingPaper;
    // 右栏空白区（页面级水印）：_pageSection 必须持久复用，见 PropertyPanel 的空白区铁律。
    FrameworkElement? _pageSection;
    bool _syncingWatermark;
    CheckBox _watermarkEnabled = null!;
    TextBox _watermarkText = null!;
    TextBox _watermarkBinding = null!;
    TextBox _watermarkFontSize = null!;
    TextBox _watermarkOpacity = null!;
    TextBox _watermarkRotation = null!;
    TextBox _watermarkSpacing = null!;
    TextBox _watermarkColor = null!;

    bool _paperDialogOpen;
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;
    PreviewWindow? _previewWindow;

    /// <summary>创建主窗口并加载 kitchen-sink 示例模板。</summary>
    public MainWindow()
    {
        Title = Localizer.T("App.Title.Short") + " — WinUI";

        var template = LoadInitialTemplate(out var loadMessages);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 4, 10, 4),
        };
        _designer = new DesignerControl(template) { Margin = new Thickness(8) };

        // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
        // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
        _designer.Data = SampleData.Create();

        _properties = new PropertyPanel(_designer.Kernel);
        // 页面级水印挂在右栏空白区：必须返回懒构建的持久实例。属性面板随 ViewChanged /
        // TemplateChanged 频繁 Rebuild（先 Clear 摘树再重挂），每次新建控件树会让字段级单例
        // 控件重复进视觉树，WinUI 会抛 "already has a visual parent"（Avalonia 崩溃实锤）。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();

        Content = BuildLayout();
        // 窗口尺寸与 WPF 基准一致（AppWindow 尺寸含边框，单位是像素）。
        AppWindow.Resize(new SizeInt32(InitialWindowWidth, InitialWindowHeight));
        AppWindow.Changed += OnAppWindowChanged;

        _designer.PointerPressed += OnDesignerPointerPressed;
        BindKernel();

        // 阶段 E：桌面自动保存——30s 一拍，有脏改动且已入解决方案才落库；未保存的新模板不打扰（走 Ctrl+S 另存为）。
        _autosaveTimer.Tick += (_, _) => { if (_session.IsDirty) _ = SaveSolution(false, silent: true); };
        _autosaveTimer.Start();

        SetStatus(loadMessages.Count == 0
            ? Localizer.T("App.Status.Ready")
            : Localizer.T("App.Status.Warnings", string.Join("；", loadMessages)));
    }

    sealed record PaperChoice(string? Key, string Label);

    static PrintTemplate LoadInitialTemplate(out List<string> messages)
    {
        messages = [];
        var path = Path.Combine(AppContext.BaseDirectory, "kitchen-sink.template.json");
        if (!File.Exists(path))
        {
            messages.Add(Localizer.T("Status.KitchenSinkNotFound"));
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
            messages.Add(Localizer.T("Error.LoadFailed", ex.Message));
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>窗口小于最小尺寸时撑回去（WinAppSDK 1.6 的 OverlappedPresenter 还没有 PreferredMinimumWidth）。</summary>
    void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
            return;

        // 1.6 的 AppWindowChangedEventArgs 不带 Size，从 sender 上读当前尺寸。
        var width = Math.Max(sender.Size.Width, MinWindowWidth);
        var height = Math.Max(sender.Size.Height, MinWindowHeight);
        if (width != sender.Size.Width || height != sender.Size.Height)
            sender.Resize(new SizeInt32(width, height));
    }

    Grid BuildLayout()
    {
        var root = new Grid { RequestedTheme = ElementTheme.Light, Background = new SolidColorBrush(Colors.White) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var menuBar = BuildMenuBar();
        Grid.SetRow(menuBar, 0);
        root.Children.Add(menuBar);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var content = new Grid();
        // 三栏布局与 WPF 基准一致：左 200（纸张栏 + 工具箱）、中画布、右 310（属性面板）。
        // WinUI 3 没有内置 GridSplitter、也不为示例单独引入 CommunityToolkit 的 Sizers 包，
        // 这里用零依赖的轻量拖拽分隔条（两道 4px）实现「分栏可拖宽」：拖动改两侧固定栏宽、
        // 中间画布自适应，符合阶段 G 验收项。
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });        // 0 左侧栏
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });          // 1 左分隔条
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 画布
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });          // 3 右分隔条
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });        // 4 属性面板

        var leftSidebar = BuildLeftSidebar();
        Grid.SetColumn(leftSidebar, 0);
        content.Children.Add(leftSidebar);

        var leftSplitter = MakeColumnSplitter(content, targetColumn: 0, sign: +1);
        Grid.SetColumn(leftSplitter, 1);
        content.Children.Add(leftSplitter);

        var canvasHost = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            Child = _designer,
        };
        Grid.SetColumn(canvasHost, 2);
        content.Children.Add(canvasHost);

        var rightSplitter = MakeColumnSplitter(content, targetColumn: 4, sign: -1);
        Grid.SetColumn(rightSplitter, 3);
        content.Children.Add(rightSplitter);

        var propertiesHost = BuildPropertiesPanel();
        Grid.SetColumn(propertiesHost, 4);
        content.Children.Add(propertiesHost);

        Grid.SetRow(content, 2);
        root.Children.Add(content);

        var jsonPanel = BuildTemplateJsonPanel();
        Grid.SetRow(jsonPanel, 3);
        root.Children.Add(jsonPanel);

        var zoomBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.FitToPage"), FitToWindow, Localizer.T("Toolbar.FitToPage.Tip")));

        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Margin = new Thickness(8, 0, 4, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(10, 0, 10, 0);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;

        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(zoomBox, 0);
        Grid.SetColumn(_zoomText, 1);
        Grid.SetColumn(_status, 2);
        statusGrid.Children.Add(zoomBox);
        statusGrid.Children.Add(_zoomText);
        statusGrid.Children.Add(_status);

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = statusGrid,
        };
        Grid.SetRow(statusBar, 4);
        root.Children.Add(statusBar);

        // 快捷键挂在窗口内容根上：画布、工具栏、菜单关闭后都能用；属性输入框里的按键由处理器自己跳过。
        AddAccelerator(root, VirtualKey.D, VirtualKeyModifiers.Control, OnDuplicateAcceleratorInvoked);
        AddAccelerator(root, VirtualKey.Z, VirtualKeyModifiers.Control, OnUndoAcceleratorInvoked);
        AddAccelerator(root, VirtualKey.Y, VirtualKeyModifiers.Control, OnRedoAcceleratorInvoked);
        AddAccelerator(root, VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, OnRedoAcceleratorInvoked);
        // 阶段 E：Ctrl+S 立即保存。
        AddAccelerator(root, VirtualKey.S, VirtualKeyModifiers.Control, OnSaveAcceleratorInvoked);

        // 阶段 G：右上角 Toast 浮层——覆盖整窗、不拦截鼠标。
        Grid.SetRowSpan(_toastLayer, 100);
        Grid.SetColumnSpan(_toastLayer, 100);
        root.Children.Add(_toastLayer);

        return root;

        // 零依赖的轻量列分隔条：拖动改变 targetColumn 的固定像素宽（sign 决定方向），中间画布自适应。
        // 注：本示例的 WinUI 版本较旧，无 Cursor/InputCursor/HasPointerCapture API，故用标志位跟踪拖拽。
        Border MakeColumnSplitter(Grid grid, int targetColumn, double sign)
        {
            var splitter = new Border
            {
                Background = new SolidColorBrush(Colors.LightGray),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            double startX = 0;
            double startWidth = 0;
            Microsoft.UI.Xaml.Input.Pointer? active = null;
            splitter.PointerPressed += (_, e) =>
            {
                active = e.Pointer;
                splitter.CapturePointer(active);
                startX = e.GetCurrentPoint(grid).Position.X;
                startWidth = grid.ColumnDefinitions[targetColumn].Width.Value;
                splitter.Background = new SolidColorBrush(Colors.SteelBlue);
            };
            splitter.PointerMoved += (_, e) =>
            {
                if (active is null || e.Pointer != active)
                    return;
                var x = e.GetCurrentPoint(grid).Position.X;
                var w = startWidth + (x - startX) * sign;
                w = Math.Max(140, Math.Min(560, w));
                grid.ColumnDefinitions[targetColumn].Width = new GridLength(w);
            };
            splitter.PointerReleased += (_, e) =>
            {
                if (active is null || e.Pointer != active)
                    return;
                splitter.ReleasePointerCapture(active);
                active = null;
                splitter.Background = new SolidColorBrush(Colors.LightGray);
            };
            return splitter;
        }
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、加载复杂测试模板）。</summary>
    FrameworkElement BuildMenuBar()
    {
        var fileMenu = new MenuBarItem { Title = Localizer.T("Menu.File") };
        var importItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => _ = ImportHiprintAsync();
        fileMenu.Items.Add(importItem);

        var templatesItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.Templates") };
        ToolTipService.SetToolTip(templatesItem, Localizer.T("Menu.File.TemplatesTip"));
        templatesItem.Click += (_, _) => _ = OpenTemplateLibraryAsync();
        fileMenu.Items.Add(templatesItem);

        var stylesItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.StyleLibrary") };
        ToolTipService.SetToolTip(stylesItem, Localizer.T("Menu.File.StyleLibraryTip"));
        stylesItem.Click += (_, _) => _ = StyleLibraryDialog.ShowAsync(Content.XamlRoot, _designer.Kernel);
        fileMenu.Items.Add(stylesItem);

        fileMenu.Items.Add(new MenuFlyoutSeparator());

        var newItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.New") };
        newItem.Click += (_, _) => NewSolution();
        fileMenu.Items.Add(newItem);

        var openItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.Open") };
        openItem.Click += (_, _) => OpenSolutionDialog();
        fileMenu.Items.Add(openItem);

        _recentSub = new MenuFlyoutSubItem { Text = Localizer.T("Menu.File.Recent") };
        fileMenu.Items.Add(_recentSub);

        var saveItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.Save") };
        saveItem.Click += (_, _) => _ = SaveSolution(false);
        fileMenu.Items.Add(saveItem);

        var saveAsItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.SaveAs") };
        saveAsItem.Click += (_, _) => _ = SaveSolution(true);
        fileMenu.Items.Add(saveAsItem);

        fileMenu.Items.Add(new MenuFlyoutSeparator());

        var exportJsonItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.ExportJson") };
        exportJsonItem.Click += (_, _) => OnExportJson(this, null!);
        fileMenu.Items.Add(exportJsonItem);

        var exportPdfItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.ExportPdf") };
        exportPdfItem.Click += (_, _) => OnExportPdf(this, null!);
        fileMenu.Items.Add(exportPdfItem);

        var printItem = new MenuFlyoutItem { Text = Localizer.T("Menu.Print.System") };
        printItem.Click += (_, _) => PrintSolution();
        fileMenu.Items.Add(printItem);

        var helpMenu = new MenuBarItem { Title = Localizer.T("Menu.Help") };
        var aboutItem = new MenuFlyoutItem { Text = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => _ = ShowAboutDialogAsync();
        helpMenu.Items.Add(aboutItem);

        var preferred = LocalizationSwitch.PreferredCulture();
        helpMenu.Items.Add(new MenuFlyoutSeparator());
        var languageItem = new MenuFlyoutSubItem { Text = Localizer.T("Menu.Language") };
        void AddChoice(string? culture, string label)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = preferred is null
                    ? culture is null
                    : string.Equals(preferred, culture, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ChangeLanguage(culture);
            languageItem.Items.Add(item);
        }

        AddChoice(null, Localizer.T("Language.System"));
        AddChoice("zh-CN", Localizer.T("Language.Chinese"));
        AddChoice("en-US", Localizer.T("Language.English"));
        helpMenu.Items.Add(languageItem);

        var bar = new MenuBar();
        bar.Items.Add(fileMenu);
        bar.Items.Add(helpMenu);
        return bar;
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
                _ = ShowLanguageRestartFailedAsync(culture);
                break;
        }
    }

    async Task ShowLanguageRestartFailedAsync(string? culture)
    {
        var dialog = new ContentDialog
        {
            Title = Localizer.T("Menu.Language"),
            Content = Localizer.T("Language.SavedRestartFailed", LocalizationSwitch.DisplayName(culture)),
            CloseButtonText = Localizer.T("About.Close"),
            RequestedTheme = ElementTheme.Light,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    FrameworkElement BuildToolbar()
    {
        var bar = new StackPanel { Padding = new Thickness(6, 4, 6, 4), Spacing = 2 };

        // 第一行（元素条）与 WPF 基准一致：八种元素类型，点按钮即在页面中心新建。
        // 文字保持两三个字的短标签，完整说明放 ToolTip，窄窗口时省横向空间。
        var elementRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (label, key) in ToolboxItems)
        {
            var typeKey = key;
            elementRow.Children.Add(ActionButton(label, (_, _) => AddElement(typeKey), Localizer.T("Toolbar.AddElement.Tooltip", label)));
        }
        bar.Children.Add(elementRow);

        // 第二行：编辑/视图/输出合并成一条（对齐 WPF 双带方案的第二带），功能组之间用分隔线区隔。
        // 标签尽量短（完整说明在 ToolTip 里）：这一行项目最多，窄窗口时更要省横向空间。
        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        // 导入按钮放最前面：窗口变窄时末尾的按钮会先被裁掉。
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.Import"), OnImportHiprint, Localizer.T("Toolbar.Import.Tip")));
        _undoButton = ActionButton(Localizer.T("Toolbar.Undo"), (_, _) => DoUndo(), Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = ActionButton(Localizer.T("Toolbar.Redo"), (_, _) => DoRedo(), Localizer.T("Toolbar.Redo.Tip"));
        editRow.Children.Add(_undoButton);
        editRow.Children.Add(_redoButton);
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.Duplicate"), (_, _) => DuplicateSelection(), Localizer.T("Toolbar.Duplicate.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.Delete"), (_, _) => DeleteSelection(), Localizer.T("Toolbar.Delete.Tip")));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.BringForward"), (_, _) => RunKernel(kernel => kernel.BringForward()), Localizer.T("Toolbar.BringForward.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.SendBackward"), (_, _) => RunKernel(kernel => kernel.SendBackward()), Localizer.T("Toolbar.SendBackward.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.BringToFront"), (_, _) => RunKernel(kernel => kernel.BringToFront()), Localizer.T("Toolbar.BringToFront.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.SendToBack"), (_, _) => RunKernel(kernel => kernel.SendToBack()), Localizer.T("Toolbar.SendToBack.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Canvas.Context.AlignCenter"), (_, _) => CenterHorizontally(), Localizer.T("Canvas.Context.AlignCenter")));
        editRow.Children.Add(ActionButton(Localizer.T("Canvas.Context.AlignMiddle"), (_, _) => CenterVertically(), Localizer.T("Canvas.Context.AlignMiddle")));
        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单仍保留同名项。
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(Toggle(Localizer.T("Toolbar.Grid"), value => _designer.Overlay.Options.ShowGrid = value));
        editRow.Children.Add(Toggle(Localizer.T("Toolbar.Ruler"), value => _designer.Overlay.Options.ShowRulers = value));
        editRow.Children.Add(Toggle(Localizer.T("Toolbar.SafeArea"), value => _designer.Overlay.Options.ShowMarginGuides = value));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.ExportPdf"), OnExportPdf, Localizer.T("Toolbar.ExportPdf.Tip")));
        editRow.Children.Add(ActionButton(Localizer.T("Toolbar.Preview"), OnShowPreview, Localizer.T("Toolbar.Preview.Tip")));
        bar.Children.Add(editRow);

        return bar;
    }

    /// <summary>左栏（200px）：纸张栏在上、工具箱列表在下，两行分别是 Auto / Star。</summary>
    FrameworkElement BuildLeftSidebar()
    {
        var left = new Grid
        {
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var paperPanel = BuildPaperPanel();
        Grid.SetRow(paperPanel, 0);
        left.Children.Add(paperPanel);

        var toolbox = BuildToolbox();
        Grid.SetRow(toolbox, 1);
        left.Children.Add(toolbox);
        return left;
    }

    /// <summary>右栏（310px）：属性面板，自带滚动。</summary>
    FrameworkElement BuildPropertiesPanel()
    {
        var propertiesHost = new Grid
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        propertiesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        propertiesHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var propertiesTitle = new TextBlock
        {
            Text = Localizer.T("PropertyGrid.Section"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 6, 8, 0),
        };
        Grid.SetRow(propertiesTitle, 0);
        propertiesHost.Children.Add(propertiesTitle);

        Grid.SetRow(_properties, 1);
        propertiesHost.Children.Add(_properties);
        return propertiesHost;
    }

    FrameworkElement BuildPaperPanel()
    {
        var panel = new StackPanel
        {
            // 节标题即分组（不用 GroupBox 外框），留白收紧，把竖向空间让给下面的工具箱。
            Padding = new Thickness(8, 6, 8, 6),
            Spacing = 4,
        };
        panel.Children.Add(new TextBlock { Text = Localizer.T("Paper.Section"), FontWeight = FontWeights.SemiBold });

        foreach (var choice in PaperChoices)
            _paperCombo.Items.Add(choice.Label);

        _paperCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperCombo.SelectionChanged += OnPaperChoiceChanged;
        panel.Children.Add(_paperCombo);

        var buttons = new Grid { ColumnSpacing = 6 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _rotatePaperButton.Content = Localizer.T("Paper.Rotate");
        _rotatePaperButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rotatePaperButton.Click += (_, _) => TogglePaperOrientation();
        Grid.SetColumn(_rotatePaperButton, 0);
        buttons.Children.Add(_rotatePaperButton);

        var customPaperButton = new Button
        {
            Content = Localizer.T("Paper.CustomBtn"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        customPaperButton.Click += (_, _) => RequestCustomPaperDialog();
        Grid.SetColumn(customPaperButton, 1);
        buttons.Children.Add(customPaperButton);

        panel.Children.Add(buttons);

        // 连续纸独立开关：与预设正交（选 A4 再勾上就是"A4 宽的连续纸"）。走内核命令可撤销。
        _continuousToggle.Content = "连续纸（打印长度随内容自适应）";
        _continuousToggle.Margin = new Thickness(0, 4, 0, 0);
        _continuousToggle.Checked += (_, _) => TogglePaperContinuous(true);
        _continuousToggle.Unchecked += (_, _) => TogglePaperContinuous(false);
        panel.Children.Add(_continuousToggle);

        _paperInfo.Foreground = new SolidColorBrush(Colors.Gray);
        _paperInfo.TextWrapping = TextWrapping.Wrap;
        _paperInfo.FontSize = 12;
        panel.Children.Add(_paperInfo);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = panel,
        };
    }

    /// <summary>右栏空白区持久实例：属性面板每次 Rebuild 复用同一棵控件树（见挂接处注释）。</summary>
    FrameworkElement BuildPageSection()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(BuildWatermarkSection());
        return stack;
    }

    /// <summary>
    /// 水印设置区：启用/文字/字段绑定/字号/透明度/角度/间距/颜色。
    /// 校验、夹取、可撤销提交、回填统一走共享层 <see cref="WatermarkEdits"/>，宿主只搭控件。
    /// </summary>
    FrameworkElement BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox
        {
            Content = Localizer.T("Watermark.Enabled"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        _watermarkEnabled.Checked += (_, _) =>
            CommitIfNotSyncing(() => WatermarkEdits.SetEnabled(_designer.Kernel, true));
        _watermarkEnabled.Unchecked += (_, _) =>
            CommitIfNotSyncing(() => WatermarkEdits.SetEnabled(_designer.Kernel, false));

        _watermarkText = WatermarkBox(() => WatermarkEdits.SetText(_designer.Kernel, _watermarkText.Text));
        _watermarkBinding = WatermarkBox(() => WatermarkEdits.SetBinding(_designer.Kernel, _watermarkBinding.Text));
        _watermarkFontSize = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.FontSizePt, _watermarkFontSize));
        _watermarkOpacity = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.Opacity, _watermarkOpacity));
        _watermarkRotation = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.RotationDeg, _watermarkRotation));
        _watermarkSpacing = WatermarkBox(() => CommitWatermarkNumber(WatermarkNumber.SpacingMm, _watermarkSpacing));
        _watermarkColor = WatermarkBox(CommitWatermarkColor);

        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock
        {
            Text = Localizer.T("Watermark.Section"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 2),
        });
        section.Children.Add(_watermarkEnabled);
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Text"), _watermarkText));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Binding"), _watermarkBinding));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.FontSize"), _watermarkFontSize));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Opacity"), _watermarkOpacity));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Rotation"), _watermarkRotation));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Spacing"), _watermarkSpacing));
        section.Children.Add(WatermarkRow(Localizer.T("Watermark.Color"), _watermarkColor));

        RefreshWatermarkControls();
        return section;
    }

    /// <summary>水印输入框：失焦或回车提交；回填期间的事件由 CommitIfNotSyncing 挡掉。</summary>
    TextBox WatermarkBox(Action commit)
    {
        var box = new TextBox();
        box.LostFocus += (_, _) => CommitIfNotSyncing(commit);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter)
                return;

            e.Handled = true;
            CommitIfNotSyncing(commit);
        };
        return box;
    }

    static FrameworkElement WatermarkRow(string label, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.DimGray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 6, 2),
        });
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    void CommitIfNotSyncing(Action commit)
    {
        if (!_syncingWatermark)
            commit();
    }

    void CommitWatermarkNumber(WatermarkNumber field, TextBox box)
    {
        // 非法输入共享层返回 false：把控件刷回模型值，别把脏文本留在框里。
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
            _watermarkEnabled.IsChecked = w.Enabled;
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

    /// <summary>工具箱：元素类型列表，双击或回车（或点按钮）添加到画布中心，与 WPF 基准一致。</summary>
    FrameworkElement BuildToolbox()
    {
        var grid = new Grid { Padding = new Thickness(8, 8, 8, 8), RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = Localizer.T("Toolbox.Section"), FontWeight = FontWeights.SemiBold });

        var search = new TextBox { PlaceholderText = Localizer.T("Toolbox.Search") };
        search.TextChanged += (_, _) => ApplyToolboxFilter(search.Text);
        Grid.SetRow(search, 1);
        grid.Children.Add(search);

        ApplyToolboxFilter(string.Empty);
        _toolbox.SelectionMode = ListViewSelectionMode.Single;
        _toolbox.DoubleTapped += (_, _) => AddFromToolbox();
        _toolbox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                AddFromToolbox();
                e.Handled = true;
            }
        };
        Grid.SetRow(_toolbox, 2);
        grid.Children.Add(_toolbox);

        var add = new Button
        {
            Content = Localizer.T("Toolbox.AddToCanvas"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 3);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = Localizer.T("Toolbox.HintDrag"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(hint, 4);
        grid.Children.Add(hint);

        HookToolboxDragDrop();
        return grid;
    }

    /// <summary>工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。条目用记录类型，SelectedIndex 不再等于全量下标。</summary>
    void ApplyToolboxFilter(string? query)
        => _toolbox.ItemsSource = ToolboxItems
            .Select(item => new ToolboxItem(item.Label, item.Key))
            .Where(item => ToolboxNames.Matches(item.TypeKey, query))
            .ToArray();

    /// <summary>工具箱条目拖入画布：DataPackage 属性里带类型键，别家拖文本进来不会误触发放置。</summary>
    const string ToolboxDragKey = "CrossPrint.Toolbox.TypeKey";

    void HookToolboxDragDrop()
    {
        // 整个列表可拖（条目跟随 DragItemsStarting）；列表本身不做拖放目标。
        _toolbox.CanDragItems = true;
        _toolbox.DragItemsStarting += (_, args) =>
        {
            if (args.Items.FirstOrDefault() is ToolboxItem item)
            {
                args.Data.Properties[ToolboxDragKey] = item.TypeKey;
                args.Data.RequestedOperation = DataPackageOperation.Copy;
            }
            else
            {
                args.Cancel = true;
            }
        };

        _designer.AllowDrop = true;
        _designer.DragOver += (_, e) =>
        {
            e.AcceptedOperation = e.DataView.Contains(ToolboxDragKey)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        };
        _designer.Drop += (_, e) =>
        {
            if (!e.DataView.Contains(ToolboxDragKey))
                return;

            if (!e.DataView.Properties.TryGetValue(ToolboxDragKey, out var value) || value is not string typeKey)
                return;

            // WinUI 拖放坐标是 DIP，统一走 ToDesignerPointer 换算成设备 px 再交给视口转 mm。
            var dip = e.GetPosition(_designer);
            var p = ToDesignerPointer(dip, PointerButton.Primary);
            var mm = _designer.Kernel.Viewport.ToMm(p.X, p.Y, p.Dpi);
            _designer.Kernel.AddElementAt(typeKey, mm.X, mm.Y);
            FocusCanvas();
            SetStatus(Localizer.T("Status.ElementAddedCenter", Localizer.T(ToolboxNames.I18nKeyOf(typeKey))));
            e.Handled = true;
        };
    }

    sealed record ToolboxItem(string Label, string TypeKey);

    void AddFromToolbox()
    {
        if (_toolbox.SelectedItem is not ToolboxItem item)
        {
            SetStatus(Localizer.T("Status.SelectFirst"));
            return;
        }

        AddElement(item.TypeKey);
        SetStatus(Localizer.T("Status.ElementAddedCenter", item.Label));
    }

    static Button ActionButton(string text, RoutedEventHandler handler, string? tooltip = null)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        button.Click += handler;
        // 按钮文字用短标签，完整说明放 ToolTip，工具栏横向更省。
        if (tooltip is not null)
            ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>状态栏缩放按钮：显式清零默认 Padding/MinHeight，26px 小格里字符才不会被挤没。</summary>
    static Button ZoomButton(string text, Action action, string tip)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 26,
            MinHeight = 0,
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => action();
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    static void AddAccelerator(FrameworkElement root, VirtualKey key, VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>WinUI 键盘加速器事件不带修饰键，Shift 状态要按当前线程键态读。</summary>
    static bool ModifierDown(VirtualKey key)
        // 不能整体 using Microsoft.UI.Input：CrossPrint.Designer.PointerPoint 与
        // Microsoft.UI.Input.PointerPoint 会撞成 CS0104，这里全限定调用即可。
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    /// <summary>
    /// Ctrl+Z：Shift 按下时交给更精确的 Ctrl+Shift+Z 加速器处理，
    /// 避免同一次按键被两个加速器各执行一次（撤销后又重做 = 看起来没反应）。
    /// </summary>
    void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || ModifierDown(VirtualKey.Shift))
            return;

        args.Handled = true;
        DoUndo();
    }

    void OnRedoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused())
            return;

        args.Handled = true;
        DoRedo();
    }

    void DoUndo()
    {
        if (!EditorActions.Undo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToUndo"));
            return;
        }

        FocusCanvas();
        SetStatus(Localizer.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!EditorActions.Redo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToRedo"));
            return;
        }

        FocusCanvas();
        SetStatus(Localizer.T("Status.Redone"));
    }

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

    /// <summary>工具栏分组分隔线：1px 细竖线，只做视觉区隔，不带任何交互。</summary>
    static FrameworkElement MakeToolbarSeparator() => new Border
    {
        Width = 1,
        Margin = new Thickness(4, 4, 4, 4),
        Background = new SolidColorBrush(Colors.LightGray),
    };

    CheckBox Toggle(string text, Action<bool> apply)
    {
        var check = new CheckBox
        {
            Content = text,
            IsChecked = true,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.Checked += (_, _) => ApplyToggle(apply, true);
        check.Unchecked += (_, _) => ApplyToggle(apply, false);
        return check;
    }

    void ApplyToggle(Action<bool> apply, bool value)
    {
        apply(value);
        _designer.Invalidate();
    }

    void OnDesignerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_designer);
        if (!point.Properties.IsRightButtonPressed)
            return;

        // 右键完全归本窗口：命中元素即单选、命中空白清选，然后就地弹菜单。
        // （宿主画布对右键不 CapturePointer，所以 PointerCaptureLost 不会把刚选中的元素清掉。）
        e.Handled = true;
        var position = point.Position;
        _designer.Kernel.SelectAt(ToDesignerPointer(position, PointerButton.Secondary));

        var flyout = BuildCanvasMenu();
        flyout.ShowAt(_designer, new FlyoutShowOptions { Position = new Point(position.X, position.Y) });
    }

    MenuFlyout BuildCanvasMenu()
    {
        var kernel = _designer.Kernel;
        var hasSelection = !kernel.Selection.IsEmpty;
        var paper = kernel.Template.Paper;

        var flyout = new MenuFlyout();

        var duplicateItem = new MenuFlyoutItem
        {
            Text = Localizer.T("Canvas.Context.Duplicate"),
            KeyboardAcceleratorTextOverride = "Ctrl+D",
            IsEnabled = hasSelection,
        };
        duplicateItem.Click += (_, _) => DuplicateSelection();
        flyout.Items.Add(duplicateItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = Localizer.T("Canvas.Context.Delete"),
            KeyboardAcceleratorTextOverride = "Delete",
            IsEnabled = hasSelection,
        };
        deleteItem.Click += (_, _) => DeleteSelection();
        flyout.Items.Add(deleteItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        void AddSelectionItem(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = hasSelection };
            item.Click += (_, _) => action();
            flyout.Items.Add(item);
        }

        AddSelectionItem(Localizer.T("Canvas.Context.BringForward"), () => RunKernel(k => k.BringForward()));
        AddSelectionItem(Localizer.T("Canvas.Context.SendBackward"), () => RunKernel(k => k.SendBackward()));
        AddSelectionItem(Localizer.T("Canvas.Context.BringToFront"), () => RunKernel(k => k.BringToFront()));
        AddSelectionItem(Localizer.T("Canvas.Context.SendToBack"), () => RunKernel(k => k.SendToBack()));

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddSelectionItem(Localizer.T("Canvas.Context.AlignCenter"), CenterHorizontally);

        var verticalCenterItem = new MenuFlyoutItem
        {
            Text = Localizer.T("Canvas.Context.AlignMiddle"),
            IsEnabled = hasSelection && !paper.IsContinuous,
        };
        verticalCenterItem.Click += (_, _) => CenterVertically();
        flyout.Items.Add(verticalCenterItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var fitItem = new MenuFlyoutItem { Text = Localizer.T("Toolbar.FitToPage") };
        fitItem.Click += (_, _) => FitToWindow();
        flyout.Items.Add(fitItem);

        var actualSizeItem = new MenuFlyoutItem { Text = Localizer.T("Canvas.Context.ActualSize") };
        actualSizeItem.Click += (_, _) => ZoomActual();
        flyout.Items.Add(actualSizeItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var rotateItem = new MenuFlyoutItem
        {
            Text = paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Canvas.Context.RotatePortrait")
                : Localizer.T("Canvas.Context.RotateLandscape"),
            IsEnabled = !paper.IsContinuous,
        };
        rotateItem.Click += (_, _) => TogglePaperOrientation();
        flyout.Items.Add(rotateItem);

        var customPaperItem = new MenuFlyoutItem { Text = Localizer.T("Canvas.Context.CustomPaper") };
        customPaperItem.Click += (_, _) => RequestCustomPaperDialog();
        flyout.Items.Add(customPaperItem);

        return flyout;
    }

    void OnDuplicateAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 属性输入框里的 Ctrl+D 不抢（与 WinForms 示例"只有画布有焦点才生效"同义）。
        if (IsTextInputFocused())
            return;

        args.Handled = true;
        DuplicateSelection();
    }

    bool IsTextInputFocused()
    {
        var xamlRoot = _designer.XamlRoot;
        if (xamlRoot is null)
            return false;

        var focused = FocusManager.GetFocusedElement(xamlRoot);
        return focused is TextBox or RichEditBox or PasswordBox;
    }

    void AddElement(string typeKey)
    {
        _designer.Kernel.AddElementAtCenter(typeKey);
        _designer.Focus(FocusState.Pointer);
        SetStatus(Localizer.T("Status.ElementAdded", typeKey));
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"));
            return;
        }

        _designer.Kernel.DuplicateSelection();
        FocusCanvas();
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void DeleteSelection()
    {
        _designer.Kernel.DeleteSelection();
        FocusCanvas();
    }

    void CenterHorizontally()
    {
        _designer.Kernel.CenterSelectionHorizontally();
        FocusCanvas();
        SetStatus(Localizer.T("Status.HAlignCenter"));
    }

    void CenterVertically()
    {
        if (_designer.Kernel.Template.Paper.IsContinuous)
        {
            SetStatus(Localizer.T("Status.ContinuousCannotRotate"));
            return;
        }

        _designer.Kernel.CenterSelectionVertically();
        FocusCanvas();
        SetStatus(Localizer.T("Status.VAlignCenter"));
    }

    void RunKernel(Action<EditorKernel> action)
    {
        action(_designer.Kernel);
        FocusCanvas();
    }

    void FitToWindow()
    {
        // 宿主 FitToPage() 用真实绘制表面尺寸，比内核 FitToPage() 更准，但它不发 ViewChanged；
        // 状态栏比例读数必须在这里手动刷一次（画布右键菜单也走这条路）。
        _designer.FitToPage();
        UpdateZoomText();
        FocusCanvas();
    }

    void ZoomActual()
    {
        _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        FocusCanvas();
    }

    void FocusCanvas()
    {
        // 菜单/对话框关闭的同一拍里焦点可能还锁在弹层上；失败就延后一拍再还。
        if (!_designer.Focus(FocusState.Programmatic))
            _designer.DispatcherQueue.TryEnqueue(() => _designer.Focus(FocusState.Programmatic));
    }

    void OnPaperChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPaper || _paperCombo.SelectedIndex < 0)
            return;

        var choice = PaperChoices[_paperCombo.SelectedIndex];
        if (choice.Key is null)
        {
            RequestCustomPaperDialog();
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
            _ = ShowMessageAsync(Localizer.T("Paper.Section"), Localizer.T("Error.PaperFailed", ex.Message));
        }

        SyncPaperControls();
        FocusCanvas();
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
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus(Localizer.T("Status.ContinuousCannotRotate"));
            return;
        }

        try
        {
            kernel.RotatePaper();
            SyncPaperControls();
            SetStatus(kernel.Template.Paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Status.PaperLandscape")
                : Localizer.T("Status.PaperPortrait"));
        }
        catch (Exception ex)
        {
            _ = ShowMessageAsync(Localizer.T("Paper.Section"), Localizer.T("Error.RotateFailed", ex.Message));
        }

        FocusCanvas();
    }

    void RequestCustomPaperDialog()
    {
        if (_paperDialogOpen)
            return;

        // 从 MenuFlyout 的 Click 里直接弹 ContentDialog，弹层可能还没关；延后一拍再弹更稳。
        _designer.DispatcherQueue.TryEnqueue(() => _ = ShowCustomPaperDialogAsync());
    }

    async Task ShowCustomPaperDialogAsync()
    {
        if (_paperDialogOpen || _designer.XamlRoot is null)
            return;

        _paperDialogOpen = true;
        try
        {
            var dialog = new PaperSizeDialog(_designer.Kernel.Template.Paper) { XamlRoot = _designer.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
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
                    await ShowMessageAsync(Localizer.T("Paper.Section"), Localizer.T("Error.PaperFailed", ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Paper.Section"), ex.Message);
        }
        finally
        {
            _paperDialogOpen = false;
            SyncPaperControls();
            FocusCanvas();
        }
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
            var target = index >= 0 ? index : PaperChoices.Length - 1;
            if (_paperCombo.SelectedIndex != target)
                _paperCombo.SelectedIndex = target;

            _continuousToggle.IsChecked = paper.IsContinuous;
            _rotatePaperButton.IsEnabled = !paper.IsContinuous;
            _rotatePaperButton.Content = paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Paper.RotatePortrait")
                : Localizer.T("Paper.Rotate");
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

    PointerPoint ToDesignerPointer(Point dipPosition, PointerButton button)
    {
        // WinUI 指针坐标是 DIP，内核要求宿主设备 px：只在这里换算一次（与宿主相同的纪律）。
        var dpi = _designer.Dpi;
        var scale = dpi / BaseDpi;
        return new PointerPoint(dipPosition.X * scale, dipPosition.Y * scale, dpi, button);
    }

    FrameworkElement BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起只留一条标题带，把竖向空间还给画布。
        _jsonToggle.Content = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.FontWeight = FontWeights.SemiBold;
        _jsonToggle.VerticalAlignment = VerticalAlignment.Center;
        _jsonToggle.Margin = new Thickness(0, 0, 10, 0);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(_jsonToggle.IsChecked == true);

        var apply = new Button
        {
            Content = Localizer.T("JsonPanel.Apply"),
            Padding = new Thickness(8, 2, 8, 2),
        };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Content = Localizer.T("JsonPanel.Reload"),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
        };
        reload.Click += (_, _) => RefreshTemplateJson(Localizer.T("Status.JsonReloaded"));

        _jsonState.FontSize = 12;
        _jsonState.Foreground = new SolidColorBrush(Colors.DimGray);
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
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();
        ScrollViewer.SetVerticalScrollBarVisibility(_templateJson, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_templateJson, ScrollBarVisibility.Auto);

        // 收起时内容行高 0：WinUI 没有内置 GridSplitter，展开高度固定 230，不做拖拽调高。
        _jsonContentRow = new RowDefinition { Height = new GridLength(0) };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(_jsonContentRow);
        grid.Children.Add(header);
        Grid.SetRow(_templateJson, 1);
        grid.Children.Add(_templateJson);

        SetJsonPanelExpanded(false);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    /// <summary>展开/收起底部 JSON 面板；箭头方向与勾选态保持一致。</summary>
    void SetJsonPanelExpanded(bool expanded)
    {
        _jsonToggle.IsChecked = expanded;
        _jsonToggle.Content = (expanded ? "▾ " : "▸ ") + Localizer.T("JsonPanel.Title");
        _jsonContentRow.Height = new GridLength(expanded ? 230 : 0);
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
                SetJsonState(Localizer.T("JsonPanel.Synced", _designer.Kernel.Template.Elements.Count), error: false);
            }
            return;
        }

        _jsonDirty = true;
        SetJsonState(Localizer.T("JsonPanel.Dirty"), error: false);
    }

    void RefreshTemplateJson(string? note = null)
    {
        var text = TemplateJson.Serialize(_designer.Kernel.Template);
        _jsonSnapshot = NormalizeJsonText(text);
        _jsonDirty = false;
        _templateJson.Text = text;
        _templateJson.Select(0, 0);
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
                SetStatus(Localizer.T("Status.HiprintImported"));
                FocusCanvas();
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
            FocusCanvas();
        }
        catch (Exception ex)
        {
            SetJsonState(Localizer.T("Status.JsonNotApplied", ex.Message), error: true);
        }
    }

    void SetJsonState(string text, bool error)
    {
        _jsonState.Text = text;
        _jsonState.Foreground = new SolidColorBrush(error ? Colors.Firebrick : Colors.DimGray);
    }

    async Task ImportHiprintAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".js");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".txt");

        // 与导出一样：WinUI 3 桌面应用必须把 picker 挂到窗口上。
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Import.Title"), Localizer.T("Error.HiprintImportFailed", ex.Message));
            return;
        }

        if (file is null)
            return;

        try
        {
            string text;
            using (var stream = await file.OpenStreamForReadAsync())
            using (var reader = new StreamReader(stream))
                text = await reader.ReadToEndAsync();

            ImportHiprint(text, file.Name);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Import.Title"), Localizer.T("Error.HiprintImportFailed", ex.Message));
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

        FocusCanvas();
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

    async void OnImportHiprint(object sender, RoutedEventArgs e) => await ImportHiprintAsync();

    // ===== 阶段 E/F：解决方案持久化 + 导出/打印 =====

    void OnSaveAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = SaveSolution(false);
    }

    void MarkSolutionDirty()
    {
        _session.IsDirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        Title = $"{Localizer.T("App.Title.Short")} — WinUI — {_session.CurrentName}{(_session.IsDirty ? "*" : "")}";
    }

    void NewSolution()
    {
        _designer.LoadTemplate(PrintTemplate.CreateDefault(PaperTypePresets.A4));
        BindKernel("未命名模板");
        _session.New();
        SetStatus(Localizer.T("Status.NewSolution"));
        UpdateTitle();
    }

    async void OpenSolutionDialog()
    {
        var records = _session.List();
        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(TemplateRecord.Name),
            ItemsSource = records,
        };

        var openButton = new Button { Content = Localizer.T("Dialog.Open"), Margin = new Thickness(4) };
        var cancelButton = new Button { Content = Localizer.T("Dialog.Cancel"), Margin = new Thickness(4) };
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(openButton);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(listView, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(listView);
        grid.Children.Add(buttonPanel);

        var dialog = new ContentDialog
        {
            Title = Localizer.T("Dialog.OpenSolution"),
            Content = grid,
            RequestedTheme = ElementTheme.Light,
            XamlRoot = _designer.XamlRoot,
        };

        void OpenSelected()
        {
            if (listView.SelectedItem is TemplateRecord rec)
            {
                dialog.Hide();
                OpenSolution(rec.Id);
            }
        }

        openButton.Click += (_, _) => OpenSelected();
        listView.DoubleTapped += (_, _) => OpenSelected();
        cancelButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
    }

    void OpenSolution(string id)
    {
        var record = _session.Load(id);
        if (record is null)
        {
            _ = ShowMessageAsync(Localizer.T("Error.OpenFailed"), Localizer.T("Error.OpenFailed"));
            return;
        }

        var template = SolutionSession.Parse(record.Json);
        if (template is null)
        {
            _ = ShowMessageAsync(Localizer.T("Error.OpenFailed"), Localizer.T("Error.OpenFailed"));
            return;
        }

        _designer.LoadTemplate(template);
        BindKernel(record.Name);
        _session.OpenRecord(record);
        SetStatus(Localizer.T("Status.Opened", record.Name));
        UpdateTitle();
        RefreshRecentMenu();
    }

    void RefreshRecentMenu(MenuFlyoutSubItem sub)
    {
        sub.Items.Clear();
        foreach (var record in _session.Recent(8))
        {
            var item = new MenuFlyoutItem { Text = record.Name };
            item.Click += (_, _) => OpenSolution(record.Id);
            sub.Items.Add(item);
        }

        if (sub.Items.Count == 0)
            sub.Items.Add(new MenuFlyoutItem { Text = Localizer.T("Menu.File.NoRecent"), IsEnabled = false });
    }

    void RefreshRecentMenu() => RefreshRecentMenu(_recentSub);

    async Task<bool> SaveSolution(bool saveAs, bool silent = false)
    {
        string? name = null;
        if (saveAs || (_session.CurrentId is null && !silent))
        {
            name = await PromptNameAsync(_session.CurrentName ?? "未命名模板");
            if (name is null)
                return false;
        }

        try
        {
            _session.SaveCurrent(_designer.Template, name);
            SetStatus(Localizer.T("Status.Saved", _session.CurrentName));
            ShowToast("保存成功", Localizer.T("Status.Saved", _session.CurrentName));
            RefreshRecentMenu();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Error.SaveFailed"), Localizer.T("Error.SaveFailed", ex.Message));
            return false;
        }

        UpdateTitle();
        return true;
    }

    async Task<string?> PromptNameAsync(string defaultName)
    {
        var textBox = new TextBox { Text = defaultName, Width = 320, Margin = new Thickness(8) };
        var dialog = new ContentDialog
        {
            Title = Localizer.T("Dialog.SaveAs"),
            Content = textBox,
            PrimaryButtonText = Localizer.T("Dialog.OK"),
            CloseButtonText = Localizer.T("Dialog.Cancel"),
            RequestedTheme = ElementTheme.Light,
            XamlRoot = _designer.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var t = textBox.Text.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        return null;
    }

    async void OnExportJson(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "crossprint-template",
        };
        picker.FileTypeChoices.Add(Localizer.T("Dialog.Export.JsonFilter"), new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Export.Title"), Localizer.T("Error.ExportFailed", ex.Message));
            return;
        }

        if (file is null)
            return;

        try
        {
            File.WriteAllText(file.Path, TemplateJson.Serialize(_designer.Template, indented: true));
            SetStatus(Localizer.T("Status.Exported", file.Path));
            ShowToast("已导出模板", Localizer.T("Status.Exported", file.Path));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Export.Title"), Localizer.T("Error.ExportFailed", ex.Message));
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

    async void OnExportPdf(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "crossprint-sample",
        };
        picker.FileTypeChoices.Add(Localizer.T("Dialog.Export.PdfFilter"), new List<string> { ".pdf" });

        // WinUI 3 桌面应用必须显式把 picker 挂到窗口上，否则 PickSaveFileAsync 直接失败。
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Export.Title"), Localizer.T("Error.ExportFailed", ex.Message));
            return;
        }

        if (file is null)
            return;

        try
        {
            using var stream = File.Create(file.Path);
            var report = PdfExporter.Export(
                _designer.Template,
                stream,
                data: SampleData.Create(),
                fontResolver: _designer.FontResolver,
                images: _designer.Images,
                renderer: _designer.Renderer);

            SetStatus(report.HasIssues
                ? Localizer.T("Status.ExportedWithWarnings", file.Path, report.Issues.Count)
                : Localizer.T("Status.Exported", file.Path));
            ShowToast("已导出 PDF", file.Path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Localizer.T("Dialog.Export.Title"), Localizer.T("Error.ExportFailed", ex.Message));
        }
    }

    void OnShowPreview(object sender, RoutedEventArgs e)
    {
        // WinUI 丢弃 Window 引用并不会销毁窗口：不先关掉上一个就会一直堆积，进程也随之无法退出。
        if (_previewWindow is not null)
        {
            _previewWindow.Close();
            _previewWindow = null;
        }

        var preview = new PreviewWindow(_designer.Template, SampleData.Create());
        // 用户手动关闭预览时也清掉引用，避免下次点击对已关闭窗口再调 Close。
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_previewWindow, preview))
                _previewWindow = null;
        };
        _previewWindow = preview;
        preview.Activate();
    }

    async Task ShowMessageAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = Localizer.T("About.Close"),
                RequestedTheme = ElementTheme.Light,
                XamlRoot = _designer.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // 已有其它对话框占用或窗口正在关闭时弹不出来；退回状态栏，不把错误静默丢掉。
            SetStatus(title + "：" + message);
        }
    }

    void SetStatus(string text) => _status.Text = text;

    // 阶段 G：右上角 Toast（成功/提示浮层），3 秒自动消失，不拦截鼠标；同一时刻只保留最新一条。
    void ShowToast(string title, string? message = null)
    {
        _toastLayer.Children.Clear();

        var icon = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 179, 74)),
            Child = new TextBlock
            {
                Text = "✓",
                Foreground = new SolidColorBrush(Colors.White),
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
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0x62, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
            });

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0xE9, 0xF0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, textStack } },
        };
        card.HorizontalAlignment = HorizontalAlignment.Right;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Margin = new Thickness(0, 14, 14, 0);

        _toastLayer.Children.Add(card);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); _toastLayer.Children.Clear(); };
        timer.Start();
    }

    /// <summary>显示关于对话框（WinUI ContentDialog）。</summary>
    async Task ShowAboutDialogAsync()
    {
        if (_designer.XamlRoot is null)
            return;

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
            Foreground = new SolidColorBrush(Colors.DimGray),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var description = new TextBlock
        {
            Text = Localizer.T("About.Description"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 4),
            Foreground = new SolidColorBrush(Colors.DimGray),
        };

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
                Foreground = new SolidColorBrush(Colors.DimGray),
                Margin = new Thickness(0, 1, 0, 1),
            });
        }

        var contentPanel = new StackPanel
        {
            Spacing = 2,
        };

        var logo = await LoadAboutLogoAsync();
        if (logo is not null)
        {
            contentPanel.Children.Add(new Image
            {
                Source = logo,
                Width = 96,
                Height = 96,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        contentPanel.Children.Add(productTitle);
        contentPanel.Children.Add(version);
        contentPanel.Children.Add(description);
        contentPanel.Children.Add(thirdPartyPanel);

        var dialog = new ContentDialog
        {
            Title = Localizer.T("About.Title"),
            Content = contentPanel,
            CloseButtonText = Localizer.T("About.Close"),
            RequestedTheme = ElementTheme.Light,
            XamlRoot = _designer.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 从 I18n 嵌入资源读 logo 并解码成 WinUI 位图；资源缺失或解码失败时返回 null（对话框退化为纯文字）。
    /// </summary>
    static async Task<BitmapImage?> LoadAboutLogoAsync()
    {
        var bytes = LogoLoader.GetLogoBytes();
        if (bytes is null)
            return null;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>模板库对话框：分类列表 + 模板卡片（缩略图即用真实引擎渲染），点击卡片套用模板。</summary>
    async Task OpenTemplateLibraryAsync()
    {
        var entries = BuiltinTemplateCatalog.BuildEntries();
        var byCategory = new Dictionary<string, List<BuiltinTemplateCatalog.Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byCategory.TryGetValue(entry.Category, out var list))
                byCategory[entry.Category] = list = new List<BuiltinTemplateCatalog.Entry>();
            list.Add(entry);
        }

        ContentDialog? dialog = null;

        var categoryList = new ListBox { Width = 150 };
        categoryList.ItemsSource = BuiltinTemplateCatalog.Categories;
        categoryList.SelectedIndex = 0;

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(8) };
        var scroll = new ScrollViewer { Content = cards };

        void ApplyFromLibrary(BuiltinTemplateCatalog.Entry entry)
        {
            _designer.LoadTemplate(entry.Create());
            BindKernel(entry.Name);
            SetStatus(Localizer.T("Status.TemplateApplied", entry.Name));
        }

        async Task<FrameworkElement> BuildCard(BuiltinTemplateCatalog.Entry entry)
        {
            var border = new Border
            {
                Width = 220,
                Height = 150,
                Margin = new Thickness(6),
                BorderBrush = new SolidColorBrush(Colors.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Colors.White),
            };
            var stack = new StackPanel();
            var title = new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var thumb = new Image { Width = 200, Height = 96, Stretch = Stretch.Uniform };
            var desc = new TextBlock { Text = entry.Description, FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            stack.Children.Add(title);
            stack.Children.Add(thumb);
            stack.Children.Add(desc);
            border.Child = stack;

            var bmp = await PngToBitmapImage(BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create()));
            if (bmp is not null)
                thumb.Source = bmp;

            border.Tapped += (_, _) => { dialog!.Hide(); ApplyFromLibrary(entry); };
            return border;
        }

        async Task ShowCards(string category)
        {
            cards.Children.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            foreach (var entry in list)
                cards.Children.Add(await BuildCard(entry));
        }

        categoryList.SelectionChanged += async (_, _) =>
        {
            if (categoryList.SelectedItem is string category)
                await ShowCards(category);
        };
        await ShowCards("全部");

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(categoryList, 0);
        Grid.SetColumn(scroll, 1);
        body.Children.Add(categoryList);
        body.Children.Add(scroll);

        var hint = new TextBlock { Text = Localizer.T("TemplateLibrary.Hint"), Margin = new Thickness(4), Foreground = new SolidColorBrush(Colors.Gray) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(hint, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(hint);
        root.Children.Add(body);

        dialog = new ContentDialog
        {
            Title = Localizer.T("TemplateLibrary.Title"),
            Content = root,
            CloseButtonText = Localizer.T("About.Close"),
            RequestedTheme = ElementTheme.Light,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>把共享层渲染出的 PNG 字节转成 WinUI 位图（沿用本宿主的 InMemoryRandomAccessStream 写法）。</summary>
    static async Task<BitmapImage?> PngToBitmapImage(byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
