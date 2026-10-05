namespace L4D2ModManager.Core.Services.Deployment;

/// <summary>
/// 安装载荷（payload.zip）的定位逻辑。
///
/// 为什么放在 Core：
///   · 安装程序（Setup 项目）与自检程序共用同一份实现，避免"测试的是一份、发布的是另一份"；
///   · 载荷采用**外置文件**而不是内嵌资源，是降低杀软误报的关键设计，
///     这里集中处理"找不到载荷时在哪里找、找不到怎么报错"。
/// </summary>
public static class InstallPayload
{
    /// <summary>载荷文件名。</summary>
    public const string FileName = "payload.zip";

    /// <summary>
    /// 在常见位置查找载荷：
    ///   1) 与可执行文件同级
    ///   2) 可执行文件下的 payload 子目录
    ///   3) 上一级目录（例如 artifacts\Setup\ 与 artifacts\ 并列时）
    ///   4) 当前工作目录
    /// </summary>
    public static string? Find(string? baseDirectory = null, string? currentDirectory = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;
        var currentDir = string.IsNullOrWhiteSpace(currentDirectory) ? Environment.CurrentDirectory : currentDirectory!;

        var candidates = new[]
        {
            Path.Combine(baseDir, FileName),
            Path.Combine(baseDir, "payload", FileName),
            Path.Combine(baseDir, "..", FileName),
            Path.Combine(currentDir, FileName),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // 非法路径直接跳过
            }
        }

        return null;
    }

    /// <summary>载荷是否存在。</summary>
    public static bool IsAvailable(string? baseDirectory = null) => Find(baseDirectory) != null;

    /// <summary>载荷大小（字节），不存在时返回 0。</summary>
    public static long GetSize(string? baseDirectory = null)
    {
        var path = Find(baseDirectory);
        if (path == null) return 0;

        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
