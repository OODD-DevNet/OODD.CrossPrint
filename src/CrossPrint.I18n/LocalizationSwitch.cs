using System.Globalization;
using System.IO;

namespace CrossPrint.I18n;

/// <summary>切换语言的结果，宿主据此决定关窗口还是提示。</summary>
public enum LanguageChangeOutcome
{
    /// <summary>偏好已保存，新进程已启动；当前窗口应关闭。</summary>
    Restarted,
    /// <summary>偏好已保存，但新进程没起来；提示用户手动重开。</summary>
    RestartFailed,
    /// <summary>设置文件写入失败（目录只读等），语言没有改变。</summary>
    SaveFailed,
}

/// <summary>
/// UI 语言的启动解析与持久化。宿主在构建界面**之前**调用 <see cref="Apply"/>：
/// 优先级为 环境变量 <c>CROSSPRINT_LANG</c> &gt; 程序目录的 <c>ui-culture.txt</c> &gt; 操作系统语言。
/// 语言只影响 <see cref="CultureInfo.CurrentUICulture"/>（界面文字），
/// 不改 <see cref="CultureInfo.CurrentCulture"/>——面单上的日期/金额仍按宿主本地口径格式化。
/// </summary>
/// <remarks>
/// 样例的界面文字在构造时一次性取好，运行时改 culture 不会自动重排；
/// 所以切换入口保存偏好后由各宿主重启进程（<see cref="TryStartNewInstance"/>）。
/// 这是刻意的：把七个宿主的每一条标签都做成可重入绑定，复杂度远高于一次重启。
/// </remarks>
public static class LocalizationSwitch
{
    /// <summary>覆盖 UI 语言的环境变量；值 <c>system</c> 表示跟随操作系统。</summary>
    public const string EnvironmentVariable = "CROSSPRINT_LANG";

    /// <summary>语言偏好文件名（放在程序目录，便携包跟着走）。</summary>
    public const string SettingsFileName = "ui-culture.txt";

    /// <summary>跟随操作系统的标记值（环境变量与设置文件都认）。</summary>
    public const string SystemValue = "system";

    /// <summary>默认设置文件路径：程序目录下的 <c>ui-culture.txt</c>。</summary>
    public static string DefaultSettingsPath => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    /// <summary>
    /// 解析应使用的 UI 语言名。纯函数，便于测试。
    /// </summary>
    /// <param name="environmentValue">环境变量值，可为 null。</param>
    /// <param name="savedValue">设置文件内容，可为 null。</param>
    /// <param name="osCultureName">操作系统语言名（通常是 <see cref="CultureInfo.InstalledUICulture"/>）。</param>
    /// <returns>非空的 culture 名；非法值被忽略并继续往下回落。</returns>
    public static string Resolve(string? environmentValue, string? savedValue, string? osCultureName)
    {
        var env = Normalize(environmentValue);
        if (env is not null)
        {
            if (IsSystem(env))
                return NormalizeOs(osCultureName);

            var normalizedEnv = NormalizeCulture(env);
            if (normalizedEnv is not null)
                return normalizedEnv;
        }

        var saved = Normalize(savedValue);
        if (saved is not null && !IsSystem(saved))
        {
            var normalizedSaved = NormalizeCulture(saved);
            if (normalizedSaved is not null)
                return normalizedSaved;
        }

        return NormalizeOs(osCultureName);
    }

    /// <summary>
    /// 按 环境变量 → 设置文件 → 操作系统 的优先级应用 UI 语言。
    /// </summary>
    /// <param name="settingsPath">设置文件路径；null 用 <see cref="DefaultSettingsPath"/>。</param>
    /// <returns>实际采用的 culture 名。</returns>
    public static string Apply(string? settingsPath = null)
    {
        var culture = Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            ReadSaved(settingsPath),
            CultureInfo.InstalledUICulture.Name);

