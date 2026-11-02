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
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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
    readonly PaperPanel _paper;
    readonly PropertyPanel _properties;
    readonly ListView _toolbox = new();
    readonly TextBlock _status = new();
    readonly TextBox _templateJson = new();
    readonly TextBlock _jsonState = new();
    // JSON 面板的折叠开关与内容行：它是低频的调试/导入入口，默认收起只留一条标题带。
    readonly ToggleButton _jsonToggle = new();
    RowDefinition _jsonContentRow = null!;
    readonly Grid _previewOverlay = new();
    readonly Grid _previewHost = new();
    PreviewControl? _preview;
    bool _controlPressed;
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

        // 右键菜单由示例自己开：宿主对右键不捕获指针（见 DesignerControl 的说明），
        // 这里用指针坐标命中元素后再 ShowAt，菜单项才有作用对象。
        _designer.PointerPressed += OnCanvasPointerPressed;
        HookDuplicateShortcut();

        var root = new Grid();
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

        Content = root;
        BindKernel();

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

    /// <summary>加载输出目录下的 complex-receipt.template.json（80mm 连续纸长小票）并替换当前模板。</summary>
    void LoadComplexReceipt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "complex-receipt.template.json");
        if (!File.Exists(path))
        {
            SetStatus(Localizer.T("Status.ComplexNotFound"), error: true);
            return;
        }

        try
        {
            var result = TemplateJson.Load(File.ReadAllText(path), TemplateJsonLoadOptions.Tolerant);
            var note = Localizer.T("Status.ComplexLoadedInfo", result.Template.Elements.Count);
            if (result.Warnings.Count > 0)
                note += string.Join("；", result.Warnings);

            // LoadTemplate 会换内核：纸张栏、属性面板和 JSON 面板都要换到新内核上。
            _designer.LoadTemplate(result.Template);
            BindKernel(note);
            SetStatus(note);
            _designer.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Status.ComplexLoadFailed", ex.Message), error: true);
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

        var loadItem = new MenuFlyoutItem { Text = Localizer.T("Menu.File.LoadComplex") };
        loadItem.Click += (_, _) => LoadComplexReceipt();
        fileMenu.Items.Add(loadItem);

        var helpMenu = new MenuBarItem { Title = Localizer.T("Menu.Help") };
        var aboutItem = new MenuFlyoutItem { Text = Localizer.T("Menu.Help.About") };
        aboutItem.Click += (_, _) => _ = ShowAboutDialogAsync();
        helpMenu.Items.Add(aboutItem);

        var bar = new MenuBar();
        bar.Items.Add(fileMenu);
        bar.Items.Add(helpMenu);
        return bar;
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
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.Delete"), () => Run(_designer.Kernel.DeleteSelection), Localizer.T("Toolbar.Delete.Tip")));
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        panel.Children.Add(MakeSeparator());
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), () => Run(_designer.FitToPage), Localizer.T("Toolbar.FitToPage.Tip")));
        panel.Children.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), () =>
        {
            _designer.Kernel.Viewport.Reset();
            _designer.Invalidate();
        }, Localizer.T("Toolbar.ZoomActual.Tip")));
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

        _toolbox.ItemsSource = Array.ConvertAll(ElementButtons, item => item.Label);
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
            Content = Localizer.T("Toolbox.AddToCanvas"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        add.Click += (_, _) => AddFromToolbox();
        Grid.SetRow(add, 2);
        grid.Children.Add(add);

        var hint = new TextBlock
        {
            Text = Localizer.T("Status.DefaultTip"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(hint, 3);
        grid.Children.Add(hint);

        return grid;
    }

    void AddFromToolbox()
    {
        if (_toolbox.SelectedIndex < 0)
        {
            SetStatus(Localizer.T("Status.SelectFirst"), error: true);
            return;
        }

        var (label, key) = ElementButtons[_toolbox.SelectedIndex];
        Run(() => _designer.Kernel.AddElementAtCenter(key));
        SetStatus(Localizer.T("Status.ElementAddedCenter", label));
    }

    UIElement BuildStatusBar()
    {
        _status.Foreground = new SolidColorBrush(Colors.DimGray);
        _status.Margin = new Thickness(10, 3, 10, 3);
        _status.TextWrapping = TextWrapping.NoWrap;

        return new Border
        {
            Background = new SolidColorBrush(Color(0xF0, 0xF0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
    }

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
    /// Ctrl+D 只订阅在画布自己的 <see cref="UIElement.KeyDown"/> 上：只有画布（或其子树）有焦点时事件才会到达，
    /// 属性面板的文本框按键不会被抢。WinUI 的 <see cref="KeyRoutedEventArgs"/> 不带修饰键，
    /// 与 Uno 宿主同一做法：自己跟踪 Control 的按下/松开，失焦时复位。
    /// </summary>
    void HookDuplicateShortcut()
    {
        _designer.KeyDown += OnDesignerKeyDown;
        _designer.KeyUp += OnDesignerKeyUp;
        _designer.LostFocus += (_, _) => _controlPressed = false;
    }

    void OnDesignerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
        {
            _controlPressed = true;
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
                SetJsonState(Localizer.T("JsonPanel.Sync", _designer.Kernel.Template.Elements.Count), error: false);
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
        SetJsonState(note ?? Localizer.T("JsonPanel.Sync", _designer.Kernel.Template.Elements.Count), error: false);
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

    void ExportPdf()
    {
        // Uno Skia 桌面头的文件选择器依赖平台实现，示例改为写到"文档"目录并回显路径。
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = AppContext.BaseDirectory;

        var path = Path.Combine(documents, "crossprint-uno-sample.pdf");
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

        var contentPanel = new StackPanel
        {
            Children =
            {
                productTitle,
                version,
                description,
                thirdPartyPanel,
            },
        };

        var dialog = new ContentDialog
        {
            Title = Localizer.T("About.Title"),
            Content = contentPanel,
            CloseButtonText = Localizer.T("About.Close"),
            XamlRoot = _designer.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    static void AddAt(Grid grid, UIElement element, int row, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        if (rowSpan > 1)
            Grid.SetRowSpan(element, rowSpan);
        grid.Children.Add(element);
    }

    static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(0xFF, r, g, b);
}
