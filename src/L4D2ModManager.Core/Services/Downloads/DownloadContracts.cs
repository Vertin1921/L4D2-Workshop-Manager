using System.IO.Compression;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>下载进度报告。</summary>
public sealed record DownloadProgress(long Downloaded, long Total, double SpeedBps, string? Detail);

/// <summary>单个下载通道的执行结果。</summary>
public sealed class DownloadResult
{
    public bool Success { get; init; }

    /// <summary>最终落盘的 .vpk 路径。</summary>
    public string? FilePath { get; init; }

    public string? Error { get; init; }

    /// <summary>true 表示该通道不可用是永久性的（例如未安装 steamcmd），不再重试。</summary>
    public bool Permanent { get; init; }

    public static DownloadResult Ok(string filePath) => new() { Success = true, FilePath = filePath };

    public static DownloadResult Fail(string error, bool permanent = false) =>
        new() { Error = error, Permanent = permanent };
}

/// <summary>创意工坊下载通道抽象。程序内置三种通道，按顺序尝试。</summary>
public interface IWorkshopDownloadProvider
{
    string Name { get; }

    string Description { get; }

    bool CanHandle(DownloadTask task, WorkshopItemInfo info);

    Task<DownloadResult> DownloadAsync(
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>下载落盘辅助：识别 VPK / ZIP，生成不冲突的目标文件名。</summary>
public static class DownloadFileHelper
{
    private const uint VpkSignature = 0x55AA1234;

    public static bool IsVpkFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[4];
            if (stream.Read(buffer) != 4) return false;
            return BitConverter.ToUInt32(buffer) == VpkSignature;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsZipFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[4];
            if (stream.Read(buffer) != 4) return false;
            return buffer[0] == 0x50 && buffer[1] == 0x4B &&
                   (buffer[2] == 0x03 || buffer[2] == 0x05 || buffer[2] == 0x07);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>决定 .vpk 的文件名：优先工坊原始文件名，其次 &lt;id&gt;.vpk。</summary>
    public static string ChooseVpkFileName(WorkshopItemInfo info, DownloadTask task)
    {
        var name = info.FileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(name)!.Trim();
            if (name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return name;
            if (!string.IsNullOrEmpty(Path.GetExtension(name))) return Path.ChangeExtension(name, ".vpk");
        }

        var id = task.WorkshopId ?? info.PublishedFileId;
        return string.IsNullOrWhiteSpace(id) ? $"workshop_{task.Id}.vpk" : $"{id}.vpk";
    }

    /// <summary>生成不覆盖已有文件的目标路径。</summary>
    public static string UniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            target = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// 把下载完成的临时文件放到目标 Mod 目录：
    ///   · VPK → 直接改名放入
    ///   · ZIP → 解压其中的 .vpk
    ///   · 其他 → 按原始文件名放入（扫描阶段会再判断）
    /// </summary>
    public static string PromoteFile(string partPath, DownloadTask task, WorkshopItemInfo info)
    {
        var destination = string.IsNullOrWhiteSpace(task.DestinationDirectory)
            ? AppPaths.DownloadTempDir
            : task.DestinationDirectory;

        Directory.CreateDirectory(destination);

        if (IsVpkFile(partPath))
        {
            var target = UniquePath(destination, ChooseVpkFileName(info, task));
            File.Move(partPath, target);
            return target;
        }

        if (IsZipFile(partPath))
        {
            var extracted = new List<string>();
            using (var archive = System.IO.Compression.ZipFile.OpenRead(partPath))
            {
                var vpkEntries = archive.Entries
                    .Where(e => e.Name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var entries = vpkEntries.Count > 0
                    ? vpkEntries
                    : archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

                foreach (var entry in entries)
                {
                    var target = UniquePath(destination, Path.GetFileName(entry.Name));
                    entry.ExtractToFile(target, overwrite: true);
                    extracted.Add(target);
                }
            }

            try
            {
                File.Delete(partPath);
            }
            catch
            {
                // 忽略
            }

            if (extracted.Count == 0)
                throw new InvalidOperationException("下载的压缩包中没有可用的文件");

            return extracted[0];
        }

        var fallbackName = !string.IsNullOrWhiteSpace(info.FileName)
            ? Path.GetFileName(info.FileName)!
            : ChooseVpkFileName(info, task);

        var fallback = UniquePath(destination, fallbackName);
        File.Move(partPath, fallback);
        return fallback;
    }
}
