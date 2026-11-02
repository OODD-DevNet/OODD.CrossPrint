using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using CrossPrint.Designer;
using CrossPrint.Engine;
using CrossPrint.Hosts.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Windows.System;

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
    readonly PaperPanel _paper;
    readonly PropertyPanel _properties;
    readonly ModalHost _modal = new();
    readonly CollectionView _toolbox = new();
    readonly Label _status = new();
    readonly Editor _templateJson = new();
    readonly Label _jsonState = new();
    readonly Button _jsonToggle = new();
    readonly Grid _jsonPanel = new();
    readonly Grid _previewOverlay = new();
    readonly Grid _previewHost = new();
    readonly Grid _menuOverlay = new();
    PreviewControl? _preview;
    bool _jsonDirty;
    bool _jsonExpanded;
    string _jsonSnapshot = string.Empty;

    public MainWindow()
    {
        Title = "CrossPrint MAUI 示例 — 打印设计器";

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

        // 右键菜单由示例自己开：宿主对右键不捕获指针（见 DesignerCanvas 的说明），
        // 事件里带窗口根坐标，覆盖层菜单直接按位置摆卡片。
        _designer.CanvasRightPressed += OnCanvasRightPressed;
        _designer.RawKeyDown += OnDesignerRawKey;

        var page = new ContentPage { Content = BuildRoot() };
        page.Loaded += (_, _) => _designer.TakeFocus();
        Page = page;

        if (warnings.Count > 0)
            SetStatus("模板：" + string.Join("；", warnings), error: true);
        else
            SetStatus("已加载 kitchen-sink.template.json。点击元素选中后拖动或用方向键调整；右键画布打开菜单；Ctrl+D 创建副本；纸张大小在右侧纸张栏设置。");
    }

    static PrintTemplate LoadSampleTemplate(out IReadOnlyList<string> warnings)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "kitchen-sink.template.json");
        if (!File.Exists(path))
        {
            warnings = new[] { "未找到 kitchen-sink.template.json，已回退到默认 A4 空模板。" };
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
            warnings = new[] { "加载 kitchen-sink.template.json 失败，已回退到默认 A4 空模板：" + ex.Message };
            return PrintTemplate.CreateDefault(PaperTypePresets.A4);
        }
    }

    /// <summary>MAUI 没有跨平台的初始窗口尺寸 API：等窗口处理器挂上后借 WinUI 的 AppWindow 设一次。</summary>
    void OnWindowHandlerChanged(object? sender, EventArgs e)
    {
        if (Handler?.PlatformView is Microsoft.UI.Xaml.Window native && native.AppWindow is { } appWindow)
            appWindow.Resize(new Windows.Graphics.SizeInt32(InitialWindowWidth, InitialWindowHeight));
    }

    /// <summary>加载输出目录下的 complex-receipt.template.json（80mm 连续纸长小票）并替换当前模板。</summary>
    void LoadComplexReceipt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "complex-receipt.template.json");
        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            var note = $"已加载 complex-receipt.template.json（80mm 连续纸长小票，{result.Template.Elements.Count} 个元素）：滚轮/滚动条查看全页。";
            if (result.Warnings.Count > 0)
                note += string.Join("；", result.Warnings);

            // LoadTemplate 会换内核：纸张栏、属性面板和 JSON 面板都要换到新内核上。
            _designer.LoadTemplate(result.Template);
            BindKernel(note);
            SetStatus(note);
            _designer.TakeFocus();
        }
        catch (Exception ex)
        {
            SetStatus("加载 complex-receipt.template.json 失败：" + ex.Message, error: true);
        }
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

        AddAt(root, BuildFileMenuRow(), row: 0);
        AddAt(root, BuildElementBar(), row: 1);
        AddAt(root, BuildEditBar(), row: 2);
        AddAt(root, BuildBody(), row: 3);
        AddAt(root, BuildTemplateJsonPanel(), row: 4);
        AddAt(root, BuildStatusBar(), row: 5);

        // 覆盖层自下而上：预览 → 下拉/右键菜单 → 模态。Z 序即添加顺序。
        AddAt(root, BuildPreviewOverlay(), row: 0, rowSpan: 6);
        AddAt(root, _menuOverlay, row: 0, rowSpan: 6);
        AddAt(root, _modal, row: 0, rowSpan: 6);

        BindKernel();
        return root;
    }

    /// <summary>菜单行：MAUI 8 没有 MenuBar，用「文件 ▾」按钮 + 页内下拉卡片模拟（与画布右键菜单同一套覆盖层）。</summary>
    View BuildFileMenuRow()
    {
        var bar = new HorizontalStackLayout { Margin = new Thickness(4, 2, 4, 2) };
        bar.Add(MakeButton("文件 ▾", ToggleFileMenu));
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

        var menu = new VerticalStackLayout { Spacing = 1, MinimumWidthRequest = 180 };
        menu.Add(MakeMenuAction("导入 hiprint 模板…", true, () => _ = ShowImportHiprint()));
        menu.Add(MakeMenuAction("加载复杂测试模板", true, LoadComplexReceipt));

        // 按钮在菜单行的最左侧（外边距 4），卡片固定摆在它正下方。
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
            panel.Add(MakeButton(label, () => Run(() => _designer.Kernel.AddElementAtCenter(typeKey)), "添加" + label));
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
        panel.Add(MakeButton("导入", () => _ = ShowImportHiprint(), "导入 hiprint / vue-plugin-hiprint 导出的模板"));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton("副本", DuplicateSelection, "创建选中元素的副本（Ctrl+D）"));
        panel.Add(MakeButton("删除", () => Run(_designer.Kernel.DeleteSelection), "删除选中元素（Delete）"));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton("上移", () => Run(_designer.Kernel.BringForward), "Z 序上移一层"));
        panel.Add(MakeButton("下移", () => Run(_designer.Kernel.SendBackward), "Z 序下移一层"));
        panel.Add(MakeButton("置顶", () => Run(_designer.Kernel.BringToFront), "置于顶层"));
        panel.Add(MakeButton("置底", () => Run(_designer.Kernel.SendToBack), "置于底层"));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton("适合窗口", () => Run(_designer.FitToPage), "整页适配"));
        panel.Add(MakeButton("100%", () => Run(() =>
        {
            _designer.Kernel.Viewport.Reset();
            _designer.Invalidate();
        }), "实际大小"));
        panel.Add(MakeSeparator());
        panel.Add(MakeOverlayCheck("网格", true, value => _designer.Overlay.Options.ShowGrid = value));
        panel.Add(MakeOverlayCheck("标尺", true, value => _designer.Overlay.Options.ShowRulers = value));
        panel.Add(MakeOverlayCheck("安全区", true, value => _designer.Overlay.Options.ShowMarginGuides = value));
        panel.Add(MakeSeparator());
        panel.Add(MakeButton("导出 PDF", ExportPdf, "导出当前模板（绑定示例数据）"));
        panel.Add(MakeButton("预览", OpenPreview, "打开打印预览"));

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

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        Grid.SetColumn(leftHost, 0);
        Grid.SetColumn(designerHost, 1);
        Grid.SetColumn(propertyHost, 2);
        grid.Add(leftHost);
        grid.Add(designerHost);
        grid.Add(propertyHost);
        return grid;
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
            Text = "工具箱",
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetRow(header, 0);
        grid.Add(header);

        _toolbox.ItemsSource = Array.ConvertAll(ElementButtons, item => item.Label);
        Grid.SetRow(_toolbox, 1);
        grid.Add(_toolbox);

        var add = new Button
        {
            Text = "添加到画布",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        add.Clicked += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 2);
        grid.Add(add);

        var hint = new Label
        {
            Text = "点选工具后按「添加到画布」添加到画布中心。",
            FontSize = 11,
            TextColor = Colors.Gray,
            LineBreakMode = LineBreakMode.WordWrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(hint, 3);
        grid.Add(hint);

        return grid;
    }

    void AddFromToolbox()
    {
        // CollectionView 没有 SelectedIndex，用选中的标签反查元素类型。
        if (_toolbox.SelectedItem is not string label)
        {
            SetStatus("请先在工具箱里选择一个元素类型。", error: true);
            return;
        }

        var element = Array.Find(ElementButtons, item => item.Label == label);
        Run(() => _designer.Kernel.AddElementAtCenter(element.Key));
        SetStatus($"已添加{label}元素；右侧属性面板可直接编辑。");
    }

    View BuildStatusBar()
    {
        _status.TextColor = Colors.DimGray;
        _status.Margin = new Thickness(10, 3, 10, 3);
        _status.LineBreakMode = LineBreakMode.NoWrap;

        return new Border
        {
            Background = new SolidColorBrush(FromRgb(0xF0, 0xF0, 0xF0)),
            Stroke = FromRgb(0xD0, 0xD0, 0xD0),
            StrokeThickness = 1,
            Content = _status,
        };
    }

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
        buttons.Add(MakeButton("适合窗口", () => _preview?.FitToPage()));
        buttons.Add(MakeButton("100%", () =>
        {
            _preview?.Kernel.Viewport.Reset();
            _preview?.Invalidate();
        }));
        buttons.Add(MakeButton("关闭", ClosePreview));

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

    /// <summary>Ctrl+D 创建副本：宿主把内核没映射的按键都抛到 RawKeyDown，这里组合修饰键判断。</summary>
    void OnDesignerRawKey(object? sender, RawKeyEventArgs e)
    {
        if (e.Control && e.Key == VirtualKey.D)
            DuplicateSelection();
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

        Add("创建副本 (Ctrl+D)", hasSelection, DuplicateSelection);
        Add("删除 (Delete)", hasSelection, () => Run(_designer.Kernel.DeleteSelection));
        Add("上移一层", hasSelection, () => Run(_designer.Kernel.BringForward));
        Add("下移一层", hasSelection, () => Run(_designer.Kernel.SendBackward));
        Add("置于顶层", hasSelection, () => Run(_designer.Kernel.BringToFront));
        Add("置于底层", hasSelection, () => Run(_designer.Kernel.SendToBack));
        Add("水平居中", hasSelection, () =>
        {
            Run(_designer.Kernel.CenterSelectionHorizontally);
            SetStatus("选中元素已在页面上水平居中。");
        });
        Add("垂直居中", hasSelection && !paper.IsContinuous, () =>
        {
            Run(_designer.Kernel.CenterSelectionVertically);
            SetStatus("选中元素已在页面上垂直居中。");
        });
        Add("适合窗口", true, () => Run(_designer.FitToPage));
        Add("实际大小（100%）", true, () => Run(() =>
            _designer.Kernel.OnKeyDown(new KeyInfo(KeyAction.ZoomActual))));
        Add(
            paper.Orientation == PaperOrientation.Landscape ? "旋转为纵向" : "旋转为横向",
            !paper.IsContinuous,
            TogglePaperOrientation);
        Add("自定义纸张…", true, () => _ = ShowCustomPaper());

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
            SetStatus("先选中元素再创建副本。");
            return;
        }

        _designer.Kernel.DuplicateSelection();
        _designer.TakeFocus();
        SetStatus($"已创建副本：向页内偏移 {EditorKernel.DuplicateOffsetMm:0.#} mm 并选中副本。");
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
        _jsonToggle.Text = "▸ 模板 JSON";
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
        _jsonToggle.Text = expanded ? "▾ 模板 JSON" : "▸ 模板 JSON";
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
        _templateJson.CursorPosition = 0;
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
            "导入 hiprint 模板",
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

    void ExportPdf()
    {
        // 与 Uno 示例同一策略：写到"文档"目录并回显路径，不依赖平台文件选择器。
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = AppContext.BaseDirectory;

        var path = Path.Combine(documents, "crossprint-maui-sample.pdf");
        try
        {
            using (var stream = File.Create(path))
            {
                var report = PdfExporter.Export(
                    _designer.Template,
                    stream,
                    data: SampleData.Create(),
                    fontResolver: _designer.FontResolver,
                    images: _designer.Images,
                    renderer: _designer.Renderer);

                SetStatus(report.HasIssues
                    ? $"已导出 PDF（{report.Issues.Count} 条渲染降级）：{path}"
                    : $"已导出 PDF：{path}");
            }
        }
        catch (Exception ex)
        {
            SetStatus("导出 PDF 失败：" + ex.Message, error: true);
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

    void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.TextColor = error ? Colors.Firebrick : Colors.DimGray;
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
}
