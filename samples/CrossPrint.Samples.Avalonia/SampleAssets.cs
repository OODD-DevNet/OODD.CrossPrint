namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 样例资产（模板 JSON / hiprint 示例）读取：桌面从输出目录读文件；
/// 浏览器（WASM）没有文件系统，改读 Browser 头工程打进程序集的内嵌资源
/// （LogicalName = <c>SampleAssets::&lt;文件名&gt;</c>，见 Browser 工程）。
/// 找不到返回 null，由调用方决定降级口径，与桌面行为一致。
/// </summary>
internal static class SampleAssets
{
    const string Prefix = "SampleAssets::";

#if BROWSER
    public static string? Read(string name)
    {
        using var stream = typeof(SampleAssets).Assembly.GetManifestResourceStream(Prefix + name);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
#else
    public static string? Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
#endif
}
