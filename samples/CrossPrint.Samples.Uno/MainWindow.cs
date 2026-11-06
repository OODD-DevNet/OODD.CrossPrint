using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Uno;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using Microsoft.UI;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace CrossPrint.Samples.Uno;

/// <summary>示例主窗口：顶部菜单栏 + 紧凑工具栏 + 左侧（纸张栏 + 工具箱） + 中间设计画布 + 右侧属性面板 + 可折叠模板 JSON 带（默认收起）/状态栏，预览以整窗覆盖层呈现。</summary>
/// <remarks>
/// 与 WinForms 示例同构，但用 WinUI/Uno 的控件与事件重写：
/// 没有 ToolBar/DockPanel，工具栏是横向 <see cref="StackPanel"/>，布局全部用 <see cref="Grid"/>。
/// 预览不另开窗口（Uno Skia 桌面头的多窗口支持因平台而异），改为覆盖在主界面之上。
/// 画布右键菜单用 <see cref="MenuFlyout"/>，在 <see cref="UIElement.PointerPressed"/> 里
/// 按右键位置 <see cref="EditorKernel.SelectAt"/> 后 <see cref="MenuFlyout.ShowAt(FrameworkElement, FlyoutShowOptions)"/>；
/// 不再另外挂 <see cref="UIElement.ContextFlyout"/>，否则会弹两次。
/// </remarks>
internal sealed class MainWindow : Window
{
    // 与 WPF 基准一致：初始 1320×880，最小 1024×640（Uno 的 OverlappedPresenter 没有最小尺寸 API，用 Changed 事件兜底）。
    const double InitialWindowWidth = 1320;
    const double InitialWindowHeight = 880;
    const double MinWindowWidth = 1024;
    const double MinWindowHeight = 640;

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

    readonly DesignerControl _designer;

    // 阶段 E：解决方案持久化（共享存储 + 会话）。模板落库到 AppData/CrossPrint/templates.db（SQLite）。
    readonly SolutionSession _session = new();
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    MenuFlyoutSubItem _recentSub = null!;
    readonly PaperPanel _paper;
    readonly PropertyPanel _properties;
    readonly ListView _toolbox = new();
    readonly TextBlock _status = new();
    readonly TextBlock _zoomText = new();
    Button _undoButton = null!;
    Button _redoButton = null!;
    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    // JSON 面板的折叠开关与内容行：它是低频的调试/导入入口，默认收起只留一条标题带。
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;
    readonly Grid _previewOverlay = new();
    readonly Grid _previewHost = new();
    // 阶段 G：右上角 Toast 浮层（顶层 Grid，不拦截鼠标）。
    readonly Grid _toastLayer = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    PreviewControl? _preview;
    bool _controlPressed;
    bool _shiftPressed;
    // 右栏空白区（页面级水印）：持久实例，见 PropertyPanel.EmptyStateContentBuilder 挂接处注释。
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
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;

