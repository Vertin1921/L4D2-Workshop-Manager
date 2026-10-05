using System.Security.Principal;
using L4D2ModManager.Core.Services.Deployment;

namespace L4D2ModManager.App.Services;

/// <summary>
/// 管理员权限（UAC 自提升）支持。
///
/// 为什么不直接在清单里写 requireAdministrator？
///   · 清单强制提权会导致程序无法以普通权限运行（自动化 / 便携 / 调试场景都不方便），
///     而且一旦被策略禁用 UAC 提升，程序会直接无法启动（错误 740）。
///   · 这里改为"启动时自动请求提权"：默认行为与 requireAdministrator 完全一致
///     （双击即弹 UAC，确认后以管理员身份运行），但可以用 --no-elevate 或设置项关闭。
/// </summary>
public static class ElevationService
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重新启动自身（会弹出 UAC 提示）。
    /// 成功启动新实例后返回 true，调用方应当立即退出当前实例。
    /// </summary>
    public static bool TryRestartElevated(IReadOnlyList<string> args, out string? error)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "无法定位程序自身路径";
            return false;
        }

        var forwarded = args
            .Where(a => !a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 标记：新实例已经尝试过提权，避免任何情况下的重复请求
        forwarded.Add("--elevated");

        return AppDeployment.TryRestartElevated(executable, forwarded, out error);
    }

    /// <summary>当前命令是否为不需要界面的无界面命令（这些命令不做提权，便于自动化）。</summary>
    public static bool IsHeadlessCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return false;

        return args[0].Trim().ToLowerInvariant() is
            "--version" or "--help" or "-h" or "--diagnose" or "--scan" or "--uninstall";
    }
}
