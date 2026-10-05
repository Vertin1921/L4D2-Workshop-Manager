namespace L4D2ModManager.Core.Models;

/// <summary>排序方式。</summary>
public enum ModSortMode
{
    /// <summary>默认：禁用的 Mod 优先，其次按名称 A-Z。</summary>
    Default,
    NameAsc,
    NameDesc,
    RecentlyAdded,
    EarliestAdded,
    SizeDesc,
    SizeAsc,
    Category,
}

/// <summary>应用配置，保存到 %AppData%\L4D2ModManager\config.json。</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>用户添加的 Mod 目录（不重复）。</summary>
    public List<string> ModDirectories { get; set; } = new();

    /// <summary>下载保存目录；为空时使用第一个 Mod 目录。</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>steamcmd.exe 路径（创意工坊下载备用通道）。</summary>
    public string? SteamCmdPath { get; set; }

    /// <summary>Steam 根目录（自动检测结果）。</summary>
    public string? SteamRootPath { get; set; }

    /// <summary>可选的 Steam Web API Key（用于搜索创意工坊；浏览页面不需要）。</summary>
    public string? SteamWebApiKey { get; set; }

    public bool AutoDetectSteamPaths { get; set; } = true;
    public bool AutoScanOnStartup { get; set; } = true;

    /// <summary>是否从 VPK 内提取 addonimage 作为缩略图。</summary>
    public bool ExtractThumbnails { get; set; } = true;

    /// <summary>是否在没有内置图片时用创意工坊预览图。</summary>
    public bool FetchRemoteThumbnails { get; set; } = true;

    /// <summary>是否解析 VPK 内部文件清单（冲突检测必需，会略慢）。</summary>
    public bool BuildFileIndex { get; set; } = true;

    /// <summary>扫描完成后自动做一次冲突检测。</summary>
    public bool DetectConflictsAfterScan { get; set; } = true;

    /// <summary>
    /// 冲突检测的额外忽略关键字（每行一个，按内部路径子串匹配，不区分大小写）。
    /// 内置已忽略 addoninfo/addonimage 与常见共享脚本库，这里用于补充你自己环境里的"公共文件"。
    /// </summary>
    public List<string> ConflictIgnorePatterns { get; set; } = new();

    /// <summary>默认隐藏"内容完全相同"的重复文件（它们不会造成覆盖影响）。</summary>
    public bool HideIdenticalConflicts { get; set; } = true;

    /// <summary>启动游戏时附加的参数（例如 "-insecure -console"，按空格拆分）。</summary>
    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>优先通过 Steam 启动游戏（能带上覆盖层与游戏时间统计）。</summary>
    public bool LaunchWithSteam { get; set; } = true;

    /// <summary>以 -insecure 启动前先弹出确认（提醒会关闭 VAC）。</summary>
    public bool ConfirmInsecureLaunch { get; set; } = true;

    /// <summary>用户手动指定的 L4D2 游戏根目录（含 left4dead2.exe 的那一层）。</summary>
    public string L4D2GamePath { get; set; } = string.Empty;

    public ModSortMode SortMode { get; set; } = ModSortMode.Default;
    public bool SortDescending { get; set; }

    /// <summary>默认排序时禁用 Mod 是否排在最前。</summary>
    public bool DisabledFirst { get; set; } = true;

    public string? LastWorkshopUrl { get; set; }

    /// <summary>是否记录详细日志。</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// 是否启用毛玻璃（Acrylic）背景。
    /// 默认关闭：毛玻璃由 DWM 每帧重算，在部分机器上会明显降低滚动与动画流畅度。
    /// </summary>
    public bool UseAcrylic { get; set; }

    /// <summary>启动时自动请求管理员权限（弹出 UAC 并以管理员身份运行）。</summary>
    public bool RequestElevationOnStartup { get; set; } = true;

    /// <summary>用户曾明确选择不再提请管理员权限。</summary>
    public bool ElevationDeclined { get; set; }

    /// <summary>配置方案列表。</summary>
    public List<ModProfile> Profiles { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Profiles = ModProfile.CreateBuiltIns(),
    };

    /// <summary>去重、清理非法路径，并补齐内置方案。</summary>
    public void Normalize()
    {
        ModDirectories = ModDirectories
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
            DownloadDirectory = ModDirectories.FirstOrDefault();

        Profiles ??= new List<ModProfile>();
        foreach (var builtin in ModProfile.CreateBuiltIns())
        {
            if (!Profiles.Any(p => p.Preset == builtin.Preset || string.Equals(p.Id, builtin.Id, StringComparison.OrdinalIgnoreCase)))
                Profiles.Add(builtin);
        }

        foreach (var profile in Profiles)
            profile.Overrides ??= new Dictionary<string, ModState>(StringComparer.OrdinalIgnoreCase);
    }
}
