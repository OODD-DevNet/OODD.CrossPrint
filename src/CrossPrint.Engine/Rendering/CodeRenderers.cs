using CrossPrint.Core;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.OneD;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace CrossPrint.Engine.Rendering;

/// <summary>
/// 二维码与一维码一律按**模块矩形**矢量绘制，不落位图。
/// 三条实测依据（探针 §13/§14，ZXing.Net 0.16.11）：
/// <list type="bullet">
/// <item><description>QR 请求 <c>1x1</c> 时返回的是自然模块网格（"A"→21×21，加 MARGIN=0 后不含 ZXing 自带的 4 模块静区；默认 hint 会给到 29×29）。</description></item>
/// <item><description>1D 走 <c>encode(text, fmt, width, height)</c> 会把图案非均匀地拉伸到请求宽度：
/// Code128 "ABC-123" 自然 112 模块、首段真宽 2 模块，请求 500 宽后首段变成 26px 而第二段只有 8px——
/// 直接按位图画会让条宽比例失真，扫描枪拒读。因此改用公开的 <c>bool[] encode(string)</c> 拿裸模块序列。</description></item>
/// <item><description>1D 的 BitMatrix 不含可读文本（顶行/中行/底行黑像素数完全相同），文本必须我们自己画，
/// 于是它和正文共用 <see cref="TextStyle"/>，PDF 轮廓模式下也一致。</description></item>
/// <item><description>码制的填充画刷必须关抗锯齿（探针 §21）：40mm 二维码在 300dpi 下开 AA，
/// <c>QRCodeReader</c> 与 <c>MultiFormatReader</c> 都返回 null，关 AA 后同一张图立刻解回原内容。</description></item>
/// </list>
/// </summary>
public sealed class QrCodeRenderer : ElementRenderer<QrCodeElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.QrCode;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, QrCodeElement element, RenderContext context)
    {
        var o = element.QrCode;
        // 样式库级联：码点前景走字色族、静区底色走底色族、标题当文本套字体族；styler 的 color/bg 覆盖（脚本最高）
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element, o.Content);
        var fgColor = script?.Color ?? StyleCascade.Pick(o.ForegroundColor, SKColors.Black, named?.TextColor);
        var bgColor = script?.BackgroundColor ?? StyleCascade.PickNullable(o.BackgroundColor, SKColors.White, named?.BackgroundColor);
        var captionFamily = StyleCascade.Pick(o.CaptionFontFamily, "Microsoft YaHei", named?.FontFamily);
        var captionPt = StyleCascade.Pick(o.CaptionFontSizePt, 10.5, named?.FontSizePt);
        var captionColor = script?.Color ?? StyleCascade.Pick(o.CaptionColor, SKColors.Black, named?.TextColor);
        var captionAlign = StyleCascade.Pick(o.CaptionAlign, HorizontalAlign.Center, named?.HorizontalAlign);
        var captionLineSpacing = StyleCascade.Pick(o.CaptionLineSpacing, 1.2, named?.LineSpacing);

        var content = DataBinder.BindContent(o.Content, element.Binding, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context);
        // formatter 改编码内容（返回值即最终编码内容）
        content = ScriptSupport.FormatContent(context, element, o.Content, content);
        if (string.IsNullOrWhiteSpace(content))
        {
            context.Report.Add(element.Id, RenderIssueKind.QrCodeFailed,
                $"二维码内容为空（模板内容 '{o.Content}' 绑定后没有值）：请提供打印数据或设置测试数据；已绘制占位框。");
            CodePlaceholder.Draw(canvas, element, o.Content, bgColor, context);
            return;
        }

        BitMatrix grid;
        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 0,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.ERROR_CORRECTION] = ToLevel(o.ErrorCorrection),
            };
            grid = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 1, 1, hints);
        }
        catch (Exception ex) when (ex is WriterException or ArgumentException)
        {
            context.Report.Add(element.Id, RenderIssueKind.QrCodeFailed, $"二维码内容无法编码：{ex.Message}");
            return;
        }

        // 标题占掉码下方的一行（或按元素宽折出的多行）；码区是剩下的高度。
        var caption = o.ShowCaption ? DataBinder.BindText(o.Caption, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context) : string.Empty;
        using var captionStyle = caption.Length > 0
            ? new TextStyle(context, captionFamily, captionPt, bold: false, italic: false, caption, element.Id)
            : null;
        // hiprint 的标题 div 带 white-space:nowrap：标题永远单行，超宽时溢出不折行，
        // 否则每折一行都会从码区高度里再扣一行，把二维码压小。
        var captionLines = captionStyle is null
            ? []
            : captionStyle.Wrap(caption, element.WidthMm, wrap: false);
        var captionHeight = captionStyle is null ? 0 : captionLines.Count * captionStyle.LineHeight(captionLineSpacing);

        var codeHeight = Math.Max(0, element.HeightMm - captionHeight);
        var modules = grid.Width;
        var quiet = Math.Max(0, o.QuietZoneMm);
        var side = Math.Min(element.WidthMm, codeHeight);
        var moduleMm = o.ModuleWidthMm > 0 ? o.ModuleWidthMm : (side - 2 * quiet) / modules;
        if (moduleMm <= 0)
        {
            context.Report.Add(element.Id, RenderIssueKind.QrCodeFailed,
                $"元素 {element.WidthMm:0.##}×{codeHeight:0.##}mm（扣除标题后）放不下 {modules} 模块 + 两侧 {quiet:0.##}mm 静区，请放大元素或减小静区。");
            return;
        }

        // 静区计入底色块：扫描枪要把整块白（或浅色）当作符号起止
        var total = moduleMm * modules + 2 * quiet;
        var left = element.XMm + (element.WidthMm - total) / 2;
        var top = element.YMm + (codeHeight - total) / 2;

        if (bgColor is { } bg)
        {
            using var bgPaint = Paints.Fill(bg, antialias: false);
            canvas.DrawRect(new SKRect((float)left, (float)top, (float)(left + total), (float)(top + total)), bgPaint);
        }

        CodePainter.Paint(canvas, grid, left + quiet, top + quiet, moduleMm, fgColor);

        // 中心 Logo（白底衬垫，保证仍可扫描）
        var logoSrc = string.IsNullOrWhiteSpace(o.LogoField)
            ? o.LogoData
            : DataBinder.BindText(o.LogoField, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context);
        if (!string.IsNullOrWhiteSpace(logoSrc))
        {
            using var logo = TryDecodeImage(logoSrc);
            if (logo is not null)
            {
                var box = total * 0.28;
                var cx = left + total / 2;
                var cy = top + total / 2;
                using var pad = Paints.Fill(SKColors.White, antialias: false);
                canvas.DrawRect(new SKRect((float)(cx - box / 2), (float)(cy - box / 2), (float)(cx + box / 2), (float)(cy + box / 2)), pad);
                var (dw, dh) = FitLogo(logo.Width, logo.Height, box, o.LogoFit);
                var dx = (float)(cx - dw / 2);
                var dy = (float)(cy - dh / 2);
                canvas.DrawBitmap(logo, new SKRect(dx, dy, dx + (float)dw, dy + (float)dh));
            }
        }

        if (captionStyle is not null)
        {
            var lineHeight = captionStyle.LineHeight(captionLineSpacing);
            var captionTop = element.YMm + codeHeight + (captionHeight - captionLines.Count * lineHeight) / 2;
            var baselineOffset = captionStyle.BaselineOffset(lineHeight);
            for (var i = 0; i < captionLines.Count; i++)
            {
                var baseline = captionTop + i * lineHeight + baselineOffset;
                var x = captionStyle.AlignedX(element.XMm, element.WidthMm, captionLines[i], captionAlign);
                captionStyle.DrawLine(canvas, captionLines[i], x, baseline, captionColor);
            }
        }
    }

    static SKBitmap? TryDecodeImage(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;
        string? payload = data;
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma >= 0) payload = payload.Substring(comma + 1);
        }
        if (!ImageAcquisition.TryFromBase64(payload, out var bytes) || bytes is null)
            return null;
        try { return SKBitmap.Decode(bytes); }
        catch { return null; }
    }

    static (double, double) FitLogo(int w, int h, double box, QrLogoFit fit)
    {
        if (fit == QrLogoFit.Fill)
            return (box, box);
        var s = Math.Min(box / Math.Max(1, w), box / Math.Max(1, h));
        return (w * s, h * s);
    }

    internal static ErrorCorrectionLevel ToLevel(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => ErrorCorrectionLevel.L,
        QrErrorCorrection.Q => ErrorCorrectionLevel.Q,
        QrErrorCorrection.H => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M,
    };
}

