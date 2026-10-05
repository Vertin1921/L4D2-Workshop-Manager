using System.Collections.ObjectModel;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Workshop;

namespace L4D2ModManager.App.ViewModels;

/// <summary>
/// 创意工坊页面：在程序内部（WebView2）浏览 Steam 创意工坊，
/// 顶部固定「下载 Mod」按钮，解析当前页面 ID 后加入下载队列。
/// </summary>
public sealed class WorkshopViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;

    private string _addressText = AppInfo.WorkshopHomeUrl;
    private string _searchText = string.Empty;
    private string _currentUrl = AppInfo.WorkshopHomeUrl;
    private string _statusText = "正在启动内置浏览器…";
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _browserReady;
    private bool _isFetching;
    private WorkshopItemInfo? _currentItem;
    private string _manualId = string.Empty;
    private string? _lastFetchedId;

    public WorkshopViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        HomeCommand = new RelayCommand(_ => NavigateRequested?.Invoke(AppInfo.WorkshopHomeUrl));
        FetchWorkshopMetadataCommand = new AsyncRelayCommand(FetchWorkshopMetadataAsync, () => !IsLoading,
            ex => _services.Dialogs.Error(ex.Message, "抓取工坊信息失败", ex.ToString()));
        BackCommand = new RelayCommand(_ => BackRequested?.Invoke(), () => CanGoBack);
        ForwardCommand = new RelayCommand(_ => ForwardRequested?.Invoke(), () => CanGoForward);
        ReloadCommand = new RelayCommand(_ => ReloadRequested?.Invoke());
        GoCommand = new RelayCommand(_ => NavigateTo(AddressText));
        SearchCommand = new RelayCommand(_ => NavigateTo(AppInfo.BuildSearchUrl(SearchText)));
        BrowseTrendCommand = new RelayCommand(_ => NavigateTo(AppInfo.BuildSearchUrl(null, "trend")));
        BrowseRecentCommand = new RelayCommand(_ => NavigateTo(AppInfo.BuildSearchUrl(null, "mostrecent")));
        BrowseTopRatedCommand = new RelayCommand(_ => NavigateTo(AppInfo.BuildSearchUrl(null, "toprated")));
        FetchInfoCommand = new AsyncRelayCommand(FetchCurrentInfoAsync, () => !IsFetching, ex => ReportError("获取工坊信息失败", ex));
        DownloadCommand = new AsyncRelayCommand(DownloadCurrentAsync, () => !IsFetching, ex => ReportError("下载失败", ex));
        DownloadManyCommand = new AsyncRelayCommand(DownloadManyAsync, () => !IsFetching, ex => ReportError("批量下载失败", ex));
        OpenInBrowserCommand = new RelayCommand(_ => OpenInSystemBrowser());
        ClearInfoCommand = new RelayCommand(_ => CurrentItem = null);
    }

    public event Action<string>? NavigateRequested;

    private Func<string, Task<string?>>? _scriptRunner;

    /// <summary>由 View 注入：在页面里执行 JavaScript。同时写入共享服务，供 Mod 管理页复用。</summary>
    public Func<string, Task<string?>>? ScriptRunner
    {
        get => _scriptRunner;
        set
        {
            _scriptRunner = value;
            _services.RunWebScript = value;
        }
    }

    /// <summary>批量抓取工坊缩略图与标签（走内嵌浏览器，用当前可用的网络）。</summary>
    public AsyncRelayCommand FetchWorkshopMetadataCommand { get; }

    public event Action? BackRequested;

    public event Action? ForwardRequested;

    public event Action? ReloadRequested;

    public RelayCommand HomeCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand GoCommand { get; }

    public RelayCommand SearchCommand { get; }

    public RelayCommand BrowseTrendCommand { get; }

    public RelayCommand BrowseRecentCommand { get; }

    public RelayCommand BrowseTopRatedCommand { get; }

    public AsyncRelayCommand FetchInfoCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public AsyncRelayCommand DownloadManyCommand { get; }

    public RelayCommand OpenInBrowserCommand { get; }

    public RelayCommand ClearInfoCommand { get; }

    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    private sealed class WorkshopMeta
    {
        public string Id { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// 通过内嵌浏览器批量抓取工坊物品页的缩略图与标签。
    /// 官方 API（api.steampowered.com）在部分网络下不可达，而市面上的工坊网页能正常打开，
    /// 所以改成在页面内做同源 fetch 取 HTML，再由宿主解析 og:image 与标签。
    /// </summary>
    private async Task FetchWorkshopMetadataAsync()
    {
        if (ScriptRunner == null)
        {
            _services.Dialogs.Error("内嵌浏览器还没有准备好，请先打开一次创意工坊页面。", "抓取工坊信息");
            return;
        }

        var library = _services.Library;
        var targets = library.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.WorkshopId))
            .GroupBy(m => m.WorkshopId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();

        if (targets.Count == 0)
        {
            _services.Dialogs.Info("没有找到带创意工坊 ID 的 Mod。", "抓取工坊信息");
            return;
        }

        if (!_services.Dialogs.Confirm(
                $"将为 {targets.Count} 个创意工坊 Mod 抓取缩略图与标签。\r\n\r\n" +
                "方式：在程序内的工坊页面里逐个读取工坊物品页（用你当前能打开工坊的网络）。\r\n" +
                "每批 5 个，可能需要几分钟；抓到的标签会用来重新判定 Mod 类型。\r\n\r\n开始吗？",
                "抓取工坊缩略图与标签", null, "开始抓取"))
        {
            return;
        }

        IsLoading = true;
        int imageOk = 0, tagOk = 0, failed = 0;

        try
        {
            // 先回到工坊首页，保证后面的 fetch 处于 steamcommunity.com 同源上下文
            NavigateRequested?.Invoke(AppInfo.WorkshopHomeUrl);
            await Task.Delay(2500).ConfigureAwait(true);

            for (int index = 0; index < targets.Count; index += 5)
            {
                var batch = targets.Skip(index).Take(5).ToList();
                StatusText = $"正在抓取工坊信息：{index}/{targets.Count}…";

                var json = await ScriptRunner(BuildMetadataScript(batch)).ConfigureAwait(true);
                var metas = ParseMetadata(json);

                if (metas.Count == 0)
                {
                    failed += batch.Count;
                    continue;
                }

                foreach (var meta in metas)
                {
                    var item = library.Mods.FirstOrDefault(m =>
                        string.Equals(m.WorkshopId, meta.Id, StringComparison.OrdinalIgnoreCase));
                    if (item == null) continue;

                    if (!string.IsNullOrWhiteSpace(meta.Image))
                    {
                        var path = await library.Thumbnails.DownloadPreviewAsync(item, meta.Image).ConfigureAwait(true);
                        if (path != null)
                        {
                            item.ThumbnailPath = path;
                            imageOk++;
                        }
                    }

                    if (meta.Tags.Count > 0)
                    {
                        item.Tags = string.Join(", ", meta.Tags);
                        item.Category = CategoryClassifier.Classify(
                            item.DisplayName, item.FileIndex, item.Tags, item.Description);
                        tagOk++;
                    }
                }

                await Task.Delay(300).ConfigureAwait(true);
            }

            library.SaveDatabase();
            _services.Thumbnails.Clear();

            StatusText = $"抓取完成：缩略图 {imageOk} 个，标签 {tagOk} 个，失败 {failed} 个。到「Mod 管理」点刷新即可看到。";
            _services.Dialogs.Info(StatusText, "抓取工坊信息");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>在页面里批量取 og:image 与标签，返回 JSON 字符串。</summary>
    private static string BuildMetadataScript(IReadOnlyList<string> ids)
    {
        var list = string.Join(",", ids.Select(id => "\"" + id + "\""));

        return
            "(async () => { const ids = [" + list + "]; const out = [];" +
            " for (const id of ids) {" +
            "  try {" +
            "   const r = await fetch('https://steamcommunity.com/sharedfiles/filedetails/?id=' + id, { credentials: 'include' });" +
            "   const html = await r.text();" +
            "   let og = html.match(/<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"']([^\"']+)[\"']/i);" +
            "   if (!og) { og = html.match(/<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+property=[\"']og:image[\"']/i); }" +
            "   const tags = [];" +
            "   const re = /workshop\\/taglist\\/\\?tag=([^\"'&]+)/gi; let t;" +
            "   while ((t = re.exec(html)) !== null) { const v = decodeURIComponent(t[1]); if (tags.indexOf(v) < 0) { tags.push(v); } }" +
            "   out.push({ id: id, image: og ? og[1] : '', tags: tags });" +
            "  } catch (e) { out.push({ id: id, image: '', tags: [] }); }" +
            " }" +
            " return JSON.stringify(out); })()";
    }

    private static List<WorkshopMeta> ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<WorkshopMeta>();

        try
        {
            var inner = json.Trim();
            if (inner.StartsWith("\"", StringComparison.Ordinal))
            {
                inner = System.Text.Json.JsonSerializer.Deserialize<string>(inner) ?? inner;
            }

            return System.Text.Json.JsonSerializer.Deserialize<List<WorkshopMeta>>(
                       inner,
                       new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<WorkshopMeta>();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析工坊抓取结果失败：{ex.Message}");
            return new List<WorkshopMeta>();
        }
    }

    /// <summary>
    /// 在程序内部的创意工坊页面打开某个工坊物品（供 Mod 列表「查看详情」跳转使用）。
    /// </summary>
    public void OpenWorkshopItem(string workshopIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(workshopIdOrUrl)) return;

        var url = workshopIdOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? workshopIdOrUrl
            : Core.Models.WorkshopItemInfo.BuildPageUrl(workshopIdOrUrl.Trim());

        CurrentUrl = url;
        StatusText = "正在打开工坊页面：" + url;
        NavigateRequested?.Invoke(url);
    }

    /// <summary>手动输入工坊 ID / 链接。</summary>
    public string ManualId
    {
        get => _manualId;
        set
        {
            if (Set(ref _manualId, value)) Raise(nameof(ManualHint));
        }
    }

    public string ManualHint
    {
        get
        {
            var id = SteamWorkshopClient.ParseWorkshopId(ManualId);
            return string.IsNullOrWhiteSpace(ManualId)
                ? "可直接粘贴工坊链接或 ID"
                : id == null
                    ? "无法识别该输入"
                    : $"识别到工坊 ID：{id}";
        }
    }

    public string CurrentUrl
    {
        get => _currentUrl;
        private set
        {
            if (Set(ref _currentUrl, value)) Raise(nameof(CurrentIdText), nameof(CanDownloadCurrent));
        }
    }

    /// <summary>当前页面对应的工坊 ID（如果不是工坊物品页则为 null）。</summary>
    public string? CurrentWorkshopId => SteamWorkshopClient.ParseWorkshopId(CurrentUrl);

    public string CurrentIdText => CurrentWorkshopId == null
        ? "当前页面不是工坊物品页"
        : $"当前物品 ID：{CurrentWorkshopId}";

    public bool CanDownloadCurrent => CurrentWorkshopId != null;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set
        {
            if (Set(ref _canGoBack, value)) BackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set
        {
            if (Set(ref _canGoForward, value)) ForwardCommand.RaiseCanExecuteChanged();
        }
    }

    public bool BrowserReady
    {
        get => _browserReady;
        private set => Set(ref _browserReady, value);
    }

    public bool IsFetching
    {
        get => _isFetching;
        private set
        {
            if (!Set(ref _isFetching, value)) return;
            FetchInfoCommand.RaiseCanExecuteChanged();
            DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public WorkshopItemInfo? CurrentItem
    {
        get => _currentItem;
        private set
        {
            if (!Set(ref _currentItem, value)) return;
            Raise(nameof(HasItem), nameof(InfoTitle), nameof(InfoAuthor), nameof(InfoSize),
                  nameof(InfoDescription), nameof(InfoTags), nameof(InfoStats), nameof(InfoUrl),
                  nameof(DownloadButtonText));
            DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasItem => CurrentItem != null;

    public string InfoTitle => CurrentItem?.Title ?? string.Empty;

    public string InfoAuthor => CurrentItem == null ? string.Empty : "作者：" + CurrentItem.AuthorText;

    public string InfoSize => CurrentItem == null ? string.Empty : "大小：" + CurrentItem.SizeText;

    public string InfoDescription => CurrentItem?.ShortDescription ?? string.Empty;

    public string InfoTags => CurrentItem == null ? string.Empty : "标签：" + CurrentItem.TagText;

    public string InfoStats => CurrentItem == null
        ? string.Empty
        : $"订阅 {CurrentItem.Subscriptions:N0} · 收藏 {CurrentItem.Favorited:N0} · 浏览 {CurrentItem.Views:N0}";

    public string InfoUrl => CurrentItem?.PageUrl ?? string.Empty;

    public string DownloadButtonText => CurrentItem == null ? "下载 Mod" : "下载此 Mod";

    /// <summary>下载目标目录（可在设置中修改）。</summary>
    public string DownloadDirectoryText => _library.ResolveDownloadDirectory();

    /// <summary>WebView2 初始化完成。</summary>
    public void SetBrowserReady(bool ready, string? detail = null)
    {
        BrowserReady = ready;
        StatusText = ready ? "已就绪，可在下方浏览创意工坊" : detail ?? "内置浏览器不可用";
    }

    /// <summary>页面导航完成时由视图调用。</summary>
    public void OnNavigated(string url, bool canGoBack, bool canGoForward)
    {
        CurrentUrl = url;
        AddressText = url;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        IsLoading = false;
        _library.Config.LastWorkshopUrl = url;

        // 打开工坊物品页时自动拉取信息（300ms 去抖），这样右侧面板与下载按钮立即就绪
        var id = CurrentWorkshopId;
        if (!string.IsNullOrWhiteSpace(id) && id != _lastFetchedId)
        {
            _lastFetchedId = id;
            StatusText = $"已进入工坊物品 {id}，正在获取信息…";
            _ = AutoFetchAsync(id);
        }
        else if (string.IsNullOrWhiteSpace(id))
        {
            StatusText = url;
        }
    }

    private async Task AutoFetchAsync(string id)
    {
        try
        {
            // 简单去抖：连续快速点击多个物品时只保留最后一次
            await Task.Delay(300).ConfigureAwait(true);
            if (CurrentWorkshopId != id) return;

            await FetchInfoAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"自动获取工坊信息失败：{ex.Message}");
        }
    }

    public void OnNavigationStarting(string url)
    {
        IsLoading = true;
        StatusText = "正在加载：" + url;
    }

    public void ReportBrowserError(string message) => StatusText = message;

    private void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.TrimStart('/');

        NavigateRequested?.Invoke(url);
    }

    /// <summary>获取当前页面（或手动输入）的工坊信息。</summary>
    public async Task FetchCurrentInfoAsync()
    {
        var id = SteamWorkshopClient.ParseWorkshopId(ManualId) ?? CurrentWorkshopId;
        if (string.IsNullOrWhiteSpace(id))
        {
            _services.Dialogs.Info("请先打开一个工坊物品页面，或在输入框中粘贴工坊链接 / ID。", "无法确定工坊 ID");
            return;
        }

        IsFetching = true;
        await FetchInfoAsync(id).ConfigureAwait(true);
    }

    /// <summary>按 ID 获取工坊信息（内部共用实现）。</summary>
    private async Task FetchInfoAsync(string id)
    {
        IsFetching = true;
        StatusText = $"正在获取工坊信息（{id}）…";
        try
        {
            var result = await _library.Workshop.GetDetailsAsync(new[] { id }).ConfigureAwait(true);
            var item = result.First;

            if (item == null)
            {
                // 不弹窗打断：下载时还有订阅缓存 / steamcmd 两条通道可用，状态栏提示即可
                CurrentItem = null;
                StatusText = result.Error ?? "未获取到条目信息（可能网络无法访问 Steam 接口）";
                return;
            }

            CurrentItem = item;
            StatusText = item.Available
                ? $"已获取：{item.Title}"
                : item.Error ?? "该条目不可用";
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>把当前条目加入下载队列（接口不可用时也会用 ID 直接入队，走订阅缓存 / steamcmd 通道）。</summary>
    public async Task DownloadCurrentAsync()
    {
        var id = SteamWorkshopClient.ParseWorkshopId(ManualId) ?? CurrentWorkshopId;
        if (string.IsNullOrWhiteSpace(id))
        {
            _services.Dialogs.Info(
                "当前页面不是创意工坊物品页。\r\n\r\n" +
                "请先在上方的内置浏览器里打开一个 Mod 页面（地址形如 .../filedetails/?id=123456789），" +
                "或直接在右侧输入框粘贴工坊链接 / ID，然后再次点击「下载 Mod」。",
                "还没有选中 Mod");
            return;
        }

        // 若当前页面就是该条目且信息已就绪则直接入队，否则先尝试获取信息
        WorkshopItemInfo? item = CurrentItem;
        if (item == null || !string.Equals(item.PublishedFileId, id, StringComparison.OrdinalIgnoreCase))
        {
            await FetchInfoAsync(id).ConfigureAwait(true);
            item = CurrentItem;
        }

        var directory = _library.ResolveDownloadDirectory();

        if (item == null || !string.Equals(item.PublishedFileId, id, StringComparison.OrdinalIgnoreCase))
        {
            // 接口拿不到信息（被墙 / 限流 / 条目刚发布）也不阻塞下载：
            // 订阅缓存与 steamcmd 两条通道只需要 ID。
            var task = _library.Downloads.EnqueueById(id, directory, null, WorkshopItemInfo.BuildPageUrl(id));
            StatusText = $"工坊接口不可用，已按 ID {id} 加入下载队列（改用订阅缓存 / steamcmd 通道）";
            _services.Dialogs.Info(
                $"未能从 Steam 接口获取信息，已直接按工坊 ID 加入下载队列。\r\n\r\n" +
                $"工坊 ID：{id}\r\n保存到：{directory}\r\n\r\n" +
                "程序会依次尝试：\r\n" +
                "  1. Steam UGC 直链（需要接口信息）\r\n" +
                "  2. Steam 订阅缓存（在 Steam 里订阅过就能直接复制）\r\n" +
                "  3. steamcmd\r\n\r\n" +
                "如果都失败：在 Steam 中订阅该 Mod，再回到「下载管理」点「重试」。",
                "已加入下载队列");
            _ = task;
            return;
        }

        if (item!.Available == false)
        {
            _services.Dialogs.Error(item.Error ?? "该条目不可用", "无法下载");
            return;
        }

        _library.Downloads.Enqueue(item, directory);

        StatusText = $"已加入下载队列：{item.Title} → {directory}";
        _services.Dialogs.Info(
            $"已加入下载队列。\r\n\r\nMod：{item.Title}\r\n工坊 ID：{item.PublishedFileId}\r\n保存到：{directory}\r\n\r\n" +
            "可切换到「下载管理」查看进度。若直链下载失败，程序会自动尝试 Steam 订阅缓存与 steamcmd。",
            "开始下载");
    }

    /// <summary>
    /// 批量下载（合集 / 当前页面上的所有物品）。
    /// 由视图从页面 DOM 提取 ID 后调用（见 WorkshopView 的「下载合集」按钮）。
    /// </summary>
    public async Task DownloadManyAsync()
    {
        var ids = PendingBatchIds;
        if (ids == null || ids.Count == 0)
        {
            _services.Dialogs.Info(
                "没有从当前页面找到可下载的工坊物品。\r\n\r\n" +
                "「下载合集」会抓取页面上所有物品链接（合集页通常包含几十上百个 Mod）；" +
                "物品详情页请直接用「下载 Mod」。",
                "没有可下载的物品");
            return;
        }

        var directory = _library.ResolveDownloadDirectory();
        var available = Math.Min(ids.Count, 200);
        var targetIds = ids.Take(available).ToList();

        IsFetching = true;
        StatusText = $"正在获取 {targetIds.Count} 个物品的信息…";
        try
        {
            // 一次接口请求拿到全部条目信息（接口单次上限 100，分批处理）
            var known = new Dictionary<string, WorkshopItemInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in targetIds.Chunk(100))
            {
                var result = await _library.Workshop.GetDetailsAsync(chunk).ConfigureAwait(true);
                foreach (var item in result.Items)
                    known[item.PublishedFileId] = item;
            }

            var queued = 0;
            foreach (var id in targetIds)
            {
                if (known.TryGetValue(id, out var info) && info.Available)
                {
                    _library.Downloads.Enqueue(info, directory, info.PageUrl);
                    queued++;
                }
                else
                {
                    // 接口没有返回（或不可用）时仍然按 ID 入队，交给订阅缓存 / steamcmd
                    _library.Downloads.EnqueueById(id, directory, null, WorkshopItemInfo.BuildPageUrl(id));
                    queued++;
                }
            }

            StatusText = $"已加入下载队列：{queued} 个物品（共发现 {ids.Count} 个）";
            PendingBatchIds = null;
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>视图从页面 DOM 提取到的待下载 ID 列表。</summary>
    public IReadOnlyList<string>? PendingBatchIds { get; set; }

    /// <summary>请求视图执行脚本抓取当前页面上的工坊 ID（由视图实现）。</summary>
    public event Func<Task<IReadOnlyList<string>>>? CollectWorkshopIdsRequested;

    /// <summary>供视图回调：把抓取到的 ID 交给视图模型。</summary>
    public async Task CollectAndDownloadAsync()
    {
        var handler = CollectWorkshopIdsRequested;
        if (handler == null) return;

        try
        {
            PendingBatchIds = await handler().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"抓取页面物品失败：{ex.Message}");
            PendingBatchIds = null;
        }

        await DownloadManyAsync().ConfigureAwait(true);
    }

    private void OpenInSystemBrowser()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CurrentUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportError("打开浏览器失败", ex);
        }
    }

    private void ReportError(string title, Exception exception)
    {
        Log.Error(title, exception);
        StatusText = $"{title}：{exception.Message}";
        _services.Dialogs.Error(exception.Message, title, exception.ToString());
    }
}
