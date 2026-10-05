using System.Text;

namespace L4D2ModManager.Core.Services.Vpk;

/// <summary>
/// VPK 目录文件（*_dir.vpk 或单文件 .vpk）的只读访问器。
/// 支持：
///   · VPK 版本 1 / 2
///   · 内嵌数据（archive_index = 0x7FFF，数据位于目录树之后）
///   · 外部分卷（xxx_000.vpk ...）
///   · 预载数据（preload bytes 内联在目录树中）
/// 该类可安全释放，内部对文件流做了缓存。
/// </summary>
public sealed class VpkArchive : IDisposable
{
    private readonly Dictionary<string, VpkEntry> _byPath;
    private readonly Dictionary<int, FileStream> _chunkStreams = new();
    private FileStream? _dirStream;
    private bool _disposed;

    internal VpkArchive(string dirFilePath, uint version, int headerSize, uint treeSize, List<VpkEntry> entries)
    {
        DirFilePath = dirFilePath;
        Version = version;
        HeaderSize = headerSize;
        TreeSize = treeSize;
        Entries = entries;
        DataSectionStart = (long)headerSize + treeSize;

        _byPath = new Dictionary<string, VpkEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            _byPath.TryAdd(entry.NormalizedPath, entry);
    }

    /// <summary>目录文件的完整路径。</summary>
    public string DirFilePath { get; }

    public uint Version { get; }

    public int HeaderSize { get; }

    public uint TreeSize { get; }

    /// <summary>数据区起点 = 头部长度 + 目录树长度。</summary>
    public long DataSectionStart { get; }

    public IReadOnlyList<VpkEntry> Entries { get; }

    public int FileCount => Entries.Count;

    /// <summary>是否存在外部分卷。</summary>
    public bool IsMultiChunk => Entries.Any(e => e.ArchiveIndex != VpkReader.EmbeddedArchiveIndex);

    /// <summary>全部内部路径。</summary>
    public IEnumerable<string> FilePaths => Entries.Select(e => e.FullPath);

    public VpkEntry? Find(string innerPath)
    {
        if (string.IsNullOrWhiteSpace(innerPath)) return null;
        return _byPath.TryGetValue(VpkEntry.Normalize(innerPath), out var entry) ? entry : null;
    }

    public bool Contains(string innerPath) => Find(innerPath) != null;

    /// <summary>读取条目内容（预载 + 数据区）。读取失败返回 null。</summary>
    public byte[]? Read(VpkEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TotalLength <= 0) return Array.Empty<byte>();
        if (entry.TotalLength > int.MaxValue) return null;

        var result = new byte[entry.TotalLength];
        Buffer.BlockCopy(entry.Preload, 0, result, 0, entry.PreloadBytes);

        if (entry.Length > 0)
        {
            var stream = GetStream(entry.ArchiveIndex);
            if (stream == null) return null;

            long offset = entry.ArchiveIndex == VpkReader.EmbeddedArchiveIndex
                ? DataSectionStart + entry.Offset
                : entry.Offset;

            if (offset + entry.Length > stream.Length) return null;

            stream.Position = offset;
            int read = 0;
            while (read < entry.Length)
            {
                int n = stream.Read(result, entry.PreloadBytes + read, (int)entry.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read < entry.Length)
            {
                // 数据不足时截断返回，避免抛异常影响扫描
                Array.Resize(ref result, entry.PreloadBytes + read);
            }
        }

        return result;
    }

    /// <summary>按内部路径读取内容。</summary>
    public byte[]? ReadFile(string innerPath)
    {
        var entry = Find(innerPath);
        return entry == null ? null : Read(entry);
    }

    /// <summary>按内部路径读取文本（UTF-8，宽容处理非法字节）。</summary>
    public string? ReadText(string innerPath)
    {
        var bytes = ReadFile(innerPath);
        if (bytes == null) return null;
        if (bytes.Length == 0) return string.Empty;

        var text = Encoding.UTF8.GetString(bytes);
        // 去除 UTF-8 BOM
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>列出文件名（不含路径）以指定前缀开头的条目。</summary>
    public IEnumerable<VpkEntry> FindByFileNamePrefix(string prefix) =>
        Entries.Where(e => e.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 查找 addoninfo.txt（L4D2 规定位于 VPK 根目录，部分 Mod 放在子目录）。
    /// </summary>
    public VpkEntry? FindAddonInfo()
    {
        var entry = Find("addoninfo.txt");
        if (entry != null) return entry;
        return Entries.FirstOrDefault(e =>
            string.Equals(e.FileName, "addoninfo", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Extension, "txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>查找缩略图（addonimage.jpg / png ...）。</summary>
    public VpkEntry? FindAddonImage()
    {
        string[] preferred = { "jpg", "jpeg", "png", "bmp", "gif" };
        foreach (var ext in preferred)
        {
            var entry = Find($"addonimage.{ext}");
            if (entry != null) return entry;
        }

        return Entries
            .Where(e => e.FileName.StartsWith("addonimage", StringComparison.OrdinalIgnoreCase))
            .Where(e => preferred.Contains(e.Extension.ToLowerInvariant()))
            .OrderBy(e => e.Directory.Length)
            .FirstOrDefault();
    }

    private FileStream? GetStream(int archiveIndex)
    {
        if (archiveIndex == VpkReader.EmbeddedArchiveIndex)
        {
            _dirStream ??= OpenRead(DirFilePath);
            return _dirStream;
        }

        if (_chunkStreams.TryGetValue(archiveIndex, out var cached)) return cached;

        var chunkPath = VpkReader.GetChunkPath(DirFilePath, archiveIndex);
        if (chunkPath == null || !File.Exists(chunkPath)) return null;

        var stream = OpenRead(chunkPath);
        if (stream == null) return null;

        _chunkStreams[archiveIndex] = stream;
        return stream;
    }

    private static FileStream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dirStream?.Dispose();
        foreach (var stream in _chunkStreams.Values) stream.Dispose();
        _chunkStreams.Clear();
    }
}
