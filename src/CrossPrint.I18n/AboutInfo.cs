using System.Reflection;

namespace CrossPrint.I18n;

/// <summary>
/// 关于框静态信息：产品名、版本、仓库链接、第三方库清单、许可证。
/// 所有宿主 About Dialog 从这里取数据——避免每个宿主写一份。
/// </summary>
public static class AboutInfo
{
    /// <summary>产品名，用于 About Dialog 标题 / 窗口 Title。</summary>
    public const string ProductName = "OODD CrossPrint";
    /// <summary>GitHub 仓库地址。</summary>
    public const string RepoUrl = "https://github.com/OODD-DevNet/OODD.CrossPrint";
    /// <summary>产品官网（当前与仓库同址）。</summary>
    public const string WebsiteUrl = "https://github.com/OODD-DevNet/OODD.CrossPrint";
    /// <summary>开源许可证标识符。</summary>
    public const string License = "Apache-2.0";

    /// <summary>当前程序集版本（只读 FileVersion，不含 Pre-release 标签）。</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var attr = asm.GetCustomAttribute<AssemblyFileVersionAttribute>();
            return attr?.Version ?? "0.1.0";
        }
    }

    /// <summary>
    /// 本仓库直接依赖的第三方运行时库。每项提供：显示名 / 简短描述 / 许可证 / 仓库链接。
    /// About Dialog 展示时两栏布局：左侧名+描述，右侧 License + GitHub。
    /// </summary>
    public static readonly ThirdParty[] ThirdParty =
    [
        new("SkiaSharp",
            "跨平台 2D 矢量图形引擎 .NET 绑定（引擎渲染内核）",
            "MIT",
            "https://github.com/mono/SkiaSharp"),
        new("ZXing.Net",
            "二维码 / 一维码生成（零额外运行时依赖）",
            "Apache-2.0",
            "https://github.com/micjahn/ZXing.Net"),
        new("Jint",
            "纯托管 JavaScript 解释器（模板内脚本执行）",
            "BSD-3-Clause",
            "https://github.com/sebastienros/jint"),
        new("Avalonia",
            "跨平台 .NET UI 框架（Avalonia 宿主 + Browser 在线示例）",
            "MIT",
            "https://github.com/AvaloniaUI/Avalonia"),
        new("System.Text.Json",
            ".NET 内置 JSON 序列化（模板 JSON 读写）",
            "MIT",
            "https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json"),
    ];
}

/// <summary>本仓库直接依赖的一个第三方运行时库的元数据。</summary>
/// <param name="Name">显示名，例如 "SkiaSharp"。</param>
/// <param name="Description">简短中文描述，About Dialog 左侧展示。</param>
/// <param name="License">许可证标识符，例如 "MIT"、"Apache-2.0"。</param>
/// <param name="Url">项目主页 / GitHub 仓库链接。</param>
public readonly record struct ThirdParty(
    string Name,
    string Description,
    string License,
    string Url);
