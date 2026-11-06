using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Maui;
using CrossPrint.I18n;
using CrossPrint.Printing;
using CrossPrint.SolutionStorage;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace CrossPrint.Samples.Maui;

/// <summary>示例主窗口：顶部菜单行 + 工具栏 + 左侧（纸张栏 + 工具箱） + 中间设计画布 + 右侧属性面板 + 可折叠的模板 JSON 带 + 状态栏，预览/模态以整窗覆盖层呈现。</summary>
/// <remarks>
/// 与 Uno 示例同构，但按 MAUI 8 的能力做三处替换：
/// <list type="bullet">
/// <item>net8 没有 MenuBar——"文件"菜单用页内下拉卡片模拟（「文件 ▾」按钮 + 覆盖层，
/// 与画布右键菜单同一套路），导入 hiprint 与加载复杂测试模板都在里面；</item>
/// <item>没有 ContentDialog——导入/自定义纸张用页内 <see cref="ModalHost"/> 覆盖层；</item>
/// <item>没有 MenuFlyout——画布右键菜单用页内覆盖层卡片（Margin 定位、点背景关闭），
/// 位置来自宿主的 <see cref="DesignerControl.CanvasRightPressed"/>（带窗口根 DIP 坐标）。</item>
/// </list>
/// </remarks>
internal sealed class MainWindow : Window
{
    // 与 WPF 基准一致：初始 1320×880（挂处理器后经 WinUI 的 AppWindow 设置）、最小 1024×640。
    const int InitialWindowWidth = 1320;
    const int InitialWindowHeight = 880;

