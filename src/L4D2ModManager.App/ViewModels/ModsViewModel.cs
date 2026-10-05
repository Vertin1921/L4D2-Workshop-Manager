using System.Collections.ObjectModel;
using System.Diagnostics;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Workshop;

namespace L4D2ModManager.App.ViewModels;

public sealed record SortOption(ModSortMode Mode, string Text);

public sealed record CategoryOption(ModCategory? Category, string Text);

public sealed record StateOption(bool? Enabled, string Text);

/// <summary>Mod 列表页面：搜索、排序、过滤、批量启停、删除、下载链接。</summary>
public sealed class ModsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;
    private readonly Dictionary<string, ModItemViewModel> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _thumbnailCts;

    private string _searchText = string.Empty;
    private SortOption _selectedSort;
    private CategoryOption _selectedCategory;
    private StateOption _selectedState;
    private bool _showOnlyConflicts;
    private bool _isBusy;
    private string _progressText = string.Empty;
    private string _linkInput = string.Empty;
    private int _selectedCount;
    private bool _updatingSelection;

    public ModsViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        SortOptions = new List<SortOption>
        {
            new(ModSortMode.Default, ModQuery.DescribeSortMode(ModSortMode.Default)),
            new(ModSortMode.NameAsc, ModQuery.DescribeSortMode(ModSortMode.NameAsc)),
            new(ModSortMode.NameDesc, ModQuery.DescribeSortMode(ModSortMode.NameDesc)),
            new(ModSortMode.RecentlyAdded, ModQuery.DescribeSortMode(ModSortMode.RecentlyAdded)),
            new(ModSortMode.EarliestAdded, ModQuery.DescribeSortMode(ModSortMode.EarliestAdded)),
            new(ModSortMode.SizeDesc, ModQuery.DescribeSortMode(ModSortMode.SizeDesc)),
            new(ModSortMode.SizeAsc, ModQuery.DescribeSortMode(ModSortMode.SizeAsc)),
            new(ModSortMode.Category, ModQuery.DescribeSortMode(ModSortMode.Category)),
        };
        _selectedSort = SortOptions[0];

        var categories = new List<CategoryOption> { new(null, "全部分类") };
        categories.AddRange(ModCategoryInfo.All.Select(c => new CategoryOption(c, ModCategoryInfo.Full(c))));
        Categories = categories;
        _selectedCategory = Categories[0];

        States = new List<StateOption>
        {
            new(null, "全部"),
            new(true, "已启用"),
            new(false, "已禁用"),
        };
        _selectedState = States[0];

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy, ex => ReportError("扫描失败", ex));
        EnableAllCommand = new RelayCommand(_ => RunStateChange(() => _library.EnableAll(), "全部启用"));
        DisableAllCommand = new RelayCommand(_ => RunStateChange(() => _library.DisableAll(), "全部禁用"));
        EnableListCommand = new RelayCommand(_ => RunStateChange(() => _library.SetState(VisibleModelItems(), ModState.Enabled), "启用当前列表"));
        DisableListCommand = new RelayCommand(_ => RunStateChange(() => _library.SetState(VisibleModelItems(), ModState.Disabled), "禁用当前列表"));
        EnableSelectedCommand = new RelayCommand(_ => RunStateChange(() => _library.SetState(SelectedModelItems(), ModState.Enabled), "启用所选"));
        DisableSelectedCommand = new RelayCommand(_ => RunStateChange(() => _library.SetState(SelectedModelItems(), ModState.Disabled), "禁用所选"));
        RefreshCommand = new RelayCommand(_ => Refresh());
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
        AddLinkCommand = new AsyncRelayCommand(AddFromLinkAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(LinkInput), ex => ReportError("添加 Mod 失败", ex));
        SelectAllCommand = new RelayCommand(_ => SelectAll(true), _ => Items.Count > 0);
        SelectNoneCommand = new RelayCommand(_ => SelectAll(false), _ => SelectedCount > 0);
        InvertSelectionCommand = new RelayCommand(_ => InvertSelection(), _ => Items.Count > 0);
        FilterByCategoryCommand = new RelayCommand(parameter => FilterByCategory(parameter));
        ToggleCommand = new AsyncRelayCommand(parameter => ToggleAsync(parameter as ModItemViewModel), ex => ReportError("切换状态失败", ex));
        DeleteCommand = new AsyncRelayCommand(parameter => DeleteAsync(parameter as ModItemViewModel), ex => ReportError("删除失败", ex));
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder(parameter as ModItemViewModel));
        CopyPathCommand = new RelayCommand(parameter => CopyToClipboard(parameter as ModItemViewModel, workshopLink: false));
        CopyWorkshopCommand = new RelayCommand(parameter => CopyToClipboard(parameter as ModItemViewModel, workshopLink: true));
        DetailsCommand = new RelayCommand(parameter => ShowDetails(parameter as ModItemViewModel));

        _library.ModsChanged += (_, _) => Ui.InvokeAsync(Refresh);
        _library.ConflictsUpdated += (_, _) => Ui.InvokeAsync(Refresh);
    }

    /// <summary>下载队列新增任务（供主界面跳转到下载页）。</summary>
    public event Action<DownloadTask>? DownloadQueued;

    /// <summary>列表内容（批量替换，只发一次集合通知，避免逐条通知造成的卡顿）。</summary>
    public RangeObservableCollection<ModItemViewModel> Items { get; } = new();

    public IReadOnlyList<SortOption> SortOptions { get; }

    public IReadOnlyList<CategoryOption> Categories { get; }

    public IReadOnlyList<StateOption> States { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand EnableAllCommand { get; }

    public RelayCommand DisableAllCommand { get; }

    public RelayCommand EnableListCommand { get; }

    public RelayCommand DisableListCommand { get; }

    public RelayCommand EnableSelectedCommand { get; }

    public RelayCommand DisableSelectedCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public AsyncRelayCommand AddLinkCommand { get; }

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand InvertSelectionCommand { get; }

    /// <summary>点击卡片上的类型徽章 → 只看该类型（参数可以是 ModItemViewModel 或 ModCategory）。</summary>
    public RelayCommand FilterByCategoryCommand { get; }

    /// <summary>按类型筛选（再次点击同一类型则取消筛选，回到全部分类）。</summary>
    private void FilterByCategory(object? parameter)
    {
        ModCategory? category = parameter switch
        {
            ModItemViewModel item => item.Model.Category,
            ModCategory value => value,
            _ => null,
        };

        if (category == null) return;

        var option = Categories.FirstOrDefault(o => o.Category == category);
        if (option == null) return;

        // 已经是该类型 → 取消筛选
        if (ReferenceEquals(SelectedCategory, option))
        {
            SelectedCategory = Categories[0];
            StatusMessage = "已取消类型筛选";
            return;
        }

        SelectedCategory = option;
        StatusMessage = $"只看「{ModCategoryInfo.Full(category.Value)}」类型的 Mod";
    }

    public AsyncRelayCommand ToggleCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand CopyWorkshopCommand { get; }

    public RelayCommand DetailsCommand { get; }

    // ------------------------------------------------------------------ 绑定属性

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            ClearSearchCommand.RaiseCanExecuteChanged();
            Refresh();
        }
    }

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!Set(ref _selectedSort, value)) return;
            _library.Config.SortMode = value.Mode;
            _library.SaveConfig();
            Refresh();
        }
    }

    public CategoryOption SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            Refresh();
        }
    }

    public StateOption SelectedState
    {
        get => _selectedState;
        set
        {
            if (!Set(ref _selectedState, value)) return;
            Refresh();
        }
    }

    public bool ShowOnlyConflicts
    {
        get => _showOnlyConflicts;
        set
        {
            if (!Set(ref _showOnlyConflicts, value)) return;
            Refresh();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            AddLinkCommand.RaiseCanExecuteChanged();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string LinkInput
    {
        get => _linkInput;
        set
        {
            if (!Set(ref _linkInput, value)) return;
            AddLinkCommand.RaiseCanExecuteChanged();
        }
    }

    public int TotalCount => _library.Mods.Count;

    public int EnabledCount => _library.Mods.Count(m => m.IsEnabled);

    public int DisabledCount => _library.Mods.Count(m => m.IsDisabled);

    public int FilteredCount => Items.Count;

    public int ConflictModCount => _library.Mods.Count(m => m.ConflictCount > 0);

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (!Set(ref _selectedCount, value)) return;
            Raise(nameof(SelectionText));
            EnableSelectedCommand.RaiseCanExecuteChanged();
            DisableSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectionText => SelectedCount == 0 ? "未选择" : $"已选择 {SelectedCount} 个";

    /// <summary>
    /// 表头复选框：是否"当前列表已全部选中"。
    /// 用普通 bool（而不是 bool?）：三态绑定在勾选/取消时容易与 WPF 的复选循环打架，
    /// 导致"点了没反应"（全选失效）。这里保持两态语义，配合独立的全选/取消按钮。
    /// </summary>
    public bool AllSelected
    {
        get => Items.Count > 0 && Items.All(i => i.IsSelected);
        set
        {
            SelectAll(value);
            Raise();
            AllSelectedChanged?.Invoke();
        }
    }

    /// <summary>表头复选框状态变化（用于让 WPF 刷新视觉状态）。</summary>
    public event Action? AllSelectedChanged;

    /// <summary>是否有任何选中项（用于按钮可用性提示）。</summary>
    public bool HasSelection => SelectedCount > 0;

    public string SummaryText
    {
        get
        {
            var conflicts = ConflictModCount;
            var text = $"共 {TotalCount} 个 Mod · 启用 {EnabledCount} · 禁用 {DisabledCount}";
            if (conflicts > 0) text += $" · {conflicts} 个存在冲突";
            if (FilteredCount != TotalCount) text += $" · 当前显示 {FilteredCount}";
            return text;
        }
    }

    public bool HasItems => Items.Count > 0;

    // ------------------------------------------------------------------ 方法

    /// <summary>首次进入时调用。</summary>
    public async Task InitializeAsync()
    {
        Refresh();
        if (_library.Config.AutoScanOnStartup && _library.Config.ModDirectories.Count > 0)
            await ScanAsync().ConfigureAwait(true);
    }

    public async Task ScanAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        ProgressText = "正在扫描 Mod 目录…";

        try
        {
            var progress = new Progress<ScanProgress>(p => ProgressText = p.Text);
            var result = await _library.ScanAsync(progress).ConfigureAwait(true);
            _services.Thumbnails.Clear();
            Refresh();
            ProgressText = result.Summary;
        }
        catch (Exception ex)
        {
            ProgressText = "扫描失败：" + ex.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按当前条件重新生成列表。</summary>
    public void Refresh()
    {
        var query = new ModQuery
        {
            SearchText = SearchText,
            Category = SelectedCategory?.Category,
            Enabled = SelectedState?.Enabled,
            SortMode = SelectedSort?.Mode ?? ModSortMode.Default,
            DisabledFirst = _library.Config.DisabledFirst,
        };

        var source = _library.Mods.AsEnumerable();
        if (ShowOnlyConflicts) source = source.Where(m => m.ConflictCount > 0);
        var filtered = query.Apply(source);

        _updatingSelection = true;
        try
        {
            var viewModels = new List<ModItemViewModel>(filtered.Count);
            foreach (var model in filtered)
            {
                if (!_cache.TryGetValue(model.Key, out var vm))
                {
                    vm = new ModItemViewModel(model, this, _services);
                    _cache[model.Key] = vm;
                }
                else
                {
                    vm.Refresh();
                }

                viewModels.Add(vm);
            }

            // 一次性替换：无论多少条，列表只重建一次
            Items.ReplaceAll(viewModels);

            // 清理已不存在的条目
            var alive = new HashSet<string>(filtered.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys.Where(k => !alive.Contains(k)).ToList())
                _cache.Remove(key);

            StartThumbnailPump(viewModels);
        }
        finally
        {
            _updatingSelection = false;
        }

        Raise(nameof(SummaryText), nameof(TotalCount), nameof(EnabledCount), nameof(DisabledCount),
              nameof(FilteredCount), nameof(ConflictModCount), nameof(HasItems));
        NotifySelectionChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    internal void NotifySelectionChanged()
    {
        if (_updatingSelection) return;

        SelectedCount = Items.Count(i => i.IsSelected);
        Raise(nameof(AllSelected), nameof(HasSelection));

        EnableSelectedCommand.RaiseCanExecuteChanged();
        DisableSelectedCommand.RaiseCanExecuteChanged();
    }

    public void SelectAll(bool value)
    {
        _updatingSelection = true;
        try
        {
            foreach (var item in Items) item.IsSelected = value;
        }
        finally
        {
            _updatingSelection = false;
        }

        NotifySelectionChanged();
        ProgressText = value
            ? $"已全选当前列表（{Items.Count} 个 Mod）"
            : "已取消选择";
    }

    /// <summary>状态栏文字（供全选等操作反馈）。</summary>
    public string StatusMessage
    {
        get => ProgressText;
        private set => ProgressText = value;
    }

    /// <summary>反选。</summary>
    public void InvertSelection()
    {
        _updatingSelection = true;
        try
        {
            foreach (var item in Items) item.IsSelected = !item.IsSelected;
        }
        finally
        {
            _updatingSelection = false;
        }

        NotifySelectionChanged();
        StatusMessage = $"已反选（当前选中 {SelectedCount} 个）";
    }

    private IEnumerable<ModItem> VisibleModelItems() => Items.Select(i => i.Model).ToList();

    private IEnumerable<ModItem> SelectedModelItems() => Items.Where(i => i.IsSelected).Select(i => i.Model).ToList();

    private void RunStateChange(Func<StateChangeResult> action, string what)
    {
        try
        {
            var result = action();
            ProgressText = $"{what}：{result.Summary}";
        }
        catch (Exception ex)
        {
            ReportError(what + "失败", ex);
        }
    }

    private Task ToggleAsync(ModItemViewModel? item)
    {
        if (item == null) return Task.CompletedTask;

        var target = item.Model.IsEnabled ? ModState.Disabled : ModState.Enabled;
        if (!_library.SetState(item.Model, target, out var error))
        {
            ProgressText = "操作失败：" + error;
            _services.Dialogs.Error("无法切换该 Mod 的启用状态。", "操作失败", error);
            return Task.CompletedTask;
        }

        item.Refresh();
        Raise(nameof(SummaryText), nameof(EnabledCount), nameof(DisabledCount));
        return Task.CompletedTask;
    }

    private Task DeleteAsync(ModItemViewModel? item)
    {
        if (item == null) return Task.CompletedTask;

        if (!_services.Dialogs.ConfirmDelete(item.Model)) return Task.CompletedTask;

        // 先取出 Key，删除后缓存需要清理
        var key = item.Model.Key;
        var result = _library.DeleteMod(item.Model);
        if (!result.Success)
        {
            _services.Dialogs.Error("删除失败。", "删除 Mod", result.Error);
            return Task.CompletedTask;
        }

        _cache.Remove(key);
        _services.Thumbnails.Invalidate(item.Model);
        ProgressText = result.Message;
        Refresh();
        return Task.CompletedTask;
    }

    private async Task AddFromLinkAsync()
    {
        var link = LinkInput?.Trim();
        if (string.IsNullOrWhiteSpace(link)) return;

        IsBusy = true;
        try
        {
            var (task, message) = await _library.DownloadFromLinkAsync(link).ConfigureAwait(true);

            if (task == null)
            {
                _services.Dialogs.Error(message, "添加 Mod 失败",
                    "支持的形式：\r\n" +
                    "  · https://steamcommunity.com/sharedfiles/filedetails/?id=123456789\r\n" +
                    "  · steam://url/CommunityFilePage/123456789\r\n" +
                    "  · 123456789（纯 ID）");
                return;
            }

            ProgressText = message;
            LinkInput = string.Empty;
            DownloadQueued?.Invoke(task);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void OpenFolder(ModItemViewModel? item)
    {
        if (item == null) return;

        try
        {
            var target = item.Model.ActualPath;
            if (File.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            else if (Directory.Exists(item.Model.Directory))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Model.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败: {ex.Message}");
        }
    }

    private void CopyToClipboard(ModItemViewModel? item, bool workshopLink)
    {
        if (item == null) return;

        var text = workshopLink
            ? (item.HasWorkshopId ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={item.WorkshopId}" : string.Empty)
            : item.Model.FilePath;

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            ProgressText = "已复制：" + text;
        }
        catch
        {
            // 剪贴板占用时忽略
        }
    }

    private void ShowDetails(ModItemViewModel? item)
    {
        if (item == null) return;
        _services.Dialogs.Info(item.DisplayName, "Mod 详情", item.DetailText);
    }

    private void ReportError(string title, Exception exception)
    {
        Log.Error(title, exception);
        ProgressText = $"{title}：{exception.Message}";
        _services.Dialogs.Error(exception.Message, title, exception.ToString());
    }

    /// <summary>
    /// 分批加载缩略图：串行 + 每张之间让出 UI 线程。
    /// 相比"一次性并发加载 100 多张图片"，滚动和切换筛选都明显更跟手。
    /// </summary>
    private void StartThumbnailPump(IReadOnlyList<ModItemViewModel> items)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();

        if (items.Count == 0) return;

        var token = _thumbnailCts.Token;
        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                if (token.IsCancellationRequested) return;

                try
                {
                    await item.EnsureThumbnailAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"缩略图加载失败（{item.DisplayName}）：{ex.Message}");
                }

                // 让出时间片，保证界面线程始终有机会处理输入与重绘
                await Task.Delay(1, token).ConfigureAwait(false);
            }
        }, token);
    }
}
