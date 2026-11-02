using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// 默认字体解析器的"这个族名到底存不存在"判定。它直接决定渲染告警里有没有 <c>FontFallback</c>：
/// 判错一边是天天误报缺字体（告警通道变噪音，真问题被淹掉），判错另一边是缺字体时一声不吭（字变了没人知道）。
/// 断言只引用**这台机器自己列出的字体**，不写死任何族名，因此在 Windows / macOS / Linux 上都成立。
/// 与 <see cref="FontResolverTests"/>（Resolve 永不返回 null / 缓存稳定）是两件事。
/// </summary>
public class FontFamilyExistsTests
{
    [Fact]
    public void 字体列表里的族名必须判定为存在()
    {
        var resolver = DefaultFontResolver.Shared;
        foreach (var family in SKFontManager.Default.FontFamilies)
            resolver.FamilyExists(family).Should().BeTrue($"列表里既然有 '{family}'，就不该报它不可用");
    }

    [Fact]
    public void 列表查不到但按名能选中的英文名也得算存在()
    {
        // 实测（探针 §22）：中文 Windows 的 FontFamilies 列的是本地化名"微软雅黑"，
        // 而 MatchCharacter 用的是字体内部英文名 "Microsoft YaHei"。
        // 只按列表判定会把装得好好的字体报成缺失——这正是本仓曾经误报 4 条 FontFallback 的原因。
        var manager = SKFontManager.Default;
        var listed = new HashSet<string>(manager.FontFamilies, StringComparer.OrdinalIgnoreCase);

        var hidden = new List<string>();
        foreach (var family in manager.FontFamilies)
        {
            using var matched = manager.MatchCharacter(family, SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null, 'A');
            var english = matched?.FamilyName?.Trim();
            if (english is { Length: > 0 } && !listed.Contains(english) && !hidden.Contains(english))
                hidden.Add(english);
        }

        hidden.Should().NotBeEmpty("这台机器上应至少存在一个'本地化名 ≠ 字体内部英文名'的族，否则本条测试没有覆盖力");
        foreach (var name in hidden)
            DefaultFontResolver.Shared.FamilyExists(name).Should().BeTrue($"'{name}' 能通过按名匹配选中，却不能从列表里查到");
    }

    [Fact]
    public void 不存在的族名判定为不存在且回退到真能画的字体()
    {
        const string bogus = "ZZZ Not Installed Anywhere 12345";

        DefaultFontResolver.Shared.FamilyExists(bogus).Should().BeFalse("缺字体必须被报告出来，否则用户永远不知道为什么字变了");

        // 注意：Resolve 返回的是解析器缓存里的 SKTypeface，调用方不得释放
        DefaultFontResolver.Shared.Resolve(bogus, false, false, 'A').FamilyName.Should().NotBeNullOrWhiteSpace("回退也得给出一张真能画的字体");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空族名不算存在(string? family)
        => DefaultFontResolver.Shared.FamilyExists(family!).Should().BeFalse();
}