/// <summary>一维码元素渲染器。</summary>
public sealed class BarcodeRenderer : ElementRenderer<BarcodeElement>
{
    /// <inheritdoc/>
    public override string TypeKey => ElementTypeKeys.Barcode;

    /// <inheritdoc/>
    protected override void Draw(SKCanvas canvas, BarcodeElement element, RenderContext context)
    {
        var o = element.Barcode;
        // 样式库级联：条与可读文本走字色族、底色走底色族、文本字体套字体族；styler 的 color/bg 覆盖（脚本最高）
        var named = StyleCascade.Find(context, o.StyleId);
        var script = ScriptSupport.StyleOf(context, element, o.Content);
        var barColor = script?.Color ?? StyleCascade.Pick(o.BarColor, SKColors.Black, named?.TextColor);
        var bgColor = script?.BackgroundColor ?? StyleCascade.PickNullable(o.BackgroundColor, SKColors.White, named?.BackgroundColor);
        var fontFamily = StyleCascade.Pick(o.FontFamily, "Microsoft YaHei", named?.FontFamily);
        var fontSizePt = StyleCascade.Pick(o.FontSizePt, 7, named?.FontSizePt);
        var captionFamily = StyleCascade.Pick(o.CaptionFontFamily, "Microsoft YaHei", named?.FontFamily);
        var captionPt = StyleCascade.Pick(o.CaptionFontSizePt, 10.5, named?.FontSizePt);
        var captionColor = script?.Color ?? StyleCascade.Pick(o.CaptionColor, SKColors.Black, named?.TextColor);
        var captionAlign = StyleCascade.Pick(o.CaptionAlign, HorizontalAlign.Center, named?.HorizontalAlign);
        var captionLineSpacing = StyleCascade.Pick(o.CaptionLineSpacing, 1.2, named?.LineSpacing);

        var content = DataBinder.BindContent(o.Content, element.Binding, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context);
        // formatter 改编码内容（返回值即最终编码内容）
        content = ScriptSupport.FormatContent(context, element, o.Content, content);

        // 空内容不能交给 ZXing：Code128 encode("") 会返回一段 24 模块的"合法"图案，
        // 拉到元素宽度就是几块巨大的黑砖——既不是条码也不是占位，现场最容易被当成渲染 bug。
        if (string.IsNullOrWhiteSpace(content))
        {
            context.Report.Add(element.Id, RenderIssueKind.BarcodeFailed,
                $"{o.Symbology} 内容为空（模板内容 '{o.Content}' 绑定后没有值）：请提供打印数据或设置测试数据；已绘制占位框。");
            CodePlaceholder.Draw(canvas, element, o.Content, bgColor, context);
            return;
        }

        bool[] pattern;
        try
        {
            // UPC-A 是"首位补 0 的 EAN-13"：ZXing 的 UPCAWriter 只是个包装器，不暴露裸模块序列，
            // 直接借 EAN13Writer 编码，可读文本仍画原始 UPC 内容。
            var encodeContent = o.Symbology == BarcodeSymbology.UpcA ? "0" + content : content;
            pattern = NewWriter(o.Symbology).encode(encodeContent);
        }
        catch (Exception ex) when (ex is WriterException or ArgumentException)
        {
            context.Report.Add(element.Id, RenderIssueKind.BarcodeFailed,
                $"{o.Symbology} 无法编码内容 '{content}'：{ex.Message}");
            return;
        }

        if (pattern.Length == 0)
        {
            context.Report.Add(element.Id, RenderIssueKind.BarcodeFailed, $"{o.Symbology} 返回了空条宽序列。");
            return;
        }

        var quiet = Math.Max(0, o.QuietZoneMm);
        var barWidthMm = o.ModuleWidthMm > 0 ? o.ModuleWidthMm : (element.WidthMm - 2 * quiet) / pattern.Length;
        if (barWidthMm <= 0)
        {
            context.Report.Add(element.Id, RenderIssueKind.BarcodeFailed,
                $"元素 {element.WidthMm:0.##}mm 放不下 {pattern.Length} 个模块 + 两侧 {quiet:0.##}mm 静区。");
            return;
        }

        using var style = o.ShowText
            ? new TextStyle(context, fontFamily, fontSizePt, bold: false, italic: false, content, element.Id)
            : null;

        // 标题占顶部一行、可读文本占底部一行，其余给条
        var captionText = o.ShowCaption ? DataBinder.BindText(o.Caption, context.Data, context.Report, element.Id, element.TestData, context.PreferTestData, context) : string.Empty;
        using var captionStyle = captionText.Length > 0
            ? new TextStyle(context, captionFamily, captionPt, bold: false, italic: false, captionText, element.Id)
            : null;
        var captionLines = captionStyle is null ? [] : captionStyle.Wrap(captionText, element.WidthMm, wrap: false);
        var captionHeight = captionStyle is null ? 0 : captionLines.Count * captionStyle.LineHeight(captionLineSpacing);

        // 可读文本占掉底部一行行高，剩下的全部给条；文本关掉时条占满元素高
        var textLineHeight = style?.LineHeight(1.2) ?? 0;
        var barsHeight = Math.Max(0, element.HeightMm - textLineHeight - captionHeight);
        var left = o.ModuleWidthMm > 0
            ? element.XMm + (element.WidthMm - (barWidthMm * pattern.Length + 2 * quiet)) / 2
            : element.XMm + quiet;
        var barTop = element.YMm + captionHeight;

        // EAN/UPC 家族的护栏条按 ISO/IEC 15420 比数据条长 5 个模块宽，扫描枪靠它判定符号起止。
        // 增量取模块宽（不是固定毫米）：条码拉宽/缩窄时护栏的视觉比例才跟着变；
        // 上限 25% 条高用来防止矮条把数据条压成零高度——总高还是 barsHeight，可读文本位置不受影响。
        var guards = GuardRanges(pattern.Length);
        var guardExtra = guards.Count == 0 ? 0 : Math.Min(5 * barWidthMm, barsHeight * 0.25);
        var dataHeight = barsHeight - guardExtra;

        if (bgColor is { } bg)
        {
            using var bgPaint = Paints.Fill(bg, antialias: false);
            canvas.DrawRect(Geometry.Bounds(element), bgPaint);
        }

        // 条与模块一律硬边：窄条在 300dpi 只有 3px，抗锯齿会把整条抹成灰，扫描枪按二值判定直接拒读
        using (var barPaint = Paints.Fill(barColor, antialias: false))
        {
            // 把连续的等色模块合并成一个矩形再画：模块多时段数远少于模块数，
            // 且合并边界仍落在 i*barWidthMm 上，条宽比例与 bool[] 完全一致。
            var runStart = 0;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == pattern[runStart])
                    continue;

                if (pattern[runStart])
                    DrawBar(barPaint, canvas, left + barWidthMm * runStart, barWidthMm * (i - runStart), barTop, dataHeight);

                runStart = i;
            }

