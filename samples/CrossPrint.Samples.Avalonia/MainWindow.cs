using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Avalonia;
using CrossPrint.I18n;
using CrossPrint.Printing;
using SkiaSharp;
using EditorPointerPoint = CrossPrint.Designer.PointerPoint;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 示例主视图：顶部工具栏 + 左侧纸张栏/工具箱（可拖宽）+ 中间设计画布 + 右侧属性面板（可拖宽）+ 可折叠模板 JSON 带 + 底部状态栏。
/// 用 UserControl 而非 Window：浏览器单视图生命周期不支持 WindowingPlatform（连 new Window 都会抛），
/// 桌面版由 MainWindow 薄包装持有本视图。
/// </summary>
public sealed class MainView : UserControl, IModalOverlayHost
{
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

    static readonly (string Label, string Key)[] ElementButtons =
    [
        (Localizer.T("Element.Text"), ElementTypeKeys.Text),
        (Localizer.T("Element.Line"), ElementTypeKeys.Line),
        (Localizer.T("Element.Rectangle"), ElementTypeKeys.Rectangle),
        (Localizer.T("Element.Ellipse"), ElementTypeKeys.Ellipse),
        (Localizer.T("Element.Shape"), ElementTypeKeys.Shape),
        (Localizer.T("Element.Image"), ElementTypeKeys.Image),
        (Localizer.T("Element.QrCode"), ElementTypeKeys.QrCode),
        (Localizer.T("Element.Barcode"), ElementTypeKeys.Barcode),
        (Localizer.T("Element.Table"), ElementTypeKeys.Table),
    ];

    readonly DesignerControl _designer;
    readonly PropertyPanel _properties;
    readonly TextBlock _status = new()
    {
        Margin = new Thickness(8, 4),
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
    };

    readonly ComboBox _paperCombo = new();
    readonly WrapPanel _toolbox = new() { Orientation = Orientation.Horizontal };
    readonly List<(string Key, string Label, Control Tile)> _toolboxTiles = [];
    readonly Button _rotateButton = new();
    readonly Button _customPaperButton = new();
    readonly TextBlock _paperInfo = new();
    readonly ContextMenu _canvasMenu = new();
    readonly List<MenuItem> _selectionMenuItems = [];
    MenuItem _verticalCenterItem = null!;
    MenuItem _rotatePaperItem = null!;

    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;
    GridSplitter _jsonSplitter = null!;

    bool _syncingPaper;
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;

    Button _undoButton = null!;
    Button _redoButton = null!;

    readonly TabControl _leftTabs = new();
    readonly TextBlock _zoomText = new();
    readonly TextBlock _selectionText = new();
    readonly TextBlock _saveText = new();
    readonly TextBlock _dimensionText = new();

    readonly Panel _modalLayer = new Panel();

    // ---- 解决方案（模板持久化）----
    readonly ITemplateStore _store = TemplateStoreFactory.Create();
    readonly ListBox _solutionList = new();
    readonly TextBlock _solutionTitle = new();
    string? _solutionId;                    // null = 当前模板还没进解决方案
    string _solutionName = "未命名模板";
    bool _dirty;
    bool _syncingSolutionList;

    /// <summary>浏览器单视图的模态 overlay 挂载层（桌面端不用，保持空）。</summary>
    public Panel? ModalLayer => _modalLayer;

    /// <summary>创建主视图并加载 kitchen-sink 示例模板。窗口标题/尺寸等宿主属性见 MainWindow 包装。</summary>
    public MainView()
    {
        var template = LoadSampleTemplate(out var warnings);
        _designer = new DesignerControl(template);
#if BROWSER
        // 画布文字走 CrossPrint 自己的 IFontResolver，浏览器上系统字体不可用，落到内嵌 Noto Sans SC。
        _designer.FontResolver = EmbeddedFonts.CreateCanvasResolver();
#endif

        // kitchen-sink 的正文/二维码/一维码是 {{title}} 等占位符；设计态也绑样例数据，
        // 画布上才看得见内容（占位符本身仍在模板里，属性面板照常显示原文）。
        _designer.Data = SampleData.Create();

        _properties = new PropertyPanel(_designer.Kernel);
        // 水印/页面设置在右侧属性栏的无选中空白区（用户预期：水印像元素属性一样在右栏）。
        _properties.EmptyStateContentBuilder = BuildWatermarkSection;
        // 「测试数据」行显示画布当前生效值（元素测试数据 > 宿主数据 > 模板设计数据）——
        // 与 EditorCanvas.Draw 的合并口径逐字一致（同一个 BindText 解析器）。
        _properties.CanvasDataProvider = () => DataBinder.WithDesignData(
            _designer.Data, _designer.Kernel.Template.BuildDesignBindingData());

        BuildCanvasContextMenu();
        Content = new Grid { Children = { BuildRoot(), _modalLayer } };

        BindKernel();
        _designer.Kernel.ViewChanged += UpdateLiveStatus;
        UpdateLiveStatus();
        _designer.PointerPressed += OnDesignerPointerPressed;
        _designer.KeyDown += OnDesignerKeyDown;

#if BROWSER
        bool hintShown = false;
        // HTML splash 的收起时机从 JS 挪到这里：WASM 运行时启动成功 ≠ 界面已画出第一帧，
        // 之前 splash 一关用户看到的是空白。挂树后再让首帧渲染出来，然后通知 JS 收起遮罩。
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(200); // 等第一帧实际渲染
            BrowserBoot.MarkReady();
            if (!hintShown)
            {
                hintShown = true;
                _ = ModalDialog.ShowAsync(this, "提示", new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "浏览器端不支持自动保存，请注意手动保存：Ctrl+S 或左侧「解决方案」面板（保存在本机浏览器中）。",
                }, 420, 200, 340, 160, canResize: false);
            }
        });
#endif

        // Ctrl+S 全局保存（画布/属性框/列表哪里有焦点都能存）。
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } topLevel)
                topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        };

#if !BROWSER
        // 桌面自动保存：30s 一拍，有脏改动且已入解决方案才落库；未保存的新模板不打扰（走 Ctrl+S 另存为）。
        var autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        autoSave.Tick += (_, _) =>
        {
            if (_dirty && _solutionId is not null)
            {
                _ = SaveSolutionCoreAsync(true);
                SetStatus("已自动保存到解决方案。");
                Toast.Show(this, "已自动保存", $"「{_solutionName}」已自动保存到解决方案。");
            }
        };
        autoSave.Start();