        SetUiCulture(culture);
        return culture;
    }

    /// <summary>把进程（含后续线程）的 UI culture 设为 <paramref name="cultureName"/>。</summary>
    /// <param name="cultureName">culture 名；非法时回落 en-US。</param>
    /// <returns>实际采用的 culture 名。</returns>
    public static string SetUiCulture(string? cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(NormalizeCulture(cultureName) ?? "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Localizer.ClearCache();
        return culture.Name;
    }

    /// <summary>
    /// 保存语言偏好。<paramref name="cultureName"/> 为 null、空串或 <c>system</c> 时删除设置文件（跟随系统）。
    /// </summary>
    /// <param name="cultureName">culture 名或 <c>system</c>。</param>
    /// <param name="settingsPath">设置文件路径；null 用 <see cref="DefaultSettingsPath"/>。</param>
    /// <returns>写入/删除成功返回 true；IO 失败返回 false（宿主应提示"重启后生效"失败原因）。</returns>
    public static bool Save(string? cultureName, string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var value = Normalize(cultureName);

        try
        {
            if (value is null || IsSystem(value))
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }

            var canonical = NormalizeCulture(value);
            if (canonical is null)
                return false;

            File.WriteAllText(path, canonical);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>读取设置文件里的语言名；文件不存在/为空/为 system 时返回 null。</summary>
    /// <param name="settingsPath">设置文件路径；null 用 <see cref="DefaultSettingsPath"/>。</param>
    /// <returns>保存的 culture 名或 null（跟随系统）。</returns>
    public static string? ReadSaved(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            if (!File.Exists(path))
                return null;

            var value = Normalize(File.ReadAllText(path));
            return value is null || IsSystem(value) ? null : NormalizeCulture(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 切换 UI 语言：先保存偏好，再启动一个新进程。成功返回 <see cref="LanguageChangeOutcome.Restarted"/>，
    /// 宿主应立即关闭当前窗口，让新实例按新语言重建界面。
    /// </summary>
    /// <param name="cultureName">目标 culture；null / 空串 / <c>system</c> 表示跟随系统。</param>
    public static LanguageChangeOutcome Change(string? cultureName)
    {
        if (!Save(cultureName))
            return LanguageChangeOutcome.SaveFailed;

        return TryStartNewInstance()
            ? LanguageChangeOutcome.Restarted
            : LanguageChangeOutcome.RestartFailed;
    }

    /// <summary>语言在菜单里的显示名（用于"已保存，正在重启"之类的提示）。</summary>
    /// <param name="cultureName">目标 culture；null / <c>system</c> 返回"跟随系统"。</param>
    public static string DisplayName(string? cultureName)
    {
        var value = Normalize(cultureName);
        if (value is null || IsSystem(value))
            return Localizer.T("Language.System");

        return value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Localizer.T("Language.Chinese")
            : Localizer.T("Language.English");
    }

    /// <summary>
    /// 菜单勾选用：当前显式指定的语言；跟随系统（环境变量与设置文件都没指定）返回 null。
    /// 与 <see cref="Resolve"/> 同口径，非法值继续回落。
    /// </summary>
    /// <param name="settingsPath">设置文件路径；null 用 <see cref="DefaultSettingsPath"/>。</param>
    public static string? PreferredCulture(string? settingsPath = null)
    {
        var env = Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (env is not null)
        {
            if (IsSystem(env))
                return null;

            var normalizedEnv = NormalizeCulture(env);
            if (normalizedEnv is not null)
                return normalizedEnv;
        }

        var saved = Normalize(ReadSaved(settingsPath));
        if (saved is null || IsSystem(saved))
            return null;

        return NormalizeCulture(saved);
    }

    /// <summary>
    /// 启动当前可执行文件的新实例（语言切换用：新实例启动时读设置文件并按新语言重建界面）。
    /// 只在本机桌面宿主可用；拿不到可执行文件路径或启动失败返回 false。
    /// </summary>
    /// <returns>新实例是否成功启动。</returns>
    public static bool TryStartNewInstance()
    {
        try
        {
#if NET8_0_OR_GREATER
            var exe = Environment.ProcessPath;
#elif NETFRAMEWORK
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
#else
            string? exe = null;
#endif
            if (string.IsNullOrEmpty(exe))
                return false;

#if NET8_0_OR_GREATER
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
#else
            System.Diagnostics.Process.Start(exe);
#endif
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static bool IsSystem(string value) => value.Equals(SystemValue, StringComparison.OrdinalIgnoreCase);

    static string NormalizeOs(string? osCultureName)
        => NormalizeCulture(osCultureName) ?? "en-US";

    /// <summary>
    /// 只接受真正带翻译表的 culture（<see cref="Localizer.AvailableCultures"/>），返回资源清单里的规范写法。
    /// 不用 <see cref="CultureInfo.GetCultureInfo(string)"/> 校验：实测它在 ICU/NLS 下会接受任意名字，
    /// 挡不住 <c>xx-NOT-A-CULTURE</c> 这种拼错的值。
    /// </summary>
    static string? NormalizeCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        foreach (var culture in Localizer.AvailableCultures)
        {
            if (string.Equals(culture, trimmed, StringComparison.OrdinalIgnoreCase))
                return culture;
        }

        return null;
    }
}
