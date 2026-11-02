using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
using EditorPointerPoint = CrossPrint.Designer.PointerPoint;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 示例主视图：顶部工具栏 + 左侧纸张栏/工具箱（可拖宽）+ 中间设计画布 + 右侧属性面板（可拖宽）+ 可折叠模板 JSON 带 + 底部状态栏。
/// 用 UserControl 而非 Window：浏览器单视图生命周期不支持 WindowingPlatform（连 new Window 都会抛），
/// 桌面版由 MainWindow 薄包装持有本视图。
/// </summary>
public sealed class MainView : UserControl
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
    readonly ListBox _toolbox = new();
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

        BuildCanvasContextMenu();
        Content = BuildRoot();

        BindKernel();
        _designer.PointerPressed += OnDesignerPointerPressed;
        _designer.KeyDown += OnDesignerKeyDown;

        _status.Text = warnings.Count == 0
            ? Localizer.T("App.Status.Ready")
            : Localizer.T("App.Status.Warnings", string.Join("；", warnings));
    }

    Control BuildRoot()
    {
        var root = new DockPanel();

        var menu = BuildMenuBar();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        var toolbar = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            // 横向滚动禁用后 WrapPanel 才能真正换行：否则它拿到无限宽，
            // 靠右的「导入」等按钮会被挤出可视区，用户根本看不到。
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BuildToolbar(),
            },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var status = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // 状态栏先停靠到底部，JSON 面板随后停靠，就叠在状态栏上方。
        var jsonPanel = BuildTemplateJsonPanel();
        DockPanel.SetDock(jsonPanel, Dock.Bottom);
        root.Children.Add(jsonPanel);

        var body = new Grid();
        // 五列：左栏 200（可拖，最小 150）、两道 4px 分割条、画布、右栏 310（可拖，最小 240）。
        // 侧栏宽度可拖：窄窗口时把空间让给画布，宽屏时也能把属性面板拉宽看长字段。
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200), MinWidth = 150 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310), MinWidth = 240 });

        body.Children.Add(BuildSidebar());

        var leftSplitter = MakeColumnSplitter();
        Grid.SetColumn(leftSplitter, 1);
        body.Children.Add(leftSplitter);

        var canvasHost = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1, 0, 1, 0),
            Child = _designer,
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

    /// <summary>透明背景的列分割条：透明画刷仍参与命中测试，悬停/拖拽时才有可见反馈。</summary>
    static GridSplitter MakeColumnSplitter() => new()
    {
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ResizeDirection = GridResizeDirection.Columns,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent,
    };

    /// <summary>顶部菜单栏：文档级操作（加载复杂测试模板、hiprint 导入）。</summary>
    Control BuildMenuBar()
    {
        var fileMenu = new MenuItem { Header = Localizer.T("Menu.File") };

        var loadComplexItem = new MenuItem { Header = Localizer.T("Menu.File.LoadComplex") };
        loadComplexItem.Click += (_, _) => LoadComplexReceiptTemplate();
        fileMenu.Items.Add(loadComplexItem);

        var importItem = new MenuItem { Header = Localizer.T("Menu.File.ImportHiprint") };
        importItem.Click += (_, _) => _ = ImportHiprintAsync();
        fileMenu.Items.Add(importItem);

        fileMenu.Items.Add(new Separator());

        var aboutItem = new MenuItem { Header = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => _ = ShowAboutAsync();
        fileMenu.Items.Add(aboutItem);

        var menu = new Menu();
        menu.Items.Add(fileMenu);
        return menu;
    }

    /// <summary>左侧栏：纸张面板固定在顶部，工具箱占满剩余高度。</summary>
    Control BuildSidebar()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        grid.Children.Add(BuildPaperPanel());

        var toolbox = BuildToolbox();
        Grid.SetRow(toolbox, 1);
        grid.Children.Add(toolbox);

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
    }

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

    /// <summary>工具箱列表：与 WPF 基准一致，双击/回车/「添加到画布」按钮把选中元素加到画布中心。</summary>
    Control BuildToolbox()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(8, 4, 8, 6),
        };

        grid.Children.Add(new TextBlock
        {
            Text = Localizer.T("Toolbox.Section"),
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // 列表项顺序与 ElementButtons 一一对应：选中项下标即元素类型。
        _toolbox.ItemsSource = ElementButtons.Select(item => item.Label).ToArray();
        _toolbox.DoubleTapped += (_, _) => AddFromToolbox();
        _toolbox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddFromToolbox();
                e.Handled = true;
            }
        };
        Grid.SetRow(_toolbox, 1);
        grid.Children.Add(_toolbox);

        var add = new Button
        {
            Content = Localizer.T("Toolbox.AddToCanvas"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 2);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = Localizer.T("Toolbox.Hint"),
            FontSize = 11,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(hint, 3);
        grid.Children.Add(hint);

        return new ScrollViewer { Content = grid };
    }

    Control BuildToolbar()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4) };

        foreach (var (label, key) in ElementButtons)
            panel.Children.Add(ToolbarButton(label, () => AddElement(key), Localizer.T("Toolbar.AddWithLabel", label)));

        panel.Children.Add(Separator());
        // 标签尽量短（完整说明放 ToolTip）：工具带项目多，窄窗口换行时省横向空间。
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.DuplicateTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Delete"), DeleteSelection, Localizer.T("Toolbar.DeleteTip")));

        panel.Children.Add(Separator());
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.BringForward"), () => RunKernel(kernel => kernel.BringForward()), Localizer.T("Toolbar.BringForwardTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.SendBackward"), () => RunKernel(kernel => kernel.SendBackward()), Localizer.T("Toolbar.SendBackwardTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.BringToFront"), () => RunKernel(kernel => kernel.BringToFront()), Localizer.T("Toolbar.BringToFrontTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.SendToBack"), () => RunKernel(kernel => kernel.SendToBack()), Localizer.T("Toolbar.SendToBackTip")));

        panel.Children.Add(Separator());
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.FitToPage"), FitToPage, Localizer.T("Toolbar.FitToPageTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.ZoomActual"), ZoomActual, Localizer.T("Toolbar.ZoomActualTip")));

        panel.Children.Add(Separator());
        panel.Children.Add(Toggle(Localizer.T("Toolbar.Grid"), true, value =>
        {
            _designer.Overlay.Options.ShowGrid = value;
            _designer.Redraw();
        }));
        panel.Children.Add(Toggle(Localizer.T("Toolbar.Rulers"), true, value =>
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
        panel.Children.Add(ToolbarButton(Localizer.T("Menu.File.ImportHiprint"), () => _ = ImportHiprintAsync(), Localizer.T("Toolbar.ImportTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.ExportPdf"), () => _ = ExportPdfAsync(), Localizer.T("Toolbar.ExportPdfTip")));
        panel.Children.Add(ToolbarButton(Localizer.T("Toolbar.Preview"), OpenPreview, Localizer.T("Toolbar.PreviewTip")));

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

    /// <summary>把工具箱当前选中的元素添加到画布中心。</summary>
    void AddFromToolbox()
    {
        var index = _toolbox.SelectedIndex;
        if (index < 0)
        {
            SetStatus(Localizer.T("Status.SelectFirstInToolbox"));
            return;
        }

        var (label, key) = ElementButtons[index];
        _designer.Kernel.AddElementAtCenter(key);
        _designer.Focus();
        SetStatus(Localizer.T("Status.ToolboxAdded", label));
    }

    /// <summary>加载 80mm 连续纸长小票复杂模板：写法照抄 kitchen-sink 的 LoadSampleTemplate，失败时状态栏提示。</summary>
    void LoadComplexReceiptTemplate()
    {
        // 资产统一走 SampleAssets：桌面读输出目录，浏览器读内嵌资源（WASM 没有文件系统）。
        var text = SampleAssets.Read("complex-receipt.template.json");
        if (text is null)
        {
            SetStatus(Localizer.T("Status.LoadComplexNotFound"));
            return;
        }

        try
        {
            var result = TemplateJson.Load(text, TemplateJsonLoadOptions.Tolerant);
            // LoadTemplate 会换内核：属性面板、纸张栏和 JSON 面板都要接到新内核上。
            _designer.LoadTemplate(result.Template);
            BindKernel(Localizer.T("Status.LoadComplex"));

            var status = Localizer.T("Status.LoadComplexDetail", result.Template.Elements.Count);
            if (result.Warnings.Count > 0)
                status += "；" + string.Join("；", result.Warnings);
            SetStatus(status);
            _designer.Focus();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Status.LoadComplexFailed", ex.Message));
        }
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
            SetStatus(Localizer.T("Status.SelectFirstBeforeDuplicate"));
            _designer.Focus();
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.Focus();
        SetStatus(Localizer.T("Status.Duplicated", EditorKernel.DuplicateOffsetMm));
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
            SetStatus(Localizer.T("Status.AlignHorizontally"));
        });
        _verticalCenterItem = AddSelectionItem(Localizer.T("Canvas.Context.AlignMiddle"), null, () =>
        {
            RunKernel(kernel => kernel.CenterSelectionVertically());
            SetStatus(Localizer.T("Status.AlignVertically"));
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

    void OnDesignerKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+D 只在设计画布有焦点时生效：属性面板文本框里的按键不该被抢。
        if (!_designer.IsFocused
            || e.Handled
            || e.Key != Key.D
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        e.Handled = true;
        DuplicateSelection();
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
            // 浏览器单视图没有 Window 可作模态宿主，弹窗只支持桌面。
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                SetStatus(Localizer.T("Error.NotSupportedInBrowser"));
                return;
            }
            await dialog.ShowDialog(owner);
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

    void SetStatus(string text) => _status.Text = text;

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

        var window = new Window
        {
            Title = Localizer.T("Dialog.Preview.Title"),
            Width = 900,
            Height = 1000,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };

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

    /// <summary>显示关于对话框窗口。浏览器环境降级为在状态栏提示。</summary>
    async Task ShowAboutAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetStatus(Localizer.T("Error.NotSupportedInBrowser"));
            return;
        }

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

        var window = new Window
        {
            Title = Localizer.T("About.Title"),
            Width = 560,
            Height = 520,
            MinWidth = 420,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new Border
            {
                Padding = new Thickness(4),
                Child = root,
            },
        };

        try
        {
            using var stream = LogoLoader.GetLogoStream();
            if (stream is not null) window.Icon = new WindowIcon(stream);
        }
        catch { }

        closeButton.Click += (_, _) => window.Close();

        await window.ShowDialog(owner);
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
