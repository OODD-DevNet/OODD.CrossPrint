using System;
using System.Collections.Generic;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.I18n;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrossPrint.Samples.Uno;

/// <summary>
/// 样式库对话框（Uno，复用 WinUI XAML 写法）：左列样式清单（增删），右列选中样式的字段表单。
/// 字段模型 / 可空值读写 / 解析全部走共享层 <see cref="StyleLibrarySupport"/>，本文件只负责 UI 摆布与事件接线。
/// </summary>
public static class StyleLibraryDialog
{
    public static async Task ShowAsync(XamlRoot? xamlRoot, EditorKernel kernel)
    {
        string? currentId = null;
        var syncing = false;

        var message = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.Firebrick),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 4),
            Visibility = Visibility.Collapsed,
        };

        var list = new ListBox { Width = 210, Margin = new Thickness(8) };

        var fieldControls = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        var fieldsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        foreach (var def in StyleLibrarySupport.Fields)
        {
            FrameworkElement editor = def.Kind switch
            {
                StyleFieldKind.Bool => new CheckBox { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center },
                StyleFieldKind.Enum => new ComboBox
                {
                    MinWidth = 160,
                    ItemsSource = new[] { StyleLibrarySupport.Unset }.Concat(Enum.GetNames(def.EnumType!)).ToArray(),
                },
                _ => new TextBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center },
            };
            fieldControls[def.Key] = editor;
            fieldsPanel.Children.Add(Row(def.LabelZh, editor));
        }

        var add = new Button { Content = "新增", Margin = new Thickness(8, 0, 4, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        var del = new Button { Content = "删除", Margin = new Thickness(4, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(add);
        left.Children.Add(del);
        left.Children.Add(list);

        var right = new ScrollViewer
        {
            Content = fieldsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(grid, 0);
        Grid.SetRow(message, 1);
        root.Children.Add(grid);
        root.Children.Add(message);

        void ShowMessage(string? text)
        {
            message.Text = text ?? string.Empty;
            message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshList(string? keepId)
        {
            currentId = keepId;
            syncing = true;
            try
            {
                var items = kernel.Template.Styles
                    .Select(s => new StyleItem(s.Id, $"{s.Name}（{s.Id}）"))
                    .ToList();
                list.ItemsSource = items;
                list.SelectedItem = keepId is null
                    ? (items.Count > 0 ? items[0] : null)
                    : items.FirstOrDefault(i => i.Id == keepId);
            }
            finally
            {
                syncing = false;
            }

            LoadFields();
        }

        void LoadFields()
        {
            syncing = true;
            try
            {
                var s = currentId is null ? null : kernel.Template.FindStyle(currentId);
                foreach (var def in StyleLibrarySupport.Fields)
                {
                    var raw = s is null ? null : StyleLibrarySupport.GetValue(s, def.Key);
                    switch (fieldControls[def.Key])
                    {
                        case TextBox tb:
                            tb.Text = StyleLibrarySupport.Format(def, raw);
                            break;
                        case CheckBox cb:
                            cb.IsChecked = raw as bool?;
                            break;
                        case ComboBox cbx:
                            cbx.SelectedItem = raw is null ? StyleLibrarySupport.Unset : raw.ToString();
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
                StyleFieldKind.Bool => (fieldControls[def.Key] as CheckBox)?.IsChecked,
                StyleFieldKind.Enum => (fieldControls[def.Key] as ComboBox)?.SelectedItem is string sel && sel != StyleLibrarySupport.Unset
                    ? StyleLibrarySupport.Parse(def, sel)
                    : null,
                _ => StyleLibrarySupport.Parse(def, (fieldControls[def.Key] as TextBox)!.Text ?? string.Empty),
            };

            try
            {
                kernel.UpdateStyle(currentId, "修改样式 " + def.LabelZh, s => StyleLibrarySupport.SetValue(s, def.Key, value));
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
                case TextBox tb:
                    tb.LostFocus += (_, _) => CommitField(def);
                    break;
                case CheckBox cb:
                    cb.Click += (_, _) => CommitField(def);
                    break;
                case ComboBox cbx:
                    cbx.SelectionChanged += (_, _) => { if (!syncing) CommitField(def); };
                    break;
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            if (syncing)
                return;
            currentId = (list.SelectedItem as StyleItem)?.Id;
            LoadFields();
        };

        add.Click += (_, _) =>
        {
            try
            {
                var style = kernel.AddStyle();
                ShowMessage(null);
                RefreshList(style.Id);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        };

        del.Click += (_, _) =>
        {
            if (currentId is null)
                return;
            try
            {
                kernel.RemoveStyle(currentId);
                ShowMessage(null);
                RefreshList(null);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message);
            }
        };

        RefreshList(kernel.Template.Styles.Count > 0 ? kernel.Template.Styles[0].Id : null);

        var dialog = new ContentDialog
        {
            Title = Localizer.T("Menu.File.StyleLibrary"),
            Content = root,
            CloseButtonText = Localizer.T("About.Close"),
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }

    static Grid Row(string label, FrameworkElement editor)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    sealed record StyleItem(string Id, string Display)
    {
        public override string ToString() => Display;
    }
}
