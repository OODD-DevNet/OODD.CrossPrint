using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CrossPrint.I18n.Tests;

/// <summary>
/// 源码 → 资源表的键覆盖回归：<see cref="Localizer"/> 找不到 key 时原样返回 key，
/// 界面上就会直接露出 <c>Toolbar.Print</c> 这类原始标识符。这里扫描所有宿主的
/// <c>Localizer.T/En/Zh("…")</c> 字面量，要求两份翻译表都收录。
/// </summary>
public sealed class LocalizerKeyCoverageTests
{
    static readonly Regex KeyCall = new(@"Localizer\.(?:T|En|Zh)\(\s*""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void 源码里引用的每个键都必须在两种语言表里存在()
    {
        var used = UsedKeys();
        used.Should().NotBeEmpty("扫描至少应找到宿主界面里的 Localizer 调用");

        var zh = Localizer.GetTable("zh-CN");
        var en = Localizer.GetTable("en-US");

        var missingZh = used.Where(k => !zh.ContainsKey(k)).ToArray();
        var missingEn = used.Where(k => !en.ContainsKey(k)).ToArray();

        missingZh.Should().BeEmpty(
            "缺中文译文时界面会显示原始 key：{0}",
            string.Join(", ", missingZh));
        missingEn.Should().BeEmpty(
            "缺英文译文时界面会显示原始 key：{0}",
            string.Join(", ", missingEn));
    }

    static SortedSet<string> UsedKeys()
    {
        var root = FindRepoRoot();
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var folder in new[] { "src", "samples" })
        {
            var directory = Path.Combine(root, folder);
            Directory.Exists(directory).Should().BeTrue($"{directory} 应在仓库根目录下");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                foreach (Match match in KeyCall.Matches(File.ReadAllText(file)))
                    used.Add(match.Groups[1].Value);
            }
        }

        return used;
    }

    static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OODD.CrossPrint.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("找不到仓库根目录（OODD.CrossPrint.sln）。");
    }
}
