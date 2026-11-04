using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>页码区间的解析/规范化：属性面板与分页引擎共用同一份口径。</summary>
public class PageRangeSpecTests
{
    [Fact]
    public void 规范化_全角逗号与倒置区间与前导零()
    {
        PageRangeSpec.Normalize(" 1， 5-3 ，1, 08 ").Should().Be("1,3-5,8");
        PageRangeSpec.Normalize("all").Should().Be("all");
        PageRangeSpec.Normalize(" ALL ").Should().Be("all");
        PageRangeSpec.Normalize("*").Should().Be("*");
        PageRangeSpec.Normalize("  ").Should().BeNull();
        PageRangeSpec.Normalize(null).Should().BeNull();
    }

    [Fact]
    public void 规范化_非法片段原样保留_不吞用户输入()
    {
        PageRangeSpec.Normalize("abc,1-,3-5-7").Should().Be("abc,1-,3-5-7");
    }

    [Fact]
    public void 匹配_区间边界与all与星号()
    {
        const string spec = "1,3-5,8";
        new[] { 1, 3, 4, 5, 8 }.Should().OnlyContain(p => PageRangeSpec.Matches(spec, p));
        new[] { 0, 2, 6, 9 }.Should().OnlyContain(p => !PageRangeSpec.Matches(spec, p));
        PageRangeSpec.Matches("ALL", 4).Should().BeTrue();
        PageRangeSpec.Matches("*", 4).Should().BeTrue();
        PageRangeSpec.Matches("", 4).Should().BeFalse();
        PageRangeSpec.Matches(null, 4).Should().BeFalse();
    }

    [Fact]
    public void 规范化前后逐页语义完全一致()
    {
        foreach (var raw in new[] { "1,3-5,8", "1， 5-3 ，1, 08", "all", "*", "abc,1-,3-5-7", "  " })
        {
            var normalized = PageRangeSpec.Normalize(raw);
            for (var page = 1; page <= 12; page++)
                PageRangeSpec.Matches(normalized, page).Should().Be(
                    PageRangeSpec.Matches(raw, page),
                    $"规格 {raw} 在第 {page} 页的命中结果不能因规范化改变");
        }
    }

    [Fact]
    public void 可见性_unShow优先于show_空值表示每页可见()
    {
        PageRangeSpec.IsVisibleOn(null, null, 3).Should().BeTrue();
        PageRangeSpec.IsVisibleOn("1,3-5", null, 2).Should().BeFalse();
        PageRangeSpec.IsVisibleOn("1-5", "3", 3).Should().BeFalse("隐藏区间优先");
        PageRangeSpec.IsVisibleOn("1-5", "3", 4).Should().BeTrue();
        PageRangeSpec.IsVisibleOn("1，3-5", null, 4).Should().BeTrue("全角逗号与半角等价");
    }
}
