using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// 许可白名单门：`Directory.Packages.props` 里的每个包都必须出现在评审过的清单里，
/// 且许可证属于允许集合。
/// </summary>
/// <remarks>
/// 这不是"读 nuspec 自动生成"的检查——那样新增一个 AGPL 包也会自动通过。
/// 要挡的正是"悄悄多出一个依赖"，所以清单是人工维护的输入，测试只做对账。
/// </remarks>
public sealed class LicenseInventoryTests
{
    static readonly string Root = FindRoot();

    static readonly HashSet<string> AllowedLicenses = new(StringComparer.Ordinal)
    {
        "MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "MS-PL", "ISC", "Zlib",
        "CC0-1.0", "Unlicense", "Microsoft-EULA",
    };

    [Fact]
    public void 清单与版本锁定文件一一对应_多一个少一个都红()
    {
        var props = ReadProps();
        var inventory = ReadInventory();

        props.Count.Should().BeGreaterThanOrEqualTo(20, "props 解析失败时两边会同时为空，不能假绿");

        var missing = props.Keys.Except(inventory.Keys, StringComparer.Ordinal).ToList();
        var stale = inventory.Keys.Except(props.Keys, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty("这些包在 Directory.Packages.props 里，但没有经过许可评审");
        stale.Should().BeEmpty("这些条目已从 props 删除，清单需要同步收敛");
    }

    [Fact]
    public void 每个包的许可证都在允许集合里()
    {
        var inventory = ReadInventory();

        foreach (var (key, license) in inventory)
        {
            AllowedLicenses.Should().Contain(license,
                $"{key} 的许可证是 {license}，要么换包，要么把评审结论写进 docs/third-party-licenses.md 并扩展允许集合");
        }
    }

    [Fact]
    public void HTTP客户端与PDF库的清理结果仍成立()
    {
        var inventory = ReadInventory();

        inventory.Keys.Should().NotContain(key => key.StartsWith("QRCoder", StringComparison.Ordinal),
            "QRCoder 传递引入 System.Drawing.Common 或强制 SkiaSharp 3.x，见 docs/02 A4b");
        inventory.Keys.Should().NotContain(key => key.StartsWith("QuestPDF", StringComparison.Ordinal),
            "QuestPDF 是 AGPL/商业双许可，不得进入本库");
        inventory.Keys.Should().Contain("FluentAssertions|6.12.2",
            "7.0 起 FluentAssertions 改为商业许可，必须锁在 6.12.2");
    }

    static Dictionary<string, string> ReadProps()
    {
        var file = Path.Combine(Root, "Directory.Packages.props");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in XDocument.Load(file).Descendants("PackageVersion"))
        {
            var id = (string?)node.Attribute("Include");
            var version = (string?)node.Attribute("Version");
            if (id is null || version is null)
                continue;

            map[$"{id}|{version}"] = id;
        }

        return map;
    }

    static Dictionary<string, string> ReadInventory()
    {
        var file = Path.Combine(Root, "docs", "third-party-licenses.md");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(file))
        {
            var cells = line.Split('|');
            if (cells.Length < 5)
                continue;

            var id = cells[1].Trim();
            var version = cells[2].Trim();
            var license = cells[3].Trim();

            if (id.Length == 0 || id == "包" || id.StartsWith("---", StringComparison.Ordinal))
                continue;

            map[$"{id}|{version}"] = license;
        }

        return map;
    }

    static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OODD.CrossPrint.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（OODD.CrossPrint.sln）");
    }
}
