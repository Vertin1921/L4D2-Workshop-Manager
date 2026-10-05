using System.Collections.ObjectModel;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;
namespace L4D2ModManager.App.ViewModels;

/// <summary>冲突检测页面：列出被多个 Mod 修改的同一个文件，并支持一键禁用。</summary>
public sealed class ConflictsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;

    private string _filterText = string.Empty;
    private bool _showOnlyActive;
    private bool _showOnlyHighSeverity;
    private bool _showIdenticalContent;
    private bool _isBusy;
    private string _statusText = "点击「开始检测」扫描所有 Mod 的内部文件。";

    public ConflictsViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;
        _showIdenticalContent = !_library.Config.HideIdenticalConflicts;

        DetectCommand = new AsyncRelayCommand(DetectAsync, () => !IsBusy, ex => ReportError("冲突检测失败", ex));
        DisableModCommand = new RelayCommand(p => SetState(p as ConflictParticipant, ModState.Disabled));
        EnableModCommand = new RelayCommand(p => SetState(p as ConflictParticipant, ModState.Enabled));
        DisableOthersCommand = new RelayCommand(p => DisableOthers(p as ConflictParticipant));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as ConflictFileEntry));
        ClearFilterCommand = new RelayCommand(_ => FilterText = string.Empty);
        ResetIgnorePatternsCommand = new RelayCommand(_ =>
        {
            _library.Config.ConflictIgnorePatterns = new List<string>();
            _library.SaveConfig();
            StatusText = "已清空自定义忽略关键字（内置忽略规则仍然生效）";
        });

        _library.ConflictsUpdated += (_, report) => Ui.InvokeAsync(() => Update(report));
    }

    public ObservableCollection<ConflictFileEntry> Conflicts { get; } = new();

    public AsyncRelayCommand DetectCommand { get; }

    public RelayCommand DisableModCommand { get; }

    public RelayCommand EnableModCommand { get; }

    public RelayCommand DisableOthersCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand ResetIgnorePatternsCommand { get; }

    /// <summary>全局冲突总数（含被隐藏的内容相同项）。</summary>
    public int TotalConflictCount => Report?.FileCount ?? 0;

    /// <summary>真实冲突数量（排除内容相同的重复文件）。</summary>
    public int RealConflictCount => Report?.RealConflictCount ?? 0;

    public ConflictReport? Report { get; private set; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value)) ApplyFilter();
        }
    }

    public bool ShowOnlyActive
    {
        get => _showOnlyActive;
        set
        {
            if (Set(ref _showOnlyActive, value)) ApplyFilter();
        }
    }

    /// <summary>只看资源覆盖类（模型 / 材质 / 音频 / 地图）冲突。</summary>
    public bool ShowOnlyHighSeverity
    {
        get => _showOnlyHighSeverity;
        set
        {
            if (Set(ref _showOnlyHighSeverity, value)) ApplyFilter();
        }
    }

    /// <summary>是否显示"内容完全相同"的重复文件（默认隐藏：CRC 相同即内容一致，不会互相覆盖）。</summary>
    public bool ShowIdenticalContent
    {
        get => _showIdenticalContent;
        set
        {
            if (!Set(ref _showIdenticalContent, value)) return;

            _library.Config.HideIdenticalConflicts = !value;
            _library.SaveConfig();
            ApplyFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            DetectCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SummaryText => Report?.SummaryText ?? "尚未检测";

    public string ScannedText => Report == null ? string.Empty : "检测时间：" + Report.ScannedText;

    public int VisibleCount => Conflicts.Count;

    public bool HasConflicts => Conflicts.Count > 0;

    public async Task DetectAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = "正在比较所有 Mod 的内部文件…";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var report = await _library.DetectConflictsAsync(progress).ConfigureAwait(true);
            Update(report);
            StatusText = report.SummaryText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Refresh()
    {
        if (_library.LastConflictReport != null) Update(_library.LastConflictReport);
        else Raise(nameof(SummaryText), nameof(ScannedText));
    }

    private void Update(ConflictReport report)
    {
        Report = report;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var source = Report?.Files ?? new List<ConflictFileEntry>();

        // 默认隐藏"内容完全相同"的重复文件（CRC 一致 → 不会互相覆盖，只是重复打包）
        if (!ShowIdenticalContent) source = source.Where(f => !f.ContentIdentical).ToList();

        if (ShowOnlyHighSeverity)
            source = source.Where(f => f.Severity == ConflictSeverity.High || f.ContentIdentical).ToList();

        if (ShowOnlyActive) source = source.Where(f => f.IsActiveConflict).ToList();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var term = FilterText.Trim();
            source = source.Where(f =>
                    f.InnerPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    f.KindText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    f.Mods.Any(m => m.ModName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        Conflicts.Clear();
        foreach (var entry in source) Conflicts.Add(entry);

        Raise(nameof(SummaryText), nameof(ScannedText), nameof(VisibleCount), nameof(HasConflicts),
              nameof(TotalConflictCount), nameof(RealConflictCount));
    }

    private void SetState(ConflictParticipant? participant, ModState state)
    {
        if (participant == null) return;

        var item = _library.Repository.FindByKey(participant.ModKey)
                   ?? _library.Mods.FirstOrDefault(m => string.Equals(m.FilePath, participant.FilePath, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            StatusText = "未在数据库中找到该 Mod（可能已被移除）";
            return;
        }

        if (_library.SetState(item, state, out var error))
        {
            StatusText = $"{(state == ModState.Enabled ? "已启用" : "已禁用")}：{item.DisplayName}";
            _ = DetectAsync();
        }
        else
        {
            StatusText = "操作失败：" + error;
        }
    }

    /// <summary>保留指定的 Mod，禁用同一冲突中的其他 Mod。</summary>
    private void DisableOthers(ConflictParticipant? keep)
    {
        if (keep == null || Report == null) return;

        var entry = Report.Files.FirstOrDefault(f => f.Mods.Any(m => m.ModKey == keep.ModKey));
        if (entry == null) return;

        var others = entry.Mods.Where(m => m.ModKey != keep.ModKey && m.Enabled).ToList();
        if (others.Count == 0)
        {
            StatusText = "该冲突中没有其他启用的 Mod";
            return;
        }

        var names = string.Join("\r\n", others.Select(m => "  · " + m.ModName));
        if (!_services.Dialogs.Confirm(
                $"将禁用与该 Mod 冲突的其他 {others.Count} 个 Mod：\r\n{names}\r\n\r\n保留：{keep.ModName}\r\n冲突文件：{entry.InnerPath}",
                "禁用冲突的 Mod", null, "禁用"))
            return;

        int changed = 0;
        foreach (var participant in others)
        {
            var item = _library.Repository.FindByKey(participant.ModKey);
            if (item == null) continue;
            if (_library.SetState(item, ModState.Disabled, out _)) changed++;
        }

        StatusText = $"已禁用 {changed} 个冲突 Mod";
        _ = DetectAsync();
    }

    private void CopyPath(ConflictFileEntry? entry)
    {
        if (entry == null) return;

        try
        {
            System.Windows.Clipboard.SetText(entry.InnerPath);
            StatusText = "已复制冲突文件路径：" + entry.InnerPath;
        }
        catch
        {
            // 忽略
        }
    }

    private void ReportError(string title, Exception exception)
    {
        Log.Error(title, exception);
        StatusText = $"{title}：{exception.Message}";
        _services.Dialogs.Error(exception.Message, title, exception.ToString());
    }
}
