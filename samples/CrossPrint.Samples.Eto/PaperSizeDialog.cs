using System.Globalization;
using CrossPrint.Core;
using Eto.Drawing;
using Eto.Forms;

namespace CrossPrint.Samples.Eto;

/// <summary>
/// 自定义纸张对话框：纸宽必填，纸高留空表示连续纸（长度随内容自适应）。
/// 单位一律 mm，与模板存储口径一致；转换与校验在本对话框内完成，
/// 非法值留在对话框里提示，不会把半截状态写进模板。
/// </summary>
internal sealed class PaperSizeDialog : Dialog
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly TextBox _width = new();
    readonly TextBox _height = new();
    readonly Label _error = new() { TextColor = Colors.Firebrick, Wrap = WrapMode.Word, Visible = false };

    /// <summary>为当前纸张创建对话框，预填现有尺寸。</summary>
    /// <param name="current">当前纸张，用于预填。</param>
    public PaperSizeDialog(PaperSpec current)
    {
        Title = "自定义纸张";
        Resizable = false;
        ShowInTaskbar = false;
        ClientSize = new Size(400, 248);

        _width.Text = current.WidthMm.ToString("0.##", Inv);
        _height.Text = current.HeightMm > 0 ? current.HeightMm.ToString("0.##", Inv) : string.Empty;

        var form = new TableLayout { Spacing = new Size(8, 6) };
        form.Rows.Add(new TableRow(
            new TableCell(new Label { Text = "纸宽（mm）", VerticalAlignment = VerticalAlignment.Center }),
            new TableCell(_width) { ScaleWidth = true }));
        form.Rows.Add(new TableRow(
            new TableCell(new Label { Text = "纸高（mm）", VerticalAlignment = VerticalAlignment.Center }),
            new TableCell(_height) { ScaleWidth = true }));

        var hint = new Label
        {
            Text = "纸高留空表示连续纸（长度随内容自适应）。",
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        var orientation = new Label
        {
            Text = current.IsContinuous ? "方向：连续纸固定纵向。" : "方向沿用当前设置。",
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close();

        var buttons = new TableLayout { Spacing = new Size(8, 0) };
        buttons.Rows.Add(new TableRow(
            new TableCell { ScaleWidth = true },
            new TableCell(ok) { ScaleWidth = false },
            new TableCell(cancel) { ScaleWidth = false }));

        var layout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Padding = new Padding(14, 12, 14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        layout.Items.Add(form);
        layout.Items.Add(hint);
        layout.Items.Add(orientation);
        layout.Items.Add(_error);
        layout.Items.Add(buttons);

        Content = layout;
        DefaultButton = ok;
        AbortButton = cancel;
    }

    /// <summary>用户是否点了“确定”；为 <c>false</c> 表示取消或不合法值时直接关闭。</summary>
    public bool Accepted { get; private set; }

    /// <summary>纸宽（mm），仅在 <see cref="Accepted"/> 为 <c>true</c> 后有效。</summary>
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
        Accepted = true;
        Close();
    }

    void Warn(string message, TextBox focus)
    {
        // 非法值留在对话框里提示，用户改完可以直接再点“确定”。
        _error.Text = message;
        _error.Visible = true;
        focus.Focus();
    }
}
