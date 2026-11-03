using System.Globalization;
using System.IO;
using FluentAssertions;
using Xunit;

namespace CrossPrint.I18n.Tests;

public sealed class LocalizationSwitchTests
{
    [Fact]
    public void 环境变量优先于设置文件和系统语言()
    {
        LocalizationSwitch.Resolve("zh-CN", "en-US", "fr-FR").Should().Be("zh-CN");
    }

    [Fact]
    public void 环境变量system表示跟随系统并压过设置文件()
    {
        LocalizationSwitch.Resolve("system", "en-US", "zh-CN").Should().Be("zh-CN");
    }

    [Fact]
    public void 没有环境变量时用设置文件()
    {
        LocalizationSwitch.Resolve(null, "en-US", "zh-CN").Should().Be("en-US");
    }

    [Fact]
    public void 设置文件为system时跟随系统()
    {
        LocalizationSwitch.Resolve(null, "system", "zh-CN").Should().Be("zh-CN");
    }

    [Fact]
    public void 全都为空时回落英文()
    {
        LocalizationSwitch.Resolve(null, null, null).Should().Be("en-US");
        LocalizationSwitch.Resolve("", "   ", "").Should().Be("en-US");
    }

    [Fact]
    public void 非法环境变量被忽略并继续回落设置文件()
    {
        LocalizationSwitch.Resolve("xx-NOT-A-CULTURE", "zh-CN", "en-US").Should().Be("zh-CN");
    }

    [Fact]
    public void 非法设置文件被忽略并回落到系统语言()
    {
        LocalizationSwitch.Resolve(null, "xx-NOT-A-CULTURE", "en-US").Should().Be("en-US");
    }

    [Fact]
    public void 非法系统语言回落英文()
    {
        LocalizationSwitch.Resolve(null, null, "xx-NOT-A-CULTURE").Should().Be("en-US");
    }

    [Fact]
    public void 保存后能原样读回()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("ui-culture.txt");

        LocalizationSwitch.Save("zh-CN", path).Should().BeTrue();
        LocalizationSwitch.ReadSaved(path).Should().Be("zh-CN");
    }

    [Fact]
    public void 保存system或空值会删除设置文件()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("ui-culture.txt");

        LocalizationSwitch.Save("en-US", path).Should().BeTrue();
        File.Exists(path).Should().BeTrue();

        LocalizationSwitch.Save(LocalizationSwitch.SystemValue, path).Should().BeTrue();
        File.Exists(path).Should().BeFalse();

        LocalizationSwitch.Save("en-US", path).Should().BeTrue();
        LocalizationSwitch.Save(null, path).Should().BeTrue();
        File.Exists(path).Should().BeFalse();
        LocalizationSwitch.ReadSaved(path).Should().BeNull();
    }

    [Fact]
    public void 非法语言不落盘并返回false()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("ui-culture.txt");

        LocalizationSwitch.Save("xx-NOT-A-CULTURE", path).Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Apply按设置文件设置CurrentUICulture()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("ui-culture.txt");
        LocalizationSwitch.Save("zh-CN", path).Should().BeTrue();

        var original = CultureInfo.CurrentUICulture;
        try
        {
            var applied = LocalizationSwitch.Apply(path);

            applied.Should().Be("zh-CN");
            CultureInfo.CurrentUICulture.Name.Should().Be("zh-CN");
            CultureInfo.DefaultThreadCurrentUICulture!.Name.Should().Be("zh-CN");
            Localizer.T("Element.Text").Should().Be("文字");
        }
        finally
        {
            LocalizationSwitch.SetUiCulture(original.Name);
        }
    }

    [Fact]
    public void DisplayName跟随当前界面语言()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            LocalizationSwitch.SetUiCulture("zh-CN");
            LocalizationSwitch.DisplayName(null).Should().Be("跟随系统");
            LocalizationSwitch.DisplayName(LocalizationSwitch.SystemValue).Should().Be("跟随系统");
            LocalizationSwitch.DisplayName("zh-CN").Should().Be("中文");
            LocalizationSwitch.DisplayName("en-US").Should().Be("English");

            LocalizationSwitch.SetUiCulture("en-US");
            LocalizationSwitch.DisplayName(null).Should().Be("Follow system");
            LocalizationSwitch.DisplayName("zh-CN").Should().Be("中文");
            LocalizationSwitch.DisplayName("en-US").Should().Be("English");
        }
        finally
        {
            LocalizationSwitch.SetUiCulture(original.Name);
        }
    }

    [Fact]
    public void PreferredCulture以环境变量为先且跟随系统时为null()
    {
        var original = Environment.GetEnvironmentVariable(LocalizationSwitch.EnvironmentVariable);
        using var dir = new TempDirectory();
        var path = dir.Combine("ui-culture.txt");
        try
        {
            Environment.SetEnvironmentVariable(LocalizationSwitch.EnvironmentVariable, "zh-CN");
            LocalizationSwitch.PreferredCulture().Should().Be("zh-CN");

            Environment.SetEnvironmentVariable(LocalizationSwitch.EnvironmentVariable, LocalizationSwitch.SystemValue);
            LocalizationSwitch.Save("en-US", path).Should().BeTrue();
            LocalizationSwitch.PreferredCulture(path).Should().BeNull("环境变量 system 表示跟随系统，压过设置文件");

            Environment.SetEnvironmentVariable(LocalizationSwitch.EnvironmentVariable, null);
            LocalizationSwitch.PreferredCulture(path).Should().Be("en-US");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalizationSwitch.EnvironmentVariable, original);
        }
    }

    sealed class TempDirectory : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), "crossprint-i18n-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_path);

        public string Combine(string relative) => Path.Combine(_path, relative);

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
