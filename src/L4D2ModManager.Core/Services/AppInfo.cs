namespace L4D2ModManager.Core.Services;

/// <summary>程序基本信息。</summary>
public static class AppInfo
{
    public const string Name = "Left 4 Dead 2 Mod Manager";
    public const string ShortName = "L4D2 Mod Manager";
    public const string Version = "1.0.0";
    public const string UserAgent = "L4D2ModManager/1.0 (+https://steamcommunity.com/app/550/workshop/)";

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
