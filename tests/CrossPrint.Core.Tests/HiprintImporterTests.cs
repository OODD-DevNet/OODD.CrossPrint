using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// hiprint 模板导入器的回归测试。三份真实夹具各自钉住一条归一化/映射机制：
/// <list type="bullet">
/// <item><description>
/// <c>hiprint-template7.js</c> 是完整 JS 模块（import、lodash 调用、注释、反引号函数体）。
/// 导入成功证明归一化器的「逐个顶层对象枚举 + 反引号模板字符串整体跳过」机制：
/// 关掉顶层对象枚举，或把反引号里的 <c>{ }</c> 当成对象边界，该文件会解析失败，模板7 的用例必红。
/// </description></item>
/// <item><description>
/// <c>hiprint-template1.js</c> 的表头里有 <c>checked:false</c> 的“性别”列。导入结果 5 列而不是 6 列，
/// 证明 <c>ReadCell.Hidden</c> + 网格放置会跳过隐藏表头；去掉该判断，列数、“不出现性别列”的断言必红。
/// </description></item>
/// <item><description>
/// <c>hiprint-getjson.json</c> 以 <c>"panels":</c> 开头、尾部还跟着 <c>export default { ... }</c> 代码。
/// 导入成功证明先补外层花括号再做片段提取；关掉该分支，getjson 用例会直接抛异常。
/// </description></item>
/// </list>
/// 合成用例覆盖多面板选择、未知/畸形元素跳过、连续纸、文本/条码/二维码/图片/多行表头映射与序列化往返。
/// 几何断言全部来自实际导入结果，mm 容差 0.01。
/// <para>
/// 注：template4 夹具中只有第二个表格（field=product_info）带 <c>tableBorder:noBorder</c>；
/// 第一个表格（field=work_info）没有任何边框配置，实测保留默认网格（ShowBorder=true、GridColor=黑）。
/// 这里按实测行为断言，不按“两个表格都没有边框”的假设。
/// </para>
/// </summary>
public class HiprintImporterTests
{
    const string TwoPanelTemplate = """
    {
      "panels": [
        {
          "index": 0, "name": "第一联", "width": 210, "height": 297,
          "printElements": [
            { "options": { "left": 10, "top": 10, "title": "第一联" }, "printElementType": { "type": "text" } }
          ]
        },
        {
          "index": 1, "name": "第二联", "width": 100, "height": 150,
          "printElements": [
            { "options": { "left": 20, "top": 20, "title": "第二联" }, "printElementType": { "type": "text" } }
          ]
        }
      ]
    }
    """;

    // ---------------- 夹具与合成模板辅助 ----------------

    static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hiprint", name));

    static HiprintImportResult ImportFixture(string name) => HiprintTemplateImporter.Import(Fixture(name));

    static string TextElementJson(string optionsJson)
        => "{ \"options\": " + optionsJson + ", \"printElementType\": { \"type\": \"text\" } }";

    static string TemplateWithElements(params string[] elementJson)
        => "{ \"panels\": [ { \"width\": 210, \"height\": 297, \"printElements\": ["
           + string.Join(",", elementJson)
           + "] } ] }";

    static HiprintImportResult ImportElements(params string[] elementJson)
        => HiprintTemplateImporter.Import(TemplateWithElements(elementJson));

    static HiprintImportResult ImportSingleElement(string optionsJson, string type = "text")
        => HiprintTemplateImporter.Import(TemplateWithElements(
            "{ \"options\": " + optionsJson + ", \"printElementType\": { \"type\": \"" + type + "\" } }"));

    static string TableWithOptionsJson(string optionsBody)
        => "{ \"options\": { \"left\": 5, \"top\": 5, \"width\": 300, \"height\": 20, " + optionsBody
           + " }, \"printElementType\": { \"type\": \"table\" } }";

    static void AssertIssue(HiprintImportResult result, string code, string? elementId = null)
    {
        var issue = result.Issues.FirstOrDefault(i => i.Code == code);
        issue.Should().NotBeNull($"导入结果应记录 {code}，不能静默丢弃能力差异");
        if (elementId is not null)
            issue!.ElementId.Should().Be(elementId, $"{code} 要能指回具体元素");
    }

    // ---------------- hiprint-template1.js ----------------

    [Fact]
    public void 模板1_纸张为A4纵向且上下留白按pt换算()
    {
        var result = ImportFixture("hiprint-template1.js");

        result.Template.Paper.Preset.Should().Be(PaperTypePresets.A4);
        result.Template.Paper.WidthMm.Should().Be(210);
        result.Template.Paper.HeightMm.Should().Be(297);
        result.Template.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
        result.Template.Paper.IsContinuous.Should().BeFalse("height=297 是固定纸张");

        result.Template.Margins.Top.Should().BeApproximately(17.4625, 0.01, "paperHeader=49.5pt");
        result.Template.Margins.Bottom.Should().BeApproximately(21.8333333, 0.01, "297mm - paperFooter 780pt");
        result.Template.Margins.Left.Should().Be(0);
        result.Template.Margins.Right.Should().Be(0);
    }

    [Fact]
    public void 模板1_三十三个元素按顺序分配连续id()
    {
        var result = ImportFixture("hiprint-template1.js");

        result.Template.Elements.Should().HaveCount(33);
        result.Template.Elements.Select(e => e.Id)
            .Should().Equal(Enumerable.Range(1, 33).Select(i => $"el-{i}"), "id 必须按 printElements 顺序从 el-1 递增");
    }

    [Fact]
    public void 模板1_首个文本保留字号加粗与居中()
    {
        var result = ImportFixture("hiprint-template1.js");

        var first = result.Template.Elements[0].Should().BeOfType<TextElement>().Subject;
        first.Id.Should().Be("el-1");
        first.Text.Content.Should().Be("HiPrint自定义模块打印插件");
        first.Text.FontSizePt.Should().Be(19);
        first.Text.Bold.Should().BeTrue("fontWeight=600 按数值 >=600 视为加粗");
        first.Text.HorizontalAlign.Should().Be(HorizontalAlign.Center);
        first.Text.VerticalAlign.Should().Be(VerticalAlign.Top, "hiprint 只有显式 middle/bottom 才加对齐类，默认顶对齐");
        first.Text.FontFamily.Should().Be("SimSun", "print-lock.css 的默认字体，模板宽度按它的半角 ASCII 量出来");
        first.XMm.Should().BeApproximately(61.9125, 0.01, "left=175.5pt");
    }

    [Fact]
    public void 模板1_字段文本拼出标题并保留下划线与红色()
    {
        var result = ImportFixture("hiprint-template1.js");

        var fieldText = result.Template.Elements
            .OfType<TextElement>()
            .Single(t => t.Text.Content == "姓名：{{name}}");

        fieldText.Text.Underline.Should().BeTrue("textDecoration=underline");
        fieldText.Text.Color.Should().Be(new SKColor(0xF0, 0x08, 0x08), "color=#f00808");
        fieldText.Text.HorizontalAlign.Should().Be(HorizontalAlign.Center);
    }

