using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Steam;
using L4D2ModManager.Core.Services.Vpk;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>扫描选项。</summary>
public sealed class ScanOptions
{
    /// <summary>是否解析 VPK 内部文件清单（冲突检测需要）。</summary>
    public bool BuildFileIndex { get; set; } = true;

    /// <summary>是否从 VPK 内提取缩略图。</summary>
    public bool ExtractThumbnails { get; set; } = true;

    /// <summary>是否递归子目录。</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>是否清理数据库中已不存在的条目。</summary>
    public bool PruneMissing { get; set; } = true;

    public static ScanOptions FromConfig(AppConfig config) => new()
    {
        BuildFileIndex = config.BuildFileIndex,
        ExtractThumbnails = config.ExtractThumbnails,
        Recursive = true,
        PruneMissing = true,
    };
}

public sealed class ScanProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;

    public string Text => Total <= 0 ? "准备中…" : $"({Current}/{Total}) {CurrentFile}";
}

public sealed class ScanResult
{
    public int FilesFound { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Failed { get; set; }
    public int Removed { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<ModItem> Mods { get; set; } = new();

    public string Summary =>
        $"扫描完成：发现 {FilesFound} 个 VPK，新增 {Added}，更新 {Updated}，未变化 {Unchanged}，失败 {Failed}" +
        (Removed > 0 ? $"，移除失效记录 {Removed}" : string.Empty) +
        $"，用时 {Duration.TotalSeconds:0.0} 秒";
}

/// <summary>
/// Mod 扫描器：枚举目录中的 .vpk / .vpk.disabled，
/// 读取 addoninfo.txt、缩略图、内部文件清单，并推断分类。
/// </summary>
public sealed class ModScanner
{
    private readonly ThumbnailService _thumbnails;

    public ModScanner(ThumbnailService thumbnails) => _thumbnails = thumbnails;

    /// <summary>枚举一个目录中的 Mod 文件（去重，跳过外部分卷）。</summary>
    public IEnumerable<string> EnumerateModFiles(string directory, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", option);
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举目录失败: {directory} -> {ex.Message}");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!IsModFile(file)) continue;
            if (!seen.Add(file)) continue;
            yield return file;
        }
    }

    /// <summary>判断是否为 Mod 主文件（.vpk 或 .vpk.disabled，且不是外部分卷）。</summary>
    public static bool IsModFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) return false;

        bool disabled = name.EndsWith(ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        var effective = disabled ? name[..^ModItem.DisabledSuffix.Length] : name;
        if (!effective.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return false;

        // 跳过外部分卷：xxx_000.vpk、xxx_001.vpk …（它们属于 xxx_dir.vpk）
        var stem = effective[..^4];
        int underscore = stem.LastIndexOf('_');
        if (underscore >= 0 && stem.Length - underscore - 1 == 3 &&
            stem.Skip(underscore + 1).All(char.IsDigit))
        {
            var baseName = stem[..underscore];
            var dirPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, baseName + "_dir.vpk");
            return !File.Exists(dirPath);
        }

        return true;
    }

