namespace L4D2ModManager.Core.Models;

/// <summary>方案预设规则。</summary>
public enum ProfilePreset
{
    /// <summary>自定义：只看 Overrides。</summary>
    Custom,
    /// <summary>全部启用。</summary>
    EnableAll,
    /// <summary>全部禁用。</summary>
    DisableAll,
    /// <summary>只启用武器与 UI。</summary>
    WeaponsOnly,
    /// <summary>写实向：关闭武器类与脚本类。</summary>
    Realism,
    /// <summary>联机安全：关闭脚本类与冲突的 Mod。</summary>
    OnlineSafe,
}

/// <summary>配置方案：一套 Mod 启停组合。</summary>
public sealed class ModProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新方案";
    public string Description { get; set; } = string.Empty;

    /// <summary>内置方案不可删除。</summary>
    public bool IsBuiltIn { get; set; }

    public ProfilePreset Preset { get; set; } = ProfilePreset.Custom;

    /// <summary>在预设规则之上按文件名（小写）覆盖单个 Mod 的状态。</summary>
    public Dictionary<string, ModState> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static List<ModProfile> CreateBuiltIns() => new()
    {
        new ModProfile
        {
            Id = "builtin-single",
            Name = "单人模式",
            Description = "全部 Mod 启用，适合单人战役。",
            IsBuiltIn = true,
            Preset = ProfilePreset.EnableAll,
        },
        new ModProfile
        {
            Id = "builtin-online",
            Name = "联机模式",
            Description = "关闭脚本类 Mod 以及互相冲突的 Mod，保留模型 / 材质 / 语音。",
            IsBuiltIn = true,
            Preset = ProfilePreset.OnlineSafe,
        },
        new ModProfile
        {
            Id = "builtin-realism",
            Name = "写实模式",
            Description = "关闭武器类与脚本类 Mod，保留人物 / 语音 / 地图。",
            IsBuiltIn = true,
            Preset = ProfilePreset.Realism,
        },
        new ModProfile
        {
            Id = "builtin-weapons",
            Name = "枪械模式",
            Description = "只启用武器与 UI 类 Mod，其余全部关闭。",
            IsBuiltIn = true,
            Preset = ProfilePreset.WeaponsOnly,
        },
    };

    public string PresetText => Preset switch
    {
        ProfilePreset.EnableAll => "预设：全部启用",
        ProfilePreset.DisableAll => "预设：全部禁用",
        ProfilePreset.WeaponsOnly => "预设：仅武器 + UI",
        ProfilePreset.Realism => "预设：写实（无武器/脚本）",
        ProfilePreset.OnlineSafe => "预设：联机安全",
        _ => "预设：自定义",
    };

    public string OverrideText => Overrides.Count == 0 ? "无单独覆盖" : $"覆盖 {Overrides.Count} 个 Mod";
}