    public MainWindow()
    {
        Title = Localizer.T("App.Title.Short") + " — Uno";

        // 窗口尺寸与 WPF 基准一致（AppWindow 尺寸含边框，单位是宿主像素；Uno 的 SizeInt32 无双参构造）。
        AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = (int)InitialWindowWidth, Height = (int)InitialWindowHeight });
        AppWindow.Changed += OnAppWindowChanged;

        var template = LoadSampleTemplate(out var warnings);
        _designer = new DesignerControl(template)
        {
            // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
            // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
            Data = SampleData.Create(),
        };

        _paper = new PaperPanel(_designer.Kernel, () => _designer.Focus(FocusState.Programmatic));
        _paper.StatusRequested += message => SetStatus(message);

        _properties = new PropertyPanel(_designer.Kernel);
        // 空白区必须返回懒构建的持久实例：属性面板随 ViewChanged/TemplateChanged 频繁
        // Rebuild（先 Clear 摘树再重挂），每次新建控件树会让字段级单例控件重复进视觉树。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();

        // 右键菜单由示例自己开：宿主对右键不捕获指针（见 DesignerControl 的说明），
        // 这里用指针坐标命中元素后再 ShowAt，菜单项才有作用对象。
        _designer.PointerPressed += OnCanvasPointerPressed;
        HookDuplicateShortcut();

        // 示例配色全部按浅色主题硬编码（0xF5F5F5 底 + 深灰字），与 WinUI 宿主一样显式锁 Light：
        // 否则 Uno 落到系统深色主题时，按钮默认白字会落在浅灰底上整排不可见（2026-11-06 真机筛查实锤）。
        var root = new Grid { RequestedTheme = ElementTheme.Light };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddAt(root, BuildMenuBar(), row: 0);
        AddAt(root, BuildElementBar(), row: 1);
        AddAt(root, BuildEditBar(), row: 2);
        AddAt(root, BuildBody(), row: 3);
        AddAt(root, BuildTemplateJsonPanel(), row: 4);
        AddAt(root, BuildStatusBar(), row: 5);
        AddAt(root, BuildPreviewOverlay(), row: 0, rowSpan: 6);
        // 阶段 G：右上角 Toast 浮层——覆盖整窗、不拦截鼠标（在预览浮层之上）。
        AddAt(root, _toastLayer, row: 0, rowSpan: 6);

        Content = root;
        BindKernel();

        // 阶段 E：桌面自动保存——30s 一拍，有脏改动且已入解决方案才落库；未保存的新模板不打扰（走 Ctrl+S 另存为）。
        _autosaveTimer.Tick += (_, _) => { if (_session.IsDirty) _ = SaveSolution(false, silent: true); };
        _autosaveTimer.Start();

        if (warnings.Count > 0)
            SetStatus(Localizer.T("App.Status.Warnings", string.Join("；", warnings)), error: true);
        else
            SetStatus(Localizer.T("App.Status.Ready"));

        root.Loaded += (_, _) => _designer.Focus(FocusState.Programmatic);
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
            warnings = new[] { Localizer.T("Error.LoadFailed", "kitchen-sink.template.json: " + ex.Message) };
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>窗口小于最小尺寸时撑回去（Uno 桌面头没有 presenter 级最小尺寸 API）。</summary>
    void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
            return;

        // Uno 的 AppWindowChangedEventArgs 不带 Size，从 sender 上读当前尺寸。
        var width = Math.Max(sender.Size.Width, (int)MinWindowWidth);
        var height = Math.Max(sender.Size.Height, (int)MinWindowHeight);
        if (width != sender.Size.Width || height != sender.Size.Height)
            sender.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、加载复杂测试模板）。</summary>
    UIElement BuildMenuBar()
    {
        var fileMenu = new MenuBarItem { Title = Localizer.T("Menu.File") };
        var importItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => ShowImportHiprint();
        fileMenu.Items.Add(importItem);

        var templatesItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.Templates") };
        ToolTipService.SetToolTip(templatesItem, Localizer.T("Menu.File.TemplatesTip"));
        templatesItem.Click += (_, _) => _ = OpenTemplateLibraryAsync();
        fileMenu.Items.Add(templatesItem);

        var stylesItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.StyleLibrary") };
        ToolTipService.SetToolTip(stylesItem, Localizer.T("Menu.File.StyleLibraryTip"));
        stylesItem.Click += (_, _) => _ = StyleLibraryDialog.ShowAsync(_designer.XamlRoot, _designer.Kernel);
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
        exportPdfItem.Click += (_, _) => ExportPdf();
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
                SetStatus(Localizer.T("Language.SaveFailed"), error: true);
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
            XamlRoot = _designer.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    UIElement BuildElementBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 2) };

        // 元素按钮文字本就是两三个字的短标签，完整说明放 ToolTip，窄窗口时省横向空间。
        foreach (var (label, key) in ElementButtons)
        {
            var typeKey = key;
            panel.Children.Add(MakeButton(label, () => Run(() => _designer.Kernel.AddElementAtCenter(typeKey)), Localizer.T("Toolbar.AddElement.Tooltip", label)));
        }

        return new Border
        {
            Background = new SolidColorBrush(Color(0xF5, 0xF5, 0xF5)),
            Padding = new Thickness(0),
            Child = panel,
        };
    }

    UIElement BuildEditBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 6) };

        // 标签尽量短（完整说明在 ToolTip 里）：这一行项目最多，窄窗口时要省横向空间；
        // 功能组之间用分隔线区隔。
        // 导入按钮放最前面：窗口变窄时末尾的按钮会先被裁掉。
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Import"), ShowImportHiprint, Localizer.T("Toolbar.Import.Tip")));
        panel.Children.Add(MakeSeparator());
        _undoButton = MakeButton(Localizer.T("Toolbar.Undo"), DoUndo, Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = MakeButton(Localizer.T("Toolbar.Redo"), DoRedo, Localizer.T("Toolbar.Redo.Tip"));
        panel.Children.Add(_undoButton);
        panel.Children.Add(_redoButton);
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Delete"), () => Run(_designer.Kernel.DeleteSelection), Localizer.T("Toolbar.Delete.Tip")));
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单与预览覆盖层仍保留同名项。
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeOverlayCheck(Localizer.T("Toolbar.Grid"), true, value => _designer.Overlay.Options.ShowGrid = value));
        panel.Children.Add(MakeOverlayCheck(Localizer.T("Toolbar.Ruler"), true, value => _designer.Overlay.Options.ShowRulers = value));
        panel.Children.Add(MakeOverlayCheck(Localizer.T("Toolbar.SafeArea"), true, value => _designer.Overlay.Options.ShowMarginGuides = value));
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.ExportPdf"), ExportPdf, Localizer.T("Toolbar.ExportPdf.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.Preview.Tip")));

        return new Border
        {
            Background = new SolidColorBrush(Color(0xF5, 0xF5, 0xF5)),
            Padding = new Thickness(0),
            Child = panel,
        };
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

        // WinUI 的 CheckBox 在点击后同步更新 IsChecked，再让画布重绘即可。
        check.Click += (_, _) =>
        {
            apply(check.IsChecked == true);
            _designer.Invalidate();
            _designer.Focus(FocusState.Programmatic);
        };

        return check;
    }

    UIElement BuildBody()
    {
        // 三列布局与 WPF 基准一致：左 200（纸张栏 + 工具箱）、中画布、右 310（属性面板）。
        // Uno 没有内置 GridSplitter，示例也未引用 CommunityToolkit.WinUI.Controls.Sizers
        //（为示例单独引入工具包依赖不划算），所以两侧栏保持固定宽度并按统一方案收窄，
        // 拖动调宽留待将来引入 Sizers 后再补。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });

        var designerHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6),
            Child = _designer,
        };

        // 左栏顶部是纸张栏，下面是工具箱；两行分别是 Auto / Star。
        var paperHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _paper,
        };

        var leftSidebar = new Grid();
        leftSidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftSidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbox = BuildToolbox();
        Grid.SetRow(paperHost, 0);
        Grid.SetRow(toolbox, 1);
        leftSidebar.Children.Add(paperHost);
        leftSidebar.Children.Add(toolbox);

        var leftHost = new Border
        {
            Background = new SolidColorBrush(Color(0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = leftSidebar,
        };

        var propertyHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = _properties,
        };

        Grid.SetColumn(leftHost, 0);
        Grid.SetColumn(designerHost, 1);
        Grid.SetColumn(propertyHost, 2);
        grid.Children.Add(leftHost);
        grid.Children.Add(designerHost);
        grid.Children.Add(propertyHost);
        return grid;
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
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var search = new TextBox { PlaceholderText = Localizer.T("Toolbox.Search") };
        search.TextChanged += (_, _) => ApplyToolboxFilter(search.Text);
        Grid.SetRow(search, 1);
        grid.Children.Add(search);

        ApplyToolboxFilter(string.Empty);
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
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 3);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = Localizer.T("Status.DefaultTip"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(hint, 4);
        grid.Children.Add(hint);

        return grid;
    }

    /// <summary>
    /// 工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。
    /// 条目用记录类型（SelectedIndex 不再等于全量下标），双击/回车/按钮加到画布中心。
    /// 拖放：Uno 的跨平台拖放支持随目标平台而异（Windows 兼容层之外的 iOS/Android/WASM 各有一套），
    /// 无法无头实证，按待办口径以「点选 + 添加到画布」兜底。
    /// </summary>
    void ApplyToolboxFilter(string? query)
        => _toolbox.ItemsSource = ElementButtons
            .Select(item => new ToolboxItem(item.Label, item.Key))
            .Where(item => ToolboxNames.Matches(item.TypeKey, query))
            .ToArray();

    sealed record ToolboxItem(string Label, string TypeKey);

    void AddFromToolbox()
    {
        if (_toolbox.SelectedItem is not ToolboxItem item)
        {
            SetStatus(Localizer.T("Status.SelectFirst"), error: true);
            return;
        }

        Run(() => _designer.Kernel.AddElementAtCenter(item.TypeKey));
        SetStatus(Localizer.T("Status.ElementAddedCenter", item.Label));
    }

    UIElement BuildStatusBar()
    {
        _status.Foreground = new SolidColorBrush(Colors.DimGray);
        _status.Margin = new Thickness(10, 3, 10, 3);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _zoomText.Foreground = new SolidColorBrush(Colors.DimGray);
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Margin = new Thickness(8, 3, 4, 3);

        var zoomBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip")));
        zoomBox.Children.Add(ZoomButton(Localizer.T("Toolbar.FitToPage"), FitToPageFromStatus, Localizer.T("Toolbar.FitToPage.Tip")));

        // 三列 Grid：缩放组/比例固定左侧，状态文本吃掉剩余宽度才能正确省略号截断。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(zoomBox, 0);
        Grid.SetColumn(_zoomText, 1);
        Grid.SetColumn(_status, 2);
        grid.Children.Add(zoomBox);
        grid.Children.Add(_zoomText);
        grid.Children.Add(_status);

        return new Border
        {
            Background = new SolidColorBrush(Color(0xF0, 0xF0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    /// <summary>状态栏缩放按钮：显式清零默认 Padding/MinHeight，小按钮里字符才不会被挤没。</summary>
    static Button ZoomButton(string text, Action action, string tip)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 26,
            MinHeight = 0,
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => action();
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    /// <summary>「适合窗口」用宿主真实绘制表面尺寸；它不发 ViewChanged，读数要手动刷一次。</summary>
    void FitToPageFromStatus()
    {
        _designer.FitToPage();
        UpdateZoomText();
        _designer.Focus(FocusState.Programmatic);
    }

    void DoUndo()
    {
        if (!EditorActions.Undo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToUndo"));
            return;
        }

        _designer.Focus(FocusState.Programmatic);
        SetStatus(Localizer.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!EditorActions.Redo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToRedo"));
            return;
        }

        _designer.Focus(FocusState.Programmatic);
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

    UIElement BuildPreviewOverlay()
    {
        _previewOverlay.Background = new SolidColorBrush(Color(0xB4, 0xB4, 0xB4));
        _previewOverlay.Visibility = Visibility.Collapsed;
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Background = new SolidColorBrush(Colors.White) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new TextBlock
        {
            Text = Localizer.T("Dialog.Preview.Title"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DimGray),
            Margin = new Thickness(12, 6, 8, 6),
            TextWrapping = TextWrapping.NoWrap,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 8, 4) };
        buttons.Children.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), () => _preview?.FitToPage()));
        buttons.Children.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), () =>
        {
            _preview?.Kernel.Viewport.Reset();
            _preview?.Invalidate();
        }));
        buttons.Children.Add(MakeButton(Localizer.T("About.Close"), ClosePreview));

        Grid.SetColumn(info, 0);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(info);
        bar.Children.Add(buttons);

        Grid.SetRow(bar, 0);
        Grid.SetRow(_previewHost, 1);
        _previewOverlay.Children.Add(bar);
        _previewOverlay.Children.Add(_previewHost);
        return _previewOverlay;
    }

    void Run(Action action)
    {
        action();
        _designer.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Ctrl+D / Ctrl+Z / Ctrl+Y 只订阅在画布自己的 <see cref="UIElement.KeyDown"/> 上：只有画布（或其子树）
    /// 有焦点时事件才会到达，属性面板的文本框按键不会被抢。Uno 的 <see cref="KeyRoutedEventArgs"/>
    /// 不带修饰键：自己跟踪 Control / Shift 的按下松开，失焦时复位。
    /// </summary>
    void HookDuplicateShortcut()
    {
        _designer.KeyDown += OnDesignerKeyDown;
        _designer.KeyUp += OnDesignerKeyUp;
        _designer.LostFocus += (_, _) =>
        {
            _controlPressed = false;
            _shiftPressed = false;
        };
    }

    void OnDesignerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
        {
            _controlPressed = true;
            return;
        }

        if (e.Key == VirtualKey.Shift)
        {
            _shiftPressed = true;
            return;
        }

        // Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y：撤销/重做（与 Avalonia 基准一致）。
        if (_controlPressed && (e.Key == VirtualKey.Z || e.Key == VirtualKey.Y))
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Y || _shiftPressed)
                DoRedo();
            else
                DoUndo();
            return;
        }

        // 阶段 E：Ctrl+S 立即保存。
        if (_controlPressed && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            _ = SaveSolution(false);
            return;
        }

        if (e.Key != VirtualKey.D || !_controlPressed)
            return;

        e.Handled = true;
        DuplicateSelection();
    }

    void OnDesignerKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
            _controlPressed = false;
        else if (e.Key == VirtualKey.Shift)
            _shiftPressed = false;
    }

    void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_designer);
        if (!point.Properties.IsRightButtonPressed)
            return;

        var position = point.Position;

        // 命中：右键元素即单选它、右键空白清空选中，菜单项可用性据此计算。
        // Uno 的指针坐标是 DIP，内核要宿主设备 px，按宿主同一口径换算一次。
        var dpi = _designer.Dpi;
        _designer.Kernel.SelectAt(new PointerPoint(
            position.X * dpi / 96.0,
            position.Y * dpi / 96.0,
            dpi,
            PointerButton.Secondary));

        ShowCanvasMenu(position);
        e.Handled = true;
    }

    /// <summary>在右键位置弹出画布菜单；菜单项全部直连内核命令，没有选中时选中类项禁用。</summary>
    /// <param name="position">相对画布左上角的 DIP 坐标。</param>
    void ShowCanvasMenu(Point position)
    {
        var kernel = _designer.Kernel;
        var paper = kernel.Template.Paper;
        var hasSelection = !kernel.Selection.IsEmpty;

        var menu = new MenuFlyout();

        void Add(string text, string? accelerator, bool enabled, Action action)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = enabled,
            };

            if (accelerator is not null)
                item.KeyboardAcceleratorTextOverride = accelerator;

            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add(Localizer.T("Canvas.Context.Duplicate"), "Ctrl+D", hasSelection, DuplicateSelection);
        Add(Localizer.T("Canvas.Context.Delete"), "Delete", hasSelection, () => Run(_designer.Kernel.DeleteSelection));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Localizer.T("Canvas.Context.BringForward"), null, hasSelection, () => Run(_designer.Kernel.BringForward));
        Add(Localizer.T("Canvas.Context.SendBackward"), null, hasSelection, () => Run(_designer.Kernel.SendBackward));
        Add(Localizer.T("Canvas.Context.BringToFront"), null, hasSelection, () => Run(_designer.Kernel.BringToFront));
        Add(Localizer.T("Canvas.Context.SendToBack"), null, hasSelection, () => Run(_designer.Kernel.SendToBack));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Localizer.T("Canvas.Context.AlignCenter"), null, hasSelection, () =>
        {
            Run(_designer.Kernel.CenterSelectionHorizontally);
            SetStatus(Localizer.T("Status.HAlignCenter"));
        });
        Add(Localizer.T("Canvas.Context.AlignMiddle"), null, hasSelection && !paper.IsContinuous, () =>
        {
            Run(_designer.Kernel.CenterSelectionVertically);
            SetStatus(Localizer.T("Status.VAlignCenter"));
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Localizer.T("Toolbar.FitToPage"), null, true, () => Run(_designer.FitToPage));
        Add(Localizer.T("Canvas.Context.ActualSize"), null, true, () => Run(() =>
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual))));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(
            paper.Orientation == PaperOrientation.Landscape ? Localizer.T("Canvas.Context.RotatePortrait") : Localizer.T("Canvas.Context.RotateLandscape"),
            null,
            !paper.IsContinuous,
            TogglePaperOrientation);
        Add(Localizer.T("Canvas.Context.CustomPaper"), null, true, ShowCustomPaper);

        menu.ShowAt(_designer, new FlyoutShowOptions { Position = position });
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"));
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.Focus(FocusState.Programmatic);
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void TogglePaperOrientation() => _paper.ToggleOrientation();

    async void ShowCustomPaper()
    {
        await _paper.ShowCustomPaperDialogAsync();
        _designer.Focus(FocusState.Programmatic);
    }

    UIElement BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起只留一条标题带，把竖向空间还给画布。
        _jsonToggle.Content = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
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

        // 收起时内容行高 0：Uno 没有内置 GridSplitter，展开高度固定 230，不做拖拽调高。
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

    /// <summary>把纸张栏/属性面板/JSON 面板全部接到当前内核上；LoadTemplate 换内核后必须重调。</summary>
    void BindKernel(string? jsonNote = null)
    {
        _paper.Attach(_designer.Kernel);
        _properties.Attach(_designer.Kernel);
        _designer.Kernel.TemplateChanged += OnKernelTemplateChanged;
        // 阶段 E：任何编辑都标记脏位（LoadTemplate 已换内核，这里重绑到新内核）。
        _designer.Kernel.TemplateChanged += MarkSolutionDirty;
        // 提交、撤销、重做、换模板都会发 TemplateChanged：水印控件统一从这里回读模型值。
        _designer.Kernel.TemplateChanged += RefreshWatermarkControls;
        // LoadTemplate 会换内核：撤销态与缩放读数的订阅必须每次重绑（与 Avalonia 基准一致）。
        _designer.Kernel.UndoStateChanged += UpdateUndoButtons;
        _designer.Kernel.ViewChanged += UpdateZoomText;
        RefreshWatermarkControls();
        UpdateUndoButtons();
        UpdateZoomText();
        RefreshTemplateJson(jsonNote);
    }

    /// <summary>右栏空白区持久实例：属性面板每次 Rebuild 复用同一棵控件树。</summary>
    FrameworkElement BuildPageSection()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(BuildWatermarkSection());
        return stack;
    }

    /// <summary>
    /// 水印设置区：启用/文字/字段绑定/字号/透明度/角度/间距/颜色。
    /// 校验、夹取、可撤销提交、回填统一走共享层 <see cref="WatermarkEdits"/>。
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
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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

    /// <summary>水印输入框：失焦或回车提交；回填期间的事件由重入守卫挡掉。</summary>
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
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
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
                _designer.Focus(FocusState.Programmatic);
                return;
            }

            var load = TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant);
            var loadNote = load.Warnings.Count == 0
                ? Localizer.T("Status.JsonApplied", load.Template.Elements.Count)
                : Localizer.T("Status.JsonAppliedWithWarnings", load.Template.Elements.Count, string.Join("；", load.Warnings));
            _designer.LoadTemplate(load.Template);
            BindKernel(loadNote);
            SetJsonState(loadNote, error: load.Warnings.Count > 0);
            _designer.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            SetJsonState(Localizer.T("Error.HiprintImportFailed", ex.Message), error: true);
        }
    }

    void SetJsonState(string text, bool error)
    {
        _jsonState.Text = text;
        _jsonState.Foreground = new SolidColorBrush(error ? Colors.Firebrick : Colors.DimGray);
    }

    async void ShowImportHiprint()
    {
        var xamlRoot = _designer.XamlRoot;
        if (xamlRoot is null)
        {
            SetStatus(Localizer.T("Error.NoFileAccess"), error: true);
            return;
        }

        // Uno Skia 桌面头的文件选择器依赖平台实现（导出 PDF 也因此改为直接写"文档"目录），
        // 导入改用路径输入：默认指向随输出复制的 hiprint 示例，点"导入"即可看到结果。
        var pathBox = new TextBox
        {
            Header = Localizer.T("Dialog.Import.Title"),
            Text = Path.Combine(AppContext.BaseDirectory, "hiprint-sample.js"),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Localizer.T("Dialog.Import.Title"),
            PrimaryButtonText = Localizer.T("Menu.File.ImportHiprint").Replace("…", ""),
            CloseButtonText = Localizer.T("Dialog.CustomPaper.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = pathBox,
        };

        // 读文件与解析都在"导入"里完成；失败就把对话框留在原地显示原因。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var path = (pathBox.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                args.Cancel = true;
                return;
            }

            try
            {
                HiprintTemplateImporter.Import(File.ReadAllText(path));
            }
            catch (Exception)
            {
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        ApplyHiprint(
            HiprintTemplateImporter.Import(File.ReadAllText((pathBox.Text ?? string.Empty).Trim())),
            Path.GetFileName((pathBox.Text ?? string.Empty).Trim()));
    }

    void ApplyHiprint(HiprintImportResult result, string sourceName)
    {
        var note = DescribeImport(sourceName, result);

        // LoadTemplate 会换内核：纸张栏、属性面板和 JSON 面板都要换到新内核上。
        // 导入摘要交给 BindKernel，否则会被"已同步当前模板"覆盖，用户看不到跳过/降级原因。
        _designer.LoadTemplate(result.Template);
        BindKernel(note);

        _designer.Focus(FocusState.Programmatic);
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
        Title = $"{Localizer.T("App.Title.Short")} — Uno — {_session.CurrentName}{(_session.IsDirty ? "*" : "")}";
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
        RefreshRecentMenu(_recentSub);
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

    async Task<string?> PromptNameAsync(string defaultName)
    {
        var textBox = new TextBox { Text = defaultName, Width = 320, Margin = new Thickness(8) };
        var dialog = new ContentDialog
        {
            Title = Localizer.T("Dialog.SaveAs"),
            Content = textBox,
            PrimaryButtonText = Localizer.T("Dialog.OK"),
            CloseButtonText = Localizer.T("Dialog.Cancel"),
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
        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
            // 某些 Uno 平台无需 HWND 初始化，忽略。
        }

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
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
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
        }
    }

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

    async void ExportPdf()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "crossprint-uno-sample",
        };
        picker.FileTypeChoices.Add(Localizer.T("Dialog.Export.PdfFilter"), new List<string> { ".pdf" });
        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
            // 某些 Uno 平台无需 HWND 初始化，忽略。
        }

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
            return;
        }

        if (file is null)
            return;

        try
        {
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            PdfExporter.Export(_designer.Template, stream, data: SampleData.Create(), fontResolver: _designer.FontResolver, images: _designer.Images, renderer: _designer.Renderer);
            SetStatus(Localizer.T("Status.Exported", file.Path));
            ShowToast("已导出 PDF", file.Path);
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
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

        _previewHost.Children.Clear();
        _previewHost.Children.Add(preview);
        _preview = preview;
        _previewOverlay.Visibility = Visibility.Visible;
    }

    void ClosePreview()
    {
        _previewOverlay.Visibility = Visibility.Collapsed;
        _previewHost.Children.Clear();
        _preview = null;
        _designer.Focus(FocusState.Programmatic);
    }

    void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = new SolidColorBrush(error ? Colors.Firebrick : Colors.DimGray);
    }

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
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var textStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        textStack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
            Padding = new Thickness(14, 11),
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

    static Button MakeButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(1, 0, 1, 0),
        };

        button.Click += (_, _) => action();
        // 按钮文字用短标签，完整说明放 ToolTip，工具栏横向更省。
        if (tooltip is not null)
            ToolTipService.SetToolTip(button, tooltip);

        return button;
    }

    static UIElement MakeSeparator() => new Border
    {
        Width = 1,
        Background = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
        Margin = new Thickness(5, 3, 5, 3),
    };

    async Task ShowAboutDialogAsync()
    {
        if (_designer.XamlRoot is null)
            return;

        var productTitle = new TextBlock
        {
            Text = AboutInfo.ProductName,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
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
            Margin = new Thickness(16, 4),
            Foreground = new SolidColorBrush(Colors.DimGray),
        };

        var thirdPartyPanel = new StackPanel { Margin = new Thickness(16, 8) };
        thirdPartyPanel.Children.Add(new TextBlock
        {
            Text = Localizer.T("About.ThirdParty"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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

        var contentPanel = new StackPanel();

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
            XamlRoot = _designer.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 从 I18n 嵌入资源读 logo 并解码成位图；资源缺失或解码失败时返回 null（对话框退化为纯文字）。
    /// </summary>
    static async Task<BitmapImage?> LoadAboutLogoAsync()
    {
        var bytes = LogoLoader.GetLogoBytes();
        if (bytes is null)
            return null;

        try
        {
            // Uno 的 DataWriter.DetachStream 未实现，这里直接写内存流；
            // ⚠ 包装流不能 using（Dispose 会关闭底层流，Seek(0) 抛 ObjectDisposedException），
            //   与 PngToBitmapImage 同款修复（2026-11-06）。
            using var stream = new InMemoryRandomAccessStream();
            var output = stream.AsStreamForWrite();
            await output.WriteAsync(bytes, 0, bytes.Length);
            await output.FlushAsync();

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

    static void AddAt(Grid grid, UIElement element, int row, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        if (rowSpan > 1)
            Grid.SetRowSpan(element, rowSpan);
        grid.Children.Add(element);
    }

    static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(0xFF, r, g, b);

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
            var title = new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var thumb = new Image { Width = 200, Height = 96, Stretch = Stretch.Uniform };
            var desc = new TextBlock { Text = entry.Description, FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            stack.Children.Add(title);
            stack.Children.Add(thumb);
            stack.Children.Add(desc);
            border.Child = stack;

            var bmp = await PngToImageSource(BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create()));
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
            XamlRoot = _designer.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>PNG 字节 → 图片源。经临时文件走文件 URI 解码。</summary>
    /// <remarks>
    /// Uno Skia 桌面头对内存流图片解码有两条已知问题（2026-11-06 诊断日志实锤，
    /// 模板库缩略图曾因此整列空白）：
    /// ① <c>DataWriter.DetachStream</c> 未实现，抛异常；
    /// ② <c>AsStreamForWrite</c> 的包装流 Dispose 会关闭底层流，且
    /// <c>BitmapImage.SetSourceAsync</c>（InMemoryRandomAccessStream 路径）在
    /// Uno 内部 <c>BitmapSource.UpdatePixelWidthAndHeightPartial</c> 处 NRE。
    /// 文件 URI 走的是另一条解码路径，可用；缩略图落 %TEMP%\crossprint-uno-thumbs。
    /// </remarks>
    static async Task<ImageSource?> PngToImageSource(byte[] png)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "crossprint-uno-thumbs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "img-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, png);
            var bitmap = new BitmapImage(new Uri(path));
            await Task.CompletedTask;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
