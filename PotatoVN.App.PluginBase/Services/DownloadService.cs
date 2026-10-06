using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
/// 重试策略对齐 ReinaManager（takanawa）：网络错误/空闲超时/408/429/5xx 指数退避重试，
/// 确定性错误（越界数据、范围不符、4xx 拒绝）不重试；任一块彻底失败立即中止整个下载。
/// </summary>
public class DownloadService : IDisposable
{
    private const int DefaultChunkSize = 64 * 1024 * 1024; // 大文件每块 64MiB，减少请求切换和代理租约释放造成的空档
    private const int MinimumChunkSize = 16 * 1024 * 1024; // 小文件保留原有并发能力，不为凑大块而减少活跃连接
    // 分块并发连接数。Shionlib 下载代理按会话最多 8 个租约（ReinaManager 也开 8），租约要等响应管道结束才释放，
    // 留 2 个名额给释放延迟与探测请求；真撞上限也只是 429 → 退避重试，不致命
    private const int MaxConnections = 6;
    private const int DefaultMaxAttempts = 5;             // 单个请求（探测/分块/顺序）最多尝试次数
    private const int BufferSize = 81920;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private const int MaxRedirects = 5;           // 手动跟随重定向的最大跳数（自动重定向已关，每跳都要过守卫）

    private readonly HttpClient? _singleClient;   // 测试注入的单一客户端：不做目标守卫与代理降级
    private readonly Func<bool, HttpMessageHandler>? _handlerFactory; // null = 生产默认 CreateSafeHandler
    private readonly object _clientsGate = new();
    private HttpClient? _proxyClient;
    private HttpClient? _directClient;
    private readonly TimeSpan _idleTimeout;
    private readonly int _chunkSize;
    private volatile bool _proxyBroken;  // 本实例（=一次下载任务）内共享：代理连不上就降级直连，不再每个连接都撞墙
    private IWebProxy? _taskProxy;       // 任务开始时快照系统代理：新任务重读设置，改代理不必重启宿主
    private Action<string>? _log;

    /// <summary>瞬时错误的最多尝试次数（测试用）。</summary>
    internal int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>代理选择策略（测试用）：null 时用任务快照的系统代理及其绕过列表判定。</summary>
    internal Func<Uri, bool>? ProxySelector { get; init; }

    /// <summary>目标守卫开关（测试用）：本地回环模拟服务器过不了守卫，纯传输层用例需显式关闭。</summary>
    internal bool DisableTargetGuard { get; init; }

    public DownloadService()
    {
        _idleTimeout = DefaultIdleTimeout;
        _chunkSize = DefaultChunkSize;
    }

    /// <summary>自定义传输层、空闲超时与块大小（测试用）。</summary>
    internal DownloadService(HttpMessageHandler handler, TimeSpan idleTimeout, int chunkSize = DefaultChunkSize)
    {
        _idleTimeout = idleTimeout;
        _chunkSize = chunkSize;
        _singleClient = CreateClient(handler);
    }

    /// <summary>传输层工厂（测试用）：true 造代理通道、false 造直连通道；启用目标守卫与代理降级。</summary>
    internal DownloadService(Func<bool, HttpMessageHandler> handlerFactory, TimeSpan idleTimeout,
        int chunkSize = DefaultChunkSize)
    {
        _idleTimeout = idleTimeout;
        _chunkSize = chunkSize;
        _handlerFactory = handlerFactory;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PotatoVN-PotatoDownload/1.0");
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity"); // 分块按字节偏移落盘，不许服务端压缩
        return client;
    }

    private HttpClient ClientFor(bool useProxy)
    {
        if (_singleClient is not null) return _singleClient;
        lock (_clientsGate)
        {
            var factory = _handlerFactory ?? CreateSafeHandler;
            return useProxy
                ? (_proxyClient ??= CreateClient(factory(true)))
                : (_directClient ??= CreateClient(factory(false)));
        }
    }

    public void Dispose()
    {
        lock (_clientsGate)
        {
            _singleClient?.Dispose();
            _proxyClient?.Dispose();
            _directClient?.Dispose();
        }
    }

