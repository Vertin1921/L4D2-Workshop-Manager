using System.Diagnostics;

namespace L4D2ModManager.Core.Services.Steam;

/// <summary>启动游戏的参数。</summary>
public sealed class GameLaunchOptions
{
    /// <summary>以 -insecure 启动（关闭 VAC，仅建议测试 Mod / 本地游玩）。</summary>
    public bool Insecure { get; set; }

    /// <summary>附加 -console（打开控制台）。</summary>
    public bool Console { get; set; }

    /// <summary>附加 -windowed（窗口化运行）。</summary>
    public bool Windowed { get; set; }

    /// <summary>用户自定义附加参数（设置页可填，按空格拆分，支持引号）。</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>优先通过 Steam 启动（能正确带上 Steam 覆盖层与游戏时间统计）。</summary>
    public bool PreferSteam { get; set; } = true;

    /// <summary>只计算命令行、不真正启动进程（自检与测试用）。</summary>
    public bool DryRun { get; set; }

    /// <summary>本次启动会传给游戏的参数列表。</summary>
    public List<string> ToArgumentList()
    {
        var args = new List<string>();

        if (Insecure) args.Add("-insecure");
        if (Console) args.Add("-console");
        if (Windowed) args.Add("-windowed");

        if (!string.IsNullOrWhiteSpace(ExtraArguments))
            args.AddRange(GameLauncher.SplitArguments(ExtraArguments));

        return args;
    }

    /// <summary>Steam 命令行参数：-applaunch 550 <附加参数>。</summary>
    public string ToSteamArguments()
    {
        var parts = new List<string> { "-applaunch", GameLauncher.AppId };
        parts.AddRange(ToArgumentList());
        return string.Join(' ', parts);
    }

    /// <summary>直接运行游戏可执行文件的参数。</summary>
    public string ToDirectArguments() => string.Join(' ', ToArgumentList());

    /// <summary>人类可读的说明。</summary>
    public string Describe()
    {
        var args = ToArgumentList();
        var text = Insecure ? "以 -insecure 启动（已关闭 VAC）" : "正常启动";
        if (args.Count > 0) text += "：" + string.Join(' ', args);
        return text;
    }
}

