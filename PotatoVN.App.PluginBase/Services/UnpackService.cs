using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PotatoVN.App.PluginBase.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 解压服务：Windows 上的 7z 使用官方原生引擎，其它格式/平台使用 SharpCompress。
/// 两个引擎共用安全输出流，按实际写出的字节刷新进度，并在文件内部响应取消。
/// </summary>
public static class UnpackService
{
    /// <summary>
    /// 解压未完成标记：只有带此标记的目录（本插件上次没做完的解压）才会在重试时被清理重建；
    /// 用户已有的同名目录一律不动。解压入库全部完成后由 <see cref="MarkComplete"/> 移除。
    /// </summary>
    internal const string IncompleteMarker = ".potatodownload-incomplete";

    /// <summary>
    /// 解压压缩包到目标目录。
    /// </summary>
    /// <param name="request">推送请求（含压缩格式与密码）</param>
    /// <param name="packPath">压缩包路径</param>
    /// <param name="targetDirectory">解压目标目录（必须已存在）</param>
    /// <param name="onProgress">进度回调 (已解压文件数, 总文件数)；总数未知时为 -1</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onDetailedProgress">实际输出字节、文件计数和当前条目的快照</param>
    public static Task UnpackAsync(InstallRequest request, string packPath, string targetDirectory,
        Action<int, int>? onProgress = null, CancellationToken ct = default,
        Action<UnpackProgress>? onDetailedProgress = null)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows() && request.ArchiveFormat == "7z")
            {
                // 原生引擎缺失/损坏必须明确报错；静默回退会把性能问题隐藏成偶发卡顿。
                SevenZipUnpacker.Extract(request, packPath, targetDirectory, onProgress, ct, onDetailedProgress);
                return;
            }
            if (IsTarFamily(request))
            {
                // tar 及其压缩变体只能流式读取（ArchiveFactory 认不出压缩过的 tar）；总数未知
                using var tar = TarSource.Open(request, packPath, ct);
                var progress = new ExtractionSession(targetDirectory, null, null, onProgress, onDetailedProgress, ct);
                ExtractSequential(tar.Reader, progress, ct);
                return;
            }

            using var archive = ArchiveFactory.OpenArchive(packPath, OptionsFor(request));
            var entries = archive.Entries.ToArray();
            var total = entries.Count(e => !e.IsDirectory);
            var session = new ExtractionSession(targetDirectory, TotalSize(entries), total,
                onProgress, onDetailedProgress, ct);
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                // solid 7z/rar 逐条 entry.WriteToDirectory 每个文件都要从 solid 块头重新解压（O(n²)），必须顺序读取
                using var reader = archive.ExtractAllEntries();
                ExtractSequential(reader, session, ct);
                return;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                using var output = session.OpenEntry(entry.Key, entry.IsDirectory, entry.Size, entry.LinkTarget, AttributesFor(entry));
                if (output is null) continue;
                using (var input = entry.OpenEntryStream()) CopyEntry(input, output, ct);
                session.CompleteEntry(output, entry.LastModifiedTime);
            }
        }, ct);
    }

    private static long? TotalSize(IEnumerable<IEntry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (entry.Size < 0 || entry.Size > long.MaxValue - total) return null;
            total += entry.Size;
        }
        return total;
    }

    private static int? AttributesFor(IEntry entry)
    {
        try { return entry.Attrib; }
        // SharpCompress 的 tar 条目没有实现可选 Attrib；路径及 LinkTarget 校验仍然照常执行。
        catch (NotImplementedException) { return null; }
    }

    private static void ExtractSequential(IReader reader, ExtractionSession session, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.MoveToNextEntry()) break;
                var entry = reader.Entry;
                using var output = session.OpenEntry(entry.Key, entry.IsDirectory, entry.Size, entry.LinkTarget, AttributesFor(entry));
                if (output is null) continue;
                using (var input = reader.OpenEntryStream())
                {
                    try { CopyEntry(input, output, ct); }
                    catch
                    {
                        // EntryStream.Dispose 默认会把剩余文件解完；先 Cancel 才能立即退出一个大 solid 条目。
                        reader.Cancel();
                        throw;
                    }
                }
                session.CompleteEntry(output, entry.LastModifiedTime);
            }
        }
        catch
        {
            // 路径/元数据校验或 MoveToNextEntry 抛错时也终止 reader，禁止清理继续扫描 solid 数据。
            reader.Cancel();
            throw;
        }
    }

    private static void CopyEntry(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                output.Write(buffer, 0, count);
            }
            ct.ThrowIfCancellationRequested();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>所有解压引擎共享同一处路径校验和进度统计，避免改用原生库时丢失安全边界。</summary>
    internal sealed class ExtractionSession
    {
        private readonly string _root;
        private readonly string _rootPrefix;
        private readonly long? _totalBytes;
        private readonly int? _totalFiles;
        private readonly Action<int, int>? _onProgress;
        private readonly Action<UnpackProgress>? _onDetailedProgress;
        private readonly CancellationToken _ct;
        private long _bytes;
        private int _files;
        private string? _currentEntry;
        private long _lastReport;
        private bool _reportedBytes;

        internal ExtractionSession(string targetDirectory, long? totalBytes, int? totalFiles,
            Action<int, int>? onProgress, Action<UnpackProgress>? onDetailedProgress, CancellationToken ct)
        {
            _root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _rootPrefix = _root + Path.DirectorySeparatorChar;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _onProgress = onProgress;
            _onDetailedProgress = onDetailedProgress;
            _ct = ct;
            RejectReparsePoint(_root);
            Report(true);
        }

        internal OutputStream? OpenEntry(string? key, bool directory, long? size, string? linkTarget = null, int? attributes = null)
        {
            _ct.ThrowIfCancellationRequested();
            ValidateLink(key, linkTarget, attributes);
            var path = SafeEntryPath(key, directory);
            if (directory)
            {
                EnsureDirectory(path);
                return null;
            }
            EnsureDirectory(Path.GetDirectoryName(path)!);
            RejectReparsePoint(path);
            _currentEntry = key;
            Report(true);
            // 不覆盖已有文件：同时拒绝重复条目和预先放置的硬链接，防止写到目标目录以外。
            return new OutputStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.SequentialScan), size is >= 0 ? size : null, this, _ct);
        }

        internal void ValidateEntry(string? key, bool directory, string? linkTarget, int? attributes)
        {
            _ct.ThrowIfCancellationRequested();
            ValidateLink(key, linkTarget, attributes);
            _ = SafeEntryPath(key, directory);
        }

        private static void ValidateLink(string? key, string? linkTarget, int? attributes)
        {
            var attr = unchecked((uint)(attributes ?? 0));
            // 7z/zip 的 Unix mode 放在高 16 位；Windows 的重解析点同样不可交给文件写入流跟随。
            if (!string.IsNullOrEmpty(linkTarget) || (attr & (uint)FileAttributes.ReparsePoint) != 0
                || ((attr >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException($"压缩包包含不支持的链接条目：{key}");
        }

        private string SafeEntryPath(string? key, bool directory)
        {
            if (string.IsNullOrEmpty(key) || key[0] is '/' or '\\') throw UnsafePath(key);
            var segments = new List<string>();
            foreach (var segment in key.Replace('\\', '/').Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue; // tar 常见的 ./ 前缀
                if (!InstallRequest.IsSafeFileName(segment)
                    || segment.Equals(IncompleteMarker, StringComparison.OrdinalIgnoreCase)) throw UnsafePath(key);
                segments.Add(segment);
            }
            if (segments.Count == 0)
            {
                if (directory) return _root;
                throw UnsafePath(key);
            }
            var path = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments.ToArray())));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(_rootPrefix, comparison)) throw UnsafePath(key);
            return path;
        }

        private static InvalidDataException UnsafePath(string? key) =>
            new($"压缩条目路径不安全或越出目标目录：{key}");

        private void EnsureDirectory(string directory)
        {
            // 已存在的目录也逐层检查，不能让归档中的相对路径穿过外部创建的符号链接/目录联接。
            var relative = Path.GetRelativePath(_root, directory);
            var current = _root;
            RejectReparsePoint(current);
            if (relative == ".") return;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                RejectReparsePoint(current);
                Directory.CreateDirectory(current);
                RejectReparsePoint(current);
            }
        }

        private static void RejectReparsePoint(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"解压路径不能经过符号链接或目录联接：{path}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        internal void CompleteEntry(OutputStream output, DateTime? lastModified = null)
        {
            _ct.ThrowIfCancellationRequested();
            output.VerifyLength();
            output.Dispose(); // 文件句柄关闭后才对外宣布文件完成
            if (lastModified is { } timestamp)
            {
                try { File.SetLastWriteTimeUtc(output.FilePath, timestamp.ToUniversalTime()); }
                catch (ArgumentException) { /* 不合法的归档时间不影响已解出的文件 */ }
            }
            _files++;
            _onProgress?.Invoke(_files, _totalFiles ?? -1);
            Report(true);
        }

        private void Wrote(int count)
        {
            _bytes = checked(_bytes + count);
            Report(false);
        }

        private void Report(bool force)
        {
            if (_onDetailedProgress is null) return;
            var now = Stopwatch.GetTimestamp();
            // 模型始终累计真实字节；快照最多每 100ms 一次，首批字节及文件边界立即发布。
            if (!force && _reportedBytes && Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 100) return;
            _lastReport = now;
            _reportedBytes |= _bytes > 0;
            _onDetailedProgress(new UnpackProgress(_bytes, _totalBytes, _files, _totalFiles, _currentEntry));
        }

        internal sealed class OutputStream : Stream
        {
            private readonly FileStream _file;
            private readonly long? _expectedLength;
            private readonly ExtractionSession _session;
            private readonly CancellationToken _ct;
            private long _written;

            internal OutputStream(FileStream file, long? expectedLength, ExtractionSession session, CancellationToken ct)
            {
                _file = file;
                _expectedLength = expectedLength;
                _session = session;
                _ct = ct;
            }

            internal string FilePath => _file.Name;
            internal void VerifyLength()
            {
                if (_expectedLength is { } expected && _written != expected)
                    throw new InvalidDataException($"解压输出长度不符：{_session._currentEntry}");
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _ct.ThrowIfCancellationRequested();
                if (_expectedLength is { } expected && buffer.Length > expected - _written)
                    throw new InvalidDataException($"解压输出超过条目声明的长度：{_session._currentEntry}");
                _file.Write(buffer);
                _written += buffer.Length;
                _session.Wrote(buffer.Length);
                _ct.ThrowIfCancellationRequested();
            }
            public override void Flush() => _file.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => _file.CanWrite;
            public override long Length => _written;
            public override long Position { get => _written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _file.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// 根据压缩包内容决定游戏目录名：压缩包内只有一个顶层文件夹时使用该文件夹名，否则使用压缩包文件名（去扩展名）。
    /// 顶层名来自压缩包内容（推送方可控），必须是安全的单段目录名，否则同样退回压缩包文件名。
    /// </summary>
    public static string ResolveGameDirectoryName(InstallRequest request, string packPath, CancellationToken ct = default)
    {
        var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in EntryKeys(request, packPath, ct))
        {
            var first = key.Split('/', '\\')[0];
            if (first.Length == 0) continue;
            topLevel.Add(first);
            if (topLevel.Count > 1) break; // 已确定不是单一顶层目录，流式格式无需再读完整个包
        }
        if (topLevel.Count == 1 && InstallRequest.IsSafeFileName(topLevel.First()))
            return topLevel.First();

        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(packPath));
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        return InstallRequest.IsSafeFileName(stem) ? stem : "game";
    }

    private static IEnumerable<string> EntryKeys(InstallRequest request, string packPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && request.ArchiveFormat == "7z")
        {
            foreach (var key in SevenZipUnpacker.EntryKeys(request, packPath, ct)) yield return key;
            yield break;
        }
        if (IsTarFamily(request))
        {
            using var tar = TarSource.Open(request, packPath, ct);
            while (tar.Reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (!tar.Reader.Entry.IsDirectory && tar.Reader.Entry.Key is { } key) yield return key;
            }
            yield break;
        }
        using var archive = ArchiveFactory.OpenArchive(packPath, OptionsFor(request));
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory && entry.Key is { } key) yield return key;
        }
    }

    private static bool IsTarFamily(InstallRequest request) =>
        request.ArchiveFormat.StartsWith("tar", StringComparison.Ordinal);

    private static ReaderOptions OptionsFor(InstallRequest request) => new()
    {
        Password = request.ArchivePassword,
        LeaveStreamOpen = false, // 文件流由 SharpCompress 打开，随 archive 一起关闭
    };

    /// <summary>
    /// tar 系列的读取器：按 archive_format 自己剥掉外层压缩（gz/bz2/xz/zst），再把纯 tar 交给读取器。
    /// 不让 ReaderFactory 直接探测压缩过的 tar——探测 bz2 时要先解开整个首块（可达 900KB）再回卷，
    /// 会撞上它的回卷缓冲上限。
    /// </summary>
    private sealed class TarSource : IDisposable
    {
        private Stream? _file;
        private Stream? _tar;
        private IReader? _reader;

        public IReader Reader => _reader!;

        public static TarSource Open(InstallRequest request, string packPath, CancellationToken ct)
        {
            var source = new TarSource();
            try
            {
                var options = OptionsFor(request) with { LeaveStreamOpen = true };
                source._file = File.OpenRead(packPath);
                var compression = request.ArchiveFormat switch
                {
                    "tar.gz" => CompressionType.GZip,
                    "tar.bz2" => CompressionType.BZip2,
                    "tar.xz" => CompressionType.Xz,
                    "tar.zst" => CompressionType.ZStandard,
                    _ => CompressionType.None,
                };
                var tarStream = compression == CompressionType.None
                    ? source._file
                    : options.Providers.CreateDecompressStream(compression, source._file);
                // TarReadOnlySubStream.Dispose 会跳过剩余内容；在解压后的流上检查取消，避免收尾继续解完整个大文件。
                source._tar = new CancellableReadStream(tarStream, ct);
                // 外层已剥掉，普通探测只读纯 tar 头；不能设置 ExtensionHint="tar"，0.50.4 的
                // hint 成功分支会重复回卷首个文件头，导致头部写进正文并在下一条报 EOF。
                source._reader = ReaderFactory.OpenReader(source._tar, options);
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // 只是清理：tar 读到归档结束标记就停，外层 gzip 尾部没被读完，GZipStream 在关闭时会报 CRC 错——
            // 整包完整性已由下载后的哈希校验保证，这里不能让清理异常盖过真正的结果
            try { _reader?.Dispose(); } catch { /* ignore */ }
            try { if (!ReferenceEquals(_tar, _file)) _tar?.Dispose(); } catch { /* ignore */ }
            _file?.Dispose();
        }
    }

    private sealed class CancellableReadStream : Stream
    {
        private readonly Stream _stream;
        private readonly CancellationToken _ct;
        internal CancellableReadStream(Stream stream, CancellationToken ct) { _stream = stream; _ct = ct; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            return _stream.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            _ct.ThrowIfCancellationRequested();
            return _stream.Read(buffer);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _ct.ThrowIfCancellationRequested();
            return _stream.Seek(offset, origin);
        }
        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() => _stream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 在下载目录下准备游戏目录并返回完整路径。目录必须落在下载目录内；
    /// 已存在的目录只有带 <see cref="IncompleteMarker"/> 才清理重建，否则改用"名称 (2)"这样的唯一名称，
    /// 绝不删除不是本插件创建的目录。
    /// </summary>
    public static string PrepareGameDirectory(string downloadDirectory, string gameDirectoryName)
    {
        if (!InstallRequest.IsSafeFileName(gameDirectoryName))
            throw new InvalidOperationException($"非法的游戏目录名: {gameDirectoryName}");
        var root = Path.GetFullPath(downloadDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, gameDirectoryName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"游戏目录越出下载目录: {path}");

        for (var suffix = 2; ; suffix++)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, IncompleteMarker)))
            {
                Directory.Delete(path, true); // 本插件上次未完成的解压残留
                break;
            }
            if (!Directory.Exists(path) && !File.Exists(path)) break;
            path = Path.Combine(root, $"{gameDirectoryName} ({suffix})");
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, IncompleteMarker), string.Empty);
        return path;
    }

    /// <summary>解压与入库全部完成：移除未完成标记，此后该目录不会再被本插件删除。</summary>
    public static void MarkComplete(string gamePath)
    {
        try
        {
            File.Delete(Path.Combine(gamePath, IncompleteMarker));
        }
        catch
        {
            // 标记删不掉只影响下次重试时是否清理，不影响本次结果
        }
    }
}
