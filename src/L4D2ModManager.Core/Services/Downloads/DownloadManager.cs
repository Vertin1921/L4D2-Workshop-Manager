using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Steam;
using L4D2ModManager.Core.Services.Workshop;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>
/// 下载管理器：队列、并发控制、暂停/继续/取消/重试、进度与速度统计。
/// 下载完成后触发 <see cref="DownloadCompleted"/>，由上层扫描并入库。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private const int MaxConcurrent = 2;

    private readonly List<DownloadTask> _tasks = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly SteamWorkshopClient _workshop;
    private readonly Func<AppConfig> _config;
    private readonly List<IWorkshopDownloadProvider> _providers;
    private bool _disposed;

    public DownloadManager(SteamWorkshopClient workshop, Func<AppConfig> config, Func<SteamPaths> steamPaths)
    {
        _workshop = workshop;
        _config = config;

        _providers = new List<IWorkshopDownloadProvider>
        {
            // 大陆优先：国内可直连的工坊镜像站 + 加速线路；镜像站不可达时会自己快速跳过，不拖慢其它通道
            new MirrorWorkshopProvider(),
            new HttpWorkshopProvider(),
            new SubscribedContentProvider(config, steamPaths),
            new SteamCmdProvider(config, steamPaths),
        };
    }

    public event Action<DownloadTask>? TaskAdded;

    public event Action<DownloadTask>? TaskUpdated;

    /// <summary>下载完成（文件已落盘），参数为任务与最终 .vpk 路径。</summary>
    public event Action<DownloadTask, string>? DownloadCompleted;

    public IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_gate) return _tasks.ToList();
        }
    }

    public IReadOnlyList<IWorkshopDownloadProvider> Providers => _providers;

    /// <summary>加入下载队列。</summary>
    public DownloadTask Enqueue(WorkshopItemInfo info, string destinationDirectory, string? sourceUrl = null)
    {
        var task = new DownloadTask
        {
            WorkshopId = info.PublishedFileId,
            Title = string.IsNullOrWhiteSpace(info.Title) ? $"创意工坊 Mod #{info.PublishedFileId}" : info.Title,
            ThumbnailUrl = info.PreviewUrl,
            SourceUrl = sourceUrl ?? info.PageUrl,
            DestinationDirectory = string.IsNullOrWhiteSpace(destinationDirectory)
                ? (_config().DownloadDirectory ?? string.Empty)
                : destinationDirectory,
            FileName = info.FileName,
            TotalBytes = info.FileSize,
            Info = info,
            Status = DownloadStatus.Queued,
            StatusDetail = "等待下载",
        };

        lock (_gate) _tasks.Insert(0, task);
        Log.Info($"加入下载队列：{task.Title}（{task.WorkshopId}）-> {task.DestinationDirectory}");

        TaskAdded?.Invoke(task);
        _ = RunAsync(task);
        return task;
    }

    /// <summary>
    /// 只凭工坊 ID 加入下载队列（接口不可用时也能走订阅缓存 / steamcmd 通道）。
    /// </summary>
    public DownloadTask EnqueueById(string workshopId, string destinationDirectory, string? title = null, string? sourceUrl = null)
    {
        var info = new WorkshopItemInfo
        {
            PublishedFileId = workshopId,
            Title = string.IsNullOrWhiteSpace(title) ? $"创意工坊 Mod #{workshopId}" : title!,
        };

        return Enqueue(info, destinationDirectory, sourceUrl);
    }

    public DownloadTask? Find(string id)
    {
        lock (_gate) return _tasks.FirstOrDefault(t => t.Id == id);
    }

    public bool Pause(string id)
    {
        var task = Find(id);
        if (task == null || !task.CanPause) return false;

        task.PauseGate.Reset();
        task.Status = DownloadStatus.Paused;
        task.StatusDetail = "已暂停";
        task.SpeedBps = 0;
        TaskUpdated?.Invoke(task);
        return true;
    }

    public bool Resume(string id)
    {
        var task = Find(id);
        if (task == null) return false;

        if (task.Status == DownloadStatus.Paused)
        {
            task.PauseGate.Set();
            task.Status = DownloadStatus.Downloading;
            task.StatusDetail = "继续下载";
            TaskUpdated?.Invoke(task);
            return true;
        }

        if (task.Status is DownloadStatus.Failed or DownloadStatus.Canceled)
            return Retry(id) != null;

        return false;
    }

    public bool Cancel(string id)
    {
        var task = Find(id);
        if (task == null || !task.CanCancel) return false;

        try
        {
            task.PauseGate.Set();
            task.Cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        task.Status = DownloadStatus.Canceled;
        task.StatusDetail = "已取消";
        TaskUpdated?.Invoke(task);
        return true;
    }

    public DownloadTask? Retry(string id)
    {
        var task = Find(id);
        if (task == null) return null;

        task.Attempt++;
        task.Error = null;
        task.SpeedBps = 0;
        task.Status = DownloadStatus.Queued;
        task.StatusDetail = $"第 {task.Attempt + 1} 次尝试";
        TaskUpdated?.Invoke(task);

        _ = RunAsync(task);
        return task;
    }

    public void RemoveFinished()
    {
        lock (_gate)
            _tasks.RemoveAll(t => t.IsFinished);
    }

    /// <summary>取消全部任务并释放资源。</summary>
    public void CancelAll()
    {
        foreach (var task in Tasks)
        {
            try
            {
                task.PauseGate.Set();
                task.Cts?.Cancel();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private async Task RunAsync(DownloadTask task)
    {
        if (_disposed) return;

        try
        {
            await _concurrency.WaitAsync().ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        task.Cts?.Dispose();
        task.Cts = new CancellationTokenSource();
        var token = task.Cts.Token;

        try
        {
            task.Status = DownloadStatus.Resolving;
            task.StatusDetail = "正在获取创意工坊信息…";
            TaskUpdated?.Invoke(task);

            var info = task.Info;
            var lookupError = (string?)null;

            if (info == null && !string.IsNullOrWhiteSpace(task.WorkshopId))
            {
                var result = await _workshop.GetDetailsAsync(new[] { task.WorkshopId! }, token).ConfigureAwait(true);
                info = result.First;
                if (info == null) lookupError = result.Error;

                // 关键：即使拿不到接口信息（例如国内网络访问不到 api.steampowered.com，
                // 或条目被限流），也要继续尝试「Steam 订阅缓存」与「steamcmd」两条不依赖接口的通道。
                info ??= new WorkshopItemInfo
                {
                    PublishedFileId = task.WorkshopId!,
                    Title = string.IsNullOrWhiteSpace(task.Title) ? $"创意工坊 Mod #{task.WorkshopId}" : task.Title,
                    Available = true,
                };

                task.Info = info;
            }

            if (info == null)
                throw new InvalidOperationException("缺少创意工坊 ID，无法下载");

            if (info.Available == false)
                throw new InvalidOperationException(info.Error ?? "该创意工坊条目不可用（可能已被作者删除）");

            if (string.IsNullOrWhiteSpace(task.Title) || task.Title.StartsWith("未命名", StringComparison.Ordinal))
                task.Title = info.Title;

            task.ThumbnailUrl ??= info.PreviewUrl;
            task.FileName ??= info.FileName;
            if (info.FileSize > 0) task.TotalBytes = info.FileSize;

            var errors = new List<string>();
            if (!string.IsNullOrWhiteSpace(lookupError))
                errors.Add($"工坊接口：{lookupError}（将改用订阅缓存 / steamcmd 通道）");

            foreach (var provider in _providers)
            {
                token.ThrowIfCancellationRequested();

                if (!provider.CanHandle(task, info))
                {
                    errors.Add($"{provider.Name}：该通道不适用（{provider.Description}）");
                    continue;
                }

                task.ProviderName = provider.Name;
                task.Status = DownloadStatus.Downloading;
                task.StatusDetail = $"使用通道：{provider.Name}";
                task.DownloadedBytes = 0;
                TaskUpdated?.Invoke(task);

                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (p.Downloaded > 0) task.DownloadedBytes = p.Downloaded;
                    if (p.Total > 0) task.TotalBytes = p.Total;
                    task.SpeedBps = p.SpeedBps;
                    if (!string.IsNullOrWhiteSpace(p.Detail)) task.StatusDetail = p.Detail!;
                });

                // 瞬时网络失败（断流 / 超时 / 连接被重置）自动重试；带退避，且续传会接着已下载的部分继续。
                const int MaxAttempts = 3;
                DownloadResult result = DownloadResult.Fail("未执行");

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    try
                    {
                        result = await provider.DownloadAsync(task, info, progress, token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result = DownloadResult.Fail(ex.Message);
                        Log.Warn($"{provider.Name} 通道异常（第 {attempt}/{MaxAttempts} 次）: {ex.Message}");
                    }

                    if (result.Success) break;
                    if (result.Permanent) break;                 // 条目不存在 / 403 等，重试没有意义
                    if (attempt >= MaxAttempts) break;

                    task.StatusDetail = $"{provider.Name}：网络中断，正在自动重试（{attempt}/{MaxAttempts}）…";
                    TaskUpdated?.Invoke(task);
                    Log.Warn($"{provider.Name} 第 {attempt} 次失败，{1.5 * attempt:0.#} 秒后重试：{result.Error}");
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), token).ConfigureAwait(true);
                }

                if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath))
                {
                    task.FinalFilePath = result.FilePath;
                    task.Status = DownloadStatus.Completed;
                    task.CompletedUtc = DateTime.UtcNow;
                    task.SpeedBps = 0;
                    task.StatusDetail = $"已保存：{Path.GetFileName(result.FilePath)}";
                    Log.Info($"下载完成：{task.Title} -> {result.FilePath}");

                    TaskUpdated?.Invoke(task);
                    DownloadCompleted?.Invoke(task, result.FilePath!);
                    return;
                }

                errors.Add(result.Permanent
                    ? $"{provider.Name}：{result.Error ?? "条目不可用"}（该条目本身不可下载）"
                    : $"{provider.Name}：{result.Error ?? "网络错误"}（网络问题，已自动重试 {MaxAttempts} 次，可再点「重试」）");
            }

            throw new InvalidOperationException(
                "所有下载通道均失败：\n" + string.Join("\n", errors) +
                "\n\n提示：可在 Steam 中订阅该 Mod（内容会出现在 steamapps\\workshop\\content\\550），再点击“重试”。");
        }
        catch (OperationCanceledException)
        {
            if (task.Status != DownloadStatus.Canceled)
            {
                task.Status = DownloadStatus.Canceled;
                task.StatusDetail = "已取消";
            }
            task.SpeedBps = 0;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.Error = ex.Message;
            task.StatusDetail = "下载失败";
            task.SpeedBps = 0;
            Log.Error($"下载失败：{task.Title}", ex);
        }
        finally
        {
            try
            {
                task.Cts?.Dispose();
                task.Cts = null;
            }
            catch
            {
                // 忽略
            }

            _concurrency.Release();
            TaskUpdated?.Invoke(task);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
        _concurrency.Dispose();
    }
}
