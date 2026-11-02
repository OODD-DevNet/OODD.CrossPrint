using System.Globalization;
using CrossPrint.Core;

namespace CrossPrint.Samples.WinForms;

/// <summary>
/// 自定义纸张对话框：纸宽必填，纸高留空表示连续纸（长度随内容自适应）。
/// 单位一律 mm，与模板存储口径一致；转换与校验在 <see cref="EditorKernel"/> 之前完成，
/// 非法值留在对话框里改，不会把半截状态写进模板。
/// </summary>
public sealed class PaperSizeDialog : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly TextBox _width = new();
    readonly TextBox _height = new();

    /// <summary>为当前纸张创建对话框，预填现有尺寸。</summary>
    /// <param name="current">当前纸张，用于预填。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        Text = "自定义纸张";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 200);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 8),
            ColumnCount = 2,
            RowCount = 5,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 3 ? 40 : 32));

        var widthLabel = new Label { Text = "纸宽（mm）", AutoSize = true, Anchor = AnchorStyles.Left };
        _width.Dock = DockStyle.Fill;
        _width.Margin = new Padding(0, 3, 0, 3);
        _width.Text = current.WidthMm.ToString("0.##", Inv);
        grid.Controls.Add(widthLabel, 0, 0);
        grid.Controls.Add(_width, 1, 0);

        var heightLabel = new Label { Text = "纸高（mm）", AutoSize = true, Anchor = AnchorStyles.Left };
        _height.Dock = DockStyle.Fill;
        _height.Margin = new Padding(0, 3, 0, 3);
        _height.Text = current.HeightMm > 0 ? current.HeightMm.ToString("0.##", Inv) : string.Empty;
        grid.Controls.Add(heightLabel, 0, 1);
        grid.Controls.Add(_height, 1, 1);

        var hint = new Label
        {
            Text = "纸高留空表示连续纸（长度随内容自适应）。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 0, 2),
        };
        grid.Controls.Add(hint, 0, 2);
        grid.SetColumnSpan(hint, 2);

        var orientation = new Label
        {
            Text = current.IsContinuous ? "方向：连续纸固定纵向" : "方向沿用当前设置",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 0, 2),
        };
        grid.Controls.Add(orientation, 0, 3);
        grid.SetColumnSpan(orientation, 2);

        var ok = new Button { Text = "确定", DialogResult = DialogResult.None, AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        ok.Click += (_, _) => Commit();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 0, 4);
        grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>纸宽（mm），仅在 <see cref="Form.DialogResult"/> 为 OK 后有效。</summary>
    public double WidthMm { get; private set; }

    /// <summary>纸高（mm）；0 表示连续纸。</summary>
    public double HeightMm { get; private set; }

    void Commit()
    {
        if (!double.TryParse(_width.Text.Trim(), NumberStyles.Float, Inv, out var width)
            || !double.IsFinite(width) || width <= 0)
        {
            Warn("纸宽必须是大于 0 的数字（mm）。", _width);
            return;
        }

        var heightText = _height.Text.Trim();
        var height = 0.0;
        if (heightText.Length > 0
            && (!double.TryParse(heightText, NumberStyles.Float, Inv, out height)
                || !double.IsFinite(height) || height < 0))
        {
            Warn("纸高必须是 0 或更大的数字（mm），留空表示连续纸。", _height);
            return;
        }

        WidthMm = width;
        HeightMm = height;
        DialogResult = DialogResult.OK;
    }

    void Warn(string message, Control focus)
    {
        MessageBox.Show(this, message, "自定义纸张", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focus.Focus();
    }
}
