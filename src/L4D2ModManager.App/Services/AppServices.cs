using System.Windows;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.Services;

/// <summary>
/// 应用服务容器（组合根）。所有 UI 通过这里拿到 Core 服务，
/// 避免各处 new 出多个实例。
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices(Func<Window?> ownerProvider)
    {
        Dialogs = new DialogService(ownerProvider);
        Library = new ModLibraryService();
        Thumbnails = new ThumbnailLoader(Library);
    }

    public ModLibraryService Library { get; }

    public DialogService Dialogs { get; }

    public ThumbnailLoader Thumbnails { get; }

    /// <summary>
    /// 请求在程序内部的创意工坊页面打开某个工坊物品（由 MainViewModel 接管：
    /// 切换到创意工坊页 + 让内嵌浏览器导航过去）。为 null 时调用方回退到其它方式。
    /// </summary>
    public Action<string>? ShowWorkshopItem { get; set; }

    /// <summary>
    /// 在创意工坊页面里执行 JavaScript（由 WorkshopView 在 WebView2 就绪后注入）。
    /// Mod 管理页抓取缩略图/标签时复用它——这样复用的正是"能打开工坊"的那条网络通道。
    /// </summary>
    public Func<string, Task<string?>>? RunWebScript { get; set; }

    /// <summary>页面脚本通道就绪（首次进入创意工坊页）时触发，用于自动开始抓取缩略图。</summary>
    public Action? ThumbnailChannelReady { get; set; }

    /// <summary>初始化：创建数据目录、载入配置与数据库、探测 Steam 路径。</summary>
    public void Initialize()
    {
        AppPaths.EnsureCreated();
        Library.Initialize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Library.Dispose();
    }
}
