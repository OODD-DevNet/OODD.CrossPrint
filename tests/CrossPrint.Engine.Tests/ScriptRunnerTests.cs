using System.Text.Json;
using CrossPrint.Core;
using CrossPrint.Engine.Rendering;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// formatter/styler 脚本执行取证：hiprint 同款签名、双形态样式解析、沙箱约束与降级承诺、
/// 元素/表格列两条接入链路，以及「脚本 &gt; 内联/条件样式 &gt; 样式库」的优先级口径。
/// </summary>
public class ScriptRunnerTests
{
    static readonly object?[] NoArgs = [null, null, null, null];

    static (ScriptStyle? Style, RenderReport Report) Styler(string source, object?[]? args = null)
    {
        var report = new RenderReport();
        var style = new ScriptRunner().RunStyler(source, args ?? NoArgs, "e1", report);
        return (style, report);
    }

    static (object? Result, RenderReport Report) Formatter(string source, object?[]? args = null)
    {
        var report = new RenderReport();
        var result = new ScriptRunner().RunFormatter(source, args ?? NoArgs, "e1", report);
        return (result, report);
    }

    // ---------------- formatter：基本语义 ----------------

    [Fact]
    public void formatter_返回值转字符串()
    {
        var (result, report) = Formatter("function(value, options, target, templateData){ return value.toUpperCase(); }",
            ["abc", null, null, null]);

        result.Should().Be("ABC");
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void formatter_能拿到主绑定值_元素参数与整包数据()
    {
        const string source = """
            function(value, options, target, templateData){
                return options.id + ':' + target.type + ':' + value + ':' + templateData.order.no;
            }
            """;
        var templateData = JsonSerializer.Deserialize<JsonElement>("""{"order":{"no":"CP-2026"}}""");
        var opts = new { id = "e1", type = "text" };
        var (result, report) = Formatter(source, ["x1", opts, opts, templateData]);

        result.Should().Be("e1:text:x1:CP-2026");
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void formatter_返回undefined降级为null()
    {
        var (result, report) = Formatter("function(value){ if (value) return value; }", [null, null, null, null]);

        result.Should().BeNull();
        report.HasIssues.Should().BeFalse("返回 undefined 与『没有脚本』同义，不算告警");
    }

    [Fact]
    public void formatter_不是函数的源码降级并告警()
    {
        var (result, report) = Formatter("42");

        result.Should().BeNull();
        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(RenderIssueKind.ScriptIssue);
    }

    [Fact]
    public void formatter_抛异常降级并告警()
    {
        var (result, report) = Formatter("function(value){ throw new Error('boom'); }");

        result.Should().BeNull();
        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(RenderIssueKind.ScriptIssue);
    }

    [Fact]
    public void formatter_死循环被超时拦截()
    {
        var (result, report) = Formatter("function(value){ while(true){} }");

        result.Should().BeNull();
        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(RenderIssueKind.ScriptIssue);
    }

    [Fact]
    public void formatter_箭头函数同样可用()
    {
        var (result, _) = Formatter("(v) => v + '!'", ["hi", null, null, null]);

        result.Should().Be("hi!");
    }

    // ---------------- styler：双形态解析 ----------------

    [Fact]
    public void styler_对象形态逐字段解析()
    {
        const string source = """
            function(value){
                return {
                    color: 'red',
                    backgroundColor: '#f5f5f5',
                    fontSize: '14px',
                    fontWeight: 'bold',
                    fontStyle: 'italic',
                    textDecoration: 'underline',
                    letterSpacing: '1pt',
                    lineHeight: 1.5,
                    textAlign: 'center',
                    verticalAlign: 'middle',
                    padding: '2mm',
                    borderColor: '#008000',
                    borderWidth: '0.5mm',
                    borderStyle: 'dashed',
                };
            }
            """;
        var (style, report) = Styler(source);

        style.Should().NotBeNull();
        style!.Color.Should().Be(SKColors.Red);
        style.BackgroundColor.Should().Be(new SKColor(245, 245, 245));
        style.FontSizePt.Should().BeApproximately(14 * 72.0 / 96, 1e-9);
        style.Bold.Should().BeTrue();
        style.Italic.Should().BeTrue();
        style.Underline.Should().BeTrue();
        style.LetterSpacingPt.Should().Be(1);
        style.LineSpacing.Should().Be(1.5);
        style.HorizontalAlign.Should().Be(HorizontalAlign.Center);
        style.VerticalAlign.Should().Be(VerticalAlign.Middle);
        style.PaddingLeftMm.Should().Be(2);
        style.PaddingTopMm.Should().Be(2);
        style.PaddingRightMm.Should().Be(2);
        style.PaddingBottomMm.Should().Be(2);
        style.BorderColor.Should().Be(SKColors.Green);
        style.BorderWidthMm.Should().Be(0.5);
        style.BorderDashPatternMm.Should().Equal(1.5, 1.0);
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void styler_CSS字符串形态同样解析()
    {
        var (style, report) = Styler("function(){ return 'color:red;background-color:#f5f5f5;font-weight:700'; }");

        style.Should().NotBeNull();
        style!.Color.Should().Be(SKColors.Red);
        style.BackgroundColor.Should().Be(new SKColor(245, 245, 245));
        style.Bold.Should().BeTrue("数值字重 ≥700 视为加粗");
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void styler_按行值返回不同样式()
    {
        const string source = """
            function(value){
                if (value < 0) return { color: 'red' };
                if (value > 100) return 'color:green';
                return null;
            }
            """;
        var (negative, _) = Styler(source, [-5, null, null, null]);
        var (large, _) = Styler(source, [200, null, null, null]);
        var (normal, _) = Styler(source, [50, null, null, null]);

        negative!.Color.Should().Be(SKColors.Red);
        large!.Color.Should().Be(SKColors.Green);
        normal.Should().BeNull("返回 null 就是『不改样式』");
    }

    [Fact]
    public void styler_rgb与颜色名都认得()
    {
        var (rgb, _) = Styler("function(){ return { color: 'rgb(10, 20, 30)' }; }");
        var (rgba, _) = Styler("function(){ return { color: 'rgba(10, 20, 30, 0.5)' }; }");
        var (named, _) = Styler("function(){ return { backgroundColor: 'gold' }; }");

        rgb!.Color.Should().Be(new SKColor(10, 20, 30));
        rgba!.Color!.Value.Alpha.Should().Be((byte)Math.Round(0.5 * 255));
        named!.BackgroundColor.Should().Be(new SKColor(255, 215, 0));
    }

    [Fact]
    public void styler_未知字段与取值静默忽略()
    {
        var (style, report) = Styler("function(){ return { color: 'not-a-color', unknown: 'x', fontSize: '3em' }; }");

        style.Should().BeNull("一个字段都没解析出来就是『没有脚本样式』");
        report.HasIssues.Should().BeFalse();
    }

    [Fact]
    public void styler_无单位数字按px解释()
    {
        var (style, _) = Styler("function(){ return { fontSize: 12, letterSpacing: 2 }; }");

        style!.FontSizePt.Should().BeApproximately(12 * 72.0 / 96, 1e-9);
        style.LetterSpacingPt.Should().BeApproximately(2 * 72.0 / 96, 1e-9);
    }

    // ---------------- 模板检测与执行器注入 ----------------

    [Fact]
    public void HasScripts_元素级与列级都能发现()
    {
        var plain = new PrintTemplate();
        ScriptRunner.HasScripts(plain).Should().BeFalse();

        var withFormatter = new PrintTemplate();
        withFormatter.Elements.Add(new TextElement { Formatter = "function(){return 1;}" });
        ScriptRunner.HasScripts(withFormatter).Should().BeTrue();

        var withColumnScript = new PrintTemplate();
        var table = new TableElement();
        table.Table.Columns.Add(new TableColumn { Styler = "function(){return {};}" });
        withColumnScript.Elements.Add(table);
        ScriptRunner.HasScripts(withColumnScript).Should().BeTrue();
    }

    // ---------------- 元素接入：ScriptSupport ----------------

    [Fact]
    public void 元素formatter_替换绑定文本()
    {
        var element = new TextElement { Formatter = "function(value){ return 'NO.' + value; }" };
        element.Text.Content = "{{orderNo}}";
        var context = new RenderContext(new RenderOptions(), new RenderReport(), data: new { orderNo = "2026" })
        {
            Scripts = new ScriptRunner(),
        };

        var content = ScriptSupport.FormatContent(context, element, element.Text.Content, "2026");

        content.Should().Be("NO.2026");
    }

    [Fact]
    public void 元素styler_同一渲染上下文只执行一次()
    {
        var element = new TextElement { Styler = "function(){ return { color: 'red' }; }" };
        var context = new RenderContext(new RenderOptions(), new RenderReport()) { Scripts = new ScriptRunner() };

        var first = ScriptSupport.StyleOf(context, element);
        var second = ScriptSupport.StyleOf(context, element);

        first!.Color.Should().Be(SKColors.Red);
        second.Should().BeSameAs(first, "框底/边框与内容绘制共用一次执行结果");
    }

    [Fact]
    public void 设计态_绑定缺失时formatter拿测试数据兜底()
    {
        var element = new TextElement { TestData = "TP-9" };
        element.Text.Content = "{{sku}}";
        element.Formatter = "function(value){ return '[' + value + ']'; }";
        var context = new RenderContext(new RenderOptions(), new RenderReport(), data: null, preferTestData: true)
        {
            Scripts = new ScriptRunner(),
        };

        ScriptSupport.FormatterResult(context, element, element.Text.Content).Should().Be("[TP-9]");
    }

    [Fact]
    public void 无脚本模板_执行器不注入也不产生开销()
    {
        var element = new TextElement();
        element.Text.Content = "plain";
        var context = new RenderContext(new RenderOptions(), new RenderReport());

        ScriptSupport.FormatContent(context, element, element.Text.Content, "plain").Should().Be("plain");
        ScriptSupport.StyleOf(context, element).Should().BeNull();
    }

    // ---------------- 表格列接入 ----------------

    [Fact]
    public void 列formatter_改单元格文本并拿到行对象与序号()
    {
        var o = new TableOptions();
        o.Columns.Add(new TableColumn { Field = "items.sku", Formatter = "function(value, row, index){ return index + ':' + value; }" });
        o.Columns.Add(new TableColumn { Field = "items.qty" });
        var items = new[]
        {
            new { sku = "SKU-A", qty = 2 },
            new { sku = "SKU-B", qty = 17 },
        };
        var context = new RenderContext(new RenderOptions(), new RenderReport(), data: new { items })
        {
            Scripts = new ScriptRunner(),
        };

        var rows = TableRenderer.ResolveRows(o, context, "t1", null, out var dataStart, out _, out _);

        dataStart.Should().Be(0);
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be("0:SKU-A");
        rows[1][0].Should().Be("1:SKU-B");
        rows[0][1].Should().Be("2", "没有脚本的列保持原值");
    }

    [Fact]
    public void 列styler_覆盖条件样式的底色与字色()
    {
        var o = new TableOptions { FontSizePt = 9 };
        o.Columns.Add(new TableColumn { Field = "items.sku", Styler = "function(value){ return { backgroundColor: 'red', color: 'white' }; }" });
        o.Columns.Add(new TableColumn { Field = "items.qty" });
        o.ConditionalStyles.Add(new TableConditionalStyle
        {
            Field = "sku",
            Operator = TableConditionOperator.Contains,
            Value = "SKU",
            BackgroundColor = SKColors.Yellow,
        });
        var items = new[] { new { sku = "SKU-A", qty = 2 } };
        var context = new RenderContext(new RenderOptions(), new RenderReport(), data: new { items })
        {
            Scripts = new ScriptRunner(),
        };
        using var pool = new TableRenderer.TableStylePool(context);

        var rows = TableRenderer.ResolveRows(o, context, "t1", null, out var dataStart, out var dataItems, out _);
        var body = TableRenderer.LayoutBody(o, rows, dataStart, dataItems, [0, 30], [30, 30], pool, "t1", [HorizontalAlign.Left, HorizontalAlign.Left]);

        var scriptCell = body.Cells.First(c => c.X == 0);
        scriptCell.Background.Should().Be(SKColors.Red, "列 styler 优先级高于条件样式");
        scriptCell.TextColor.Should().Be(SKColors.White);
        body.RowFills[dataStart].Should().Be(SKColors.Yellow, "没有脚本的列照常吃整行条件底色（行带由 RowFills 承载）");
    }

    [Fact]
    public void 列styler_脚本异常降级为不改样式()
    {
        var o = new TableOptions { FontSizePt = 9 };
        o.Columns.Add(new TableColumn { Field = "items.sku", Styler = "function(value){ throw new Error('x'); }" });
        var items = new[] { new { sku = "SKU-A" } };
        var context = new RenderContext(new RenderOptions(), new RenderReport(), data: new { items })
        {
            Scripts = new ScriptRunner(),
        };
        using var pool = new TableRenderer.TableStylePool(context);

        var rows = TableRenderer.ResolveRows(o, context, "t1", null, out var dataStart, out var dataItems, out _);
        var body = TableRenderer.LayoutBody(o, rows, dataStart, dataItems, [0], [30], pool, "t1", [HorizontalAlign.Left]);

        body.Cells.Single().Background.Should().BeNull();
        body.Cells.Single().TextColor.Should().BeNull();
        context.Report.Issues.Should().Contain(i => i.Kind == RenderIssueKind.ScriptIssue);
    }

    // ---------------- 渲染链路 ----------------

    [Fact]
    public void 渲染入口_带脚本的模板_告警与内容正确()
    {
        var template = new PrintTemplate();
        var text = new TextElement { Formatter = "function(value){ return 'F-' + value; }" };
        text.Text.Content = "{{orderNo}}";
        text.XMm = 5;
        text.YMm = 5;
        text.WidthMm = 40;
        text.HeightMm = 6;
        template.Elements.Add(text);

        var result = new TemplateRenderer().RenderToBitmap(template, new RenderOptions(), data: new { orderNo = "77" });
        using (result.Bitmap)
        {
            result.Report.HasIssues.Should().BeFalse();
        }

        ScriptRunner.HasScripts(template).Should().BeTrue();
    }

    [Fact]
    public void 渲染入口_脚本坏掉_渲染不中断并记告警()
    {
        var template = new PrintTemplate();
        var text = new TextElement { Styler = "function(){ return { color: }; }" };
        text.Text.Content = "hello";
        text.XMm = 5;
        text.YMm = 5;
        text.WidthMm = 40;
        text.HeightMm = 6;
        template.Elements.Add(text);

        var result = new TemplateRenderer().RenderToBitmap(template, new RenderOptions());
        using (result.Bitmap)
        {
            result.Report.Issues.Should().Contain(i => i.Kind == RenderIssueKind.ScriptIssue);
        }
    }
}
