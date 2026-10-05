using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>
/// 通道一：Steam UGC 直链下载（最快，支持断点续传）。
/// 通过 GetPublishedFileDetails 返回的 file_url 直接下载 .vpk。
/// </summary>
public sealed class HttpWorkshopProvider : IWorkshopDownloadProvider
{
    private readonly HttpClient _http;

    public HttpWorkshopProvider(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    public string Name => "HTTP 直链";

    public string Description => "使用创意工坊 UGC 直链下载，支持断点续传、暂停与限速统计";

    public bool CanHandle(DownloadTask task, WorkshopItemInfo info) => info.HasDirectUrl;

    public async Task<DownloadResult> DownloadAsync(
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var url = info.FileUrl;
        if (string.IsNullOrWhiteSpace(url))
            return DownloadResult.Fail("该条目没有可用的直链", permanent: true);

        Directory.CreateDirectory(AppPaths.DownloadTempDir);
        var key = string.IsNullOrWhiteSpace(task.WorkshopId) ? task.Id : task.WorkshopId!;
        var partPath = Path.Combine(AppPaths.DownloadTempDir, key + ".part");

        // 同一个 Mod 被重复加入下载队列时，两个任务会抢同一个 .part 文件，
        // 于是报"The process cannot access the file ... because it is being used by another process"。
        // 这里先探测：文件被别的任务占着就改用唯一文件名（下载仍会成功，只是不再共享断点文件）。
        try
        {
            if (File.Exists(partPath))
            {
                using var probe = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        catch (IOException)
        {
            partPath = Path.Combine(AppPaths.DownloadTempDir, key + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".part");
            Log.Warn($"断点文件被占用，改用新的临时文件：{Path.GetFileName(partPath)}");
        }

        long existing = 0;
        if (File.Exists(partPath))
        {
            existing = new FileInfo(partPath).Length;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 本地文件已经完整（或服务端不支持续传）→ 删除重来
                TryDelete(partPath);
                existing = 0;
                return DownloadResult.Fail("断点信息失效，请重试", permanent: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var permanent = (int)response.StatusCode is 403 or 404 or 410;
                return DownloadResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", permanent);
            }

            bool resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed) existing = 0;

            long contentLength = response.Content.Headers.ContentLength ?? 0;
            long total = contentLength > 0 ? contentLength + existing : Math.Max(info.FileSize, 0);

            progress?.Report(new DownloadProgress(existing, total, 0, resumed ? "断点续传中…" : "开始下载…"));

            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var target = new FileStream(
                partPath,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 << 20,
                useAsync: true);

            var buffer = new byte[1 << 20];
            long downloaded = existing;
            var stopwatch = Stopwatch.StartNew();
            long lastBytes = downloaded;
            var lastReport = TimeSpan.Zero;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                task.PauseGate.Wait(cancellationToken);

                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                {
                    double seconds = (elapsed - lastReport).TotalSeconds;
                    double speed = seconds > 0 ? (downloaded - lastBytes) / seconds : 0;
                    progress?.Report(new DownloadProgress(downloaded, total, speed, null));
                    lastBytes = downloaded;
                    lastReport = elapsed;
                }
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Close();

            var finalSize = new FileInfo(partPath).Length;
            if (total > 0 && finalSize < total)
            {
                return DownloadResult.Fail($"下载不完整（{finalSize}/{total} 字节），可重试续传");
            }

            progress?.Report(new DownloadProgress(finalSize, total > 0 ? total : finalSize, 0, "正在校验并安装…"));

            var filePath = DownloadFileHelper.PromoteFile(partPath, task, info);
            Log.Info($"HTTP 通道下载完成：{info.Title} -> {filePath}");
            return DownloadResult.Ok(filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"HTTP 通道下载失败: {info.PublishedFileId} -> {ex.Message}");
            return DownloadResult.Fail(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