            if (pattern[^1])
                DrawBar(barPaint, canvas, left + barWidthMm * runStart, barWidthMm * (pattern.Length - runStart), barTop, dataHeight);

            // 数据条先按矮高度画完，再把护栏的黑模块向下补齐——合并行程的边界不受影响
            if (guardExtra > 0)
            {
                foreach (var (start, length) in guards)
                {
                    for (var m = start; m < start + length && m < pattern.Length; m++)
                    {
                        if (pattern[m])
                            DrawBar(barPaint, canvas, left + barWidthMm * m, barWidthMm, barTop + dataHeight, guardExtra);
                    }
                }
            }
        }

        if (style is not null && o.ShowText)
        {
            var baseline = barTop + barsHeight + style.BaselineOffset(textLineHeight);
            var x = style.AlignedX(element.XMm, element.WidthMm, content, HorizontalAlign.Center);
            style.DrawLine(canvas, content, x, baseline, barColor);
        }

        if (captionStyle is not null)
        {
            var lineHeight = captionStyle.LineHeight(captionLineSpacing);
            var captionTop = element.YMm + (captionHeight - captionLines.Count * lineHeight) / 2;
            var baselineOffset = captionStyle.BaselineOffset(lineHeight);
            for (var i = 0; i < captionLines.Count; i++)
            {
                var baseline = captionTop + i * lineHeight + baselineOffset;
                var x = captionStyle.AlignedX(element.XMm, element.WidthMm, captionLines[i], captionAlign);
                captionStyle.DrawLine(canvas, captionLines[i], x, baseline, captionColor);
            }
        }
    }

    /// <summary>
    /// 一维码护栏区间（模块下标，左闭右开）。按 ZXing 编码结果的模块总数判定，实测（本机 ZXing）：
    /// EAN-13/UPC-A 95 = 3+42+5+42+3，起始 <c>101</c>、中段 <c>01010</c>、终止 <c>101</c>；
    /// EAN-8 67 = 3+28+5+28+3；UPC-E 51 = 3+42+6，终止护栏是 6 模块的 <c>010101</c>。
    /// 其余码制（Code128/Code39/ITF 等）没有独立护栏，返回空。
    /// </summary>
    internal static IReadOnlyList<(int Start, int Length)> GuardRanges(int modules) => modules switch
    {
        95 => [(0, 3), (45, 5), (92, 3)],
        67 => [(0, 3), (31, 5), (64, 3)],
        51 => [(0, 3), (45, 6)],
        _ => [],
    };

    internal static void DrawBar(SKPaint paint, SKCanvas canvas, double x, double width, double top, double height)
        => canvas.DrawRect(new SKRect((float)x, (float)top, (float)(x + width), (float)(top + height)), paint);

    internal static OneDimensionalCodeWriter NewWriter(BarcodeSymbology symbology) => symbology switch
    {
        BarcodeSymbology.Ean13 => new EAN13Writer(),
        BarcodeSymbology.Ean8 => new EAN8Writer(),
        BarcodeSymbology.UpcA => new EAN13Writer(),
        BarcodeSymbology.UpcE => new UPCEWriter(),
        BarcodeSymbology.Itf => new ITFWriter(),
        BarcodeSymbology.Codabar => new CodaBarWriter(),
        BarcodeSymbology.Code39 => new Code39Writer(),
        BarcodeSymbology.Code93 => new Code93Writer(),
        BarcodeSymbology.Msi => new MSIWriter(),
        _ => new Code128Writer(),
    };
}

