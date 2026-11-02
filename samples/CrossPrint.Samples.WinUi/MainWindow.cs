using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.WinUi;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

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
        new(PaperTypePresets.A3, "A3（297×420）"),
        new(PaperTypePresets.A4, "A4（210×297）"),
        new(PaperTypePresets.A5, "A5（148×210）"),
        new(PaperTypePresets.B5, "B5（176×250）"),
        new(PaperTypePresets.Receipt80, "80mm 连续纸"),
        new(PaperTypePresets.Receipt58, "58mm 连续纸"),
        new(PaperTypePresets.Label100x150, "100×150 面单"),
        new(null, "自定义…"),
    ];

    static readonly (string Label, string Key)[] ToolboxItems =
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

    readonly DesignerControl _designer;
    readonly PropertyPanel _properties;
    readonly ListView _toolbox = new();
    readonly TextBlock _status;
    readonly ComboBox _paperCombo = new();
    readonly Button _rotatePaperButton = new();
    readonly TextBlock _paperInfo = new();
    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    // JSON 面板的折叠开关与内容行：它是低频的调试/导入入口，默认收起只留一条标题带。
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;

    bool _syncingPaper;
    bool _paperDialogOpen;
    bool _jsonDirty;
    string _jsonSnapshot = string.Empty;
    PreviewWindow? _previewWindow;

    /// <summary>创建主窗口并加载 kitchen-sink 示例模板。</summary>
    public MainWindow()
    {
        Title = "CrossPrint WinUI 示例";

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

        Content = BuildLayout();
        // 窗口尺寸与 WPF 基准一致（AppWindow 尺寸含边框，单位是像素）。
        AppWindow.Resize(new SizeInt32(InitialWindowWidth, InitialWindowHeight));
        AppWindow.Changed += OnAppWindowChanged;

        _designer.PointerPressed += OnDesignerPointerPressed;
        BindKernel();

        SetStatus(loadMessages.Count == 0
            ? "已加载 kitchen-sink.template.json；点击元素可选中，拖动或方向键调整位置，右键画布打开菜单。"
            : "模板告警：" + string.Join("；", loadMessages));
    }

    sealed record PaperChoice(string? Key, string Label);

    static PrintTemplate LoadInitialTemplate(out List<string> messages)
    {
        messages = [];
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

    /// <summary>加载输出目录下的 complex-receipt.template.json（80mm 连续纸长小票）并替换当前模板。</summary>
    void LoadComplexReceipt()
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
                note += string.Join("；", result.Warnings);

            // LoadTemplate 会换内核：属性面板、纸张同步和 JSON 面板都要接到新内核上。
            _designer.LoadTemplate(result.Template);
            BindKernel(note);
            SetStatus(note);
            FocusCanvas();
        }
        catch (Exception ex)
        {
            SetStatus("加载 complex-receipt.template.json 失败：" + ex.Message);
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
        // 三列布局与 WPF 基准一致：左 200（纸张栏 + 工具箱）、中画布、右 310（属性面板）。
        // WinUI 3 没有内置 GridSplitter，示例也未引用 CommunityToolkit.WinUI.Controls.Sizers
        //（为示例单独引入工具包依赖不划算），所以两侧栏保持固定宽度并按统一方案收窄，
        // 拖动调宽留待将来引入 Sizers 后再补。
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        content.Children.Add(BuildLeftSidebar());

        var canvasHost = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            Child = _designer,
        };
        Grid.SetColumn(canvasHost, 1);
        content.Children.Add(canvasHost);

        var propertiesHost = BuildPropertiesPanel();
        Grid.SetColumn(propertiesHost, 2);
        content.Children.Add(propertiesHost);

        Grid.SetRow(content, 2);
        root.Children.Add(content);

        var jsonPanel = BuildTemplateJsonPanel();
        Grid.SetRow(jsonPanel, 3);
        root.Children.Add(jsonPanel);

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
        Grid.SetRow(statusBar, 4);
        root.Children.Add(statusBar);

        // Ctrl+D 挂在窗口内容根上：画布、工具栏、菜单关闭后都能用；属性输入框里的按键由处理器自己跳过。
        var duplicateAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.D,
            Modifiers = VirtualKeyModifiers.Control,
        };
        duplicateAccelerator.Invoked += OnDuplicateAcceleratorInvoked;
        root.KeyboardAccelerators.Add(duplicateAccelerator);

        return root;
    }

    /// <summary>顶部菜单栏：文档级操作（hiprint 导入、加载复杂测试模板）。</summary>
    FrameworkElement BuildMenuBar()
    {
        var fileMenu = new MenuBarItem { Title = "文件(_F)" };
        var importItem = new MenuFlyoutItem { Text = "导入 hiprint 模板…" };
        importItem.Click += (_, _) => _ = ImportHiprintAsync();
        fileMenu.Items.Add(importItem);

        var loadItem = new MenuFlyoutItem { Text = "加载复杂测试模板" };
        loadItem.Click += (_, _) => LoadComplexReceipt();
        fileMenu.Items.Add(loadItem);

        var bar = new MenuBar();
        bar.Items.Add(fileMenu);
        return bar;
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
            elementRow.Children.Add(ActionButton(label, (_, _) => AddElement(typeKey), "添加" + label));
        }
        bar.Children.Add(elementRow);

        // 第二行：编辑/视图/输出合并成一条（对齐 WPF 双带方案的第二带），功能组之间用分隔线区隔。
        // 标签尽量短（完整说明在 ToolTip 里）：这一行项目最多，窄窗口时更要省横向空间。
        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        // 导入按钮放最前面：窗口变窄时末尾的按钮会先被裁掉。
        editRow.Children.Add(ActionButton("导入", OnImportHiprint, "导入 hiprint / vue-plugin-hiprint 导出的模板"));
        editRow.Children.Add(ActionButton("副本", (_, _) => DuplicateSelection(), "创建选中元素的副本（Ctrl+D）"));
        editRow.Children.Add(ActionButton("删除", (_, _) => DeleteSelection(), "删除选中元素（Delete）"));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton("上移", (_, _) => RunKernel(kernel => kernel.BringForward()), "Z 序上移一层"));
        editRow.Children.Add(ActionButton("下移", (_, _) => RunKernel(kernel => kernel.SendBackward()), "Z 序下移一层"));
        editRow.Children.Add(ActionButton("置顶", (_, _) => RunKernel(kernel => kernel.BringToFront()), "置于顶层"));
        editRow.Children.Add(ActionButton("置底", (_, _) => RunKernel(kernel => kernel.SendToBack()), "置于底层"));
        editRow.Children.Add(ActionButton("水平居中", (_, _) => CenterHorizontally(), "选中元素在页面上水平居中"));
        editRow.Children.Add(ActionButton("垂直居中", (_, _) => CenterVertically(), "选中元素在页面上垂直居中"));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton("适合窗口", (_, _) => FitToWindow(), "整页适配"));
        editRow.Children.Add(ActionButton("100%", (_, _) => ZoomActual(), "实际大小（100%）"));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(Toggle("网格", value => _designer.Overlay.Options.ShowGrid = value));
        editRow.Children.Add(Toggle("标尺", value => _designer.Overlay.Options.ShowRulers = value));
        editRow.Children.Add(Toggle("安全区", value => _designer.Overlay.Options.ShowMarginGuides = value));
        editRow.Children.Add(MakeToolbarSeparator());
        editRow.Children.Add(ActionButton("导出 PDF", OnExportPdf, "导出当前模板（绑定示例数据）"));
        editRow.Children.Add(ActionButton("预览", OnShowPreview, "打开打印预览"));
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
            Text = "属性",
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
        panel.Children.Add(new TextBlock { Text = "纸张", FontWeight = FontWeights.SemiBold });

        foreach (var choice in PaperChoices)
            _paperCombo.Items.Add(choice.Label);

        _paperCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperCombo.SelectionChanged += OnPaperChoiceChanged;
        panel.Children.Add(_paperCombo);

        var buttons = new Grid { ColumnSpacing = 6 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _rotatePaperButton.Content = "转横向";
        _rotatePaperButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rotatePaperButton.Click += (_, _) => TogglePaperOrientation();
        Grid.SetColumn(_rotatePaperButton, 0);
        buttons.Children.Add(_rotatePaperButton);

        var customPaperButton = new Button
        {
            Content = "自定义…",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        customPaperButton.Click += (_, _) => RequestCustomPaperDialog();
        Grid.SetColumn(customPaperButton, 1);
        buttons.Children.Add(customPaperButton);

        panel.Children.Add(buttons);

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

    /// <summary>工具箱：元素类型列表，双击或回车（或点按钮）添加到画布中心，与 WPF 基准一致。</summary>
    FrameworkElement BuildToolbox()
    {
        var grid = new Grid { Padding = new Thickness(8, 8, 8, 8), RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = "工具箱", FontWeight = FontWeights.SemiBold });

        _toolbox.ItemsSource = ToolboxItems.Select(item => item.Label).ToArray();
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
        Grid.SetRow(_toolbox, 1);
        grid.Children.Add(_toolbox);

        var add = new Button
        {
            Content = "添加到画布",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 2);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = "双击工具或按回车添加到画布中心。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(hint, 3);
        grid.Children.Add(hint);

        return grid;
    }

    void AddFromToolbox()
    {
        if (_toolbox.SelectedIndex < 0)
        {
            SetStatus("请先在工具箱里选择一个元素类型。");
            return;
        }

        var (label, key) = ToolboxItems[_toolbox.SelectedIndex];
        AddElement(key);
        SetStatus($"已添加{label}元素；右侧属性面板可直接编辑。");
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
            Text = "创建副本",
            KeyboardAcceleratorTextOverride = "Ctrl+D",
            IsEnabled = hasSelection,
        };
        duplicateItem.Click += (_, _) => DuplicateSelection();
        flyout.Items.Add(duplicateItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除",
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

        AddSelectionItem("上移一层", () => RunKernel(k => k.BringForward()));
        AddSelectionItem("下移一层", () => RunKernel(k => k.SendBackward()));
        AddSelectionItem("置于顶层", () => RunKernel(k => k.BringToFront()));
        AddSelectionItem("置于底层", () => RunKernel(k => k.SendToBack()));

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddSelectionItem("水平居中", CenterHorizontally);

        var verticalCenterItem = new MenuFlyoutItem
        {
            Text = "垂直居中",
            IsEnabled = hasSelection && !paper.IsContinuous,
        };
        verticalCenterItem.Click += (_, _) => CenterVertically();
        flyout.Items.Add(verticalCenterItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var fitItem = new MenuFlyoutItem { Text = "适合窗口" };
        fitItem.Click += (_, _) => FitToWindow();
        flyout.Items.Add(fitItem);

        var actualSizeItem = new MenuFlyoutItem { Text = "实际大小（100%）" };
        actualSizeItem.Click += (_, _) => ZoomActual();
        flyout.Items.Add(actualSizeItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var rotateItem = new MenuFlyoutItem
        {
            Text = paper.Orientation == PaperOrientation.Landscape ? "旋转为纵向" : "旋转为横向",
            IsEnabled = !paper.IsContinuous,
        };
        rotateItem.Click += (_, _) => TogglePaperOrientation();
        flyout.Items.Add(rotateItem);

        var customPaperItem = new MenuFlyoutItem { Text = "自定义纸张…" };
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
        SetStatus($"已添加元素（{typeKey}）：拖动移动，方向键微调，Delete 删除。");
    }

    void DuplicateSelection()
    {
        if (_designer.Kernel.Selection.IsEmpty)
        {
            SetStatus("先选中元素再创建副本。");
            return;
        }

        _designer.Kernel.DuplicateSelection();
        FocusCanvas();
        SetStatus($"已创建副本：向页内偏移 {EditorKernel.DuplicateOffsetMm:0.#} mm 并选中副本。");
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
        SetStatus("选中元素已在页面上水平居中。");
    }

    void CenterVertically()
    {
        if (_designer.Kernel.Template.Paper.IsContinuous)
        {
            SetStatus("连续纸没有固定页高，不能垂直居中。");
            return;
        }

        _designer.Kernel.CenterSelectionVertically();
        FocusCanvas();
        SetStatus("选中元素已在页面上垂直居中。");
    }

    void RunKernel(Action<EditorKernel> action)
    {
        action(_designer.Kernel);
        FocusCanvas();
    }

    void FitToWindow()
    {
        _designer.FitToPage();
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
            SetStatus($"纸张已设为 {paper.PageWidthMm:0.##} × {paper.PageHeightMm:0.##} mm。");
        }
        catch (Exception ex)
        {
            _ = ShowMessageAsync("纸张", "设置纸张失败：" + ex.Message);
        }

        SyncPaperControls();
        FocusCanvas();
    }

    void TogglePaperOrientation()
    {
        var kernel = _designer.Kernel;
        if (kernel.Template.Paper.IsContinuous)
        {
            SetStatus("连续纸没有固定页高，不能旋转。");
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
            _ = ShowMessageAsync("纸张", "旋转纸张失败：" + ex.Message);
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
                    _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm);
                    SetStatus(dialog.HeightMm > 0
                        ? $"自定义纸张：{dialog.WidthMm:0.##} × {dialog.HeightMm:0.##} mm。"
                        : $"自定义连续纸：宽 {dialog.WidthMm:0.##} mm，长度随内容自适应。");
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync("纸张", "设置纸张失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("纸张", "打开自定义纸张对话框失败：" + ex.Message);
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

            _rotatePaperButton.IsEnabled = !paper.IsContinuous;
            _rotatePaperButton.Content = paper.Orientation == PaperOrientation.Landscape ? "转纵向" : "转横向";
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
        _jsonToggle.Content = "▸ 模板 JSON";
        _jsonToggle.FontWeight = FontWeights.SemiBold;
        _jsonToggle.VerticalAlignment = VerticalAlignment.Center;
        _jsonToggle.Margin = new Thickness(0, 0, 10, 0);
        _jsonToggle.Click += (_, _) => SetJsonPanelExpanded(_jsonToggle.IsChecked == true);

        var apply = new Button
        {
            Content = "应用 JSON",
            Padding = new Thickness(8, 2, 8, 2),
        };
        apply.Click += (_, _) => ApplyTemplateJson();

        var reload = new Button
        {
            Content = "重新载入",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
        };
        reload.Click += (_, _) => RefreshTemplateJson("已重新载入当前模板，未应用的编辑已丢弃。");

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
        _jsonToggle.Content = expanded ? "▾ 模板 JSON" : "▸ 模板 JSON";
        _jsonContentRow.Height = new GridLength(expanded ? 230 : 0);
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
        SetJsonState("有未应用的修改：点「应用 JSON」生效。", error: false);
    }

    void RefreshTemplateJson(string? note = null)
    {
        var text = TemplateJson.Serialize(_designer.Kernel.Template);
        _jsonSnapshot = NormalizeJsonText(text);
        _jsonDirty = false;
        _templateJson.Text = text;
        _templateJson.Select(0, 0);
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
                FocusCanvas();
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
            FocusCanvas();
        }
        catch (Exception ex)
        {
            SetJsonState("JSON 未应用：" + ex.Message, error: true);
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
            await ShowMessageAsync("导入失败", ex.Message);
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
            await ShowMessageAsync("导入失败", ex.Message);
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

    async void OnExportPdf(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "crossprint-sample",
        };
        picker.FileTypeChoices.Add("PDF 文件", new List<string> { ".pdf" });

        // WinUI 3 桌面应用必须显式把 picker 挂到窗口上，否则 PickSaveFileAsync 直接失败。
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导出失败", ex.Message);
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
                ? $"已导出 PDF：{file.Path}（{report.Issues.Count} 条渲染告警）"
                : $"已导出 PDF：{file.Path}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导出失败", ex.Message);
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
                CloseButtonText = "确定",
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
}
