using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PotatoVN.App.PluginBase.Models;
using PotatoVN.App.PluginBase.Services;

namespace CoreChecks;

/// <summary>实际输出进度、安全输出与中途取消；Windows 同一组 fixture 还直接覆盖随包的 7z.dll。</summary>
internal static class UnpackProgressChecks
{
    private const int BigSize = 8 * 1024 * 1024;
    private const string SmallText = "PotatoDownload native and managed extraction fixture\n";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var dir = Path.Combine(root, "unpack-progress");
        Directory.CreateDirectory(dir);
        var data = new byte[BigSize];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        var zip = Path.Combine(dir, "large.zip");
        using (var file = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var output = file.CreateEntry("Game/big.bin", CompressionLevel.Fastest).Open()) output.Write(data);
        var snapshots = new List<UnpackProgress>();
        var legacy = new List<(int, int)>();
        var target = UnpackService.PrepareGameDirectory(dir, "zip-output");
        await UnpackService.UnpackAsync(Request("zip"), zip, target, (f, t) => legacy.Add((f, t)),
            onDetailedProgress: snapshots.Add);
        check(snapshots.Any(p => p.BytesExtracted > 0 && p.BytesExtracted < BigSize && p.FilesExtracted == 0),
            "unpack progress: a single large ZIP publishes actual bytes before the file completes");
        check(snapshots[^1] is { BytesExtracted: BigSize, TotalBytes: BigSize, FilesExtracted: 1, TotalFiles: 1 }
            && snapshots.Zip(snapshots.Skip(1)).All(p => p.Second.BytesExtracted >= p.First.BytesExtracted),
            "unpack progress: ZIP byte totals are exact and monotonic");
        check(legacy.SequenceEqual(new[] { (1, 1) })
            && File.ReadAllBytes(Path.Combine(target, "Game", "big.bin")).AsSpan().SequenceEqual(data),
            "unpack progress: legacy per-file callback and contents remain correct");
        check(snapshots.Any(p => p.CurrentEntry == "Game/big.bin"), "unpack progress: current entry is published");

        await CheckCancellationAsync(Request("zip"), zip, dir, "zip-cancel", check);