#endif

        _status.Text = warnings.Count == 0
            ? Localizer.T("App.Status.Ready")
            : Localizer.T("App.Status.Warnings", string.Join("；", warnings));
    }

    /// <summary>新 UI 骨架：顶部蓝条（标题 + 搜索 + 文件菜单）→ 工具栏 → 主体（左分页面板 | 画布 | 右属性）→ 模板 JSON 带 → 状态栏。</summary>
    Control BuildRoot()
    {
        var root = new DockPanel();

        // 顶部蓝白标题条。
        var topBar = BuildTopBar();
        DockPanel.SetDock(topBar, Dock.Top);
        root.Children.Add(topBar);

        // 工具栏（元素添加已收入左侧组件库，这里保留编辑/视图/导出等所有操作）。
        var toolbar = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BuildToolbar(),
            },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // 底部：模板 JSON 面板（可折叠）→ 状态栏。停靠顺序决定状态栏在最底。
        var jsonPanel = BuildTemplateJsonPanel();
        DockPanel.SetDock(jsonPanel, Dock.Bottom);
        root.Children.Add(jsonPanel);

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // 主体：左分页面板（可拖）| 画布 | 右属性（可拖）。
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 180 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 240 });

        body.Children.Add(BuildLeftPanel());

        var leftSplitter = MakeColumnSplitter();
        Grid.SetColumn(leftSplitter, 1);
        body.Children.Add(leftSplitter);

        var canvasHost = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1, 0, 1, 0),
            Child = _designer,
        };

        // 组件库图标可拖到画布：落点即元素中心。
        DragDrop.SetAllowDrop(_designer, true);
        _designer.AddHandler(DragDrop.DragOverEvent, OnCanvasDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        _designer.AddHandler(DragDrop.DropEvent, OnCanvasDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        canvasHost.SizeChanged += (_, _) =>
        {
            var b = canvasHost.Bounds;
            if (b.Width > 0 && b.Height > 0)
                _dimensionText.Text = $"{b.Width:0}×{b.Height:0}";
        };
        Grid.SetColumn(canvasHost, 2);
        body.Children.Add(canvasHost);

        var rightSplitter = MakeColumnSplitter();
        Grid.SetColumn(rightSplitter, 3);
        body.Children.Add(rightSplitter);

        Grid.SetColumn(_properties, 4);
        body.Children.Add(_properties);

        root.Children.Add(body);
        return root;
    }

    /// <summary>顶部蓝条：最左 logo（点击开关于），随后「文件/模板库/设置」菜单。</summary>
    Control BuildTopBar()
    {
        // 菜单在左侧、白字加粗：蓝底上要清晰可见。
        var menus = (Menu)BuildMenuBar();
        foreach (var item in menus.Items.OfType<MenuItem>())
        {
            item.Foreground = Brushes.White;
            item.FontWeight = FontWeight.Bold;
        }

        // logo 按钮：程序徽标（圆角方块 CP 字母标），点击打开关于弹窗。
        var logoMark = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x51, 0x8D, 0xFF), 0),
                    new GradientStop(Color.FromRgb(0x1E, 0x4F, 0xC4), 1),
                },
            },
            BoxShadow = BoxShadows.Parse("0 1 3 0 #80000000"),
            Child = new TextBlock
            {
                Text = "CP",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var logo = new Button
        {
            Content = logoMark,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        ToolTip.SetTip(logo, Localizer.T("Menu.Help.About"));
        logo.Click += (_, _) => _ = ShowAboutAsync();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(logo, 0);
        grid.Children.Add(logo);
        Grid.SetColumn(menus, 1);
        grid.Children.Add(menus);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
            Height = 46,
            Padding = new Thickness(12, 0, 12, 0),
            Child = grid,
        };
    }

    /// <summary>底部状态栏：缩放控制 + 缩放比例 + 提示信息 + 选中数 + 保存时间 + 画布尺寸。</summary>
    Control BuildStatusBar()
    {
        // Fluent 按钮默认 Padding≈12、MinHeight≈32：26px 宽的小按钮里字符会被 padding 挤没，必须清零。
        var zoomOut = new Button
        {
            Content = "−",
            Width = 26,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        zoomOut.Click += (_, _) => _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomOut));
        var zoomIn = new Button
        {
            Content = "+",
            Width = 26,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        zoomIn.Click += (_, _) => _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomIn));
        var zoomReset = new Button
        {
            Content = "100%",
            Height = 22,
            MinHeight = 0,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(2, 0, 0, 0),
        };
        zoomReset.Click += (_, _) => _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        var zoomFit = new Button
        {
            Content = "适合窗口",
            Height = 22,
            MinHeight = 0,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(2, 0, 0, 0),
        };
        zoomFit.Click += (_, _) => FitToPage();

        var zoomBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOut, zoomIn, zoomReset, zoomFit },
        };

        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Margin = new Thickness(10, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0, 0, 0);
        _selectionText.VerticalAlignment = VerticalAlignment.Center;
        _selectionText.Margin = new Thickness(12, 0, 0, 0);
        _saveText.VerticalAlignment = VerticalAlignment.Center;
        _saveText.Margin = new Thickness(12, 0, 0, 0);
        _saveText.Text = "已就绪";
        _dimensionText.VerticalAlignment = VerticalAlignment.Center;
        _dimensionText.Margin = new Thickness(0, 0, 8, 0);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            Height = 28,
        };
        Grid.SetColumn(zoomBox, 0);
        Grid.SetColumn(_zoomText, 1);
        Grid.SetColumn(_status, 2);
        Grid.SetColumn(_selectionText, 3);
        Grid.SetColumn(_saveText, 4);
        Grid.SetColumn(_dimensionText, 5);
        grid.Children.Add(zoomBox);
        grid.Children.Add(_zoomText);
        grid.Children.Add(_status);
        grid.Children.Add(_selectionText);
        grid.Children.Add(_saveText);
        grid.Children.Add(_dimensionText);

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    /// <summary>透明背景的列分割条：透明画刷仍参与命中测试，悬停/拖拽时才有可见反馈。</summary>
    static GridSplitter MakeColumnSplitter() => new()
    {
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ResizeDirection = GridResizeDirection.Columns,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent,
    };

    /// <summary>顶部菜单栏：文件（新建/打开/最近/保存/导入）+ 模板库 + 设置（语言、关于）。</summary>
    Control BuildMenuBar()
    {
        var menu = new Menu();
        menu.Items.Add(BuildFileMenu());
        menu.Items.Add(BuildTemplatesMenu());
        menu.Items.Add(BuildSettingsMenu());
        return menu;
    }

    /// <summary>文件菜单：新建 / 打开模板 / 最近打开 / 保存 / 另存为 / hiprint 导入（样式库挪到工具条）。</summary>
    MenuItem BuildFileMenu()
    {
        var fileMenu = new MenuItem { Header = Localizer.T("Menu.File") };

        fileMenu.Items.Add(MenuItem(Localizer.T("Menu.File.New"), () => _ = NewSolutionAsync()));
        fileMenu.Items.Add(MenuItem(Localizer.T("Menu.File.Open"), () => _ = OpenSolutionDialogAsync()));

        var recent = new MenuItem { Header = Localizer.T("Menu.File.Recent") };
        recent.SubmenuOpened += (_, _) => RefreshRecentMenu(recent);
        RefreshRecentMenu(recent);
        fileMenu.Items.Add(recent);

        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(MenuItem(Localizer.T("Menu.File.Save"), () => _ = SaveSolutionAsync()));
        fileMenu.Items.Add(MenuItem(Localizer.T("Menu.File.SaveAs"), () => _ = SaveSolutionAsAsync()));

        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(MenuItem(Localizer.T("Menu.File.ImportHiprint"), () => _ = ImportHiprintAsync()));

        return fileMenu;
    }

    static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>「最近打开」子菜单：按更新时间倒序取前 8 个；展开时重建（保存/另存为后顺序会变）。</summary>
    void RefreshRecentMenu(MenuItem recent)
    {
        recent.Items.Clear();
        List<TemplateRecord> records;
        try
        {
            records = _store.List()
                .OrderByDescending(r => r.UpdatedAt)
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            recent.Items.Add(new MenuItem { Header = "读取失败：" + ex.Message, IsEnabled = false });
            return;
        }

        if (records.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = Localizer.T("Menu.File.Recent.Empty"), IsEnabled = false });
            return;
        }

        foreach (var record in records)
        {
            var captured = record;
            var item = new MenuItem { Header = $"{captured.Name} · {captured.UpdatedAt:MM-dd HH:mm}" };
            item.Click += (_, _) => _ = OpenRecordAsync(captured);
            recent.Items.Add(item);
        }
    }

    /// <summary>「打开模板」：模态列出全部解决方案模板（最近在前），双击或确定打开。</summary>
    async Task OpenSolutionDialogAsync()
    {
        List<TemplateRecord> records;
        try
        {
            records = _store.List().OrderByDescending(r => r.UpdatedAt).ToList();
        }
        catch (Exception ex)
        {
            SetStatus("读取模板列表失败：" + ex.Message, error: true);
            return;
        }

        if (records.Count == 0)
        {
            SetStatus("还没有保存过的模板（保存后会出现在这里）。");
            return;
        }

        var list = new ListBox { MinHeight = 220, MinWidth = 360 };
        foreach (var record in records)
            list.Items.Add(new ListBoxItem
            {
                Content = $"{record.Name} · {record.UpdatedAt:MM-dd HH:mm}",
                Tag = record.Id,
            });
        list.SelectedIndex = 0;

        var confirm = new Button { Content = "打开", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, confirm },
        };
        var root = new StackPanel { Spacing = 4, Children = { list, buttons } };

        TemplateRecord? chosen = null;
        void CaptureChosen()
        {
            if (list.SelectedItem is ListBoxItem item && item.Tag is string id)
                chosen = records.FirstOrDefault(r => r.Id == id);
        }

        confirm.Click += (_, _) => CaptureChosen();
        list.DoubleTapped += (_, _) => CaptureChosen();
        var close = (Action)(() => ModalDialog.CloseHost(confirm));
        confirm.Click += (_, _) => close();
        cancel.Click += (_, _) => close();
        list.DoubleTapped += (_, _) => close();

        await ModalDialog.ShowAsync(this, Localizer.T("Menu.File.Open"), root, 420, 480);
        if (chosen is { } pick)
            await OpenRecordAsync(pick);
    }

    /// <summary>模板库菜单：弹出内置模板选择窗（分类 + 卡片 + 悬停使用/预览）。</summary>
    MenuItem BuildTemplatesMenu()
    {
        var item = new MenuItem { Header = Localizer.T("Menu.Templates") };
        ToolTip.SetTip(item, Localizer.T("Menu.Templates.Tip"));
        item.Click += (_, _) => _ = TemplateLibraryDialog.Show(this, UseTemplateFromLibraryAsync);
        return item;
    }

    /// <summary>模板库「使用」：当前模板有未保存修改时先确认，防止误点覆盖编写到一半的内容。</summary>
    async Task<bool> UseTemplateFromLibraryAsync(PrintTemplate template, string note)
    {
        if (_dirty && !await ModalDialog.ConfirmAsync(this, "使用模板", $"当前模板「{_solutionName}」有未保存的修改，确认使用将覆盖。"))
            return false;
        ApplyTemplate(template, note);
        return true;
    }

    /// <summary>模板库/导入等入口共用的「换模板」流程：LoadTemplate 换内核后重接属性面板、纸张栏与 JSON 面板。</summary>
    void ApplyTemplate(PrintTemplate template, string note)
    {
        _designer.LoadTemplate(template);
        BindKernel(note);
        SetStatus(note);
        _designer.Focus();
    }

    /// <summary>设置菜单：环境偏好（语言、关于）。样式库已挪到工具条。</summary>
    MenuItem BuildSettingsMenu()
    {
        var settingsMenu = new MenuItem { Header = Localizer.T("Menu.Settings") };

#if !BROWSER
        settingsMenu.Items.Add(BuildLanguageMenu());
#endif

        var aboutItem = new MenuItem { Header = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => _ = ShowAboutAsync();
        settingsMenu.Items.Add(aboutItem);

        return settingsMenu;
    }

#if !BROWSER
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
                ToggleType = MenuItemToggleType.CheckBox,
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
                (TopLevel.GetTopLevel(this) as Window)?.Close();
                break;
            case LanguageChangeOutcome.SaveFailed:
                SetStatus(Localizer.T("Language.SaveFailed"));
                break;
            default:
                SetStatus(Localizer.T("Language.SavedRestartFailed", LocalizationSwitch.DisplayName(culture)));
                break;
        }
    }
