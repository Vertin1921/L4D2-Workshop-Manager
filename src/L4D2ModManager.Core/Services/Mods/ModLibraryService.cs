using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Downloads;
using L4D2ModManager.Core.Services.Steam;
using L4D2ModManager.Core.Services.Vpk;
using L4D2ModManager.Core.Services.Workshop;

namespace L4D2ModManager.Core.Services.Mods;

public sealed class DeleteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public bool ThumbnailDeleted { get; init; }
    public bool RecordDeleted { get; init; }

    public string Message => Success
        ? $"已删除 {Path.GetFileName(FilePath)}" + (ThumbnailDeleted ? "（含缓存图片）" : string.Empty)
        : $"删除失败：{Error}";
}

/// <summary>
/// Mod 库服务：应用程序的核心门面（Facade）。
/// UI 只需要与本类交互：扫描、启停、删除、冲突检测、方案、下载、缩略图。
/// </summary>
public sealed class ModLibraryService : IDisposable
{
    private readonly Lazy<SteamPaths> _steamPaths;
    private bool _disposed;

    public ModLibraryService(string? configPath = null, string? databasePath = null)
    {
        ConfigPath = configPath ?? AppPaths.ConfigFile;

        Config = JsonStore.Load<AppConfig>(ConfigPath) ?? AppConfig.CreateDefault();
        Config.Normalize();

        Workshop = new SteamWorkshopClient(() => Config.SteamWebApiKey);
        Repository = new ModRepository(databasePath);
        Thumbnails = new ThumbnailService(Workshop);
        Scanner = new ModScanner(Thumbnails);
        StateService = new ModStateService();
        ConflictDetector = new ConflictDetector();
        ProfileService = new ProfileService(StateService);

        _steamPaths = new Lazy<SteamPaths>(SteamLibraryLocator.Detect);
        Downloads = new DownloadManager(Workshop, () => Config, () => SteamPaths);

        StateService.StateChanged += (item, oldState, newState) =>
        {
            Repository.MarkDirty();
            StatusChanged?.Invoke(this, $"{(newState == ModState.Enabled ? "启用" : "禁用")}：{item.DisplayName}");
        };

        Downloads.DownloadCompleted += (task, path) => DownloadFinished?.Invoke(this, (task, path));
    }

    public string ConfigPath { get; }

    public AppConfig Config { get; private set; }

    public ModRepository Repository { get; }

    public ModScanner Scanner { get; }

    public ModStateService StateService { get; }

    public ConflictDetector ConflictDetector { get; }

    public ProfileService ProfileService { get; }

    public ThumbnailService Thumbnails { get; }

    public SteamWorkshopClient Workshop { get; }

    public DownloadManager Downloads { get; }

    public SteamPaths SteamPaths => _steamPaths.Value;

    public ConflictReport? LastConflictReport { get; private set; }

    public IReadOnlyList<ModItem> Mods => Repository.Mods;

    public event EventHandler? ModsChanged;

    public event EventHandler<ConflictReport>? ConflictsUpdated;

    public event EventHandler<string>? StatusChanged;

    /// <summary>下载完成：需要上层提示用户并刷新列表。</summary>
    public event EventHandler<(DownloadTask Task, string FilePath)>? DownloadFinished;

    /// <summary>载入配置 + 数据库，并（按配置）自动检测 Steam 路径。</summary>
    public void Initialize()
    {
        AppPaths.EnsureCreated();
        Repository.Load();

        if (Config.AutoDetectSteamPaths)
        {
            try
            {
                ApplyDetectedSteamPaths();
            }
            catch (Exception ex)
            {
                Log.Warn($"自动检测 Steam 路径失败: {ex.Message}");
            }
        }

        StatusChanged?.Invoke(this, $"已加载 {Repository.Count} 个 Mod 记录");
    }

    /// <summary>把自动探测到的目录补充进配置（不覆盖用户手动添加的目录）。</summary>
    public void ApplyDetectedSteamPaths()
    {
        var paths = SteamPaths;
        Config.SteamRootPath ??= paths.SteamRoot;
        Config.SteamCmdPath ??= paths.SteamCmdPath;

        bool changed = false;
        foreach (var directory in paths.AddonDirectories.Concat(paths.WorkshopDirectories))
        {
            if (Config.ModDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)) continue;
            Config.ModDirectories.Add(directory);
            changed = true;
        }

