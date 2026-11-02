namespace CrossPrint.Core;

/// <summary>
/// 单位换算的唯一实现点。模板存储单位是 mm 与 pt，像素只在渲染瞬间出现。
/// </summary>
public static class UnitConverter
{
    /// <summary>每英寸毫米数。</summary>
    public const double MmPerInch = 25.4;

    /// <summary>每英寸磅数。</summary>
    public const double PtPerInch = 72.0;

    /// <summary>毫米 → 像素（指定 DPI）。</summary>
    public static double MmToPx(double mm, double dpi) => mm / MmPerInch * dpi;

    /// <summary>像素 → 毫米（指定 DPI）。</summary>
    public static double PxToMm(double px, double dpi) => px * MmPerInch / dpi;

    /// <summary>磅 → 像素（指定 DPI）。</summary>
    public static double PtToPx(double pt, double dpi) => pt / PtPerInch * dpi;

    /// <summary>磅 → 毫米（与 DPI 无关）。</summary>
    public static double PtToMm(double pt) => pt / PtPerInch * MmPerInch;

    /// <summary>毫米 → PDF 点（PostScript point，1/72 英寸）。PDF 是矢量目标，页面无需高 DPI。</summary>
    public static double MmToPt(double mm) => mm / MmPerInch * PtPerInch;
}

/// <summary>页面四边留白（mm）。仅作安全区提示与物理边距参考，不参与元素坐标计算。</summary>
public sealed class ThicknessMm
{
    /// <summary>创建留白。</summary>
    public ThicknessMm()
    {
    }

    /// <summary>创建留白。</summary>
    public ThicknessMm(double top, double right, double bottom, double left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>上边距（mm）。</summary>
    public double Top { get; set; } = 10;

    /// <summary>右边距（mm）。</summary>
    public double Right { get; set; } = 10;

    /// <summary>下边距（mm）。</summary>
    public double Bottom { get; set; } = 10;

    /// <summary>左边距（mm）。</summary>
    public double Left { get; set; } = 10;

    /// <summary>深拷贝。</summary>
    public ThicknessMm Clone() => new(Top, Right, Bottom, Left);
}

/// <summary>
/// 纸张规格。<see cref="WidthMm"/>/<see cref="HeightMm"/> 恒为**纵向（Portrait）**尺寸，
/// Landscape 且纸高固定时由渲染引擎交换得到实际页面尺寸；连续纸（<see cref="HeightMm"/> 为 0）不交换，
/// 横向仍按 <see cref="WidthMm"/> 走纸、长度自适应内容。
/// </summary>
public sealed class PaperSpec
{
    /// <summary>纸张名预设键，如 <c>"A4"</c>；自定义纸张为 null。</summary>
    public string? Preset { get; set; } = PaperTypePresets.A4;

    /// <summary>纵向纸宽（mm）。</summary>
    public double WidthMm { get; set; } = 210;

    /// <summary>纵向纸高（mm）。0 表示连续纸（长度自适应内容）。</summary>
    public double HeightMm { get; set; } = 297;

    /// <summary>方向。</summary>
    public PaperOrientation Orientation { get; set; } = PaperOrientation.Portrait;

    /// <summary>实际页面宽度（mm，已应用方向）。连续纸不交换，宽度恒为纸宽。</summary>
    public double PageWidthMm => SwapsDimensions ? HeightMm : WidthMm;

    /// <summary>实际页面高度（mm，已应用方向）。0 表示自适应内容。</summary>
    public double PageHeightMm => SwapsDimensions ? WidthMm : HeightMm;

    /// <summary>是否为连续纸（高度自适应）。</summary>
    public bool IsContinuous => PageHeightMm <= 0;

    // 连续纸的 HeightMm=0 是"长度自适应内容"而不是一个可旋转的维度，交换后纸宽会变成 0。
    bool SwapsDimensions => Orientation == PaperOrientation.Landscape && HeightMm > 0;

    /// <summary>按纸张名预设构造。</summary>
    public static PaperSpec FromPreset(string preset)
    {
        if (!PaperTypePresets.TryGet(preset, out var size))
            throw new ArgumentException($"未知纸张预设 '{preset}'。可用：{string.Join(", ", PaperTypePresets.Names)}", nameof(preset));

        return new PaperSpec { Preset = preset, WidthMm = size.WidthMm, HeightMm = size.HeightMm };
    }

    /// <summary>深拷贝。</summary>
    public PaperSpec Clone() => new()
    {
        Preset = Preset,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        Orientation = Orientation,
    };
}

/// <summary>内置纸张预设表。</summary>
public static class PaperTypePresets
{
    /// <summary>单张纸尺寸。</summary>
    public readonly record struct SizeMm(double WidthMm, double HeightMm);

    /// <summary>A3。</summary>
    public const string A3 = "A3";

    /// <summary>A4。</summary>
    public const string A4 = "A4";

    /// <summary>A5。</summary>
    public const string A5 = "A5";

    /// <summary>B5。</summary>
    public const string B5 = "B5";

    /// <summary>80mm 热敏连续纸。</summary>
    public const string Receipt80 = "Receipt80";

    /// <summary>58mm 热敏连续纸。</summary>
    public const string Receipt58 = "Receipt58";

    /// <summary>100×150mm 物流面单。</summary>
    public const string Label100x150 = "Label100x150";

    /// <summary>自定义。</summary>
    public const string Custom = "Custom";

    static readonly Dictionary<string, SizeMm> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        [A3] = new SizeMm(297, 420),
        [A4] = new SizeMm(210, 297),
        [A5] = new SizeMm(148, 210),
        [B5] = new SizeMm(176, 250),
        [Receipt80] = new SizeMm(80, 0),
        [Receipt58] = new SizeMm(58, 0),
        [Label100x150] = new SizeMm(100, 150),
    };

    /// <summary>全部预设名（含 Custom）。</summary>
    public static IReadOnlyList<string> Names { get; } = [.. _map.Keys, Custom];

    /// <summary>查询预设尺寸。Custom 或未知名称返回 false。</summary>
    public static bool TryGet(string preset, out SizeMm size) => _map.TryGetValue(preset, out size);
}
