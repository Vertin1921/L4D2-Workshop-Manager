using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace L4D2ModManager.Core.Models;

public enum DownloadStatus
{
    Queued,
    Resolving,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled,
}

/// <summary>一个下载任务（创意工坊 Mod）。</summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    private long _downloadedBytes;
    private long _totalBytes;
    private double _speedBps;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _error;
    private string _statusDetail = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? WorkshopId { get; set; }
    public string Title { get; set; } = "未命名 Mod";
    public string? ThumbnailUrl { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>目标 Mod 目录（保存 .vpk 的位置）。</summary>
    public string DestinationDirectory { get; set; } = string.Empty;

    /// <summary>下载完成后的最终 .vpk 路径。</summary>
    public string? FinalFilePath { get; set; }

    public string? FileName { get; set; }

    /// <summary>已使用的下载通道（HTTP 直链 / steamcmd / 订阅缓存）。</summary>
    public string? ProviderName { get; set; }

    public long TotalBytes
    {
        get => _totalBytes;
        set { if (Set(ref _totalBytes, value)) Raise(nameof(SizeText), nameof(ProgressPercent), nameof(ProgressText)); }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set { if (Set(ref _downloadedBytes, value)) Raise(nameof(ProgressPercent), nameof(ProgressText)); }
    }

    public double SpeedBps
    {
        get => _speedBps;
        set { if (Set(ref _speedBps, value)) Raise(nameof(SpeedText)); }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                Raise(nameof(StatusText), nameof(IsActive), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry), nameof(IsFinished));
        }
    }

    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    /// <summary>补充说明，例如 “正在通过 steamcmd 下载”。</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        set => Set(ref _statusDetail, value);
    }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public int Attempt { get; set; }

    // ---- 运行时字段（不序列化） ----

    [JsonIgnore] internal CancellationTokenSource? Cts { get; set; }

    /// <summary>对应的创意工坊信息（运行期缓存，不序列化）。</summary>
    [JsonIgnore] public WorkshopItemInfo? Info { get; set; }

    [JsonIgnore] internal ManualResetEventSlim PauseGate { get; } = new(true);

    // ---- 计算属性 ----

    [JsonIgnore] public double ProgressPercent => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100);

    [JsonIgnore] public string ProgressText => TotalBytes <= 0
        ? ModItem.FormatSize(DownloadedBytes)
        : $"{ModItem.FormatSize(DownloadedBytes)} / {ModItem.FormatSize(TotalBytes)}  ({ProgressPercent:0.0}%)";

    [JsonIgnore] public string SpeedText => SpeedBps <= 1 ? "-" : $"{ModItem.FormatSize((long)SpeedBps)}/s";

    [JsonIgnore] public string SizeText => ModItem.FormatSize(TotalBytes);

    [JsonIgnore] public bool HasError => !string.IsNullOrWhiteSpace(Error);

    [JsonIgnore] public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Paused;

    [JsonIgnore] public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Resolving;

    [JsonIgnore] public bool CanResume => Status == DownloadStatus.Paused;

    [JsonIgnore] public bool CanCancel => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Resolving;

    [JsonIgnore] public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Canceled;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "排队中",
        DownloadStatus.Resolving => "解析链接",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => "失败",
        DownloadStatus.Canceled => "已取消",
        _ => Status.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(params string?[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>通知 UI 刷新所有计算属性。</summary>
    public void RaiseAll()
    {
        Raise(nameof(TotalBytes), nameof(DownloadedBytes), nameof(SpeedBps), nameof(Status),
              nameof(Error), nameof(StatusDetail), nameof(ProgressPercent), nameof(ProgressText),
              nameof(SpeedText), nameof(SizeText), nameof(StatusText), nameof(IsActive),
              nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry), nameof(IsFinished));
    }
}
