using System.Collections.ObjectModel;
using System.Diagnostics;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Deployment;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Steam;

namespace L4D2ModManager.App.ViewModels;

/// <summary>设置页面：Mod 目录管理、下载目录、Steam 路径、功能开关与诊断信息。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;

    private string? _selectedDirectory;
    private string _downloadDirectory = string.Empty;
    private string _steamRootPath = string.Empty;
    private string _steamCmdPath = string.Empty;
    private string _steamWebApiKey = string.Empty;
    private string _statusText = "修改会立即保存。";
    private bool _suppressSave;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        AddDirectoryCommand = new RelayCommand(_ => AddDirectory());
        RemoveDirectoryCommand = new RelayCommand(_ => RemoveDirectory(), _ => SelectedDirectory != null);
        BrowseDownloadDirectoryCommand = new RelayCommand(_ => BrowseDownloadDirectory());
        UseFirstAddonDirectoryCommand = new RelayCommand(_ => UseFirstAddonDirectory());
        BrowseSteamCmdCommand = new RelayCommand(_ => BrowseSteamCmd());
        DetectSteamCommand = new RelayCommand(_ => DetectSteam());
        OpenDataFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.Root));
        OpenLogFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.LogDir));
        OpenConfigFileCommand = new RelayCommand(_ => OpenPath(AppPaths.ConfigFile, select: true));
        OpenDatabaseFileCommand = new RelayCommand(_ => OpenPath(AppPaths.DatabaseFile, select: true));
        OpenThumbnailFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.ThumbnailDir));
        ClearThumbnailCacheCommand = new RelayCommand(_ => ClearThumbnailCache());
        SaveCommand = new RelayCommand(_ => SaveAll());
        ReloadCommand = new RelayCommand(_ => Refresh());
        ResetThumbnailCacheCommand = new RelayCommand(_ => ResetThumbnailCache());
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());

        Refresh();
    }

    public ObservableCollection<string> Directories { get; } = new();

    public RelayCommand AddDirectoryCommand { get; }

    public RelayCommand RemoveDirectoryCommand { get; }

    public RelayCommand BrowseDownloadDirectoryCommand { get; }

    public RelayCommand UseFirstAddonDirectoryCommand { get; }

    public RelayCommand BrowseSteamCmdCommand { get; }

    public RelayCommand DetectSteamCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenConfigFileCommand { get; }

    public RelayCommand OpenDatabaseFileCommand { get; }

    public RelayCommand OpenThumbnailFolderCommand { get; }

    public RelayCommand ClearThumbnailCacheCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand ResetThumbnailCacheCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }

    public string? SelectedDirectory
    {
        get => _selectedDirectory;
        set
        {
            if (!Set(ref _selectedDirectory, value)) return;
            RemoveDirectoryCommand.RaiseCanExecuteChanged();
        }
    }

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            if (!Set(ref _downloadDirectory, value)) return;
            _library.Config.DownloadDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SaveConfig();
        }
    }

    public string SteamRootPath
    {
        get => _steamRootPath;
        set
        {
            if (!Set(ref _steamRootPath, value)) return;
            _library.Config.SteamRootPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SaveConfig();
        }
    }

    public string SteamCmdPath
    {
        get => _steamCmdPath;
        set
        {
            if (!Set(ref _steamCmdPath, value)) return;
            _library.Config.SteamCmdPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SaveConfig();
        }
    }

    public string SteamWebApiKey
    {
        get => _steamWebApiKey;
        set
        {
            if (!Set(ref _steamWebApiKey, value)) return;
            _library.Config.SteamWebApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SaveConfig();
        }
    }

    /// <summary>冲突检测忽略关键字（每行一个，按内部路径子串匹配）。</summary>
    public string ConflictIgnoreText
    {
        get => string.Join(Environment.NewLine, _library.Config.ConflictIgnorePatterns);
        set
        {
            var list = (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.SequenceEqual(_library.Config.ConflictIgnorePatterns, StringComparer.OrdinalIgnoreCase)) return;

            _library.Config.ConflictIgnorePatterns = list;
            Raise();
            SaveConfig();
            StatusText = list.Count == 0
                ? "已清空自定义忽略关键字（内置规则仍然生效）"
                : $"已保存 {list.Count} 条自定义忽略关键字";
        }
    }

    /// <summary>启动游戏时附加的参数（例如 "-insecure -console"）。</summary>
    public string LaunchArgumentsText
    {
        get => _library.Config.LaunchArguments;
        set
        {
            var text = value ?? string.Empty;
            if (_library.Config.LaunchArguments == text) return;
            _library.Config.LaunchArguments = text;
            Raise();
            SaveConfig();
            StatusText = string.IsNullOrWhiteSpace(text) ? "已清空启动参数" : $"启动参数已保存：{text}";
        }
    }

    public bool LaunchWithSteam
    {
        get => _library.Config.LaunchWithSteam;
        set
        {
            if (_library.Config.LaunchWithSteam == value) return;
            _library.Config.LaunchWithSteam = value;
            Raise();
            SaveConfig();
        }
    }

    public bool ConfirmInsecureLaunch
    {
        get => _library.Config.ConfirmInsecureLaunch;
        set
        {
            if (_library.Config.ConfirmInsecureLaunch == value) return;
            _library.Config.ConfirmInsecureLaunch = value;
            Raise();
            SaveConfig();
        }
    }

    /// <summary>默认隐藏"内容完全相同"的重复文件。</summary>
    public bool HideIdenticalConflicts
    {
        get => _library.Config.HideIdenticalConflicts;
        set
        {
            if (_library.Config.HideIdenticalConflicts == value) return;
            _library.Config.HideIdenticalConflicts = value;
            Raise();
            SaveConfig();
        }
    }

    public string BuiltinIgnoreHintText =>
        "内置忽略规则（部分）：" + string.Join("、", ConflictDetector.DefaultIgnoredPatterns.Take(5)) + " …";

    public bool AutoScanOnStartup
    {
        get => _library.Config.AutoScanOnStartup;
        set
        {
            if (_library.Config.AutoScanOnStartup == value) return;
            _library.Config.AutoScanOnStartup = value;
            Raise();
            SaveConfig();
        }
    }

    public bool AutoDetectSteamPaths
    {
        get => _library.Config.AutoDetectSteamPaths;
        set
        {
            if (_library.Config.AutoDetectSteamPaths == value) return;
            _library.Config.AutoDetectSteamPaths = value;
            Raise();
            SaveConfig();
        }
    }

    public bool ExtractThumbnails
    {
        get => _library.Config.ExtractThumbnails;
        set
        {
            if (_library.Config.ExtractThumbnails == value) return;
            _library.Config.ExtractThumbnails = value;
            Raise();
            SaveConfig();
        }
    }

    public bool FetchRemoteThumbnails
    {
        get => _library.Config.FetchRemoteThumbnails;
        set
        {
            if (_library.Config.FetchRemoteThumbnails == value) return;
            _library.Config.FetchRemoteThumbnails = value;
            Raise();
            SaveConfig();
        }
    }

    public bool BuildFileIndex
    {
        get => _library.Config.BuildFileIndex;
        set
        {
            if (_library.Config.BuildFileIndex == value) return;
            _library.Config.BuildFileIndex = value;
            Raise();
            SaveConfig();
        }
    }

    public bool DetectConflictsAfterScan
    {
        get => _library.Config.DetectConflictsAfterScan;
        set
        {
            if (_library.Config.DetectConflictsAfterScan == value) return;
            _library.Config.DetectConflictsAfterScan = value;
            Raise();
            SaveConfig();
        }
    }

    public bool DisabledFirst
    {
        get => _library.Config.DisabledFirst;
        set
        {
            if (_library.Config.DisabledFirst == value) return;
            _library.Config.DisabledFirst = value;
            Raise();
            SaveConfig();
        }
    }

    public bool VerboseLogging
    {
        get => _library.Config.VerboseLogging;
        set
        {
            if (_library.Config.VerboseLogging == value) return;
            _library.Config.VerboseLogging = value;
            Raise();
            SaveConfig();
        }
    }

    /// <summary>毛玻璃背景：更美观，但由 DWM 每帧重算，可能降低滚动与动画流畅度。</summary>
    public bool UseAcrylic
    {
        get => _library.Config.UseAcrylic;
        set
        {
            if (_library.Config.UseAcrylic == value) return;
            _library.Config.UseAcrylic = value;
            Raise();
            SaveConfig();
            AcrylicChanged?.Invoke();
        }
    }

    /// <summary>启动时自动请求管理员权限（UAC 自提升）。</summary>
    public bool RequestElevationOnStartup
    {
        get => _library.Config.RequestElevationOnStartup;
        set
        {
            if (_library.Config.RequestElevationOnStartup == value) return;
            _library.Config.RequestElevationOnStartup = value;
            if (value) _library.Config.ElevationDeclined = false;
            Raise();
            SaveConfig();
        }
    }

    public event Action? AcrylicChanged;

    public string ElevationStatusText => AppDeployment.IsElevated
        ? "当前权限：管理员（推荐，可写入 Steam 安装目录）"
        : "当前权限：普通用户（写入 C:\\Program Files (x86) 下的 Steam 目录可能失败）";

    public string GraphicsStatusText
    {
        get
        {
            try
            {
                return (System.Windows.Media.RenderCapability.Tier >> 16) switch
                {
                    0 => "图形加速：软件渲染（未启用硬件加速，动画会明显偏慢；请更新显卡驱动或关闭远程桌面）",
                    1 => "图形加速：部分硬件加速",
                    _ => "图形加速：硬件加速（Tier 2，动画最流畅）",
                };
            }
            catch
            {
                return "图形加速：未知";
            }
        }
    }

    public string DragDropStatusText => AppDeployment.IsElevated
        ? "拖放安装：管理员权限下 Windows 禁止接收资源管理器拖放，请使用「添加 Mod 文件」按钮"
        : "拖放安装：可直接把 .vpk 拖入窗口";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DataDirectoryText => AppPaths.Root;

    public string ConfigPathText => AppPaths.ConfigFile;

    public string DatabasePathText => AppPaths.DatabaseFile;

    public string LogPathText => AppPaths.LogDir;

    public string ModCountText => $"{_library.Mods.Count} 个 Mod（启用 {_library.Mods.Count(m => m.IsEnabled)}）";

    public string DirectoryCountText => $"{_library.Config.ModDirectories.Count} 个目录";

    public string ThumbnailCacheText
    {
        get
        {
            try
            {
                if (!Directory.Exists(AppPaths.ThumbnailDir)) return "0 个文件";
                var files = new DirectoryInfo(AppPaths.ThumbnailDir).GetFiles();
                return $"{files.Length} 个文件 · {ModItem.FormatSize(files.Sum(x => x.Length))}";
            }
            catch
            {
                return "无法读取";
            }
        }
    }

    public string SteamStatusText
    {
        get
        {
            try
            {
                var paths = _library.SteamPaths;
                var parts = new List<string>();
                parts.Add(paths.SteamRoot == null ? "未检测到 Steam" : $"Steam：{paths.SteamRoot}");
                parts.Add($"库 {paths.Libraries.Count} 个");
                parts.Add($"addons 目录 {paths.AddonDirectories.Count} 个");
                parts.Add($"工坊目录 {paths.WorkshopDirectories.Count} 个");
                parts.Add(paths.SteamCmdPath == null ? "未找到 steamcmd" : "已找到 steamcmd");
                return string.Join(" · ", parts);
            }
            catch (Exception ex)
            {
                return "探测失败：" + ex.Message;
            }
        }
    }

    public string DownloadProvidersText =>
        string.Join(" → ", _library.Downloads.Providers.Select(p => p.Name));

    public void Refresh()
    {
        _suppressSave = true;
        try
        {
            Directories.Clear();
            foreach (var directory in _library.Config.ModDirectories) Directories.Add(directory);

            DownloadDirectory = _library.Config.DownloadDirectory ?? _library.ResolveDownloadDirectory();
            SteamRootPath = _library.Config.SteamRootPath ?? string.Empty;
            SteamCmdPath = _library.Config.SteamCmdPath ?? string.Empty;
            SteamWebApiKey = _library.Config.SteamWebApiKey ?? string.Empty;

            Raise(nameof(AutoScanOnStartup), nameof(AutoDetectSteamPaths), nameof(ExtractThumbnails),
                  nameof(FetchRemoteThumbnails), nameof(BuildFileIndex), nameof(DetectConflictsAfterScan),
                  nameof(DisabledFirst), nameof(VerboseLogging), nameof(DataDirectoryText), nameof(ConfigPathText),
                  nameof(DatabasePathText), nameof(LogPathText), nameof(ModCountText), nameof(DirectoryCountText),
                  nameof(ThumbnailCacheText), nameof(SteamStatusText), nameof(DownloadProvidersText));
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private void SaveConfig()
    {
        if (_suppressSave) return;
        _library.SaveConfig();
    }

    private void SaveAll()
    {
        _library.SaveConfig();
        _library.SaveDatabase(force: true);
        StatusText = "配置与数据库已保存";
        Refresh();
    }

    private void AddDirectory()
    {
        var picked = FolderPicker.Pick(_services.Dialogs == null ? null : System.Windows.Application.Current?.MainWindow,
            "选择包含 .vpk 的 Mod 目录");

        if (string.IsNullOrWhiteSpace(picked)) return;

        if (_library.AddDirectory(picked))
        {
            StatusText = "已添加目录：" + picked;
            Refresh();
        }
        else
        {
            StatusText = "目录已存在或无效：" + picked;
        }
    }

    private void RemoveDirectory()
    {
        if (SelectedDirectory == null) return;

        var directory = SelectedDirectory;
        if (!_services.Dialogs.Confirm($"确定要从列表中移除该目录吗？\r\n\r\n{directory}\r\n\r\n（不会删除磁盘上的任何文件）",
                "移除目录")) return;

        if (_library.RemoveDirectory(directory))
        {
            StatusText = "已移除目录：" + directory;
            Refresh();
        }
    }

    private void BrowseDownloadDirectory()
    {
        var picked = FolderPicker.Pick(System.Windows.Application.Current?.MainWindow, "选择下载保存目录", DownloadDirectory);
        if (!string.IsNullOrWhiteSpace(picked)) DownloadDirectory = picked;
    }

    private void UseFirstAddonDirectory()
    {
        var addons = _library.SteamPaths.AddonDirectories.FirstOrDefault(Directory.Exists);
        if (addons == null)
        {
            _services.Dialogs.Info("未检测到 L4D2 的 addons 目录，请手动选择。", "未找到");
            return;
        }

        DownloadDirectory = addons;
        StatusText = "下载目录已设为：" + addons;
    }

    private void BrowseSteamCmd()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 steamcmd.exe",
            Filter = "steamcmd|steamcmd.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) SteamCmdPath = dialog.FileName;
    }

    private void DetectSteam()
    {
        try
        {
            var paths = SteamLibraryLocator.Detect();
            var added = 0;

            foreach (var directory in paths.AddonDirectories.Concat(paths.WorkshopDirectories))
            {
                if (_library.Config.ModDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)) continue;
                _library.Config.ModDirectories.Add(directory);
                added++;
            }

            if (paths.SteamRoot != null) _library.Config.SteamRootPath = paths.SteamRoot;
            if (paths.SteamCmdPath != null) _library.Config.SteamCmdPath = paths.SteamCmdPath;

            _library.SaveConfig();
            StatusText = $"重新探测完成：新增 {added} 个目录，Steam 库 {paths.Libraries.Count} 个";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = "探测失败：" + ex.Message;
        }
    }

    private void ClearThumbnailCache()
    {
        if (!_services.Dialogs.Confirm("确定要清空缩略图缓存吗？下次浏览时会重新提取或下载。", "清空缩略图缓存")) return;

        try
        {
            var deleted = 0;
            if (Directory.Exists(AppPaths.ThumbnailDir))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.ThumbnailDir))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // 跳过占用中的文件
                    }
                }
            }

            foreach (var mod in _library.Mods) mod.ThumbnailPath = null;
            _library.SaveDatabase();
            _services.Thumbnails.Clear();

            StatusText = $"已删除 {deleted} 个缩略图缓存文件";
            Raise(nameof(ThumbnailCacheText));
        }
        catch (Exception ex)
        {
            StatusText = "清空缓存失败：" + ex.Message;
        }
    }

    private void ResetThumbnailCache()
    {
        _services.Thumbnails.Clear();
        StatusText = "已重置内存中的缩略图缓存";
    }

    /// <summary>以管理员身份重新启动（提权后即可写入受保护的 Steam 目录）。</summary>
    private void RestartElevated()
    {
        if (AppDeployment.IsElevated)
        {
            StatusText = "当前已经是管理员权限";
            return;
        }

        if (ElevationService.TryRestartElevated(Array.Empty<string>(), out var error))
        {
            _library.SaveConfig();
            _library.SaveDatabase(force: true);
            System.Windows.Application.Current.Shutdown();
            return;
        }

        StatusText = "未能获取管理员权限：" + error;
        _services.Dialogs.Info("未能获取管理员权限。\r\n\r\n" + error, "管理员权限");
    }

    private void OpenPath(string path, bool select = false)
    {
        try
        {
            if (select && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开失败：" + ex.Message;
        }
    }
}