    [Fact]
    public void 模板1_二维码内容标题与纠错等级()
    {
        var result = ImportFixture("hiprint-template1.js");

        var qr = result.Template.Elements.OfType<QrCodeElement>().Should().ContainSingle().Subject;
        qr.QrCode.Content.Should().Be("123456789");
        qr.QrCode.Caption.Should().Be("123456789");
        qr.QrCode.ShowCaption.Should().BeTrue("未设置 hideTitle");
        qr.QrCode.ErrorCorrection.Should().Be(QrErrorCorrection.M, "缺省纠错等级为 M");
        qr.QrCode.CaptionFontSizePt.Should().Be(9, "标题实测 computed font-size 12px = 9pt，不是元素缺省以外的任何值");
        qr.QrCode.CaptionLineSpacing.Should().BeApproximately(9.75 / 9, 0.0001, "标题行高同样是固定 9.75pt");
    }

    [Fact]
    public void 未写lineHeight时行距按固定975pt换算()
    {
        var result = ImportFixture("hiprint-template1.js");

        // 这份模板里"姓名：{{name}}"没有 lineHeight，字号也是缺省 9pt。
        var fieldText = result.Template.Elements
            .OfType<TextElement>()
            .Single(t => t.Text.Content == "姓名：{{name}}");
        fieldText.Text.FontSizePt.Should().Be(9);
        fieldText.Text.LineSpacing.Should().BeApproximately(9.75 / 9, 0.0001,
            "print-lock.css 的 line-height: 9.75pt 是固定长度，不随字号缩放");

        // 有 lineHeight 的元素仍按 lineHeight / fontSize 精确换算。
        var first = (TextElement)result.Template.Elements[0];
        first.Text.LineSpacing.Should().BeApproximately(26.0 / 19, 0.0001, "lineHeight=26pt / fontSize=19pt");
    }

    [Fact]
    public void 模板1_条码内容与码制()
    {
        var result = ImportFixture("hiprint-template1.js");

        var barcode = result.Template.Elements.OfType<BarcodeElement>().Should().ContainSingle().Subject;
        barcode.Barcode.Content.Should().Be("123456789");
        barcode.Barcode.Symbology.Should().Be(BarcodeSymbology.Code128);
    }

