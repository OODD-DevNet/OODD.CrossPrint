using System.Reflection;
using FluentAssertions;
using Xunit;

namespace CrossPrint.I18n.Tests;

/// <summary>
/// 资源内嵌回归：MSBuild 的 AssignCulture 会把形如 <c>Strings.zh-CN.json</c> 的文件
/// 当成卫星资源的 culture 标记，塞进 <c>zh-CN/CrossPrint.I18n.resources.dll</c> 并把名字剥成
/// <c>Strings.json</c>。主程序集里一旦没有这两份 JSON，<see cref="Localizer"/> 就会整片返回 key。
/// 这组用例盯的就是"资源在主程序集里、且真的能取到译文"。
/// </summary>
public sealed class LocalizerResourceTests
{
    static readonly Assembly I18nAssembly = typeof(Localizer).Assembly;

    [Fact]
    public void 两份资源JSON必须内嵌在主程序集()
    {
        var names = I18nAssembly.GetManifestResourceNames();

        names.Should().Contain("CrossPrint.I18n.Strings.zh-CN.json",
            "中文资源被 AssignCulture 搬进卫星程序集后，主程序集里就只剩 logo");
        names.Should().Contain("CrossPrint.I18n.Strings.en-US.json",
            "英文资源同理；缺了它 fallback 也是空表");

        Localizer.AvailableCultures.Should().Contain(new[] { "zh-CN", "en-US" },
            "语言切换的合法性校验直接读这份清单");
    }

    [Fact]
    public void 无序言要求的culture会被拒绝并回落英文()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            LocalizationSwitch.SetUiCulture("fr-FR").Should().Be("en-US",
                "没有 Strings.fr-FR.json 时切过去也只会得到英文兜底");
        }
        finally
        {
            LocalizationSwitch.SetUiCulture(original.Name);
        }
    }

    [Fact]
    public void 中文表能取到样例界面用到的译文()
    {
        var table = Localizer.GetTable("zh-CN");

        table.Should().NotBeEmpty();
        table["Element.Text"].Should().Be("文字");
        table["Toolbar.Import"].Should().Be("导入");
        table["Paper.A4"].Should().Be("A4（210×297）");
        table["Menu.File"].Should().Be("文件(_F)");
        table["Toolbar.StyleLibrary"].Should().Be("样式库");
        table["Toolbar.Print"].Should().Be("打印");
        table["Toolbox.Hint"].Should().Be("双击工具或按回车添加到画布中心。");
        table["Paper.Orientation.Portrait"].Should().Be("纵向");
        table["JsonPanel.Synced"].Should().Be("已同步当前模板（{0} 个元素）");
    }

    [Fact]
    public void 英文表能取到样例界面用到的译文()
    {
        var table = Localizer.GetTable("en-US");

        table.Should().NotBeEmpty();
        table["Element.Text"].Should().Be("Text");
        table["Toolbar.Import"].Should().Be("Import");
        table["Paper.A4"].Should().Be("A4 (210×297)");
        table["Menu.File"].Should().Be("File(_F)");
        table["Toolbar.StyleLibrary"].Should().Be("Styles");
        table["Toolbar.Print"].Should().Be("Print");
        table["Toolbox.Hint"].Should().Be("Double-click a tool or press Enter to add it to the canvas center.");
        table["Paper.Orientation.Portrait"].Should().Be("portrait");
        table["JsonPanel.Synced"].Should().Be("Synced with canvas ({0} elements)");
    }

    [Fact]
    public void 两种语言的键集合必须完全一致且无空值()
    {
        var zh = Localizer.GetTable("zh-CN");
        var en = Localizer.GetTable("en-US");

        zh.Keys.Should().BeEquivalentTo(en.Keys, "任何一边新增文案都必须同步另一边，否则切语言会露 key");
        zh.Should().NotContain(kv => string.IsNullOrWhiteSpace(kv.Value));
        en.Should().NotContain(kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    [Theory]
    [InlineData("zh-CN", "已同步当前模板（3 个元素）")]
    [InlineData("en-US", "Synced with canvas (3 elements)")]
    public void 新补的格式串能按参数渲染(string culture, string expected)
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            LocalizationSwitch.SetUiCulture(culture);
            Localizer.T("JsonPanel.Synced", 3).Should().Be(expected);
        }
        finally
        {
            LocalizationSwitch.SetUiCulture(original.Name);
        }
    }

    [Fact]
    public void 切换CurrentUICulture后T返回对应译文()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            LocalizationSwitch.SetUiCulture("zh-CN");
            Localizer.T("Element.Text").Should().Be("文字");

            LocalizationSwitch.SetUiCulture("en-US");
            Localizer.T("Element.Text").Should().Be("Text");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
            LocalizationSwitch.SetUiCulture(original.Name);
        }
    }

    [Theory]
    [InlineData("zh-Hans-CN")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant-TW")]
    public void 带脚本子标签的中文culture落到中文表而不是英文(string culture)
    {
        Localizer.GetTable(culture)["Element.Text"].Should().Be("文字",
            "部分运行时（实测 Uno）把 zh-CN 规范化成带脚本子标签的 zh-Hans-CN，" +
            "候选顺序先英文的话中文系统会整片回落成英文（2026-11-06 真机筛查实锤）");
    }

    [Fact]
    public void CurrentUICulture被框架改写成带脚本子标签时T仍取中文()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-Hans-CN");
            Localizer.T("Element.Text").Should().Be("文字");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }
}