#endif

    /// <summary>左侧分页面板：解决方案 / 组件库。解决方案放最前（工作入口），组件库其次。</summary>
    Control BuildLeftPanel()
    {
        _leftTabs.Items.Add(new TabItem { Header = "解决方案", Content = BuildSolutionPanel() });
        _leftTabs.Items.Add(new TabItem { Header = "组件库", Content = BuildComponentLibraryTab() });

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _leftTabs,
        };
    }

    /// <summary>解决方案页：当前文档名 + 已保存模板列表。操作全部走右键菜单，双击打开。</summary>
    Control BuildSolutionPanel()
    {
        _solutionTitle.FontWeight = FontWeight.SemiBold;
        _solutionTitle.Margin = new Thickness(2, 0, 0, 0);
        _solutionTitle.TextTrimming = TextTrimming.CharacterEllipsis;

        _solutionList.MaxHeight = 320;
        _solutionList.DoubleTapped += (_, _) => _ = OpenSelectedSolutionAsync();
        ToolTip.SetTip(_solutionList, "双击打开；右键更多操作（保存过的模板才会出现在这里）");
        BuildSolutionContextMenu();

        // 右键命中哪一项就先选中哪一项，菜单动作才能作用到它；随后显式打开菜单
        // （attached ContextMenu 在部分环境右键无响应，与画布菜单统一为手动 Open）。
        // 必须 handledEventsToo:true —— ListBoxItem 会把指针按下标成已处理，
        // 普通 += 订阅收不到冒泡事件，右键菜单从未真正弹出过（真机实测 2026-11-04）。
        _solutionList.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (!e.GetCurrentPoint(_solutionList).Properties.IsRightButtonPressed)
                    return;
                _contextMenuPoint = e.GetPosition(_solutionList);
                var hit = SelectedItemUnderPointer();
                if (hit is not null)
                    _solutionList.SelectedItem = hit;
                _solutionMenu.Open(_solutionList);
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        var box = new StackPanel { Margin = new Thickness(8, 6), Spacing = 5 };
        box.Children.Add(_solutionTitle);
#if BROWSER
        box.Children.Add(new TextBlock
        {
            Text = "⚠ 浏览器端不会自动保存，请注意手动保存。",
            FontSize = 11,
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
        });
#endif
        box.Children.Add(_solutionList);

        return box;
    }

    /// <summary>指针位置命中的列表项（右键选中用）。</summary>
    ListBoxItem? SelectedItemUnderPointer()
    {
        var hit = _solutionList.InputHitTest(_contextMenuPoint) as Control;
        while (hit is not null && hit is not ListBoxItem)
            hit = hit.Parent as Control;
        return hit as ListBoxItem;
    }

    Point _contextMenuPoint;

    void BuildSolutionContextMenu()
    {
        MenuItem Item(string header, Action click, string? tip = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            if (tip is not null)
                ToolTip.SetTip(item, tip);
            return item;
        }

        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(Item("新增打印单", () => _ = NewSolutionAsync(), "新建一份空白模板并开始编辑"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开", () => _ = OpenSelectedSolutionAsync(), "打开选中的模板（双击同效）"));
        menu.Items.Add(Item("保存当前（Ctrl+S）", () => _ = SaveSolutionAsync(), "把正在编辑的模板保存进解决方案"));
        menu.Items.Add(Item("另存为…", () => _ = SaveSolutionAsAsync(), "以新名称保存一份副本"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("重命名", () => _ = RenameSolutionAsync(), "重命名选中的模板"));
        menu.Items.Add(Item("删除", () => _ = DeleteSolutionAsync(), "删除选中的模板（会先确认）"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("预览", () => _ = PreviewSolutionAsync(), "弹出预览页查看选中的模板"));
        menu.Items.Add(Item("导出模板", () => _ = ExportSolutionTemplateAsync(), "导出为 .json 文件（紧凑单行格式）"));
        menu.Items.Add(Item("导出 PDF", () => _ = ExportSolutionPdfAsync(), "把选中的模板导出为 PDF"));
#if !BROWSER
        menu.Items.Add(Item("打印测试", () => PrintSolution(), "用系统打印机试打选中的模板"));
#endif
        _solutionMenu = menu;
    }

    ContextMenu _solutionMenu = new();

    /// <summary>右键菜单作用对象：当前选中的存储记录；未选中提示。</summary>
    TemplateRecord? SelectedSolutionRecord()
    {
        if (_solutionList.SelectedItem is not ListBoxItem item || item.Tag is not string id)
        {
            SetStatus("请先在列表中选择一个模板（右键项会自动选中）。");
            return null;
        }

        var record = _store.Load(id);
        if (record is null)
        {
            SetStatus("模板不存在（可能已被删除）。", error: true);
            RefreshSolutionList();
            return null;
        }
        return record;
    }

    async Task RenameSolutionAsync()
    {
        if (SelectedSolutionRecord() is not { } record)
            return;
        var name = await ModalDialog.PromptAsync(this, "重命名模板", "新名称", record.Name);
        if (string.IsNullOrWhiteSpace(name) || name == record.Name)
            return;
        try
        {
            _store.Save(record.Id, name.Trim(), record.Json);
            if (_solutionId == record.Id)
            {
                _solutionName = name.Trim();
                UpdateSolutionUi();
            }
            else
            {
                RefreshSolutionList();
            }
            SetStatus($"已重命名为「{name.Trim()}」。");
        }
        catch (Exception ex)
        {
            SetStatus("重命名失败：" + ex.Message, error: true);
        }
    }

    async Task PreviewSolutionAsync()
    {
        if (SelectedSolutionRecord() is not { } record)
            return;
        try
        {
            var json = record.Json;
            await TemplatePreviewDialog.ShowAsync(
                this, record.Name, "解决方案", null,
                () => TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant).Template,
                UseTemplateFromLibraryAsync);
        }
        catch (Exception ex)
        {
            SetStatus("预览失败：" + ex.Message, error: true);
        }
    }

    async Task ExportSolutionTemplateAsync()
    {
        if (SelectedSolutionRecord() is not { } record)
            return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus(Localizer.T("Error.NoSaveAccess"));
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出模板",
                SuggestedFileName = SanitizeFileName(record.Name) + ".json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("CrossPrint 模板") { Patterns = ["*.json"] }],
            });
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(record.Json);
            SetStatus($"已导出「{record.Name}」到 {file.Name}（紧凑格式，{record.Json.Length:N0} 字符）。");
        }
        catch (Exception ex)
        {
            SetStatus("导出模板失败：" + ex.Message, error: true);
        }
    }

    async Task ExportSolutionPdfAsync()
    {
        if (SelectedSolutionRecord() is not { } record)
            return;
        try
        {
            var template = TemplateJson.Load(record.Json, TemplateJsonLoadOptions.Tolerant).Template;
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus(Localizer.T("Error.NoSaveAccess"));
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localizer.T("Dialog.Export.Title"),
                SuggestedFileName = SanitizeFileName(record.Name) + ".pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
            });
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            var report = PdfExporter.Export(
                template,
                stream,
                data: SampleData.Create(),
                fontResolver: _designer.FontResolver,
                images: _designer.Images,
                renderer: _designer.Renderer);
            SetStatus(report.HasIssues
                ? Localizer.T("Status.PdfWithWarnings", file.Name, report.Issues.Count)
                : $"已导出「{record.Name}」为 {file.Name}。");
        }
        catch (Exception ex)
        {
            SetStatus("导出 PDF 失败：" + ex.Message, error: true);
        }
    }

    void PrintSolution()
    {
        if (SelectedSolutionRecord() is not { } record)
            return;
        if (_printing)
        {
            SetStatus(Localizer.T("Status.PrintingBusy"), error: true);
            return;
        }

        try
        {
            _printing = true;
            SetStatus(Localizer.T("Status.PrintingStarting"));
            var template = TemplateJson.Load(record.Json, TemplateJsonLoadOptions.Tolerant).Template;
            var issues = new RenderReport();
            var document = new PrintDocument(template, SampleData.Create());
            _ = PrintToSystemAsync(document, issues);
        }
        catch (Exception ex)
        {
            _printing = false;
            SetStatus(Localizer.T("Error.PrintFailed", ex.Message), error: true);
        }
    }


    // ---- 解决方案操作 ----

    void ResetSolutionState()
    {
        _solutionId = null;
        _solutionName = "未命名模板";
        UpdateSolutionUi();
    }

    void UpdateSolutionUi()
    {
        _solutionTitle.Text = _solutionName + (_solutionId is null ? "（未保存）" : (_dirty ? " ●未保存" : ""));
        RefreshSolutionList();
    }

    void RefreshSolutionList()
    {
        _syncingSolutionList = true;
        try
        {
            _solutionList.Items.Clear();
            foreach (var record in _store.List())
                _solutionList.Items.Add(new ListBoxItem
                {
                    Content = $"{record.Name} · {record.UpdatedAt:MM-dd HH:mm}",
                    Tag = record.Id,
                });
        }
        catch (Exception ex)
        {
            SetStatus("读取解决方案列表失败：" + ex.Message, error: true);
        }
        finally
        {
            _syncingSolutionList = false;
        }
    }

    /// <summary>把当前内核模板写进存储（紧凑 JSON）。调用方保证 _solutionId 非 null 或已走另存为。</summary>
    Task SaveSolutionCoreAsync(bool silent)
    {
        var json = TemplateJson.Serialize(_designer.Kernel.Template, indented: false);
        _solutionId = _store.Save(_solutionId, _solutionName, json);
        MarkSaved();
        UpdateSolutionUi();
        if (!silent)
        {
            SetStatus($"已保存到解决方案「{_solutionName}」。");
            Toast.Show(this, "保存成功", $"已保存到解决方案「{_solutionName}」。");
        }
        return Task.CompletedTask;
    }

    /// <summary>Ctrl+S / 保存按钮：已入解决方案直接覆盖保存，否则走另存为。</summary>
    async Task SaveSolutionAsync()
    {
        if (_solutionId is null)
        {
            await SaveSolutionAsAsync();
            return;
        }
        await SaveSolutionCoreAsync(false);
        _designer.Focus();
    }

    async Task<bool> SaveSolutionAsAsync()
    {
        var initial = _solutionName == "未命名模板" ? string.Empty : _solutionName;
        var name = await ModalDialog.PromptAsync(this, "保存到解决方案", "模板名称", initial);
        if (string.IsNullOrEmpty(name))
            return false;
        _solutionName = name;
        await SaveSolutionCoreAsync(false);
        _designer.Focus();
        return true;
    }

    /// <summary>退出前保存：保存过的直接落盘；没保存过的先走另存为。用户取消另存为返回 false（不退出）。</summary>
    public async Task<bool> SaveForExitAsync()
    {
        if (_solutionId is null)
            return await SaveSolutionAsAsync();
        await SaveSolutionCoreAsync(false);
        return true;
    }

    async Task NewSolutionAsync()
    {
        if (_dirty && !await ModalDialog.ConfirmAsync(this, "新建模板", $"当前模板「{_solutionName}」有未保存的修改，确认放弃并新建？"))
            return;
        _designer.LoadTemplate(PrintTemplate.CreateDefault(PaperTypePresets.A4));
        BindKernel("已新建空白模板。");
        SetStatus("已新建空白模板（保存后才加入解决方案）。");
        _designer.Focus();
    }

    async Task OpenSelectedSolutionAsync()
    {
        if (_syncingSolutionList)
            return;
        if (_solutionList.SelectedItem is not ListBoxItem item || item.Tag is not string id)
        {
            SetStatus("请先在列表中选择要打开的模板。");
            return;
        }

        var record = _store.Load(id);
        if (record is null)
        {
            SetStatus("模板不存在（可能已被删除）。", error: true);
            RefreshSolutionList();
            return;
        }

        await OpenRecordAsync(record);
    }

    /// <summary>打开一条存储记录（文件菜单/最近打开/左侧列表共用）：脏确认 → 载入 → 恢复文档身份。</summary>
    async Task OpenRecordAsync(TemplateRecord record)
    {
        if (_dirty)
        {
            // 「确定」= 先保存当前修改再打开；「取消」= 放弃修改直接打开。
            if (await ModalDialog.ConfirmAsync(this, "打开模板",
                    $"当前模板「{_solutionName}」有未保存的修改。确定=先保存再打开，取消=放弃修改直接打开。"))
            {
                await SaveSolutionAsync();
            }
        }

        try
        {
            var template = TemplateJson.Load(record.Json, TemplateJsonLoadOptions.Tolerant).Template;
            _designer.LoadTemplate(template);
            BindKernel($"已打开「{record.Name}」。"); // 内部会 ResetSolutionState，之后再恢复文档身份
            _solutionId = record.Id;
            _solutionName = record.Name;
            UpdateSolutionUi();
            SetStatus($"已打开「{record.Name}」。");
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetStatus("打开模板失败：" + ex.Message, error: true);
        }
    }

    async Task DeleteSolutionAsync()
    {
        if (_solutionList.SelectedItem is not ListBoxItem item || item.Tag is not string id)
        {
            SetStatus("请先在列表中选择要删除的模板。");
            return;
        }

        var name = (item.Content as string)?.Split(" · ")[0] ?? id;
        if (!await ModalDialog.ConfirmAsync(this, "删除模板", $"确认删除「{name}」？该操作不可撤销。"))
            return;

        try
        {
            _store.Delete(id);
            if (_solutionId == id)
                _solutionId = null;
            UpdateSolutionUi();
            SetStatus($"已删除「{name}」。");
        }
        catch (Exception ex)
        {
            SetStatus("删除失败：" + ex.Message, error: true);
        }
    }

    /// <summary>组件库：纸张设置 + 可拖入画布的元素清单（顶部搜索框可过滤）。</summary>
    Control BuildComponentLibraryTab()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(BuildPaperPanel());
        stack.Children.Add(BuildToolbox());
        return new ScrollViewer { Content = stack };
    }

    /// <summary>数据类型：模板可用占位符字段，双击复制占位符（{{字段名}}）到剪贴板。</summary>
    Control BuildPaperPanel()
    {
        _paperCombo.ItemsSource = PaperChoices.Select(choice => choice.Label).ToList();
        _paperCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperCombo.SelectionChanged += (_, _) => OnPaperChoiceChanged();

        _rotateButton.Content = Localizer.T("Paper.Rotate");
        _rotateButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rotateButton.Margin = new Thickness(0, 0, 2, 0);
        _rotateButton.Click += (_, _) => TogglePaperOrientation();

        _customPaperButton.Content = Localizer.T("Paper.CustomBtn");
        _customPaperButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _customPaperButton.Margin = new Thickness(2, 0, 0, 0);
        _customPaperButton.Click += (_, _) => _ = ShowCustomPaperDialogAsync();

        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto"),
            Margin = new Thickness(0, 2),
        };
        buttons.Children.Add(_rotateButton);
        Grid.SetColumn(_customPaperButton, 1);
        buttons.Children.Add(_customPaperButton);

        _paperInfo.TextWrapping = TextWrapping.Wrap;
        _paperInfo.Foreground = Brushes.DimGray;
        _paperInfo.FontSize = 12;
        _paperInfo.Margin = new Thickness(0, 2, 0, 0);

        // 不用 GroupBox 式外框，节标题 + 紧凑留白就够：侧栏被画布和属性面板夹着，竖向空间宝贵。
        var content = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(8, 4, 8, 6),
        };
        content.Children.Add(new TextBlock
        {
            Text = Localizer.T("Paper.Section"),
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        });
        content.Children.Add(_paperCombo);
        content.Children.Add(buttons);
        content.Children.Add(_paperInfo);

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };
    }

    CheckBox _watermarkEnabled = null!;
    TextBox _watermarkText = null!;
    TextBox _watermarkBinding = null!;
    TextBox _watermarkFontSize = null!;
    TextBox _watermarkOpacity = null!;
    TextBox _watermarkRotation = null!;
    TextBox _watermarkSpacing = null!;
    ColorField.Handle _watermarkColor = null!;
    bool _syncingWatermark;

    /// <summary>页面属性的水印设置区：启用/文字/字号/颜色/透明度/角度/间距，全部走内核命令可撤销。</summary>
    Control BuildWatermarkSection()
    {
        _watermarkEnabled = new CheckBox { Content = "启用水印（文字平铺斜纹）", Margin = new Thickness(0, 2, 0, 0) };
        _watermarkEnabled.IsCheckedChanged += (_, _) =>
        {
            if (_syncingWatermark)
                return;
            var value = _watermarkEnabled.IsChecked == true;
            CommitWatermark("启用水印", w => w.Enabled = value, w => w.Enabled = !value);
        };

        _watermarkText = new TextBox
        {
            Watermark = "水印文字，支持 {{字段}} 绑定",
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _watermarkText.LostFocus += (_, _) => CommitWatermarkText();
        _watermarkText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitWatermarkText();
            }
        };

        // 字段绑定：与元素属性同一套语义——绑定值优先于文字内容（纯字段名自动包 {{}}），
        // 设计态测试数据优先显示，打印时测试数据仅作缺字段兜底。
        // 「测试数据」框已撤（用户反馈与「文字」重复）：模型字段保留，渲染兜底只走既有 JSON/导入值。
        _watermarkBinding = new TextBox
        {
            Watermark = "纯字段名或 {{字段}}，留空 = 不绑定",
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _watermarkBinding.LostFocus += (_, _) =>
            CommitWatermarkField("水印字段绑定", _watermarkBinding, w => w.Binding, (w, v) => w.Binding = v);
        _watermarkBinding.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitWatermarkField("水印字段绑定", _watermarkBinding, w => w.Binding, (w, v) => w.Binding = v);
            }
        };

        _watermarkFontSize = new TextBox { Watermark = "14" };
        _watermarkOpacity = new TextBox { Watermark = "0.2" };
        _watermarkRotation = new TextBox { Watermark = "-30" };
        _watermarkSpacing = new TextBox { Watermark = "60" };
        _watermarkFontSize.LostFocus += (_, _) => CommitWatermarkNumber("水印字号", "FontSizePt", 14, 4, 200, _watermarkFontSize);
        _watermarkOpacity.LostFocus += (_, _) => CommitWatermarkNumber("水印透明度", "Opacity", 0.2, 0, 1, _watermarkOpacity);
        _watermarkRotation.LostFocus += (_, _) => CommitWatermarkNumber("水印角度", "RotationDeg", -30, -90, 90, _watermarkRotation);
        _watermarkSpacing.LostFocus += (_, _) => CommitWatermarkNumber("水印间距", "SpacingMm", 60, 4, 400, _watermarkSpacing);
        _watermarkFontSize.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitWatermarkNumber("水印字号", "FontSizePt", 14, 4, 200, _watermarkFontSize); } };
        _watermarkOpacity.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitWatermarkNumber("水印透明度", "Opacity", 0.2, 0, 1, _watermarkOpacity); } };
        _watermarkRotation.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitWatermarkNumber("水印角度", "RotationDeg", -30, -90, 90, _watermarkRotation); } };
        _watermarkSpacing.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitWatermarkNumber("水印间距", "SpacingMm", 60, 4, 400, _watermarkSpacing); } };

        _watermarkColor = ColorField.Create("#333333", hex =>
        {
            if (!SKColor.TryParse((hex ?? string.Empty).Trim(), out var color))
                return;
            var old = _designer.Kernel.Template.Watermark.Color; // 撤销快照必须在提交前抓
            CommitWatermark("水印颜色", w => w.Color = color, w => w.Color = old);
        }, "点击打开取色面板");

        Control Field2(string label, Control editor)
        {
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("52,*") };
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Brushes.DimGray });
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            return grid;
        }

        var numbers = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*"), Margin = new Thickness(0, 2) };
        var sizeHost = Field2("字号", _watermarkFontSize);
        var opacityHost = Field2("透明度", _watermarkOpacity);
        numbers.Children.Add(sizeHost);
        Grid.SetColumn(opacityHost, 1);
        opacityHost.Margin = new Thickness(6, 0, 0, 0);
        numbers.Children.Add(opacityHost);

        var angleRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*"), Margin = new Thickness(0, 2) };
        var rotationHost = Field2("角度", _watermarkRotation);
        var spacingHost = Field2("间距", _watermarkSpacing);
        angleRow.Children.Add(rotationHost);
        Grid.SetColumn(spacingHost, 1);
        spacingHost.Margin = new Thickness(6, 0, 0, 0);
        angleRow.Children.Add(spacingHost);

        var colorHost = Field2("颜色", _watermarkColor.Root);

        var section = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = "水印", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 2) },
                _watermarkEnabled,
                Field2("文字", _watermarkText),
                Field2("字段绑定", _watermarkBinding),
                numbers,
                angleRow,
                colorHost,
            },
        };
        RefreshWatermarkControls();
        return section;
    }

    void CommitWatermarkText()
    {
        var text = _watermarkText.Text ?? string.Empty;
        var old = _designer.Kernel.Template.Watermark.Text; // 撤销快照必须在提交前抓
        CommitWatermark("水印文字", w => w.Text = text, w => w.Text = old);
    }

    /// <summary>
    /// 水印可选字符串字段（测试数据/字段绑定）提交：空串归一为 null（不设置），
    /// 值没变不推命令（失焦即提交的编辑框，点一下就走这里，不灌撤销栈）。
    /// </summary>
    void CommitWatermarkField(
        string label,
        TextBox box,
        Func<WatermarkOptions, string?> current,
        Action<WatermarkOptions, string?> apply)
    {
        var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        var old = current(_designer.Kernel.Template.Watermark); // 撤销快照必须在提交前抓
        if (string.Equals(old, value, StringComparison.Ordinal))
            return;
        CommitWatermark(label, w => apply(w, value), w => apply(w, old));
    }

    void CommitWatermarkNumber(string label, string property, double fallback, double min, double max, TextBox box)
    {
        if (!double.TryParse((box.Text ?? string.Empty).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            SetStatus($"{label}：请输入 {min}~{max} 的数字。", error: true);
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
            _watermarkFontSize.Text = w.FontSizePt.ToString("0.##");
            _watermarkOpacity.Text = w.Opacity.ToString("0.##");
            _watermarkRotation.Text = w.RotationDeg.ToString("0.##");
            _watermarkSpacing.Text = w.SpacingMm.ToString("0.##");
            ColorField.Update(_watermarkColor, $"#{w.Color.Red:X2}{w.Color.Green:X2}{w.Color.Blue:X2}");
        }
        finally
        {
            _syncingWatermark = false;
        }
    }

    /// <summary>组件库图标网格：每个元素一枚图标磁贴，拖到画布落在松手处，双击加到画布中心。</summary>
    Control BuildToolbox()
    {
        _toolboxTiles.Clear();
        _toolbox.Children.Clear();
        _toolbox.ItemWidth = 100;
        _toolbox.ItemHeight = 60;

        foreach (var (label, key) in ElementButtons)
            _toolbox.Children.Add(BuildToolboxTile(label, key));

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(8, 4, 8, 6),
        };

        grid.Children.Add(new TextBlock
        {
            Text = Localizer.T("Toolbox.Section"),
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        Grid.SetRow(_toolbox, 1);
        grid.Children.Add(_toolbox);

        var hint = new TextBlock
        {
            Text = "拖到画布放置，或双击图标加到画布中心。",
            FontSize = 11,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(hint, 2);
        grid.Children.Add(hint);

        return grid;
    }

    /// <summary>单枚图标磁贴：图标 + 名称，可拖拽到画布（落点即添加位置），双击加到中心。</summary>
    Control BuildToolboxTile(string label, string typeKey)
    {
        var icon = DesignerIcons.Stroke(typeKey, new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)), 22, 1.5);

        var tile = new Border
        {
            Margin = new Thickness(2),
            Padding = new Thickness(4, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        ToolTip.SetTip(tile, "拖到画布放置：" + label);

        Point dragStart = default;
        var armed = false;
        var placing = false;

        // 拖拽放置：按下磁贴即捕获指针，越过阈值后进入内核放置态（画布上跟一个幽灵框），
        // 松手在落点生成元素。指针被磁贴捕获，鼠标移出磁贴后仍能持续收到 Move/Release。
        EditorPointerPoint ToDesignerPointer(Point p)
        {
            var dpi = 96.0 * _designer.RenderScaling;
            return new EditorPointerPoint(p.X, p.Y, dpi, PointerButton.Primary);
        }

        tile.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                return;

            dragStart = e.GetPosition(tile);
            armed = true;
            placing = false;
            e.Pointer.Capture(tile);
        };

        tile.PointerMoved += (_, e) =>
        {
            if (!armed)
                return;

            var local = e.GetPosition(tile);
            if (!placing)
            {
                if (Math.Abs(local.X - dragStart.X) < 4 && Math.Abs(local.Y - dragStart.Y) < 4)
                    return;

                var start = ToDesignerPointer(e.GetPosition(_designer));
                placing = _designer.Kernel.BeginPlacing(typeKey, start);
                if (placing)
                    _designer.Kernel.OnPointerMoved(start);
                return;
            }

            _designer.Kernel.OnPointerMoved(ToDesignerPointer(e.GetPosition(_designer)));
        };

        tile.PointerReleased += (_, e) =>
        {
            if (placing)
                _designer.Kernel.OnPointerReleased(ToDesignerPointer(e.GetPosition(_designer)));

            armed = false;
            placing = false;
            e.Pointer.Capture(null);
        };

        tile.DoubleTapped += (_, _) => AddElementAtCenter(typeKey, label);

        _toolboxTiles.Add((typeKey, label, tile));
        return tile;
    }

    /// <summary>把指定元素加到画布中心（双击磁贴时用）。</summary>
    void AddElementAtCenter(string typeKey, string label)
    {
        _designer.Kernel.AddElementAtCenter(typeKey);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ElementAddedCenter", label));
    }

    // Avalonia 11.3 起拖放载荷改用异步 IAsyncDataTransfer（Contains/TryGetTextAsync）。
    // 迁移会把 void 事件处理器改成 async void，而载荷的有效期与真机拖放时序无法无头实证；
    // 这里保留同步旧 API 并局部抑制过时警告，行为一字不改，迁移留到真机联调批次。
#pragma warning disable CS0618
    /// <summary>画布拖入经过：接受文本载荷（元素类型键）即显示「复制」光标。</summary>
    void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>画布拖入松手：把类型键换算成页面 mm 落点后添加元素。</summary>
    void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        var key = e.Data.Get(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(key))
            return;

        var pos = e.GetPosition(_designer);
        var dpi = 96.0 * _designer.RenderScaling;
        var mm = _designer.Kernel.Viewport.ToMm(pos.X, pos.Y, dpi);
        _designer.Kernel.AddElementAt(key, mm.X, mm.Y);
        _designer.Focus();
        e.Handled = true;

        var entry = ElementButtons.FirstOrDefault(b => b.Key == key);
        SetStatus(Localizer.T("Status.ElementAddedCenter", entry.Label ?? key));
    }
#pragma warning restore CS0618

    Control BuildToolbar()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4) };

        panel.Children.Add(Separator());
        // 标签尽量短（完整说明放 ToolTip）：工具带项目多，窄窗口换行时省横向空间。
        _undoButton = ToolbarButton("撤销", DoUndo, "撤销上一步操作（Ctrl+Z），最多保留 200 步");
        _redoButton = ToolbarButton("重做", DoRedo, "重做上一步被撤销的操作（Ctrl+Y / Ctrl+Shift+Z）");
        panel.Children.Add(_undoButton);
        panel.Children.Add(_redoButton);
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.Delete.Tip")));

        panel.Children.Add(Separator());
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.BringForward"), () => RunKernel(kernel => kernel.BringForward()), Localizer.T("Toolbar.BringForward.Tip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.SendBackward"), () => RunKernel(kernel => kernel.SendBackward()), Localizer.T("Toolbar.SendBackward.Tip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.BringToFront"), () => RunKernel(kernel => kernel.BringToFront()), Localizer.T("Toolbar.BringToFront.Tip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.SendToBack"), () => RunKernel(kernel => kernel.SendToBack()), Localizer.T("Toolbar.SendToBack.Tip")));

        // 「适合窗口/100%」不再放顶部工具条：与底部状态栏的缩放组（−/+/100%/适合窗口）完全重复，
        // 且工具条换行时会和画布标尺挤在一起（用户反馈 2026-11-04）；画布右键菜单里仍保留同名项。

        panel.Children.Add(Separator());
        panel.Children.Add(Toggle(Localizer.T("Toolbar.Grid"), true, value =>
        {
            _designer.Overlay.Options.ShowGrid = value;
            _designer.Redraw();
        }));
        panel.Children.Add(Toggle(Localizer.T("Toolbar.Ruler"), true, value =>
        {
            _designer.Overlay.Options.ShowRulers = value;
            _designer.Redraw();
        }));
        panel.Children.Add(Toggle(Localizer.T("Toolbar.SafeArea"), true, value =>
        {
            _designer.Overlay.Options.ShowMarginGuides = value;
            _designer.Redraw();
        }));

        panel.Children.Add(Separator());
        // 样式库从「设置」菜单挪到工具条（用户要求放到外面）；hiprint 导入只留在文件菜单。
        panel.Children.Add(ToolbarButton(Localizer.T("Menu.File.StyleLibrary"), () => _ = StyleLibraryDialog.Show(this, _designer.Kernel), Localizer.T("Menu.File.StyleLibraryTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.ExportPdf"), () => _ = ExportPdfAsync(), Localizer.T("Toolbar.ExportPdf.Tip")));
        panel.Children.Add(ToolbarButton("导出模板", () => _ = ExportTemplateAsync(), "把当前模板导出为 .json 文件（紧凑单行格式，便于分享/备份）"));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.Preview.Tip")));
