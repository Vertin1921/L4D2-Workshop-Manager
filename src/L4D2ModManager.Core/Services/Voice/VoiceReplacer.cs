using System.IO.Compression;
using System.Text.Json;
using L4D2ModManager.Core.Services.Steam;
using L4D2ModManager.Core.Services.Vpk;

namespace L4D2ModManager.Core.Services.Voice;

/// <summary>L4D2 八名官方生还者。</summary>
public enum VoiceCharacter
{
    // 一代
    Bill,
    Francis,
    Louis,
    Zoey,

    // 二代
    Coach,
    Ellis,
    Nick,
    Rochelle,

    Unknown,
}

/// <summary>
/// 角色定义。
/// Codename 是游戏内部代号（语音目录名），必须与 VPK 内路径一致：
///   Coach=coach  Ellis=mechanic  Nick=gambler  Rochelle=producer
///   Bill=namvet  Francis=biker  Louis=manager  Zoey=teengirl
/// </summary>
public sealed record VoiceCharacterInfo(
    VoiceCharacter Character,
    string Codename,
    string EnglishName,
    string ChineseName,
    bool IsL4D1,
    string[] Aliases)
{

    public string DisplayName => $"{EnglishName}（{ChineseName}）";

    public string GenerationText => IsL4D1 ? "一代生还者" : "二代生还者";

    public string Initial => EnglishName.Length > 0 ? EnglishName[..1] : "?";

    /// <summary>语音目录（相对游戏目录），例如 sound\player\survivor\voice\mechanic。</summary>
    public string VoiceRelativeDirectory =>
        Path.Combine("sound", "player", "survivor", "voice", Codename);
}

