namespace L4D2ModManager.Core.Models;

/// <summary>ModDatabase.json 的文档结构。</summary>
public sealed class ModDatabaseDocument
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public List<ModItem> Mods { get; set; } = new();
}
