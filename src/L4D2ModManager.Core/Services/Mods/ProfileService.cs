using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

public sealed class ApplyProfileResult
{
    public int Enabled { get; set; }
    public int Disabled { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();

    public string Summary => Failed == 0
        ? $"方案已应用：启用 {Enabled} 个，禁用 {Disabled} 个"
        : $"方案已应用（部分失败）：启用 {Enabled}，禁用 {Disabled}，失败 {Failed}";
}

/// <summary>
/// 配置方案服务：把一套启用/禁用组合应用到当前 Mod 列表。
/// 内置方案（单人 / 联机 / 写实 / 枪械）按规则动态计算，新加入的 Mod 也会自动纳入。
/// </summary>
public sealed class ProfileService
{
    private readonly ModStateService _stateService;

    public ProfileService(ModStateService stateService) => _stateService = stateService;

    /// <summary>计算某个 Mod 在方案下应有的状态。</summary>
    public static ModState ResolveTarget(ModItem mod, ModProfile profile, ConflictReport? report)
    {
        var conflicted = mod.ConflictCount > 0;

        switch (profile.Preset)
        {
            case ProfilePreset.EnableAll:
                return ModState.Enabled;

            case ProfilePreset.DisableAll:
                return ModState.Disabled;

            case ProfilePreset.WeaponsOnly:
                return mod.Category is ModCategory.Weapon or ModCategory.Ui
                    ? ModState.Enabled
                    : ModState.Disabled;

            case ProfilePreset.Realism:
                if (mod.Category is ModCategory.Weapon or ModCategory.Script) return ModState.Disabled;
                if (conflicted && report is { ActiveConflictCount: > 0 }) return ModState.Disabled;
                return ModState.Enabled;

            case ProfilePreset.OnlineSafe:
                if (mod.Category == ModCategory.Script) return ModState.Disabled;
                if (conflicted && report is { ActiveConflictCount: > 0 }) return ModState.Disabled;
                return ModState.Enabled;

            default:
                return mod.State; // 自定义方案：保持现状，仅应用 Overrides
        }
    }

    /// <summary>应用方案。</summary>
    public ApplyProfileResult Apply(
        ModProfile profile,
        IReadOnlyList<ModItem> mods,
        ConflictReport? report = null,
        IProgress<string>? progress = null)
    {
        var result = new ApplyProfileResult();

        foreach (var mod in mods)
        {
            progress?.Report($"应用方案：{mod.DisplayName}");

            var target = ResolveTarget(mod, profile, report);

            // 单独覆盖优先
            if (TryGetOverride(profile, mod, out var overridden))
                target = overridden;

            if (mod.State == target) continue;

            if (_stateService.TrySetState(mod, target, out var error))
            {
                if (target == ModState.Enabled) result.Enabled++;
                else result.Disabled++;
            }
            else
            {
                result.Failed++;
                if (!string.IsNullOrWhiteSpace(error))
                    result.Errors.Add($"{Path.GetFileName(mod.FilePath)}: {error}");
            }
        }

        Log.Info($"[{profile.Name}] {result.Summary}");
        return result;
    }

    /// <summary>把当前状态保存成一个自定义方案。</summary>
    public static ModProfile Capture(string name, string description, IEnumerable<ModItem> mods)
    {
        var profile = new ModProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            Preset = ProfilePreset.Custom,
            IsBuiltIn = false,
        };

        foreach (var mod in mods)
            profile.Overrides[mod.FileName.ToLowerInvariant()] = mod.State;

        return profile;
    }

    public static bool TryGetOverride(ModProfile profile, ModItem mod, out ModState state)
    {
        state = ModState.Enabled;
        if (profile.Overrides == null || profile.Overrides.Count == 0) return false;

        var key = mod.FileName.ToLowerInvariant();
        if (profile.Overrides.TryGetValue(key, out state)) return true;

        foreach (var pair in profile.Overrides)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                state = pair.Value;
                return true;
            }
        }
        return false;
    }
}