#if !BROWSER
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Print"), PrintToSystem, Localizer.T("Toolbar.Print.Tip")));
#endif

        return panel;
    }

    static Button ToolbarButton(string text, Action onClick, string? tooltip = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(2, 0) };
        button.Click += (_, _) => onClick();
        if (tooltip is not null)
            ToolTip.SetTip(button, tooltip);
        return button;
    }

    static Control Separator() => new Border
    {
        Width = 1,
        Height = 20,
        Margin = new Thickness(6, 2),
        Background = Brushes.LightGray,
    };

    static CheckBox Toggle(string text, bool initial, Action<bool> changed)
    {
        var box = new CheckBox
        {
            Content = text,
            IsChecked = initial,
            Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    void AddElement(string typeKey)
    {
        _designer.Kernel.AddElementAtCenter(typeKey);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ElementAdded", typeKey));
    }

    void DeleteSelection() => _ = DeleteSelectionWithConfirmAsync();

    /// <summary>删除选中元素：先弹确认，防止误删；确认后走内核命令（可撤销恢复）。</summary>
    async Task DeleteSelectionWithConfirmAsync()
    {
        var kernel = _designer.Kernel;
        if (kernel.Selection.IsEmpty)
            return;

        var count = kernel.Selection.Ids.Count;
        var subject = count == 1
            ? (kernel.Template.FindElement(kernel.Selection.PrimaryId!)?.TypeKey ?? "1 个元素")
            : $"{count} 个元素";
        if (!await ModalDialog.ConfirmAsync(this, "删除元素", $"确认删除选中的{subject}？删除后可用撤销（Ctrl+Z）恢复。"))
            return;

        kernel.DeleteSelection();
        _designer.Focus();
        SetStatus($"已删除{subject}（可撤销）。");
    }

    void DoUndo()
    {
        if (!_designer.Kernel.Undo())
        {
            SetStatus("没有可撤销的操作。");
            return;
        }
        _designer.Focus();
        SetStatus("已撤销。");
    }

    void DoRedo()
    {
        if (!_designer.Kernel.Redo())
        {
            SetStatus("没有可重做的操作。");
            return;
        }
        _designer.Focus();
        SetStatus("已重做。");
    }

    /// <summary>按内核撤销栈可用性刷新按钮可用态（内核在 UI 线程触发，无需调度）。</summary>
    void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = _designer.Kernel.CanUndo;
        _redoButton.IsEnabled = _designer.Kernel.CanRedo;
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

    void RunKernel(Action<EditorKernel> action)
    {
        action(_designer.Kernel);
        _designer.Focus();
    }

    void FitToPage()
    {
        _designer.FitToPage();
        _designer.Focus();
    }

    void ZoomActual()
    {
        _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual));
        _designer.Focus();
    }

    void BuildCanvasContextMenu()
    {
        _canvasMenu.Placement = PlacementMode.Pointer;

        // 「新增打印单」入口在左侧解决方案列表的右键菜单（BuildSolutionContextMenu），
        // 画布右键只放选中/层级/对齐这类与画布相关的动作——上一批误加在这里，已撤。

        AddSelectionItem(Localizer.T("Canvas.Context.Duplicate"), "Ctrl+D", DuplicateSelection);
        AddSelectionItem(Localizer.T("Canvas.Context.Delete"), "Delete", DeleteSelection);
        _canvasMenu.Items.Add(new Separator());
        AddSelectionItem(Localizer.T("Canvas.Context.BringForward"), null, () => RunKernel(kernel => kernel.BringForward()));
        AddSelectionItem(Localizer.T("Canvas.Context.SendBackward"), null, () => RunKernel(kernel => kernel.SendBackward()));
        AddSelectionItem(Localizer.T("Canvas.Context.BringToFront"), null, () => RunKernel(kernel => kernel.BringToFront()));
        AddSelectionItem(Localizer.T("Canvas.Context.SendToBack"), null, () => RunKernel(kernel => kernel.SendToBack()));
        _canvasMenu.Items.Add(new Separator());
        AddSelectionItem(Localizer.T("Canvas.Context.AlignCenter"), null, () =>
        {
            RunKernel(kernel => kernel.CenterSelectionHorizontally());
            SetStatus(Localizer.T("Status.HAlignCenter"));
        });
        _verticalCenterItem = AddSelectionItem(Localizer.T("Canvas.Context.AlignMiddle"), null, () =>
        {
            RunKernel(kernel => kernel.CenterSelectionVertically());
            SetStatus(Localizer.T("Status.VAlignCenter"));
        });
        _canvasMenu.Items.Add(new Separator());
        AddCanvasItem(Localizer.T("Canvas.Context.FitToPage"), null, FitToPage);
        AddCanvasItem(Localizer.T("Canvas.Context.ZoomActual"), null, ZoomActual);
        _canvasMenu.Items.Add(new Separator());
        _rotatePaperItem = AddCanvasItem(Localizer.T("Canvas.Context.RotateLandscape"), null, TogglePaperOrientation);
        _canvasMenu.Items.Add(CreateCustomPaperItem());
    }

    MenuItem AddCanvasItem(string text, string? gesture, Action action)
    {
        var item = new MenuItem { Header = text };
        if (ToGesture(gesture) is { } keyGesture)
            item.InputGesture = keyGesture;

        item.Click += (_, _) =>
        {
            action();
            // 菜单闭合后焦点会回到先前控件；显式再还一次，保证 Ctrl+D/Delete 继续作用于画布。
            Dispatcher.UIThread.Post(() => _designer.Focus());
        };

        _canvasMenu.Items.Add(item);
        return item;
    }

    MenuItem AddSelectionItem(string text, string? gesture, Action action)
    {
        var item = AddCanvasItem(text, gesture, action);
        _selectionMenuItems.Add(item);
        return item;
    }

    MenuItem CreateCustomPaperItem()
    {
        var item = new MenuItem { Header = Localizer.T("Canvas.Context.CustomPaper") };
        // 等菜单关完再开模态窗，避免弹出顺序/焦点打架。
        item.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = ShowCustomPaperDialogAsync());
        return item;
    }

    static KeyGesture? ToGesture(string? text) => text switch
    {
        "Ctrl+D" => new KeyGesture(Key.D, KeyModifiers.Control),
        "Delete" => new KeyGesture(Key.Delete),
        _ => null,
    };

    void OnDesignerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_designer).Properties.IsRightButtonPressed)
            return;

        // 先按右键位置更新选中（命中元素即单选、空白即清空），菜单项才有作用对象。
        // 坐标口径与宿主一致：控件内 DIP 乘 RenderScaling 得到设备 px。
        var scaling = _designer.RenderScaling;
        var position = e.GetPosition(_designer);
        _designer.Kernel.SelectAt(new EditorPointerPoint(
            position.X * scaling,
            position.Y * scaling,
            96.0 * scaling,
            PointerButton.Secondary));

        UpdateCanvasMenuState();
        _canvasMenu.Open(_designer);

        // 不在右键按下时 Handled/Capture：让菜单和后续指针事件保持正常工作。
    }

    void UpdateCanvasMenuState()
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

        SyncPaperControls();
    }

    /// <summary>全局 Ctrl+S：无论焦点在哪（画布/属性面板/JSON 框），都保存到解决方案。</summary>
    void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // 文本框（属性面板/JSON 面板/对话框输入框）有焦点时不抢这些键：Ctrl+Z 留给文本撤销，Delete 留给编辑。
        var inTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = SaveSolutionAsync();
            return;
        }

        if (!inTextBox && e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            // Ctrl+Shift+Z = 重做（与 Ctrl+Y 等效）。
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                DoRedo();
            else
                DoUndo();
            return;
        }

        if (!inTextBox && e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            DoRedo();
            return;
        }

        // Delete/Backspace 在设计画布上删元素：tunnel 先拦下来走确认，避免直达内核绕过提醒。
        if (!inTextBox
            && e.Key is Key.Delete or Key.Back
            && _designer.IsFocused
            && !_designer.Kernel.Selection.IsEmpty)
        {
            e.Handled = true;
            _ = DeleteSelectionWithConfirmAsync();
        }
    }

    readonly List<PaperElement> _clipboard = [];

    void OnDesignerKeyDown(object? sender, KeyEventArgs e)
    {
        // 复制/粘贴/再制只在设计画布有焦点时生效：属性面板文本框里的按键不该被抢。
        if (!_designer.IsFocused
            || e.Handled
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D:
                e.Handled = true;
                DuplicateSelection();
                return;
            case Key.C:
                e.Handled = true;
                CopySelection();
                return;
            case Key.V:
                e.Handled = true;
                PasteClipboard();
                return;
        }
    }

    /// <summary>Ctrl+C：把选中元素深拷贝进进程内剪贴板（不碰系统剪贴板，元素对象没法跨应用粘贴）。</summary>
    void CopySelection()
    {
        var sources = _designer.Kernel.SelectedElements;
        if (sources.Count == 0)
        {
            SetStatus("先选中元素再复制。");
            return;
        }

        _clipboard.Clear();
        _clipboard.AddRange(sources.Select(e => e.CloneDeep()));
        SetStatus($"已复制 {_clipboard.Count} 个元素，Ctrl+V 粘贴。");
    }

    /// <summary>Ctrl+V：把剪贴板元素按 5mm 偏移落位（超出纸边自动夹回），粘贴后选中新元素，可撤销。</summary>
    void PasteClipboard()
    {
        if (_clipboard.Count == 0)
        {
            SetStatus("剪贴板是空的：先 Ctrl+C 复制元素。");
            return;
        }

        _designer.Kernel.PasteElements(_clipboard, EditorKernel.DuplicateOffsetMm, EditorKernel.DuplicateOffsetMm);
        SetStatus($"已粘贴 {_clipboard.Count} 个元素。");
    }

    void OnPaperChoiceChanged()
    {
        if (_syncingPaper || _paperCombo.SelectedIndex < 0)
            return;

        var choice = PaperChoices[_paperCombo.SelectedIndex];
        if (choice.Key is null)
        {
            // 等下拉收起后再开模态窗，避免两个弹层抢焦点。
            Dispatcher.UIThread.Post(() => _ = ShowCustomPaperDialogAsync());
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
            SetStatus(Localizer.T("Error.PaperFailed", ex.Message));
        }

        SyncPaperControls();
        _designer.Focus();
    }

    async Task ShowCustomPaperDialogAsync()
    {
        try
        {
            var dialog = new PaperSizeDialog(_designer.Kernel.Template.Paper);
            await ModalDialog.ShowAsync(this, "自定义纸张", dialog, 400, 280, canResize: false);
            if (dialog.Confirmed)
            {
                _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm);
                SetStatus(dialog.HeightMm > 0
                    ? Localizer.T("Status.CustomPaperSet", dialog.WidthMm, dialog.HeightMm)
                    : Localizer.T("Status.CustomContinuousPaperSet", dialog.WidthMm));
            }
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.PaperFailed", ex.Message));
        }
        finally
        {
            SyncPaperControls();
            _designer.Focus();
        }
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus(Localizer.T("Status.ContinuousCannotRotate"));
            _designer.Focus();
            return;
        }

        try
        {
            kernel.RotatePaper();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.RotateFailed", ex.Message));
            _designer.Focus();
            return;
        }

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
            _rotateButton.IsEnabled = !continuous;
            _rotateButton.Content = paper.Orientation == PaperOrientation.Landscape ? Localizer.T("Paper.RotatePortrait") : Localizer.T("Paper.Rotate");

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

    void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

