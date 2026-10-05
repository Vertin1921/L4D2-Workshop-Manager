using L4D2ModManager.Core.Services.Vpk;
using Microsoft.Win32;

namespace L4D2ModManager.Core.Services.Steam;

/// <summary>检测结果。</summary>
public sealed class SteamPaths
{
    public string? SteamRoot { get; set; }
    public string? SteamCmdPath { get; set; }

    /// <summary>全部 Steam 库目录。</summary>
    public List<string> Libraries { get; set; } = new();

    /// <summary>发现的 L4D2 addons 目录（left4dead2\addons）。</summary>
    public List<string> AddonDirectories { get; set; } = new();

    /// <summary>发现的创意工坊内容目录（steamapps\workshop\content\550）。</summary>
    public List<string> WorkshopDirectories { get; set; } = new();

    /// <summary>发现的 L4D2 安装目录。</summary>
    public List<string> GameDirectories { get; set; } = new();

    public bool FoundAnything => AddonDirectories.Count > 0 || WorkshopDirectories.Count > 0 || Libraries.Count > 0;
}

/// <summary>
/// Steam 路径探测：
///   · 注册表 HKCU\Software\Valve\Steam\SteamPath / HKLM\...\Valve\Steam\InstallPath
///   · steamapps\libraryfolders.vdf（新旧两种格式）
///   · 常见默认安装位置
/// </summary>
public static class SteamLibraryLocator
{
    /// <summary>Left 4 Dead 2 的 Steam AppId。</summary>
    public const int L4D2AppId = 550;

    private const string GameFolderRelative = @"steamapps\common\Left 4 Dead 2";
    private const string AddonsRelative = @"left4dead2\addons";

    public static SteamPaths Detect()
    {
        var result = new SteamPaths();
        var root = FindSteamRoot();
        result.SteamRoot = root;

        var libraries = new List<string>();
        if (!string.IsNullOrWhiteSpace(root))
        {
            AddLibrary(libraries, root);
            AddLibraryFromVdf(libraries, Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            result.SteamCmdPath = FindSteamCmd(root);
        }

        // 常见默认位置兜底
        foreach (var guess in GuessLibraryPaths())
        {
            AddLibrary(libraries, guess);
            AddLibraryFromVdf(libraries, Path.Combine(guess, "steamapps", "libraryfolders.vdf"));
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.Libraries.Add(library);

            var addons = Path.Combine(library, @"steamapps\common\Left 4 Dead 2\left4dead2\addons");
            if (Directory.Exists(addons) && !result.AddonDirectories.Contains(addons, StringComparer.OrdinalIgnoreCase))
                result.AddonDirectories.Add(addons);

            var gameDir = Path.Combine(library, GameFolderRelative);
            if (Directory.Exists(gameDir) && !result.GameDirectories.Contains(gameDir, StringComparer.OrdinalIgnoreCase))
                result.GameDirectories.Add(gameDir);

            var workshop = Path.Combine(library, @"steamapps\workshop\content", L4D2AppId.ToString());
            if (Directory.Exists(workshop) && !result.WorkshopDirectories.Contains(workshop, StringComparer.OrdinalIgnoreCase))
                result.WorkshopDirectories.Add(workshop);
        }

        Log.Info($"Steam 探测：根目录={result.SteamRoot ?? "(未找到)"}，库={result.Libraries.Count}，addons={result.AddonDirectories.Count}，workshop={result.WorkshopDirectories.Count}");
        return result;
    }

    public static string? FindSteamRoot()
    {
        string[] valueNames = { "SteamPath", "InstallPath" };

        (RegistryKey Root, string SubKey)[] locations =
        {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
        };

        foreach (var (root, subKey) in locations)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key == null) continue;

                foreach (var valueName in valueNames)
                {
                    if (key.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
                    {
                        // 注册表里可能是正斜杠
                        path = path.Replace('/', '\\').TrimEnd('\\');
                        if (Directory.Exists(path)) return path;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 Steam 注册表失败 ({subKey}): {ex.Message}");
            }
        }

        foreach (var guess in GuessLibraryPaths())
        {
            if (File.Exists(Path.Combine(guess, "steam.exe")) || Directory.Exists(Path.Combine(guess, "steamapps")))
                return guess;
        }

        return null;
    }

    /// <summary>解析 libraryfolders.vdf（兼容新格式 "path" 键与旧格式 "1" "D:\\..."）。</summary>
    public static void AddLibraryFromVdf(List<string> libraries, string vdfPath)
    {
        try
        {
            if (!File.Exists(vdfPath)) return;
            var text = File.ReadAllText(vdfPath);
            var root = KeyValuesParser.Parse(text);
            if (root == null) return;

            CollectPaths(root, libraries);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 libraryfolders.vdf 失败: {vdfPath} -> {ex.Message}");
        }
    }

    private static void CollectPaths(KeyValuesNode node, List<string> libraries)
    {
        foreach (var child in node.Children)
        {
            if (string.Equals(child.Name, "path", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(child.Value))
            {
                AddLibrary(libraries, child.Value);
            }
            else if (int.TryParse(child.Name, out _) && !string.IsNullOrWhiteSpace(child.Value) &&
                     LooksLikePath(child.Value))
            {
                // 旧格式： "1" "D:\\SteamLibrary"
                AddLibrary(libraries, child.Value);
            }

            if (child.Children.Count > 0)
                CollectPaths(child, libraries);
        }
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(':') || value.Contains('\\') || value.Contains('/');

    private static void AddLibrary(List<string> libraries, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (!Directory.Exists(normalized)) return;
        if (!libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            libraries.Add(normalized);
    }

    public static string? FindSteamCmd(string? steamRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            candidates.Add(Path.Combine(steamRoot, "steamcmd", "steamcmd.exe"));
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "SteamCMD", "steamcmd.exe"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "Steam", "steamcmd", "steamcmd.exe"));

        foreach (var baseDir in new[] { @"C:\steamcmd", @"D:\steamcmd", @"C:\SteamCMD", @"D:\SteamCMD" })
            candidates.Add(Path.Combine(baseDir, "steamcmd.exe"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "steamcmd", "steamcmd.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> GuessLibraryPaths()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";
        yield return @"C:\Steam";
        yield return @"C:\SteamLibrary";
        yield return @"D:\Steam";
        yield return @"D:\SteamLibrary";
        yield return @"D:\Program Files (x86)\Steam";
        yield return @"E:\Steam";
        yield return @"E:\SteamLibrary";
        yield return @"F:\Steam";
        yield return @"F:\SteamLibrary";

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            yield return Path.Combine(drive.RootDirectory.FullName, "SteamLibrary");
            yield return Path.Combine(drive.RootDirectory.FullName, "Games", "SteamLibrary");
            yield return Path.Combine(drive.RootDirectory.FullName, "Steam");
        }
    }
}