        if (changed)
        {
            Config.Normalize();
            SaveConfig();
            Log.Info($"已自动添加 {paths.AddonDirectories.Count + paths.WorkshopDirectories.Count} 个 Steam 目录");
        }
    }

    public void SaveConfig()
    {
        Config.Normalize();
        JsonStore.Save(ConfigPath, Config);
    }

    public void SaveDatabase(bool force = false) => Repository.Save(force);

    /// <summary>一键重新扫描全部 Mod 目录。</summary>
    public async Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directories = Config.ModDirectories.ToList();
        Log.Info($"开始扫描：{string.Join(" | ", directories)}");

        var options = ScanOptions.FromConfig(Config);
        var result = await Task.Run(() => Scanner.Scan(directories, Repository, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(true);

        SaveDatabase();

        if (Config.DetectConflictsAfterScan)
        {
            try
            {
                await DetectConflictsAsync(null, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 用户取消
            }
        }

        ModsChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, result.Summary);
        return result;
    }

    /// <summary>批量启用 / 禁用。</summary>
    public StateChangeResult SetState(IEnumerable<ModItem> items, ModState target)
    {
        var result = StateService.Apply(items, target);
        SaveDatabase();
        ModsChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, result.Summary);
        return result;
    }

    /// <summary>启用全部（可指定子集）。</summary>
    public StateChangeResult EnableAll(IEnumerable<ModItem>? items = null) => SetState(items ?? Mods, ModState.Enabled);

    /// <summary>禁用全部（可指定子集）。</summary>
    public StateChangeResult DisableAll(IEnumerable<ModItem>? items = null) => SetState(items ?? Mods, ModState.Disabled);

    /// <summary>启用 / 禁用单个 Mod。</summary>
    public bool Toggle(ModItem item, out string? error)
    {
        var ok = StateService.Toggle(item, out error);
        if (ok)
        {
            SaveDatabase();
            ModsChanged?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    public bool SetState(ModItem item, ModState target, out string? error)
    {
        var ok = StateService.TrySetState(item, target, out error);
        if (ok)
        {
            SaveDatabase();
            ModsChanged?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    /// <summary>删除 Mod：VPK 文件 + 缩略图缓存 + 数据库记录。</summary>
    public DeleteResult DeleteMod(ModItem item, bool deleteThumbnail = true)
    {
        try
        {
            var deletedFiles = new List<string>();

            foreach (var candidate in new[] { item.FilePath, item.FilePath + ModItem.DisabledSuffix })
            {
                if (!File.Exists(candidate)) continue;
                File.Delete(candidate);
                deletedFiles.Add(candidate);
            }

            if (deletedFiles.Count == 0 && !File.Exists(item.ActualPath))
                Log.Warn($"删除时未找到文件（仅清理记录）：{item.FilePath}");

            bool thumbnailDeleted = false;
            if (deleteThumbnail)
            {
                Thumbnails.DeleteFor(item);
                thumbnailDeleted = true;
            }

            var recordDeleted = Repository.Remove(item.Key);
            SaveDatabase();
            ModsChanged?.Invoke(this, EventArgs.Empty);

            Log.Info($"已删除 Mod：{item.DisplayName}（文件 {deletedFiles.Count} 个）");
            StatusChanged?.Invoke(this, $"已删除：{item.DisplayName}");

            return new DeleteResult
            {
                Success = true,
                FilePath = item.FilePath,
                ThumbnailDeleted = thumbnailDeleted,
                RecordDeleted = recordDeleted,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"删除 Mod 失败: {item.FilePath}", ex);
            return new DeleteResult { Success = false, Error = ex.Message, FilePath = item.FilePath };
        }
    }

    /// <summary>冲突检测。</summary>
    public async Task<ConflictReport> DetectConflictsAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Config.BuildFileIndex)
        {
            Log.Warn("冲突检测需要 VPK 内部文件清单，当前设置未启用「解析 VPK 内部文件清单」；将只比较已有索引的 Mod");
        }

        var mods = Mods.ToList();
        var report = await Task.Run(
                () => ConflictDetector.Detect(mods, includeDisabled: true, Config.ConflictIgnorePatterns, progress, cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);

        LastConflictReport = report;
        SaveDatabase();
        ConflictsUpdated?.Invoke(this, report);
        StatusChanged?.Invoke(this, report.SummaryText);
        return report;
    }

    /// <summary>应用配置方案。</summary>
    public ApplyProfileResult ApplyProfile(ModProfile profile, IProgress<string>? progress = null)
    {
        var result = ProfileService.Apply(profile, Mods.ToList(), LastConflictReport, progress);
        SaveDatabase();
        ModsChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"[{profile.Name}] {result.Summary}");
        return result;
    }

    /// <summary>把当前启停状态保存为自定义方案。</summary>
    public ModProfile SaveCurrentAsProfile(string name, string description)
    {
        var profile = ProfileService.Capture(name, description, Mods);
        Config.Profiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        Config.Profiles.Add(profile);
        SaveConfig();
        StatusChanged?.Invoke(this, $"已保存方案：{name}");
        return profile;
    }

    public bool DeleteProfile(ModProfile profile)
    {
        if (profile.IsBuiltIn) return false;
        var removed = Config.Profiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) SaveConfig();
        return removed;
    }

    /// <summary>目录管理。</summary>
    public bool AddDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Trim().TrimEnd('\\', '/');
        if (Directory.Exists(normalized) is false)
        {
            StatusChanged?.Invoke(this, $"目录不存在：{normalized}");
            return false;
        }

        if (Config.ModDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusChanged?.Invoke(this, "该目录已存在");
            return false;
        }

        Config.ModDirectories.Add(normalized);
        Config.DownloadDirectory ??= normalized;
        SaveConfig();
        StatusChanged?.Invoke(this, $"已添加目录：{normalized}");
        return true;
    }

    public bool RemoveDirectory(string path)
    {
        var normalized = path.Trim().TrimEnd('\\', '/');
        var removed = Config.ModDirectories.RemoveAll(p => string.Equals(p.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return false;

        if (string.Equals(Config.DownloadDirectory?.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
            Config.DownloadDirectory = Config.ModDirectories.FirstOrDefault();

        SaveConfig();
        StatusChanged?.Invoke(this, $"已移除目录：{normalized}");
        return true;
    }

    /// <summary>下载目标目录（默认第一个 addons 目录）。</summary>
    public string ResolveDownloadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Config.DownloadDirectory) && Directory.Exists(Config.DownloadDirectory))
            return Config.DownloadDirectory!;

        var addons = SteamPaths.AddonDirectories.FirstOrDefault(Directory.Exists);
        if (addons != null) return addons;

        var existing = Config.ModDirectories.FirstOrDefault(Directory.Exists);
        if (existing != null) return existing;

        return Path.Combine(AppPaths.Root, "Addons");
    }

    /// <summary>拖放安装：把 .vpk 复制到 Mod 目录并立即入库。</summary>
    public async Task<(ModItem? Item, string Message)> InstallVpkAsync(
        string sourcePath,
        string? targetDirectory = null,
        bool move = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return (null, $"文件不存在：{sourcePath}");

            var name = Path.GetFileName(sourcePath);
            if (!name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"只能安装 .vpk 文件：{name}");
            }

            if (!VpkReader.LooksLikeVpk(sourcePath))
            {
                Log.Warn($"文件头不是 VPK 签名，仍尝试安装：{sourcePath}");
            }

            var directory = string.IsNullOrWhiteSpace(targetDirectory) ? ResolveDownloadDirectory() : targetDirectory!;
            Directory.CreateDirectory(directory);

            var destination = Path.Combine(directory, name);
            if (File.Exists(destination))
            {
                var existingInfo = new FileInfo(destination);
                var sourceInfo = new FileInfo(sourcePath);
                if (existingInfo.Length == sourceInfo.Length)
                    return (Repository.FindByPath(destination), $"目标目录已存在同名同大小文件：{name}");
                destination = DownloadFileHelper.UniquePath(directory, name);
            }

            if (move)
                File.Move(sourcePath, destination);
            else
                File.Copy(sourcePath, destination, overwrite: false);

            Log.Info($"安装完成：{sourcePath} -> {destination}");

            var options = ScanOptions.FromConfig(Config);
            var item = await Task.Run(
                () => Scanner.ScanFile(destination, Repository.FindByPath(destination), options),
                cancellationToken).ConfigureAwait(true);

            Repository.Upsert(item);
            SaveDatabase();
            ModsChanged?.Invoke(this, EventArgs.Empty);

            var message = $"已安装：{item.DisplayName}（{item.SizeText}）";
            StatusChanged?.Invoke(this, message);
            return (item, message);
        }
        catch (Exception ex)
        {
            Log.Error($"安装 VPK 失败: {sourcePath}", ex);
            return (null, $"安装失败：{ex.Message}");
        }
    }

    /// <summary>解析创意工坊链接并加入下载队列。</summary>
    public async Task<(DownloadTask? Task, string Message)> DownloadFromLinkAsync(
        string link,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var id = SteamWorkshopClient.ParseWorkshopId(link);
        if (string.IsNullOrWhiteSpace(id))
            return (null, "无法从输入中解析出创意工坊 ID，请检查链接是否正确");

        var result = await Workshop.GetDetailsAsync(new[] { id }, cancellationToken).ConfigureAwait(true);
        var info = result.First;

        if (info == null)
            return (null, result.Error ?? "获取创意工坊信息失败");

        if (info.Available == false)
            return (null, info.Error ?? "该条目不可用");

        var directory = string.IsNullOrWhiteSpace(targetDirectory) ? ResolveDownloadDirectory() : targetDirectory!;
        var task = Downloads.Enqueue(info, directory, link);
        return (task, $"已加入下载队列：{info.Title}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            SaveConfig();
            SaveDatabase(force: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"退出保存失败: {ex.Message}");
        }

        Downloads.Dispose();
        Workshop.Dispose();
    }
}
