using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>
/// 大陆镜像加速通道（zhrradiant 创意工坊镜像站）。
///
/// 国内网络环境里 steamcommunity.com 常常无法访问、直连 cdn.steamusercontent.com 也很慢，
/// 这个通道改成：
///   ① 先从**国内可直连**的镜像站（zhrradiant.com）按工坊 ID 取条目直链 file_url；
///   ② 下载时优先走它的**加速线路**（accelerator.zhrradiantcos.top?url=…），失败再回退原始直链。
///
/// 镜像站不可达时会快速失败并缓存 10 分钟，期间 CanHandle 直接返回 false，
/// 于是下载自动落到其它通道，不会拖慢整体流程。
/// </summary>
public sealed class MirrorWorkshopProvider : IWorkshopDownloadProvider
{
    /// <summary>镜像站 REST 基址（WordPress 插件 l4d2-workshop/v1）。</summary>
    public const string MirrorApiBase = "https://zhrradiant.com/wp-json/l4d2-workshop/v1";

    /// <summary>下载加速线路；用法：{AcceleratorBase}?url={UrlEncode(原始直链)}。</summary>
    public const string AcceleratorBase = "https://accelerator.zhrradiantcos.top";

    /// <summary>下载与详情请求都带这个 Referer，避免被服务端拒绝。</summary>
    private const string MirrorReferer = "https://zhrradiant.com/tools/l4d2-workshop";

    private const string ChunkNotice = "大陆镜像加速";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object ProbeGate = new();

    /// <summary>探测失败后的冷却截止时间：这段时间内不再尝试镜像通道。</summary>
    private static DateTime _probeCoolDownUntil = DateTime.MinValue;

    private static string? _probeNote;

    public string Name => ChunkNotice;

    public string Description => "国内可直连的工坊镜像（zhrradiant）+ 加速线路，无需代理；不可达时自动跳过";

    public bool CanHandle(DownloadTask task, WorkshopItemInfo info)
    {
        if (string.IsNullOrWhiteSpace(task.WorkshopId)) return false;
        return MirrorAvailable();
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // ① 直链：本地已有就用，没有就问镜像站要
        var url = info.FileUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            progress?.Report(new DownloadProgress(0, Math.Max(info.FileSize, 0), 0, "从大陆镜像站获取直链…"));
            url = await ResolveFileUrlAsync(task.WorkshopId!, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(url))
            return DownloadResult.Fail("镜像站里没有这个条目的下载直链");

        Directory.CreateDirectory(AppPaths.DownloadTempDir);
        var key = string.IsNullOrWhiteSpace(task.WorkshopId) ? task.Id : task.WorkshopId!;
        var partPath = Path.Combine(AppPaths.DownloadTempDir, key + ".mirror.part");

        try
        {
            if (File.Exists(partPath))
            {
                using var probe = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        catch (IOException)
        {
            partPath = Path.Combine(AppPaths.DownloadTempDir, key + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mirror.part");
            Log.Warn($"镜像通道断点文件被占用，改用新的临时文件：{Path.GetFileName(partPath)}");
        }

        // ② 先加速线路，再原始直链
        var candidates = new List<(string Url, string Label)>
        {
            ($"{AcceleratorBase}?url={Uri.EscapeDataString(url)}", "加速线路"),
            (url, "原始直链"),
        };

        string? lastError = null;
        foreach (var (candidate, label) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await DownloadOnceAsync(candidate, label, partPath, task, info, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success) return result;
            if (result.Permanent) return result;

            lastError = $"{label}：{result.Error}";
            Log.Warn($"镜像通道 {label} 失败：{result.Error}");
        }

        return DownloadResult.Fail(lastError ?? "镜像通道下载失败");
    }

    /// <summary>单次下载（支持断点续传；服务端不支持续传时自动从头写）。</summary>
    private static async Task<DownloadResult> DownloadOnceAsync(
        string url,
        string label,
        string partPath,
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(MirrorReferer);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDelete(partPath);
                return DownloadResult.Fail("断点信息失效，请重试");
            }

            if (!response.IsSuccessStatusCode)
            {
                var permanent = (int)response.StatusCode is 403 or 404 or 410;
                return DownloadResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", permanent);
            }

            var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed) existing = 0;

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var total = contentLength > 0 ? contentLength + existing : Math.Max(info.FileSize, 0);

            progress?.Report(new DownloadProgress(existing, total, 0, $"{label}：{(resumed ? "断点续传中…" : "开始下载…")}"));

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(
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

                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                {
                    var seconds = (elapsed - lastReport).TotalSeconds;
                    var speed = seconds > 0 ? (downloaded - lastBytes) / seconds : 0;
                    progress?.Report(new DownloadProgress(downloaded, total, speed, null));
                    lastBytes = downloaded;
                    lastReport = elapsed;
                }
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Close();

            var finalSize = new FileInfo(partPath).Length;
            if (total > 0 && finalSize < total)
                return DownloadResult.Fail($"下载不完整（{finalSize}/{total} 字节），可重试续传");

            progress?.Report(new DownloadProgress(finalSize, total > 0 ? total : finalSize, 0, "正在校验并安装…"));

            var filePath = DownloadFileHelper.PromoteFile(partPath, task, info);
            Log.Info($"大陆镜像通道下载完成（{label}）：{info.Title} -> {filePath}");
            return DownloadResult.Ok(filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"大陆镜像通道 {label} 异常：{ex.Message}");
            return DownloadResult.Fail(ex.Message);
        }
    }

    /// <summary>问镜像站要这个工坊条目的 vpk 直链。</summary>
    private static async Task<string?> ResolveFileUrlAsync(string workshopId, CancellationToken cancellationToken)
    {
        try
        {
            var api = $"{MirrorApiBase}/detail?id={Uri.EscapeDataString(workshopId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, api);
            request.Headers.Referrer = new Uri(MirrorReferer);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"镜像站 detail 返回 HTTP {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("file_url", out var fileUrl) &&
                fileUrl.ValueKind == JsonValueKind.String)
            {
                var value = fileUrl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Log.Info($"镜像站已给出直链：{workshopId}");
                    return value;
                }
            }

            Log.Warn($"镜像站没有 {workshopId} 的 file_url（可能条目被删除或未收录）");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"镜像站 detail 查询失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>镜像站是否可用：失败后冷却 10 分钟，避免每次下载都白等一次超时。</summary>
    private static bool MirrorAvailable()
    {
        lock (ProbeGate)
        {
            if (DateTime.UtcNow < _probeCoolDownUntil) return false;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var request = new HttpRequestMessage(HttpMethod.Get, MirrorApiBase);
                request.Headers.Referrer = new Uri(MirrorReferer);
                using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    if (_probeNote != "ok")
                    {
                        _probeNote = "ok";
                        Log.Info("大陆镜像加速通道可用（zhrradiant）");
                    }
                    return true;
                }

                Log.Warn($"大陆镜像站返回 HTTP {(int)response.StatusCode}，本次跳过镜像通道");
            }
            catch (Exception ex)
            {
                Log.Warn($"大陆镜像站不可达（{ex.Message}），10 分钟内跳过镜像通道");
            }

            _probeCoolDownUntil = DateTime.UtcNow.AddMinutes(10);
            _probeNote = "failed";
            return false;
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return client;
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
