namespace L4D2ModManager.Core.Models;

/// <summary>Mod 分类（依据 VPK 内部目录结构自动推断）。</summary>
public enum ModCategory
{
    /// <summary>武器</summary>
    Weapon,
    /// <summary>人物 / 模型</summary>
    Character,
    /// <summary>音频 / 语音</summary>
    Audio,
    /// <summary>地图</summary>
    Map,
    /// <summary>脚本</summary>
    Script,
    /// <summary>UI / 界面</summary>
    Ui,
    /// <summary>其他</summary>
    Other,
}

/// <summary>分类的显示信息（图标 + 中文名）。</summary>
public static class ModCategoryInfo
{
    public static readonly IReadOnlyList<ModCategory> All = new[]
    {
        ModCategory.Weapon, ModCategory.Character, ModCategory.Audio,
        ModCategory.Map, ModCategory.Script, ModCategory.Ui, ModCategory.Other,
    };

    public static string Icon(ModCategory category) => category switch
    {
        ModCategory.Weapon => "🔫",
        ModCategory.Character => "👤",
        ModCategory.Audio => "🎵",
        ModCategory.Map => "🗺",
        ModCategory.Script => "⚙",
        ModCategory.Ui => "🎨",
        _ => "❓",
    };

    public static string DisplayName(ModCategory category) => category switch
    {
        ModCategory.Weapon => "武器",
        ModCategory.Character => "人物",
        ModCategory.Audio => "音频",
        ModCategory.Map => "地图",
        ModCategory.Script => "脚本",
        ModCategory.Ui => "UI",
        _ => "其他",
    };

    /// <summary>例如 "🔫 武器"。</summary>
    public static string Full(ModCategory category) => $"{Icon(category)} {DisplayName(category)}";

    /// <summary>用于搜索：分类的中文名与英文名。</summary>
    public static string SearchText(ModCategory category) =>
        $"{DisplayName(category)} {category}";

    public static bool TryParse(string? text, out ModCategory category)
    {
        category = ModCategory.Other;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        foreach (var item in All)
        {
            if (string.Equals(DisplayName(item), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Full(item), value, StringComparison.OrdinalIgnoreCase))
            {
                category = item;
                return true;
            }
        }
        return false;
    }
}