    /// <summary>
    /// SSRF 守卫分两层：请求层（SendAsync 里校验 RequestUri.Host 的解析结果，每跳重定向都查）对两种
    /// 通道都生效；直连通道额外在建连回调里「解析+校验+钉住解析结果」，防 DNS 重绑定。
    /// 走代理时建连端点是代理本身（常为 127.0.0.1）——代理是用户自己的配置，不是 SSRF 目标，
    /// 绝不能在建连层拦它（issue #1：代理被误判内网导致整功能不可用）。
    /// 注意：代理通道下请求层只查客户端本地 DNS，实际解析的是代理端，不抗代理端 DNS 重绑定，
    /// 仅防误配/顺手；代理是用户可信配置且下载内容只落盘无回读，威胁模型上可接受。
    /// </summary>
    private static SocketsHttpHandler CreateSafeHandler(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            AllowAutoRedirect = false, // 重定向由请求层守卫逐跳校验后手动跟随
        };
        if (useProxy)
        {
            // 显式快照当前系统代理：HttpClient.DefaultProxy 是进程级缓存，用户改了系统代理
            // 要重启宿主才生效；这里每个任务读一次，随改随用
            handler.Proxy = CurrentSystemProxy();
            return handler;
        }
        handler.ConnectCallback = ConnectDirectChecked;
        return handler;
    }

    /// <summary>直连建连回调：解析、拒绝全禁地址，再钉住解析结果建连（防校验与建连之间的 DNS 重绑定）。</summary>
    private static async ValueTask<Stream> ConnectDirectChecked(SocketsHttpConnectionContext context, CancellationToken ct)
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
    }

    private static IWebProxy? CurrentSystemProxy()
    {
        try
        {
#pragma warning disable SYSLIB0014 // 唯一能拿到含绕过列表/PAC 的系统代理的 API；WebRequest 本体虽过时，这个入口没有替代品
            return WebRequest.GetSystemWebProxy();
#pragma warning restore SYSLIB0014
        }
        catch
        {
            return null; // 读不到系统代理按直连处理（直连通道自带守卫）
        }
    }

    /// <summary>
    /// 请求层 SSRF 守卫：校验「目标」而非连接端点。字面 IP 与 localhost/.lan 等后缀直接判；
    /// 域名先解析，全部地址都禁才拒绝（与建连层同语义）。DNS 解析失败抛 SocketException，按瞬时错误重试。
    /// </summary>
    internal static async Task EnsureTargetAllowedAsync(Uri uri, CancellationToken ct = default)
    {
        var host = uri.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            if (InstallRequest.IsPrivateOrLoopbackHost(host))
                throw new DownloadException($"下载地址指向本机或内网，已拒绝连接: {host}");
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        if (addresses.All(InstallRequest.IsForbiddenAddress))
            throw new DownloadException($"下载地址指向本机或内网，已拒绝连接: {host}");
    }

    private bool ShouldUseProxy(Uri uri)
    {
        if (_singleClient is not null || _proxyBroken) return false;
        if (ProxySelector is { } selector) return selector(uri);
        try
        {
            return _taskProxy is not null && !_taskProxy.IsBypassed(uri);
        }
        catch
        {
            return false; // 代理判定失败时直连：判定都失败多半也用不了，直连通道自带守卫兜底
        }
    }

    /// <summary>任务开始时落一次通道选择（只记结论不记直链：签名 URL 绝不进日志）。</summary>
    private void LogChannelChoice(InstallRequest request, Action<string>? log)
    {
        if (log is null || _singleClient is not null) return;
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            log("proxy: target URL invalid, direct");
            return;
        }
        if (ShouldUseProxy(uri))
        {
            var via = _taskProxy?.GetProxy(uri)?.Authority ?? "proxy";
            log($"proxy: trying {via} first, fallback to direct on connect failure");
        }
        else
        {
            log("proxy: none configured or target bypassed, direct");
        }
    }

    /// <summary>
    /// 下载文件到目标路径（支持断点续传与多线程分块）。
    /// </summary>
    /// <param name="request">安装请求（含 URL、预期大小）</param>
    /// <param name="targetPath">最终文件存放路径（下载完成即挪到此处）</param>
    /// <param name="onProgress">进度回调 (已写入字节, 总字节)；每次写入都会回调，可能来自多个后台线程，
    /// 分块并发时按累计字节递增顺序串行通知；顺序下载被服务端拒绝续传时仍需从 0 重启</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="log">可选诊断日志（下载路径决策、重试），仅落文字不携带敏感信息</param>
    public async Task DownloadAsync(InstallRequest request, string targetPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default,
        Action<string>? log = null)
    {
        if (request.IsExpired(DateTimeOffset.Now))
            throw new DownloadException("下载直链已过期，请重新从来源提供方获取链接");

        _log = log;
        _taskProxy = _singleClient is null && ProxySelector is null ? CurrentSystemProxy() : null;
        LogChannelChoice(request, log);

        var totalBytes = (long)request.Size;
        var hasHash = request.ChecksumAlgo is not null && request.Checksum is not null;
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == totalBytes)
        {
            if (hasHash)
            {
                // 上次已下载完整（例如在解压/入库阶段失败）——直接复用，由调用方重新校验哈希
                log?.Invoke("target already complete, skip download");
                onProgress?.Invoke(totalBytes, totalBytes);
                return;
            }
            if (await ConfirmServerTotalAsync(request.Url, totalBytes, ct, log))
            {
                // 无哈希时只确认了长度一致，内容仍未校验（同长损坏需解压或源哈希发现）
                log?.Invoke("target length matches server total, skip download (content unverified without hash)");
                onProgress?.Invoke(totalBytes, totalBytes);
                return;
            }
            log?.Invoke("target length matches pushed size but server total unconfirmed, re-downloading");
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
            var reusedCompletePart = false;
            if (IsAlreadyComplete(partPath, watermarkPath, totalBytes))
            {
                if (hasHash)
                {
                    log?.Invoke("reuse complete .part, skip download");
                    onProgress?.Invoke(totalBytes, totalBytes);
                    reusedCompletePart = true;
                }
                else if (await ConfirmServerTotalAsync(request.Url, totalBytes, ct, log))
                {
                    log?.Invoke("reuse complete .part, skip download (content unverified without hash)");
                    onProgress?.Invoke(totalBytes, totalBytes);
                    reusedCompletePart = true;
                }
                else
                {
                    // 无法确认服务器总长：不按本地长度宣告完成，删掉看似完整的 .part 走正常下载
                    log?.Invoke("complete .part length unconfirmed by server, re-downloading");
                    TryDelete(partPath);
                    TryDelete(watermarkPath);
                }
            }
            if (!reusedCompletePart)
            {
                if (await ProbeRangeAsync(request.Url, totalBytes, ct, log))
                {
                    var chunkSize = GetChunkSize(totalBytes);
                    log?.Invoke($"probe: range supported, chunked download ({(totalBytes + chunkSize - 1) / chunkSize} chunks, up to {MaxConnections} connections, chunk size {chunkSize} bytes)");
                    await DownloadChunkedAsync(request, partPath, watermarkPath, chunkSize, onProgress, ct, log);
                }
                else
                {
                    log?.Invoke("probe: range NOT supported, sequential download");
                    await RetryAsync(() => DownloadSequentialAsync(request, partPath, watermarkPath, onProgress, ct),
                        ct, log, "sequential download");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 不是调用方取消，而是空闲计时器触发（重试耗尽）：服务器长时间没有返回数据
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

    /// <summary>
    /// 探测服务器是否支持 HTTP Range（发一个 1 字节 Range 请求看是否回 206）。
    /// 瞬时错误按统一策略重试——探测时撞上一次 429/5xx 就静默退化成单连接会把整个下载拖慢数倍；
    /// 明确拒绝（401/403/404 等）直接失败，避免用一条更难懂的顺序下载错误代替真正原因。
    /// 206 必须携带与推送 size 一致的总长，否则说明推送 size 偏小，直接失败不把前缀当完整文件。
    /// </summary>
    private Task<bool> ProbeRangeAsync(string url, long expectedTotal, CancellationToken ct, Action<string>? log) =>
        RetryAsync(async () =>
        {
            using var idle = CreateIdleCts(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await SendAsync(request, idle.Token);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                ValidateRangeResponse(response, 0, 0, expectedTotal);
                // 只读预期的 1 个字节并确认数据确实到达，不再无上限 ReadAsByteArrayAsync
                await using var contentStream = await response.Content.ReadAsStreamAsync(idle.Token);
                var probeBuffer = new byte[1];
                var got = await ReadAsync(contentStream, probeBuffer, idle);
                if (got != 1)
                    throw new DownloadException($"探测返回数据不完整：期望 1 字节，实际 {got} 字节");
                return true;
            }
            ThrowIfFailed(response);
            return false; // 2xx 但没按 Range 返回：服务器不支持 Range
        }, ct, log, "probe");

    /// <summary>非成功状态码分类：408/429/5xx 是瞬时错误（进入重试），其余直接判定失败。</summary>
    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is >= 200 and < 300) return;
        if (code is 408 or 429 or >= 500)
            throw new HttpRequestException($"服务器暂时不可用（HTTP {code}）", null, response.StatusCode);
        throw new DownloadException($"服务器拒绝了下载请求（HTTP {code}），直链可能已失效");
    }

    /// <summary>
    /// 统一校验 206 分块响应：Content-Range 必须为 bytes、From/To 与请求一致、
    /// Length（资源总长）必须已知且等于推送 size；同时保留 Content-Length 等于本次请求长度的检查。
    /// 总长不一致说明推送 size 偏小（或服务器资源已变），把截断前缀当完整文件会得到坏包，必须直接失败。
    /// </summary>
    private static void ValidateRangeResponse(HttpResponseMessage response, long start, long end, long expectedTotal)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is null)
            throw new DownloadException($"服务器未返回 Content-Range，无法确认总大小：推送 {expectedTotal} 字节，请求范围 {start}-{end}");
        if (!string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException($"服务器返回的范围单位不受支持：{range.Unit}（推送 {expectedTotal} 字节）");
        if (range.From != start || range.To != end)
            throw new DownloadException($"服务器返回的分块范围不符：请求 {start}-{end}，返回 {range.From}-{range.To}");
        if (range.Length is null)
            throw new DownloadException($"服务器未返回资源总大小，无法确认完整性：推送 {expectedTotal} 字节，请求范围 {start}-{end}");
        if (range.Length.Value != expectedTotal)
            throw new DownloadException($"服务器声明的总大小与推送不一致：推送 {expectedTotal} 字节，服务器 {range.Length.Value} 字节");
        var expectedLength = end - start + 1;
        if (response.Content.Headers.ContentLength is { } declared && declared != expectedLength)
            throw new DownloadException($"服务器返回的分块长度不符：期望 {expectedLength}，返回 {declared}");
    }

    /// <summary>
    /// 无哈希历史复用前的联网确认：206 走与分块相同的总长校验；200 只有完整 Content-Length 等于推送 size
    /// 才算确认；无长度信息时返回 false（不按本地长度宣告完成，走正常下载）。
    /// 总长不一致直接抛确定性错误（含推送与服务器字节数），不重试。
    /// </summary>
    private Task<bool> ConfirmServerTotalAsync(string url, long expectedTotal, CancellationToken ct, Action<string>? log) =>
        RetryAsync(async () =>
        {
            using var idle = CreateIdleCts(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await SendAsync(request, idle.Token);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                ValidateRangeResponse(response, 0, 0, expectedTotal);
                await using var contentStream = await response.Content.ReadAsStreamAsync(idle.Token);
                var buffer = new byte[1];
                var got = await ReadAsync(contentStream, buffer, idle);
                if (got != 1)
                    throw new DownloadException($"探测返回数据不完整：期望 1 字节，实际 {got} 字节");
                return true;
            }
            ThrowIfFailed(response);
            if (response.Content.Headers.ContentLength is { } length)
            {
                if (length != expectedTotal)
                    throw new DownloadException($"服务器声明的总大小与推送不一致：推送 {expectedTotal} 字节，服务器 {length} 字节");
                return true;
            }
            return false;
        }, ct, log, "confirm total");

    /// <summary>
    /// 瞬时错误重试（对齐 takanawa：指数退避 1/2/4/8s）。网络错误、空闲超时、408/429/5xx 可重试；
    /// 调用方取消与 <see cref="DownloadException"/>（确定性错误）立即抛出。
    /// </summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct, Action<string>? log, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw;
            }
            catch (Exception e) when (attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(8, 1 << (attempt - 1))); // ≥ Shionlib 限流回复的 Retry-After: 1
                var reason = e is OperationCanceledException ? "idle timeout" : $"{e.GetType().Name}: {e.Message}";
                log?.Invoke($"{what}: attempt {attempt}/{MaxAttempts} failed ({reason}), retrying in {delay.TotalSeconds:F0}s");
                await Task.Delay(delay, ct);
            }
        }
    }

    private Task RetryAsync(Func<Task> action, CancellationToken ct, Action<string>? log, string what) =>
        RetryAsync(async () =>
        {
            await action();
            return 0;
        }, ct, log, what);

    /// <summary>多线程分块下载：每个连接认领块，按 Range 写入文件对应偏移。</summary>
    private async Task DownloadChunkedAsync(InstallRequest request, string partPath,
        string watermarkPath, int chunkSize, Action<long, long>? onProgress, CancellationToken ct, Action<string>? log)
    {
        var totalBytes = (long)request.Size;
        var chunkCount = (int)((totalBytes + chunkSize - 1) / chunkSize);

        // 水位是连续前缀：块会乱序完成，只有从头连续完成的字节才能记为水位，续传才不会漏块
        var contiguous = File.Exists(partPath)
            ? ReadWatermark(watermarkPath, Math.Min(totalBytes, new FileInfo(partPath).Length)) : 0;
        var completed = new bool[chunkCount];
        var offsets = new long[chunkCount]; // 每块下一个未写入的位置，只有认领该块的 worker 会推进
        var received = contiguous; // 包含旧水位在新块中间的部分，不因调整块大小而重下已有前缀
        var pending = new ConcurrentQueue<int>();
        for (var i = 0; i < chunkCount; i++)
        {
            offsets[i] = Math.Clamp(contiguous, ChunkStart(i, chunkSize), ChunkEnd(i, chunkSize, totalBytes) + 1);
            if (ChunkEnd(i, chunkSize, totalBytes) < contiguous)
            {
                completed[i] = true;
            }
            else
            {
                pending.Enqueue(i);
            }
        }
        onProgress?.Invoke(received, totalBytes);

        // 预分配完整大小；各连接用 RandomAccess 按偏移写入（共享一个 FileStream 的 Seek+Write 不是线程安全的）
        using (var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (stream.Length != totalBytes) stream.SetLength(totalBytes);
        }
        using var handle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Write, FileShare.None,
            FileOptions.Asynchronous);

        // 任一块彻底失败（重试耗尽/确定性错误）立即中止其余连接。否则幸存连接会以越来越低的并发把队列耗完，
        // 表现为「越下越慢」，而失败要等它们全部结束才浮现（2026-09 线上症状）
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = abort.Token;
        var nextContiguousChunk = 0;
        var gate = new object();
        var progressGate = new object();
        var workers = new Task[Math.Clamp(pending.Count, 1, MaxConnections)];
        for (var w = 0; w < workers.Length; w++)
            workers[w] = Task.Run(async () =>
            {
                try
                {
                    while (pending.TryDequeue(out var chunkIndex))
                    {
                        token.ThrowIfCancellationRequested();
                        var end = ChunkEnd(chunkIndex, chunkSize, totalBytes);
                        // 瞬时断流保留本块已经写好的字节，下一次只请求剩余部分；整块回退会把 UI 的速度增量拉成负数。
                        await RetryAsync(() => DownloadChunkAsync(request.Url, handle, offsets[chunkIndex], end, totalBytes,
                                delta =>
                                {
                                    offsets[chunkIndex] += delta;
                                    // Interlocked.Add 后再回调仍可能乱序，计数和通知必须在同一临界区内完成。
                                    lock (progressGate)
                                    {
                                        received += delta;
                                        onProgress?.Invoke(received, totalBytes);
                                    }
                                }, token),
                            token, log, $"chunk {chunkIndex}");
                        lock (gate)
                        {
                            completed[chunkIndex] = true;
                            while (nextContiguousChunk < chunkCount && completed[nextContiguousChunk])
                                nextContiguousChunk++;
                            var reached = nextContiguousChunk == chunkCount
                                ? totalBytes
                                : ChunkStart(nextContiguousChunk, chunkSize);
                            if (reached > contiguous)
                            {
                                contiguous = reached;
                                SaveWatermark(watermarkPath, contiguous);
                            }
                        }
                    }
                }
                catch
                {
                    abort.Cancel();
                    throw;
                }
            }, token);
        try
        {
            await Task.WhenAll(workers);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // WhenAll 抛的是数组里第一个失败的任务，可能只是被中止的旁路连接（OCE）——找真正的根因抛出
            var root = workers.Select(worker => worker.Exception?.GetBaseException())
                .FirstOrDefault(e => e is not null and not OperationCanceledException);
            if (root is not null) ExceptionDispatchInfo.Capture(root).Throw();
            throw;
        }
        finally
        {
            // 所有 worker 都已停止，再保存第一个未完成块的有效前缀；不能跨过空洞，也不丢掉大块内的暂停进度。
            // RandomAccess.WriteAsync 完成后才推进 offsets，因此这里不会把尚未写入的字节记成水位。
            var watermark = 0L;
            for (var i = 0; i < chunkCount; i++)
            {
                watermark = offsets[i];
                if (watermark < ChunkEnd(i, chunkSize, totalBytes) + 1) break;
            }
            SaveWatermark(watermarkPath, watermark);
        }
    }

    private int GetChunkSize(long totalBytes) =>
        (int)Math.Min(_chunkSize, Math.Max(MinimumChunkSize, (totalBytes + MaxConnections - 1) / MaxConnections));

    private static long ChunkStart(int chunkIndex, int chunkSize) => (long)chunkIndex * chunkSize;

    private static long ChunkEnd(int chunkIndex, int chunkSize, long totalBytes) =>
        Math.Min((long)(chunkIndex + 1) * chunkSize, totalBytes) - 1;

    /// <summary>下载分块尚未写入的范围。<paramref name="report"/> 每次写入报告正增量，失败重试保留此前已写的前缀。</summary>
    private async Task DownloadChunkAsync(string url, SafeFileHandle handle,
        long start, long end, long expectedTotal, Action<long> report, CancellationToken ct)
    {
        if (start > end) return;
        using var idle = CreateIdleCts(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await SendAsync(request, idle.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            ThrowIfFailed(response);
            throw new DownloadException($"服务器未按 Range 返回分块（状态码 {(int)response.StatusCode}）");
        }
        ValidateRangeResponse(response, start, end, expectedTotal);
        var length = end - start + 1;

        await using var contentStream = await response.Content.ReadAsStreamAsync(idle.Token);
        var buffer = new byte[BufferSize];
        var written = 0L;
        int read;
        while (written < length && (read = await ReadAsync(contentStream, buffer, idle)) > 0)
        {
            if (written + read > length)
                throw new DownloadException("服务器返回的数据超出请求的分块范围");
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), start + written, ct);
            written += read;
            report(read);
        }
        if (written < length)
            throw new IOException($"分块数据不完整，还差 {length - written} 字节");
    }

    /// <summary>单连接顺序下载：服务器不支持 Range 时的退化方案（可断点续传，重试时按水位续传）。</summary>
    private async Task DownloadSequentialAsync(InstallRequest request, string partPath,
        string watermarkPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var totalBytes = (long)request.Size;
        var committed = File.Exists(partPath)
            ? ReadWatermark(watermarkPath, Math.Min(totalBytes, new FileInfo(partPath).Length)) : 0;

        using var idle = CreateIdleCts(ct);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (committed > 0)
            httpRequest.Headers.Range = new RangeHeaderValue(committed, null);
        using var response = await SendAsync(httpRequest, idle.Token);
        ThrowIfFailed(response);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            // 顺序续传同样校验 Content-Range：From 必须等于续传点，To/Length 必须等于推送总量
            ValidateRangeResponse(response, committed, totalBytes - 1, totalBytes);
        }
        else if (committed > 0)
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
        onProgress?.Invoke(committed, totalBytes);
        try
        {
            int read;
            while ((read = await ReadAsync(contentStream, buffer, idle)) > 0)
            {
                if (committed + read > totalBytes)
                    throw new DownloadException($"服务器返回的数据超出声明大小 {totalBytes} 字节");
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                committed += read;
                onProgress?.Invoke(committed, totalBytes);
                if (committed - lastWatermark >= Math.Min(_chunkSize, MinimumChunkSize))
                {
                    // 顺序模式仍按原有 16MiB 间隔记水位，不随并发块大小一起放大。
                    await fileStream.FlushAsync(ct);
                    SaveWatermark(watermarkPath, committed);
                    lastWatermark = committed;
                }
            }

            if (committed != totalBytes)
                throw new IOException($"连接中断：期望 {totalBytes} 字节，实际 {committed}");
        }
        finally
        {
            // 中断时也先刷出文件缓冲再记精确水位；取消令牌此时可能已触发，不能再用它打断这一步收尾。
            await fileStream.FlushAsync(CancellationToken.None);
            SaveWatermark(watermarkPath, committed);
        }
    }

    /// <summary>
    /// 发送请求：先过请求层 SSRF 守卫，再按系统代理判定走代理/直连；代理连不上自动降级直连重试一次。
    /// 重定向手动跟随（最多 <see cref="MaxRedirects"/> 跳），每一跳重新校验目标。
    /// 调用方只拥有首个 request 的所有权；跟随重定向时内部新建的请求由本方法负责释放。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var owned = false;
        var redirects = 0;
        // 测试注入单一传输层时不启用守卫（本地模拟服务器就是 127.0.0.1）
        var guardEnabled = _singleClient is null && !DisableTargetGuard;
        while (true)
        {
            if (guardEnabled) await EnsureTargetAllowedAsync(request.RequestUri!, ct);
            HttpResponseMessage response;
            try
            {
                response = await SendOnceAsync(request, ct);
            }
            catch
            {
                if (owned) request.Dispose();
                throw;
            }
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                if (owned) request.Dispose();
                return response;
            }
            response.Dispose();
            if (redirects++ >= MaxRedirects)
            {
                if (owned) request.Dispose();
                throw new DownloadException("重定向次数过多，已放弃下载");
            }
            var next = new Uri(request.RequestUri!, location);
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            {
                if (owned) request.Dispose();
                throw new DownloadException($"重定向到不支持的协议: {next.Scheme}");
            }
            var followed = CloneRequest(request, next);
            if (owned) request.Dispose();
            request = followed;
            owned = true;
        }
    }

    /// <summary>单次发送。代理通道建连失败（MSIX 拦回环代理 / 代理进程已退出）时降级直连并重试。</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!ShouldUseProxy(request.RequestUri!))
            return await SendUnwrappingAsync(ClientFor(false), request, ct);
        try
        {
            return await SendUnwrappingAsync(ClientFor(true), request, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException se)
        {
            // ResponseHeadersRead 之后的中途失败在读流阶段抛出，到不了这里——这里一定是建连阶段失败。
            // 典型：MSIX 无回环豁免，连 127.0.0.1:7890 被系统拒绝（AccessDenied，立即返回不会卡）。
            _proxyBroken = true;
            _log?.Invoke($"proxy unreachable ({se.SocketErrorCode}), falling back to direct connection");
            // HttpRequestMessage 不能重复发送，降级重试必须克隆
            return await SendUnwrappingAsync(ClientFor(false), CloneRequest(request), ct);
        }
    }

    private static async Task<HttpResponseMessage> SendUnwrappingAsync(HttpClient client,
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is DownloadException rejected)
        {
            throw rejected; // 直连建连守卫的拒绝：把原因直接抛给用户而不是"发送请求时出错"
        }
    }

    /// <summary>克隆一个 GET 请求（HttpRequestMessage 发送过一次就不能再发）。下载只发 GET，无正文要处理。</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, Uri? uri = null)
    {
        var clone = new HttpRequestMessage(request.Method, uri ?? request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

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
