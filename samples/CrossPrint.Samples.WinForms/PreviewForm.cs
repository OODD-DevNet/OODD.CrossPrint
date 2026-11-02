using CrossPrint.Core;
using CrossPrint.Hosts.WinForms;

namespace CrossPrint.Samples.WinForms;

/// <summary>打印预览窗口：复用 PreviewControl，并在状态栏汇总渲染告警。</summary>
public sealed class PreviewForm : Form
{
    readonly PreviewControl _preview;
    readonly ToolStripStatusLabel _status;

    /// <summary>用模板与样例数据创建预览窗口。</summary>
    public PreviewForm(PrintTemplate template, object? data)
    {
        Text = "打印预览";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 780);
        MinimumSize = new Size(640, 520);

        _status = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _preview = new PreviewControl(template)
        {
            Data = data,
            Dock = DockStyle.Fill,
        };
        _preview.Rendered += OnPreviewRendered;

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(_status);

        Controls.Add(_preview);
        Controls.Add(statusStrip);
    }

    void OnPreviewRendered(object? sender, EventArgs e)
    {
        var report = _preview.LastReport;
        if (report is null || !report.HasIssues)
        {
            _status.Text = "渲染无告警";
            _status.ToolTipText = string.Empty;
            return;
        }

        var summary = string.Join("；", report.Issues.Take(3).Select(issue => issue.Message));
        _status.Text = $"渲染告警 {report.Issues.Count} 条：{summary}" + (report.Issues.Count > 3 ? "…" : string.Empty);
        _status.ToolTipText = report.ToString();
    }
}
