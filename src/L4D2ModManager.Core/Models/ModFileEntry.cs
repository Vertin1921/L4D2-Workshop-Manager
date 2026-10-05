namespace L4D2ModManager.Core.Models;

/// <summary>
/// VPK 内部的一个文件条目。
/// 序列化时紧凑表示为 "内部路径|CRC32"（例如 models/weapons/v_rif_m16.mdl|A1B2C3D4），
/// 有了 CRC 才能判断"两个 Mod 都包含这个文件"到底是**同一份内容**（无影响）
/// 还是**不同内容**（真正的覆盖冲突）—— 这是冲突检测准确度的关键。
/// </summary>
public sealed class ModFileEntry
{
    public string Path { get; init; } = string.Empty;

    /// <summary>VPK 目录树中记录的 CRC32。</summary>
    public uint Crc { get; init; }

    /// <summary>是否带有 CRC 信息（旧版本数据库或外部索引可能没有）。</summary>
    public bool HasCrc { get; init; }

    /// <summary>规范化路径（小写、正斜杠），用于比较。</summary>
    public string NormalizedPath => Path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>是否为脚本类内部文件。</summary>
    public bool IsScript => NormalizedPath.StartsWith("scripts/", StringComparison.Ordinal);

    public static ModFileEntry Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new ModFileEntry();

        int separator = raw.LastIndexOf('|');
        if (separator <= 0 || separator == raw.Length - 1)
            return new ModFileEntry { Path = raw, HasCrc = false };

        var crcText = raw[(separator + 1)..];
        if (crcText.Length is > 8 or < 1 || !uint.TryParse(crcText, System.Globalization.NumberStyles.HexNumber, null, out var crc))
            return new ModFileEntry { Path = raw, HasCrc = false };

        return new ModFileEntry
        {
            Path = raw[..separator],
            Crc = crc,
            HasCrc = true,
        };
    }

    public static string Format(string path, uint crc) => $"{path}|{crc:X8}";

    public static string Format(string path) => path;

    public override string ToString() => HasCrc ? $"{Path}|{Crc:X8}" : Path;
}