    /// <summary>扫描单个文件。</summary>
    public ModItem ScanFile(string actualPath, ModItem? cached, ScanOptions options)
    {
        var info = new FileInfo(actualPath);
        var logicalPath = ModItem.ToLogicalPath(info.FullName);
        var state = ModItem.StateFromPath(info.FullName);
        var stamp = ModItem.MakeStamp(info.Length, info.LastWriteTimeUtc);
        var workshopId = GuessWorkshopId(info.FullName);

        var item = cached ?? new ModItem();
        bool firstSeen = cached == null || item.AddedUtc == default;

        item.FilePath = logicalPath;
        item.Directory = info.DirectoryName ?? string.Empty;
        item.State = state;
        item.SizeBytes = info.Length;
        item.CreatedUtc = info.CreationTimeUtc;
        item.ModifiedUtc = info.LastWriteTimeUtc;
        if (firstSeen) item.AddedUtc = DateTime.UtcNow;

        bool indexUsable = cached != null && cached.IndexStamp == stamp &&
                           (!options.BuildFileIndex || cached.FileIndex.Count > 0);
        bool metadataUsable = cached != null && cached.IndexStamp == stamp && cached.HasAddonInfo;
        bool thumbnailUsable = cached?.ThumbnailPath != null && File.Exists(cached.ThumbnailPath);

        // 快速路径：文件未变且已有完整缓存
        if (indexUsable && metadataUsable && (thumbnailUsable || !options.ExtractThumbnails))
        {
            item.IndexStamp = stamp;
            item.Error = null;
            if (workshopId != null) item.WorkshopId = workshopId;
            item.Key = ModItem.MakeKey(logicalPath, item.WorkshopId);
            return item;
        }

        var addonInfo = AddonInfo.Empty;
        var fileIndex = indexUsable ? item.FileIndex : new List<string>();
        string? parseError = null;

        var archive = VpkReader.TryOpen(actualPath, out parseError);
        if (archive == null)
        {
            item.Error = parseError ?? "无法解析 VPK";
            item.HasAddonInfo = false;
            item.Key = ModItem.MakeKey(logicalPath, workshopId);
            item.DisplayName = Path.GetFileNameWithoutExtension(logicalPath);
            item.Category = CategoryClassifier.Classify(item.DisplayName, null, null, null);
            item.IndexStamp = stamp;
            return item;
        }

        try
        {
            item.IndexStamp = stamp;

            if (!metadataUsable)
            {
                addonInfo = AddonInfo.FromArchive(archive);
                item.HasAddonInfo = addonInfo.Found;
                item.Tags = addonInfo.TagText;

                if (addonInfo.Found)
                {
                    item.DisplayName = addonInfo.Title ?? string.Empty;
                    item.Author = addonInfo.Author ?? string.Empty;
                    item.Description = addonInfo.Description ?? string.Empty;
                }

                // addoninfo 里可能带创意工坊 ID
                if (string.IsNullOrWhiteSpace(item.WorkshopId) && !string.IsNullOrWhiteSpace(addonInfo.SteamId) &&
                    addonInfo.SteamId.All(char.IsDigit) && addonInfo.SteamId.Length >= 5)
                {
                    item.WorkshopId = addonInfo.SteamId;
                }

                item.Error = null;
            }

            if (options.BuildFileIndex && !indexUsable)
            {
                // 同时记录 CRC32：冲突检测据此区分"同一份内容的重复"与"真正的覆盖冲突"
                fileIndex = archive.Entries.Select(e => ModFileEntry.Format(e.FullPath, e.Crc)).ToList();
                item.FileIndex = fileIndex;
            }

            if (options.ExtractThumbnails && !thumbnailUsable)
            {
                var thumb = _thumbnails.ExtractFromVpk(item, archive);
                if (thumb != null) item.ThumbnailPath = thumb;
            }
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            Log.Warn($"解析 VPK 失败: {actualPath} -> {ex.Message}");
        }
        finally
        {
            archive.Dispose();
        }

        var baseName = Path.GetFileNameWithoutExtension(logicalPath);

        // 创意工坊下载到 addons 目录的 Mod 常直接以数字 ID 命名（例如 123456789.vpk）
        if (string.IsNullOrWhiteSpace(item.WorkshopId) &&
            baseName.Length is >= 5 and <= 20 && baseName.All(char.IsDigit))
        {
            item.WorkshopId = baseName;
        }

        if (string.IsNullOrWhiteSpace(item.DisplayName))
        {
            // 没有 addoninfo：退回文件名；工坊文件通常是数字 ID
            item.DisplayName = !string.IsNullOrWhiteSpace(item.WorkshopId) && baseName.All(char.IsDigit)
                ? $"创意工坊 Mod #{item.WorkshopId}"
                : baseName;
        }

        item.Category = CategoryClassifier.Classify(item.DisplayName, item.FileIndex, item.Tags, item.Description);
        item.WorkshopId = item.WorkshopId ?? workshopId;
        item.Key = ModItem.MakeKey(logicalPath, item.WorkshopId);
        return item;
    }

    /// <summary>扫描多个目录并写入数据库。</summary>
    public ScanResult Scan(
        IEnumerable<string> directories,
        ModRepository repository,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var result = new ScanResult();
        var dirs = directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var files = new List<string>();
        foreach (var dir in dirs)
            files.AddRange(EnumerateModFiles(dir, options.Recursive));

        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.FilesFound = files.Count;

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new ScanProgress { Current = index, Total = files.Count, CurrentFile = Path.GetFileName(file) });

            try
            {
                var logical = ModItem.ToLogicalPath(file);
                var existing = repository.FindByPath(logical) ?? repository.FindByPath(file);
                var previousStamp = existing?.IndexStamp;
                var previousName = existing?.DisplayName;
                var item = ScanFile(file, existing, options);

                var isNew = existing == null;
                repository.Upsert(item);
                seenKeys.Add(item.Key);

                if (isNew) result.Added++;
                else if (previousStamp == item.IndexStamp && previousName == item.DisplayName) result.Unchanged++;
                else result.Updated++;

                result.Mods.Add(item);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                Log.Error($"扫描文件失败: {file}", ex);
            }
        }

        if (options.PruneMissing)
        {
            var stale = repository.Mods
                .Where(m => !File.Exists(m.ActualPath))
                .Where(m => dirs.Any(d => IsUnder(m.FilePath, d)))
                .Where(m => !seenKeys.Contains(m.Key))
                .ToList();

            foreach (var item in stale)
            {
                repository.Remove(item.Key);
                result.Removed++;
                Log.Info($"移除失效记录: {item.FilePath}");
            }
        }

        result.Duration = DateTime.UtcNow - started;
        Log.Info(result.Summary);
        return result;
    }

    /// <summary>从路径推断创意工坊 ID（steamapps\workshop\content\550\&lt;id&gt;\xxx.vpk）。</summary>
    public static string? GuessWorkshopId(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory))
            {
                var name = Path.GetFileName(directory);
                var parent = Path.GetDirectoryName(directory);
                var parentName = string.IsNullOrEmpty(parent) ? string.Empty : Path.GetFileName(parent);

                if (string.Equals(parentName, SteamLibraryLocator.L4D2AppId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    name.Length >= 5 && name.All(char.IsDigit))
                {
                    return name;
                }

                // 兼容 550 目录本身被选为 Mod 目录的情况
                if (string.Equals(name, SteamLibraryLocator.L4D2AppId.ToString(), StringComparison.OrdinalIgnoreCase))
                    return null;

                directory = parent;
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private static bool IsUnder(string path, string directory)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
