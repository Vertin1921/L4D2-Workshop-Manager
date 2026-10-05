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
