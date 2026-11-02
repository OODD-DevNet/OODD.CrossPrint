using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>
/// 依赖方向门：内核不依赖任何宿主 UI，宿主只允许依赖内核层。
/// </summary>
/// <remarks>
/// 读项目图而不是读已编译程序集：编译引用会把框架程序集一并带进来，
/// 而这里要守的恰恰是"谁 ProjectReference 了谁"这一条人写的边界。
/// </remarks>
public sealed class DependencyDirectionTests
{
    static readonly string Root = FindRoot();

    [Fact]
    public void 扫描到了预期的项目数_空扫描不能假绿()
    {
        var projects = LoadProjects();

        projects.Keys.Should().Contain(new[]
        {
            "CrossPrint.Core", "CrossPrint.Engine", "CrossPrint.Designer", "CrossPrint.Printing",
        });
        projects.Count.Should().BeGreaterThanOrEqualTo(7,
            "项目图读取失败（路径或 XML 结构变了）时，后面的方向断言会全部空转");
    }

    [Fact]
    public void Core不引用任何项目_Engine只引用Core_Designer只引用Core与Engine()
    {
        var projects = LoadProjects();

        projects["CrossPrint.Core"].Should().BeEmpty("Core 必须是最底层");

        projects["CrossPrint.Engine"].Should().BeEquivalentTo(new[] { "CrossPrint.Core" });

        projects["CrossPrint.Designer"].Should().BeEquivalentTo(
            new[] { "CrossPrint.Core", "CrossPrint.Engine" },
            "Designer 是 UI 无关内核，不允许依赖宿主");
    }

    [Fact]
    public void 宿主只依赖内核层_且不反向引用彼此()
    {
        var projects = LoadProjects();
        var hosts = projects.Keys.Where(name => name.StartsWith("CrossPrint.Hosts.", StringComparison.Ordinal)).ToList();

        hosts.Should().NotBeEmpty("至少要扫描到已交付的宿主适配包");

        foreach (var host in hosts)
        {
            projects[host].Should().OnlyContain(reference =>
                    reference == "CrossPrint.Core"
                    || reference == "CrossPrint.Engine"
                    || reference == "CrossPrint.Designer"
                    || reference == "CrossPrint.Printing",
                $"{host} 只能依赖内核层，不能依赖另一个宿主包");
        }
    }

    [Fact]
    public void 内核层项目文件里不出现任何宿主UI框架的依赖()
    {
        var projects = LoadProjects();
        var uiFrameworks = new[]
        {
            "Avalonia", "Eto.Forms", "SkiaSharp.Views.WPF", "SkiaSharp.Views.WinUI",
            "SkiaSharp.Views.WindowsForms", "SkiaSharp.Views.Uno.WinUI", "Microsoft.WindowsAppSDK",
        };

        foreach (var name in new[] { "CrossPrint.Core", "CrossPrint.Engine", "CrossPrint.Designer" })
        {
            var text = File.ReadAllText(PathOf(name));
            foreach (var framework in uiFrameworks)
            {
                text.Should().NotContain(framework,
                    $"{name} 是 UI 无关层，不能直接引用 {framework}");
            }
        }
    }

    static string PathOf(string projectName)
    {
        var matches = Directory.GetFiles(Root, projectName + ".csproj", SearchOption.AllDirectories);
        matches.Should().ContainSingle($"{projectName}.csproj 应当有且只有一个");
        return matches[0];
    }

    static Dictionary<string, List<string>> LoadProjects()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(file);
            var references = document.Descendants("ProjectReference")
                .Select(node => (string?)node.Attribute("Include"))
                .Where(include => include is not null)
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .ToList();

            map[Path.GetFileNameWithoutExtension(file)] = references;
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
