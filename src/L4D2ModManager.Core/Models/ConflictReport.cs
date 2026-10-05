using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.Core.Models;

/// <summary>冲突中涉及的一个 Mod。</summary>
public sealed class ConflictParticipant
{
    public string ModKey { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    public string StateText => Enabled ? "已启用" : "已禁用";
}

/// <summary>一个被多个 Mod 共同包含的文件。</summary>
public sealed class ConflictFileEntry
{
    public string InnerPath { get; set; } = string.Empty;
    public List<ConflictParticipant> Mods { get; set; } = new();

    /// <summary>严重程度：资源覆盖（高）/ 脚本定义（中）/ 其他（低）。</summary>
    public ConflictSeverity Severity { get; set; } = ConflictSeverity.Low;

    /// <summary>
    /// 各 Mod 中该文件的 CRC32 完全一致 —— 内容一模一样，只是重复打包，
    /// 不会产生任何覆盖影响（默认在界面上隐藏，可切换显示）。
    /// </summary>
    public bool ContentIdentical { get; set; }

    public int Count => Mods.Count;
    public int EnabledCount => Mods.Count(m => m.Enabled);

    public string ModsText => string.Join("  ·  ", Mods.Select(m => m.ModName));

    public string SeverityText => Severity switch
    {
        ConflictSeverity.High => "高",
        ConflictSeverity.Medium => "中",
        _ => "低",
    };

    public string KindText => ContentIdentical
        ? "内容相同"
        : Severity switch
        {
            ConflictSeverity.High => "资源覆盖",
            ConflictSeverity.Medium => "脚本/定义",
            _ => "其他",
        };

    public string Extension
    {
        get
        {
            var ext = Path.GetExtension(InnerPath);
            return string.IsNullOrEmpty(ext) ? "(无扩展名)" : ext.TrimStart('.').ToLowerInvariant();
        }
    }

    /// <summary>是否至少有两个"已启用"的 Mod 争抢该文件（真正的运行时冲突）。</summary>
    public bool IsActiveConflict => !ContentIdentical && EnabledCount >= 2;
}

/// <summary>一次冲突检测的完整结果。</summary>
public sealed class ConflictReport
{
    public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;
    public List<ConflictFileEntry> Files { get; set; } = new();
    public int TotalFilesCompared { get; set; }
    public int ConflictedModCount { get; set; }
    public int ActiveConflictCount { get; set; }

    /// <summary>内容完全相同的重复文件数量（已判定为无影响）。</summary>
    public int IdenticalDuplicateCount { get; set; }

    /// <summary>被规则忽略的文件数量（addoninfo、共享脚本库等）。</summary>
    public int IgnoredFiles { get; set; }

    public int FileCount => Files.Count;

    /// <summary>真实冲突数量（排除内容相同的重复）。</summary>
    public int RealConflictCount => Files.Count(f => !f.ContentIdentical);

    public int HighSeverityCount => Files.Count(f => !f.ContentIdentical && f.Severity == ConflictSeverity.High);

    public string SummaryText
    {
        get
        {
            if (Files.Count == 0)
                return $"未发现冲突（比较了 {TotalFilesCompared:N0} 个内部文件，忽略 {IgnoredFiles:N0} 个共享/描述文件）";

            var text = $"发现 {RealConflictCount:N0} 个真实冲突文件（其中 {HighSeverityCount:N0} 个为资源覆盖），" +
                       $"涉及 {ConflictedModCount} 个 Mod，{ActiveConflictCount} 个为「同时启用」冲突";

            if (IdenticalDuplicateCount > 0)
                text += $"；另有 {IdenticalDuplicateCount:N0} 个文件内容完全相同（已判为无影响）";

            return text;
        }
    }

    public string ScannedText => ScannedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
