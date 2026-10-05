using System.Text.Json;
using L4D2ModManager.Core.Services.Steam;

namespace L4D2ModManager.Core.Services.Voice;

/// <summary>L4D2 / L4D1 的可替换语音角色。</summary>
public enum VoiceCharacter
{
    Coach,
    Ellis,
    Nick,
    Rochelle,

    // 1 代角色（在 L4D2 里由 DLC 提供语音）
    Bill,
    Francis,
    Louis,
    Zoey,

    Unknown,
}

/// <summary>角色信息。</summary>
public sealed record VoiceCharacterInfo(VoiceCharacter Character, string Id, string DisplayName, bool IsL4D1, string[] Aliases);

/// <summary>角色表与识别。</summary>
public static class VoiceCharacters
{
    public static readonly IReadOnlyList<VoiceCharacterInfo> All = new[]
    {
        new VoiceCharacterInfo(VoiceCharacter.Coach, "coach", "Coach（教练）", false, new[] { "coach", "教练" }),
        new VoiceCharacterInfo(VoiceCharacter.Ellis, "ellis", "Ellis（埃利斯）", false, new[] { "ellis", "艾利斯", "埃利斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Nick, "nick", "Nick（尼克）", false, new[] { "nick", "尼克" }),
        new VoiceCharacterInfo(VoiceCharacter.Rochelle, "rochelle", "Rochelle（罗谢尔）", false, new[] { "rochelle", "罗谢尔", "罗歇尔", "萝谢尔" }),

        new VoiceCharacterInfo(VoiceCharacter.Bill, "bill", "Bill（比尔）· 1 代", true, new[] { "bill", "比尔" }),
        new VoiceCharacterInfo(VoiceCharacter.Francis, "francis", "Francis（弗朗西斯）· 1 代", true, new[] { "francis", "弗朗西斯", "弗兰西斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Louis, "louis", "Louis（路易斯）· 1 代", true, new[] { "louis", "路易斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Zoey, "zoey", "Zoey（佐伊）· 1 代", true, new[] { "zoey", "zoe", "佐伊", "柔依" }),
    };

    /// <summary>1 代角色的语音放在这三个 DLC 目录里，替换时要一起处理。</summary>
    public static readonly IReadOnlyList<string> L4D1DlcFolders = new[]
    {
        "left4dead2_dlc1", "left4dead2_dlc2", "left4dead2_dlc3",
    };

    public const string BaseGameFolder = "left4dead2";

    public static VoiceCharacterInfo? Find(VoiceCharacter character) =>
        All.FirstOrDefault(c => c.Character == character);

    public static VoiceCharacterInfo? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return All.FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>这个角色应该写入哪些目录（相对游戏根目录）。</summary>
    public static IReadOnlyList<string> TargetFolders(VoiceCharacterInfo info) =>
        info.IsL4D1 ? L4D1DlcFolders : new[] { BaseGameFolder };

    /// <summary>
    /// 从文件夹名 / 文件路径 / VPK 内部路径里识别角色。
    /// 例如 "...\佐伊语音包\"、"sound/player/zoey/zoey_laugh01.wav" 都能识别为 Zoey。
    /// </summary>
    public static VoiceCharacterInfo? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Replace('\\', '/').ToLowerInvariant();

        // 先按 "sound/player/<id>" 这种最明确的路径匹配
        foreach (var info in All)
        {
            if (normalized.Contains($"sound/player/{info.Id}/", StringComparison.Ordinal) ||
                normalized.EndsWith($"sound/player/{info.Id}", StringComparison.Ordinal))
            {
                return info;
            }
        }

        // 再按别名（英文 ID / 中文名）匹配
        foreach (var info in All)
        {
            foreach (var alias in info.Aliases)
            {
                if (normalized.Contains(alias, StringComparison.OrdinalIgnoreCase)) return info;
            }
        }

        return null;
    }
}

/// <summary>一个写入目标（某个 DLC / 基础游戏的语音目录）。</summary>
public sealed record VoiceTarget(string Folder, string CharacterDirectory);

/// <summary>备份清单中的一条记录。</summary>
public sealed class VoiceBackupEntry
{
    public string CharacterId { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;

    /// <summary>相对 <c>&lt;游戏目录&gt;\&lt;folder&gt;\sound\</c> 的路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>替换前磁盘上是否已有该文件（决定还原时是写回还是删除）。</summary>
    public bool HadOriginal { get; set; }

    /// <summary>备份文件相对 <c>&lt;备份根&gt;</c> 的路径（HadOriginal=false 时为空）。</summary>
    public string? BackupFile { get; set; }

    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
}

public sealed record VoiceReplaceResult(
    bool Success,
    int CopiedFiles,
    int BackedUpFiles,
    IReadOnlyList<string> Targets,
    string? Error)
{
    public string Summary => Success
        ? $"已替换 {CopiedFiles} 个语音文件（其中 {BackedUpFiles} 个原有文件已备份）" +
          (Targets.Count > 0 ? "；写入目录：" + string.Join("、", Targets) : string.Empty)
        : Error ?? "替换失败";
}

public sealed record VoiceRestoreResult(bool Success, int RestoredFiles, int RemovedFiles, string? Error)
{
    public string Summary => Success
        ? $"已还原 {RestoredFiles} 个文件" + (RemovedFiles > 0 ? $"，并移除 {RemovedFiles} 个新增文件" : string.Empty)
        : Error ?? "还原失败";
}

/// <summary>
/// 人物语音替换 + 备份/还原。
///
/// 工作方式：
///   1. 从源文件夹（用户拖进来的语音包）里找出所有音频文件，推导出相对
///      <c>sound\</c> 的路径（例如 <c>player\zoey\zoey_laugh01.wav</c>）；
///   2. 对每个目标目录（2 代角色 = left4dead2，1 代角色 = left4dead2_dlc1/2/3）
///      写入 <c>&lt;游戏根&gt;\&lt;目录&gt;\sound\&lt;相对路径&gt;</c>；
///   3. 覆盖前先把原文件复制到备份目录并记录清单；原本不存在的文件记 HadOriginal=false，
///      还原时直接删除，保证"一键还原"能回到替换前的状态。
/// </summary>
public sealed class VoiceReplacer
{
    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ogg" };

    private readonly string _backupRoot;
    private readonly string _manifestPath;

    public VoiceReplacer(string? backupRoot = null)
    {
        _backupRoot = backupRoot ?? Path.Combine(AppPaths.Root, "voice-backup");
        _manifestPath = Path.Combine(_backupRoot, "voice-manifest.json");
    }

    public string BackupRoot => _backupRoot;

    /// <summary>从 Steam 路径里定位游戏根目录（含 left4dead2 的那一层）。</summary>
    public static string? FindGameRoot(SteamPaths paths)
    {
        foreach (var gameDirectory in paths.GameDirectories)
        {
            if (!Directory.Exists(gameDirectory)) continue;

            if (Directory.Exists(Path.Combine(gameDirectory, VoiceCharacters.BaseGameFolder))) return gameDirectory;
            if (Directory.Exists(Path.Combine(gameDirectory, VoiceCharacters.BaseGameFolder, "left4dead2")))
                return Path.Combine(gameDirectory, VoiceCharacters.BaseGameFolder);
        }

        // 从 addons 目录反推：<游戏>\left4dead2\addons
        foreach (var addon in paths.AddonDirectories)
        {
            try
            {
                var left4dead2 = Directory.GetParent(addon);
                var root = left4dead2?.Parent;
                if (root != null && Directory.Exists(root.FullName)) return root.FullName;
            }
            catch
            {
                // 忽略
            }
        }

        return null;
    }

    /// <summary>该角色会被写入的目录列表（绝对路径）。</summary>
    public static IReadOnlyList<VoiceTarget> BuildTargets(string gameRoot, VoiceCharacterInfo info)
    {
        var list = new List<VoiceTarget>();
        foreach (var folder in VoiceCharacters.TargetFolders(info))
        {
            var characterDirectory = Path.Combine(gameRoot, folder, "sound", "player", info.Id);
            list.Add(new VoiceTarget(folder, characterDirectory));
        }

        return list;
    }

    /// <summary>执行替换。</summary>
    public VoiceReplaceResult Replace(
        string gameRoot,
        string sourceDirectory,
        VoiceCharacterInfo info,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(gameRoot))
                return new VoiceReplaceResult(false, 0, 0, Array.Empty<string>(), $"找不到游戏目录：{gameRoot}");

            if (!Directory.Exists(sourceDirectory))
                return new VoiceReplaceResult(false, 0, 0, Array.Empty<string>(), $"找不到语音文件夹：{sourceDirectory}");

            var files = Directory
                .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                return new VoiceReplaceResult(false, 0, 0, Array.Empty<string>(),
                    "该文件夹里没有找到音频文件（支持 .wav / .mp3 / .ogg）。\r\n" +
                    "请选择包含人物语音的文件夹，例如解压后的 sound\\player\\zoey\\…");
            }

            var backups = LoadBackups();
            var targets = BuildTargets(gameRoot, info);
            var writtenTargets = new List<string>();
            int copied = 0;
            int backedUp = 0;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var soundRoot = Path.Combine(gameRoot, target.Folder, "sound");
                if (!Directory.Exists(soundRoot))
                {
                    Log.Warn($"跳过不存在的目录：{soundRoot}");
                    continue;
                }

                writtenTargets.Add(target.Folder);

                foreach (var source in files)
                {
                    var relative = BuildRelativePath(sourceDirectory, source, info);
                    var destination = Path.Combine(soundRoot, relative);

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    // 备份（同一个文件只备份一次）
                    var already = backups.Any(b =>
                        string.Equals(b.CharacterId, info.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.Folder, target.Folder, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.RelativePath, relative, StringComparison.OrdinalIgnoreCase));

                    if (!already)
                    {
                        var entry = new VoiceBackupEntry
                        {
                            CharacterId = info.Id,
                            Folder = target.Folder,
                            RelativePath = relative,
                            HadOriginal = File.Exists(destination),
                        };

                        if (entry.HadOriginal)
                        {
                            var backupFile = Path.Combine(_backupRoot, info.Id, target.Folder, relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                            File.Copy(destination, backupFile, overwrite: true);
                            entry.BackupFile = Path.GetRelativePath(_backupRoot, backupFile);
                            backedUp++;
                        }

                        backups.Add(entry);
                    }

                    File.Copy(source, destination, overwrite: true);
                    copied++;
                    progress?.Report($"正在写入 {target.Folder}：{relative}");
                }
            }

            SaveBackups(backups);

            Log.Info($"语音替换：{info.DisplayName} → {string.Join('、', writtenTargets)}，共 {copied} 个文件");

            return new VoiceReplaceResult(true, copied, backedUp, writtenTargets, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new VoiceReplaceResult(false, 0, 0, Array.Empty<string>(),
                "没有权限写入游戏目录（Steam 若装在 Program Files 需要管理员权限）：\r\n" + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("语音替换失败", ex);
            return new VoiceReplaceResult(false, 0, 0, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>还原：character 为 null 表示还原全部角色。</summary>
    public VoiceRestoreResult Restore(VoiceCharacter? character, string? gameRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameRoot))
                return new VoiceRestoreResult(false, 0, 0, "找不到游戏目录，无法还原。");

            var backups = LoadBackups();
            var characterId = character.HasValue ? VoiceCharacters.Find(character.Value)?.Id : null;

            var targets = backups
                .Where(b => characterId == null || string.Equals(b.CharacterId, characterId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (targets.Count == 0)
            {
                return new VoiceRestoreResult(true, 0, 0, null);
            }

            int restored = 0;
            int removed = 0;

            foreach (var entry in targets)
            {
                var destination = Path.Combine(gameRoot, entry.Folder, "sound", entry.RelativePath);

                if (entry.HadOriginal && !string.IsNullOrWhiteSpace(entry.BackupFile))
                {
                    var backupFile = Path.Combine(_backupRoot, entry.BackupFile);
                    if (File.Exists(backupFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backupFile, destination, overwrite: true);
                        restored++;
                    }
                }
                else if (File.Exists(destination))
                {
                    File.Delete(destination);
                    removed++;
                }
            }

            backups = backups.Except(targets).ToList();
            SaveBackups(backups);

            // 清掉已还原角色的备份文件
            if (characterId != null)
            {
                try
                {
                    var directory = Path.Combine(_backupRoot, characterId);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理备份目录失败：{ex.Message}");
                }
            }

            Log.Info($"语音还原：{characterId ?? "全部角色"}，写回 {restored} 个，删除 {removed} 个");
            return new VoiceRestoreResult(true, restored, removed, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new VoiceRestoreResult(false, 0, 0,
                "没有权限写入游戏目录（可能需要管理员权限）：\r\n" + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("语音还原失败", ex);
            return new VoiceRestoreResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>已备份的角色 → 文件数。</summary>
    public IReadOnlyDictionary<string, int> BackupSummary()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in LoadBackups())
        {
            result[entry.CharacterId] = result.GetValueOrDefault(entry.CharacterId) + 1;
        }

        return result;
    }

    public List<VoiceBackupEntry> LoadBackups()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return new List<VoiceBackupEntry>();
            var json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<List<VoiceBackupEntry>>(json) ?? new List<VoiceBackupEntry>();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取语音备份清单失败：{ex.Message}");
            return new List<VoiceBackupEntry>();
        }
    }

    private void SaveBackups(List<VoiceBackupEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(_backupRoot);
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入语音备份清单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 推导写入用的相对路径：
    ///   · 源路径里出现 sound 目录 → 取 sound 之后的相对路径（如 player/zoey/x.wav）；
    ///   · 否则取"文件名"，放到该角色的 player 目录下。
    /// </summary>
    internal static string BuildRelativePath(string sourceRoot, string sourceFile, VoiceCharacterInfo info)
    {
        var relative = Path.GetRelativePath(sourceRoot, sourceFile).Replace('\\', '/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "sound", StringComparison.OrdinalIgnoreCase))
            {
                var tail = string.Join('/', parts.Skip(i + 1));
                if (!string.IsNullOrWhiteSpace(tail)) return tail.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        var fileName = Path.GetFileName(sourceFile);
        return Path.Combine("player", info.Id, fileName);
    }
}
