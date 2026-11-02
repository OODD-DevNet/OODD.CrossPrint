using System;
using System.Collections.Generic;
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
using Microsoft.Win32;

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
    readonly ComboBox _paperCombo = new();
    readonly Button _rotateButton = new();
    readonly Button _customPaperButton = new();
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
        Content = root;

        BindKernel();

        if (warnings.Count > 0)
            SetStatus(Localizer.T("App.Status.Warnings", string.Join("；", warnings)), error: true);
        else
            SetStatus(Localizer.T("App.Status.Ready"));

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

        var loadComplexItem = new MenuItem { Header = Localizer.T("Menu.File.LoadComplex") };
        loadComplexItem.Click += (_, _) => LoadComplexSampleTemplate();
        fileMenu.Items.Add(loadComplexItem);

        var stylesItem = new MenuItem { Header = Localizer.T("Menu.File.StyleLibrary") };
        stylesItem.Click += (_, _) => OpenStyleLibrary();
        stylesItem.ToolTip = Localizer.T("Menu.File.StyleLibraryTip");
        fileMenu.Items.Add(stylesItem);

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
        menu.Items.Add(helpMenu);

        return menu;
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
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Duplicate"), DuplicateSelection, Localizer.T("Toolbar.Duplicate.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.Delete"), () => Run(() => _designer.Kernel.DeleteSelection()), Localizer.T("Toolbar.Delete.Tip")));
        editBar.Items.Add(new Separator());
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.BringForward"), () => Run(_designer.Kernel.BringForward), Localizer.T("Toolbar.BringForward.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.SendBackward"), () => Run(_designer.Kernel.SendBackward), Localizer.T("Toolbar.SendBackward.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.BringToFront"), () => Run(_designer.Kernel.BringToFront), Localizer.T("Toolbar.BringToFront.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.SendToBack"), () => Run(_designer.Kernel.SendToBack), Localizer.T("Toolbar.SendToBack.Tip")));
        editBar.Items.Add(new Separator());
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.FitToPage"), () => Run(_designer.FitToPage), Localizer.T("Toolbar.FitToPage.Tip")));
        editBar.Items.Add(MakeButton(Localizer.T("Toolbar.ZoomActual"), SetActualZoom, Localizer.T("Toolbar.ZoomActual.Tip")));
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
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var paper = BuildPaperPanel();
        Grid.SetRow(paper, 0);
        grid.Children.Add(paper);

        var toolbox = BuildToolbox();
        Grid.SetRow(toolbox, 1);
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

        _paperInfo.Foreground = Brushes.Gray;
        _paperInfo.TextWrapping = TextWrapping.Wrap;
        _paperInfo.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_paperInfo, 3);
        Grid.SetColumn(_paperInfo, 0);
        Grid.SetColumnSpan(_paperInfo, 2);
        grid.Children.Add(_paperInfo);

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
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        _toolbox.ItemsSource = Array.ConvertAll(ElementButtons, item => new ToolboxItem(item.Label, item.Key));
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
            Text = Localizer.T("Toolbox.Hint"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(hint, 3);
        grid.Children.Add(hint);

        return grid;
    }

    UIElement BuildStatusBar()
    {
        _status.Foreground = Brushes.DimGray;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(10, 3, 10, 3);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
    }

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
        // Ctrl+D 只在设计画布有焦点时生效：属性面板的文本框里按键不该被抢。
        if (!_designer.IsKeyboardFocusWithin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (e.Key != Key.D
            || (modifiers & ModifierKeys.Control) == 0
            || (modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) != 0)
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
            _designer.Kernel.SetCustomPaper(dialog.WidthMm, dialog.HeightMm);
            SetStatus(dialog.HeightMm > 0
                ? Localizer.T("Status.CustomPaperSet", dialog.WidthMm, dialog.HeightMm)
                : Localizer.T("Status.CustomContinuousPaperSet", dialog.WidthMm));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localizer.T("Error.PaperFailed", ex.Message), Localizer.T("Paper.Section"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var paper = _designer.Kernel.Template.Paper;
            var index = Array.FindIndex(
                PaperChoices,
                choice => choice.Key is not null
                    && string.Equals(choice.Key, paper.Preset, StringComparison.OrdinalIgnoreCase));
            _paperCombo.SelectedIndex = index >= 0 ? index : PaperChoices.Length - 1;

            var continuous = paper.IsContinuous;
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

    void ImportHiprintDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.T("Dialog.Import.Title"),
            Filter = $"{Localizer.T("Dialog.Import.HiprintFilter")} (*.js;*.json;*.txt)|*.js;*.json;*.txt|{Localizer.T("Dialog.Import.AllFiles")}(*.*)|*.*",
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

    /// <summary>读取输出目录下的复杂小票模板（80mm 连续纸长小票），替换当前设计器模板。</summary>
    void LoadComplexSampleTemplate()
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
                note += "；" + string.Join("；", result.Warnings);

            // LoadTemplate 会换内核：属性面板、纸张同步和 JSON 面板都要接到新内核上（同 hiprint 导入）。
            _designer.LoadTemplate(result.Template);
            BindKernel(note);
            SetStatus(note);
            FocusDesigner();
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.T("Status.ComplexLoadFailed", ex.Message), error: true);
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
