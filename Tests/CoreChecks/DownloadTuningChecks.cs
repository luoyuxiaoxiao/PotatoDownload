using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        await CheckTotalMismatchAsync(directory, payload, check);
        await CheckProbeThenBlockChangedAsync(directory, payload, check);
        await CheckSequentialRangeMismatchAsync(directory, payload, check);
        await CheckMissingTotalAsync(directory, payload, check);
        await CheckNoHashReuseAsync(directory, payload, check);
        await CheckRetryAfterAsync(directory, payload, check);
        CheckAimdThrottle(check);
        await CheckHandoffAsync(directory, payload, check);
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

    private static async Task CheckTotalMismatchAsync(string directory, byte[] real, Action<bool, string> check)
    {
        // 推送 size 比真实资源小：各块 From/To 与响应长度都对，但 Content-Range 总长更大。
        // 旧代码忽略 Length 会把前缀当完整文件产出；新代码必须拒绝且不产生最终文件。
        var pushed = 3 * ChunkSize;
        var target = Path.Combine(directory, "size-mismatch.bin");
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return ResponseWithTotal(real, range.From!.Value, range.To!.Value, real.Length);
        });
        using var service = Service(handler);
        var error = await Capture(() => service.DownloadAsync(Request(pushed), target));
        check(error is DownloadException && error.Message.Contains(pushed.ToString())
              && error.Message.Contains(real.Length.ToString()),
            $"download integrity: pushed size smaller than server total is rejected with both sizes ({error?.Message})");
        check(!File.Exists(target),
            "download integrity: truncated prefix is not promoted to final file");
    }

    private static async Task CheckProbeThenBlockChangedAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 探测 0-0 总长正常，但后续块总长变大：同样不能当完整文件。
        var target = Path.Combine(directory, "probe-then-changed.bin");
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            if (start == ChunkSize) return ResponseWithTotal(payload, start, end, payload.Length + ChunkSize);
            return Response(payload, start, end);
        });
        using var service = Service(handler);
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
        check(error is DownloadException && error.Message.Contains(payload.Length.ToString()),
            $"download integrity: total change after a good probe is rejected ({error?.Message})");
        check(!File.Exists(target),
            "download integrity: probe-then-changed does not produce final file");
    }

    private static async Task CheckSequentialRangeMismatchAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 强制走顺序续传（探测回 200），续传 206 的 From 与续传点不符。
        var target = Path.Combine(directory, "sequential-range.bin");
        await File.WriteAllBytesAsync(target + ".part", payload.AsSpan(0, PartialBytes).ToArray());
        await File.WriteAllTextAsync(target + ".part.watermark", PartialBytes.ToString());
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range?.From == 0 && range?.To == 0)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            var start = range?.From ?? 0;
            var response = Response(payload, start, payload.Length - 1);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, payload.Length - 1, payload.Length);
            return response;
        });
        using var service = Service(handler);
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
        check(error is DownloadException && error.Message.Contains("范围不符"),
            $"download integrity: sequential resume with wrong Content-Range is rejected ({error?.Message})");
        check(!File.Exists(target),
            "download integrity: bad sequential resume does not produce final file");
    }

    private static async Task CheckMissingTotalAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 206 缺少 Content-Range，或总长未知（bytes start-end/*），都不能当完整文件。
        foreach (var kind in new[] { "missing", "unknown" })
        {
            using var handler = new Handler(request =>
            {
                var range = request.Headers.Range!.Ranges.Single();
                var start = range.From!.Value;
                var end = range.To!.Value;
                if (start == 0 && end == 0) return Response(payload, start, end);
                var response = Response(payload, start, end);
                if (kind == "missing")
                    response.Content.Headers.ContentRange = null;
                else
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end);
                return response;
            });
            using var service = Service(handler);
            var target = Path.Combine(directory, $"missing-total-{kind}.bin");
            var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
            var want = kind == "missing" ? "Content-Range" : "总大小";
            check(error is DownloadException && error.Message.Contains(want),
                $"download integrity: 206 without {(kind == "missing" ? "Content-Range" : "total length")} is rejected ({error?.Message})");
            check(!File.Exists(target),
                $"download integrity: {kind} total does not produce final file");
        }
    }

    private static async Task CheckNoHashReuseAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 无哈希历史复用：目标/.part 看似完整时必须联网确认总长；有哈希才允许离线复用。
        var okTarget = Path.Combine(directory, "reuse-target-ok.bin");
        TryDelete(okTarget); TryDelete(okTarget + ".part"); TryDelete(okTarget + ".part.watermark");
        await File.WriteAllBytesAsync(okTarget, payload);
        var okRequests = new ConcurrentQueue<string>();
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            okRequests.Enqueue($"{range.From}-{range.To}");
            return Response(payload, range.From!.Value, range.To!.Value);
        }))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(Request(payload.Length), okTarget));
            check(error is null && Matches(okTarget, payload) && okRequests.Count == 1,
                $"download integrity: no-hash target confirmed by server reuses without re-download ({error?.Message}, {okRequests.Count} requests)");
        }

        var real = payload;
        var pushed = 3 * ChunkSize;
        var badTarget = Path.Combine(directory, "reuse-target-mismatch.bin");
        TryDelete(badTarget); TryDelete(badTarget + ".part"); TryDelete(badTarget + ".part.watermark");
        await File.WriteAllBytesAsync(badTarget, real.AsSpan(0, pushed).ToArray());
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return ResponseWithTotal(real, range.From!.Value, range.To!.Value, real.Length);
        }))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(Request(pushed), badTarget));
            check(error is DownloadException && error.Message.Contains(pushed.ToString()) && error.Message.Contains(real.Length.ToString()),
                $"download integrity: no-hash target with smaller pushed size is rejected ({error?.Message})");
            check(File.Exists(badTarget) && new FileInfo(badTarget).Length == pushed,
                "download integrity: mismatched target is preserved, not overwritten");
        }

        var okPart = Path.Combine(directory, "reuse-part-ok.bin");
        TryDelete(okPart); TryDelete(okPart + ".part"); TryDelete(okPart + ".part.watermark");
        await File.WriteAllBytesAsync(okPart + ".part", payload);
        await File.WriteAllTextAsync(okPart + ".part.watermark", payload.Length.ToString());
        var partRequests = new ConcurrentQueue<string>();
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            partRequests.Enqueue($"{range.From}-{range.To}");
            return Response(payload, range.From!.Value, range.To!.Value);
        }))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(Request(payload.Length), okPart));
            check(error is null && Matches(okPart, payload) && partRequests.Count == 1,
                $"download integrity: no-hash complete .part confirmed by server is reused ({error?.Message}, {partRequests.Count} requests)");
        }

        var badPart = Path.Combine(directory, "reuse-part-mismatch.bin");
        TryDelete(badPart); TryDelete(badPart + ".part"); TryDelete(badPart + ".part.watermark");
        await File.WriteAllBytesAsync(badPart + ".part", real.AsSpan(0, pushed).ToArray());
        await File.WriteAllTextAsync(badPart + ".part.watermark", pushed.ToString());
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return ResponseWithTotal(real, range.From!.Value, range.To!.Value, real.Length);
        }))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(Request(pushed), badPart));
            check(error is DownloadException,
                $"download integrity: no-hash complete .part with smaller pushed size is rejected ({error?.Message})");
            check(!File.Exists(badPart),
                "download integrity: mismatched .part is not promoted to final file");
        }

        var hashedPart = Path.Combine(directory, "reuse-part-hashed.bin");
        TryDelete(hashedPart); TryDelete(hashedPart + ".part"); TryDelete(hashedPart + ".part.watermark");
        await File.WriteAllBytesAsync(hashedPart + ".part", payload);
        await File.WriteAllTextAsync(hashedPart + ".part.watermark", payload.Length.ToString());
        using (var handler = new Handler(_ => throw new InvalidOperationException("should stay offline")))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(HashedRequest(payload.Length), hashedPart));
            check(error is null && Matches(hashedPart, payload),
                $"download integrity: hashed complete .part reuses offline without network ({error?.Message})");
        }

        var unconfirmedTarget = Path.Combine(directory, "reuse-unconfirmed.bin");
        TryDelete(unconfirmedTarget); TryDelete(unconfirmedTarget + ".part"); TryDelete(unconfirmedTarget + ".part.watermark");
        await File.WriteAllBytesAsync(unconfirmedTarget, payload);
        var unconfirmedRequests = new ConcurrentQueue<string>();
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            unconfirmedRequests.Enqueue(range is null ? "none" : $"{range.From}-{range.To}");
            if (range?.From == 0 && range?.To == 0)
            {
                var probe = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                probe.Content.Headers.ContentLength = null;
                return probe;
            }
            var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            full.Content.Headers.ContentLength = null;
            return full;
        }))
        using (var service = Service(handler))
        {
            var error = await Capture(() => service.DownloadAsync(Request(payload.Length), unconfirmedTarget));
            check(error is null && Matches(unconfirmedTarget, payload) && unconfirmedRequests.Count > 1,
                $"download integrity: unconfirmed length is not treated as complete, normal download follows ({error?.Message}, {unconfirmedRequests.Count} requests)");
        }
    }

    private static async Task CheckRetryAfterAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 服务端明确要求等 3 秒：必须遵守 Retry-After，而不是按 1s 退避立即重试
        var attempts = 0;
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var rejected = new HttpResponseMessage((HttpStatusCode)429);
                rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return rejected;
            }
            return Response(payload, start, end);
        });
        using var service = new DownloadService(handler, TimeSpan.FromSeconds(5), payload.Length) { MaxAttempts = 2 };
        var target = Path.Combine(directory, "retry-after.bin");
        var watch = Stopwatch.StartNew();
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target));
        watch.Stop();
        check(error is null && Matches(target, payload),
            $"download tail: 429 with Retry-After then success ({error?.Message})");
        check(watch.Elapsed >= TimeSpan.FromSeconds(2.8),
            $"download tail: Retry-After: 3 is honored instead of 1s backoff ({watch.Elapsed.TotalSeconds:F1}s)");
    }

    private static void CheckAimdThrottle(Action<bool, string> check)
    {
        // 油门状态机是纯内存逻辑：逐档验证，不依赖网络与计时
        using var throttle = new AimdThrottle();
        check(throttle.Available == 6, $"tail AIMD: starts at steady 6 (got {throttle.Available})");
        throttle.NoteTransient();
        check(throttle.Available == 4, $"tail AIMD: one rejection parks to 4 (got {throttle.Available})");
        throttle.NoteTransient();
        check(throttle.Available == 2, $"tail AIMD: storm parks to min 2 (got {throttle.Available})");
        throttle.NoteTransient();
        check(throttle.Available == 2, "tail AIMD: never below min 2");
        for (var i = 0; i < 19; i++) throttle.NoteResponse();
        check(throttle.Available == 2, "tail AIMD: 19 consecutive successes are not enough for a step up");
        throttle.NoteResponse();
        check(throttle.Available == 3, $"tail AIMD: 20 consecutive successes restore 1 (got {throttle.Available})");
        throttle.EnterTail();
        check(throttle.Available == 8, $"tail AIMD: tail phase bursts to 8 (got {throttle.Available})");
        throttle.NoteTransient();
        check(throttle.Available == 6 && throttle.TransientEvents == 4,
            $"tail AIMD: congestion during tail parks again ({throttle.Available} available, {throttle.TransientEvents} events)");
    }

    private static async Task CheckHandoffAsync(string directory, byte[] payload, Action<bool, string> check)
    {
        // 块 0 先给 8KiB 然后卡死：空闲连接应在 30s 空闲超时之前接管，后续从 8KiB 续传而非从头重下
        var requests = new ConcurrentQueue<long>();
        using var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            if (start == 0 && end == 0) return Response(payload, start, end);
            requests.Enqueue(start);
            return start == 0
                ? Response(payload, start, end, new InterruptedStream(payload, (int)start, (int)(end - start + 1), stall: true))
                : Response(payload, start, end);
        });
        using var service = new DownloadService(handler, TimeSpan.FromSeconds(30), ChunkSize)
        {
            MaxAttempts = 2,
            MinHandoffAge = TimeSpan.FromMilliseconds(500),
            HandoffMinBytes = 4096,
        };
        var progress = new List<long>();
        var target = Path.Combine(directory, "handoff.bin");
        var watch = Stopwatch.StartNew();
        var error = await Capture(() => service.DownloadAsync(Request(payload.Length), target,
            (received, _) => { lock (progress) progress.Add(received); }));
        watch.Stop();
        check(error is null && Matches(target, payload),
            $"download tail: stalled chunk is rescued by handoff ({error?.Message})");
        check(service.HandoffCount >= 1,
            $"download tail: handoff actually fired ({service.HandoffCount} handoffs in {watch.Elapsed.TotalSeconds:F1}s)");
        check(watch.Elapsed < TimeSpan.FromSeconds(15),
            $"download tail: no 30s idle-timeout wait ({watch.Elapsed.TotalSeconds:F1}s)");
        check(requests.Contains(PartialBytes),
            "download tail: rescue resumes the unwritten suffix, preserving the 8KiB prefix");
        check(progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second)
              && progress.All(value => value <= payload.Length) && progress.LastOrDefault() == payload.Length,
            "download tail: handoff never rolls back or double counts bytes");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
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

    private static HttpResponseMessage ResponseWithTotal(byte[] payload, long start, long end, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(payload.AsSpan((int)start, (int)(end - start + 1)).ToArray()),
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        response.Content.Headers.ContentLength = end - start + 1;
        return response;
    }

    private static InstallRequest HashedRequest(int length) => new()
    {
        V = 1, Provider = "shionlib", ResourceId = "tuning", Url = "https://download.example/test.bin",
        FileName = "test.bin", ArchiveFormat = "zip", Size = (ulong)length, BgmId = "13", Title = "CLANNAD",
        ChecksumAlgo = "sha256", Checksum = new string('a', 64),
    };

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
