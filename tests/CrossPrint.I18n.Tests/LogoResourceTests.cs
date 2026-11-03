using FluentAssertions;
using Xunit;

namespace CrossPrint.I18n.Tests;

/// <summary>
/// About 对话框的 logo 只走 <see cref="LogoLoader"/> 这一条路径。资源名一旦和
/// 嵌入时的 ManifestResourceName 对不上（改 RootNamespace、换项目名、加子目录），
/// 所有宿主都会静默降级成"没有 logo"，编译期毫无提示。这里直接把名字和 PNG 头都钉住。
/// </summary>
public sealed class LogoResourceTests
{
    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Logo必须以约定资源名内嵌在主程序集()
    {
        typeof(Localizer).Assembly.GetManifestResourceNames()
            .Should().Contain(LogoLoader.ResourceName);
    }

    [Fact]
    public void Logo字节流必须是完整PNG()
    {
        var bytes = LogoLoader.GetLogoBytes();

        bytes.Should().NotBeNull("资源名对不上时 GetLogoBytes 返回 null，About 对话框就只剩文字");
        bytes!.Length.Should().BeGreaterThan(PngSignature.Length);
        bytes.Take(PngSignature.Length).Should().Equal(PngSignature);
    }
}
