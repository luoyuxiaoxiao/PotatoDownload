using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Blake3.Managed;
using Microsoft.Win32.SafeHandles;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 下载服务：多线程分块下载 + 断点续传。
/// 支持 Range 的服务器分块并发下载；不支持时自动退化为单连接顺序下载。
/// 断点续传通过 .part 文件 + 水位记录实现，水位 = 从文件头起连续已落盘的字节数，
/// 中断后再次下载只重下水位之后的部分。
/// 本类只保证落盘字节数与声明的 size 一致；哈希校验由调用方在下载完成后执行。
/// </summary>
public class DownloadService : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024; // 每块 4MB
    private const int MaxConnections = 4;          // 分块并发连接数
    private const int MaxRetries = 3;              // 单块失败重试次数
    private const int BufferSize = 81920;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _idleTimeout;

    public DownloadService() : this(CreateSafeHandler(), DefaultIdleTimeout)
    {
    }

    /// <summary>自定义传输层与空闲超时（测试用）。</summary>
    internal DownloadService(HttpMessageHandler handler, TimeSpan idleTimeout)
    {
        _idleTimeout = idleTimeout;
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PotatoVN-PotatoDownload/1.0");
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// 建连时按解析出的 IP 做 SSRF 检查：InstallRequest.Validate 只能看字面主机名，
    /// 域名解析到内网、以及 HTTP 重定向到内网地址都在这里被拒绝。
    /// </summary>
    private static SocketsHttpHandler CreateSafeHandler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = addresses.Where(address => !InstallRequest.IsForbiddenAddress(address)).ToArray();
            if (allowed.Length == 0)
                throw new DownloadException($"下载地址指向本机或内网，已拒绝连接: {host}");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// 下载文件到目标路径（支持断点续传与多线程分块）。
    /// </summary>
    /// <param name="request">安装请求（含 URL、预期大小）</param>
    /// <param name="targetPath">最终文件存放路径（下载完成即挪到此处）</param>
    /// <param name="onProgress">进度回调 (已提交字节, 总字节)</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="log">可选诊断日志（下载路径决策），仅落文字不携带敏感信息</param>
    public async Task DownloadAsync(InstallRequest request, string targetPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default,
        Action<string>? log = null)
    {
        if (request.IsExpired(DateTimeOffset.Now))
            throw new DownloadException("下载直链已过期，请重新从来源提供方获取链接");

        var totalBytes = (long)request.Size;
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == totalBytes)
        {
            // 上次已下载完整（例如在解压/入库阶段失败）——直接复用，由调用方重新校验哈希
            log?.Invoke("target already complete, skip download");
            onProgress?.Invoke(totalBytes, totalBytes);
            return;
        }

        var partPath = targetPath + ".part";
        var watermarkPath = targetPath + ".part.watermark";
        // size 来自深链：预分配前先看磁盘放不放得下，避免把系统盘写满后才报错
        var alreadyOnDisk = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        var free = new DriveInfo(Path.GetDirectoryName(Path.GetFullPath(targetPath))!).AvailableFreeSpace;
        if (free < totalBytes - alreadyOnDisk)
            throw new DownloadException($"磁盘空间不足：需要 {totalBytes - alreadyOnDisk:N0} 字节，可用 {free:N0} 字节");
        try
        {
            if (IsAlreadyComplete(partPath, watermarkPath, totalBytes))
            {
                log?.Invoke("reuse complete .part, skip download");
                onProgress?.Invoke(totalBytes, totalBytes);
            }
            else if (await ProbeRangeAsync(request.Url, ct))
            {
                log?.Invoke($"probe: range supported, chunked download ({(totalBytes + ChunkSize - 1) / ChunkSize} chunks)");
                await DownloadChunkedAsync(request, partPath, watermarkPath, onProgress, ct);
            }
            else
            {
                log?.Invoke("probe: range NOT supported, sequential download");
                await DownloadSequentialAsync(request, partPath, watermarkPath, onProgress, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 不是调用方取消，而是空闲计时器触发：服务器长时间没有返回数据
            throw new DownloadException("连接空闲超时，服务器长时间未返回数据");
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(partPath, targetPath);
        TryDelete(watermarkPath);
    }

    /// <summary>上次的 .part 已完整（水位与文件大小都达到预期值）。</summary>
    private static bool IsAlreadyComplete(string partPath, string watermarkPath, long totalBytes)
    {
        try
        {
            return File.Exists(partPath) &&
                   new FileInfo(partPath).Length == totalBytes &&
                   ReadWatermark(watermarkPath, totalBytes) >= totalBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>探测服务器是否支持 HTTP Range（发一个小 Range 请求看是否回 206）。</summary>
    private async Task<bool> ProbeRangeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var idle = CreateIdleCts(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await SendAsync(request, idle.Token);
            return response.StatusCode == HttpStatusCode.PartialContent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>多线程分块下载：每个连接认领块，按 Range 写入文件对应偏移。</summary>
    private async Task DownloadChunkedAsync(InstallRequest request, string partPath,
        string watermarkPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var totalBytes = (long)request.Size;
        var chunkCount = (int)((totalBytes + ChunkSize - 1) / ChunkSize);

        // 水位是连续前缀：块会乱序完成，只有从头连续完成的字节才能记为水位，续传才不会漏块
        var contiguous = ReadWatermark(watermarkPath, totalBytes);
        var completed = new bool[chunkCount];
        var committed = 0L;
        var pending = new ConcurrentQueue<int>();
        for (var i = 0; i < chunkCount; i++)
        {
            if (ChunkEnd(i, totalBytes) < contiguous)
            {
                completed[i] = true;
                committed += ChunkEnd(i, totalBytes) - (long)i * ChunkSize + 1;
            }
            else
            {
                pending.Enqueue(i);
            }
        }

        // 预分配完整大小；各连接用 RandomAccess 按偏移写入（共享一个 FileStream 的 Seek+Write 不是线程安全的）
        using (var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (stream.Length != totalBytes) stream.SetLength(totalBytes);
        }
        using var handle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Write, FileShare.None,
            FileOptions.Asynchronous);

        var nextContiguousChunk = 0;
        var gate = new object();
        var workers = new Task[Math.Clamp(pending.Count, 1, MaxConnections)];
        for (var w = 0; w < workers.Length; w++)
            workers[w] = Task.Run(async () =>
            {
                while (pending.TryDequeue(out var chunkIndex))
                {
                    ct.ThrowIfCancellationRequested();
                    var start = (long)chunkIndex * ChunkSize;
                    var end = ChunkEnd(chunkIndex, totalBytes);
                    await DownloadChunkWithRetryAsync(request.Url, handle, start, end, ct);
                    lock (gate)
                    {
                        completed[chunkIndex] = true;
                        committed += end - start + 1;
                        while (nextContiguousChunk < chunkCount && completed[nextContiguousChunk])
                            nextContiguousChunk++;
                        var reached = nextContiguousChunk == chunkCount
                            ? totalBytes
                            : (long)nextContiguousChunk * ChunkSize;
                        if (reached > contiguous)
                        {
                            contiguous = reached;
                            SaveWatermark(watermarkPath, contiguous);
                        }
                        onProgress?.Invoke(committed, totalBytes);
                    }
                }
            }, ct);
        await Task.WhenAll(workers);
    }

    private static long ChunkEnd(int chunkIndex, long totalBytes) =>
        Math.Min((long)(chunkIndex + 1) * ChunkSize, totalBytes) - 1;

    private async Task DownloadChunkWithRetryAsync(string url, SafeFileHandle handle,
        long start, long end, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadChunkAsync(url, handle, start, end, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw; // 我们自己判定的确定性错误（越界数据、SSRF 拒绝等），重试无意义
            }
            catch (Exception) when (attempt < MaxRetries - 1)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    private async Task DownloadChunkAsync(string url, SafeFileHandle handle,
        long start, long end, CancellationToken ct)
    {
        using var idle = CreateIdleCts(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await SendAsync(request, idle.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new DownloadException($"服务器未按 Range 返回分块（状态码 {(int)response.StatusCode}）");

        await using var contentStream = await response.Content.ReadAsStreamAsync(idle.Token);
        var buffer = new byte[BufferSize];
        var offset = start;
        var remaining = end - start + 1;
        int read;
        while (remaining > 0 && (read = await ReadAsync(contentStream, buffer, idle)) > 0)
        {
            if (read > remaining)
                throw new DownloadException("服务器返回的数据超出请求的分块范围");
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct);
            offset += read;
            remaining -= read;
        }
        if (remaining > 0)
            throw new IOException($"分块数据不完整，还差 {remaining} 字节");
    }

    /// <summary>单连接顺序下载：服务器不支持 Range 时的退化方案（可断点续传）。</summary>
    private async Task DownloadSequentialAsync(InstallRequest request, string partPath,
        string watermarkPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var totalBytes = (long)request.Size;
        var committed = ReadWatermark(watermarkPath, totalBytes);

        using var idle = CreateIdleCts(ct);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (committed > 0)
            httpRequest.Headers.Range = new RangeHeaderValue(committed, null);
        using var response = await SendAsync(httpRequest, idle.Token);
        response.EnsureSuccessStatusCode();

        if (committed > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            // 服务端忽略了 Range（整包返回）——必须从头覆盖写入，否则会在已有数据后继续追加
            // （2026-09 实测：上次校验失败留下的完整 .part + 水位，续传时整包追加导致 2 倍大小）
            committed = 0;
            SaveWatermark(watermarkPath, 0);
        }
        if (response.Content.Headers.ContentLength is { } declared && declared != totalBytes - committed)
            throw new DownloadException($"文件大小与声明不符：服务器返回 {declared + committed} 字节，期望 {totalBytes}");

        await using var contentStream = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var fileStream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, BufferSize, true);
        // 截掉水位之后的脏数据（防御 .part 比水位记录更长的情况）
        if (fileStream.Length > committed) fileStream.SetLength(committed);
        fileStream.Seek(committed, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        var lastWatermark = committed;
        int read;
        while ((read = await ReadAsync(contentStream, buffer, idle)) > 0)
        {
            if (committed + read > totalBytes)
                throw new DownloadException($"服务器返回的数据超出声明大小 {totalBytes} 字节");
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            committed += read;
            onProgress?.Invoke(committed, totalBytes);
            if (committed - lastWatermark >= ChunkSize)
            {
                // 先落盘再记水位，水位永远不会领先于文件里真正写好的字节
                await fileStream.FlushAsync(ct);
                SaveWatermark(watermarkPath, committed);
                lastWatermark = committed;
            }
        }
        await fileStream.FlushAsync(ct);
        SaveWatermark(watermarkPath, committed);

        if (committed != totalBytes)
            throw new DownloadException($"文件大小校验失败：期望 {totalBytes}，实际 {committed}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is DownloadException rejected)
        {
            throw rejected; // 建连阶段被 SSRF 检查拒绝：把原因直接抛给用户而不是"发送请求时出错"
        }
    }

    private CancellationTokenSource CreateIdleCts(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_idleTimeout);
        return cts;
    }

    /// <summary>每次读取前重新武装空闲计时器：服务器停止发数据超过空闲超时即中止，任务不会永久卡住。</summary>
    private async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationTokenSource idle)
    {
        idle.CancelAfter(_idleTimeout);
        return await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token);
    }

    private static long ReadWatermark(string watermarkPath, long totalBytes)
    {
        try
        {
            if (File.Exists(watermarkPath) &&
                long.TryParse(File.ReadAllText(watermarkPath), out var value))
                return Math.Clamp(value, 0, totalBytes);
        }
        catch
        {
            // ignore
        }
        return 0;
    }

    private static void SaveWatermark(string watermarkPath, long committed)
    {
        try
        {
            File.WriteAllText(watermarkPath, committed.ToString());
        }
        catch
        {
            // 水位写失败不影响下载
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
            // ignore
        }
    }

    /// <summary>
    /// 校验文件哈希（SHA-256 或 BLAKE3）。未提供校验值时直接通过。
    /// </summary>
    public static async Task VerifyChecksumAsync(InstallRequest request, string filePath,
        CancellationToken ct = default)
    {
        if (request.ChecksumAlgo is null || request.Checksum is null) return;

        var actual = await ComputeChecksumAsync(request.ChecksumAlgo, filePath, ct);
        if (!string.Equals(actual, request.Checksum, StringComparison.OrdinalIgnoreCase))
            throw new DownloadException("下载文件哈希校验失败");
    }

    private static async Task<string> ComputeChecksumAsync(string algo, string filePath,
        CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, true);
        return algo switch
        {
            "sha256" => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(),
            "blake3" => await ComputeBlake3Async(stream, ct),
            _ => throw new DownloadException($"不支持的校验算法: {algo}"),
        };
    }

    private static async Task<string> ComputeBlake3Async(Stream stream, CancellationToken ct)
    {
        using var hasher = Hasher.New();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            hasher.Update(buffer.AsSpan(0, read));
        return hasher.Finalize().ToString();
    }
}

public class DownloadException(string message) : Exception(message);
