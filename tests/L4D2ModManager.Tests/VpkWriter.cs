using System.Text;

namespace L4D2ModManager.Tests;

/// <summary>
/// 测试用 VPK 生成器：按照 VPK 文件格式规范（Valve 官方说明）逐字节写出，
/// 用于验证读取端实现，而不是复用读取端逻辑。
///
/// 目录树结构：
///   [扩展名\0 [目录\0 [文件名\0 条目(18 字节) [预载数据] ... \0] ... \0] ... ]
/// 条目（18 字节）：
///   CRC(4) 预载字节数(2) 分卷号(2) 偏移(4) 长度(4) 终止符 0xFFFF(2)
/// 数据区紧跟目录树，内嵌条目（分卷号 0x7FFF）的偏移相对数据区起点。
/// </summary>
internal sealed class VpkWriter
{
    public sealed record Entry(string Path, byte[] Data, int PreloadBytes, ushort ArchiveIndex);

    private readonly List<Entry> _entries = new();

    public VpkWriter Add(string path, byte[] data, int preloadBytes = 0, ushort archiveIndex = 0x7FFF)
    {
        _entries.Add(new Entry(path.Replace('\\', '/').TrimStart('/'), data, preloadBytes, archiveIndex));
        return this;
    }

    public VpkWriter AddText(string path, string text, int preloadBytes = 0, ushort archiveIndex = 0x7FFF) =>
        Add(path, Encoding.UTF8.GetBytes(text), preloadBytes, archiveIndex);

    /// <param name="version">VPK 版本（1 或 2）。</param>
    /// <param name="writeFinalSentinel">是否写出“结尾空扩展名 + 0xFFFF”这种真实 VPK 常见的收尾。</param>
    /// <param name="chunks">外部分卷数据：分卷号 → 文件内容。</param>
    public byte[] Build(uint version, out Dictionary<int, byte[]> chunks, bool writeFinalSentinel = true)
    {
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();
        var chunkStreams = new Dictionary<int, MemoryStream>();
        chunks = new Dictionary<int, byte[]>();

        foreach (var extensionGroup in _entries.GroupBy(e => ExtensionOf(e.Path)))
        {
            WriteString(tree, extensionGroup.Key);

            foreach (var directoryGroup in extensionGroup.GroupBy(e => DirectoryOf(e.Path)))
            {
                WriteString(tree, directoryGroup.Key);

                foreach (var entry in directoryGroup)
                {
                    WriteString(tree, FileNameOf(entry.Path));

                    ushort preload = (ushort)entry.PreloadBytes;
                    uint length = (uint)(entry.Data.Length - preload);
                    uint offset;

                    if (entry.ArchiveIndex == 0x7FFF)
                    {
                        offset = (uint)dataSection.Position;
                        dataSection.Write(entry.Data, preload, entry.Data.Length - preload);
                    }
                    else
                    {
                        if (!chunkStreams.TryGetValue(entry.ArchiveIndex, out var chunk))
                        {
                            chunk = new MemoryStream();
                            chunkStreams[entry.ArchiveIndex] = chunk;
                        }
                        offset = (uint)chunk.Position;
                        chunk.Write(entry.Data, preload, entry.Data.Length - preload);
                    }

                    WriteUInt32(tree, Crc32(entry.Data));  // CRC
                    WriteUInt16(tree, preload);            // 预载字节数
                    WriteUInt16(tree, entry.ArchiveIndex); // 分卷号
                    WriteUInt32(tree, offset);             // 偏移
                    WriteUInt32(tree, length);             // 长度
                    WriteUInt16(tree, 0xFFFF);             // 条目终止符

                    if (preload > 0)
                        tree.Write(entry.Data, 0, preload);
                }

                tree.WriteByte(0); // 目录结束
            }

            tree.WriteByte(0); // 扩展名结束
        }

        if (writeFinalSentinel)
        {
            tree.WriteByte(0);        // 空扩展名
            WriteUInt16(tree, 0xFFFF);// 目录树终止符
        }

        var treeBytes = tree.ToArray();
        var output = new MemoryStream();

        WriteUInt32(output, 0x55AA1234);
        WriteUInt32(output, version);
        WriteUInt32(output, (uint)treeBytes.Length);
        if (version >= 2)
        {
            WriteUInt32(output, (uint)dataSection.Length); // file_data_section_size
            WriteUInt32(output, 0);                        // archive_md5_section_size
            WriteUInt32(output, 0);                        // other_md5_section_size
            WriteUInt32(output, 0);                        // signature_section_size
        }

        output.Write(treeBytes);
        dataSection.WriteTo(output);

        foreach (var pair in chunkStreams)
            chunks[pair.Key] = pair.Value.ToArray();

        return output.ToArray();
    }

    /// <summary>写出 dir 文件与全部分卷文件。</summary>
    public void WriteToDirectory(string directory, string baseName, uint version = 1, bool writeFinalSentinel = true)
    {
        Directory.CreateDirectory(directory);
        var dirPath = Path.Combine(directory, $"{baseName}_dir.vpk");
        var bytes = Build(version, out var chunks, writeFinalSentinel);
        File.WriteAllBytes(dirPath, bytes);

        foreach (var pair in chunks)
            File.WriteAllBytes(Path.Combine(directory, $"{baseName}_{pair.Key:000}.vpk"), pair.Value);
    }

    private static string ExtensionOf(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? string.Empty : path[(dot + 1)..];
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        // 真实 VPK 用单个空格表示“根目录”，不能用空字符串（空字符串是目录结束标记）
        return slash < 0 ? " " : path[..slash];
    }

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value) =>
        stream.Write(BitConverter.GetBytes(value));

    private static void WriteUInt32(Stream stream, uint value) =>
        stream.Write(BitConverter.GetBytes(value));

    /// <summary>标准 CRC32（ISO-HDLC），与 VPK 使用的算法一致。</summary>
    public static uint Crc32(byte[] data)
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }

        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
