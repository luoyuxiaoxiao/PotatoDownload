using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PotatoVN.App.PluginBase;
using PotatoVN.App.PluginBase.Models;
using PotatoVN.App.PluginBase.Services;

namespace CoreChecks;

/// <summary>
/// 核心逻辑自检：InstallRequest 校验/脱敏、DownloadService 分块/续传/越界/空闲超时/SSRF、
/// UnpackService 目录名与解压安全。不依赖 WinUI 宿主：cd Tests/CoreChecks &amp;&amp; dotnet run
/// </summary>
internal static class Program
{
    private static int _failed;
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "potatodownload-corechecks-" + Guid.NewGuid().ToString("N"));

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            CheckValidation();
            ProgressDisplayChecks.Run(Check);
            await CheckDownloadsAsync();
            await DownloadTuningChecks.RunAsync(Root, Check);
            await CheckUnpackAsync();
            await UnpackProgressChecks.RunAsync(Root, Check);
            await CheckManagerAsync();
        }
        finally
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
        Console.WriteLine(_failed == 0 ? "ALL PASS" : $"{_failed} FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) _failed++;
    }

    private static async Task<Exception?> Throws(Func<Task> action)
    {
        try { await action(); return null; } catch (Exception e) { return e; }
    }

    private static string Show(string s) => s.Replace("\t", "\\t").Replace("\n", "\\n");

    /// <summary>运行外部命令，返回退出码；命令不存在返回 -1。</summary>
    private static async Task<int> RunAsync(string file, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (proc is null) return -1;
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string FileSha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static InstallRequest Req(string url = "https://dl.example.com/game.7z", string fileName = "game.7z",
        ulong size = 1234, string title = "CLANNAD", string? algo = null, string? checksum = null,
        string? password = null) => new()
    {
        V = 1, Provider = "shionlib", ResourceId = "1", Url = url, FileName = fileName,
        ArchiveFormat = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "7z",
        Size = size, BgmId = "13", Title = title, ChecksumAlgo = algo, Checksum = checksum, ArchivePassword = password,
    };

    private static bool Rejected(InstallRequest request)
    {
        try { request.Validate(); return false; } catch (InstallRequestException) { return true; }
    }

    // ---------------------------------------------------------------- InstallRequest

    private static void CheckValidation()
    {
        Check(!Rejected(Req()), "validate: normal request accepted");
        foreach (var name in new[] { "..", "a/b.7z", "a\\b.7z", "C:x.7z", "CON.7z", "nul", "com1.zip", "a*b.7z", "bad.7z.", "bad.7z ", "t\tab.7z", new string('a', 256) })
            Check(Rejected(Req(fileName: name)), $"validate: rejects file_name '{Show(name)}'");
        Check(!Rejected(Req(fileName: "Game v1.2 [汉化].tar.zst")), "validate: accepts unicode file_name with spaces");
        Check(Rejected(Req(size: 0)), "validate: rejects size 0");
        Check(Rejected(Req(size: InstallRequest.MaxSize + 1)), "validate: rejects size > MaxSize");
        Check(Rejected(Req(title: new string('t', 513))), "validate: rejects overlong title");
        Check(Rejected(Req(algo: "sha256")), "validate: rejects checksum_algo without checksum");
        Check(Rejected(Req(algo: "md5", checksum: new string('a', 64))), "validate: rejects md5");
        Check(!Rejected(Req(algo: "blake3", checksum: new string('a', 64))), "validate: accepts blake3");

        foreach (var host in new[] { "127.0.0.1", "localhost", "LOCALHOST.", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.1.1", "0.0.0.0", "100.64.0.1", "224.0.0.1", "255.255.255.255", "[::1]", "[::]", "[fd00::1]", "[fc00::1]", "[fe80::1]", "[::ffff:10.0.0.1]", "[::ffff:127.0.0.1]", "router.local", "nas.lan", "x.internal", "y.home" })
            Check(Rejected(Req(url: $"http://{host}/f.7z")), $"validate: rejects private host {host}");
        foreach (var host in new[] { "dl.example.com", "1.1.1.1", "172.32.0.1", "100.128.0.1", "[2606:4700::1111]" })
            Check(!Rejected(Req(url: $"https://{host}/f.7z")), $"validate: accepts public host {host}");
        Check(Rejected(Req(url: "ftp://dl.example.com/f.7z")), "validate: rejects non-http scheme");

        var uri = new Uri("potato-vn://install?v=1&provider=shionlib&resource_id=7&url=https%3A%2F%2Fdl.example.com%2Fg.7z%3Fsig%3DSECRET&file_name=g.7z&archive_format=7z&size=10&bgm_id=13&title=CLANNAD&archive_password=pw123");
        var parsed = InstallRequest.Parse(uri);
        Check(parsed.Url == "https://dl.example.com/g.7z?sig=SECRET" && parsed.ArchivePassword == "pw123", "parse: url and password decoded");
        var redacted = InstallRequest.RedactForLog(uri);
        Check(!redacted.Contains("SECRET") && !redacted.Contains("pw123") && redacted.Contains("title=CLANNAD") && redacted.Contains("url=<redacted>"), $"redact: {redacted}");
        Check(parsed.DeduplicationKey.Contains('\u001f'), "dedupe key: fields separated by U+001F");
    }

    // ---------------------------------------------------------------- DownloadService

    private static async Task CheckDownloadsAsync()
    {
        var payload = new byte[25 * 1024 * 1024 + 12345]; // 7 个块，最后一块不满
        new Random(42).NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var server = new TestServer(payload);
        var idle = TimeSpan.FromSeconds(2);
        var dir = Path.Combine(Root, "dl");
        Directory.CreateDirectory(dir);

        // 4MB 块：25MB 载荷 = 7 块，与下面各场景的偏移假设一致；重试退避上限压到 3 次（1s+2s）让失败用例跑得快
        DownloadService NewService() => new(new SocketsHttpHandler(), idle, 4 * 1024 * 1024) { MaxAttempts = 3 };
        InstallRequest R(string mode, string? algo = "sha256", string? checksum = null) => new()
        {
            V = 1, Provider = "shionlib", ResourceId = "1", Url = server.Prefix + mode, FileName = "g.bin", ArchiveFormat = "zip",
            Size = (ulong)payload.Length, BgmId = "13", Title = "t", ChecksumAlgo = algo, Checksum = algo is null ? null : checksum ?? sha,
        };
        string Target(string name) => Path.Combine(dir, name);
        byte[] Prefix(int n) => payload.AsSpan(0, n).ToArray();

        // 1. 分块并发下载
        var t1 = Target("ok.bin");
        await NewService().DownloadAsync(R("ok"), t1);
        Check(FileSha(t1) == sha, "download: chunked (7 chunks, up to 6 connections) matches sha256");
        Check(!File.Exists(t1 + ".part") && !File.Exists(t1 + ".part.watermark"), "download: .part and watermark cleaned up");
        Check(await Throws(() => DownloadService.VerifyChecksumAsync(R("ok"), t1)) is null, "verify: sha256 accepted");
        Check(await Throws(() => DownloadService.VerifyChecksumAsync(R("ok", checksum: new string('0', 64)), t1)) is DownloadException, "verify: wrong sha256 rejected");
        var reuse = await Throws(() => NewService().DownloadAsync(R("stall"), t1));
        Check(reuse is null, "download: existing complete file reused without touching the network");

        // 2. 续传只补水位之后的块（水位以下服务器返回垃圾，重下就会被发现）
        var t2 = Target("resume.bin");
        await File.WriteAllBytesAsync(t2 + ".part", Prefix(8 << 20));
        await File.WriteAllTextAsync(t2 + ".part.watermark", (8 << 20).ToString());
        await NewService().DownloadAsync(R("garbagebelow8m"), t2);
        Check(FileSha(t2) == sha, "download: resume keeps the 8MiB prefix and only fetches the rest");

        // 3. 乱序完成时水位只推进连续前缀
        var t3 = Target("ooo.bin");
        using (var cts = new CancellationTokenSource(700))
        {
            var e = await Throws(() => NewService().DownloadAsync(R("slowfirst"), t3, null, cts.Token));
            Check(e is OperationCanceledException, $"download: caller cancellation surfaces as OperationCanceledException ({e?.GetType().Name})");
        }
        var watermark = File.Exists(t3 + ".part.watermark") ? long.Parse(await File.ReadAllTextAsync(t3 + ".part.watermark")) : 0;
        Check(watermark == 0, $"download: watermark stays 0 while chunk 0 is unfinished even though later chunks completed (got {watermark})");
        await NewService().DownloadAsync(R("ok"), t3);
        Check(FileSha(t3) == sha, "download: resume after cancel completes with correct content");

        // 4. 不支持 Range → 单连接
        var t4 = Target("seq.bin");
        await NewService().DownloadAsync(R("norange"), t4);
        Check(FileSha(t4) == sha, "download: sequential fallback matches sha256");

        // 5. 单连接续传但服务器忽略 Range → 从头覆盖而不是追加
        var t5 = Target("seqresume.bin");
        await File.WriteAllBytesAsync(t5 + ".part", Prefix(5 << 20));
        await File.WriteAllTextAsync(t5 + ".part.watermark", (5 << 20).ToString());
        await NewService().DownloadAsync(R("norange"), t5);
        Check(FileSha(t5) == sha, "download: server ignoring Range restarts from 0 instead of appending");

        // 6. 服务器多发数据
        var t6 = Target("extra.bin");
        var e6 = await Throws(() => NewService().DownloadAsync(R("extra"), t6));
        Check(e6 is DownloadException && e6.Message.Contains("超出声明大小"), $"download: rejects body longer than declared size ({e6?.Message})");
        Check(new FileInfo(t6 + ".part").Length <= payload.Length, "download: never writes beyond declared size");

        // 7. Content-Length 与声明不符：读正文前就拒绝
        var e7 = await Throws(() => NewService().DownloadAsync(R("wronglen"), Target("wronglen.bin")));
        Check(e7 is DownloadException && e7.Message.Contains("与声明不符"), $"download: rejects Content-Length mismatch up front ({e7?.Message})");

        // 8. 服务器发完头就不动 → 空闲超时（3 次尝试 × 2s 空闲 + 1s/2s 退避 ≈ 9s 内）
        var watch = Stopwatch.StartNew();
        var e8 = await Throws(() => NewService().DownloadAsync(R("stall"), Target("stall.bin")));
        Check(e8 is DownloadException && e8.Message.Contains("空闲超时") && watch.Elapsed < TimeSpan.FromSeconds(15),
            $"download: idle timeout aborts a stalled server in {watch.Elapsed.TotalSeconds:F1}s ({e8?.Message})");

        // 9. 探测回 206 但分块请求回 200
        var e9 = await Throws(() => NewService().DownloadAsync(R("rangebroken"), Target("rb.bin")));
        Check(e9 is DownloadException && e9.Message.Contains("未按 Range"), $"download: chunk request answered with 200 is rejected ({e9?.Message})");

        // 9b. 限流（429）与 5xx 是瞬时错误：退避重试后成功，内容完整（Shionlib 代理超并发即回 429 + Retry-After: 1）
        server.ResetCounters();
        var t9b = Target("flaky.bin");
        watch.Restart();
        var e9b = await Throws(() => NewService().DownloadAsync(R("flaky"), t9b));
        Check(e9b is null && FileSha(t9b) == sha, $"download: 429/503 on first attempts are retried and the file is complete ({e9b?.Message})");
        Check(server.FlakyRejections >= 2, $"download: server really rejected {server.FlakyRejections} requests before succeeding");

        // 9c. 一块彻底失败（403）→ 其余连接立即中止，根因浮现，而不是等所有块慢慢跑完。
        //     用 1MB 块（26 块 > 8 连接）：不中止的话 8 连接要跑 4 轮慢块（≈4s、25 个慢请求）
        server.ResetCounters();
        watch.Restart();
        var small = new DownloadService(new SocketsHttpHandler(), idle, 1024 * 1024) { MaxAttempts = 3 };
        var e9c = await Throws(() => small.DownloadAsync(R("chunk3forbidden"), Target("forbidden.bin")));
        Check(e9c is DownloadException && e9c.Message.Contains("403") && watch.Elapsed < TimeSpan.FromSeconds(3),
            $"download: a chunk rejected with 403 fails the whole download fast with the real reason in {watch.Elapsed.TotalSeconds:F1}s ({e9c?.GetType().Name}: {e9c?.Message})");
        Check(server.SlowChunkRequests <= 8, $"download: sibling connections were aborted ({server.SlowChunkRequests} slow-chunk requests served, 25 without abort)");

        // 9d. 探测时被限流不能静默退化成单连接：重试后仍走分块
        server.ResetCounters();
        var t9d = Target("probe429.bin");
        var e9d = await Throws(() => NewService().DownloadAsync(R("probe429"), t9d));
        Check(e9d is null && FileSha(t9d) == sha && server.RangeRequests > 2,
            $"download: probe hit by 429 retries instead of falling back to sequential ({server.RangeRequests} range requests, {e9d?.Message})");

        // 10. 默认传输层在建连时拒绝回环/内网地址（本测试服务器就在 127.0.0.1）
        var e10 = await Throws(() => new DownloadService().DownloadAsync(R("ok"), Target("ssrf.bin")));
        Check(e10 is DownloadException && e10.Message.Contains("内网"), $"download: default handler refuses loopback at connect time ({e10?.GetType().Name}: {e10?.Message})");

        // 11. BLAKE3 参考向量（空输入）
        var empty = Target("empty.bin");
        File.WriteAllBytes(empty, []);
        var e11 = await Throws(() => DownloadService.VerifyChecksumAsync(
            R("ok", algo: "blake3", checksum: "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"), empty));
        Check(e11 is null, $"verify: blake3 of empty input matches reference vector ({e11?.Message})");

        // 12. 声明大小超过可用磁盘空间：预分配前拒绝
        var huge = R("ok");
        var e12 = await Throws(() => NewService().DownloadAsync(new InstallRequest
        {
            V = 1, Provider = "shionlib", ResourceId = "1", Url = huge.Url, FileName = "huge.bin", ArchiveFormat = "zip",
            Size = InstallRequest.MaxSize, BgmId = "13", Title = "t",
        }, Target("huge.bin")));
        Check(e12 is DownloadException && e12.Message.Contains("磁盘空间不足") && !File.Exists(Target("huge.bin.part")),
            $"download: refuses to preallocate more than the free disk space ({e12?.Message})");

        // 13. 真实 HTTPS（默认传输层的 TLS 走 ConnectCallback）：CORECHECKS_ONLINE=1 时启用
        if (Environment.GetEnvironmentVariable("CORECHECKS_ONLINE") == "1")
        {
            const string onlineUrl = "https://raw.githubusercontent.com/luoyuxiaoxiao/PotatoDownload/main/LICENSE";
            byte[]? expected = null;
            try
            {
                using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                expected = await plain.GetByteArrayAsync(onlineUrl);
            }
            catch (Exception e)
            {
                Console.WriteLine($"SKIP  online HTTPS check, reference fetch failed: {e.Message}");
            }
            if (expected is not null)
            {
                var online = new InstallRequest
                {
                    V = 1, Provider = "shionlib", ResourceId = "1", Url = onlineUrl, FileName = "online.bin", ArchiveFormat = "zip",
                    Size = (ulong)expected.Length, BgmId = "13", Title = "t",
                    ChecksumAlgo = "sha256", Checksum = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                };
                var eOn = await Throws(() => new DownloadService().DownloadAsync(online, Target("online.bin")));
                Check(eOn is null && FileSha(Target("online.bin")) == online.Checksum,
                    $"download: real HTTPS through the SSRF-checking handler ({eOn?.Message})");
            }
        }
        else
        {
            Console.WriteLine("SKIP  online HTTPS check (set CORECHECKS_ONLINE=1)");
        }
    }

    // ---------------------------------------------------------------- UnpackService

    private static void MakeZip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
    }

    private static async Task CheckUnpackAsync()
    {
        var dir = Path.Combine(Root, "unpack");
        Directory.CreateDirectory(dir);
        var req = Req(fileName: "g.zip");

        // 所有引擎共用的安全输出层拒绝 zip-slip，不会写到目标目录之外。
        var slip = Path.Combine(dir, "slip.zip");
        MakeZip(slip, ("ok/a.txt", "a"), ("../evil.txt", "evil"));
        var slipTarget = UnpackService.PrepareGameDirectory(dir, "slip-target");
        var eSlip = await Throws(() => UnpackService.UnpackAsync(req, slip, slipTarget));
        Check(eSlip is InvalidDataException && !File.Exists(Path.Combine(dir, "evil.txt")),
            $"unpack: zip-slip entry rejected by the destination check ({eSlip?.Message})");

        // 游戏目录名解析
        var single = Path.Combine(dir, "single.zip");
        MakeZip(single, ("CLANNAD/a.txt", "a"), ("CLANNAD/b/c.txt", "c"));
        Check(UnpackService.ResolveGameDirectoryName(req, single) == "CLANNAD", "resolve: single top-level folder used");
        var singleOut = UnpackService.PrepareGameDirectory(dir, "single-out");
        var eZip = await Throws(() => UnpackService.UnpackAsync(req, single, singleOut));
        Check(eZip is null && File.ReadAllText(Path.Combine(singleOut, "CLANNAD", "b", "c.txt")) == "c"
              && File.ReadAllText(Path.Combine(singleOut, "CLANNAD", "a.txt")) == "a", $"unpack: plain zip fully extracted ({eZip?.Message})");
        var dotdot = Path.Combine(dir, "dotdot.zip");
        MakeZip(dotdot, ("../a.txt", "a"), ("../b.txt", "b"));
        Check(UnpackService.ResolveGameDirectoryName(req, dotdot) == "dotdot", "resolve: '..' top-level falls back to archive stem");
        var flat = Path.Combine(dir, "flat.zip");
        MakeZip(flat, ("a.txt", "a"), ("b.txt", "b"));
        Check(UnpackService.ResolveGameDirectoryName(req, flat) == "flat", "resolve: multiple top-level entries fall back to stem");
        // 不能命名为 con.zip：Windows 保留设备名写入不落盘，读取得到非 seekable 流
        var con = Path.Combine(dir, "reserved.zip");
        MakeZip(con, ("CON/a.txt", "a"));
        Check(UnpackService.ResolveGameDirectoryName(req, con) == "reserved", "resolve: reserved top-level name falls back to archive stem");
        var weird = Path.Combine(dir, "..zip");
        MakeZip(weird, ("a.txt", "a"), ("b.txt", "b"));
        Check(UnpackService.ResolveGameDirectoryName(req, weird) == "game", "resolve: unsafe stem '.' falls back to 'game'");

        // 目录准备：不删用户目录、只清理自己的未完成残留、完成后不再删除
        var foreign = Path.Combine(dir, "Existing");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "save.dat"), "keep");
        var prepared = UnpackService.PrepareGameDirectory(dir, "Existing");
        Check(prepared == Path.Combine(dir, "Existing (2)") && File.Exists(Path.Combine(foreign, "save.dat")),
            $"prepare: foreign dir untouched, unique name used ({Path.GetFileName(prepared)})");
        File.WriteAllText(Path.Combine(prepared, "partial.bin"), "x");
        var again = UnpackService.PrepareGameDirectory(dir, "Existing");
        Check(again == prepared && !File.Exists(Path.Combine(prepared, "partial.bin")), "prepare: incomplete dir from a failed attempt is cleaned and reused");
        UnpackService.MarkComplete(again);
        var third = UnpackService.PrepareGameDirectory(dir, "Existing");
        Check(third == Path.Combine(dir, "Existing (3)"), "prepare: completed dir is never deleted again");
        Check((await Throws(() => Task.Run(() => UnpackService.PrepareGameDirectory(dir, "..")))) is InvalidOperationException, "prepare: '..' rejected");
        Check((await Throws(() => Task.Run(() => UnpackService.PrepareGameDirectory(dir, "CON")))) is InvalidOperationException, "prepare: reserved name rejected");

        // solid + 加密头 7z（需要 7z 命令行；没有则跳过）
        var src = Path.Combine(dir, "src", "Game");
        Directory.CreateDirectory(Path.Combine(src, "data"));
        var content = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(content);
        File.WriteAllBytes(Path.Combine(src, "data", "a.bin"), content);
        File.WriteAllText(Path.Combine(src, "game.exe"), "exe");

        // tar 系列：ArchiveFactory 会把 tar.gz 当成只含一个 .tar 条目的 gzip，必须经 ReaderFactory 解开
        foreach (var (ext, flag) in new[] { ("tar", ""), ("tar.gz", "z"), ("tar.bz2", "j"), ("tar.xz", "J"), ("tar.zst", "--zstd") })
        {
            var tarPath = Path.Combine(dir, $"tarred.{ext}");
            var args = flag.StartsWith("--") ? $"{flag} -cf" : $"-c{flag}f";
            var tarProc = await RunAsync("tar", $"{args} \"{tarPath}\" -C \"{Path.GetDirectoryName(src)}\" Game");
            if (tarProc != 0)
            {
                Console.WriteLine($"SKIP  tar {ext} (tar exited {tarProc}, compressor missing?)");
                continue;
            }
            var reqTar = new InstallRequest
            {
                V = 1, Provider = "shionlib", ResourceId = "1", Url = "https://dl.example.com/t", FileName = $"tarred.{ext}",
                ArchiveFormat = ext, Size = 1, BgmId = "13", Title = "t",
            };
            var name = UnpackService.ResolveGameDirectoryName(reqTar, tarPath);
            Check(name == "Game", $"{ext}: top-level folder resolved (got '{name}')");
            var outTar = UnpackService.PrepareGameDirectory(dir, $"out-{ext}");
            var eTar = await Throws(() => UnpackService.UnpackAsync(reqTar, tarPath, outTar));
            Check(eTar is null && File.Exists(Path.Combine(outTar, "Game", "game.exe"))
                  && File.ReadAllBytes(Path.Combine(outTar, "Game", "data", "a.bin")).AsSpan().SequenceEqual(content),
                $"{ext}: extracted with real file contents ({eTar?.Message})");
        }

        var sevenZip = Path.Combine(dir, "solid.7z");
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo("7z", $"a -t7z -ms=on -mhe=on -pSECRET \"{sevenZip}\" \"{src}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
        }
        catch
        {
            proc = null;
        }
        if (proc is null)
        {
            Console.WriteLine("SKIP  7z CLI not available, solid archive checks skipped");
            return;
        }
        await proc.WaitForExitAsync();
        var reqPw = Req(fileName: "solid.7z", password: "SECRET");
        Check(UnpackService.ResolveGameDirectoryName(reqPw, sevenZip) == "Game", "7z: top-level folder resolved through encrypted headers");
        var out7 = UnpackService.PrepareGameDirectory(dir, "Game");
        var progress = new List<(int Finished, int Total)>();
        var e7z = await Throws(() => UnpackService.UnpackAsync(reqPw, sevenZip, out7, (f, t) => progress.Add((f, t))));
        Check(e7z is null && File.ReadAllBytes(Path.Combine(out7, "Game", "data", "a.bin")).AsSpan().SequenceEqual(content)
              && File.Exists(Path.Combine(out7, "Game", "game.exe")), $"7z: solid+encrypted archive extracted via sequential reader ({e7z?.Message})");
        Check(progress.Count == 2 && progress[^1] == (2, 2), $"7z: progress reported per file ({progress.Count} callbacks)");
        var wrong = await Throws(() => UnpackService.UnpackAsync(Req(fileName: "solid.7z", password: "nope"), sevenZip, UnpackService.PrepareGameDirectory(dir, "GameWrong")));
        Check(wrong is not null, "7z: wrong password fails instead of writing garbage");

        // solid rar（需要 rar 命令行；没有则跳过）。-ep1：条目名不带基目录，只保留 Game/...
        foreach (var (label, extra) in new[] { ("rar-solid", "-s"), ("rar-solid-pw", "-s -pSECRET") })
        {
            var rarPath = Path.Combine(dir, $"{label}.rar");
            var rarExit = await RunAsync("rar", $"a -ep1 {extra} \"{rarPath}\" \"{src}\"");
            if (rarExit != 0)
            {
                Console.WriteLine($"SKIP  {label} (rar CLI exited {rarExit})");
                continue;
            }
            var reqRar = new InstallRequest
            {
                V = 1, Provider = "shionlib", ResourceId = "1", Url = "https://dl.example.com/t", FileName = $"{label}.rar",
                ArchiveFormat = "rar", Size = 1, BgmId = "13", Title = "t", ArchivePassword = extra.Contains("-p") ? "SECRET" : null,
            };
            var rarName = UnpackService.ResolveGameDirectoryName(reqRar, rarPath);
            var outRar = UnpackService.PrepareGameDirectory(dir, $"out-{label}");
            var eRar = await Throws(() => UnpackService.UnpackAsync(reqRar, rarPath, outRar));
            Check(rarName == "Game" && eRar is null && File.Exists(Path.Combine(outRar, "Game", "game.exe"))
                  && File.ReadAllBytes(Path.Combine(outRar, "Game", "data", "a.bin")).AsSpan().SequenceEqual(content),
                $"{label}: extracted via sequential reader (name '{rarName}', {eRar?.Message})");
        }
    }

    // ---------------------------------------------------------------- DownloadManager（宿主 API 见 HostStubs）

    private static async Task<T> WaitUntilAsync<T>(Func<T?> probe, string what, int timeoutMs = 30000) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }
        throw new TimeoutException($"timed out waiting for {what}");
    }

    private static async Task CheckManagerAsync()
    {
        // 载荷是真正的 zip（20MB 不压缩条目），管线要走完 下载→校验→解压→入库(桩)→完成
        var dir = Path.Combine(Root, "mgr");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "payload.zip");
        var content = new byte[20 * 1024 * 1024];
        new Random(9).NextBytes(content);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var data = zip.CreateEntry("Game/data.bin", CompressionLevel.NoCompression).Open()) data.Write(content);
            using (var exe = zip.CreateEntry("Game/game.exe", CompressionLevel.NoCompression).Open()) exe.Write("exe"u8);
        }
        var payload = File.ReadAllBytes(zipPath);
        File.Delete(zipPath);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var server = new TestServer(payload);
        var downloadDir = Path.Combine(dir, "downloads");
        Plugin.DownloadPath = downloadDir;
        var host = new HostStub();
        var manager = new DownloadManager(host)
        {
            ServiceFactory = () => new DownloadService(new SocketsHttpHandler(), TimeSpan.FromSeconds(5), 4 * 1024 * 1024),
        };
        InstallRequest R(string mode) => new()
        {
            V = 1, Provider = "shionlib", ResourceId = mode, Url = server.Prefix + mode, FileName = "g.zip", ArchiveFormat = "zip",
            Size = (ulong)payload.Length, BgmId = "13", Title = "CLANNAD " + mode, ChecksumAlgo = "sha256", Checksum = sha,
        };
        var pack = Path.Combine(downloadDir, ".potatodownload", "g.zip");
        bool PartExists() => File.Exists(pack + ".part");
        Task<DownloadTask> Downloading(int index) => WaitUntilAsync(
            () => manager.Tasks.Count > index && manager.Tasks[index] is { Stage: DownloadTaskStage.Downloading, Received: > 0 } t ? t : null,
            $"task {index} to start receiving");

        // 1. 完整流程
        await manager.EnqueueAsync(R("ok"));
        var t1 = manager.Tasks[0];
        Check(t1.Stage == DownloadTaskStage.Completed && t1.Received == t1.Total && host.Errors == 0,
            $"manager: full pipeline completes ({t1.Stage}: {t1.Message})");
        Check(t1.UnpackProgress is { FilesExtracted: 2 } unpack && unpack.BytesExtracted == content.Length + 3 &&
              unpack.TotalBytes == unpack.BytesExtracted && t1.ProgressPercent == 100,
            "manager: completed pipeline publishes final extraction bytes and completion percentage");
        // 游戏目录名取自压缩包唯一顶层文件夹（Game），包内结构原样解到该目录下：downloads/Game/Game/game.exe
        Check(File.Exists(Path.Combine(downloadDir, "Game", "Game", "game.exe"))
              && !File.Exists(Path.Combine(downloadDir, "Game", UnpackService.IncompleteMarker)), "manager: game extracted and marked complete");
        Check(!File.Exists(pack) && !PartExists(), "manager: staging archive removed after import");
        Check(Plugin.HistoryCollection.Count == 1 && Plugin.HistoryCollection[0].Outcome == DownloadRecord.OutcomeCompleted,
            "manager: history records the completion");

        // 2. 下载中取消 → 任务 Cancelled，.part/水位/暂存包全部删除，记历史
        var run2 = manager.EnqueueAsync(R("slow"));
        var t2 = await Downloading(1);
        manager.CancelTask(t2);
        await run2;
        Check(t2.Stage == DownloadTaskStage.Cancelled && !t2.IsListed, $"manager: cancel mid-download ends as Cancelled ({t2.Stage})");
        Check(!PartExists() && !File.Exists(pack + ".part.watermark") && !File.Exists(pack),
            "manager: cancel deletes .part, watermark and staging archive");
        Check(Plugin.HistoryCollection[0].Outcome == DownloadRecord.OutcomeCancelled, "manager: history records the cancellation");

        // 3. 下载中暂停 → 保留 .part、仍参与去重；继续 → 完成
        var run3 = manager.EnqueueAsync(R("slow"));
        var t3 = await Downloading(2);
        t3.Pause();
        await run3;
        Check(t3.Stage == DownloadTaskStage.Paused && t3.PausedFromStage == DownloadTaskStage.Downloading && PartExists(),
            $"manager: pause keeps the .part and the previous progress stage ({t3.Stage}, part exists: {PartExists()})");
        Check(manager.HasActiveTask(t3.Request.DeduplicationKey), "manager: a paused task still blocks a duplicate push");
        manager.ResumeTask(t3);
        await WaitUntilAsync(() => t3.IsListed ? null : t3, "resumed task to finish", 90000);
        Check(t3.Stage == DownloadTaskStage.Completed && File.Exists(Path.Combine(downloadDir, "Game (2)", "Game", "game.exe")),
            $"manager: resume finishes the paused task into 'Game (2)' ({t3.Stage}: {t3.Message})");

        // 4. 暂停后取消 → 清理现场
        var run4 = manager.EnqueueAsync(R("slow"));
        var t4 = await Downloading(3);
        t4.Pause();
        await run4;
        manager.CancelTask(t4);
        Check(t4.Stage == DownloadTaskStage.Cancelled && !PartExists() && !File.Exists(pack + ".part.watermark"),
            $"manager: cancelling a paused task deletes its .part ({t4.Stage})");

        // 5. 暂停请求后紧接着取消（管线尚未收尾）：最终语义是取消，不是暂停
        var run5 = manager.EnqueueAsync(R("slow"));
        var t5 = await Downloading(4);
        t5.Pause();
        manager.CancelTask(t5);
        await run5;
        Check(t5.Stage == DownloadTaskStage.Cancelled && !PartExists(), $"manager: pause immediately followed by cancel ends as Cancelled ({t5.Stage})");

        // 6. 插件卸载必须等管线释放句柄；仍保留续传现场，也不能误写成用户取消历史。
        var run6 = manager.EnqueueAsync(R("slow"));
        var t6 = await Downloading(5);
        var historyCount = Plugin.HistoryCollection.Count;
        await manager.ShutdownAsync();
        var canOpenExclusively = false;
        try
        {
            using var part = new FileStream(pack + ".part", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            canOpenExclusively = true;
        }
        catch (IOException) { }
        Check(t6.Stage == DownloadTaskStage.Failed && canOpenExclusively && Plugin.HistoryCollection.Count == historyCount,
            "manager: shutdown waits for file handles to close and preserves resume data without cancellation history");
        await run6;
        Check(host.Errors == 0, $"manager: no error events were raised during the scenarios ({host.Errors})");
    }

    // ---------------------------------------------------------------- 测试用 HTTP 服务器

    /// <summary>本地 HTTP 服务器，按路径模拟正常与异常行为。</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private int _flakyHits, _flakyRejections, _slowChunkRequests, _rangeRequests, _probeHits;

        public string Prefix { get; }
        public int FlakyRejections => _flakyRejections;
        public int SlowChunkRequests => _slowChunkRequests;
        public int RangeRequests => _rangeRequests;

        public void ResetCounters() =>
            _flakyHits = _flakyRejections = _slowChunkRequests = _rangeRequests = _probeHits = 0;

        public TestServer(byte[] payload)
        {
            _payload = payload;
            Prefix = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var mode = context.Request.Url!.AbsolutePath.Trim('/');
            var response = context.Response;
            try
            {
                long start = 0, end = _payload.Length - 1;
                var rangeHeader = context.Request.Headers["Range"];
                var hasRange = rangeHeader is not null && rangeHeader.StartsWith("bytes=");
                if (hasRange)
                {
                    var parts = rangeHeader![6..].Split('-');
                    start = long.Parse(parts[0]);
                    if (parts[1].Length > 0) end = long.Parse(parts[1]);
                }
                var length = end - start + 1;
                switch (mode)
                {
                    case "ok": // 正常：支持 Range
                        await ServeAsync(response, hasRange, start, end, _payload);
                        break;
                    case "slow": // 支持 Range，每 256KB 停 40ms：给暂停/取消留出下载中的窗口
                        await ServeAsync(response, hasRange, start, end, _payload, 256 * 1024, 40);
                        break;
                    case "garbagebelow8m": // 8MiB 以下返回垃圾：证明续传确实跳过了已完成的块
                        await ServeAsync(response, hasRange, start, end, start < 8L << 20 ? new byte[_payload.Length] : _payload);
                        break;
                    case "slowfirst": // 第一块延迟 1.5s，其余立即返回：制造乱序完成
                        if (start == 0 && length > 1) await Task.Delay(1500, _cts.Token);
                        await ServeAsync(response, hasRange, start, end, _payload);
                        break;
                    case "norange": // 忽略 Range，永远整包 200
                        response.StatusCode = 200;
                        response.ContentLength64 = _payload.Length;
                        await response.OutputStream.WriteAsync(_payload);
                        break;
                    case "extra": // 忽略 Range，chunked 且多发 1000 字节
                        response.StatusCode = 200;
                        response.SendChunked = true;
                        await response.OutputStream.WriteAsync(_payload);
                        await response.OutputStream.WriteAsync(new byte[1000]);
                        break;
                    case "wronglen": // Content-Length 与声明不符
                        response.StatusCode = 200;
                        response.ContentLength64 = _payload.Length + 1000;
                        await response.OutputStream.WriteAsync(_payload);
                        await response.OutputStream.WriteAsync(new byte[1000]);
                        break;
                    case "stall": // 发完头就不动
                        response.StatusCode = 200;
                        response.SendChunked = true;
                        await response.OutputStream.FlushAsync();
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        break;
                    case "rangebroken": // 探测（1 字节）回 206，真正的分块请求却整包 200
                        if (hasRange && length == 1)
                        {
                            await ServeAsync(response, true, start, end, _payload);
                        }
                        else
                        {
                            response.StatusCode = 200;
                            response.ContentLength64 = _payload.Length;
                            await response.OutputStream.WriteAsync(_payload);
                        }
                        break;
                    case "flaky": // 前两个分块请求分别回 429 / 503，之后正常：验证瞬时错误重试
                    {
                        var n = hasRange && length > 1 ? Interlocked.Increment(ref _flakyHits) : 0;
                        if (n is 1 or 2)
                        {
                            Interlocked.Increment(ref _flakyRejections);
                            response.StatusCode = n == 1 ? 429 : 503;
                            response.AddHeader("Retry-After", "1");
                            break;
                        }
                        await ServeAsync(response, hasRange, start, end, _payload);
                        break;
                    }
                    case "chunk3forbidden": // 第 3 块（1MB 块）永远 403（确定性失败），其余块慢吞吞：验证兄弟连接被立即中止
                        if (hasRange && start == 3L * 1024 * 1024)
                        {
                            response.StatusCode = 403;
                            break;
                        }
                        if (hasRange && length > 1)
                        {
                            Interlocked.Increment(ref _slowChunkRequests);
                            await Task.Delay(1000, _cts.Token);
                        }
                        await ServeAsync(response, hasRange, start, end, _payload);
                        break;
                    case "probe429": // 第一次探测（1 字节 Range）回 429，之后一切正常
                        if (hasRange) Interlocked.Increment(ref _rangeRequests);
                        if (hasRange && length == 1 && Interlocked.Increment(ref _probeHits) == 1)
                        {
                            response.StatusCode = 429;
                            break;
                        }
                        await ServeAsync(response, hasRange, start, end, _payload);
                        break;
                    default:
                        response.StatusCode = 404;
                        break;
                }
            }
            catch
            {
                // 客户端提前断开等，忽略
            }
            finally
            {
                try { response.Close(); } catch { /* ignore */ }
            }
        }

        private async Task ServeAsync(HttpListenerResponse response, bool partial, long start, long end, byte[] data,
            int pieceSize = 0, int pieceDelayMs = 0)
        {
            if (partial)
            {
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}");
            }
            var length = (int)(end - start + 1);
            response.ContentLength64 = length;
            if (pieceSize <= 0)
            {
                await response.OutputStream.WriteAsync(data.AsMemory((int)start, length));
                return;
            }
            for (var offset = 0; offset < length; offset += pieceSize)
            {
                await response.OutputStream.WriteAsync(data.AsMemory((int)start + offset, Math.Min(pieceSize, length - offset)));
                await response.OutputStream.FlushAsync();
                await Task.Delay(pieceDelayMs, _cts.Token);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            _listener.Close();
        }
    }
}