    // 底部模板 JSON 面板展开时的定高（折叠时只剩标题带一行）；与 WPF 基准同为 230。
    const double JsonPanelExpandedHeight = 230;

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
    readonly PaperPanel _paper;
    readonly PropertyPanel _properties;
    readonly ModalHost _modal = new();
    readonly CollectionView _toolbox = new();
    readonly Label _status = new();
    readonly Label _zoomText = new();
    Button _undoButton = null!;
    Button _redoButton = null!;
    readonly Editor _templateJson = new();
    readonly Label _jsonState = new();
    readonly Button _jsonToggle = new();
    readonly Grid _jsonPanel = new();
    readonly Grid _previewOverlay = new();
    readonly Grid _previewHost = new();
    readonly Grid _menuOverlay = new();
    // 阶段 G：右上角 Toast 浮层（顶层 Grid，不拦截鼠标）。
    readonly Grid _toastLayer = new() { IsVisible = true, InputTransparent = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
    // 阶段 E：当前解决方案会话（保存/打开/最近/脏位），所有桌面宿主共用 SQLite 存储。
    readonly SolutionSession _session = new();
    bool _printing;
    PreviewControl? _preview;
    // 右栏空白区（页面级水印）：持久实例，见 PropertyPanel.EmptyStateContentBuilder 挂接处注释。
    View? _pageSection;
    bool _syncingWatermark;
    CheckBox _watermarkEnabled = null!;
    Entry _watermarkText = null!;
    Entry _watermarkBinding = null!;
    Entry _watermarkFontSize = null!;
    Entry _watermarkOpacity = null!;
    Entry _watermarkRotation = null!;
    Entry _watermarkSpacing = null!;
    Entry _watermarkColor = null!;
    bool _jsonDirty;
    bool _jsonExpanded;
    string _jsonSnapshot = string.Empty;

    public MainWindow()
    {
        Title = Localizer.T("App.Title.Short") + " — MAUI";

        // 窗口尺寸与 WPF 基准一致：最小值用 MAUI 的跨平台属性，初始尺寸要等处理器挂上
        // 再借 WinUI 的 AppWindow 设一次（MAUI 没有跨平台的初始窗口尺寸 API）。
        MinimumWidth = 1024;
        MinimumHeight = 640;
        HandlerChanged += OnWindowHandlerChanged;

        var template = LoadSampleTemplate(out var warnings);
        _designer = new DesignerControl(template)
        {
            // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
            // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
            Data = SampleData.Create(),
        };

        _paper = new PaperPanel(_designer.Kernel, _modal, () => _designer.TakeFocus());
        _paper.StatusRequested += message => SetStatus(message);

        _properties = new PropertyPanel(_designer.Kernel);
        // 空白区必须返回懒构建的持久实例：属性面板随 ViewChanged/TemplateChanged 频繁
        // Rebuild（先 Clear 摘树再重挂），每次新建控件树会让字段级单例控件重复进视觉树。
        _properties.EmptyStateContentBuilder = () => _pageSection ??= BuildPageSection();

        // 右键菜单由示例自己开：宿主对右键不捕获指针（见 DesignerCanvas 的说明），
        // 事件里带窗口根坐标，覆盖层菜单直接按位置摆卡片。
        _designer.CanvasRightPressed += OnCanvasRightPressed;
        _designer.RawKeyDown += OnDesignerRawKey;

        var page = new ContentPage { Content = BuildRoot() };
        page.Loaded += (_, _) =>
        {
            _designer.TakeFocus();
            // 平台元素级锁浅色：MAUI 按钮等原生控件默认样式跟随后台主题，
            // 应用级锁（App.xaml RequestedTheme / UserAppTheme）在 MAUI Windows 头上
            // 不切 Fluent 控件资源（2026-11-06 复测实锤），元素级级联才生效（与 WinUI 宿主同款）。
            ApplyLightTheme();
        };
        Page = page;

        if (warnings.Count > 0)
            SetStatus(Localizer.T("App.Status.Warnings", string.Join("；", warnings)), error: true);
        else
            SetStatus(Localizer.T("App.Status.Ready"));

        StartAutosave();
    }

    /// <summary>阶段 E：桌面自动保存——30s 一拍，有脏改动且已入解决方案才落库；未保存的新模板不打扰（走 Ctrl+S 另存为）。</summary>
    void StartAutosave()
    {
        Dispatcher.StartTimer(TimeSpan.FromSeconds(30), () =>
        {
            if (_session.IsDirty)
                _ = SaveSolution(false, silent: true);
            return true; // 持续计时。
        });
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

    /// <summary>MAUI 没有跨平台的初始窗口尺寸 API：等窗口处理器挂上后借 WinUI 的 AppWindow 设一次。</summary>
    void OnWindowHandlerChanged(object? sender, EventArgs e)
    {
        if (Handler?.PlatformView is Microsoft.UI.Xaml.Window native && native.AppWindow is { } appWindow)
            appWindow.Resize(new Windows.Graphics.SizeInt32(InitialWindowWidth, InitialWindowHeight));
        ApplyLightTheme();
    }

    /// <summary>
    /// 给平台窗口根内容元素锁 <see cref="Microsoft.UI.Xaml.ElementTheme.Light"/>：
    /// 示例配色全部按浅色硬编码，系统深色主题下 Fluent 默认样式（按钮白字）落在浅灰底上不可读。
    /// Handler 挂上时 Content 可能还没就位，所以 HandlerChanged 与页面 Loaded 两处都调用（幂等）。
    /// </summary>
    void ApplyLightTheme()
    {
        if (Handler?.PlatformView is Microsoft.UI.Xaml.Window native
            && native.Content is Microsoft.UI.Xaml.FrameworkElement root)
            root.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Light;
    }

    Grid BuildRoot()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0 菜单行
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 1 元素栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 2 编辑栏
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3 主体
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 4 JSON
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 5 状态栏

        AddAt(root, BuildMenuRow(), row: 0);
        AddAt(root, BuildElementBar(), row: 1);
        AddAt(root, BuildEditBar(), row: 2);
        AddAt(root, BuildBody(), row: 3);
        AddAt(root, BuildTemplateJsonPanel(), row: 4);
        AddAt(root, BuildStatusBar(), row: 5);

        // 覆盖层自下而上：预览 → 下拉/右键菜单 → 模态。Z 序即添加顺序。
        AddAt(root, BuildPreviewOverlay(), row: 0, rowSpan: 6);
        AddAt(root, _menuOverlay, row: 0, rowSpan: 6);
        AddAt(root, _modal, row: 0, rowSpan: 6);
        // 阶段 G：右上角 Toast 浮层——覆盖整窗、不拦截鼠标（最顶层）。
        AddAt(root, _toastLayer, row: 0, rowSpan: 6);

        BindKernel();
        return root;
    }

    /// <summary>菜单行：MAUI 8 没有 MenuBar，用「文件 ▾」「帮助 ▾」按钮 + 页内下拉卡片模拟（与画布右键菜单同一套覆盖层）。</summary>
    View BuildMenuRow()
    {
        var bar = new HorizontalStackLayout { Margin = new Thickness(4, 2, 4, 2) };
        bar.Add(MakeButton(Localizer.T("Menu.File").Replace("(_F)", string.Empty) + " ▾", ToggleFileMenu));
        bar.Add(MakeButton(Localizer.T("Menu.Help").Replace("(_H)", string.Empty) + " ▾", ToggleHelpMenu));
        return bar;
    }

    /// <summary>展开/收起「文件」下拉卡片：导入 hiprint 模板、加载复杂测试模板。</summary>
    void ToggleFileMenu()
    {
        if (_menuOverlay.IsVisible)
        {
            CloseCanvasMenu();
            return;
        }

        var menu = new VerticalStackLayout { Spacing = 1, MinimumWidthRequest = 200 };
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.ImportHiprint"), true, () => _ = ShowImportHiprint()));
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.Templates"), true, () => OpenTemplateLibrary()));
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.StyleLibrary"), true, () => OpenStyleLibrary()));
        menu.Add(new BoxView { HeightRequest = 1, Color = Colors.LightGray, Margin = new Thickness(4, 2) });
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.New"), true, () => NewSolution()));
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.Open"), true, () => _ = OpenSolutionDialog()));
        // 最近打开：每次展开都从存储重新读，反映最新保存记录（与桌面宿主同口径）。
        foreach (var record in _session.Recent(8))
        {
            var id = record.Id;
            menu.Add(MakeMenuAction(record.Name, true, () => OpenSolution(id)));
        }
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.Save"), true, () => _ = SaveSolution(false)));
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.SaveAs"), true, () => _ = SaveSolution(true)));
        menu.Add(new BoxView { HeightRequest = 1, Color = Colors.LightGray, Margin = new Thickness(4, 2) });
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.ExportJson"), true, () => _ = OnExportJson()));
        menu.Add(MakeMenuAction(Localizer.T("Menu.File.ExportPdf"), true, () => _ = ExportPdf()));
        menu.Add(MakeMenuAction(Localizer.T("Menu.Print.System"), true, () => PrintSolution()));

        ShowMenuCard(menu);
    }

    /// <summary>展开/收起「帮助」下拉卡片：关于 OODD CrossPrint。</summary>
    void ToggleHelpMenu()
    {
        if (_menuOverlay.IsVisible)
        {
            CloseCanvasMenu();
            return;
        }

        var menu = new VerticalStackLayout { Spacing = 1, MinimumWidthRequest = 180 };
        menu.Add(MakeMenuAction(Localizer.T("Menu.Help.About"), true, () => _ = ShowAboutDialogAsync()));
        menu.Add(new BoxView { HeightRequest = 1, Color = Colors.LightGray, Margin = new Thickness(4, 2) });
        menu.Add(MakeMenuAction(Localizer.T("Language.System"), true, () => ChangeLanguage(null)));
        menu.Add(MakeMenuAction(Localizer.T("Language.Chinese"), true, () => ChangeLanguage("zh-CN")));
        menu.Add(MakeMenuAction(Localizer.T("Language.English"), true, () => ChangeLanguage("en-US")));

        ShowMenuCard(menu);
    }

    void ChangeLanguage(string? culture)
    {
        switch (LocalizationSwitch.Change(culture))
        {
            case LanguageChangeOutcome.Restarted:
                // MAUI 的 Window 没有 Close()；新进程已经起来了，直接结束当前进程。
                Environment.Exit(0);
                break;
            case LanguageChangeOutcome.SaveFailed:
                SetStatus(Localizer.T("Language.SaveFailed"), error: true);
                break;
            default:
                SetStatus(Localizer.T("Language.SavedRestartFailed", LocalizationSwitch.DisplayName(culture)), error: true);
                break;
        }
    }

    void ShowMenuCard(VerticalStackLayout menu)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(2),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(6, 34, 0, 0),
            Content = menu,
        };

        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
        };
        backdrop.Clicked += (_, _) => CloseCanvasMenu();

        _menuOverlay.Children.Clear();
        _menuOverlay.Add(backdrop);
        _menuOverlay.Add(card);
        _menuOverlay.IsVisible = true;
    }

    /// <summary>构造一个菜单项按钮：点击先收起下拉/菜单，再执行动作。</summary>
    Button MakeMenuAction(string text, bool enabled, Action action)
    {
        var item = new Button
        {
            Text = text,
            IsEnabled = enabled,
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(8, 4, 8, 4),
        };
        item.Clicked += (_, _) =>
        {
            CloseCanvasMenu();
            action();
        };
        return item;
    }

    /// <summary>元素栏：八种元素类型，点按钮即在页面中心新建。</summary>
    View BuildElementBar()
    {
        var panel = new HorizontalStackLayout { Margin = new Thickness(8, 6, 8, 2), Spacing = 2 };

        foreach (var (label, key) in ElementButtons)
        {
            var typeKey = key;
            panel.Add(MakeButton(label, () => Run(() => _designer.Kernel.AddElementAtCenter(typeKey)), Localizer.T("Toolbar.AddElement.Tooltip", label)));
        }

        return new Border
        {
            Background = new SolidColorBrush(FromRgb(0xF5, 0xF5, 0xF5)),
            Padding = new Thickness(0),
            Content = panel,
        };
    }

    View BuildEditBar()
    {
        var panel = new HorizontalStackLayout { Margin = new Thickness(8, 2, 8, 6), Spacing = 2 };

        // 标签尽量短（完整说明放 ToolTip，与 WPF 基准同一套文案）：编辑带项目多，
        // 窗口变窄时末尾的按钮会先被裁掉，短标签能多留几个在可视区；导入按钮放最前面。
        panel.Add(MakeButton(Localizer.T("Toolbar.Import"), () => _ = ShowImportHiprint(), Localizer.T("Toolbar.Import.Tip")));
        panel.Add(MakeSeparator());
        _undoButton = MakeButton(Localizer.T("Toolbar.Undo"), DoUndo, Localizer.T("Toolbar.Undo.Tip"));
        _redoButton = MakeButton(Localizer.T("Toolbar.Redo"), DoRedo, Localizer.T("Toolbar.Redo.Tip"));
        panel.Add(_undoButton);
        panel.Add(_redoButton);
        panel.Add(MakeSeparator());
        panel.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        panel.Add(MakeButton(Localizer.T("Toolbar.Delete"), () => Run(_designer.Kernel.DeleteSelection), Localizer.T("Toolbar.Delete.Tip")));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        panel.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        panel.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        panel.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        // 「适合窗口 / 100%」与底部状态栏缩放组重复，统一收敛到状态栏（Avalonia 基准口径）；
        // 画布右键菜单与预览覆盖层仍保留同名项。
        panel.Add(MakeSeparator());
        panel.Add(MakeOverlayCheck(Localizer.T("Toolbar.Grid"), true, value => _designer.Overlay.Options.ShowGrid = value));
        panel.Add(MakeOverlayCheck(Localizer.T("Toolbar.Ruler"), true, value => _designer.Overlay.Options.ShowRulers = value));
        panel.Add(MakeOverlayCheck(Localizer.T("Toolbar.SafeArea"), true, value => _designer.Overlay.Options.ShowMarginGuides = value));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton(Localizer.T("Toolbar.ExportPdf"), () => _ = ExportPdf(), Localizer.T("Toolbar.ExportPdf.Tip")));
        panel.Add(MakeButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.Preview.Tip")));

        return new Border
        {
            Background = new SolidColorBrush(FromRgb(0xF5, 0xF5, 0xF5)),
            Padding = new Thickness(0),
            Content = panel,
        };
    }

    /// <summary>MAUI 的 CheckBox 不带文字，套一层横向布局补标签。</summary>
    View MakeOverlayCheck(string text, bool initial, Action<bool> apply)
    {
        var check = new CheckBox
        {
            IsChecked = initial,
            VerticalOptions = LayoutOptions.Center,
        };

        // MAUI 的 CheckBox 点击后同步更新 IsChecked，再让画布重绘即可。
        check.CheckedChanged += (_, _) =>
        {
            apply(check.IsChecked);
            _designer.Invalidate();
            _designer.TakeFocus();
        };

        return new HorizontalStackLayout
        {
            Margin = new Thickness(8, 0, 2, 0),
            Spacing = 4,
            Children =
            {
                check,
                new Label { Text = text, VerticalOptions = LayoutOptions.Center },
            },
        };
    }

    View BuildBody()
    {
        // 三列布局与 WPF 基准一致，但 MAUI 没有可拖动 splitter，宽度定死：按基准初始值收窄一档
        // （左 200 / 右 300），把中间画布多让一点；窄窗口时也少裁编辑带按钮。
        var designerHost = new Border
        {
            Stroke = FromRgb(0xD0, 0xD0, 0xD0),
            StrokeThickness = 1,
            Margin = new Thickness(6),
            Content = _designer,
        };

        // 左栏顶部是纸张栏，下面是工具箱；两行分别是 Auto / Star。
        // 外框由 leftHost 一层承担，纸张栏不再单独套 Border（原先两道相邻描边叠成双框，太重）。
        var leftSidebar = new Grid();
        leftSidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftSidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbox = BuildToolbox();
        Grid.SetRow(_paper, 0);
        Grid.SetRow(toolbox, 1);
        leftSidebar.Add(_paper);
        leftSidebar.Add(toolbox);

        var leftHost = new Border
        {
            Background = new SolidColorBrush(FromRgb(0xFA, 0xFA, 0xFA)),
            Stroke = FromRgb(0xD0, 0xD0, 0xD0),
            StrokeThickness = 1,
            Content = leftSidebar,
        };

        var propertyHost = new Border
        {
            Stroke = FromRgb(0xD0, 0xD0, 0xD0),
            StrokeThickness = 1,
            Content = _properties,
        };

        // 五列布局（对齐 WinUI 宿主）：左 200 / 4px 分隔条 / 中画布 / 4px 分隔条 / 右 300（属性面板）。
        // MAUI 没有内置 GridSplitter，用 PanGestureRecognizer 的零依赖轻量分隔条（Windows 头鼠标拖拽可用）。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        Grid.SetColumn(leftHost, 0);
        grid.Add(leftHost);

        var leftSplitter = MakeColumnSplitter(grid, targetColumn: 0, sign: +1);
        Grid.SetColumn(leftSplitter, 1);
        grid.Add(leftSplitter);

        Grid.SetColumn(designerHost, 2);
        grid.Add(designerHost);

        var rightSplitter = MakeColumnSplitter(grid, targetColumn: 4, sign: -1);
        Grid.SetColumn(rightSplitter, 3);
        grid.Add(rightSplitter);

        Grid.SetColumn(propertyHost, 4);
        grid.Add(propertyHost);
        return grid;
    }

    /// <summary>零依赖的轻量列分隔条（与 WinUI/Uno 宿主同款语义）：PanGestureRecognizer 拖动改变
    /// targetColumn 的固定像素宽（sign 决定方向），中间画布自适应。限幅 140–560。</summary>
    static BoxView MakeColumnSplitter(Grid grid, int targetColumn, double sign)
    {
        var splitter = new BoxView
        {
            Color = Colors.LightGray,
            WidthRequest = 4,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
        double startWidth = 0;
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            if (e.StatusType == GestureStatus.Started)
            {
                startWidth = grid.ColumnDefinitions[targetColumn].Width.Value;
            }
            else if (e.StatusType == GestureStatus.Running)
            {
                var w = startWidth + e.TotalX * sign;
                w = Math.Max(140, Math.Min(560, w));
                grid.ColumnDefinitions[targetColumn].Width = new GridLength(w);
            }
        };
        splitter.GestureRecognizers.Add(pan);
        return splitter;
    }

    /// <summary>工具箱：元素类型列表，点选后按「添加到画布」添加到画布中心（CollectionView 没有双击语义）。</summary>
    View BuildToolbox()
    {
        // 左栏收窄到 200 后外框/留白都收紧一档：外层留 8（原 10），节标题下距 4（原 6）。
        var grid = new Grid { Margin = new Thickness(8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Label
        {
            Text = Localizer.T("Toolbox.Section"),
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetRow(header, 0);
        grid.Add(header);

        var search = new Entry { Placeholder = Localizer.T("Toolbox.Search") };
        search.TextChanged += (_, _) => ApplyToolboxFilter(search.Text);
        Grid.SetRow(search, 1);
        grid.Add(search);

        ApplyToolboxFilter(string.Empty);
        Grid.SetRow(_toolbox, 2);
        grid.Add(_toolbox);

        var add = new Button
        {
            Text = Localizer.T("Toolbox.AddToCanvas"),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        add.Clicked += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 3);
        grid.Add(add);

        var hint = new Label
        {
            Text = Localizer.T("Toolbox.HintClick"),
            FontSize = 11,
            TextColor = Colors.Gray,
            LineBreakMode = LineBreakMode.WordWrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(hint, 4);
        grid.Add(hint);

        return grid;
    }

    /// <summary>
    /// 工具箱按中英文名（或类型键）过滤条目：空查询=全部显示。
    /// 拖放：MAUI 的 DragGestureRecognizer 需要每个平台 handler 注册 Drop 事件，示例宿主
    /// 无法跨平台无头实证，故按待办口径以「点选 + 添加到画布」兜底（见 HintClick 文案）。
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

    View BuildStatusBar()
    {
        _status.TextColor = Colors.DimGray;
        _status.Margin = new Thickness(10, 3, 10, 3);
        _status.LineBreakMode = LineBreakMode.NoWrap;
        _status.VerticalOptions = LayoutOptions.Center;
        _zoomText.TextColor = Colors.DimGray;
        _zoomText.Margin = new Thickness(8, 3, 4, 3);
        _zoomText.VerticalOptions = LayoutOptions.Center;
        _zoomText.LineBreakMode = LineBreakMode.NoWrap;

        Button ZoomBtn(string text, Action action, string tip)
        {
            var b = new Button
            {
                Text = text,
                Padding = new Thickness(4, 1, 4, 1),
                MinimumHeightRequest = 26,
                VerticalOptions = LayoutOptions.Center,
            };
            if (!string.IsNullOrEmpty(tip))
                ToolTipProperties.SetText(b, tip);
            b.Clicked += (_, _) => action();
            return b;
        }

        var zoomBox = new HorizontalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        zoomBox.Add(ZoomBtn(Localizer.T("Toolbar.ZoomOut"), () => EditorActions.ZoomOut(_designer.Kernel), Localizer.T("Toolbar.ZoomOut.Tip")));
        zoomBox.Add(ZoomBtn(Localizer.T("Toolbar.ZoomIn"), () => EditorActions.ZoomIn(_designer.Kernel), Localizer.T("Toolbar.ZoomIn.Tip")));
        zoomBox.Add(ZoomBtn(Localizer.T("Toolbar.ZoomActual"), () => EditorActions.ZoomActual(_designer.Kernel), Localizer.T("Toolbar.ZoomActual.Tip")));
        zoomBox.Add(ZoomBtn(Localizer.T("Toolbar.FitToPage"), FitToPageFromStatus, Localizer.T("Toolbar.FitToPage.Tip")));

        // 三列 Grid：缩放组/比例固定左侧，状态文本吃掉剩余宽度。
        var grid = new Grid { ColumnSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(zoomBox, 0);
        Grid.SetColumn(_zoomText, 1);
        Grid.SetColumn(_status, 2);
        grid.Add(zoomBox);
        grid.Add(_zoomText);
        grid.Add(_status);

        return new Border
        {
            Background = new SolidColorBrush(FromRgb(0xF0, 0xF0, 0xF0)),
            Stroke = FromRgb(0xD0, 0xD0, 0xD0),
            StrokeThickness = 1,
            Content = grid,
        };
    }

    /// <summary>「适合窗口」用宿主真实绘制表面尺寸；它不发 ViewChanged，读数要手动刷一次。</summary>
    void FitToPageFromStatus()
    {
        _designer.FitToPage();
        UpdateZoomText();
        _designer.TakeFocus();
    }

    void DoUndo()
    {
        if (!EditorActions.Undo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToUndo"));
            return;
        }

        _designer.TakeFocus();
        SetStatus(Localizer.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!EditorActions.Redo(_designer.Kernel))
        {
            SetStatus(Localizer.T("Status.NothingToRedo"));
            return;
        }

        _designer.TakeFocus();
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

    View BuildPreviewOverlay()
    {
        _previewOverlay.Background = new SolidColorBrush(Color.FromRgba(0xB4, 0xB4, 0xB4, 0xE0));
        _previewOverlay.IsVisible = false;
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Background = new SolidColorBrush(Colors.White) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new Label
        {
            Text = "预览：示例数据 title / orderNo / sku。与 PDF 导出共用同一条渲染路径。",
            VerticalOptions = LayoutOptions.Center,
            TextColor = Colors.DimGray,
            Margin = new Thickness(12, 6, 8, 6),
            LineBreakMode = LineBreakMode.NoWrap,
        };

        var buttons = new HorizontalStackLayout { Margin = new Thickness(4, 4, 8, 4), Spacing = 2 };
        buttons.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), () => _preview?.FitToPage()));
        buttons.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), () =>
        {
            _preview?.Kernel.Viewport.Reset();
            _preview?.Invalidate();
        }));
        buttons.Add(MakeButton(Localizer.T("About.Close"), ClosePreview));

        Grid.SetColumn(info, 0);
        Grid.SetColumn(buttons, 1);
        bar.Add(info);
        bar.Add(buttons);

        Grid.SetRow(bar, 0);
        Grid.SetRow(_previewHost, 1);
        _previewOverlay.Add(bar);
        _previewOverlay.Add(_previewHost);
        return _previewOverlay;
    }

    void Run(Action action)
    {
        action();
        _designer.TakeFocus();
    }

    /// <summary>
    /// 右键命中：右键元素即单选它、右键空白清空选中，菜单项可用性据此计算。
    /// 坐标直接用宿主给的画布 DIP（内核要宿主设备 px，按同一口径换算）。
    /// </summary>
    void OnCanvasRightPressed(object? sender, CanvasRightPressedEventArgs e)
    {
        var dpi = _designer.Dpi;
        _designer.Kernel.SelectAt(new PointerPoint(
            e.XDip * dpi / 96.0,
            e.YDip * dpi / 96.0,
            dpi,
            PointerButton.Secondary));

        ShowCanvasMenu(e.WindowXDip, e.WindowYDip);
    }

    /// <summary>Ctrl+D 创建副本 / Ctrl+Z / Ctrl+Y 撤销重做：宿主把内核没映射的按键都抛到 RawKeyDown，这里组合修饰键判断。</summary>
    void OnDesignerRawKey(object? sender, RawKeyEventArgs e)
    {
        if (e.Control && !e.Alt && (e.Key == VirtualKey.Z || e.Key == VirtualKey.Y))
        {
            // Ctrl+Shift+Z 与 Ctrl+Y 都算重做（与 Avalonia 基准一致）。
            if (e.Key == VirtualKey.Y || e.Shift)
                DoRedo();
            else
                DoUndo();
            return;
        }

        if (e.Control && e.Key == VirtualKey.D)
            DuplicateSelection();

        // 阶段 E：Ctrl+S 立即保存。
        if (e.Control && e.Key == VirtualKey.S)
            _ = SaveSolution(false);
    }

    /// <summary>在右键位置弹出画布菜单；菜单项全部直连内核命令，没有选中时选中类项禁用。</summary>
    /// <param name="x">相对窗口内容根的 DIP 坐标（宿主事件给出）。</param>
    void ShowCanvasMenu(double x, double y)
    {
        var kernel = _designer.Kernel;
        var paper = kernel.Template.Paper;
        var hasSelection = !kernel.Selection.IsEmpty;

        var menu = new VerticalStackLayout { Spacing = 1, MinimumWidthRequest = 180 };

        void Add(string text, bool enabled, Action action) => menu.Add(MakeMenuAction(text, enabled, action));

        Add(Localizer.T("Canvas.Context.Duplicate") + " (Ctrl+D)", hasSelection, DuplicateSelection);
        Add(Localizer.T("Canvas.Context.Delete") + " (Delete)", hasSelection, () => Run(_designer.Kernel.DeleteSelection));
        Add(Localizer.T("Canvas.Context.BringForward"), hasSelection, () => Run(_designer.Kernel.BringForward));
        Add(Localizer.T("Canvas.Context.SendBackward"), hasSelection, () => Run(_designer.Kernel.SendBackward));
        Add(Localizer.T("Canvas.Context.BringToFront"), hasSelection, () => Run(_designer.Kernel.BringToFront));
        Add(Localizer.T("Canvas.Context.SendToBack"), hasSelection, () => Run(_designer.Kernel.SendToBack));
        Add(Localizer.T("Canvas.Context.AlignCenter"), hasSelection, () =>
        {
            Run(_designer.Kernel.CenterSelectionHorizontally);
            SetStatus(Localizer.T("Status.HAlignCenter"));
        });
        Add(Localizer.T("Canvas.Context.AlignMiddle"), hasSelection && !paper.IsContinuous, () =>
        {
            Run(_designer.Kernel.CenterSelectionVertically);
            SetStatus(Localizer.T("Status.VAlignCenter"));
        });
        Add(Localizer.T("Toolbar.FitToPage"), true, () => Run(_designer.FitToPage));
        Add(Localizer.T("Canvas.Context.ActualSize"), true, () => Run(() =>
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual))));
        Add(
            paper.Orientation == PaperOrientation.Landscape
                ? Localizer.T("Canvas.Context.RotatePortrait")
                : Localizer.T("Canvas.Context.RotateLandscape"),
            !paper.IsContinuous,
            TogglePaperOrientation);
        Add(Localizer.T("Canvas.Context.CustomPaper"), true, () => _ = ShowCustomPaper());

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(2),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(x + 2, y + 2, 0, 0),
            Content = menu,
        };

        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
        };
        backdrop.Clicked += (_, _) => CloseCanvasMenu();

        _menuOverlay.Children.Clear();
        _menuOverlay.Add(backdrop);
        _menuOverlay.Add(card);
        _menuOverlay.IsVisible = true;
    }

    void CloseCanvasMenu()
    {
        _menuOverlay.IsVisible = false;
        _menuOverlay.Children.Clear();
        _designer.TakeFocus();
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus(Localizer.T("Status.SelectBeforeDuplicate"));
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.TakeFocus();
        SetStatus(Localizer.T("Status.DuplicateDone", EditorKernel.DuplicateOffsetMm));
    }

    void TogglePaperOrientation() => _paper.ToggleOrientation();

    async Task ShowCustomPaper()
    {
        await _paper.ShowCustomPaperDialogAsync();
        _designer.TakeFocus();
    }

    View BuildTemplateJsonPanel()
    {
        // JSON 面板是低频的调试/导入入口：默认折叠只留一条标题带（与 WPF 基准一致），
        // 竖向空间优先留给画布；「应用 JSON」「重新载入」常驻标题带，折叠时也能操作。
        _jsonToggle.Text = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.Padding = new Thickness(6, 2, 6, 2);
        _jsonToggle.FontAttributes = FontAttributes.Bold;
        ToolTipProperties.SetText(_jsonToggle, "展开/收起模板 JSON 面板");
        _jsonToggle.Clicked += (_, _) => SetJsonPanelExpanded(!_jsonExpanded);

        var apply = new Button
        {
            Text = "应用 JSON",
            Padding = new Thickness(8, 2, 8, 2),
        };
        apply.Clicked += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Text = "重新载入",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
        };
        reload.Clicked += (_, _) => RefreshTemplateJson("已重新载入当前模板，未应用的编辑已丢弃。");

        _jsonState.FontSize = 12;
        _jsonState.TextColor = Colors.DimGray;
        _jsonState.VerticalOptions = LayoutOptions.Center;
        _jsonState.LineBreakMode = LineBreakMode.TailTruncation;
        _jsonState.Margin = new Thickness(10, 0, 0, 0);

        var header = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0 折叠开关
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1 应用 JSON
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2 重新载入
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3 状态文字

        header.Add(_jsonToggle);
        Grid.SetColumn(apply, 1);
        header.Add(apply);
        Grid.SetColumn(reload, 2);
        header.Add(reload);
        Grid.SetColumn(_jsonState, 3);
        header.Add(_jsonState);

        _templateJson.FontFamily = "Consolas";
        _templateJson.FontSize = 12;
        _templateJson.Margin = new Thickness(8, 0, 8, 6);
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();

        _jsonPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _jsonPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _jsonPanel.Add(header);
        Grid.SetRow(_templateJson, 1);
        _jsonPanel.Add(_templateJson);

        SetJsonPanelExpanded(false);

        return new Border
        {
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Content = _jsonPanel,
        };
    }

    /// <summary>展开/收起 JSON 面板：折叠时编辑器随 IsVisible=false 归零（Star 行无可见内容即 0 高），只剩标题带；展开时定高，防止长 JSON 把画布挤扁。</summary>
    void SetJsonPanelExpanded(bool expanded)
    {
        _jsonExpanded = expanded;
        _jsonToggle.Text = (expanded ? "▾ " : "▸ ") + Localizer.T("JsonPanel.Title");
        _templateJson.IsVisible = expanded;
        // HeightRequest 的"未设置"哨兵是 -1：折叠时高度交还内容（标题带一行），展开时钉住定高。
        _jsonPanel.HeightRequest = expanded ? JsonPanelExpandedHeight : -1;
    }

    /// <summary>把纸张栏/属性面板/JSON 面板全部接到当前内核上；LoadTemplate 换内核后必须重调。</summary>
    void BindKernel(string? jsonNote = null)
    {
        _paper.Attach(_designer.Kernel);
        _properties.Attach(_designer.Kernel);
        _designer.Kernel.TemplateChanged += OnKernelTemplateChanged;
        // 提交、撤销、重做、换模板都会发 TemplateChanged：水印控件统一从这里回读模型值。
        _designer.Kernel.TemplateChanged += RefreshWatermarkControls;
        // 阶段 E：任何编辑都标记脏位（LoadTemplate 已换内核，这里重绑到新内核）。
        _designer.Kernel.TemplateChanged += MarkSolutionDirty;
        // LoadTemplate 会换内核：撤销态与缩放读数的订阅必须每次重绑（与 Avalonia 基准一致）。
        _designer.Kernel.UndoStateChanged += UpdateUndoButtons;
        _designer.Kernel.ViewChanged += UpdateZoomText;
        RefreshWatermarkControls();
        UpdateUndoButtons();
        UpdateZoomText();
        RefreshTemplateJson(jsonNote);
    }

    /// <summary>右栏空白区持久实例：属性面板每次 Rebuild 复用同一棵控件树。</summary>
    View BuildPageSection()
    {
        var stack = new VerticalStackLayout { Margin = new Thickness(0, 4, 0, 0) };
        stack.Add(BuildWatermarkSection());
        return stack;
    }

    /// <summary>
    /// 水印设置区：启用/文字/字段绑定/字号/透明度/角度/间距/颜色。
    /// 校验、夹取、可撤销提交、回填统一走共享层 <see cref="WatermarkEdits"/>。
    /// </summary>
    View BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox { Margin = new Thickness(0, 2, 0, 2) };
        _watermarkEnabled.CheckedChanged += (_, e) =>
            CommitIfNotSyncing(() => WatermarkEdits.SetEnabled(_designer.Kernel, e.Value));

        _watermarkText = WatermarkEntry(() => WatermarkEdits.SetText(_designer.Kernel, _watermarkText.Text));
        _watermarkBinding = WatermarkEntry(() => WatermarkEdits.SetBinding(_designer.Kernel, _watermarkBinding.Text));
        _watermarkFontSize = WatermarkEntry(() => CommitWatermarkNumber(WatermarkNumber.FontSizePt, _watermarkFontSize));
        _watermarkOpacity = WatermarkEntry(() => CommitWatermarkNumber(WatermarkNumber.Opacity, _watermarkOpacity));
        _watermarkRotation = WatermarkEntry(() => CommitWatermarkNumber(WatermarkNumber.RotationDeg, _watermarkRotation));
        _watermarkSpacing = WatermarkEntry(() => CommitWatermarkNumber(WatermarkNumber.SpacingMm, _watermarkSpacing));
        _watermarkColor = WatermarkEntry(CommitWatermarkColor);

        var section = new VerticalStackLayout { Spacing = 2 };
        section.Add(new Label
        {
            Text = Localizer.T("Watermark.Section"),
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 8, 0, 2),
        });
        section.Add(new HorizontalStackLayout
        {
            Spacing = 6,
            Children = { _watermarkEnabled, new Label { Text = Localizer.T("Watermark.Enabled"), FontSize = 12 } },
        });
        section.Add(WatermarkRow(Localizer.T("Watermark.Text"), _watermarkText));
        section.Add(WatermarkRow(Localizer.T("Watermark.Binding"), _watermarkBinding));
        section.Add(WatermarkRow(Localizer.T("Watermark.FontSize"), _watermarkFontSize));
        section.Add(WatermarkRow(Localizer.T("Watermark.Opacity"), _watermarkOpacity));
        section.Add(WatermarkRow(Localizer.T("Watermark.Rotation"), _watermarkRotation));
        section.Add(WatermarkRow(Localizer.T("Watermark.Spacing"), _watermarkSpacing));
        section.Add(WatermarkRow(Localizer.T("Watermark.Color"), _watermarkColor));

        RefreshWatermarkControls();
        return section;
    }

    /// <summary>水印输入框：失焦或回车提交；回填期间的事件由重入守卫挡掉。</summary>
    Entry WatermarkEntry(Action commit)
    {
        var box = new Entry();
        box.Unfocused += (_, _) => CommitIfNotSyncing(commit);
        box.Completed += (_, _) => CommitIfNotSyncing(commit);
        return box;
    }

    static View WatermarkRow(string label, View editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Add(new Label
        {
            Text = label,
            FontSize = 12,
            TextColor = Colors.DimGray,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 2, 6, 2),
        });
        editor.HorizontalOptions = LayoutOptions.Fill;
        Grid.SetColumn(editor, 1);
        grid.Add(editor);
        return grid;
    }

    void CommitIfNotSyncing(Action commit)
    {
        if (!_syncingWatermark)
            commit();
    }

    void CommitWatermarkNumber(WatermarkNumber field, Entry box)
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
        _templateJson.CursorPosition = 0;
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
                SetStatus("已把 hiprint 模板转换为 CrossPrint 模板。");
                _designer.TakeFocus();
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
            _designer.TakeFocus();
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

    /// <summary>导入 hiprint 模板：文件选择器不在本示例范围（同 Uno），用路径输入 + 页内模态完成。</summary>
    async Task ShowImportHiprint()
    {
        // 读文件与解析都在"确定"里完成；失败就把模态留在原地显示原因。
        var pathBox = new Entry
        {
            Text = Path.Combine(AppContext.BaseDirectory, "hiprint-sample.js"),
            Placeholder = @"例如 D:\templates\hiprint.js",
        };

        var hint = new Label
        {
            Text = "支持 hiprint 模块源码、getJson() 片段与严格 JSON；输出目录里已附带 hiprint-sample.js。",
            FontSize = 12,
            TextColor = Colors.DimGray,
            LineBreakMode = LineBreakMode.WordWrap,
            Margin = new Thickness(0, 8, 0, 2),
        };

        var content = new VerticalStackLayout { WidthRequest = 360 };
        content.Add(new Label { Text = "模板文件路径", FontSize = 12 });
        content.Add(pathBox);
        content.Add(hint);

        HiprintImportResult? imported = null;

        var confirmed = await _modal.ShowAsync(
            Localizer.T("Dialog.Import.Title"),
            content,
            validate: () =>
            {
                var path = (pathBox.Text ?? string.Empty).Trim();
                if (path.Length == 0)
                    return "请填写模板文件路径。";

                try
                {
                    imported = HiprintTemplateImporter.Import(File.ReadAllText(path));
                    return null;
                }
                catch (Exception ex)
                {
                    return "导入失败：" + ex.Message;
                }
            });

        if (!confirmed || imported is null)
            return;

        ApplyHiprint(imported, Path.GetFileName((pathBox.Text ?? string.Empty).Trim()));
    }

    void ApplyHiprint(HiprintImportResult result, string sourceName)
    {
        var note = DescribeImport(sourceName, result);

        // LoadTemplate 会换内核：纸张栏、属性面板和 JSON 面板都要换到新内核上。
        // 导入摘要交给 BindKernel，否则会被"已同步当前模板"覆盖，用户看不到跳过/降级原因。
        _designer.LoadTemplate(result.Template);
        BindKernel(note);

        _designer.TakeFocus();
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

    async Task ExportPdf()
    {
        using var stream = new MemoryStream();
        var report = PdfExporter.Export(
            _designer.Template,
            stream,
            data: SampleData.Create(),
            fontResolver: _designer.FontResolver,
            images: _designer.Images,
            renderer: _designer.Renderer);
        stream.Position = 0;

        var file = await PickSaveFileAsync("crossprint-maui", ".pdf", Localizer.T("Dialog.Export.PdfFilter"));
        if (file is null)
        {
            SetStatus(Localizer.T("Error.ExportFailed", Localizer.T("Dialog.Cancel")), error: true);
            return;
        }

        try
        {
            await System.IO.File.WriteAllBytesAsync(file.Path, stream.ToArray());
            SetStatus(report.HasIssues
                ? Localizer.T("Status.Exported", file.Path) + "（" + report.Issues.Count + " 条渲染降级）"
                : Localizer.T("Status.Exported", file.Path));
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
        _previewHost.Add(preview);
        _preview = preview;
        _previewOverlay.IsVisible = true;
    }

    void ClosePreview()
    {
        _previewOverlay.IsVisible = false;
        _previewHost.Children.Clear();
        _preview = null;
        _designer.TakeFocus();
    }

    /// <summary>显示关于对话框：产品信息 + 版本 + 描述 + 第三方依赖列表 + 关闭按钮。</summary>
    async Task ShowAboutDialogAsync()
    {
        // 构建内容：标题居中、产品名 + 版本、描述、分隔线、第三方列表、关闭按钮
        var logoBytes = LogoLoader.GetLogoBytes();
        var logo = new Image
        {
            Source = logoBytes is null ? null : ImageSource.FromStream(() => new MemoryStream(logoBytes)),
            WidthRequest = 96,
            HeightRequest = 96,
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var productTitle = new Label
        {
            Text = AboutInfo.ProductName,
            FontSize = 22,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var version = new Label
        {
            Text = Localizer.T("About.Version", AboutInfo.Version),
            FontSize = 14,
            TextColor = Colors.DimGray,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var description = new Label
        {
            Text = Localizer.T("About.Description"),
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = Colors.DimGray,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var thirdPartyHeader = new Label
        {
            Text = Localizer.T("About.ThirdParty"),
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 4, 0, 4),
        };

        var thirdPartyPanel = new VerticalStackLayout { Spacing = 2 };
        thirdPartyPanel.Add(thirdPartyHeader);
        foreach (var tp in AboutInfo.ThirdParty)
        {
            thirdPartyPanel.Add(new Label
            {
                Text = $"  • {tp.Name}  ({tp.License}) — {tp.Description}",
                LineBreakMode = LineBreakMode.WordWrap,
                TextColor = Colors.DimGray,
                FontSize = 12,
            });
        }

        var closeButton = new Button
        {
            Text = Localizer.T("About.Close"),
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(16, 4),
        };

        var content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    logo,
                    productTitle,
                    version,
                    description,
                    new Border
                    {
                        HeightRequest = 1,
                        BackgroundColor = Colors.LightGray,
                        Margin = new Thickness(0, 4, 0, 4),
                    },
                    thirdPartyPanel,
                    closeButton,
                },
            },
        };

        closeButton.Clicked += (_, _) => CloseAboutDialog();

        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
        };
        backdrop.Clicked += (_, _) => CloseAboutDialog();

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(20),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 520,
            HeightRequest = 480,
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label
                    {
                        Text = Localizer.T("About.Title"),
                        FontSize = 18,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalOptions = LayoutOptions.Center,
                        Margin = new Thickness(0, 0, 0, 4),
                    },
                    content,
                },
            },
        };

        _menuOverlay.Children.Clear();
        _menuOverlay.Add(backdrop);
        _menuOverlay.Add(card);
        _menuOverlay.IsVisible = true;

        await Task.CompletedTask;
    }

    void CloseAboutDialog()
    {
        _menuOverlay.IsVisible = false;
        _menuOverlay.Children.Clear();
        _designer.TakeFocus();
    }

    void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.TextColor = error ? Colors.Firebrick : Colors.DimGray;
    }

    // 阶段 G：右上角 Toast（成功/提示浮层），3 秒自动消失，不拦截鼠标；同一时刻只保留最新一条。
    void ShowToast(string title, string? message = null)
    {
        _toastLayer.Children.Clear();

        var icon = new Frame
        {
            WidthRequest = 22,
            HeightRequest = 22,
            CornerRadius = 11,
            Padding = 0,
            BackgroundColor = Colors.Green,
            Content = new Label
            {
                Text = "✓",
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold,
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            },
        };

        var textStack = new VerticalStackLayout { Spacing = 1, Margin = new Thickness(10, 0, 0, 0) };
        textStack.Children.Add(new Label { Text = title, FontSize = 13, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrEmpty(message))
            textStack.Children.Add(new Label
            {
                Text = message,
                FontSize = 12,
                TextColor = Color.FromArgb("#FF606266"),
                LineBreakMode = LineBreakMode.WordWrap,
            });

        var card = new Border
        {
            BackgroundColor = Colors.White,
            Stroke = Color.FromArgb("#FFE5E9F0"),
            StrokeThickness = 1,
            Padding = new Thickness(14, 11),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 14, 14, 0),
            Content = new HorizontalStackLayout { Spacing = 1, Children = { icon, textStack } },
        };

        _toastLayer.Children.Add(card);

        Dispatcher.StartTimer(TimeSpan.FromSeconds(3), () =>
        {
            _toastLayer.Children.Clear();
            return false;
        });
    }

    /// <summary>构造工具栏按钮；tooltip 走 ToolTipProperties（MAUI 没有 WPF 的 ToolTip 属性，WinUI 侧落成悬停提示）。</summary>
    static Button MakeButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button
        {
            Text = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(1, 0, 1, 0),
        };

        if (tooltip is not null)
            ToolTipProperties.SetText(button, tooltip);

        button.Clicked += (_, _) => action();
        return button;
    }

    static View MakeSeparator() => new Border
    {
        WidthRequest = 1,
        Background = new SolidColorBrush(FromRgb(0xD0, 0xD0, 0xD0)),
        Margin = new Thickness(5, 3, 5, 3),
    };

    static void AddAt(Grid grid, View element, int row, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        if (rowSpan > 1)
            Grid.SetRowSpan(element, rowSpan);
        grid.Add(element);
    }

    static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>模板库对话框：左侧分类 + 右侧缩略图卡片（用 FlexLayout 自动换行），点击卡片套用模板。</summary>
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

        var cardHost = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(8), Margin = new Thickness(4) };

        void ApplyFromLibrary(BuiltinTemplateCatalog.Entry entry)
        {
            _designer.LoadTemplate(entry.Create());
            BindKernel(entry.Name);
            SetStatus(Localizer.T("Status.TemplateApplied", entry.Name));
            CloseTemplateLibrary();
        }

        View BuildCard(BuiltinTemplateCatalog.Entry entry)
        {
            var thumbnail = new Image
            {
                WidthRequest = 200,
                HeightRequest = 96,
                Aspect = Aspect.AspectFit,
                Margin = new Thickness(0, 0, 0, 4),
            };
            try
            {
                var png = BuiltinTemplateCatalog.RenderThumbnailPng(entry.Create());
                thumbnail.Source = ImageSource.FromStream(() => new MemoryStream(png));
            }
            catch
            {
                // 缩略图渲染失败不阻断选单本身。
            }

            var title = new Label
            {
                Text = entry.Name,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.NoWrap,
            };
            var desc = new Label
            {
                Text = entry.Description,
                FontSize = 11,
                TextColor = Colors.Gray,
                LineBreakMode = LineBreakMode.WordWrap,
            };

            var stack = new VerticalStackLayout { Spacing = 2, Children = { thumbnail, title, desc } };
            var card = new Border
            {
                Stroke = Colors.LightGray,
                StrokeThickness = 1,
                Padding = new Thickness(8),
                WidthRequest = 224,
                Margin = new Thickness(4),
                Content = stack,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ApplyFromLibrary(entry);
            card.GestureRecognizers.Add(tap);
            return card;
        }

        var categoryList = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(8, 4) };
        foreach (var c in new[] { "全部" }.Concat(BuiltinTemplateCatalog.Categories))
        {
            var cat = c;
            var btn = new Button
            {
                Text = c,
                HorizontalOptions = LayoutOptions.Fill,
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 13,
            };
            btn.Clicked += (_, _) => ShowCards(cat);
            categoryList.Add(btn);
        }

        void ShowCards(string category)
        {
            cardHost.Children.Clear();
            var list = category == "全部" ? entries : byCategory.GetValueOrDefault(category, new List<BuiltinTemplateCatalog.Entry>());
            foreach (var entry in list)
                cardHost.Children.Add(BuildCard(entry));
        }

        var leftScroll = new ScrollView { Content = categoryList };
        var rightScroll = new ScrollView { Content = cardHost, Orientation = ScrollOrientation.Both };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightScroll, 1);
        body.Add(leftScroll);
        body.Add(rightScroll);

        var hint = new Label
        {
            Text = Localizer.T("TemplateLibrary.Hint"),
            FontSize = 12,
            TextColor = Colors.DimGray,
            Margin = new Thickness(8, 6),
        };

        var root = new VerticalStackLayout { Spacing = 4, Children = { hint, body } };

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(8),
            WidthRequest = 1024,
            HeightRequest = 720,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = root,
        };

        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
        };
        backdrop.Clicked += (_, _) => CloseTemplateLibrary();

        _menuOverlay.Children.Clear();
        _menuOverlay.Add(backdrop);
        _menuOverlay.Add(card);
        _menuOverlay.IsVisible = true;
        ShowCards("全部");
    }

    void CloseTemplateLibrary()
    {
        _menuOverlay.IsVisible = false;
        _menuOverlay.Children.Clear();
        _designer.TakeFocus();
    }

    // ===== 阶段 G：样式库对话框 =====

    /// <summary>
    /// 样式库对话框（MAUI，复用模板库覆盖层）：左列样式清单（增删），右列选中样式的字段表单。
    /// 字段模型 / 可空值读写 / 解析全部走共享层 <see cref="StyleLibrarySupport"/>，本方法只负责 UI 摆布与事件接线。
    /// 所有变更经 <see cref="EditorKernel.UpdateStyle"/>，可撤销；删除被引用的样式由内核拦截并提示。
    /// </summary>
    sealed class StyleItem
    {
        public string Id = string.Empty;
        public string Display = string.Empty;
    }

    void OpenStyleLibrary()
    {
        string? currentId = null;
        var syncing = false;

        // MAUI 的 Picker.ItemsSource 拿到的是包装后的项，无法直接回转型；
        // 因此 id 与枚举候选用并行列表保存，界面只认 SelectedIndex。
        var styleItems = new List<StyleItem>();
        var enumOptions = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var message = new Label { TextColor = Colors.Firebrick, Margin = new Thickness(8, 4) };
        var styleList = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(8, 4) };
        var addBtn = new Button { Text = "新增", Margin = new Thickness(4) };
        var delBtn = new Button { Text = "删除", Margin = new Thickness(4) };

        var fieldControls = new Dictionary<string, View>(StringComparer.Ordinal);
        var fieldsForm = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(8) };
        foreach (var def in StyleLibrarySupport.Fields)
        {
            View editor = def.Kind switch
            {
                StyleFieldKind.Bool => new Picker(),
                StyleFieldKind.Enum => new Picker(),
                _ => new Entry(),
            };
            fieldControls[def.Key] = editor;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new Label { Text = def.LabelZh, VerticalOptions = LayoutOptions.Center };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(editor, 1);
            row.Add(label);
            row.Add(editor);
            fieldsForm.Add(row);
        }

        var leftContent = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new HorizontalStackLayout { Spacing = 4, Padding = new Thickness(4), Children = { addBtn, delBtn } },
                styleList,
            },
        };
        var leftScroll = new ScrollView { Content = leftContent };
        var rightScroll = new ScrollView { Content = fieldsForm };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightScroll, 1);
        body.Add(leftScroll);
        body.Add(rightScroll);

        var root = new VerticalStackLayout { Spacing = 4, Children = { body, message } };
        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(8),
            WidthRequest = 760,
            HeightRequest = 560,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = root,
        };
        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
        backdrop.Clicked += (_, _) => CloseStyleLibrary();

        void ShowMessage(string? text) => message.Text = text ?? string.Empty;

        void RefreshList(string? keepId)
        {
            currentId = keepId;
            syncing = true;
            try
            {
                styleItems.Clear();
                styleList.Children.Clear();
                foreach (var s in _designer.Kernel.Template.Styles)
                {
                    var item = new StyleItem { Id = s.Id, Display = $"{s.Name}（{s.Id}）" };
                    styleItems.Add(item);
                    var btn = new Button
                    {
                        Text = item.Display,
                        HorizontalOptions = LayoutOptions.Fill,
                        Padding = new Thickness(6, 3),
                        FontSize = 13,
                    };
                    if (item.Id == keepId)
                        btn.Background = new SolidColorBrush(Colors.LightBlue);
                    var capturedId = item.Id;
                    btn.Clicked += (_, _) => { if (!syncing) SelectStyle(capturedId); };
                    styleList.Add(btn);
                }
            }
            finally
            {
                syncing = false;
            }

            LoadFields();
        }

        void SelectStyle(string id) => RefreshList(id);

        void LoadFields()
        {
            syncing = true;
            try
            {
                var s = currentId is null ? null : _designer.Kernel.Template.FindStyle(currentId);
                foreach (var def in StyleLibrarySupport.Fields)
                {
                    var raw = s is null ? null : StyleLibrarySupport.GetValue(s, def.Key);
                    switch (fieldControls[def.Key])
                    {
                        case Entry e:
                            e.Text = StyleLibrarySupport.Format(def, raw);
                            break;
                        case Picker p:
                            var opts = enumOptions[def.Key];
                            p.ItemsSource = opts;
                            p.SelectedIndex = raw is null ? 0 : Math.Max(0, Array.IndexOf(opts, raw.ToString()));
                            break;
                    }
                }
            }
            finally
            {
                syncing = false;
            }
        }

        void CommitField(StyleFieldDef def)
        {
            if (currentId is null || syncing)
                return;

            object? value = def.Kind switch
            {
                StyleFieldKind.Bool => (fieldControls[def.Key] as Picker) is { SelectedIndex: > 0 } p
                    ? StyleLibrarySupport.Parse(def, enumOptions[def.Key][p.SelectedIndex])
                    : null,
                StyleFieldKind.Enum => (fieldControls[def.Key] as Picker) is { SelectedIndex: > 0 } p
                    ? StyleLibrarySupport.Parse(def, enumOptions[def.Key][p.SelectedIndex])
                    : null,
                _ => StyleLibrarySupport.Parse(def, (fieldControls[def.Key] as Entry)!.Text ?? string.Empty),
            };

            try
            {
                _designer.Kernel.UpdateStyle(currentId, "修改样式 " + def.LabelZh, s => StyleLibrarySupport.SetValue(s, def.Key, value));
                ShowMessage(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        }

        foreach (var def in StyleLibrarySupport.Fields)
        {
            switch (fieldControls[def.Key])
            {
                case Entry e:
                    e.Unfocused += (_, _) => CommitField(def);
                    e.Completed += (_, _) => CommitField(def);
                    break;
                case Picker p:
                    p.SelectedIndexChanged += (_, _) => { if (!syncing) CommitField(def); };
                    break;
            }
        }

        addBtn.Clicked += (_, _) =>
        {
            try
            {
                var style = _designer.Kernel.AddStyle();
                ShowMessage(null);
                RefreshList(style.Id);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        };

        delBtn.Clicked += (_, _) =>
        {
            if (currentId is null)
                return;
            try
            {
                _designer.Kernel.RemoveStyle(currentId);
                ShowMessage(null);
                RefreshList(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        };

        foreach (var def in StyleLibrarySupport.Fields.Where(f => f.Kind == StyleFieldKind.Bool || f.Kind == StyleFieldKind.Enum))
        {
            if (fieldControls[def.Key] is Picker p && def.EnumType is { } t)
            {
                var opts = def.Kind == StyleFieldKind.Bool
                    ? new[] { StyleLibrarySupport.Unset, "true", "false" }
                    : new[] { StyleLibrarySupport.Unset }.Concat(Enum.GetNames(t)).ToArray();
                enumOptions[def.Key] = opts;
                p.ItemsSource = opts;
                p.SelectedIndex = 0;
            }
        }

        RefreshList(_designer.Kernel.Template.Styles.Count > 0 ? _designer.Kernel.Template.Styles[0].Id : null);

        _menuOverlay.Children.Clear();
        _menuOverlay.Add(backdrop);
        _menuOverlay.Add(card);
        _menuOverlay.IsVisible = true;
    }

    void CloseStyleLibrary()
    {
        _menuOverlay.IsVisible = false;
        _menuOverlay.Children.Clear();
        _designer.TakeFocus();
    }

    // ===== 阶段 E/F：解决方案持久化 + 导出/打印 =====

    void MarkSolutionDirty()
    {
        _session.IsDirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        Title = $"{Localizer.T("App.Title.Short")} — MAUI — {_session.CurrentName}{(_session.IsDirty ? "*" : "")}";
    }

    void NewSolution()
    {
        _designer.LoadTemplate(PrintTemplate.CreateDefault(PaperTypePresets.A4));
        BindKernel("未命名模板");
        _session.New();
        SetStatus(Localizer.T("Status.NewSolution"));
        UpdateTitle();
    }

    async Task OpenSolutionDialog()
    {
        var records = _session.List();
        string? selectedId = null;
        var stack = new VerticalStackLayout { Spacing = 2, Margin = new Thickness(8) };
        foreach (var record in records)
        {
            var id = record.Id;
            var btn = new Button { Text = record.Name, HorizontalOptions = LayoutOptions.Start };
            btn.Clicked += (_, _) => selectedId = id;
            stack.Children.Add(btn);
        }

        if (records.Count == 0)
            stack.Children.Add(new Label { Text = "（暂无已保存模板）" });

        var confirmed = await _modal.ShowAsync(
            Localizer.T("Dialog.OpenSolution"),
            stack,
            validate: () => selectedId is null ? Localizer.T("Dialog.SelectRequired") : null);
        if (confirmed && selectedId is not null)
            OpenSolution(selectedId);
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
        return true;
    }

    async Task<string?> PromptNameAsync(string defaultName)
    {
        var textBox = new Entry { Text = defaultName, WidthRequest = 320, Margin = new Thickness(8) };
        var confirmed = await _modal.ShowAsync(
            Localizer.T("Dialog.SaveAs"),
            new VerticalStackLayout { Children = { textBox } },
            validate: () =>
            {
                var t = (textBox.Text ?? string.Empty).Trim();
                return t.Length == 0 ? Localizer.T("Dialog.NameRequired") : null;
            });
        if (!confirmed)
            return null;
        var result = (textBox.Text ?? string.Empty).Trim();
        return result.Length == 0 ? null : result;
    }

    async Task OnExportJson()
    {
        var json = TemplateJson.Serialize(_designer.Template, indented: true);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        var file = await PickSaveFileAsync("crossprint-template", ".json", Localizer.T("Dialog.Export.JsonFilter"));
        if (file is null)
        {
            SetStatus(Localizer.T("Error.ExportFailed", Localizer.T("Dialog.Cancel")), error: true);
            return;
        }

        try
        {
            await System.IO.File.WriteAllBytesAsync(file.Path, bytes);
            SetStatus(Localizer.T("Status.Exported", file.Path));
            ShowToast("已导出模板", Localizer.T("Status.Exported", file.Path));
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.ExportFailed", ex.Message), error: true);
        }
    }

    /// <summary>Windows 文件保存选择器（与 WinUi 示例同口径），返回用户选定文件或取消时的 null。</summary>
    async Task<Windows.Storage.StorageFile?> PickSaveFileAsync(string suggestedName, string extension, string filterLabel)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(filterLabel, new List<string> { extension });

        if (this.Handler?.PlatformView is Microsoft.UI.Xaml.Window xamlWindow)
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(xamlWindow));

        return await picker.PickSaveFileAsync();
    }

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
}
