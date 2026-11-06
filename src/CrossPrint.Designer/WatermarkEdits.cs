using System;
using System.Globalization;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>水印的可编辑数值字段。</summary>
public enum WatermarkNumber
{
    /// <summary>字号（磅，4-200）。</summary>
    FontSizePt,

    /// <summary>整体不透明度（0-1）。</summary>
    Opacity,

    /// <summary>旋转角（度，-90~90）。</summary>
    RotationDeg,

    /// <summary>平铺间距（mm，4-400）。</summary>
    SpacingMm,
}

/// <summary>水印面板一次回填所需的全部字符串（宿主只负责把值塞进自己的控件）。</summary>
public readonly record struct WatermarkFields(
    bool Enabled,
    string Text,
    string Binding,
    string FontSizePt,
    string Opacity,
    string RotationDeg,
    string SpacingMm,
    string Color);

/// <summary>
/// 页面级水印编辑的<b>共享提交逻辑</b>：新对齐的 WinUI / Eto / Uno / MAUI 四个宿主复用同一份
/// 「快照 → 可撤销命令 → 回填」口径，避免每个宿主各写一套 apply/revert 导致撤销后字段错位、
/// 或解析/夹取范围不一致。Avalonia / WPF / WinForms 三个基准宿主保留等价的本地实现（阶段 A 之前已落地）。
/// UI 控件各平台不同，仍由宿主自己搭；这里只管数据、校验与命令。
/// </summary>
/// <remarks>
/// 提交捕获的是<b>提交瞬间的 Template 实例</b>：撤销/重做作用于原模板，
/// 换模板之后不会把旧改动误写到新模板上（比早期样板里直接取 <c>kernel.Template.Watermark</c> 更稳）。
/// 值没变一律不推命令，避免空灌撤销栈；非法输入返回 false 让宿主把控件刷回模型值。
/// </remarks>
public static class WatermarkEdits
{
    /// <summary>提交一次可撤销的水印修改。</summary>
    public static void Commit(EditorKernel kernel, string label, Action<WatermarkOptions> apply, Action<WatermarkOptions> revert)
    {
        var template = kernel.Template;
        kernel.CommitPropertyChange(
            label,
            () => apply(template.Watermark),
            () => revert(template.Watermark));
    }

    /// <summary>启用水印开关（值未变时不灌撤销栈）。</summary>
    public static void SetEnabled(EditorKernel kernel, bool value)
    {
        var old = kernel.Template.Watermark.Enabled;
        if (old == value)
            return;

        Commit(kernel, "启用水印", w => w.Enabled = value, w => w.Enabled = old);
    }

    /// <summary>水印文字（支持 <c>{{字段}}</c> 占位符；值未变时不灌撤销栈）。</summary>
    public static void SetText(EditorKernel kernel, string? text)
    {
        var old = kernel.Template.Watermark.Text;
        var value = text ?? string.Empty;
        if (string.Equals(old, value, StringComparison.Ordinal))
            return;

        Commit(kernel, "水印文字", w => w.Text = value, w => w.Text = old);
    }

    /// <summary>字段绑定：空白归一为 null（与 hiprint field 语义一致）。</summary>
    public static void SetBinding(EditorKernel kernel, string? text)
    {
        var old = kernel.Template.Watermark.Binding;
        var value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (string.Equals(old, value, StringComparison.Ordinal))
            return;

        Commit(kernel, "水印字段绑定", w => w.Binding = value, w => w.Binding = old);
    }

    /// <summary>数值字段：不变量解析 + 范围夹取；返回 false 表示输入非法（宿主应刷新控件）。</summary>
    public static bool SetNumber(EditorKernel kernel, WatermarkNumber field, string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var (min, max) = Range(field);
        value = Math.Max(min, Math.Min(max, value)); // net461 没有 Math.Clamp，本地等价夹取
        var old = Number(kernel.Template.Watermark, field);
        if (Math.Abs(old - value) < 1e-9)
            return true;

        Commit(kernel, Label(field),
            w => Write(w, field, value),
            w => Write(w, field, old));
        return true;
    }

    /// <summary>颜色 hex（#RRGGBB）；返回 false 表示解析失败（宿主应刷新控件）。</summary>
    public static bool SetColor(EditorKernel kernel, string? text)
    {
        if (!SKColor.TryParse((text ?? string.Empty).Trim(), out var color))
            return false;

        var old = kernel.Template.Watermark.Color;
        if (old.Red == color.Red && old.Green == color.Green && old.Blue == color.Blue)
            return true;

        Commit(kernel, "水印颜色", w => w.Color = color, w => w.Color = old);
        return true;
    }

    /// <summary>换模板 / 撤销后一次性读取全部字段值。</summary>
    public static WatermarkFields Read(EditorKernel kernel)
    {
        var w = kernel.Template.Watermark;
        return new WatermarkFields(
            w.Enabled,
            w.Text,
            w.Binding ?? string.Empty,
            Num(w.FontSizePt),
            Num(w.Opacity),
            Num(w.RotationDeg),
            Num(w.SpacingMm),
            $"#{w.Color.Red:X2}{w.Color.Green:X2}{w.Color.Blue:X2}");
    }

    static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    static (double Min, double Max) Range(WatermarkNumber field) => field switch
    {
        WatermarkNumber.FontSizePt => (4, 200),
        WatermarkNumber.Opacity => (0, 1),
        WatermarkNumber.RotationDeg => (-90, 90),
        _ => (4, 400),
    };

    static string Label(WatermarkNumber field) => field switch
    {
        WatermarkNumber.FontSizePt => "水印字号",
        WatermarkNumber.Opacity => "水印透明度",
        WatermarkNumber.RotationDeg => "水印角度",
        _ => "水印间距",
    };

    static double Number(WatermarkOptions w, WatermarkNumber field) => field switch
    {
        WatermarkNumber.FontSizePt => w.FontSizePt,
        WatermarkNumber.Opacity => w.Opacity,
        WatermarkNumber.RotationDeg => w.RotationDeg,
        _ => w.SpacingMm,
    };

    static void Write(WatermarkOptions w, WatermarkNumber field, double value)
    {
        switch (field)
        {
            case WatermarkNumber.FontSizePt: w.FontSizePt = value; break;
            case WatermarkNumber.Opacity: w.Opacity = value; break;
            case WatermarkNumber.RotationDeg: w.RotationDeg = value; break;
            default: w.SpacingMm = value; break;
        }
    }
}
