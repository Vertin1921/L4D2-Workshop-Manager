using System.Text;

namespace L4D2ModManager.Core.Services.Vpk;

/// <summary>VPK 头部/目录树解析器。</summary>
public static class VpkReader
{
    /// <summary>VPK 文件签名 0x55AA1234（小端存放）。</summary>
    public const uint Signature = 0x55AA1234;

    /// <summary>条目终止符。</summary>
    public const ushort EntryTerminator = 0xFFFF;

    /// <summary>archive_index 为 0x7FFF 表示数据紧随目录树之后。</summary>
    public const ushort EmbeddedArchiveIndex = 0x7FFF;

    private const int HeaderSizeV1 = 12;
    private const int HeaderSizeV2 = 28;
    private const int EntrySize = 18;

    /// <summary>仅通过前 4 个字节判断是否为 VPK。</summary>
    public static bool LooksLikeVpk(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[4];
            int read = stream.Read(buffer);
            return read == 4 && BitConverter.ToUInt32(buffer) == Signature;
        }
        catch
        {
            return false;
        }
    }

    public static VpkArchive? TryOpen(string path, out string? error)
    {
        try
        {
            error = null;
            return Open(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>打开 VPK 并解析目录树。</summary>
    public static VpkArchive Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径为空", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 VPK 文件", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (stream.Length < HeaderSizeV1)
            throw new InvalidDataException("文件太小，不是有效的 VPK");

        uint signature = reader.ReadUInt32();
        if (signature != Signature)
            throw new InvalidDataException($"VPK 签名无效: 0x{signature:X8}");

        uint version = reader.ReadUInt32();
        if (version is not (0 or 1 or 2))
            throw new InvalidDataException($"不支持的 VPK 版本: {version}");

        uint treeSize = reader.ReadUInt32();
        int headerSize = version >= 2 ? HeaderSizeV2 : HeaderSizeV1;

        if (version >= 2)
        {
            // file_data_section_size, archive_md5_section_size, other_md5_section_size, signature_section_size
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        if ((long)headerSize + treeSize > stream.Length)
            throw new InvalidDataException($"目录树长度异常: treeSize={treeSize}, 文件长度={stream.Length}");

        var treeBytes = reader.ReadBytes((int)treeSize);
        if (treeBytes.Length != (int)treeSize)
            throw new InvalidDataException("目录树读取不完整");

        var entries = ParseTree(treeBytes);
        return new VpkArchive(path, version, headerSize, treeSize, entries);
    }

    /// <summary>外部数据分卷路径：pak01_dir.vpk → pak01_003.vpk。</summary>
    public static string? GetChunkPath(string dirFilePath, int archiveIndex)
    {
        var dir = Path.GetDirectoryName(dirFilePath);
        var name = Path.GetFileNameWithoutExtension(dirFilePath);
        if (string.IsNullOrEmpty(name)) return null;

        // 去掉 _dir 后缀（VPK 约定：xxx_dir.vpk + xxx_000.vpk）
        if (name.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var chunk = $"{name}_{archiveIndex:000}.vpk";
        return string.IsNullOrEmpty(dir) ? chunk : Path.Combine(dir, chunk);
    }

    private static List<VpkEntry> ParseTree(byte[] tree)
    {
        var entries = new List<VpkEntry>(Math.Max(16, tree.Length / 64));
        using var stream = new MemoryStream(tree, writable: false);

        while (stream.Position < tree.Length)
        {
            var extension = ReadCString(stream);
            if (extension == null || extension.Length == 0) break;

            while (true)
            {
                var path = ReadCString(stream);
                if (path == null || path.Length == 0) break;

                while (true)
                {
                    var fileName = ReadCString(stream);
                    if (fileName == null || fileName.Length == 0) break;
                    if (stream.Position + EntrySize > tree.Length) break;

                    uint crc = ReadUInt32(stream);
                    ushort preloadBytes = ReadUInt16(stream);
                    ushort archiveIndex = ReadUInt16(stream);
                    uint offset = ReadUInt32(stream);
                    uint length = ReadUInt32(stream);
                    ushort terminator = ReadUInt16(stream);

                    if (terminator != EntryTerminator)
                    {
                        // 容错：位置异常时停止解析，已解析的条目仍然可用
                        if (preloadBytes == 0 && archiveIndex == 0 && length == 0) break;
                    }

                    var entry = new VpkEntry
                    {
                        Extension = extension,
                        // 根目录文件在 VPK 中用单个空格表示目录，这里统一成空字符串
                        Directory = path == " " ? string.Empty : path.Trim(),
                        FileName = fileName,
                        Crc = crc,
                        PreloadBytes = preloadBytes,
                        ArchiveIndex = archiveIndex,
                        Offset = offset,
                        Length = length,
                    };

                    if (preloadBytes > 0)
                    {
                        if (stream.Position + preloadBytes > tree.Length) break;
                        entry.Preload = ReadBytes(stream, preloadBytes);
                    }

                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static string? ReadCString(Stream stream)
    {
        var bytes = new List<byte>(32);
        int value;
        while ((value = stream.ReadByte()) >= 0)
        {
            if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add((byte)value);
            if (bytes.Count > 4096) break; // 防御性上限
        }
        return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == count) return buffer;
        Array.Resize(ref buffer, read);
        return buffer;
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BitConverter.ToUInt32(buffer);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BitConverter.ToUInt16(buffer);
    }
}
