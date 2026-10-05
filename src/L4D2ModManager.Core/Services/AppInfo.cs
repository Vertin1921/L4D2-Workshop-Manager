namespace L4D2ModManager.Core.Services;

/// <summary>程序基本信息。</summary>
public static class AppInfo
{
    public const string Name = "Left 4 Dead 2 Mod Manager";
    public const string ShortName = "L4D2 Mod Manager";
    /// <summary>
    /// 程序版本：直接读取程序集版本（跟随 Directory.Build.props 的 &lt;Version&gt;），
    /// 这样每次用 build\bump-version.ps1 升版本号后，界面、--version、日志都会同步，不会忘记改这里。
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>联网请求的用户代理（带上当前版本）。</summary>
    public static string UserAgent => $"L4D2ModManager/{Version} (+https://steamcommunity.com/app/550/workshop/)";

    private static string ResolveVersion()
    {
        try
        {
            var assembly = typeof(AppInfo).Assembly;

            var informational = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // 形如 1.1.0+<commit>：只取语义版本部分
                var plus = informational!.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }

            var version = assembly.GetName().Version;
            return version == null ? "1.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "1.1.0";
        }
    }

    public const string WorkshopHomeUrl = "https://steamcommunity.com/app/550/workshop/";
    public const string WorkshopBrowseUrl = "https://steamcommunity.com/workshop/browse/?appid=550";

    /// <summary>创意工坊搜索地址。</summary>
    public static string BuildSearchUrl(string? query, string sort = "trend", int page = 1)
    {
        var url = $"https://steamcommunity.com/workshop/browse/?appid=550&browsesort={sort}&section=readytouseitems";
        if (page > 1) url += $"&p={page}";
        if (!string.IsNullOrWhiteSpace(query))
            url += "&searchtext=" + Uri.EscapeDataString(query.Trim());
        return url;
    }
}
