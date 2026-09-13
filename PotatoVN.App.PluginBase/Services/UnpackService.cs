using System;
using System.Collections.Generic;
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
/// 解压服务：使用 SharpCompress 解压 7z/zip/rar/tar 等格式，支持密码。
/// 路径穿越（zip-slip）与符号链接越界由 SharpCompress 的 WriteEntryToDirectory 拒绝。
/// </summary>
public static class UnpackService
{
    /// <summary>
    /// 解压未完成标记：只有带此标记的目录（本插件上次没做完的解压）才会在重试时被清理重建；
    /// 用户已有的同名目录一律不动。解压入库全部完成后由 <see cref="MarkComplete"/> 移除。
    /// </summary>
    private const string IncompleteMarker = ".potatodownload-incomplete";

    /// <summary>
    /// 解压压缩包到目标目录。
    /// </summary>
    /// <param name="request">推送请求（含压缩格式与密码）</param>
    /// <param name="packPath">压缩包路径</param>
    /// <param name="targetDirectory">解压目标目录（必须已存在）</param>
    /// <param name="onProgress">进度回调 (已解压文件数, 总文件数)；总数未知时为 -1</param>
    /// <param name="ct">取消令牌</param>
    public static Task UnpackAsync(InstallRequest request, string packPath, string targetDirectory,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var options = new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            };
            if (IsTarFamily(request))
            {
                // tar 及其压缩变体只能流式读取（ArchiveFactory 认不出压缩过的 tar）；总数未知
                using var tar = TarSource.Open(request, packPath);
                ExtractSequential(tar.Reader, targetDirectory, options, -1, onProgress, ct);
                return;
            }

            using var archive = ArchiveFactory.OpenArchive(packPath, OptionsFor(request));
            var total = archive.Entries.Count(e => !e.IsDirectory);
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                // solid 7z/rar 逐条 entry.WriteToDirectory 每个文件都要从 solid 块头重新解压（O(n²)），必须顺序读取
                using var reader = archive.ExtractAllEntries();
                ExtractSequential(reader, targetDirectory, options, total, onProgress, ct);
                return;
            }

            var finished = 0;
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                ct.ThrowIfCancellationRequested();
                entry.WriteToDirectory(targetDirectory, options);
                onProgress?.Invoke(++finished, total);
            }
        }, ct);
    }

    private static void ExtractSequential(IReader reader, string targetDirectory, ExtractionOptions options,
        int total, Action<int, int>? onProgress, CancellationToken ct)
    {
        var finished = 0;
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;
            reader.WriteEntryToDirectory(targetDirectory, options);
            onProgress?.Invoke(++finished, total);
        }
    }

    /// <summary>
    /// 根据压缩包内容决定游戏目录名：压缩包内只有一个顶层文件夹时使用该文件夹名，否则使用压缩包文件名（去扩展名）。
    /// 顶层名来自压缩包内容（推送方可控），必须是安全的单段目录名，否则同样退回压缩包文件名。
    /// </summary>
    public static string ResolveGameDirectoryName(InstallRequest request, string packPath)
    {
        var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in EntryKeys(request, packPath))
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

    private static IEnumerable<string> EntryKeys(InstallRequest request, string packPath)
    {
        if (IsTarFamily(request))
        {
            using var tar = TarSource.Open(request, packPath);
            while (tar.Reader.MoveToNextEntry())
                if (!tar.Reader.Entry.IsDirectory && tar.Reader.Entry.Key is { } key) yield return key;
            yield break;
        }
        using var archive = ArchiveFactory.OpenArchive(packPath, OptionsFor(request));
        foreach (var entry in archive.Entries)
            if (!entry.IsDirectory && entry.Key is { } key) yield return key;
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

        public static TarSource Open(InstallRequest request, string packPath)
        {
            var source = new TarSource();
            try
            {
                var options = OptionsFor(request) with { LeaveStreamOpen = true, ExtensionHint = "tar" };
                source._file = File.OpenRead(packPath);
                var compression = request.ArchiveFormat switch
                {
                    "tar.gz" => CompressionType.GZip,
                    "tar.bz2" => CompressionType.BZip2,
                    "tar.xz" => CompressionType.Xz,
                    "tar.zst" => CompressionType.ZStandard,
                    _ => CompressionType.None,
                };
                source._tar = compression == CompressionType.None
                    ? source._file
                    : options.Providers.CreateDecompressStream(compression, source._file);
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
