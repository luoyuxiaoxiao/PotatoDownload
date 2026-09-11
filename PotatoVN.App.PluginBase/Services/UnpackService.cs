using System;
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
/// </summary>
public static class UnpackService
{
    /// <summary>
    /// 解压压缩包到目标目录。
    /// </summary>
    /// <param name="request">推送请求（含压缩格式与密码）</param>
    /// <param name="packPath">压缩包路径</param>
    /// <param name="targetDirectory">解压目标目录（必须已存在）</param>
    /// <param name="onProgress">进度回调 (已解压文件数, 总文件数)</param>
    /// <param name="ct">取消令牌</param>
    public static Task UnpackAsync(InstallRequest request, string packPath, string targetDirectory,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using IArchive archive = ArchiveFactory.OpenArchive(packPath,
                new ReaderOptions { Password = request.ArchivePassword });
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var total = entries.Count;
            var finished = 0;
            var options = new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            };
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                entry.WriteToDirectory(targetDirectory, options);
                finished++;
                onProgress?.Invoke(finished, total);
            }
        }, ct);
    }

    /// <summary>
    /// 根据压缩包内容决定游戏目录：压缩包内只有一个顶层文件夹时使用该文件夹名，否则使用压缩包文件名（去扩展名）。
    /// </summary>
    public static string ResolveGameDirectoryName(InstallRequest request, string packPath)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(packPath,
            new ReaderOptions { Password = request.ArchivePassword });
        var topLevelDirs = archive.Entries
            .Where(e => !e.IsDirectory)
            .Select(e => e.Key?.Split('/', '\\')[0])
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (topLevelDirs.Count == 1)
            return topLevelDirs[0]!;

        var fileName = Path.GetFileName(packPath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(stem) ? "game" : stem;
    }
}