#if !BROWSER
    /// <summary>防连点：一次作业没回来之前不发起第二次系统打印。</summary>
    bool _printing;

    /// <summary>
    /// 调起系统打印（SystemPrintTarget）：渲染一份临时 PDF 后交给 OS 打印管线。
    /// 不指定打印机——Windows 走 ShellExecute「print/printto」动词，关联应用弹打印框让用户选打印机/确认份数；
    /// macOS/Linux 交给 CUPS。真实打印由系统与应用完成，本层只等「交付受理」结果。
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
        var document = new PrintDocument(_designer.Kernel.Template, SampleData.Create());
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
#endif

    /// <summary>模板加载/导入/应用后刷新状态栏的「已保存」时间戳。</summary>
    void MarkSaved()
    {
        _dirty = false;
        _saveText.Text = "已保存 " + DateTime.Now.ToString("HH:mm");
    }

    /// <summary>随内核视图变化刷新状态栏的缩放比例与选中数（缩放走 Viewport.Zoom，选中数走 Selection.Ids）。</summary>
    void UpdateLiveStatus()
    {
        _zoomText.Text = $"缩放 {_designer.Kernel.Viewport.Zoom * 100:0}%";
        _selectionText.Text = $"已选择 {_designer.Kernel.Selection.Ids.Count} 个元素";
    }

    Control BuildTemplateJsonPanel()
    {
        // 折叠开关：JSON 面板是低频的调试/导入入口，默认收起只留一条标题带，把竖向空间还给画布。
        _jsonToggle.Content = "▸ " + Localizer.T("JsonPanel.Title");
        _jsonToggle.FontWeight = FontWeight.SemiBold;
        _jsonToggle.VerticalAlignment = VerticalAlignment.Center;
        _jsonToggle.Margin = new Thickness(0, 0, 10, 0);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(_jsonToggle.IsChecked == true);

        var apply = new Button { Content = Localizer.T("JsonPanel.Apply") };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button { Content = Localizer.T("JsonPanel.Reload"), Margin = new Thickness(4, 0, 0, 0) };
        reload.Click += (_, _) => RefreshTemplateJson(Localizer.T("Status.JsonReloaded"));

        _jsonState.FontSize = 12;
        _jsonState.Foreground = Brushes.DimGray;
        _jsonState.VerticalAlignment = VerticalAlignment.Center;
        _jsonState.TextTrimming = TextTrimming.CharacterEllipsis;
        _jsonState.Margin = new Thickness(10, 0, 0, 0);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(8, 4, 8, 4),
        };
        header.Children.Add(_jsonToggle);
        Grid.SetColumn(apply, 1);
        header.Children.Add(apply);
        Grid.SetColumn(reload, 2);
        header.Children.Add(reload);
        Grid.SetColumn(_jsonState, 3);
        header.Children.Add(_jsonState);

        _templateJson.AcceptsReturn = true;
        _templateJson.TextWrapping = TextWrapping.NoWrap;
        _templateJson.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _templateJson.FontSize = 12;
        _templateJson.Margin = new Thickness(8, 0, 8, 6);
        // Fluent TextBox 自带 MinHeight≈32：折叠时行高 0 也被它顶开，JSON 会露出一条，必须清零。
        _templateJson.MinHeight = 0;
        _templateJson.TextChanged += (_, _) => OnTemplateJsonEdited();
        _templateJson.KeyDown += OnTemplateJsonKeyDown;
        ScrollViewer.SetVerticalScrollBarVisibility(_templateJson, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_templateJson, ScrollBarVisibility.Auto);

        // 展开时中间夹一道行分割条，展开高度可拖；收起时文本框和分割条一起隐藏。
        _jsonContentRow = new RowDefinition { Height = new GridLength(0) };
        _jsonSplitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Rows,
            Background = Brushes.Transparent,
            IsVisible = false,
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
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    void SetJsonPanelExpanded(bool expanded)
    {
        _jsonToggle.IsChecked = expanded;
        _jsonToggle.Content = (expanded ? "▾ " : "▸ ") + Localizer.T("JsonPanel.Title");
        _jsonContentRow.Height = new GridLength(expanded ? 230 : 0);
        // 分割条只在展开时可见：收起后留着会占 4px 高度，还能拖到一条看不见的带。
        _jsonSplitter.IsVisible = expanded;
        // 文本框折叠时整只隐藏：光靠行高收不住 MinHeight/滚动条这些内在最小需求。
        _templateJson.IsVisible = expanded;
    }

    /// <summary>把属性面板/纸张栏/JSON 面板全部接到当前内核上；LoadTemplate 换内核后必须重调。</summary>
    void BindKernel(string? jsonNote = null)
    {
        _designer.Kernel.TemplateChanged += SyncPaperControls;
        _designer.Kernel.TemplateChanged += OnKernelTemplateChanged;
        _designer.Kernel.UndoStateChanged += UpdateUndoButtons;
        _properties.Attach(_designer.Kernel);
        SyncPaperControls();
        UpdateUndoButtons();
        RefreshTemplateJson(jsonNote);
        MarkSaved();
        ResetSolutionState(); // 换了内核 = 换了文档：回到「未保存」态（所有换模板路径都经过这里）
    }

    void OnKernelTemplateChanged()
    {
        _dirty = true;
        UpdateSolutionUi();

        // 文本框里有未应用的编辑时不能覆盖：用户正在改的东西优先。
        if (!_jsonDirty)
            RefreshTemplateJson();
    }

    void OnTemplateJsonEdited()
    {
        // 程序化赋值也可能异步触发 TextChanged，布尔开关拦不住；改为和同步时留下的快照比对。
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
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
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
        _jsonState.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

    async Task ImportHiprintAsync()
    {
        try
        {
            // UserControl 没有 StorageProvider 属性，从 TopLevel 取；浏览器环境走 File System Access API。
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus(Localizer.T("Error.NoFileAccess"));
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.T("Dialog.Import.Title"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Localizer.T("Dialog.Import.HiprintFilter")) { Patterns = ["*.js", "*.json", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            });

            if (files.Count == 0)
                return;

            string text;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
                text = await reader.ReadToEndAsync();

            ImportHiprint(text, files[0].Name);
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Error.HiprintImportFailed", ex.Message));
        }
    }

    void ImportHiprint(string text, string sourceName)
    {
        var result = HiprintTemplateImporter.Import(text);
        var note = DescribeImport(sourceName, result);

        // LoadTemplate 会换内核：属性面板、纸张栏和 JSON 面板都得换到新内核上。
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

    async Task ExportPdfAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus(Localizer.T("Error.NoSaveAccess"));
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localizer.T("Dialog.Export.Title"),
                SuggestedFileName = "crossprint.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            var report = PdfExporter.Export(
                _designer.Kernel.Template,
                stream,
                data: SampleData.Create(),
                fontResolver: _designer.FontResolver,
                images: _designer.Images,
                renderer: _designer.Renderer);
            _status.Text = report.HasIssues
                ? Localizer.T("Status.ExportedWithWarnings", file.Name, report.Issues.Count)
                : Localizer.T("Status.Exported", file.Name);
        }
        catch (Exception ex)
        {
            _status.Text = Localizer.T("Error.ExportFailed", ex.Message);
        }
    }

    /// <summary>导出模板 JSON：紧凑单行（不缩进），减少字节占用；文件名建议用解决方案名。</summary>
    async Task ExportTemplateAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus(Localizer.T("Error.NoSaveAccess"));
                return;
            }

            var suggested = SanitizeFileName(_solutionName);
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出模板",
                SuggestedFileName = suggested + ".json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("CrossPrint 模板") { Patterns = ["*.json"] }],
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            var json = TemplateJson.Serialize(_designer.Kernel.Template, indented: false);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(json);
            SetStatus($"已导出模板 {file.Name}（紧凑格式，{json.Length:N0} 字符）。");
        }
        catch (Exception ex)
        {
            SetStatus("导出模板失败：" + ex.Message, error: true);
        }
    }

    static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "template" : name.Trim();
    }

    void OpenPreview()
    {
        var preview = new PreviewControl(_designer.Kernel.Template.Clone())
        {
            Data = SampleData.Create(),
        };

        var warningLabel = new TextBlock
        {
            Margin = new Thickness(8, 4),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Text = Localizer.T("Status.RenderingDrawing"),
        };

        preview.Rendered += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var report = preview.LastReport;
            warningLabel.Text = report is null || !report.HasIssues
                ? Localizer.T("Status.RenderingNoWarnings")
                : Localizer.T("Status.RenderingWarnings", report.Issues.Count, report);
        });

        var root = new DockPanel();
        DockPanel.SetDock(warningLabel, Dock.Bottom);
        root.Children.Add(warningLabel);
        root.Children.Add(preview);

        // 浏览器单视图开不了预览窗口：new Window 在 Avalonia.Browser 直接抛，降级为提示。
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetStatus(Localizer.T("Status.PreviewNotSupportedInBrowser"));
            return;
        }

        // 非模态预览窗也走 ModalDialog 自绘标题栏（与其它弹窗观感统一，去系统表头）。
        var window = ModalDialog.CreateWindow(
            this, Localizer.T("Dialog.Preview.Title"), root,
            width: 900, height: 1000);

        window.Show(owner);
    }

    static PrintTemplate LoadSampleTemplate(out IReadOnlyList<string> warnings)
    {
        try
        {
            // 资产统一走 SampleAssets：桌面读输出目录，浏览器读内嵌资源（WASM 没有文件系统）。
            var text = SampleAssets.Read("kitchen-sink.template.json");
            if (text is null)
            {
                warnings = [Localizer.T("Status.KitchenSinkNotFound")];
                return PrintTemplate.CreateDefault(PaperTypePresets.A4);
            }

            var result = TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant);
            warnings = result.Warnings;
            return result.Template;
        }
        catch (Exception ex)
        {
            warnings = [Localizer.T("Error.LoadFailed", ex.Message)];
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>显示关于对话框窗口（桌面 ShowDialog / 浏览器 overlay）。</summary>
    async Task ShowAboutAsync()
    {
        var productTitle = new TextBlock
        {
            Text = AboutInfo.ProductName,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
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
            Margin = new Thickness(16, 4),
            Foreground = Brushes.DimGray,
        };

        // 第三方依赖列表：名称 + 描述 + License
        var thirdPartyPanel = new StackPanel { Margin = new Thickness(16, 8) };
        thirdPartyPanel.Children.Add(new TextBlock
        {
            Text = Localizer.T("About.ThirdParty"),
            FontWeight = FontWeight.SemiBold,
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
            Margin = new Thickness(16, 12),
            Padding = new Thickness(16, 4),
        };

        var logoPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4),
            Spacing = 16,
        };

        // logo：优先从 I18n 内嵌资源加载；失败则退化为标题文字
        logoPanel.Children.Add(TryLoadLogo());

        var root = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                logoPanel,
                productTitle,
                version,
                description,
                new Separator(),
                thirdPartyPanel,
                closeButton,
            },
        };

        closeButton.Click += (_, _) => ModalDialog.CloseHost(root);

        await ModalDialog.ShowAsync(this, Localizer.T("About.Title"), new Border
        {
            Padding = new Thickness(4),
            Child = root,
        }, 560, 520, 420, 420);
    }

    /// <summary>
    /// 从 CrossPrint.I18n 嵌入资源加载 OODD CrossPrint logo 作为 Avalonia Image 控件；
    /// 找不到时退化为带标题的 Border 占位块。
    /// </summary>
    static Control TryLoadLogo()
    {
        try
        {
            using var stream = LogoLoader.GetLogoStream();
            if (stream is not null)
            {
                var bitmap = Bitmap.DecodeToWidth(stream, 128);
                return new Image
                {
                    Source = bitmap,
                    Width = 128,
                    Height = 128,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
        }
        catch
        {
            // logo 资源加载失败（格式/路径），静默降级为文字占位。
        }

        // 降级：带产品名的占位 Border
        return new Border
        {
            Width = 128,
            Height = 128,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x5C)),
            CornerRadius = new CornerRadius(12),
            Child = new TextBlock
            {
                Text = "OODD\nCrossPrint",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    sealed record PaperChoice(string? Key, string Label);
}