/// <summary>启动结果。</summary>
public sealed class GameLaunchResult
{
    public bool Success { get; set; }
    public bool UsedSteam { get; set; }
    public string CommandLine { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>
/// 启动 Left 4 Dead 2。
///
/// 优先用 `steam.exe -applaunch 550 &lt;参数&gt;`（Steam 会把附加参数原样传给游戏，
/// 这样既能带上 Steam 覆盖层/游戏时间，也能使用 -insecure）；
/// 找不到 Steam 时退化为直接运行 `left4dead2.exe &lt;参数&gt;`。
/// </summary>
public static class GameLauncher
{
    public const string AppId = "550";
    public const string GameFolderName = "Left 4 Dead 2";
    public const string GameExecutableName = "left4dead2.exe";
    public const string SteamExecutableName = "steam.exe";

    /// <summary>-insecure 的风险提示（启动前会让用户确认）。</summary>
    public const string InsecureWarning =
        "以 -insecure 启动会**关闭 VAC 反作弊**：\n" +
        "· 你只能进入未开启 VAC 的服务器，官方服与绝大多数联机服会拒绝你连接；\n" +
        "· 联机部分 Mod（VScript、自定义武器等）通常需要 -insecure 才能生效；\n" +
        "· 仅建议用于本地测试 Mod 或与朋友在自建服务器上玩。";

    /// <summary>按 Steam 目录结构查找 left4dead2.exe。</summary>
    public static string? FindGameExecutable(SteamPaths paths)
    {
        foreach (var candidate in EnumerateGameDirectories(paths))
        {
            if (candidate == null) continue;

            var exe = Path.Combine(candidate, GameExecutableName);
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    /// <summary>查找 steam.exe。</summary>
    public static string? FindSteamExecutable(SteamPaths paths)
    {
        if (!string.IsNullOrWhiteSpace(paths.SteamRoot))
        {
            var exe = Path.Combine(paths.SteamRoot, SteamExecutableName);
            if (File.Exists(exe)) return exe;
        }

        // 退一步：从库目录反推（<Steam>\steamapps）
        foreach (var library in paths.Libraries)
        {
            var steamapps = new DirectoryInfo(library);
            var root = steamapps.Parent;
            if (root == null) continue;

            var exe = Path.Combine(root.FullName, SteamExecutableName);
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    /// <summary>游戏安装目录（含 left4dead2.exe 的那一层）。</summary>
    public static string? FindGameDirectory(SteamPaths paths)
    {
        foreach (var candidate in EnumerateGameDirectories(paths))
        {
            if (candidate != null && Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>只计算将要执行的命令行（不启动进程）。</summary>
    public static GameLaunchResult Build(SteamPaths paths, GameLaunchOptions options)
    {
        var gameExe = FindGameExecutable(paths);
        var steamExe = options.PreferSteam ? FindSteamExecutable(paths) : null;

        if (steamExe != null)
        {
            return new GameLaunchResult
            {
                Success = true,
                UsedSteam = true,
                CommandLine = $"{steamExe} {options.ToSteamArguments()}",
            };
        }

        if (gameExe != null)
        {
            return new GameLaunchResult
            {
                Success = true,
                UsedSteam = false,
                CommandLine = $"{gameExe} {options.ToDirectArguments()}".TrimEnd(),
            };
        }

        return new GameLaunchResult
        {
            Success = false,
            Error = "没有找到 Left 4 Dead 2（left4dead2.exe）。\n" +
                    "请在「设置 → 数据位置」里确认 Steam 根目录，或先用 Steam 启动一次游戏让系统记录安装路径。",
            CommandLine = string.Empty,
        };
    }

    /// <summary>启动游戏。</summary>
    public static GameLaunchResult Launch(SteamPaths paths, GameLaunchOptions options)
    {
        var plan = Build(paths, options);
        if (!plan.Success || options.DryRun) return plan;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.UsedSteam ? FindSteamExecutable(paths) : FindGameExecutable(paths),
                Arguments = plan.UsedSteam ? options.ToSteamArguments() : options.ToDirectArguments(),
                UseShellExecute = true,
            };

            if (!plan.UsedSteam)
            {
                var gameDir = FindGameDirectory(paths);
                if (gameDir != null) startInfo.WorkingDirectory = gameDir;
            }

            Process.Start(startInfo);
            Log.Info($"已启动游戏：{plan.CommandLine}");
        }
        catch (Exception ex)
        {
            return new GameLaunchResult
            {
                Success = false,
                UsedSteam = plan.UsedSteam,
                CommandLine = plan.CommandLine,
                Error = $"启动失败：{ex.Message}",
            };
        }

        return plan;
    }

    /// <summary>按空格拆分参数，支持 "引号包裹" 的分段。</summary>
    internal static IEnumerable<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private static IEnumerable<string?> EnumerateGameDirectories(SteamPaths paths)
    {
        foreach (var dir in paths.GameDirectories)
        {
            yield return dir;
            yield return Path.Combine(dir, GameFolderName);
        }

        // 从 addons 目录反推：<...>\Left 4 Dead 2\left4dead2\addons → 上两级
        foreach (var addon in paths.AddonDirectories)
        {
            string? gameDir = null;
            try
            {
                gameDir = Directory.GetParent(addon)?.Parent?.FullName;
            }
            catch
            {
                // 非法路径直接跳过
            }

            if (!string.IsNullOrEmpty(gameDir)) yield return gameDir;
        }

        foreach (var library in paths.Libraries)
            yield return Path.Combine(library, "common", GameFolderName);
    }
}