/// <summary>角色表与识别。</summary>
public static class VoiceCharacters
{
    public static readonly IReadOnlyList<VoiceCharacterInfo> All = new[]
    {
        new VoiceCharacterInfo(VoiceCharacter.Bill, "namvet", "Bill", "比尔", true,
            new[] { "namvet", "bill", "比尔" }),
        new VoiceCharacterInfo(VoiceCharacter.Francis, "biker", "Francis", "弗朗西斯", true,
            new[] { "biker", "francis", "弗朗西斯", "弗兰西斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Louis, "manager", "Louis", "路易斯", true,
            new[] { "manager", "louis", "路易斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Zoey, "teengirl", "Zoey", "佐伊", true,
            new[] { "teengirl", "zoey", "zoe", "佐伊", "柔依" }),

        new VoiceCharacterInfo(VoiceCharacter.Coach, "coach", "Coach", "教练", false,
            new[] { "coach", "教练" }),
        new VoiceCharacterInfo(VoiceCharacter.Ellis, "mechanic", "Ellis", "埃利斯", false,
            new[] { "mechanic", "ellis", "埃利斯", "艾利斯" }),
        new VoiceCharacterInfo(VoiceCharacter.Nick, "gambler", "Nick", "尼克", false,
            new[] { "gambler", "nick", "尼克" }),
        new VoiceCharacterInfo(VoiceCharacter.Rochelle, "producer", "Rochelle", "罗谢尔", false,
            new[] { "producer", "rochelle", "罗谢尔", "罗歇尔", "萝谢尔" }),
    };

    /// <summary>一代生还者（界面上分组显示）。</summary>
    public static IReadOnlyList<VoiceCharacterInfo> L4D1 { get; } = All.Where(c => c.IsL4D1).ToList();

    /// <summary>二代生还者。</summary>
    public static IReadOnlyList<VoiceCharacterInfo> L4D2 { get; } = All.Where(c => !c.IsL4D1).ToList();

    /// <summary>语音可能存在的游戏目录（DLC 目录不存在时跳过）。</summary>
    public static readonly IReadOnlyList<string> GameFolders = new[]
    {
        "left4dead2", "left4dead2_dlc1", "left4dead2_dlc2", "left4dead2_dlc3",
    };

    /// <summary>只处理语音文件（不碰其他游戏资源）。</summary>
    public static bool IsVoiceFile(string pathOrName)
    {
        var ext = Path.GetExtension(pathOrName);
        return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    public static VoiceCharacterInfo? Find(VoiceCharacter character) =>
        All.FirstOrDefault(c => c.Character == character);

    public static VoiceCharacterInfo? FindByCodename(string? codename)
    {
        if (string.IsNullOrWhiteSpace(codename)) return null;
        return All.FirstOrDefault(c => string.Equals(c.Codename, codename.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从路径 / 文件夹名 / VPK 内部路径识别角色。</summary>
    public static VoiceCharacterInfo? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Replace('\\', '/').ToLowerInvariant();

        // ① 最明确：sound/player/survivor/voice/<代号>
        const string marker = "sound/player/survivor/voice/";
        int index = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = normalized[(index + marker.Length)..];
            var codename = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var byCodename = FindByCodename(codename);
            if (byCodename != null) return byCodename;
        }

        // ② 代号（单词边界，避免 manager 命中 management 之类）
        foreach (var info in All)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    normalized, $@"(?<![a-z0-9]){info.Codename}(?![a-z0-9])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return info;
            }
        }

        // ③ 英文名 / 中文名
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

/// <summary>某个角色当前的语音状态（界面用）。</summary>
public sealed class VoiceCharacterStatus
{
    public VoiceCharacterInfo Info { get; init; } = VoiceCharacters.All[0];

    /// <summary>游戏里实际存在的语音目录。</summary>
    public List<string> ExistingFolders { get; init; } = new();

    public int CurrentFileCount { get; set; }

    public long CurrentSizeBytes { get; set; }

    /// <summary>最近一次安装记录。</summary>
    public VoiceInstallRecord? Record { get; set; }

    public bool DirectoryExists => ExistingFolders.Count > 0;

    public bool IsModified => Record != null &&
                              string.Equals(Record.Status, VoiceInstallRecord.StatusInstalled, StringComparison.OrdinalIgnoreCase);

    public string StatusText
    {
        get
        {
            if (!DirectoryExists) return "未找到语音目录";
            if (IsModified)
                return $"已替换 Mod：{Record!.ModName}（{Record.FileCount} 个文件，{Record.InstalledAtLocal}）";

            return $"原版状态（{CurrentFileCount} 个语音文件）";
        }
    }

    public string DirectoryText =>
        DirectoryExists
            ? $"{string.Join("、", ExistingFolders)} → {Info.VoiceRelativeDirectory}"
            : $"缺少 {Info.VoiceRelativeDirectory}";

    public string SizeText => CurrentSizeBytes <= 0
        ? "—"
        : CurrentSizeBytes >= 1024 * 1024
            ? $"{CurrentSizeBytes / 1024.0 / 1024.0:0.0} MB"
            : $"{CurrentSizeBytes / 1024.0:0} KB";
}

/// <summary>备份 ZIP 信息。</summary>
public sealed class VoiceBackupInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string CharacterCodename { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string CreatedLocal { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public bool IsValid { get; set; }
    public string? ValidationError { get; set; }

    /// <summary>是否"恢复前"自动生成的备份。</summary>
    public bool IsPreRestore => FileName.Contains("pre-restore", StringComparison.OrdinalIgnoreCase);

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB"
        : $"{SizeBytes / 1024.0:0} KB";

    public string DetailText =>
        $"角色：{CharacterName}　备份时间：{CreatedLocal}　文件数：{FileCount}　大小：{SizeText}" +
        (IsPreRestore ? "　（恢复前自动备份）" : string.Empty);

    public string ValidText => IsValid ? "✓ 完整" : "✗ " + (ValidationError ?? "损坏");
}

/// <summary>安装记录（Data\installed_mods.json）。</summary>
public sealed class VoiceInstallRecord
{
    public const string StatusInstalled = "Installed";
    public const string StatusRestored = "Restored";

    public string CharacterCodename { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string InstalledAtLocal { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public string BackupZip { get; set; } = string.Empty;
    public List<string> TargetFolders { get; set; } = new();

    /// <summary>本次安装新增的文件（相对游戏根目录）；恢复 / 删除 Mod 时据此移除。</summary>
    public List<string> AddedFiles { get; set; } = new();
    public string Status { get; set; } = StatusInstalled;
}

/// <summary>ZIP 备份内的清单（用于记录"新增了哪些文件"，保证能完整还原）。</summary>
public sealed class VoiceBackupManifest
{
    public string CharacterCodename { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string CreatedLocal { get; set; } = string.Empty;
    public List<string> TargetFolders { get; set; } = new();

    /// <summary>备份时已存在的原版文件（相对游戏根目录）。</summary>
    public List<string> SavedFiles { get; set; } = new();

    /// <summary>备份时不存在、安装后会新增的文件（相对游戏根目录）；还原时这些文件会被删除。</summary>
    public List<string> AddedFiles { get; set; } = new();
}

/// <summary>语音 Mod 源（文件夹或 VPK）。</summary>
public sealed class VoiceSourceInfo
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public bool FromVpk { get; set; }
    public VoiceCharacterInfo? Character { get; set; }
    public int TotalVoiceFiles { get; set; }

    /// <summary>待写入文件：目标文件名 + 来源（文件路径或 VPK 内条目）。</summary>
    public List<VoiceSourceFile> Files { get; set; } = new();
}

/// <summary>一个待写入的语音文件。</summary>
public sealed class VoiceSourceFile
{
    public string TargetName { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public VpkEntry? Entry { get; set; }
}

/// <summary>操作结果（含日志行）。</summary>
public sealed record VoiceOperationResult(bool Success, string Message, IReadOnlyList<string> Log)
{
    public string? BackupZip { get; init; }

    public VoiceCharacterInfo? Character { get; init; }

    public static VoiceOperationResult Fail(string message, IReadOnlyList<string>? log = null) =>
        new(false, message, log ?? new List<string>());

    public static VoiceOperationResult Ok(string message, IReadOnlyList<string>? log = null) =>
        new(true, message, log ?? new List<string>());
}

/// <summary>
/// 语音 Mod 管理器（核心安全规则）：
///   · 任何替换之前必须先创建 ZIP 备份，且 ZIP 必须通过完整性验证；
///   · 备份失败 → 立即取消，绝不动原文件；
///   · 恢复之前先把"当前状态"再备份一次；
///   · 只处理 sound\player\survivor\voice\，不碰其他资源；
///   · 全部路径动态获取，不硬编码 Steam 路径。
/// </summary>
public sealed class VoiceManager
{
    public const string RebuildCacheCommand = "snd_rebuildaudiocache";
    public const string QuitCommand = "quit";
    private const string ManifestEntryName = "_voice_manifest.json";

    private readonly string _root;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public VoiceManager(string? dataRoot = null)
    {
        _root = dataRoot ?? AppPaths.Root;
        BackupsDirectory = Path.Combine(_root, "Backups");
        DataDirectory = Path.Combine(_root, "Data");
        LogsDirectory = Path.Combine(_root, "Logs");
        AvatarDirectory = Path.Combine(_root, "Avatars");
        TempDirectory = Path.Combine(_root, "VoiceTemp");
    }

    public string Root => _root;
    public string BackupsDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string AvatarDirectory { get; }
    public string TempDirectory { get; }
    public string RecordsFile => Path.Combine(DataDirectory, "installed_mods.json");

    // ------------------------------------------------------------------ 游戏路径

    /// <summary>自动检测游戏根目录（含 left4dead2.exe 的那一层）。</summary>
    public static string? FindGameRoot(SteamPaths paths)
    {
        foreach (var directory in EnumerateCandidates(paths))
        {
            if (directory == null) continue;
            if (LooksLikeGameRoot(directory)) return Path.GetFullPath(directory);
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidates(SteamPaths paths)
    {
        foreach (var gameDirectory in paths.GameDirectories)
        {
            yield return gameDirectory;
            yield return Path.Combine(gameDirectory, "Left 4 Dead 2");
        }

        foreach (var addon in paths.AddonDirectories)
        {
            string? root = null;
            try
            {
                root = Directory.GetParent(addon)?.Parent?.FullName;
            }
            catch
            {
                // 忽略非法路径
            }

            if (!string.IsNullOrEmpty(root)) yield return root;
        }

        foreach (var library in paths.Libraries)
            yield return Path.Combine(library, "common", "Left 4 Dead 2");
    }

    private static bool LooksLikeGameRoot(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "left4dead2.exe"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验用户手动选择的路径（可以是 left4dead2.exe，也可以是游戏目录）。
    /// 返回游戏根目录；失败时给出中文错误。
    /// </summary>
    public static string? ValidateGameRoot(string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "未找到有效的 Left 4 Dead 2 安装目录。";
            return null;
        }

        var candidate = path.Trim().Trim('"');

        try
        {
            if (File.Exists(candidate))
            {
                if (!Path.GetFileName(candidate).Equals("left4dead2.exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = "请选择 left4dead2.exe，或直接选择 Left 4 Dead 2 游戏目录。";
                    return null;
                }

                candidate = Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? candidate;
            }

            if (!Directory.Exists(candidate))
            {
                error = "未找到有效的 Left 4 Dead 2 安装目录。";
                return null;
            }

            var root = Path.GetFullPath(candidate);

            if (!File.Exists(Path.Combine(root, "left4dead2.exe")))
            {
                error = "该目录里没有 left4dead2.exe：未找到有效的 Left 4 Dead 2 安装目录。";
                return null;
            }

            if (!Directory.Exists(Path.Combine(root, "left4dead2", "sound")))
            {
                error = "找到了 left4dead2.exe，但缺少 left4dead2\\sound 目录：游戏文件可能不完整。";
                return null;
            }

            return root;
        }
        catch (Exception ex)
        {
            error = "路径无效：" + ex.Message;
            return null;
        }
    }

    /// <summary>游戏根目录下存在该角色语音目录的目录列表（绝对路径）。</summary>
    public static List<string> FindVoiceDirectories(string gameRoot, VoiceCharacterInfo info)
    {
        var list = new List<string>();

        foreach (var folder in VoiceCharacters.GameFolders)
        {
            var directory = Path.Combine(gameRoot, folder, info.VoiceRelativeDirectory);
            try
            {
                if (Directory.Exists(directory)) list.Add(directory);
            }
            catch
            {
                // 目录不存在 → 跳过，不报致命错误
            }
        }

        return list;
    }

    /// <summary>该角色在当前游戏里的状态。</summary>
    public VoiceCharacterStatus GetStatus(string? gameRoot, VoiceCharacterInfo info)
    {
        var status = new VoiceCharacterStatus { Info = info };
        status.Record = LoadRecords().LastOrDefault(r =>
            string.Equals(r.CharacterCodename, info.Codename, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(gameRoot)) return status;

        foreach (var directory in FindVoiceDirectories(gameRoot, info))
        {
            status.ExistingFolders.Add(FolderNameOf(gameRoot, directory));

            foreach (var file in SafeEnumerate(directory))
            {
                if (!VoiceCharacters.IsVoiceFile(file)) continue;
                status.CurrentFileCount++;

                try
                {
                    status.CurrentSizeBytes += new FileInfo(file).Length;
                }
                catch
                {
                    // 忽略单个文件的大小读取失败
                }
            }
        }

        return status;
    }

    private static string FolderNameOf(string gameRoot, string voiceDirectory)
    {
        var relative = Path.GetRelativePath(gameRoot, voiceDirectory);
        return relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? relative;
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举语音目录失败 {directory}：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ 扫描源

    /// <summary>扫描 Mod 源：文件夹或 VPK，识别角色并列出待写入文件。</summary>
    public VoiceSourceInfo ScanSource(string? path)
    {
        var result = new VoiceSourceInfo { SourcePath = path ?? string.Empty };

        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            result.Error = "找不到该文件夹或文件。";
            return result;
        }

        try
        {
            if (File.Exists(path) && Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                return ScanVpk(result);
            }

            if (File.Exists(path))
            {
                // 单个语音文件
                if (!VoiceCharacters.IsVoiceFile(path))
                {
                    result.Error = "只支持 .wav / .mp3 / .ogg 语音文件，或包含语音的文件夹 / .vpk。";
                    return result;
                }

                result.Files.Add(new VoiceSourceFile { TargetName = Path.GetFileName(path), SourcePath = path });
                result.TotalVoiceFiles = 1;
                result.Character = VoiceCharacters.Detect(path);
                result.Success = true;
                return result;
            }

            var files = SafeEnumerate(path).Where(VoiceCharacters.IsVoiceFile).ToList();
            if (files.Count == 0)
            {
                result.Error = "该文件夹里没有找到语音文件（.wav / .mp3 / .ogg）。";
                return result;
            }

            foreach (var file in files)
            {
                result.Files.Add(new VoiceSourceFile
                {
                    TargetName = BuildTargetName(path, file),
                    SourcePath = file,
                });
            }

            result.TotalVoiceFiles = files.Count;

            // 角色识别：先文件夹名，再内部路径
            result.Character = VoiceCharacters.Detect(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
            if (result.Character == null)
            {
                foreach (var file in files)
                {
                    var hit = VoiceCharacters.Detect(Path.GetRelativePath(path, file));
                    if (hit != null)
                    {
                        result.Character = hit;
                        break;
                    }
                }
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("扫描语音 Mod 失败", ex);
            result.Error = "读取语音 Mod 失败：" + ex.Message;
            return result;
        }
    }

    private VoiceSourceInfo ScanVpk(VoiceSourceInfo result)
    {
        var archive = VpkReader.TryOpen(result.SourcePath, out var error);
        if (archive == null)
        {
            result.Error = "无法读取 VPK：" + (error ?? "未知错误");
            return result;
        }

        using (archive)
        {
            result.FromVpk = true;
            const string marker = "sound/player/survivor/voice/";

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.NormalizedPath;
                if (!VoiceCharacters.IsVoiceFile(normalized)) continue;

                int index = normalized.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) continue;   // 只处理语音相关路径

                var rest = normalized[(index + marker.Length)..];
                var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                result.Character ??= VoiceCharacters.FindByCodename(parts[0]);

                result.Files.Add(new VoiceSourceFile
                {
                    TargetName = parts[^1],
                    Entry = entry,
                });
            }

            result.TotalVoiceFiles = result.Files.Count;

            if (result.Files.Count == 0)
            {
                result.Error = "这个 VPK 里没有 sound\\player\\survivor\\voice\\ 下的语音文件。";
                return result;
            }

            result.Success = true;
            return result;
        }
    }

    /// <summary>取 voice\&lt;代号&gt;\ 之后的文件名；没有该结构时用原文件名。</summary>
    internal static string BuildTargetName(string sourceRoot, string file)
    {
        var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
        int index = relative.IndexOf("/voice/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var rest = relative[(index + "/voice/".Length)..];
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return parts[^1];
        }

        return Path.GetFileName(file);
    }

    /// <summary>从 VPK 条目读取字节。</summary>
    private static byte[]? ReadEntry(VpkArchive? archive, VoiceSourceFile file)
    {
        if (archive == null || file.Entry == null) return null;
        return archive.Read(file.Entry);
    }

    // ------------------------------------------------------------------ 备份

    /// <summary>创建 ZIP 备份并验证；label 用于区分"恢复前"备份。</summary>
    public (VoiceOperationResult Result, VoiceBackupInfo? Backup, VpkArchive? KeepOpen) CreateBackup(
        string gameRoot,
        VoiceCharacterInfo info,
        string? label = null,
        IReadOnlyList<string>? pendingAddedFiles = null,
        IProgress<string>? progress = null)
    {
        var log = new List<string>();
        void Step(string message)
        {
            log.Add(message);
            Log.Info($"[语音备份] {message}");
            progress?.Report(message);
        }

        var targets = FindVoiceDirectories(gameRoot, info);
        if (targets.Count == 0)
        {
            return (VoiceOperationResult.Fail(
                $"没有找到 {info.DisplayName} 的语音目录（{info.VoiceRelativeDirectory}），无法备份，操作已取消。", log), null, null);
        }

        Step($"检测到 {targets.Count} 个语音目录：{string.Join("、", targets.Select(t => FolderNameOf(gameRoot, t)))}");

        var existing = new List<(string GameFolder, string File)>();
        foreach (var directory in targets)
        {
            var folder = FolderNameOf(gameRoot, directory);
            foreach (var file in SafeEnumerate(directory))
            {
                if (VoiceCharacters.IsVoiceFile(file)) existing.Add((folder, file));
            }
        }

        Step($"扫描到 {existing.Count} 个散装原版语音文件");
        if (existing.Count == 0)
        {
            Step("该目录下没有散装原版语音（原版语音打包在游戏 VPK 里属于正常现象），" +
                 "本次备份会记录要新增的文件，恢复时会删除它们。");
        }

        try
        {
            Directory.CreateDirectory(BackupsDirectory);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseName = $"{info.EnglishName}_{stamp}" + (string.IsNullOrWhiteSpace(label) ? string.Empty : $"_{label}");
            var zipPath = Path.Combine(BackupsDirectory, baseName + ".zip");

            // 同一秒内可能连续备份（例如安装前的原版备份 + 恢复前的当前状态备份），自动加序号，绝不覆盖已有备份
            int suffix = 2;
            while (File.Exists(zipPath))
            {
                zipPath = Path.Combine(BackupsDirectory, $"{baseName}_{suffix++}.zip");
            }

            var name = Path.GetFileName(zipPath);

            var manifest = new VoiceBackupManifest
            {
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                CreatedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TargetFolders = targets.Select(t => FolderNameOf(gameRoot, t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                SavedFiles = existing.Select(e => $"{e.GameFolder}/{Path.GetRelativePath(Path.Combine(gameRoot, e.GameFolder), e.File).Replace('\\', '/')}").ToList(),
                AddedFiles = pendingAddedFiles?.ToList() ?? new List<string>(),
            };

            Step($"开始创建 ZIP 备份：{name}");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (gameFolder, file) in existing)
                {
                    var relative = Path.GetRelativePath(Path.Combine(gameRoot, gameFolder), file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, $"{gameFolder}/{relative}", CompressionLevel.Optimal);
                }

                var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifestEntry.Open());
                writer.Write(JsonSerializer.Serialize(manifest, _json));
            }

            Step("ZIP 创建成功，开始完整性验证…");

            var verify = VerifyBackup(zipPath, info);
            if (!verify.Success)
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // 删除失败不影响安全（我们不会继续替换）
                }

                return (VoiceOperationResult.Fail(
                    "原版语音备份失败，为保护游戏文件，本次安装已取消。\r\n\r\n" + verify.Message, log), null, null);
            }

            Step("✓ 原版语音备份成功（ZIP 完整性验证通过）");

            var backup = new VoiceBackupInfo
            {
                FilePath = zipPath,
                FileName = name,
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                CreatedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileCount = existing.Count,
                SizeBytes = new FileInfo(zipPath).Length,
                IsValid = true,
            };

            return (VoiceOperationResult.Ok($"备份成功：{name}（{backup.SizeText}）", log), backup, null);
        }
        catch (Exception ex)
        {
            Log.Error("创建语音备份失败", ex);
            return (VoiceOperationResult.Fail(
                "原版语音备份失败，为保护游戏文件，本次安装已取消。\r\n\r\n原因：" + ex.Message, log), null, null);
        }
    }

    /// <summary>验证 ZIP：存在、非空、可读、含语音文件或清单、路径结构正确。</summary>
    public VoiceOperationResult VerifyBackup(string zipPath, VoiceCharacterInfo? info = null)
    {
        try
        {
            if (!File.Exists(zipPath)) return VoiceOperationResult.Fail("ZIP 文件不存在。");

            var size = new FileInfo(zipPath).Length;
            if (size <= 0) return VoiceOperationResult.Fail("ZIP 文件大小为 0。");

            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count == 0) return VoiceOperationResult.Fail("ZIP 里没有任何内容。");

            int voiceEntries = 0;
            bool hasManifest = false;
            var badPaths = new List<string>();

            foreach (var entry in archive.Entries)
            {
                if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    hasManifest = true;
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!VoiceCharacters.IsVoiceFile(entry.Name)) continue;

                voiceEntries++;
                var normalized = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                if (!normalized.Contains("sound/player/survivor/voice/")) badPaths.Add(entry.FullName);
            }

            if (voiceEntries == 0 && !hasManifest)
                return VoiceOperationResult.Fail("ZIP 里没有语音文件，也没有备份清单。");

            if (badPaths.Count > 0)
                return VoiceOperationResult.Fail("ZIP 内的路径结构不正确：" + string.Join("、", badPaths.Take(3)));

            if (info != null && voiceEntries > 0)
            {
                var marker = $"sound/player/survivor/voice/{info.Codename}/";
                bool matchesCharacter = archive.Entries.Any(e =>
                    e.FullName.Replace('\\', '/').ToLowerInvariant().Contains(marker));
                if (!matchesCharacter)
                {
                    // 不是致命错误（有些 DLC 目录本身就没有该角色散装语音），仅记录
                    Log.Warn($"备份 {Path.GetFileName(zipPath)} 中未发现 {info.Codename} 的语音条目");
                }
            }

            return VoiceOperationResult.Ok($"✓ ZIP 完整性验证通过（{voiceEntries} 个语音文件，{size / 1024.0:0} KB）");
        }
        catch (Exception ex)
        {
            return VoiceOperationResult.Fail("ZIP 无法读取：" + ex.Message);
        }
    }

    /// <summary>列出全部备份（按时间倒序）。</summary>
    public IReadOnlyList<VoiceBackupInfo> ListBackups()
    {
        var list = new List<VoiceBackupInfo>();

        try
        {
            if (!Directory.Exists(BackupsDirectory)) return list;

            foreach (var file in Directory.EnumerateFiles(BackupsDirectory, "*.zip"))
            {
                var item = new VoiceBackupInfo
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                    SizeBytes = new FileInfo(file).Length,
                };

                var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                item.CharacterName = parts.Length > 0 ? parts[0] : "未知";
                var known = VoiceCharacters.All.FirstOrDefault(c =>
                    string.Equals(c.EnglishName, item.CharacterName, StringComparison.OrdinalIgnoreCase));
                item.CharacterCodename = known?.Codename ?? item.CharacterName;

                // 文件名里的时间戳：Ellis_2026-10-05_16-30-25[_pre-restore]
                var tokens = Path.GetFileNameWithoutExtension(file).Split('_');
                if (tokens.Length >= 3)
                {
                    var date = tokens[1];
                    var time = tokens[2];
                    item.CreatedLocal = $"{date} {time.Replace('-', ':')}";
                }
                else
                {
                    item.CreatedLocal = File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss");
                }

                try
                {
                    using var archive = ZipFile.OpenRead(file);
                    item.FileCount = archive.Entries.Count(e => e.Name.Length > 0 && VoiceCharacters.IsVoiceFile(e.Name));
                }
                catch (Exception ex)
                {
                    item.ValidationError = ex.Message;
                }

                var verify = VerifyBackup(file);
                item.IsValid = verify.Success;
                item.ValidationError = verify.Success ? null : verify.Message;
                list.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举备份目录失败：{ex.Message}");
        }

        return list
            .OrderByDescending(b => b.CreatedLocal, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>删除备份（调用方必须先让用户确认）。</summary>
    public VoiceOperationResult DeleteBackup(VoiceBackupInfo backup)
    {
        try
        {
            if (!File.Exists(backup.FilePath)) return VoiceOperationResult.Fail("备份文件已经不存在。");
            File.Delete(backup.FilePath);
            Log.Info($"[语音备份] 已删除备份 {backup.FileName}");
            return VoiceOperationResult.Ok($"已删除备份：{backup.FileName}");
        }
        catch (Exception ex)
        {
            return VoiceOperationResult.Fail("删除备份失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 安装

    /// <summary>
    /// 安装语音 Mod：备份 → 验证 → 准备 → 替换 → 验证 → 记录。
    /// 备份失败时绝不修改任何游戏文件。
    /// </summary>
    public VoiceOperationResult Install(
        string gameRoot,
        VoiceSourceInfo source,
        VoiceCharacterInfo info,
        string modName,
        IProgress<string>? progress = null)
    {
        var log = new List<string>();
        void Step(string message)
        {
            log.Add(message);
            Log.Info($"[语音安装] {message}");
            progress?.Report(message);
        }

        if (!source.Success || source.Files.Count == 0)
            return VoiceOperationResult.Fail(source.Error ?? "语音 Mod 内容为空。", log);

        // VPK 语音包：作为 addon 放进 left4dead2\addons（L4D2 加载语音 addon 的标准方式），
        // 不覆盖、不修改游戏内的语音文件；只有散装文件夹才走"备份后覆盖语音目录"的流程。
        if (source.FromVpk)
            return InstallVpkAddon(gameRoot, source, info, modName, progress, log);

        Step($"检测 {info.DisplayName} 语音目录…");
        var targets = FindVoiceDirectories(gameRoot, info);
        if (targets.Count == 0)
        {
            return VoiceOperationResult.Fail(
                $"语音目录不存在：游戏里没有 {info.VoiceRelativeDirectory}（{string.Join("、", VoiceCharacters.GameFolders)}）。\r\n" +
                "请确认游戏文件完整，或先用游戏启动一次。", log);
        }

        Step($"找到 {targets.Count} 个语音目录：{string.Join("、", targets.Select(t => FolderNameOf(gameRoot, t)))}");

        // 每个语音目录里已存在的文件名（按目录分别统计，才能准确记录"新增的文件"）
        var existingByFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in targets)
        {
            var folder = FolderNameOf(gameRoot, directory);
            if (!existingByFolder.TryGetValue(folder, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                existingByFolder[folder] = set;
            }

            foreach (var file in SafeEnumerate(directory))
            {
                if (!VoiceCharacters.IsVoiceFile(file)) continue;
                set.Add(Path.GetFileName(file));
                existingNames.Add(Path.GetFileName(file));
            }
        }

        // 将要新增的文件（相对游戏根目录）：安装记录与备份清单都会保存，恢复 / 删除 Mod 时据此删除
        var pending = new List<string>();
        foreach (var directory in targets)
        {
            var folder = FolderNameOf(gameRoot, directory);
            var set = existingByFolder[folder];

            foreach (var file in source.Files)
            {
                if (set.Contains(file.TargetName)) continue;
                pending.Add($"{folder}/sound/player/survivor/voice/{info.Codename}/{file.TargetName}");
            }
        }

        pending = pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Step($"目标目录现有 {existingNames.Count} 个语音文件，本次将写入 {source.Files.Count} 个文件");
        var (backupResult, backup, _) = CreateBackup(gameRoot, info, label: null, pending, progress);
        log.AddRange(backupResult.Log);

        if (!backupResult.Success || backup == null)
        {
            // 关键安全规则：备份失败绝不继续
            return VoiceOperationResult.Fail(backupResult.Message, log);
        }

        var tempDirectory = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        VpkArchive? archive = null;

        try
        {
            // ② 准备：落到临时目录并验证
            Directory.CreateDirectory(tempDirectory);
            if (source.FromVpk) archive = VpkReader.TryOpen(source.SourcePath, out _);

            foreach (var file in source.Files)
            {
                var destination = Path.Combine(tempDirectory, file.TargetName);

                if (file.SourcePath != null)
                {
                    VoiceFileHelper.DeleteThenCopy(file.SourcePath, destination);
                }
                else
                {
                    var data = ReadEntry(archive, file);
                    if (data == null || data.Length == 0)
                    {
                        return VoiceOperationResult.Fail($"Mod 文件损坏或无法读取：{file.TargetName}（已取消，未修改游戏文件）", log);
                    }

                    File.WriteAllBytes(destination, data);
                }

                if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                    return VoiceOperationResult.Fail($"准备 Mod 文件失败：{file.TargetName}（已取消，未修改游戏文件）", log);
            }

            Step($"已准备 {source.Files.Count} 个 Mod 文件");

            // ③ 替换
            var written = new List<string>();
            foreach (var directory in targets)
            {
                foreach (var file in source.Files)
                {
                    var destination = Path.Combine(directory, file.TargetName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Path.Combine(tempDirectory, file.TargetName), destination, overwrite: true);
                    written.Add(destination);
                }
            }

            Step($"已替换 {written.Count} 个文件（{targets.Count} 个目录）");

            // ④ 验证替换结果
            var missing = written.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                Step("验证失败，正在尝试用备份恢复…");
                var rollback = RestoreFromZip(gameRoot, backup.FilePath, log);
                return VoiceOperationResult.Fail(
                    $"替换结果验证失败（{missing.Count} 个文件缺失）。已尝试恢复：{rollback}", log);
            }

            Step("✓ 替换结果验证通过");

            // ⑤ 记录
            var record = new VoiceInstallRecord
            {
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                ModName = modName,
                InstalledAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileCount = written.Count,
                BackupZip = backup.FilePath,
                TargetFolders = targets.Select(t => FolderNameOf(gameRoot, t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AddedFiles = pending,
                Status = VoiceInstallRecord.StatusInstalled,
            };

            SaveRecord(record);
            Step("已写入安装记录（Data\\installed_mods.json）");
            Step("安装完成");
            Step($"请使用 {RebuildCacheCommand} 重建声音缓存");

            return new VoiceOperationResult(true,
                $"已安装 {info.DisplayName} 的语音 Mod（{written.Count} 个文件）", log)
            {
                BackupZip = backup.FilePath,
                Character = info,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return VoiceOperationResult.Fail(
                "没有权限写入游戏目录（Steam 若在 Program Files，请以管理员身份运行本程序）。\r\n" + ex.Message, log);
        }
        catch (IOException ex)
        {
            return VoiceOperationResult.Fail(
                "文件被占用或磁盘空间不足：\r\n" + ex.Message + "\r\n原版语音已备份，可用「恢复原版」还原。", log);
        }
        catch (Exception ex)
        {
            Log.Error("语音安装失败", ex);
            return VoiceOperationResult.Fail("安装失败：" + ex.Message, log);
        }
        finally
        {
            archive?.Dispose();

            try
            {
                if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败无所谓
            }
        }
    }

    // ------------------------------------------------------------------ 安装（VPK → addons）

    /// <summary>
    /// 把 VPK 语音包放进 &lt;游戏&gt;\left4dead2\addons。
    /// 这是 L4D2 加载语音 addon 的标准方式：不修改游戏内的语音文件，删除 = 移除这个 VPK。
    /// 如果 addons 里已有同名文件，会先把它打包备份到 Backups 目录再覆盖。
    /// </summary>
    private VoiceOperationResult InstallVpkAddon(
        string gameRoot,
        VoiceSourceInfo source,
        VoiceCharacterInfo info,
        string modName,
        IProgress<string>? progress,
        List<string> log)
    {
        void Step(string message)
        {
            log.Add(message);
            Log.Info($"[语音安装] {message}");
            progress?.Report(message);
        }

        try
        {
            var addonsDirectory = Path.Combine(gameRoot, "left4dead2", "addons");
            if (!Directory.Exists(addonsDirectory))
            {
                return VoiceOperationResult.Fail(
                    $"找不到 addons 目录：{addonsDirectory}\r\n请确认游戏文件完整（或先用 Steam 启动一次游戏）。", log);
            }

            var fileName = SanitizeFileName(
                Path.GetFileName(modName).EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(modName)
                    : Path.GetFileName(modName) + ".vpk");

            var destination = Path.Combine(addonsDirectory, fileName);
            var relativePath = $"left4dead2/addons/{fileName}";

            Step($"目标：{destination}");

            string backupZip = string.Empty;
            if (File.Exists(destination))
            {
                Step("addons 目录里已有同名 VPK，先备份它…");
                backupZip = BackupAddonFile(destination, info, fileName, log) ?? string.Empty;

                if (string.IsNullOrEmpty(backupZip))
                {
                    return VoiceOperationResult.Fail(
                        "同名 addons 文件备份失败，为保护游戏文件，本次安装已取消。", log);
                }
            }

            VoiceFileHelper.DeleteThenCopy(source.SourcePath, destination);

            var size = new FileInfo(destination).Length;
            Step($"✓ 已放入 addons：{fileName}（{size / 1024.0 / 1024.0:0.0} MB，含 {source.TotalVoiceFiles} 个语音文件）");
            Step("VPK 作为 addon 由游戏加载，没有修改任何游戏内语音文件");

            SaveRecord(new VoiceInstallRecord
            {
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                ModName = fileName,
                InstalledAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileCount = source.TotalVoiceFiles,
                BackupZip = backupZip,
                TargetFolders = new List<string> { "left4dead2/addons" },
                AddedFiles = new List<string> { relativePath },
                Status = VoiceInstallRecord.StatusInstalled,
            });

            Step("已写入安装记录（Data\\installed_mods.json）");
            Step("安装完成");
            Step($"请使用 {RebuildCacheCommand} 重建声音缓存");

            return new VoiceOperationResult(true,
                $"已把 {fileName} 放进 addons（角色 {info.DisplayName}，{source.TotalVoiceFiles} 个语音文件）", log)
            {
                BackupZip = string.IsNullOrEmpty(backupZip) ? null : backupZip,
                Character = info,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return VoiceOperationResult.Fail(
                "没有权限写入 addons 目录（Steam 若在 Program Files，请以管理员身份运行本程序）。\r\n" + ex.Message, log);
        }
        catch (Exception ex)
        {
            Log.Error("安装 VPK 语音包失败", ex);
            return VoiceOperationResult.Fail("安装失败：" + ex.Message, log);
        }
    }

    /// <summary>把 addons 里已存在的同名 VPK 打包备份（带清单，保证验证能通过）。</summary>
    private string? BackupAddonFile(string file, VoiceCharacterInfo info, string fileName, List<string> log)
    {
        try
        {
            Directory.CreateDirectory(BackupsDirectory);

            var baseName = $"{info.EnglishName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_addons";
            var zipPath = Path.Combine(BackupsDirectory, baseName + ".zip");
            int suffix = 2;
            while (File.Exists(zipPath))
            {
                zipPath = Path.Combine(BackupsDirectory, $"{baseName}_{suffix++}.zip");
            }

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(file, $"left4dead2/addons/{fileName}", CompressionLevel.Optimal);

                var manifest = new VoiceBackupManifest
                {
                    CharacterCodename = info.Codename,
                    CharacterName = info.EnglishName,
                    CreatedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TargetFolders = new List<string> { "left4dead2/addons" },
                    SavedFiles = new List<string> { $"left4dead2/addons/{fileName}" },
                    AddedFiles = new List<string>(),
                };

                var entry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(JsonSerializer.Serialize(manifest, _json));
            }

            var verify = VerifyBackup(zipPath);
            if (!verify.Success)
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // 忽略
                }

                log.Add("备份验证未通过：" + verify.Message);
                return null;
            }

            log.Add($"✓ 同名 addons 文件已备份：{Path.GetFileName(zipPath)}");
            return zipPath;
        }
        catch (Exception ex)
        {
            log.Add("备份 addons 文件失败：" + ex.Message);
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    // ------------------------------------------------------------------ 恢复 / 删除

    /// <summary>恢复某个 ZIP 备份；恢复前会把当前状态再备份一次。</summary>
    public VoiceOperationResult Restore(string gameRoot, VoiceBackupInfo backup, IProgress<string>? progress = null)
    {
        var log = new List<string>();
        void Step(string message)
        {
            log.Add(message);
            Log.Info($"[语音恢复] {message}");
            progress?.Report(message);
        }

        Step($"验证备份 ZIP：{backup.FileName}");
        var verify = VerifyBackup(backup.FilePath);
        log.Add(verify.Message);
        if (!verify.Success)
        {
            return VoiceOperationResult.Fail("备份 ZIP 损坏，为保护游戏文件，本次恢复已取消。\r\n" + verify.Message, log);
        }

        Step("✓ ZIP 完整性验证通过");

        var info = VoiceCharacters.FindByCodename(backup.CharacterCodename) ??
                   VoiceCharacters.All.FirstOrDefault(c =>
                       string.Equals(c.EnglishName, backup.CharacterName, StringComparison.OrdinalIgnoreCase));

        if (info == null)
        {
            return VoiceOperationResult.Fail("无法从备份文件名识别角色，已取消恢复。", log);
        }

        // 恢复前先备份当前状态（规格：恢复操作也必须先备份当前状态）
        Step("恢复前先备份当前状态…");
        var (currentResult, currentBackup, _) = CreateBackup(gameRoot, info, label: "pre-restore", null, progress);
        log.AddRange(currentResult.Log);

        if (!currentResult.Success || currentBackup == null)
        {
            return VoiceOperationResult.Fail("当前状态备份失败，为保护游戏文件，本次恢复已取消。", log);
        }

        Step($"✓ 当前状态已备份：{currentBackup.FileName}");

        try
        {
            var installedRecord = LoadRecords().LastOrDefault(r =>
                string.Equals(r.CharacterCodename, info.Codename, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Status, VoiceInstallRecord.StatusInstalled, StringComparison.OrdinalIgnoreCase));

            var (restored, removed) = RestoreFromZip(
                gameRoot, backup.FilePath, log, countOnly: false, extraAddedFiles: installedRecord?.AddedFiles);

            if (restored == 0 && removed == 0)
            {
                return VoiceOperationResult.Fail("该 ZIP 里没有可恢复的语音文件。", log);
            }

            var record = new VoiceInstallRecord
            {
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                ModName = "（已恢复原版）",
                InstalledAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileCount = restored,
                BackupZip = backup.FilePath,
                TargetFolders = new List<string>(),
                Status = VoiceInstallRecord.StatusRestored,
            };
            SaveRecord(record);

            Step($"已恢复 {restored} 个文件" + (removed > 0 ? $"，并移除 {removed} 个替换时新增的文件" : string.Empty));
            Step($"请使用 {RebuildCacheCommand} 重建声音缓存");

            return new VoiceOperationResult(true,
                $"已恢复 {info.DisplayName} 的原版语音（{restored} 个文件写回，{removed} 个新增文件移除）", log)
            {
                BackupZip = currentBackup.FilePath,
                Character = info,
            };
        }
        catch (Exception ex)
        {
            Log.Error("语音恢复失败", ex);
            return VoiceOperationResult.Fail("恢复失败：" + ex.Message + "\r\n当前状态已备份，可再次尝试。", log);
        }
    }

    /// <summary>删除当前语音 Mod：备份当前状态 → 恢复安装时的原版 ZIP。</summary>
    public VoiceOperationResult Uninstall(string gameRoot, VoiceCharacterInfo info, IProgress<string>? progress = null)
    {
        var log = new List<string>();
        var record = LoadRecords().LastOrDefault(r =>
            string.Equals(r.CharacterCodename, info.Codename, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Status, VoiceInstallRecord.StatusInstalled, StringComparison.OrdinalIgnoreCase));

        if (record == null)
        {
            return VoiceOperationResult.Fail(
                $"没有 {info.DisplayName} 的安装记录，无法自动删除。\r\n" +
                "可以改用「备份管理」里的某个原版备份进行恢复。", log);
        }

        // 纯新增（例如 VPK 放进 addons，没有覆盖任何游戏文件）：直接删除新增文件即可
        if (string.IsNullOrWhiteSpace(record.BackupZip) || !File.Exists(record.BackupZip))
        {
            int removedAddons = 0;

            foreach (var relative in record.AddedFiles)
            {
                try
                {
                    var path = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removedAddons++;
                        log.Add($"已删除：{relative}");
                    }
                }
                catch (Exception ex)
                {
                    log.Add($"删除 {relative} 失败：{ex.Message}");
                }
            }

            SaveRecord(new VoiceInstallRecord
            {
                CharacterCodename = info.Codename,
                CharacterName = info.EnglishName,
                ModName = "（已删除 Mod）",
                InstalledAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileCount = 0,
                BackupZip = string.Empty,
                Status = VoiceInstallRecord.StatusRestored,
            });

            return new VoiceOperationResult(true,
                removedAddons > 0
                    ? $"已删除 {info.DisplayName} 的语音 Mod（移除 {removedAddons} 个新增文件，游戏本体未被动过）"
                    : "没有找到需要删除的文件（可能已经手动删掉了）", log);
        }

        var backup = ListBackups().FirstOrDefault(b =>
            string.Equals(Path.GetFullPath(b.FilePath), SafeFullPath(record.BackupZip), StringComparison.OrdinalIgnoreCase));

        if (backup == null)
        {
            return VoiceOperationResult.Fail("找不到安装时创建的原版备份 ZIP，为保护游戏文件已取消删除。", log);
        }

        log.Add($"将先备份当前状态，再恢复安装前的原版备份：{backup.FileName}");
        var result = Restore(gameRoot, backup, progress);
        log.InsertRange(0, result.Log);

        if (!result.Success) return result with { Log = log };

        SaveRecord(new VoiceInstallRecord
        {
            CharacterCodename = info.Codename,
            CharacterName = info.EnglishName,
            ModName = "（已删除 Mod）",
            InstalledAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            FileCount = 0,
            BackupZip = backup.FilePath,
            Status = VoiceInstallRecord.StatusRestored,
        });

        log.Add("已从安装记录中标记为「已删除 Mod」");
        return new VoiceOperationResult(true, $"已删除 {info.DisplayName} 的语音 Mod（原版已还原）", log)
        {
            BackupZip = backup.FilePath,
            Character = info,
        };
    }

    /// <summary>把 ZIP 里的语音文件写回游戏目录；同时按清单删除"安装时新增的文件"。</summary>
    private (int Restored, int Removed) RestoreFromZip(
        string gameRoot,
        string zipPath,
        List<string> log,
        bool countOnly = false,
        IReadOnlyList<string>? extraAddedFiles = null)
    {
        int restored = 0;
        int removed = 0;

        var fullRoot = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        // 先读清单
        VoiceBackupManifest? manifest = null;
        var manifestEntry = archive.GetEntry(ManifestEntryName);
        if (manifestEntry != null)
        {
            try
            {
                using var reader = new StreamReader(manifestEntry.Open());
                manifest = JsonSerializer.Deserialize<VoiceBackupManifest>(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                log.Add("读取备份清单失败（继续按 ZIP 内容恢复）：" + ex.Message);
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!VoiceCharacters.IsVoiceFile(entry.Name)) continue;   // 只恢复语音文件

            var destination = Path.GetFullPath(Path.Combine(gameRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                log.Add("跳过越界条目：" + entry.FullName);
                continue;
            }

            if (!countOnly)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            restored++;
        }

        // 删除安装时新增的文件（它们不在原版里，还原时必须移除）
        var addedFiles = new List<string>();
        if (manifest != null) addedFiles.AddRange(manifest.AddedFiles);
        if (extraAddedFiles != null) addedFiles.AddRange(extraAddedFiles);

        foreach (var relative in addedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            {
                var destination = Path.GetFullPath(Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (File.Exists(destination))
                    {
                        if (!countOnly) File.Delete(destination);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    log.Add($"删除新增文件失败：{relative}（{ex.Message}）");
                }
            }
        }

        return (restored, removed);
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    // ------------------------------------------------------------------ 记录

    public List<VoiceInstallRecord> LoadRecords()
    {
        try
        {
            if (!File.Exists(RecordsFile)) return new List<VoiceInstallRecord>();
            var json = File.ReadAllText(RecordsFile);
            return JsonSerializer.Deserialize<List<VoiceInstallRecord>>(json) ?? new List<VoiceInstallRecord>();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取安装记录失败：{ex.Message}");
            return new List<VoiceInstallRecord>();
        }
    }

    private void SaveRecord(VoiceInstallRecord record)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var records = LoadRecords();
            records.Add(record);
            File.WriteAllText(RecordsFile, JsonSerializer.Serialize(records, _json));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入安装记录失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 头像 / 日志

    /// <summary>自定义角色头像（可选）：Avatars\&lt;code&gt;.png / .jpg / .jpeg / .webp。</summary>
    public string? FindAvatar(VoiceCharacterInfo info)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(AvatarDirectory, info.Codename + extension);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>确保 Logs 目录存在并返回本次语音操作的日志文件路径（按日期）。</summary>
    public string EnsureLogFile()
    {
        Directory.CreateDirectory(LogsDirectory);
        return Path.Combine(LogsDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
    }
}