/// <summary>
/// 把二维码/一维码绘入任意矩形（不取自 <see cref="PaperElement"/> 的几何），供表格单元格逐行出码复用。
/// 与 <see cref="QrCodeRenderer"/>/<see cref="BarcodeRenderer"/> 共用同一套 ZXing 编码与 <see cref="CodePainter"/> 矩阵绘制，
/// 仅把"定位到元素坐标"换成"定位到传入矩形"，并自动留静区。
/// </summary>
internal static class CellCodePainter
{
    /// <summary>把 <paramref name="content"/> 编码成二维码绘入 <paramref name="rect"/>（短边缩放、居中、留 1 模块静区）。</summary>
    public static void PaintQr(SKCanvas canvas, SKRect rect, string content, SKColor fg, SKColor? bg, QrErrorCorrection level)
    {
        if (string.IsNullOrWhiteSpace(content) || rect.Width <= 0 || rect.Height <= 0)
            return;

        BitMatrix grid;
        try
        {
            var qr = new QRCodeWriter();
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.ERROR_CORRECTION] = Map(level),
                [EncodeHintType.MARGIN] = 0,
            };
            grid = qr.encode(content, BarcodeFormat.QR_CODE, 1, 1, hints);
        }
        catch (Exception)
        {
            return;
        }

        var modules = grid.Width;
        if (modules <= 0)
            return;

        const int quiet = 1;
        var side = Math.Min(rect.Width, rect.Height);
        var moduleMm = side / (modules + 2 * quiet);
        var total = moduleMm * (modules + 2 * quiet);
        var left = rect.Left + (rect.Width - total) / 2;
        var top = rect.Top + (rect.Height - total) / 2;

        if (bg is { } b)
        {
            using var p = Paints.Fill(b, antialias: false);
            canvas.DrawRect(rect, p);
        }

        CodePainter.Paint(canvas, grid, left + moduleMm, top + moduleMm, moduleMm, fg);
    }

    /// <summary>把 <paramref name="content"/> 编码成一维码绘入 <paramref name="rect"/>（居中、留 2 模块静区，可选码下可读文本）。</summary>
    public static void PaintBarcode(SKCanvas canvas, SKRect rect, string content, SKColor bar, SKColor? bg,
        BarcodeSymbology symbology, bool showText, RenderContext? context)
    {
        if (string.IsNullOrWhiteSpace(content) || rect.Width <= 0 || rect.Height <= 0)
            return;

        bool[] pattern;
        try
        {
            pattern = BarcodeRenderer.NewWriter(symbology).encode(content);
        }
        catch (Exception)
        {
            return;
        }

        var count = pattern.Length;
        if (count <= 0)
            return;

        const int quietMods = 2;
        var textLine = (showText && context is not null) ? Math.Min(3.5, rect.Height * 0.35) : 0.0;
        var barWidthMm = rect.Width / (count + 2 * quietMods);
        var totalW = barWidthMm * (count + 2 * quietMods);
        var left = rect.Left + (rect.Width - totalW) / 2;
        var barsHeight = rect.Height - textLine;
        var barTop = rect.Top;

        var guards = BarcodeRenderer.GuardRanges(count);
        var guardExtra = guards.Count == 0 ? 0 : Math.Min(5 * barWidthMm, barsHeight * 0.25);
        var dataHeight = barsHeight - guardExtra;

        if (bg is { } b)
        {
            using var p = Paints.Fill(b, antialias: false);
            canvas.DrawRect(rect, p);
        }

        using (var barPaint = Paints.Fill(bar, antialias: false))
        {
            var runStart = 0;
            for (var i = 0; i < count; i++)
            {
                if (pattern[i] == pattern[runStart])
                    continue;

                if (pattern[runStart])
                    BarcodeRenderer.DrawBar(barPaint, canvas, left + barWidthMm * runStart, barWidthMm * (i - runStart), barTop, dataHeight);

                runStart = i;
            }

            if (pattern[^1])
                BarcodeRenderer.DrawBar(barPaint, canvas, left + barWidthMm * runStart, barWidthMm * (count - runStart), barTop, dataHeight);

            if (guardExtra > 0)
            {
                foreach (var (start, length) in guards)
                {
                    for (var m = start; m < start + length && m < count; m++)
                    {
                        if (pattern[m])
                            BarcodeRenderer.DrawBar(barPaint, canvas, left + barWidthMm * m, barWidthMm, barTop + dataHeight, guardExtra);
                    }
                }
            }
        }

        if (showText && textLine > 0 && context is not null)
        {
            using var style = new TextStyle(context, "Microsoft YaHei", (float)Math.Min(6, textLine * 2.2), bold: false, italic: false, content, null);
            var x = style.AlignedX(rect.Left, rect.Width, content, HorizontalAlign.Center);
            style.DrawLine(canvas, content, x, barTop + barsHeight + textLine * 0.7, bar);
        }
    }

    static ErrorCorrectionLevel Map(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => ErrorCorrectionLevel.L,
        QrErrorCorrection.M => ErrorCorrectionLevel.M,
        QrErrorCorrection.Q => ErrorCorrectionLevel.Q,
        _ => ErrorCorrectionLevel.H,
    };
}

