using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 现代化线性图标：用 <see cref="StreamGeometry"/> 绘制细线图标，配 Tooltip 用在顶部栏、组件库与浮动工具条上。无外部图标资源依赖。
/// </summary>
public static class DesignerIcons
{
    const string Ns = "CrossPrint.Samples.Avalonia;component/";

    static readonly Dictionary<string, string> Paths = new()
    {
        ["text"] = "M5 6h14M12 6v12M9 18h6",
        ["line"] = "M5 19L19 5",
        ["rectangle"] = "M4 6h16v12H4z",
        ["ellipse"] = "M12 6c3.3 0 6 2.7 6 6s-2.7 6-6 6-6-2.7-6-6 2.7-6 6-6z",
        ["shape"] = "M12 4l7 5-2.7 8H7.7L5 9l7-5zM5 9h14M12 4v13",
        ["image"] = "M4 5h16v14H4zM4 15l4-4 4 4 3-3 5 5",
        ["qrcode"] = "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h3v3h-3zM18 13h3v3h-3zM13 18h3v3h-3zM18 18h3v3h-3z",
        ["barcode"] = "M4 5v14M7 5v14M10 5v14M13 5v14M16 5v14M19 5v14",
        ["table"] = "M3 5h18v14H3zM3 10h18M3 15h18M9 5v14M15 5v14",
        ["search"] = "M11 4a7 7 0 105.2 11.7L21 21M11 4a7 7 0 100 14 7 7 0 000-14z",
        ["undo"] = "M10 8L5 13l5 5M5 13h9a4 4 0 0 1 0 8h-2",
        ["redo"] = "M14 8l5 5-5 5M19 13h-9a4 4 0 0 0 0 8h2",
        ["preview"] = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z M12 9a3 3 0 100 6 3 3 0 000-6z",
        ["export"] = "M12 16V4M8 8l4-4 4 4M4 20h16",
        ["plus"] = "M12 5v14M5 12h14",
        ["trash"] = "M5 7h14M9 7V5h6v2M7 7l1 13h8l1-13",
        ["copy"] = "M9 9h11v11H9zM5 15H4V4h11v1",
        ["layers"] = "M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5",
        ["database"] = "M12 3c4.4 0 8 1.3 8 3s-3.6 3-8 3-8-1.3-8-3 3.6-3 8-3zM4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6",
        ["grid"] = "M3 3h18v18H3zM9 3v18M15 3v18M3 9h18M3 15h18",
        ["fit"] = "M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5",
        ["zoomIn"] = "M11 4a7 7 0 105 11.7L21 21M11 4a7 7 0 100 14 7 7 0 000-14zM11 8v6M8 11h6",
        ["zoomOut"] = "M11 4a7 7 0 105 11.7L21 21M11 4a7 7 0 100 14 7 7 0 000-14zM8 11h6",
        ["menu"] = "M4 6h16M4 12h16M4 18h16",
        ["logo"] = "M4 4h16v16H4z",
        ["chevron"] = "M9 6l6 6-6 6",
        ["close"] = "M6 6l12 12M18 6L6 18",
        ["eye"] = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z",
        ["page"] = "M6 3h9l4 4v14H6zM14 3v4h4",
        ["printer"] = "M7 8V3h10v5M7 16H5a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-2M7 13h10v8H7z",
    };

    /// <summary>生成描边线性图标（默认 16×16）。</summary>
    public static global::Avalonia.Controls.Shapes.Path Stroke(string key, IBrush? foreground = null, double size = 16, double thickness = 1.6)
    {
        var data = Paths.TryGetValue(key, out var d) ? d : Paths["rectangle"];
        return new global::Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(data),
            Stroke = foreground ?? Brushes.White,
            StrokeThickness = thickness,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
