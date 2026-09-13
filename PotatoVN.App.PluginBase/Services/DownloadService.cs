using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Blake3.Managed;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 下载服务：多线程分块下载 + 断点续传。
/// 支持 Range 的服务器分块并发下载；不支持时自动退化为单连接顺序下载。
/// 断点续传通过 .part 文件 + 水位记录实现，中断后再次下载会从未提交字节继续。
/// </summary>
public class DownloadService
{
    private const int ChunkSize = 4 * 1024 * 1024; // 每块 4MB
    private const int MaxConnections = 4;          // 分块并发连接数
    private const int MaxRetries = 3;              // 单块失败重试次数
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;

    public DownloadService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PotatoVN-PotatoDownload/1.0");
    }

    /// <summary>
    /// 下载文件到目标路径（支持断点续传与多线程分块）。
    /// </summary>
    /// <param name="request">安装请求（含 URL、预期大小、校验值）</param>
    /// <param name="targetPath">最终文件存放路径（下载完成即挪到此处）</param>
    /// <param name="onProgress">进度回调 (已提交字节, 总字节)</param>
    /// <param name="ct">取消令牌</param>
    public async Task DownloadAsync(InstallRequest request, string targetPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        if (request.IsExpired(DateTimeOffset.Now))
            throw new DownloadException("下载直链已过期，请重新从来源提供方获取链接");

        var partPath = targetPath + ".part";
        var watermarkPath = targetPath + ".part.watermark";
        var totalBytes = (long)request.Size;

        if (IsAlreadyComplete(partPath, watermarkPath, totalBytes))
        {
            // 上次留下了完整的 .part（例如在校验/后续步骤失败中断）——直接复用，跳过下载
            onProgress?.Invoke(totalBytes, totalBytes);
        }
        else
        {
            // 探测服务器是否支持 Range
            var rangeSupported = await ProbeRangeAsync(request.Url, ct);
            if (rangeSupported)
                await DownloadChunkedAsync(request, partPath, watermarkPath, onProgress, ct);
            else
                await DownloadSequentialAsync(request, partPath, watermarkPath, onProgress, ct);
        }

        // 校验并挪为最终文件
        await VerifyChecksumAsync(request, partPath, ct);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.StatusCode == System.Net.HttpStatusCode.PartialContent;
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
        var committed = ReadWatermark(watermarkPath, totalBytes);
        var chunkCount = (int)((totalBytes + ChunkSize - 1) / ChunkSize);

        // 已提交水位之前的块视为已完成
        var completedChunks = new bool[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkStart = (long)i * ChunkSize;
            if (chunkStart < committed) completedChunks[i] = true;
        }

        // 文件预分配完整大小，方便各线程乱序写不同偏移
        await using var fileStream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite, BufferSize, true);
        fileStream.SetLength(totalBytes);

        var pending = new ConcurrentQueue<int>();
        for (var i = 0; i < chunkCount; i++)
            if (!completedChunks[i]) pending.Enqueue(i);

        var workers = new Task[MaxConnections];
        for (var w = 0; w < MaxConnections; w++)
            workers[w] = Task.Run(async () =>
            {
                while (pending.TryDequeue(out var chunkIndex))
                {
                    ct.ThrowIfCancellationRequested();
                    var start = (long)chunkIndex * ChunkSize;
                    var end = Math.Min(start + ChunkSize, totalBytes) - 1;
                    await DownloadChunkWithRetryAsync(request.Url, fileStream, start, end, ct);
                    Interlocked.Add(ref committed, end - start + 1);
                    onProgress?.Invoke(committed, totalBytes);
                    SaveWatermark(watermarkPath, committed);
                }
            }, ct);
        await Task.WhenAll(workers);
    }

    private async Task DownloadChunkWithRetryAsync(string url, FileStream fileStream,
        long start, long end, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[BufferSize];
                var offset = start;
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    offset += read;
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < MaxRetries - 1)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    /// <summary>单连接顺序下载：服务器不支持 Range 时的退化方案（可断点续传）。</summary>
    private async Task DownloadSequentialAsync(InstallRequest request, string partPath,
        string watermarkPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var totalBytes = (long)request.Size;
        var committed = ReadWatermark(watermarkPath, totalBytes);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (committed > 0)
            httpRequest.Headers.Range = new RangeHeaderValue(committed, null);
        using var response = await _httpClient.SendAsync(httpRequest,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (committed > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // 服务端忽略了 Range（整包返回）——必须从头覆盖写入，否则会在已有数据后继续追加
            // （2026-09 实测：上次校验失败留下的完整 .part + 水位，续传时整包追加导致 2 倍大小）
            committed = 0;
            SaveWatermark(watermarkPath, 0);
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, BufferSize, true);
        // 截掉水位之后的脏数据（防御 .part 比水位记录更长的情况）
        if (fileStream.Length > committed) fileStream.SetLength(committed);
        fileStream.Seek(committed, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            committed += read;
            onProgress?.Invoke(committed, totalBytes);
            SaveWatermark(watermarkPath, committed);
        }

        if (committed != totalBytes)
            throw new DownloadException($"文件大小校验失败：期望 {totalBytes}，实际 {committed}");
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