/// <summary>
/// 码制内容为空时的占位框：浅灰虚线 + 居中显示未解析的模板表达式。
/// 设计器里一眼能看出"这里缺数据"，而不是留一块内容不明的黑砖或一片空白。
/// </summary>
internal static class CodePlaceholder
{
    /// <summary>绘制占位框。</summary>
    /// <param name="canvas">mm 画布。</param>
    /// <param name="element">元素（取框与字号定位）。</param>
    /// <param name="expression">模板里的原始内容，通常形如 <c>{{barcode}}</c>。</param>
    /// <param name="background">元素底色；null 表示透明。</param>
    /// <param name="context">渲染上下文（字体与告警）。</param>
    public static void Draw(SKCanvas canvas, PaperElement element, string? expression, SKColor? background, RenderContext context)
    {
        var rect = Geometry.Bounds(element);

        if (background is { } bg)
        {
            using var fill = Paints.Fill(bg, antialias: false);
            canvas.DrawRect(rect, fill);
        }

        using (var pen = Paints.Stroke(new SKColor(185, 185, 185), 0.25, [1.2, 0.8]))
            canvas.DrawRect(rect, pen);

        var label = string.IsNullOrWhiteSpace(expression) ? "(内容为空)" : expression.Trim();
        using var style = new TextStyle(context, "Microsoft YaHei", 7, bold: false, italic: false, label, element.Id);
        var text = style.Truncate("缺少数据 " + label, Math.Max(0, element.WidthMm - 2));
        if (text.Length == 0)
            return;

        var baseline = element.YMm + (element.HeightMm - style.AscentMm - style.DescentMm) / 2 + style.AscentMm;
        var x = element.XMm + (element.WidthMm - style.Measure(text)) / 2;
        style.DrawLine(canvas, text, x, baseline, new SKColor(130, 130, 130));
    }
}

