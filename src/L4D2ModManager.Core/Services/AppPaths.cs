namespace L4D2ModManager.Core.Services;

/// <summary>程序数据目录（配置、数据库、缩略图、下载缓存、日志）。</summary>
public static class AppPaths
{
    /// <summary>可用环境变量覆盖数据目录（便携运行 / 自动化测试）。</summary>
    public const string DataDirectoryEnvironmentVariable = "L4D2MM_DATA_DIR";

    /// <summary>便携模式标记文件名：放在 exe 同目录时，数据保存在 exe 目录的 Data 子目录。</summary>
    public const string PortableMarkerFileName = "portable.txt";

    private static string? _override;

    /// <summary>数据根目录，默认 %AppData%\L4D2ModManager。</summary>
    public static string Root => _override ?? ResolveRoot();

    /// <summary>手动指定数据目录（传 null 恢复默认）。</summary>
    public static void SetRoot(string? root)
    {
        _override = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
    }

    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string DatabaseFile => Path.Combine(Root, "ModDatabase.json");

    public static string ThumbnailDir => Path.Combine(Root, "thumbnails");

    /// <summary>下载中的临时文件目录。</summary>
    public static string DownloadTempDir => Path.Combine(Root, "downloads");

    public static string LogDir => Path.Combine(Root, "logs");

    /// <summary>应用图标等资源目录。</summary>
    public static string AssetsDir => Path.Combine(Root, "assets");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbnailDir);
        Directory.CreateDirectory(DownloadTempDir);
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>根据 Mod Key 生成缩略图缓存路径。</summary>
    public static string ThumbnailPathFor(string key, string extension)
    {
        var safe = Sanitize(key);
        if (string.IsNullOrEmpty(safe)) safe = Guid.NewGuid().ToString("N");
        if (!extension.StartsWith('.')) extension = "." + extension;
        return Path.Combine(ThumbnailDir, safe + extension);
    }

    /// <summary>查找某个 Key 已有的缩略图（任意常见扩展名）。</summary>
    public static string? FindExistingThumbnail(string key)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
        {
            var candidate = ThumbnailPathFor(key, ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return result.Length <= 120 ? result : result[..120];
    }

    private static string ResolveRoot()
    {
        // 1) 环境变量指定
        try
        {
            var custom = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(custom))
                return Path.GetFullPath(custom.Trim());
        }
        catch
        {
            // 忽略
        }

        // 2) 便携模式：exe 同目录存在 portable.txt
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName)))
                return Path.Combine(baseDirectory, "Data");
        }
        catch
        {
            // 忽略
        }

        // 3) 默认：用户漫游目录
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                return Path.Combine(appData, "L4D2ModManager");
        }
        catch
        {
            // 忽略
        }

        return Path.Combine(AppContext.BaseDirectory, "Data");
    }
}
