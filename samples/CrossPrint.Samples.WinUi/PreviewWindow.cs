using System;
using System.Collections.Generic;
using System.Linq;
using CrossPrint.Core;
using CrossPrint.Hosts.WinUi;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CrossPrint.Samples.WinUi;

/// <summary>打印预览窗口：复用 <see cref="PreviewControl"/>，并在状态栏汇总纸张、缩放与渲染告警。</summary>
public sealed class PreviewWindow : Window
{
    readonly PreviewControl _preview;
    readonly TextBlock _status;

    /// <summary>用模板与样例数据创建预览窗口。</summary>
    /// <param name="template">要预览的模板。</param>
    /// <param name="data">数据绑定源。</param>
    public PreviewWindow(PrintTemplate template, object? data)
    {
        ArgumentNullException.ThrowIfNull(template);

        Title = "打印预览";

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 4, 10, 4),
            Text = "正在渲染…",
        };

        _preview = new PreviewControl(template) { Data = data, Margin = new Thickness(8) };
        _preview.Rendered += OnPreviewRendered;

        Content = BuildLayout();
        AppWindow.Resize(new SizeInt32(920, 780));
    }

    /// <summary>预览画布，宿主可继续调整叠加层或数据。</summary>
    public PreviewControl Preview => _preview;

    Grid BuildLayout()
    {
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(Colors.White),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var canvasHost = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            Child = _preview,
        };
        Grid.SetRow(canvasHost, 0);
        root.Children.Add(canvasHost);

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
        Grid.SetRow(statusBar, 1);
        root.Children.Add(statusBar);
        return root;
    }

    void OnPreviewRendered(object? sender, EventArgs e)
    {
        var kernel = _preview.Kernel;
        var paper = kernel.Template.Paper;
        var header = $"纸张 {paper.PageWidthMm:0.##} × {kernel.PageHeightMm:0.##} mm，缩放 {kernel.Viewport.Zoom * 100:0.#}%";

        var report = _preview.LastReport;
        if (report is null || !report.HasIssues)
        {
            _status.Text = header + "；渲染无告警";
            return;
        }

        IEnumerable<string> messages = report.Issues.Take(3).Select(issue => issue.Message);
        _status.Text = header
            + $"；渲染告警 {report.Issues.Count} 条："
            + string.Join("；", messages)
            + (report.Issues.Count > 3 ? "…" : string.Empty);
    }
}
