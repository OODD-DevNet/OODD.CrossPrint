using System;
using System.Collections.Generic;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.I18n;
using Eto.Drawing;
using Eto.Forms;

namespace CrossPrint.Samples.Eto;

/// <summary>
/// 样式库对话框（Eto）：左列样式清单（增删），右列选中样式的字段表单。
/// 字段模型 / 可空值读写 / 解析全部走共享层 <see cref="StyleLibrarySupport"/>，本文件只负责 UI 摆布与事件接线。
/// 所有变更经 <see cref="EditorKernel.UpdateStyle"/>，可撤销；删除被引用的样式由内核拦截并提示。
/// </summary>
public static class StyleLibraryDialog
{
    public static void Show(EditorKernel kernel, Form owner)
    {
        string? currentId = null;
        var syncing = false;

        // Eto 的 ListBox/ComboBox.Items 会被包装成 IListItem，无法回转型；
        // 因此 id 与枚举候选用并行列表保存，界面只认 SelectedIndex。
        var styleItems = new List<StyleItem>();
        var enumOptions = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var message = new Label { TextColor = Colors.Firebrick };

        var list = new ListBox { Width = 200 };

        var addBtn = new Button { Text = "新增" };
        var delBtn = new Button { Text = "删除" };
        var buttons = new TableLayout
        {
            Spacing = new Size(6, 0),
            Rows = { new TableRow(new TableCell(addBtn), new TableCell(delBtn)) },
        };
        var left = new StackLayout
        {
            Spacing = 6,
            Width = 200,
            Items = { buttons, list },
        };

        var fieldControls = new Dictionary<string, Control>(StringComparer.Ordinal);
        var fieldsTable = new TableLayout { Spacing = new Size(4, 4), Padding = new Padding(8) };
        foreach (var def in StyleLibrarySupport.Fields)
        {
            Control editor = def.Kind switch
            {
                StyleFieldKind.Bool => new CheckBox(),
                StyleFieldKind.Enum => new ComboBox(),
                _ => new TextBox(),
            };
            fieldControls[def.Key] = editor;

            var label = new Label { Text = def.LabelZh, VerticalAlignment = VerticalAlignment.Center };
            fieldsTable.Rows.Add(new TableRow(new TableCell(label), new TableCell(editor) { ScaleWidth = true }));
        }

        var scroll = new Scrollable { Content = fieldsTable };

        var body = new TableLayout();
        body.Rows.Add(new TableRow(
            new TableCell(left) { ScaleWidth = false },
            new TableCell(scroll) { ScaleWidth = true }) { ScaleHeight = true });

        var root = new TableLayout();
        root.Padding = new Padding(4);
        root.Spacing = new Size(0, 4);
        root.Rows.Add(new TableRow(new TableCell(body) { ScaleWidth = true }) { ScaleHeight = true });
        root.Rows.Add(new TableRow(new TableCell(message) { ScaleWidth = true }));

        var dialog = new Dialog
        {
            Title = Localizer.T("Menu.File.StyleLibrary"),
            ClientSize = new Size(720, 560),
            Content = root,
            Owner = owner,
        };

        void ShowMessage(string? text)
        {
            message.Text = text ?? string.Empty;
        }

        void RefreshList(string? keepId)
        {
            currentId = keepId;
            syncing = true;
            try
            {
                list.Items.Clear();
                styleItems.Clear();
                foreach (var s in kernel.Template.Styles)
                {
                    var item = new StyleItem { Id = s.Id, Display = $"{s.Name}（{s.Id}）" };
                    styleItems.Add(item);
                    list.Items.Add(item.Display);
                }

                var index = keepId is null
                    ? (styleItems.Count > 0 ? 0 : -1)
                    : styleItems.FindIndex(i => i.Id == keepId);
                list.SelectedIndex = index;
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
                            cb.Checked = raw as bool?;
                            break;
                        case ComboBox cbx:
                            var options = enumOptions[def.Key];
                            cbx.Items.Clear();
                            foreach (var o in options)
                                cbx.Items.Add(o);
                            cbx.SelectedIndex = raw is null ? 0 : Math.Max(0, Array.IndexOf(options, raw.ToString()));
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
                StyleFieldKind.Bool => (fieldControls[def.Key] as CheckBox)?.Checked,
                StyleFieldKind.Enum => (fieldControls[def.Key] as ComboBox) is { SelectedIndex: > 0 } cbx
                    ? StyleLibrarySupport.Parse(def, enumOptions[def.Key][cbx.SelectedIndex])
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
                    cb.CheckedChanged += (_, _) => CommitField(def);
                    break;
                case ComboBox cbx:
                    cbx.SelectedIndexChanged += (_, _) => { if (!syncing) CommitField(def); };
                    break;
            }
        }

        list.SelectedIndexChanged += (_, _) =>
        {
            if (syncing)
                return;
            currentId = list.SelectedIndex >= 0 ? styleItems[list.SelectedIndex].Id : null;
            LoadFields();
        };

        addBtn.Click += (_, _) =>
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

        delBtn.Click += (_, _) =>
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

        foreach (var def in StyleLibrarySupport.Fields.Where(f => f.Kind == StyleFieldKind.Enum))
        {
            if (fieldControls[def.Key] is ComboBox cbx && def.EnumType is { } t)
            {
                var options = new[] { StyleLibrarySupport.Unset }.Concat(Enum.GetNames(t)).ToArray();
                enumOptions[def.Key] = options;
                foreach (var o in options)
                    cbx.Items.Add(o);
                cbx.SelectedIndex = 0;
            }
        }

        RefreshList(kernel.Template.Styles.Count > 0 ? kernel.Template.Styles[0].Id : null);

        dialog.ShowModal(owner);
    }

    sealed class StyleItem
    {
        public string Id = string.Empty;
        public string Display = string.Empty;
        public override string ToString() => Display;
    }
}
