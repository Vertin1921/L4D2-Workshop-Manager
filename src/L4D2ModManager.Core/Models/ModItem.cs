using System.Text.Json.Serialization;

namespace L4D2ModManager.Core.Models;

/// <summary>
/// 一个 Mod 条目，与磁盘上的一个 .vpk 文件一一对应。
/// 该对象会被序列化到 ModDatabase.json。
/// </summary>
public sealed class ModItem
{
    /// <summary>禁用状态使用的文件后缀。</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>稳定标识：workshop:&lt;id&gt; 或 file:&lt;小写完整路径&gt;。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>逻辑路径：始终以 .vpk 结尾（不含 .disabled 后缀）。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>所属 Mod 目录。</summary>
    public string Directory { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>addoninfo.txt 中的原始 tag 文本。</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>Steam 创意工坊 ID（可空）。</summary>
    public string? WorkshopId { get; set; }

    public ModCategory Category { get; set; } = ModCategory.Other;
    public ModState State { get; set; } = ModState.Enabled;

    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }

    /// <summary>首次被本程序发现的时间（用于“最近添加”排序）。</summary>
    public DateTime AddedUtc { get; set; }

    /// <summary>通过本程序下载完成的时间。</summary>
    public DateTime? DownloadUtc { get; set; }

    /// <summary>本地缩略图缓存路径。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>是否成功读取到 addoninfo.txt。</summary>
    public bool HasAddonInfo { get; set; }

    /// <summary>解析过程中的错误信息（不影响条目存在）。</summary>
    public string? Error { get; set; }

    /// <summary>VPK 内部文件清单，用于冲突检测与分类。</summary>
    public List<string> FileIndex { get; set; } = new();

    /// <summary>解析后的内部文件条目（带 CRC32），首次访问时构建并缓存。</summary>
    [JsonIgnore]
    public IReadOnlyList<ModFileEntry> Files
    {
        get
        {
            if (_files != null && _filesStamp == FileIndex.Count) return _files;
            _files = FileIndex.Select(ModFileEntry.Parse).ToList();
            _filesStamp = FileIndex.Count;
            return _files;
        }
    }

    [JsonIgnore] private List<ModFileEntry>? _files;
    [JsonIgnore] private int _filesStamp = -1;

    /// <summary>FileIndex 对应的磁盘指纹（大小 + 修改时间），用于跳过重复解析。</summary>
    public string? IndexStamp { get; set; }

    /// <summary>冲突检测结果缓存：与其他 Mod 重复的文件数量。</summary>
    public int ConflictCount { get; set; }

    [JsonIgnore] public string FileName => Path.GetFileName(FilePath);

    [JsonIgnore] public string BaseName => Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>磁盘上的真实路径（禁用时带 .disabled 后缀）。</summary>
    [JsonIgnore]
    public string ActualPath => State == ModState.Enabled ? FilePath : FilePath + DisabledSuffix;

    [JsonIgnore] public bool IsEnabled => State == ModState.Enabled;

    [JsonIgnore] public bool IsDisabled => State == ModState.Disabled;

    [JsonIgnore] public string StateText => IsEnabled ? "已启用" : "已禁用";

    [JsonIgnore] public string CategoryText => ModCategoryInfo.Full(Category);

    [JsonIgnore] public string SizeText => FormatSize(SizeBytes);

    [JsonIgnore] public string AddedText => AddedUtc == default ? "-" : AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore] public string ModifiedText => ModifiedUtc == default ? "-" : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>该 Mod 是否存在于磁盘。</summary>
    [JsonIgnore] public bool Exists => File.Exists(ActualPath);

    [JsonIgnore]
    public string ShortDescription
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Description) ? "（无描述）" : Description;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 160 ? text : text[..160] + "…";
        }
    }

    /// <summary>生成稳定 Key。</summary>
    public static string MakeKey(string logicalVpkPath, string? workshopId)
    {
        if (!string.IsNullOrWhiteSpace(workshopId))
            return "workshop:" + workshopId.Trim();
        return "file:" + logicalVpkPath.Trim().ToLowerInvariant();
    }

    /// <summary>磁盘指纹，用于判断是否需要重新解析 VPK。</summary>
    public static string MakeStamp(long size, DateTime modifiedUtc) =>
        $"{size}:{modifiedUtc.Ticks}";

    /// <summary>去掉 .disabled 后缀，得到逻辑 .vpk 路径。</summary>
    public static string ToLogicalPath(string actualPath) =>
        actualPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? actualPath[..^DisabledSuffix.Length]
            : actualPath;

    public static ModState StateFromPath(string path) =>
        path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? ModState.Disabled : ModState.Enabled;

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "-";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
