namespace L4D2ModManager.Core.Services.Vpk;

/// <summary>VPK 目录树中的一个文件条目。</summary>
public sealed class VpkEntry
{
    /// <summary>扩展名（不含点），例如 "mdl"、"txt"。</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>内部目录，使用 "/" 分隔，例如 "models/weapons"。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>文件名（不含扩展名）。</summary>
    public string FileName { get; init; } = string.Empty;

    public uint Crc { get; init; }

    /// <summary>保存在目录文件中的预载字节数。</summary>
    public ushort PreloadBytes { get; init; }

    /// <summary>0x7FFF 表示数据紧随目录树之后，否则为外部分卷编号。</summary>
    public ushort ArchiveIndex { get; init; }

    /// <summary>数据偏移：内嵌时相对数据区起点，外部分卷时相对该分卷起点。</summary>
    public uint Offset { get; init; }

    /// <summary>数据区长度（不含预载部分）。</summary>
    public uint Length { get; init; }

    /// <summary>预载数据（紧随条目之后存储）。</summary>
    public byte[] Preload { get; set; } = Array.Empty<byte>();

    /// <summary>文件总长度 = 预载 + 数据区。</summary>
    public long TotalLength => PreloadBytes + Length;

    /// <summary>内部完整路径，例如 "models/weapons/v_rif_m16.mdl"。</summary>
    public string FullPath =>
        string.IsNullOrEmpty(Directory)
            ? $"{FileName}.{Extension}"
            : $"{Directory}/{FileName}.{Extension}";

    /// <summary>用于比较的规范化路径（小写、统一分隔符）。</summary>
    public string NormalizedPath => Normalize(FullPath);

    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public override string ToString() => $"{FullPath} ({TotalLength} bytes)";
}