/// <summary>模块网格 → 矩形。QR 用行内行程合并，把 N×N 次绘制压到接近 N×段数。</summary>
internal static class CodePainter
{
    /// <summary>从 <c>(originX, originY)</c> 起按模块边长绘制前景。</summary>
    /// <param name="canvas">mm 画布。</param>
    /// <param name="grid">模块网格。</param>
    /// <param name="originX">图案左上角横坐标（mm，静区之后）。</param>
    /// <param name="originY">图案左上角纵坐标（mm）。</param>
    /// <param name="moduleMm">单个模块边长（mm）。</param>
    /// <param name="foreground">前景色。</param>
    public static void Paint(SKCanvas canvas, BitMatrix grid, double originX, double originY, double moduleMm, SKColor foreground)
    {
        var modules = grid.Width;
        using var fg = Paints.Fill(foreground, antialias: false);

        for (var y = 0; y < grid.Height; y++)
        {
            var x = 0;
            while (x < modules)
            {
                if (!grid[x, y])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < modules && grid[x, y])
                    x++;

                canvas.DrawRect(
                    new SKRect(
                        (float)(originX + start * moduleMm),
                        (float)(originY + y * moduleMm),
                        (float)(originX + x * moduleMm),
                        (float)(originY + (y + 1) * moduleMm)),
                    fg);
            }
        }
    }
}
