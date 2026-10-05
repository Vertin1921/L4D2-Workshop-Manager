using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>冲突严重程度（按被覆盖文件所在的目录判断影响面）。</summary>
public enum ConflictSeverity
{
    /// <summary>其他文件。</summary>
    Low,

    /// <summary>脚本 / 资源定义类（可能改变玩法，但通常可共存）。</summary>
    Medium,

    /// <summary>模型 / 材质 / 音频 / 地图等资源覆盖：两个 Mod 会直接抢同一个文件，必须取舍。</summary>
    High,
}

/// <summary>
/// Mod 冲突检测：找出多个 Mod 同时包含同一个内部文件的组合。
///
/// 准确度关键（这些规则决定了会不会"报一堆假冲突"）：
///   1. **CRC32 相同 → 内容完全一样 → 不是冲突**，只是重复打包（例如多个插件都带同一份共享库脚本）；
///   2. **共享脚本库 / 必备文件** 默认忽略（`director_base_addon.nut` 之类的常见共享脚本），
///      用户还可以在设置里追加自定义忽略关键字；
///   3. 按被覆盖内容的类型给出严重程度，便于优先处理真正有影响的冲突；
///   4. 区分"同时启用"（真正会互相覆盖）与"潜在冲突"（有重复但没同时开）。
/// </summary>
public sealed class ConflictDetector
{
    /// <summary>几乎所有 Mod 都带、且不会互相影响的文件（按文件名忽略）。</summary>
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "addoninfo.txt",
        "addonimage.jpg",
        "addonimage.jpeg",
        "addonimage.png",
        "addonimage.bmp",
        "addonimage.gif",
        "readme.txt",
        "license.txt",
        "steam.inf",
        "addonlist.txt",
        "addonenglish.txt",
        "sound.cache",          // 引擎自动重建的音频索引，各 Mod 内容天然不同，不算冲突
        "materials.cache",
    };

    /// <summary>
    /// 默认忽略的常见"共享库 / 游戏自带脚本"路径（子串匹配）。
    /// 这些文件被大量插件重复携带，内容通常相同或本就由最后加载者决定，报出来只会淹没有效信息。
    /// </summary>
    private static readonly string[] DefaultIgnoredPathPatterns =
    {
        "scripts/vscripts/director_base_addon.nut",
        "scripts/vscripts/director_base.nut",
        "scripts/vscripts/left4dead2/",
        "scripts/vscripts/_lib",
        "scripts/vscripts/lib_",
        "scripts/vscripts/shared",
        "scripts/vscripts/wabisuke",
        "scripts/vscripts/thirdparty/",
        "scripts/vscripts/includes/",
    };

    public static IReadOnlyList<string> DefaultIgnoredPatterns => DefaultIgnoredPathPatterns;

    /// <summary>是否忽略指定内部文件（按文件名）。</summary>
    public static bool IsIgnored(string innerPath)
    {
        var name = innerPath;
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];

        return IgnoredFileNames.Contains(name) ||
               name.StartsWith("addonimage.", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase);   // 引擎自动生成的缓存文件
    }

    /// <summary>判断路径是否匹配内置或用户自定义的忽略关键字。</summary>
    public static bool MatchesIgnoredPattern(string normalizedPath, IEnumerable<string>? userPatterns)
    {
        foreach (var pattern in DefaultIgnoredPathPatterns)
        {
            if (normalizedPath.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (userPatterns == null) return false;

        foreach (var pattern in userPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var trimmed = pattern.Trim().Replace('\\', '/').ToLowerInvariant();
            if (normalizedPath.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>按被覆盖文件的位置判断严重程度。</summary>
    public static ConflictSeverity GetSeverity(string normalizedPath)
    {
        if (normalizedPath.StartsWith("models/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("materials/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("sound/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("maps/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("particles/", StringComparison.Ordinal) ||
            normalizedPath.Contains("/models/", StringComparison.Ordinal) ||
            normalizedPath.Contains("/materials/", StringComparison.Ordinal))
        {
            return ConflictSeverity.High;
        }

        if (normalizedPath.StartsWith("scripts/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("resource/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("missions/", StringComparison.Ordinal))
        {
            return ConflictSeverity.Medium;
        }

        return ConflictSeverity.Low;
    }

    /// <summary>
    /// 执行检测。
    /// </summary>
    /// <param name="mods">参与比较的 Mod（需要已建立带 CRC 的 FileIndex）。</param>
    /// <param name="includeDisabled">是否把已禁用的 Mod 也纳入比较（默认纳入，用于提前提示）。</param>
    /// <param name="userIgnorePatterns">用户自定义忽略关键字（设置页可维护）。</param>
    public ConflictReport Detect(
        IEnumerable<ModItem> mods,
        bool includeDisabled = true,
        IEnumerable<string>? userIgnorePatterns = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = mods.Where(m => m.FileIndex is { Count: > 0 })
                       .Where(m => includeDisabled || m.IsEnabled)
                       .ToList();

        // path → 参与该文件的 (Mod, CRC)
        var map = new Dictionary<string, List<(ModItem Mod, uint Crc, bool HasCrc)>>(StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0;
        int ignoredFiles = 0;

        foreach (var mod in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在比较：{mod.DisplayName}");

            foreach (var file in mod.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Path)) continue;
                if (IsIgnored(file.Path)) continue;

                var key = file.NormalizedPath;
                if (MatchesIgnoredPattern(key, userIgnorePatterns))
                {
                    ignoredFiles++;
                    continue;
                }

                if (!map.TryGetValue(key, out var bucket))
                {
                    bucket = new List<(ModItem, uint, bool)>(2);
                    map[key] = bucket;
                }

                bucket.Add((mod, file.Crc, file.HasCrc));
                totalFiles++;
            }
        }

        var report = new ConflictReport { TotalFilesCompared = totalFiles, IgnoredFiles = ignoredFiles };
        var involved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, bucket) in map)
        {
            if (bucket.Count < 2) continue;

            // 同一 Mod 内重复只算一次
            var byMod = bucket
                .GroupBy(b => b.Mod.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (byMod.Count < 2) continue;

            // 全部带 CRC 且完全一致 → 内容相同的重复打包，不是冲突
            var crcs = byMod.Where(b => b.HasCrc).Select(b => b.Crc).Distinct().ToList();
            bool identical = byMod.All(b => b.HasCrc) && crcs.Count == 1;

            var entry = new ConflictFileEntry
            {
                InnerPath = path,
                Severity = GetSeverity(path),
                ContentIdentical = identical,
            };

            foreach (var (mod, _, _) in byMod)
            {
                entry.Mods.Add(new ConflictParticipant
                {
                    ModKey = mod.Key,
                    ModName = mod.DisplayName,
                    FilePath = mod.FilePath,
                    Enabled = mod.IsEnabled,
                });
                involved.Add(mod.Key);
            }

            if (entry.Mods.Count < 2) continue;

            if (identical) report.IdenticalDuplicateCount++;
            if (entry.IsActiveConflict && !identical) report.ActiveConflictCount++;

            report.Files.Add(entry);
        }

        report.Files = report.Files
            .OrderBy(f => f.ContentIdentical ? 1 : 0)          // 真实冲突优先
            .ThenByDescending(f => (int)f.Severity)
            .ThenByDescending(f => f.IsActiveConflict)
            .ThenByDescending(f => f.Count)
            .ThenBy(f => f.InnerPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        report.ConflictedModCount = involved.Count;

        // 回写每个 Mod 的冲突计数（只统计"内容不同的真实冲突"）
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in report.Files.Where(f => !f.ContentIdentical))
        {
            foreach (var participant in file.Mods)
                counts[participant.ModKey] = counts.GetValueOrDefault(participant.ModKey) + 1;
        }

        foreach (var mod in list)
            mod.ConflictCount = counts.GetValueOrDefault(mod.Key);
        foreach (var mod in mods.Where(m => !counts.ContainsKey(m.Key) && m.ConflictCount != 0))
            mod.ConflictCount = 0;

        Log.Info($"冲突检测：{report.SummaryText}");
        return report;
    }
}
