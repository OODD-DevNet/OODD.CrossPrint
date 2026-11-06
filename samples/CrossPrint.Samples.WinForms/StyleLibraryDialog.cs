using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using CrossPrint.Core;
using CrossPrint.Designer;
using CrossPrint.I18n;

namespace CrossPrint.Samples.WinForms;

/// <summary>
/// 样式库对话框（WinForms）：左列样式清单（增删），右列选中样式的字段表单。
/// 字段模型 / 可空值读写 / 解析全部走共享层 <see cref="StyleLibrarySupport"/>，本文件只负责 UI 摆布与事件接线。
/// 所有变更经 <see cref="EditorKernel.UpdateStyle"/>，可撤销；删除被引用的样式由内核拦截并提示。
/// </summary>
public static class StyleLibraryDialog
{
    public static void Show(EditorKernel kernel, Form owner)
    {
        string? currentId = null;
        var syncing = false;

        var form = new Form
        {
            Text = Localizer.T("Menu.File.StyleLibrary"),
            Width = 720,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
        };

        var message = new Label
        {
            Dock = DockStyle.Bottom,
            ForeColor = System.Drawing.Color.Firebrick,
            Height = 18,
            Visible = false,
        };

        var list = new ListBox { Dock = DockStyle.Fill };

        var addBtn = new Button { Text = "新增", Width = 90 };
        var delBtn = new Button { Text = "删除", Width = 90 };
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(2) };
        leftButtons.Controls.Add(addBtn);
        leftButtons.Controls.Add(delBtn);

        var left = new Panel { Dock = DockStyle.Left, Width = 220 };
        left.Controls.Add(list);
        left.Controls.Add(leftButtons);

        var fieldControls = new Dictionary<string, Control>(StringComparer.Ordinal);
        var tlp = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        foreach (var def in StyleLibrarySupport.Fields)
        {
            Control editor = def.Kind switch
            {
                StyleFieldKind.Bool => new CheckBox { ThreeState = true, Anchor = AnchorStyles.Left },
                StyleFieldKind.Enum => new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Items = { StyleLibrarySupport.Unset },
                },
                _ => new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = 220 },
            };
            fieldControls[def.Key] = editor;

            var label = new Label { Text = def.LabelZh, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 6, 4) };
            tlp.Controls.Add(label, 0, row);
            tlp.Controls.Add(editor, 1, row);
            row++;
        }

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(tlp);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(split, 0, 0);
        outer.Controls.Add(message, 0, 1);
        form.Controls.Add(outer);

        void ShowMessage(string? text)
        {
            message.Text = text ?? string.Empty;
            message.Visible = text is not null;
        }

        void RefreshList(string? keepId)
        {
            currentId = keepId;
            syncing = true;
            try
            {
                var items = kernel.Template.Styles
                    .Select(s => new StyleItem { Id = s.Id, Display = $"{s.Name}（{s.Id}）" })
                    .ToList();
                list.Items.Clear();
                foreach (var item in items)
                    list.Items.Add(item);
                list.SelectedItem = keepId is null
                    ? (list.Items.Count > 0 ? list.Items[0] : null)
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
                            var b = raw as bool?;
                            cb.CheckState = b.HasValue ? (b.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
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
                StyleFieldKind.Bool => (fieldControls[def.Key] as CheckBox)?.CheckState == CheckState.Indeterminate
                    ? null
                    : (fieldControls[def.Key] as CheckBox)?.Checked,
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
                    tb.Validated += (_, _) => CommitField(def);
                    break;
                case CheckBox cb:
                    cb.CheckStateChanged += (_, _) => CommitField(def);
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
            currentId = (list.SelectedItem as StyleItem)?.Id;
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

        // 枚举下拉先填候选值（避免第一行刷新时列表还空）。
        foreach (var def in StyleLibrarySupport.Fields.Where(f => f.Kind == StyleFieldKind.Enum))
        {
            if (fieldControls[def.Key] is ComboBox cbx && def.EnumType is { } t)
            {
                foreach (var name in Enum.GetNames(t))
                    cbx.Items.Add(name);
                cbx.SelectedIndex = 0;
            }
        }

        RefreshList(kernel.Template.Styles.Count > 0 ? kernel.Template.Styles[0].Id : null);

        form.ShowDialog(owner);
    }

    sealed class StyleItem
    {
        public string Id = string.Empty;
        public string Display = string.Empty;
        public override string ToString() => Display;
    }
}
