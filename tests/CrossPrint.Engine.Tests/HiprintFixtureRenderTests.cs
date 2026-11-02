using CrossPrint.Core;
using CrossPrint.Core.Hiprint;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 跨层回归：真实 hiprint 夹具 → 导入器 → 模型 → 渲染器。
/// 导入器的单测证明"字段读对了"，这里证明"导入出来的模板真的整页画得出来"：
/// 没有元素因类型/数据问题被整条跳过，且页面确有墨迹。
/// 数据为空时文本会记 <c>BindingMissingField</c>，那是数据侧的正常告警，不是渲染故障。
/// </summary>
public class HiprintFixtureRenderTests
{
    const double Dpi = 150;

    static PrintTemplate Import(string fixture)
    {
        var path = Path.Combine(Fixture.RepoRoot(), "tests", "CrossPrint.Core.Tests", "Fixtures", "Hiprint", fixture);
        var result = HiprintTemplateImporter.Import(File.ReadAllText(path));
        result.HasErrors.Should().BeFalse($"{fixture} 里不该有元素被跳过");
        return result.Template;
    }

    [Theory]
    [InlineData("hiprint-template1.js")]
    [InlineData("hiprint-template4.js")]
    [InlineData("hiprint-template7.js")]
    [InlineData("hiprint-getjson.json")]
    public void 真实hiprint模板导入后整页可渲染(string fixture)
    {
        var template = Import(fixture);

        var report = new RenderReport();
        var bitmap = Fixture.Render(template, Dpi, report: report);
        using (bitmap)
        {
            var page = new SKRect(0, 0, (float)template.Paper.PageWidthMm, (float)template.Paper.PageHeightMm);

            // 实测非白像素：template1/getjson 111,464、template4 91,155、template7 40,110（150 DPI）。
            // 下限取最小值的一半，既允许字体差异，又能在"整页画空"时立刻变红。
            Ink.NonWhitePixelCountInMm(bitmap, Dpi, page)
                .Should().BeGreaterThan(20000, $"{fixture} 的线条/边框/表头至少应留下可见墨迹");

            report.Issues.Where(i => i.Kind == RenderIssueKind.ElementSkipped || i.Kind == RenderIssueKind.RendererMissing)
                .Should().BeEmpty("导入的元素类型都应有渲染器且绘制不抛");
        }
    }
}
