using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>搜索 / 过滤 / 排序条件。</summary>
public sealed class ModQuery
{
    public string SearchText { get; set; } = string.Empty;

    public ModCategory? Category { get; set; }

    /// <summary>null = 全部；true = 只看启用；false = 只看禁用。</summary>
    public bool? Enabled { get; set; }

    public ModSortMode SortMode { get; set; } = ModSortMode.Default;

    public bool Descending { get; set; }

    /// <summary>默认排序时，禁用 Mod 是否优先显示。</summary>
    public bool DisabledFirst { get; set; } = true;

    public IReadOnlyList<ModItem> Apply(IEnumerable<ModItem> source)
    {
        var query = source;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(item => terms.All(term => Matches(item, term)));
        }

        if (Category.HasValue)
            query = query.Where(item => item.Category == Category.Value);

        if (Enabled.HasValue)
            query = query.Where(item => item.IsEnabled == Enabled.Value);

        return Sort(query).ToList();
    }

    /// <summary>搜索匹配：名称 / 作者 / 分类 / 标签 / 文件名 / 工坊 ID / 描述。</summary>
    public static bool Matches(ModItem item, string term)
    {
        static bool Contains(string? text, string term) =>
            !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase);

        return Contains(item.DisplayName, term)
               || Contains(item.Author, term)
               || Contains(item.Tags, term)
               || Contains(item.FileName, term)
               || Contains(item.WorkshopId, term)
               || Contains(item.Description, term)
               || Contains(ModCategoryInfo.DisplayName(item.Category), term)
               || Contains(ModCategoryInfo.Icon(item.Category), term);
    }

    public IEnumerable<ModItem> Sort(IEnumerable<ModItem> source)
    {
        IOrderedEnumerable<ModItem> ordered;

        switch (SortMode)
        {
            case ModSortMode.NameDesc:
                ordered = source.OrderByDescending(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;

            case ModSortMode.RecentlyAdded:
                ordered = Descending
                    ? source.OrderBy(m => m.AddedUtc).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    : source.OrderByDescending(m => m.AddedUtc).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                return ordered;

            case ModSortMode.EarliestAdded:
                ordered = Descending
                    ? source.OrderByDescending(m => m.AddedUtc).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    : source.OrderBy(m => m.AddedUtc).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                return ordered;

            case ModSortMode.SizeDesc:
                ordered = source.OrderByDescending(m => m.SizeBytes).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;

            case ModSortMode.SizeAsc:
                ordered = source.OrderBy(m => m.SizeBytes).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;

            case ModSortMode.Category:
                ordered = source.OrderBy(m => (int)m.Category).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;

            case ModSortMode.Default:
                if (!DisabledFirst)
                {
                    ordered = source.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                }
                ordered = source
                    .OrderBy(m => m.IsEnabled ? 1 : 0)   // 禁用优先
                    .ThenByDescending(m => m.ConflictCount) // 冲突多的靠前，方便处理
                    .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;

            default:
                ordered = Descending
                    ? source.OrderByDescending(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    : source.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                break;
        }

        return ordered.ThenBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase);
    }

    public static string DescribeSortMode(ModSortMode mode) => mode switch
    {
        ModSortMode.Default => "默认（禁用优先）",
        ModSortMode.NameAsc => "名称 A-Z",
        ModSortMode.NameDesc => "名称 Z-A",
        ModSortMode.RecentlyAdded => "最近添加",
        ModSortMode.EarliestAdded => "最早添加",
        ModSortMode.SizeDesc => "文件大小 ↓",
        ModSortMode.SizeAsc => "文件大小 ↑",
        ModSortMode.Category => "按分类",
        _ => mode.ToString(),
    };
}