    [Fact]
    public void 模板1_存在横竖线段且虚线与线宽正确()
    {
        var result = ImportFixture("hiprint-template1.js");
        var lines = result.Template.Elements.OfType<LineElement>().ToList();

        lines.Should().Contain(l => l.Line.Direction == LineDirection.Vertical, "模板里有 vline");
        lines.Should().Contain(l => l.Line.Direction == LineDirection.Horizontal, "模板里有 hline");

        var dottedVline = lines.Single(l =>
            l.Line.Direction == LineDirection.Vertical && Math.Abs(l.XMm - 8.9958) < 0.01);
        dottedVline.Line.StrokeWidthMm.Should().BeApproximately(0.2645833, 0.001, "borderWidth 缺省 0.75pt");
        dottedVline.Line.DashPatternMm.Should().NotBeNull("borderStyle=dotted 必须转成虚线段长");
        dottedVline.Line.DashPatternMm!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void 模板1_表格跳过checked为false的表头列()
    {
        var result = ImportFixture("hiprint-template1.js");

        var table = result.Template.Elements.OfType<TableElement>().Should().ContainSingle().Subject;
        table.Table.Columns.Should().HaveCount(5, "6 个表头单元格里 checked:false 的“性别”被跳过");
        table.Table.Columns.Select(c => c.Title).Should().NotContain("性别", "隐藏列不能变成第 6 列");
        table.Table.HeaderRows.Should().HaveCount(5, "隐藏表头也不应出现在表头单元格列表里");
    }

    [Fact]
    public void 模板1_表格汇总列与计数合计前缀()
    {
        var result = ImportFixture("hiprint-template1.js");
        var table = result.Template.Elements.OfType<TableElement>().Single();

        var nameColumn = table.Table.Columns.Single(c => c.Title == "姓名");
        nameColumn.Summary.Should().Be(TableSummary.Count, "tableSummary=count");
        nameColumn.SummaryLabel.Should().Be("计数：");
        nameColumn.Field.Should().Be("name");

        var countColumn = table.Table.Columns.Single(c => c.Title == "数量");
        countColumn.Summary.Should().Be(TableSummary.Sum, "tableSummary=sum");
        countColumn.SummaryLabel.Should().Be("合计：");
        countColumn.Field.Should().Be("count");
    }

    [Fact]
    public void 模板1_表格列宽缩放后合计等于元素宽度()
    {
        var result = ImportFixture("hiprint-template1.js");
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.WidthMm.Should().BeApproximately(180.4458, 0.01, "width=511.5pt");
        table.Table.Columns.Sum(c => c.WidthMm)
            .Should().BeApproximately(180.4458, 0.01, "显式列宽按元素宽度等比缩放");
        table.Table.Columns.Sum(c => c.WidthMm)
            .Should().BeApproximately(table.WidthMm, 0.01, "列宽合计必须铺满元素宽度");
    }

    [Fact]
    public void 模板1_html降级为虚线矩形并留痕()
    {
        var result = ImportFixture("hiprint-template1.js");

        var placeholder = result.Template.Elements
            .OfType<RectangleElement>()
            .Should().ContainSingle(r => r.Rectangle.DashPatternMm != null && r.Rectangle.DashPatternMm.Length > 0)
            .Subject;

        placeholder.Id.Should().Be("el-31");
        placeholder.Rectangle.StrokeWidthMm.Should().BeGreaterThan(0);
        AssertIssue(result, "hiprint.html-unsupported", "el-31");
    }

    [Fact]
    public void 模板1_各项降级都记录稳定问题码且没有错误()
    {
        var result = ImportFixture("hiprint-template1.js");

        string[] expected =
        [
            "hiprint.page-number-unsupported",
            "hiprint.image-missing",
            "hiprint.fixed-repeat",
            "hiprint.long-text-pagination",
            "hiprint.html-unsupported",
        ];
        foreach (var code in expected)
            result.Issues.Should().Contain(i => i.Code == code, $"模板1 必须记录 {code}");

        AssertIssue(result, "hiprint.image-missing", "el-4");
        AssertIssue(result, "hiprint.fixed-repeat", "el-18");
        AssertIssue(result, "hiprint.long-text-pagination", "el-23");
        AssertIssue(result, "hiprint.html-unsupported", "el-31");

        result.HasErrors.Should().BeFalse("这些都只是降级警告，元素仍然导入了");
        result.Issues.Should().NotContain(i => i.Severity == HiprintImportSeverity.Error);
    }

    // ---------------- hiprint-getjson.json ----------------

    [Fact]
    public void getjson_以panels片段开头时仍能提取完整模板()
    {
        var raw = Fixture("hiprint-getjson.json");
        raw.TrimStart().Should().StartWith("\"panels\":", "夹具故意去掉了外层花括号，尾部还跟着 export default");
        raw.Should().Contain("export default", "片段之后还有模块代码，不能整段当 JSON 解析");

        var result = HiprintTemplateImporter.Import(raw);

        result.Template.Elements.Should().HaveCount(33, "片段提取结果应与 template1 一致");
        result.Template.Paper.Preset.Should().Be(PaperTypePresets.A4);
        result.Template.Paper.WidthMm.Should().Be(210);
        result.Template.Paper.HeightMm.Should().Be(297);
        result.Template.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
    }

    // ---------------- invoice-getjson.json（用户实际导出的发票模板） ----------------

    [Fact]
    public void 发票getJson_导入全部51个元素且无错误()
    {
        var result = ImportFixture("invoice-getjson.json");

        result.HasErrors.Should().BeFalse();
        result.Panels.Should().ContainSingle();
        result.Template.Paper.PageWidthMm.Should().Be(210);
        result.Template.Paper.PageHeightMm.Should().Be(148);
        result.Template.Elements.Should().HaveCount(51, "源面板有 51 个 printElements，一个都不能静默丢");
        result.Template.Elements.Count(e => e.TypeKey == ElementTypeKeys.Text).Should().Be(33);
        result.Template.Elements.Count(e => e.TypeKey == ElementTypeKeys.Line).Should().Be(13, "4 条 hline + 9 条 vline");
        result.Template.Elements.Count(e => e.TypeKey == ElementTypeKeys.Ellipse).Should().Be(3);
        result.Template.Elements.Count(e => e.TypeKey == ElementTypeKeys.Rectangle).Should().Be(1);
        result.Template.Elements.Count(e => e.TypeKey == ElementTypeKeys.QrCode).Should().Be(1, "textType=qrcode 的文本转成二维码元素");

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be("hiprint.page-number-unsupported");
        issue.Severity.Should().Be(HiprintImportSeverity.Warning, "页码只是降级，不是错误");
    }

    [Fact]
    public void 发票getJson_字号行距字体按hiprint量得的默认值呈现()
    {
        var result = ImportFixture("invoice-getjson.json");
        var texts = result.Template.Elements.OfType<TextElement>().ToList();

        var date = texts.Single(t => t.Text.Content == "2019年05月09日");
        date.Text.FontSizePt.Should().Be(9, "未写 fontSize 时 hiprint 默认 9pt");
        date.Text.LineSpacing.Should().BeApproximately(9.75 / 9, 0.0001);
        date.Text.VerticalAlign.Should().Be(VerticalAlign.Top, "hiprint 没给对齐类，文本贴框顶");
        date.Text.FontFamily.Should().Be("SimSun");

        var header = texts.Single(t => t.Text.Content == "货物或应税劳务、服务名称");
        header.Text.FontSizePt.Should().Be(13);
        header.Text.LineSpacing.Should().BeApproximately(9.75 / 13, 0.0001,
            "13pt 表头的 computed line-height 实测仍是 13px = 9.75pt");

        var qr = result.Template.Elements.OfType<QrCodeElement>().Single();
        qr.QrCode.CaptionFontSizePt.Should().Be(9, "hiqrcode_displayValue 实测 12px = 9pt");
        qr.QrCode.CaptionLineSpacing.Should().BeApproximately(9.75 / 9, 0.0001);
    }

    [Fact]
    public void LooksLikeHiprintJson_只认panels结构()
    {
        HiprintTemplateImporter.LooksLikeHiprintJson(Fixture("invoice-getjson.json")).Should().BeTrue();
        HiprintTemplateImporter.LooksLikeHiprintJson(Fixture("hiprint-getjson.json")).Should().BeTrue();
        HiprintTemplateImporter.LooksLikeHiprintJson(Fixture("hiprint-template7.js")).Should().BeTrue("整个 JS 模块也能取到 panels");

        var ours = TemplateJson.Serialize(PrintTemplate.CreateDefault(PaperTypePresets.A4));
        HiprintTemplateImporter.LooksLikeHiprintJson(ours).Should().BeFalse("CrossPrint 模板不能被误判成 hiprint 模板");
        HiprintTemplateImporter.LooksLikeHiprintJson("hello").Should().BeFalse();
        HiprintTemplateImporter.LooksLikeHiprintJson("").Should().BeFalse();
    }

    /// <summary>
    /// 实测的「谓词 vs 导入器」对照表。samples 的 JSON 面板用谓词决定跑哪个导入器：
    /// 谓词说"不是 hiprint"而 Import 其实导得动 → 用户看到"无法解析"；
    /// 谓词说"是 hiprint"而 Import 抛异常 → 直接崩。两边必须用同一个结构判据。
    /// </summary>
    public static TheoryData<string, bool> 谓词与导入器对照表 => new()
    {
        // 判据为真：面板数组里至少有一个对象面板（= Import 不会抛"没有 panels 面板"）
        { """{ "panels": [ { "width": 210, "height": 297, "printElements": [] } ] }""", true },
        { """{ "json": { "panels": [ { "printElements": [] } ] } }""", true },
        { """[ { "printElements": [] } ]""", true },
        { """[ 5, { "printElements": [] } ]""", true },
        { """[ { "panels": [] } ]""", true },
        { """[ { "a": 1 } ]""", true },
        { """[{ "options": { "left": 1 }, "printElementType": { "type": "text" } }]""", true },

        // 判据为假：Import 对这些文本一定抛 HiprintImportException
        { """{ "panels": [] }""", false },
        { """{ "panels": [ 7 ] }""", false },
        { """{ "json": [ { "printElements": [] } ] }""", false },
        { "[]", false },
        { "[1,2,3]", false },
        { """{ "panels": { "a": 1 } }""", false },
        { """{ "panels": "not-an-array" }""", false },
        { """{ "printElements": [] }""", false },
        { "hello", false },
        { "   ", false },
    };

    [Theory]
    [MemberData(nameof(谓词与导入器对照表))]
    public void 判定谓词与导入器用同一个结构判据(string text, bool expected)
    {
        HiprintTemplateImporter.LooksLikeHiprintJson(text).Should().Be(expected, $"谓词判错会让宿主走错导入器：{text}");

        Exception? error = null;
        try
        {
            HiprintTemplateImporter.Import(text);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        (error is null).Should().Be(expected, $"谓词与 Import 必须同进同退，实际 {error?.GetType().Name}: {error?.Message} << {text}");
    }

    // ---------------- hiprint-template7.js（整个 JS 模块） ----------------

    [Fact]
    public void 模板7_整个模块只取panels对象并导入单表()
    {
        var raw = Fixture("hiprint-template7.js");
        raw.Should().Contain("import _ from", "夹具含 ES import");
        raw.Should().Contain("//", "夹具含行注释");
        raw.Should().Contain("`", "夹具含反引号函数体，其中的花括号不能当对象边界");

        var result = HiprintTemplateImporter.Import(raw);

        result.Template.Elements.Should().ContainSingle("整个模块只应挑出含 panels 的那个对象");
        var table = result.Template.Elements[0].Should().BeOfType<TableElement>().Subject;
        table.Table.Field.Should().Be("tableData");
    }

    [Fact]
    public void 模板7_序号列无字段且表头标题完整()
    {
        var result = ImportFixture("hiprint-template7.js");
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(6);
        table.Table.Columns[0].Sequence.Should().BeTrue("tableTextType=sequence");
        table.Table.Columns[0].Field.Should().BeEmpty("序号列由行号生成，不绑定字段");
        table.Table.Columns[0].Title.Should().Be("序号");

        table.Table.HeaderRows.Should().HaveCount(6);
        table.Table.HeaderRows.Select(h => h.Title)
            .Should().Equal("序号", "开课时间", "课程类型", "姓名", "性别", "学分");
    }

    [Fact]
    public void 模板7_水印页码与js格式化回调都留痕()
    {
        var result = ImportFixture("hiprint-template7.js");

        AssertIssue(result, "hiprint.watermark-unsupported");
        AssertIssue(result, "hiprint.page-number-unsupported");
        AssertIssue(result, "hiprint.js-formatter", "el-1");
        result.HasErrors.Should().BeFalse("回调无法执行只降级为警告，表格仍要导入");
    }

    // ---------------- hiprint-template4.js ----------------

    [Fact]
    public void 模板4_自定义纸张尺寸与上下留白()
    {
        var result = ImportFixture("hiprint-template4.js");

        result.Template.Elements.Should().HaveCount(13);
        result.Template.Paper.Preset.Should().Be(PaperTypePresets.Custom);
        result.Template.Paper.WidthMm.Should().Be(210);
        result.Template.Paper.HeightMm.Should().Be(296);
        result.Template.Paper.Orientation.Should().Be(PaperOrientation.Portrait);
        result.Template.Paper.IsContinuous.Should().BeFalse();

        result.Template.Margins.Top.Should().BeApproximately(23.2833, 0.01, "paperHeader=66pt");
        result.Template.Margins.Bottom.Should().BeApproximately(9.1916667, 0.01, "296mm - paperFooter 813pt");
    }

    [Fact]
    public void 模板4_带noBorder的表格关闭网格而缺省表格保留默认网格()
    {
        var result = ImportFixture("hiprint-template4.js");
        var tables = result.Template.Elements.OfType<TableElement>().ToList();

        tables.Should().HaveCount(2);

        var noBorder = tables.Single(t => t.Table.Field == "product_info");
        noBorder.Table.ShowBorder.Should().BeFalse("该表格七个 table*Border 全部是 noBorder");
        noBorder.Table.GridColor.Should().BeNull("noBorder 不能留下黑色网格");

        // 夹具里第一个表格没有 tableBorder 配置，按产品默认值导入（与外层全 noBorder 的表格不同）。
        var defaultTable = tables.Single(t => t.Table.Field == "work_info");
        defaultTable.Table.ShowBorder.Should().BeTrue("没有边框配置时保留默认网格");
        defaultTable.Table.GridColor.Should().Be(SKColors.Black);
    }

    [Fact]
    public void 模板4_渲染回调页码与水印降级并留痕()
    {
        var result = ImportFixture("hiprint-template4.js");

        AssertIssue(result, "hiprint.js-formatter", "el-7");
        AssertIssue(result, "hiprint.page-number-unsupported");
        AssertIssue(result, "hiprint.watermark-unsupported");
        result.HasErrors.Should().BeFalse();
    }

    // ---------------- 多面板 ----------------

    [Fact]
    public void 多面板_默认导入第一个并提示只导入了其中一个()
    {
        var result = HiprintTemplateImporter.Import(TwoPanelTemplate);

        result.Template.Elements.Should().ContainSingle();
        ((TextElement)result.Template.Elements[0]).Text.Content.Should().Be("第一联");

        result.Panels.Should().HaveCount(2);
        result.Panels[0].Imported.Should().BeTrue();
        result.Panels[1].Imported.Should().BeFalse();

        var issue = result.Issues.Single(i => i.Code == "hiprint.multi-panel");
        issue.Severity.Should().Be(HiprintImportSeverity.Info, "多面板是提示而非降级");
    }

    [Fact]
    public void 多面板_按下标只导入第二个面板()
    {
        var result = HiprintTemplateImporter.Import(TwoPanelTemplate, new HiprintImportOptions { PanelIndex = 1 });

        result.Template.Elements.Should().ContainSingle();
        ((TextElement)result.Template.Elements[0]).Text.Content.Should().Be("第二联");
        result.Template.Paper.WidthMm.Should().Be(100);
        result.Template.Paper.HeightMm.Should().Be(150);
        result.Panels[1].Imported.Should().BeTrue();
        result.Panels[0].Imported.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == "hiprint.multi-panel");
    }

    [Fact]
    public void 多面板_越界下标回落到第一个并留痕()
    {
        var result = HiprintTemplateImporter.Import(TwoPanelTemplate, new HiprintImportOptions { PanelIndex = 9 });

        result.Template.Elements.Should().ContainSingle();
        ((TextElement)result.Template.Elements[0]).Text.Content.Should().Be("第一联");
        result.Panels[0].Imported.Should().BeTrue();

        var issue = result.Issues.Single(i => i.Code == "hiprint.panel-index-clamped");
        issue.Severity.Should().Be(HiprintImportSeverity.Info);
    }

    // ---------------- 元素级容错 ----------------

    [Fact]
    public void 未知元素类型被跳过但其余元素仍在()
    {
        var result = ImportElements(
            TextElementJson("""{ "left": 10, "top": 10, "title": "保留" }"""),
            """{ "options": { "left": 1, "top": 1, "title": "插件" }, "printElementType": { "type": "plugin-foo" } }""");

        result.Template.Elements.Should().ContainSingle("未知类型不能拖垮整张模板");
        ((TextElement)result.Template.Elements[0]).Text.Content.Should().Be("保留");
        AssertIssue(result, "hiprint.unknown-element-type", "el-2");
    }

    [Fact]
    public void 元素缺少options时记为畸形并跳过()
    {
        var result = ImportElements(
            """{ "printElementType": { "type": "text" } }""",
            TextElementJson("""{ "left": 10, "top": 10, "title": "正常" }"""));

        result.Template.Elements.Should().ContainSingle();
        ((TextElement)result.Template.Elements[0]).Text.Content.Should().Be("正常");
        AssertIssue(result, "hiprint.element-malformed", "el-1");
    }

    // ---------------- 退化尺寸 ----------------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(-40, "-40")]
    public void 元素高为非正数时钳成零高并记录退化尺寸警告(int hiprintHeight, string expectedRaw)
    {
        var result = ImportSingleElement(
            $$"""{ "left": 10, "top": 10, "width": 100, "height": {{hiprintHeight}} }""");

        var element = result.Template.Elements.Should().ContainSingle("退化尺寸仍然导入，导入器不替用户编一个尺寸").Subject;
        element.HeightMm.Should().Be(0, "实测：0pt 与 -40pt 都被 Math.Max(0, …) 钳成 0mm");
        element.WidthMm.Should().BeApproximately(35.2778, 0.0001, "100pt = 35.28mm，另一轴照常换算");

        var issue = result.Issues.Should()
            .ContainSingle(i => i.Code == "hiprint.element-degenerate-size", "零高元素在画布上抓不住，必须留痕")
            .Subject;
        issue.Severity.Should().Be(HiprintImportSeverity.Warning);
        issue.ElementId.Should().Be(element.Id, "警告要指得回是哪个元素");
        issue.SourcePath.Should().Be("panels[0].printElements[0]");
        issue.Message.Should().Contain("height").And.Contain(expectedRaw, "要报出实测的原始数字，否则用户无从知道是哪一维");
    }

    [Fact]
    public void 元素宽为0时记录退化尺寸警告并点名width()
    {
        var result = ImportSingleElement("""{ "left": 10, "top": 10, "width": 0, "height": 20 }""");

        var element = result.Template.Elements.Should().ContainSingle().Subject;
        element.WidthMm.Should().Be(0);
        element.HeightMm.Should().BeApproximately(7.0556, 0.0001, "20pt = 7.06mm 正常换算");

        var issue = result.Issues.Should()
            .ContainSingle(i => i.Code == "hiprint.element-degenerate-size").Subject;
        issue.Message.Should().Contain("width").And.NotContain("height", "只报真正退化那一维");
    }

    [Fact]
    public void 元素尺寸正常时不记录退化尺寸警告()
    {
        var result = ImportElements(
            TextElementJson("""{ "left": 10, "top": 10, "width": 100, "height": 20, "title": "正常" }"""),
            // 未写宽高时按类型默认值（text = 120×9.75pt）导入，同样是正尺寸，不该报警。
            TextElementJson("""{ "left": 10, "top": 40, "title": "缺省" }"""));

        result.Template.Elements.Should().HaveCount(2);
        result.Issues.Should().BeEmpty("正常尺寸与走默认值都不能误报退化尺寸");
    }

    // ---------------- 连续纸 ----------------

    [Fact]
    public void 连续纸_面板规则none时导入80mm小票纸()
    {
        const string json = """
        { "panels": [ { "width": 80, "height": 200, "panelPageRule": "none", "printElements": [] } ] }
        """;

        var result = HiprintTemplateImporter.Import(json);

        result.Template.Paper.Preset.Should().Be(PaperTypePresets.Receipt80);
        result.Template.Paper.WidthMm.Should().Be(80);
        result.Template.Paper.HeightMm.Should().Be(0, "连续纸高度由内容决定");
        result.Template.Paper.IsContinuous.Should().BeTrue();
    }

    // ---------------- 文本 ----------------

    [Fact]
    public void 文本_字段没有标题时生成占位符()
    {
        var result = ImportSingleElement("""{ "left": 1, "top": 1, "field": "name" }""");

        var text = result.Template.Elements.Single().Should().BeOfType<TextElement>().Subject;
        text.Text.Content.Should().Be("{{name}}");
    }

    [Fact]
    public void 文本_标题前缀受hideTitle控制()
    {
        var withTitle = ImportSingleElement("""{ "left": 1, "top": 1, "field": "name", "title": "姓名" }""");
        ((TextElement)withTitle.Template.Elements.Single()).Text.Content
            .Should().Be("姓名：{{name}}", "hideTitle 缺省为 false，要拼出“标题：”");

        var hiddenTitle = ImportSingleElement(
            """{ "left": 1, "top": 1, "field": "name", "title": "姓名", "hideTitle": true }""");
        ((TextElement)hiddenTitle.Template.Elements.Single()).Text.Content
            .Should().Be("{{name}}", "hideTitle=true 时只保留占位符");
    }

    [Fact]
    public void 文本_ellipsis关闭换行并启用省略号()
    {
        var result = ImportSingleElement(
            """{ "left": 1, "top": 1, "field": "name", "textContentWrap": "ellipsis" }""");

        var text = (TextElement)result.Template.Elements.Single();
        text.Text.Wrap.Should().BeFalse();
        text.Text.Ellipsis.Should().BeTrue();
    }

    [Fact]
    public void 文本_nowrap关闭换行但不加省略号()
    {
        var result = ImportSingleElement(
            """{ "left": 1, "top": 1, "field": "name", "textContentWrap": "nowrap" }""");

        var text = (TextElement)result.Template.Elements.Single();
        text.Text.Wrap.Should().BeFalse();
        text.Text.Ellipsis.Should().BeFalse();
    }

    [Theory]
    [InlineData("top", VerticalAlign.Top)]
    [InlineData("bottom", VerticalAlign.Bottom)]
    [InlineData("middle", VerticalAlign.Middle)]
    public void 文本_垂直对齐逐项映射(string hiprintAlign, VerticalAlign expected)
    {
        var result = ImportSingleElement($$"""
        { "left": 1, "top": 1, "field": "name", "textContentVerticalAlign": "{{hiprintAlign}}" }
        """);

        var text = (TextElement)result.Template.Elements.Single();
        text.Text.VerticalAlign.Should().Be(expected);
    }

    // ---------------- 枚举型取值的解析口径（实测钉死） ----------------

    [Fact]
    public void 对齐_数字串与大小写混排的取值不是静默丢弃()
    {
        // 实测：textAlign 收到数字/数字串时按左对齐导入，并且记 hiprint.align-unknown——
        // "数字串被静默丢弃"的审计说法在这几个点上不成立，这里把实测结果钉住。
        var numeric = ImportSingleElement("""{ "left": 1, "top": 1, "width": 60, "height": 20, "textAlign": "3" }""");
        ((TextElement)numeric.Template.Elements.Single()).Text.HorizontalAlign.Should().Be(HorizontalAlign.Left);
        AssertIssue(numeric, "hiprint.align-unknown");
        numeric.Issues.Single(i => i.Code == "hiprint.align-unknown").Message
            .Should().Be("未知 textAlign '3'，按左对齐处理。", "既有告警文本被其它测试钉着，不能改");

        // 大小写与尾随空格是 hiprint 设计器之外手工改过的常见写法：必须照常识别，且不落告警。
        var messy = ImportSingleElement("""{ "left": 1, "top": 1, "width": 60, "height": 20, "textAlign": "LEFT " }""");
        ((TextElement)messy.Template.Elements.Single()).Text.HorizontalAlign.Should().Be(HorizontalAlign.Left);
        messy.Issues.Should().BeEmpty("trim + 忽略大小写就能接住的取值不该变成告警");
    }

    [Fact]
    public void 垂直对齐_未知取值回落顶对齐并留痕()
    {
        // 实测：改前 "center"/"down"/"3" 都静默落成 Top，一条问题都不记（对齐 textAlign 的口径应该留痕）。
        var result = ImportSingleElement(
            """{ "left": 1, "top": 1, "width": 60, "height": 20, "textContentVerticalAlign": "center" }""");

        var text = (TextElement)result.Template.Elements.Single();
        text.Text.VerticalAlign.Should().Be(VerticalAlign.Top, "hiprint 未识别的对齐一律按默认顶对齐");

        var issue = result.Issues.Should()
            .ContainSingle(i => i.Code == "hiprint.valign-unknown", "未知垂直对齐要留痕，不能静默改成顶对齐").Subject;
        issue.Severity.Should().Be(HiprintImportSeverity.Warning);
        issue.Message.Should().Contain("center");
        issue.ElementId.Should().Be(text.Id);
    }

    [Fact]
    public void 垂直对齐_hiprint自己的三个取值不留痕()
    {
        // 夹具里出现的 textContentVerticalAlign 只有 middle，这三个取值是 hiprint 设计器真正会写的。
        var result = ImportElements(
            TextElementJson("""{ "left": 1, "top": 1, "width": 60, "height": 20, "textContentVerticalAlign": "top" }"""),
            TextElementJson("""{ "left": 1, "top": 30, "width": 60, "height": 20, "textContentVerticalAlign": "middle" }"""),
            TextElementJson("""{ "left": 1, "top": 60, "width": 60, "height": 20, "textContentVerticalAlign": "bottom" }"""),
            TextElementJson("""{ "left": 1, "top": 90, "width": 60, "height": 20, "textContentVerticalAlign": " MIDDLE " }"""));

        result.Issues.Should().BeEmpty("hiprint 自己的取值（含大小写/空格写法）都是等价导入");
    }

    [Fact]
    public void 二维码_数字串容错等级照常识别超范围取值留痕()
    {
        // 实测：Dbl 认数字串，所以 "1"/"2 " 这类写法本来就等价于 1/2，不是"静默丢弃"。
        foreach (var raw in new[] { "1", "\"1\"", "\"2 \"" })
        {
            var result = ImportSingleElement(
                $$"""{ "left": 1, "top": 1, "width": 60, "height": 60, "qrCodeLevel": {{raw}} }""", "qrcode");
            result.Template.Elements.Single().Should().BeOfType<QrCodeElement>().Subject
                .QrCode.ErrorCorrection.Should().Be(raw.Contains('2') ? QrErrorCorrection.H : QrErrorCorrection.L);
            result.Issues.Should().BeEmpty("数字串写法等价于数字等级，不是被丢弃的取值");
        }

        // 0 是 hiprint 的默认等级（实测夹具 hiprint-template4 就写 0），落回 M 是等价处理，不留痕。
        var zero = ImportSingleElement("""{ "left": 1, "top": 1, "width": 60, "height": 60, "qrCodeLevel": 0 }""", "qrcode");
        zero.Template.Elements.Single().Should().BeOfType<QrCodeElement>().Subject
            .QrCode.ErrorCorrection.Should().Be(QrErrorCorrection.M);
        zero.Issues.Should().BeEmpty();

        // 真正认不出来的写法（字母等级、超出 0..3）以前静默按 M，现在必须留痕。
        var bogus = ImportSingleElement("""{ "left": 1, "top": 1, "width": 60, "height": 60, "qrCodeLevel": "H" }""", "qrcode");
        var issue = bogus.Issues.Should()
            .ContainSingle(i => i.Code == "hiprint.qrcode-level-unknown", "qrCodeLevel 认不出来时按 M 导入要告知用户").Subject;
        issue.Severity.Should().Be(HiprintImportSeverity.Warning);
        issue.Message.Should().Contain("H");

        var outOfRange = ImportSingleElement("""{ "left": 1, "top": 1, "width": 60, "height": 60, "qrCodeLevel": 4 }""", "qrcode");
        AssertIssue(outOfRange, "hiprint.qrcode-level-unknown");
    }

    [Fact]
    public void 表格表头对齐_未知取值回落hiprint默认居中()
    {
        // 实测：halign 写错（如 "3"）时回落成 hiprint 的表头默认居中，与 align 的回落口径不同，
        // 这里按实测钉住，不改代码——hiprint 自己只会写 left/center/right，都能接住。
        var result = ImportSingleElement(
            """{ "left": 5, "top": 5, "width": 300, "height": 40, "columns": [[{ "title": "A", "field": "a", "halign": "3" }]] }""",
            "table");

        var table = (TableElement)result.Template.Elements.Single();
        table.Table.HeaderRows.Single().Align.Should().Be(HorizontalAlign.Center);
        table.Table.Columns.Single().Align.Should().Be(HorizontalAlign.Left, "没有 align 时列正文按表级 textAlign 回落左对齐");
    }

    // ---------------- 条码 / 二维码 ----------------

    [Theory]
    [InlineData("code128", BarcodeSymbology.Code128)]
    [InlineData("ean13", BarcodeSymbology.Ean13)]
    [InlineData("ean8", BarcodeSymbology.Ean8)]
    [InlineData("upca", BarcodeSymbology.UpcA)]
    [InlineData("upce", BarcodeSymbology.UpcE)]
    [InlineData("itf", BarcodeSymbology.Itf)]
    [InlineData("codabar", BarcodeSymbology.Codabar)]
    [InlineData("code39", BarcodeSymbology.Code39)]
    [InlineData("code93", BarcodeSymbology.Code93)]
    [InlineData("msi", BarcodeSymbology.Msi)]
    public void 条码_码制逐项映射(string hiprintType, BarcodeSymbology expected)
    {
        var result = ImportSingleElement($$"""
        { "left": 5, "top": 5, "height": 20, "width": 60,
          "textType": "barcode", "barcodeType": "{{hiprintType}}", "title": "123456789" }
        """);

        var barcode = result.Template.Elements.Single().Should().BeOfType<BarcodeElement>().Subject;
        barcode.Barcode.Symbology.Should().Be(expected);
        barcode.Barcode.Content.Should().Be("123456789", "没有 field 时用 title 作为静态内容");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.barcode-type-unknown");
    }

    [Fact]
    public void 条码_字段绑定时testData进入模板设计数据()
    {
        var result = ImportSingleElement("""
        { "left": 5, "top": 5, "height": 20, "width": 60,
          "textType": "barcode", "field": "barcode", "testData": "8801234567890" }
        """);

        var barcode = result.Template.Elements.Single().Should().BeOfType<BarcodeElement>().Subject;
        barcode.Barcode.Content.Should().Be("{{barcode}}", "真实数据要优先，样例值只能放在设计数据里兜底");
        result.Template.DesignData.Should().ContainKey("barcode").WhoseValue.Should().Be("8801234567890",
            "丢掉 testData，预览里的条码就会绑定成空串");
    }

    [Fact]
    public void 模板1_字段的testData保留为设计数据()
    {
        var result = ImportFixture("hiprint-template1.js");

        result.Template.DesignData.Should().ContainKey("name").WhoseValue.Should().Be("古力娜扎");
        var fieldText = result.Template.Elements
            .OfType<TextElement>()
            .Single(t => t.Text.Content == "姓名：{{name}}");
        fieldText.Text.Content.Should().Contain("{{name}}", "打印数据优先，设计数据不替换占位符本身");
    }

    [Fact]
    public void 设计数据展开成嵌套字典与列表()
    {
        var template = PrintTemplate.CreateDefault(PaperTypePresets.A4);
        template.DesignData["order.customer"] = "张三";
        template.DesignData["items[0].sku"] = "SKU-A";
        template.DesignData["items[1].sku"] = "SKU-B";
        template.DesignData["items[1].qty"] = "2";

        var data = template.BuildDesignBindingData().Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        data["order"].Should().BeAssignableTo<IDictionary<string, object?>>().Subject["customer"].Should().Be("张三");

        var items = data["items"].Should().BeAssignableTo<IList<object?>>().Subject;
        items.Should().HaveCount(2, "按下标写入时列表要补到最大下标");
        ((IDictionary<string, object?>)items[0]!)["sku"].Should().Be("SKU-A");
        ((IDictionary<string, object?>)items[1]!)["qty"].Should().Be("2");

        PrintTemplate.CreateDefault().BuildDesignBindingData().Should().BeNull("没有设计数据时不能凭空造一个数据源");
    }

    [Fact]
    public void 设计数据_超大下标不补齐列表()
    {
        var template = PrintTemplate.CreateDefault();
        template.DesignData["items[2000000]"] = "超大下标";
        template.DesignData["items[1].sku"] = "SKU-B";

        var data = template.BuildDesignBindingData().Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        var items = data["items"].Should().BeAssignableTo<IList<object?>>().Subject;

        items.Should().HaveCount(2, "超过补齐上限的下标直接跳过，否则设计器每次重绘都要分配百万级列表");
        items.Should().NotContain(x => Equals(x, "超大下标"), "被跳过的路径按未命中处理，渲染走缺失字段回落");
        ((IDictionary<string, object?>)items[1]!)["sku"].Should().Be("SKU-B");
    }

    [Fact]
    public void 设计数据_上限内的嵌套下标照常补齐()
    {
        var template = PrintTemplate.CreateDefault();
        template.DesignData["order.items[3].sku"] = "SKU-D";

        var data = template.BuildDesignBindingData().Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        var order = data["order"].Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        var items = order["items"].Should().BeAssignableTo<IList<object?>>().Subject;

        items.Should().HaveCount(4, "上限内的合法下标仍要补到该位置");
        ((IDictionary<string, object?>)items[3]!)["sku"].Should().Be("SKU-D");
    }

    [Fact]
    public void 条码_未知码制回落Code128并告警()
    {
        var result = ImportSingleElement("""
        { "left": 5, "top": 5, "height": 20, "width": 60,
          "textType": "barcode", "barcodeType": "pdf417", "title": "123456789" }
        """);

        var barcode = (BarcodeElement)result.Template.Elements.Single();
        barcode.Barcode.Symbology.Should().Be(BarcodeSymbology.Code128, "未知码制要有确定回退值");
        AssertIssue(result, "hiprint.barcode-type-unknown", "el-1");
    }

    [Theory]
    [InlineData(1, QrErrorCorrection.L)]
    [InlineData(2, QrErrorCorrection.H)]
    [InlineData(3, QrErrorCorrection.Q)]
    [InlineData(null, QrErrorCorrection.M)]
    public void 二维码_纠错等级逐项映射(int? hiprintLevel, QrErrorCorrection expected)
    {
        var levelJson = hiprintLevel is null ? string.Empty : $", \"qrCodeLevel\": {hiprintLevel}";
        var result = ImportSingleElement(
            "{ \"left\": 5, \"top\": 5, \"height\": 60, \"width\": 60, \"textType\": \"qrcode\", \"title\": \"abc\""
            + levelJson + " }");

        var qr = result.Template.Elements.Single().Should().BeOfType<QrCodeElement>().Subject;
        qr.QrCode.ErrorCorrection.Should().Be(expected);
    }

    // ---------------- 图片 ----------------

    [Fact]
    public void 图片_dataURL内嵌为Base64()
    {
        var result = ImportSingleElement(
            """{ "left": 1, "top": 1, "src": "data:image/png;base64,AAAA" }""",
            type: "image");

        var image = result.Template.Elements.Single().Should().BeOfType<ImageElement>().Subject;
        image.Image.SourceType.Should().Be(ImageSourceType.EmbeddedBase64);
        image.Image.Data.Should().Be("AAAA");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.image-url");
    }

    [Fact]
    public void 图片_外链地址转为ResolverKey并提示宿主解析()
    {
        const string src = "https://example.com/logo.png";
        var result = ImportSingleElement(
            $$"""{ "left": 1, "top": 1, "src": "{{src}}" }""",
            type: "image");

        var image = (ImageElement)result.Template.Elements.Single();
        image.Image.SourceType.Should().Be(ImageSourceType.ResolverKey);
        image.Image.ResolverKey.Should().Be(src);
        image.Image.Data.Should().BeNull();

        var issue = result.Issues.Single(i => i.Code == "hiprint.image-url");
        issue.Severity.Should().Be(HiprintImportSeverity.Info, "外链需要宿主解析，但不是模板错误");
        issue.ElementId.Should().Be("el-1");
    }

    // ---------------- 多行表头 ----------------

    [Fact]
    public void 表头_两行表头按colspan与rowspan展开()
    {
        var tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 400, "height": 30,
            "columns": [
              [
                { "title": "基本信息", "colspan": 2, "rowspan": 1, "checked": true },
                { "title": "金额", "colspan": 2, "rowspan": 1, "checked": true }
              ],
              [
                { "title": "姓名", "field": "name", "checked": true },
                { "title": "性别", "field": "sex", "checked": true },
                { "title": "数量", "field": "qty", "checked": true },
                { "title": "金额", "field": "amount", "checked": true }
              ]
            ]
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(4, "两行各 2 个 colspan=2 的单元格");
        table.Table.Columns.Select(c => c.Title).Should().Equal("姓名", "性别", "数量", "金额");

        table.Table.HeaderRows.Should().HaveCount(6, "上层 2 个 + 下层 4 个表头单元格");
        var group = table.Table.HeaderRows.Single(h => h.Row == 0 && h.Column == 0);
        group.Title.Should().Be("基本信息");
        group.ColSpan.Should().Be(2);
        group.RowSpan.Should().Be(1);

        var amountGroup = table.Table.HeaderRows.Single(h => h.Row == 0 && h.Column == 2);
        amountGroup.Title.Should().Be("金额");
        amountGroup.ColSpan.Should().Be(2);
    }

    [Fact]
    public void 表头_checked为false的单元格不产生列()
    {
        var tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": [[
              { "title": "甲", "field": "a", "checked": true },
              { "title": "隐藏", "field": "hidden", "checked": false },
              { "title": "乙", "field": "b", "checked": true }
            ]]
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2);
        table.Table.Columns.Select(c => c.Title).Should().Equal("甲", "乙");
        table.Table.Columns.Select(c => c.Field).Should().NotContain("hidden");
        result.Issues.Should().Contain(i => i.Code == "hiprint.table-hidden-column");
    }

    // ---------------- 表格兼容形态 ----------------

    [Fact]
    public void 旧版tableCustom按表格导入并留提示()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": [[
              { "title": "甲", "field": "a", "checked": true },
              { "title": "乙", "field": "b", "checked": true }
            ]]
          },
          "printElementType": { "type": "tableCustom" }
        }
        """;

        var result = ImportElements(tableJson);

        // 旧类型若落进未知元素分支，整张表会静默消失——用户只看到一句“已同步”却少了一块内容。
        var table = result.Template.Elements.Should().ContainSingle().Subject.Should().BeOfType<TableElement>().Subject;
        table.Table.Columns.Select(c => c.Field).Should().Equal("a", "b");
        table.Table.HeaderRows.Should().HaveCount(2);

        var issue = result.Issues.Single(i => i.Code == "hiprint.table-custom-legacy");
        issue.Severity.Should().Be(HiprintImportSeverity.Info, "旧类型仍能等价导入，不是降级");
        issue.ElementId.Should().Be("el-1");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.unknown-element-type");
    }

    [Fact]
    public void 表格缺少列定义时按占位列导入而不是整张消失()
    {
        const string tableJson = """
        {
          "options": { "left": 5, "top": 5, "width": 300, "height": 20 },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);

        var table = result.Template.Elements.Should().ContainSingle().Subject.Should().BeOfType<TableElement>().Subject;
        table.Table.Columns.Should().ContainSingle("没有列定义时留一列占位，让用户能在属性面板补齐");
        table.Table.Columns[0].Title.Should().Be("列1");
        table.WidthMm.Should().BeApproximately(UnitConverter.PtToMm(300), 0.01, "表格框的位置尺寸必须原样保留");
        AssertIssue(result, "hiprint.table-no-columns", "el-1");
    }

    [Fact]
    public void 表格columns写成对象映射时读出全部列()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": {
              "0": { "title": "甲", "field": "a", "checked": true },
              "1": { "title": "乙", "field": "b", "checked": true }
            }
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Select(c => c.Field).Should().Equal("a", "b");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.table-no-columns");
    }

    [Fact]
    public void 表格columns是单个扁平列对象时按一列导入()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": { "title": "甲", "field": "a", "width": 120, "checked": true }
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().ContainSingle();
        table.Table.Columns[0].Title.Should().Be("甲");
        table.Table.Columns[0].Field.Should().Be("a");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.table-no-columns");
    }

    [Fact]
    public void 表头_扁平columns数组按一行多列导入()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": [
              { "title": "品名", "field": "name", "width": 120, "checked": true },
              { "title": "数量", "field": "qty", "width": 50, "checked": true },
              { "title": "单价", "field": "price", "width": 60, "checked": true },
              { "title": "金额", "field": "amount", "width": 70, "checked": true }
            ]
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(4, "扁平 columns 是一维列清单，不是一个对象一行表头");
        table.Table.Columns.Select(c => c.Title).Should().Equal("品名", "数量", "单价", "金额");
        table.Table.Columns.Select(c => c.Field).Should().Equal("name", "qty", "price", "amount");
        table.Table.Columns.Select(c => c.WidthMm).Should()
            .Equal(UnitConverter.PtToMm(120), UnitConverter.PtToMm(50), UnitConverter.PtToMm(60), UnitConverter.PtToMm(70));

        table.Table.HeaderRows.Should().HaveCount(4, "一行表头 4 个单元格");
        table.Table.HeaderRows.Select(h => h.Row).Should().OnlyContain(r => r == 0, "扁平列清单只有一行表头");
        table.Table.HeaderRows.Select(h => h.Column).Should().Equal(0, 1, 2, 3);

        result.Issues.Should().BeEmpty("扁平列清单是受支持的写法，不该留任何降级提示");
    }

    [Fact]
    public void 表头_扁平与二维两种columns写法导入出同一张表()
    {
        const string columns = """
              [
                { "title": "品名", "field": "name", "width": 120, "checked": true },
                { "title": "数量", "field": "qty", "width": 50, "checked": true }
              ]
        """;

        var flat = ImportElements(TableWithOptionsJson("\"columns\": " + columns));
        var nested = ImportElements(TableWithOptionsJson("\"columns\": [" + columns + "]"));

        var flatTable = flat.Template.Elements.OfType<TableElement>().Single();
        var nestedTable = nested.Template.Elements.OfType<TableElement>().Single();

        flatTable.Table.Columns.Select(c => c.Field).Should().Equal("name", "qty");
        TemplateJson.Serialize(flat.Template).Should().Be(TemplateJson.Serialize(nested.Template),
            "两种写法都只是外层少一层行数组，模型必须一致");
    }

    [Fact]
    public void 表头_分组列对象展开子列且列数不丢()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": [
              { "title": "甲", "field": "a", "width": 100, "checked": true },
              { "title": "基本信息", "columns": [
                { "title": "乙", "field": "b", "width": 100, "checked": true },
                { "title": "丙", "field": "c", "width": 100, "checked": true }
              ] }
            ]
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(3, "分组列要展开成子列，否则删列时只剩一列可用");
        table.Table.Columns.Select(c => c.Title).Should().Equal("甲", "乙", "丙");
        table.Table.Columns.Select(c => c.Field).Should().Equal("a", "b", "c");

        var issue = result.Issues.Single(i => i.Code == "hiprint.table-group-header");
        issue.Severity.Should().Be(HiprintImportSeverity.Info, "单行表头无法表达分组标题，属可接受的降级");
        result.Issues.Should().NotContain(i => i.Severity == HiprintImportSeverity.Warning);
    }

    [Fact]
    public void 表头_columns是行对象数组时仍按一行多列导入()
    {
        const string tableJson = """
        {
          "options": {
            "left": 5, "top": 5, "width": 300, "height": 20,
            "columns": [
              { "0": { "title": "甲", "field": "a", "width": 100, "checked": true },
                "1": { "title": "乙", "field": "b", "width": 100, "checked": true } }
            ]
          },
          "printElementType": { "type": "table" }
        }
        """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns.Should().HaveCount(2, "元素里嵌着列对象就说明它是表头行，不是列清单");
        table.Table.Columns.Select(c => c.Title).Should().Equal("甲", "乙");
    }

    // ---------------- JS 回调 / 异常 ----------------

    [Fact]
    public void 文本_formatter与styler保留入库不告警()
    {
        var result = ImportSingleElement(
            """{ "left": 1, "top": 1, "field": "amount", "formatter": "(value) => value + '元'", "styler": "function(){return {color:'red'};}" }""");

        var text = result.Template.Elements.Single().Should().BeOfType<TextElement>().Subject;
        text.Text.Content.Should().Be("{{amount}}", "字段绑定要保留");
        text.Formatter.Should().Be("(value) => value + '元'");
        text.Styler.Should().Be("function(){return {color:'red'};}");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.js-formatter", "脚本由引擎执行，不再按无法执行告警");
    }

    [Fact]
    public void 表格列formatter与styler保留入库()
    {
        const string tableJson = """
          {
            "options": {
              "left": 1, "top": 1, "width": 200, "height": 20, "field": "items",
              "columns": [
                [
                  { "title": "货号", "field": "sku", "width": 100, "checked": true,
                    "formatter": "function(value,row,index){ return index + ':' + value; }",
                    "styler": "function(value){ return {color:'red'}; }" },
                  { "title": "数量", "field": "qty", "width": 100, "checked": true }
                ]
              ]
            },
            "printElementType": { "type": "table" }
          }
          """;

        var result = ImportElements(tableJson);
        var table = result.Template.Elements.OfType<TableElement>().Single();

        table.Table.Columns[0].Formatter.Should().NotBeNull("列脚本随列定义入库");
        table.Table.Columns[0].Styler.Should().NotBeNull();
        table.Table.Columns[1].Formatter.Should().BeNull("没有脚本的列保持 null");
        result.Issues.Should().NotContain(i => i.Code == "hiprint.js-formatter");
    }

    [Fact]
    public void 非模板文本抛出HiprintImportException()
    {
        var act = () => HiprintTemplateImporter.Import("hello");

        act.Should().Throw<HiprintImportException>("纯文本里找不到 panels 模板对象");
    }

    [Fact]
    public void 空文本引用抛出ArgumentNullException()
    {
        var act = () => HiprintTemplateImporter.Import(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---------------- 序列化往返 ----------------

    [Fact]
    public void 往返_导入模板经JSON序列化后关键字段不变()
    {
        var imported = ImportFixture("hiprint-template1.js");
        var json = TemplateJson.Serialize(imported.Template);

        var back = TemplateJson.Deserialize(json);

        back.Elements.Should().HaveCount(33);
        var first = back.Elements[0].Should().BeOfType<TextElement>().Subject;
        first.Id.Should().Be("el-1");
        first.Text.Content.Should().Be("HiPrint自定义模块打印插件");
        first.Text.FontSizePt.Should().Be(19);

        var table = back.Elements.OfType<TableElement>().Single();
        table.Table.Field.Should().Be("table");
        table.Table.Columns.Should().HaveCount(5);
        table.Table.Columns[1].Title.Should().Be("姓名");
        table.Table.Columns[1].Summary.Should().Be(TableSummary.Count);
        table.Table.Columns[2].SummaryLabel.Should().Be("合计：");

        var qr = back.Elements.OfType<QrCodeElement>().Single();
        qr.QrCode.Content.Should().Be("123456789");
        qr.QrCode.ErrorCorrection.Should().Be(QrErrorCorrection.M);

        back.Paper.Preset.Should().Be(PaperTypePresets.A4);
        back.Margins.Top.Should().BeApproximately(17.4625, 0.01);
    }

    [Fact]
    public void 同一模板的JS模块与getJson片段导入出完全一致的模型()
    {
        var fromModule = ImportFixture("hiprint-template1.js");
        var fromGetJson = ImportFixture("hiprint-getjson.json");

        // 两条输入路径（整份模块源码 / getJson 片段）只是归一化方式不同，导入结果必须逐字节一致。
        fromModule.Issues.Select(i => i.Code)
            .Should().Equal(fromGetJson.Issues.Select(i => i.Code), "问题清单不能因输入形态而漂移");
        TemplateJson.Serialize(fromModule.Template)
            .Should().Be(TemplateJson.Serialize(fromGetJson.Template), "两种导出形态必须得到同一份模板");
    }
}
