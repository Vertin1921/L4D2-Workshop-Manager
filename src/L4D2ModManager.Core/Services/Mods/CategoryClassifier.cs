using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>
/// 依据 VPK 内部目录结构（配合 addoninfo 标签与文件名）自动分类。
/// 规则参考需求：
///   models/weapons     → 武器
///   models/survivors   → 人物
///   sound/player       → 音频（语音）
///   scripts            → 脚本
///   materials          → 材质（归入 UI/其他，视是否 vgui 而定）
///   maps               → 地图
/// </summary>
public static class CategoryClassifier
{
    private sealed record Rule(ModCategory Category, string[] Patterns, int Weight);

    // 权重越大优先级越高；命中数量也参与打分
    private static readonly Rule[] Rules =
    {
        new(ModCategory.Map, new[] { "maps/", "/maps/", ".bsp" }, 100),
        new(ModCategory.Script, new[] { "scripts/", "/scripts/", ".nut", "scriptedmode", "sourcemod" }, 60),
        new(ModCategory.Weapon, new[]
        {
            "models/weapons", "models/v_", "models/w_", "materials/models/weapons",
            "sound/weapons", "scripts/weapons", "weapon_", "models/props_weapons",
        }, 55),
        new(ModCategory.Character, new[]
        {
            "models/survivors", "models/infected", "models/humans", "models/zombie",
            "materials/models/survivors", "materials/models/infected", "models/player",
            "sound/survivor", "voice/survivor", "models/witch", "models/common",
        }, 50),
        new(ModCategory.Audio, new[]
        {
            "sound/player", "sound/npc", "sound/music", "sound/ambient", "sound/voice",
            "sound/weapons", "sound/", ".wav", ".mp3", "sound/music/",
        }, 40),
        new(ModCategory.Ui, new[]
        {
            "materials/vgui", "resource/", "materials/hud", "panorama/", "materials/ui",
            "resource/closecaption", "materials/console",
        }, 45),
        new(ModCategory.Weapon, new[] { "枪", "武器", "weapon", "gun", "rifle", "ak47", "m16", "手枪", "步枪" }, 30),
        new(ModCategory.Character, new[] { "人物", "角色", "幸存者", "模型", "model", "survivor", "character", "皮肤" }, 28),
        new(ModCategory.Audio, new[] { "语音", "音效", "音频", "背景音乐", "voice", "sound", "audio", "music", "bgm" }, 26),
        new(ModCategory.Map, new[] { "地图", "关卡", "map", "campaign", "地图包" }, 26),
        new(ModCategory.Script, new[] { "脚本", "script", "插件", "mod脚本", "修改" }, 24),
        new(ModCategory.Ui, new[] { "界面", "ui", "hud", "准星", "crosshair", "菜单" }, 24),
    };

    /// <summary>
    /// Steam 创意工坊标签 → 分类。
    /// 标签是作者上传时自己选的，比内部路径更可靠，因此权重最高（200）。
    /// 短标签（ui / vo 等）用单词边界匹配，避免 "building" 命中 "ui" 之类误判。
    /// </summary>
    private static readonly (ModCategory Category, string[] Tags)[] TagRules =
    {
        (ModCategory.Map, new[]
        {
            "campaign", "map", "maps", "single map", "full campaign", "versus", "survival",
            "scavenge", "realism", "mutation", "co-op", "coop", "地图", "战役", "关卡",
        }),
        (ModCategory.Weapon, new[]
        {
            "weapon", "weapons", "gun", "melee", "items", "item", "throwable", "武器", "枪械", "近战",
        }),
        (ModCategory.Character, new[]
        {
            "survivor", "survivors", "character", "skin", "skins", "model", "models", "player",
            "infected", "人物", "角色", "模型", "皮肤", "丧尸",
        }),
        (ModCategory.Audio, new[]
        {
            "sound", "sounds", "audio", "music", "vo", "voice", "音频", "语音", "音效", "音乐",
        }),
        (ModCategory.Script, new[]
        {
            "script", "scripts", "plugin", "vscript", "gameplay", "脚本", "插件", "玩法",
        }),
        (ModCategory.Ui, new[] { "ui", "gui", "hud", "crosshair", "interface", "界面", "准星", "菜单" }),
    };

    /// <summary>标签匹配：短标签要求单词边界，长标签用包含匹配。</summary>
    public static bool MatchesTag(string text, string tag)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tag)) return false;

        if (tag.Length > 3) return text.Contains(tag, StringComparison.OrdinalIgnoreCase);

        return System.Text.RegularExpressions.Regex.IsMatch(
            text,
            $@"(?<![a-z0-9]){System.Text.RegularExpressions.Regex.Escape(tag)}(?![a-z0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>体积占比权重：素材类目录的绝对文件数更能反映 Mod 类型。</summary>
    public static ModCategory Classify(
        string fileName,
        IReadOnlyList<string>? fileIndex,
        string? tagText = null,
        string? description = null)
    {
        var scores = new Dictionary<ModCategory, double>();
        void Add(ModCategory category, double score)
        {
            scores[category] = scores.GetValueOrDefault(category) + score;
        }

        // ① 先看 Steam 创意工坊标签（作者自己选的，最准）
        if (!string.IsNullOrWhiteSpace(tagText))
        {
            foreach (var (category, tags) in TagRules)
            {
                foreach (var tag in tags)
                {
                    if (!MatchesTag(tagText!, tag)) continue;
                    Add(category, 200);
                    break;
                }
            }
        }

        if (fileIndex != null)
        {
            foreach (var rawPath in fileIndex)
            {
                // FileIndex 元素可能带 "|CRC32" 后缀，这里统一解析成纯内部路径
                var path = ModFileEntry.Parse(rawPath).NormalizedPath;
                foreach (var rule in Rules)
                {
                    foreach (var pattern in rule.Patterns)
                    {
                        if (!pattern.Contains('/') && !pattern.StartsWith('.')) continue; // 纯关键字规则不用于路径
                        if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            // 路径命中：权重高，并且每多命中一个文件都累加（封顶避免刷屏）
                            Add(rule.Category, rule.Weight * 0.35);
                            break;
                        }
                    }
                }
            }
        }

        var keywordSource = string.Join(" ", new[] { fileName, tagText, description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(keywordSource))
        {
            foreach (var rule in Rules)
            {
                foreach (var pattern in rule.Patterns)
                {
                    if (pattern.Contains('/') || pattern.StartsWith('.')) continue; // 路径规则不用于关键字
                    if (keywordSource.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        Add(rule.Category, rule.Weight);
                }
            }
        }

        if (scores.Count == 0) return ModCategory.Other;

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => (int)kv.Key)
            .First().Key;
    }
}
