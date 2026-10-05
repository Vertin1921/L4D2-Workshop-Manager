namespace L4D2ModManager.Core.Services.Vpk;

/// <summary>addoninfo.txt 中的信息。</summary>
public sealed class AddonInfo
{
    public bool Found { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string? Url { get; init; }
    public string? SteamId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>标签拼接文本。</summary>
    public string TagText => Tags.Count == 0 ? string.Empty : string.Join(", ", Tags);

    public static readonly AddonInfo Empty = new() { Found = false };

    private static readonly string[] TitleKeys = { "addontitle", "title", "addonname", "name" };
    private static readonly string[] AuthorKeys = { "addonauthor", "author", "addoncreator" };
    private static readonly string[] DescriptionKeys = { "addondescription", "addondesc", "description", "desc", "addoninfo" };
    private static readonly string[] VersionKeys = { "addonversion", "version" };
    private static readonly string[] UrlKeys = { "addonurl", "url", "addonlink" };
    private static readonly string[] SteamIdKeys = { "addonsteamid", "steamid", "publishedfileid" };
    private static readonly string[] TagKeys = { "addontag", "addontags", "tag", "tags", "category", "categories", "addoncategory" };

    /// <summary>从 addoninfo.txt 文本解析。</summary>
    public static AddonInfo Parse(string? text)
    {
        var root = KeyValuesParser.Parse(text);
        if (root == null) return Empty;

        var tags = root.GetStrings(TagKeys)
            .SelectMany(SplitTags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var title = root.GetString(TitleKeys);
        var author = root.GetString(AuthorKeys);
        var description = root.GetString(DescriptionKeys);

        // 有些 Mod 会把描述里的换行写成字面 \n
        description = description?.Replace("\\n", "\n");

        bool found = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(author) ||
                     !string.IsNullOrWhiteSpace(description);

        return new AddonInfo
        {
            Found = found,
            Title = title,
            Author = author,
            Description = description,
            Version = root.GetString(VersionKeys),
            Url = root.GetString(UrlKeys),
            SteamId = root.GetString(SteamIdKeys),
            Tags = tags,
        };
    }

    /// <summary>从已打开的 VPK 中读取 addoninfo.txt 并解析。</summary>
    public static AddonInfo FromArchive(VpkArchive archive)
    {
        var entry = archive.FindAddonInfo();
        if (entry == null) return Empty;
        var text = archive.Read(entry);
        if (text == null || text.Length == 0) return Empty;
        var content = System.Text.Encoding.UTF8.GetString(text);
        if (content.Length > 0 && content[0] == '\uFEFF') content = content[1..];
        return Parse(content);
    }

    /// <summary>标签可能用逗号/分号/斜杠分隔。</summary>
    private static IEnumerable<string> SplitTags(string raw)
    {
        foreach (var part in raw.Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries))
            yield return part.Trim();
    }
}
