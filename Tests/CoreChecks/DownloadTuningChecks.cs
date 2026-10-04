using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using PotatoVN.App.PluginBase.Models;
using PotatoVN.App.PluginBase.Services;

namespace CoreChecks;

/// <summary>下载调优回归：用可控的中途断流验证进度、块内续传和暂停水位，不依赖公网。</summary>
internal static class DownloadTuningChecks
{
    private const int ChunkSize = 64 * 1024;
    private const int PartialBytes = 8 * 1024;

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "download-tuning");
        Directory.CreateDirectory(directory);
        var payload = new byte[4 * ChunkSize];
        new Random(904).NextBytes(payload);

        await CheckRetryAsync(directory, payload, check);
        await CheckRetryHeadersAsync(directory, payload, check);
        await CheckChangedChunkSizeAsync(directory, payload, check);
        await CheckPauseAsync(directory, payload, check);
        await CheckSequentialRetryAsync(directory, payload, check);
    }

    private static async Task CheckRetryAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        var requests = new ConcurrentQueue<long>();
        var firstChunkAttempts = 0;
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            requests.Enqueue(start);
            return start == 0 && Interlocked.Increment(ref firstChunkAttempts) == 1
                ? Response(payload, start, end, new InterruptedStream(payload, (int)start, (int)(end - start + 1)))
                : Response(payload, start, end);
        });
        using var service = Service(handler);
        var progress = new List<long>();
        var target = Path.Combine(directory, "retry.bin");
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target, (received, _) =>
        {
            lock (progress) progress.Add(received);
        }));
        check(error is null && Matches(target, payload), $"download tuning: partial retry preserves file bytes ({error?.Message})");
        check(requests.Contains(PartialBytes) && requests.Count(start => start == 0) == 1,
            "download tuning: interrupted chunk requests only its unwritten suffix");
        check(progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second)
              && progress.All(value => value <= payload.Length) && progress.LastOrDefault() == payload.Length,
            "download tuning: progress never rolls back or double counts bytes during retry");
    }

    private static async Task CheckRetryHeadersAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 续传改了 Range 起点后，必须按新的起点和剩余长度校验响应，不能接受原块的旧响应头。
        foreach (var wrongRange in new[] { true, false })
        {
            var attempts = 0;
            using var handler = new Handler(request =>
            {
                var range = request.Headers.Range!.Ranges.Single();
                var start = range.From!.Value;
                var end = range.To!.Value;
                if (start == 0 && end == 0) return Response(payload, start, end);
                if (Interlocked.Increment(ref attempts) == 1)
                    return Response(payload, start, end, new InterruptedStream(payload, (int)start, (int)(end - start + 1)));
                var response = Response(payload, start, end);
                if (wrongRange)
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, end, payload.Length);
                else
                    response.Content.Headers.ContentLength = payload.Length;
                return response;
            });
            using var service = new DownloadService(handler, TimeSpan.FromSeconds(2), payload.Length) { MaxAttempts = 3 };
            var target = Path.Combine(directory, wrongRange ? "retry-range.bin" : "retry-length.bin");
            var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
            check(error is DownloadException && error.Message.Contains(wrongRange ? "范围不符" : "长度不符") && attempts == 2,
                $"download tuning: resumed chunk rejects stale {(wrongRange ? "Content-Range" : "Content-Length")} without another retry ({error?.Message})");
        }
    }

    private static async Task CheckChangedChunkSizeAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 模拟升级块大小后旧水位落在新块中间；已经落盘的前缀不应因为网格变化而重新请求。
        const int previousWatermark = 3 * PartialBytes;
        var target = Path.Combine(directory, "changed-chunk.bin");
        await File.WriteAllBytesAsync(target + ".part", payload.AsMemory(0, previousWatermark).ToArray());
        await File.WriteAllTextAsync(target + ".part.watermark", previousWatermark.ToString());
        var requests = new ConcurrentQueue<long>();
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            requests.Enqueue(start);
            return Response(payload, start, end);
        });
        using var service = Service(handler);
        var progress = new ConcurrentQueue<long>();
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target,
            (received, _) => progress.Enqueue(received)));
        check(error is null && Matches(target, payload) && requests.Min() == previousWatermark,
            $"download tuning: larger chunks resume an unaligned watermark exactly ({error?.Message})");
        check(progress.FirstOrDefault() == previousWatermark,
            "download tuning: initial progress includes the preserved partial chunk");
    }

    private static async Task CheckPauseAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            return start == 0
                ? Response(payload, start, end, new InterruptedStream(payload, (int)start, (int)(end - start + 1), stall: true))
                : Response(payload, start, end);
        });
        using var service = Service(handler);
        var target = Path.Combine(directory, "pause.bin");
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target, (received, _) =>
        {
            // 后面三块全完成、第一块只写了 8 KiB 时暂停，水位必须停在这个真实连续前缀。
            if (received >= payload.Length - ChunkSize + PartialBytes) cancellation.Cancel();
        }, cancellation.Token));
        var watermark = File.Exists(target + ".part.watermark")
            ? long.Parse(await File.ReadAllTextAsync(target + ".part.watermark")) : 0;
        check(error is OperationCanceledException && watermark == PartialBytes,
            $"download tuning: pause saves only the contiguous partial prefix despite later completed chunks (watermark {watermark})");

        using var resumeHandler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return Response(payload, range.From!.Value, range.To!.Value);
        });
        using var resumed = Service(resumeHandler);
        error = await Capture(() => resumed.DownloadAsync(Request(payload.Length), target));
        check(error is null && Matches(target, payload), $"download tuning: resume after a partial-chunk pause keeps content intact ({error?.Message})");
    }

    private static async Task CheckSequentialRetryAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        var attempts = 0;
        var requests = new List<long>();
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range?.From == 0 && range.To == 0)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            var start = range?.From ?? 0;
            requests.Add(start);
            var response = Interlocked.Increment(ref attempts) == 1
                ? Response(payload, start, payload.Length - 1, new InterruptedStream(payload, (int)start, payload.Length - (int)start))
                : Response(payload, start, payload.Length - 1);
            if (range is null)
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Content.Headers.ContentRange = null;
            }
            return response;
        });
        using var service = Service(handler);
        var target = Path.Combine(directory, "sequential-retry.bin");
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
        check(error is null && Matches(target, payload) && requests.SequenceEqual(new long[] { 0, PartialBytes }),
            $"download tuning: sequential retry flushes and resumes the exact written prefix ({error?.Message})");
    }

    private static DownloadService Service(HttpMessageHandler handler) =>
        new(handler, TimeSpan.FromSeconds(2), ChunkSize) { MaxAttempts = 2 };

    private static InstallRequest Request(int length) => new()
    {
        V = 1, Provider = "shionlib", ResourceId = "tuning", Url = "https://download.example/test.bin",
        FileName = "test.bin", ArchiveFormat = "zip", Size = (ulong)length, BgmId = "13", Title = "CLANNAD",
    };

    private static bool Matches(string target, byte[] payload) =>
        File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(payload);

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static HttpResponseMessage Response(byte[] payload, long start, long end, Stream? stream = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(stream ?? new MemoryStream(payload, (int)start, (int)(end - start + 1), writable: false)),
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
        response.Content.Headers.ContentLength = end - start + 1;
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>每次先稳定返回 8 KiB，再主动断流或阻塞到取消，让回退和水位错误可重复触发。</summary>
    private sealed class InterruptedStream(byte[] payload, int start, int count, bool stall = false) : Stream
    {
        private readonly MemoryStream _source = new(payload, start, count, writable: false);
        private int _remaining = PartialBytes;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remaining == 0)
            {
                if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new IOException("test: simulated connection interruption");
            }
            var read = await _source.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
