using System.Collections.ObjectModel;
using System.Diagnostics;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.ViewModels;

/// <summary>下载管理页面：进度、速度、暂停 / 继续 / 取消 / 重试，完成后自动入库。</summary>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;
    private DownloadTask? _selectedTask;

    public DownloadsViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        PauseCommand = new RelayCommand(p => Act(p, task => _library.Downloads.Pause(task.Id)));
        ResumeCommand = new RelayCommand(p => Act(p, task => _library.Downloads.Resume(task.Id)));
        CancelCommand = new RelayCommand(p => Act(p, task => _library.Downloads.Cancel(task.Id)));
        RetryCommand = new RelayCommand(p => Act(p, task => _library.Downloads.Retry(task.Id)));
        RemoveCommand = new RelayCommand(p => Act(p, task => Remove(task)));
        OpenFileCommand = new RelayCommand(p => OpenFile(p as DownloadTask ?? SelectedTask));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearFinishedCommand = new RelayCommand(_ => ClearFinished(), () => Tasks.Any(t => t.IsFinished));
        CopyLinkCommand = new RelayCommand(p => CopyLink(p as DownloadTask ?? SelectedTask));

        _library.Downloads.TaskAdded += task => Ui.InvokeAsync(() =>
        {
            if (Tasks.Contains(task)) return;
            Tasks.Insert(0, task);
            RaiseCounts();
        });

        _library.Downloads.TaskUpdated += _ => Ui.InvokeAsync(RaiseCounts);

        _library.Downloads.DownloadCompleted += (task, path) => Ui.InvokeAsync(async () =>
        {
            RaiseCounts();
            StatusText = $"下载完成：{task.Title} → {Path.GetFileName(path)}";

            // 下载完成后自动扫描并加入 Mod 列表
            try
            {
                await _library.ScanAsync().ConfigureAwait(true);
                StatusText = $"下载完成并已加入列表：{task.Title}";
            }
            catch (Exception ex)
            {
                Log.Warn($"下载完成后自动扫描失败: {ex.Message}");
            }
        });
    }

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RetryCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand OpenFileCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand CopyLinkCommand { get; }

    public DownloadTask? SelectedTask
    {
        get => _selectedTask;
        set => Set(ref _selectedTask, value);
    }

    public string StatusText { get; private set; } = "暂无下载任务";

    public int ActiveCount => Tasks.Count(t => t.IsActive);

    public int FinishedCount => Tasks.Count(t => t.IsFinished);

    public bool HasTasks => Tasks.Count > 0;

    public string SummaryText => Tasks.Count == 0
        ? "还没有下载任务。可在「创意工坊」或「Mod 管理」中通过链接添加。"
        : $"共 {Tasks.Count} 个任务 · 进行中 {ActiveCount} · 已完成 {FinishedCount}";

    public string DownloadDirectoryText => _library.ResolveDownloadDirectory();

    /// <summary>从 Core 的下载管理器重建列表（切换页面时调用）。</summary>
    public void Refresh()
    {
        var tasks = _library.Downloads.Tasks;
        if (Tasks.Count != tasks.Count || !Tasks.SequenceEqual(tasks))
        {
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(task);
        }

        RaiseCounts();
    }

    private void Act(object? parameter, Action<DownloadTask> action)
    {
        var task = parameter as DownloadTask ?? SelectedTask;
        if (task == null) return;

        action(task);
        RaiseCounts();
    }

    private void Remove(DownloadTask task)
    {
        Tasks.Remove(task);
        RaiseCounts();
    }

    private void ClearFinished()
    {
        foreach (var task in Tasks.Where(t => t.IsFinished).ToList())
            Tasks.Remove(task);

        _library.Downloads.RemoveFinished();
        RaiseCounts();
    }

    private void OpenFile(DownloadTask? task)
    {
        if (task == null) return;

        try
        {
            var path = task.FinalFilePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            OpenFolder();
        }
        catch (Exception ex)
        {
            Log.Warn($"打开下载文件失败: {ex.Message}");
        }
    }

    private void OpenFolder()
    {
        try
        {
            var directory = _library.ResolveDownloadDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开下载目录失败: {ex.Message}");
        }
    }

    private void CopyLink(DownloadTask? task)
    {
        if (task == null) return;

        var text = task.SourceUrl
                   ?? (string.IsNullOrWhiteSpace(task.WorkshopId)
                       ? null
                       : $"https://steamcommunity.com/sharedfiles/filedetails/?id={task.WorkshopId}");
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "已复制链接：" + text;
        }
        catch
        {
            // 忽略
        }
    }

    private void RaiseCounts()
    {
        Raise(nameof(ActiveCount), nameof(FinishedCount), nameof(HasTasks), nameof(SummaryText), nameof(StatusText));
        ClearFinishedCommand.RaiseCanExecuteChanged();
        Raise(nameof(DownloadDirectoryText));
    }
}