        var tar = Path.Combine(dir, "large.tar");
        using (var file = File.Create(tar))
        // 首条直接是普通文件，覆盖 ReaderFactory 重复回卷首条头部的问题，不能靠目录条目掩盖偏移。
        using (var writer = new TarWriter(file, TarEntryFormat.Ustar))
        using (var source = new MemoryStream(data, false))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "Game/big.bin") { DataStream = source });
        snapshots.Clear();
        legacy.Clear();
        target = UnpackService.PrepareGameDirectory(dir, "tar-output");
        await UnpackService.UnpackAsync(Request("tar"), tar, target, (f, t) => legacy.Add((f, t)),
            onDetailedProgress: snapshots.Add);
        check(snapshots.All(p => p.TotalBytes is null && p.TotalFiles is null)
            && snapshots[^1].BytesExtracted == BigSize && snapshots[^1].FilesExtracted == 1
            && snapshots.Any(p => p.BytesExtracted > 0 && p.BytesExtracted < BigSize),
            "unpack progress: streaming TAR reports actual bytes without inventing totals");
        check(File.ReadAllBytes(Path.Combine(target, "Game", "big.bin")).AsSpan().SequenceEqual(data),
            "unpack TAR: first regular entry contents are exact without a replayed archive header");
        check(legacy.SequenceEqual(new[] { (1, -1) }), "unpack progress: unknown legacy total remains -1");
        await CheckCancellationAsync(Request("tar"), tar, dir, "tar-cancel", check);
        var truncatedTar = Path.Combine(dir, "truncated.tar");
        var tarBytes = await File.ReadAllBytesAsync(tar);
        await File.WriteAllBytesAsync(truncatedTar, tarBytes.AsSpan(0, 512 + 64 * 1024).ToArray());
        var truncatedError = await Capture(() => UnpackService.UnpackAsync(Request("tar"), truncatedTar,
            UnpackService.PrepareGameDirectory(dir, "truncated-output")));
        check(truncatedError is not null, "unpack safety: truncated TAR data remains a failure");

        var tarGzip = Path.Combine(dir, "large.tar.gz");
        using (var file = File.Create(tarGzip))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest)) gzip.Write(tarBytes);
        snapshots.Clear();
        target = UnpackService.PrepareGameDirectory(dir, "tar-gzip-output");
        await UnpackService.UnpackAsync(Request("tar.gz"), tarGzip, target, onDetailedProgress: snapshots.Add);
        check(File.ReadAllBytes(Path.Combine(target, "Game", "big.bin")).AsSpan().SequenceEqual(data)
            && snapshots[^1].BytesExtracted == BigSize && snapshots.All(p => p.TotalBytes is null),
            "unpack TAR.GZ: first regular entry has exact contents and actual uncompressed byte progress");
        await CheckCancellationAsync(Request("tar.gz"), tarGzip, dir, "tar-gzip-cancel", check);

        var pax = Path.Combine(dir, "pax.tar");
        var paxData = new byte[] { 17, 33, 65 };
        using (var file = File.Create(pax))
        using (var writer = new TarWriter(file, TarEntryFormat.Pax))
        using (var source = new MemoryStream(paxData, false))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Game/pax.bin") { DataStream = source });
        target = UnpackService.PrepareGameDirectory(dir, "pax-output");
        await UnpackService.UnpackAsync(Request("tar"), pax, target);
        check(File.ReadAllBytes(Path.Combine(target, "Game", "pax.bin")).AsSpan().SequenceEqual(paxData),
            "unpack TAR: PAX metadata before the first file is preserved without duplicate rewinding");

        var escaped = Path.Combine(dir, "escaped.txt");
        var badNames = new[] { "../escaped.txt", "/absolute.txt", "C:/absolute.txt", "Game/data.bin:stream",
            "Game/NUL.txt", UnpackService.IncompleteMarker, "Game/../../escaped.txt" };
        for (var i = 0; i < badNames.Length; i++)
        {
            var badZip = Path.Combine(dir, $"bad-{i}.zip");
            WriteZip(badZip, badNames[i]);
            var error = await Capture(() => UnpackService.UnpackAsync(Request("zip"), badZip,
                UnpackService.PrepareGameDirectory(dir, $"bad-output-{i}")));
            check(error is InvalidDataException && !File.Exists(escaped),
                $"unpack safety: rejects traversal/absolute/ADS/device/marker path {i}");
        }

        var links = Path.Combine(dir, "links.tar");
        using (var file = File.Create(links))
        using (var writer = new TarWriter(file, TarEntryFormat.Ustar))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "linked") { LinkName = "../outside" });
        var linkError = await Capture(() => UnpackService.UnpackAsync(Request("tar"), links,
            UnpackService.PrepareGameDirectory(dir, "link-output")));
        check(linkError is InvalidDataException, "unpack safety: archive symbolic links are rejected");

        var linkRoot = UnpackService.PrepareGameDirectory(dir, "existing-link-output");
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(linkRoot, "Game"), outside);
            var existingLinkError = await Capture(() => UnpackService.UnpackAsync(Request("zip"), zip, linkRoot));
            check(existingLinkError is InvalidDataException && !File.Exists(Path.Combine(outside, "big.bin")),
                "unpack safety: existing directory symlink cannot redirect output");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Console.WriteLine($"SKIP  creating directory symlink requires OS permission ({ex.GetType().Name})");
        }

        var duplicate = Path.Combine(dir, "duplicate.zip");
        using (var file = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            using (var output = file.CreateEntry("duplicate.txt").Open()) output.WriteByte(1);
            using (var output = file.CreateEntry("duplicate.txt").Open()) output.WriteByte(2);
        }
        var duplicateTarget = UnpackService.PrepareGameDirectory(dir, "duplicate-output");
        var duplicateError = await Capture(() => UnpackService.UnpackAsync(Request("zip"), duplicate, duplicateTarget));
        check(duplicateError is IOException && File.ReadAllBytes(Path.Combine(duplicateTarget, "duplicate.txt"))[0] == 1,
            "unpack safety: duplicate entries cannot overwrite earlier files");

        var sevenZip = Path.Combine(dir, "solid.7z");
        await File.WriteAllBytesAsync(sevenZip, Convert.FromBase64String(SolidFixture));
        snapshots.Clear();
        legacy.Clear();
        target = UnpackService.PrepareGameDirectory(dir, "sevenzip-output");
        await UnpackService.UnpackAsync(Request("7z"), sevenZip, target, (f, t) => legacy.Add((f, t)),
            onDetailedProgress: snapshots.Add);
        var backend = OperatingSystem.IsWindows() ? "native" : "managed";
        check(File.ReadAllBytes(Path.Combine(target, "Game", "big.bin")).AsSpan().SequenceEqual(data)
            && File.ReadAllText(Path.Combine(target, "Game", "small.txt")) == SmallText
            && File.Exists(Path.Combine(target, "Game", "empty.txt")),
            $"7z {backend}: solid archive extracts all files with exact contents");
        check(snapshots[^1] is { FilesExtracted: 3, TotalFiles: 3 }
            && snapshots[^1].BytesExtracted == BigSize + SmallText.Length
            && snapshots[^1].TotalBytes == BigSize + SmallText.Length
            && snapshots.Any(p => p.BytesExtracted > 0 && p.BytesExtracted < BigSize && p.FilesExtracted < 3)
            && legacy.Count == 3 && legacy[^1] == (3, 3),
            $"7z {backend}: byte progress advances inside a solid member and preserves file callbacks");
        await CheckCancellationAsync(Request("7z"), sevenZip, dir, "sevenzip-cancel", check);

        var encrypted = Path.Combine(dir, "encrypted.7z");
        await File.WriteAllBytesAsync(encrypted, Convert.FromBase64String(EncryptedFixture));
        check(UnpackService.ResolveGameDirectoryName(Request("7z", "TEST-PASSWORD"), encrypted) == "Game",
            $"7z {backend}: encrypted headers resolve the game directory");
        var encryptedTarget = UnpackService.PrepareGameDirectory(dir, "encrypted-output");
        await UnpackService.UnpackAsync(Request("7z", "TEST-PASSWORD"), encrypted, encryptedTarget);
        check(File.ReadAllBytes(Path.Combine(encryptedTarget, "Game", "big.bin")).AsSpan().SequenceEqual(data),
            $"7z {backend}: encrypted solid contents are decoded correctly");
        var wrongPassword = await Capture(() => UnpackService.UnpackAsync(Request("7z", "incorrect"), encrypted,
            UnpackService.PrepareGameDirectory(dir, "wrong-password")));
        check(wrongPassword is not null && !wrongPassword.ToString().Contains("TEST-PASSWORD", StringComparison.Ordinal),
            $"7z {backend}: wrong password fails without exposing the password");
        var brokenBytes = Convert.FromBase64String(SolidFixture);
        brokenBytes[32] ^= 0x20; // 7z 头后的第一个压缩数据字节，头部结构仍然可读。
        var broken = Path.Combine(dir, "broken.7z");
        await File.WriteAllBytesAsync(broken, brokenBytes);
        var corrupt = await Capture(() => UnpackService.UnpackAsync(Request("7z"), broken,
            UnpackService.PrepareGameDirectory(dir, "broken-output")));
        check(corrupt is not null, $"7z {backend}: corrupted packed data is rejected");

        foreach (var architecture in new[] { Architecture.X86, Architecture.X64, Architecture.Arm64 })
            check(File.Exists(SevenZipUnpacker.GetLibraryPath(AppContext.BaseDirectory, architecture)),
                $"7z package: private native asset is present for {architecture}");
        check(Marshal.SizeOf<SevenZipNative.PropVariant>() == (IntPtr.Size == 8 ? 24 : 16),
            "7z ABI: PROPVARIANT has the correct pointer-width layout");

        if (OperatingSystem.IsWindows())
        {
            var libraryPath = SevenZipUnpacker.GetLibraryPath(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture);
            Exception? locked = null;
            try { using var file = File.Open(libraryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception ex) { locked = ex; }
            check(locked is null, $"7z native: DLL is no longer locked after success/cancellation/password/data failure ({locked?.Message})");
            try
            {
                SevenZipUnpacker.SetPluginDirectory(dir);
                var missing = await Capture(() => UnpackService.UnpackAsync(Request("7z"), sevenZip,
                    UnpackService.PrepareGameDirectory(dir, "missing-native-output")));
                check(missing is FileNotFoundException, "7z native: missing private DLL is an explicit error without fallback");
            }
            finally { SevenZipUnpacker.SetPluginDirectory(AppContext.BaseDirectory); }
        }
        else Console.WriteLine("SKIP  native 7z.dll execution/unload checks require Windows; managed fallback and packaged assets verified");
    }

    private static async Task CheckCancellationAsync(InstallRequest request, string packPath, string root,
        string name, Action<bool, string> check)
    {
        using var cts = new CancellationTokenSource();
        var target = UnpackService.PrepareGameDirectory(root, name);
        var error = await Capture(() => UnpackService.UnpackAsync(request, packPath, target, ct: cts.Token,
            onDetailedProgress: progress => { if (progress.BytesExtracted > 0) cts.Cancel(); }));
        var file = Path.Combine(target, "Game", "big.bin");
        check(error is OperationCanceledException && File.Exists(file)
            && new FileInfo(file).Length is > 0 and < BigSize,
            $"unpack cancel: {name} stops inside a large file and preserves OperationCanceledException");
        // 文件已关闭，管理器才能安全删除中断目录；不依赖 GC 释放句柄。
        using var unlocked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        check(unlocked.Length < BigSize, $"unpack cancel: {name} releases its output handle immediately");
    }

    private static InstallRequest Request(string format, string? password = null) => new()
    {
        V = 1, Provider = "shionlib", ResourceId = "fixture", Url = "https://example.com/game",
        FileName = "game." + format, ArchiveFormat = format, Size = 1, BgmId = "13", Title = "CLANNAD",
        ArchivePassword = password,
    };

    private static void WriteZip(string path, string name)
    {
        using var file = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = file.CreateEntry(name).Open();
        output.WriteByte(42);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    // 自建固定 fixture：Game/{big.bin(8MiB, byte i%251),empty.txt,small.txt}，官方 7-Zip 26.03、LZMA2 solid。
    // 固定字节使 Windows 不依赖额外安装 7z.exe 就能覆盖原生路径；加密包额外启用 -mhe=on。
    private const string SolidFixture = "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";
    private const string EncryptedFixture = "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";
}
