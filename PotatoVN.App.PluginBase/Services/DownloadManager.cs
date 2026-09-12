using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.WinApp.Base.Contracts;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

public enum DownloadTaskStage
{
    Pending,
    Downloading,
    Verifying,
    Unpacking,
    Importing,
    Completed,
    Failed,
}

/// <summary>单个下载任务的可观察状态（供 UI 绑定）。</summary>
public partial class DownloadTask : ObservableObject
{
    public InstallRequest Request { get; }
    public string Title => Request.Title;

    [ObservableProperty] private DownloadTaskStage _stage = DownloadTaskStage.Pending;
    [ObservableProperty] private long _current;
    [ObservableProperty] private long _total;
    [ObservableProperty] private string _message = string.Empty;

    public DownloadTask(InstallRequest request)
    {
        Request = request;
        Total = (long)request.Size;
    }

    /// <summary>进度百分比 0-100。</summary>
    public double ProgressPercent => Total <= 0 ? 0 : (double)Current / Total * 100;
}

/// <summary>
/// 下载任务管理器：维护任务列表、串行执行下载→解压→入库流程，并向 UI 广播进度。
/// </summary>
public class DownloadManager
{
    private readonly IPotatoVnApi _hostApi;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    public DownloadManager(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>收到新推送时入队并开始处理。</summary>
    public async Task EnqueueAsync(InstallRequest request)
    {
        var task = new DownloadTask(request);
        Tasks.Add(task);
        await _gate.WaitAsync();
        try
        {
            await ProcessAsync(task);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(DownloadTask task)
    {
        try
        {
            // 1. 创建带外部 ID 的占位游戏（用于精确匹配）
            task.Stage = DownloadTaskStage.Importing;
            task.Message = "创建游戏占位...";
            var library = new LibraryService(_hostApi);
            await library.EnsurePlaceholderAsync(task.Request);

            // 2. 下载目录：设置项或插件目录下
            var downloadDir = string.IsNullOrWhiteSpace(Plugin.DownloadPath)
                ? System.IO.Path.Combine(_hostApi.GetPluginPath(), "downloads")
                : Plugin.DownloadPath;
            System.IO.Directory.CreateDirectory(downloadDir);
            var packPath = System.IO.Path.Combine(downloadDir, task.Request.FileName);

            // 3. 下载 + 校验（多线程分块 + 断点续传）
            task.Stage = DownloadTaskStage.Downloading;
            task.Message = "下载中...";
            var download = new DownloadService();
            await download.DownloadAsync(task.Request, packPath,
                (current, total) =>
                {
                    task.Current = current;
                    task.Total = total;
                    task.Message = $"下载中 {FormatBytes(current)} / {FormatBytes(total)}";
                });

            task.Stage = DownloadTaskStage.Verifying;
            task.Message = "校验中...";
            await DownloadService.VerifyChecksumAsync(task.Request, packPath);

            // 4. 解压
            task.Stage = DownloadTaskStage.Unpacking;
            task.Message = "解压中...";
            var gameDirName = UnpackService.ResolveGameDirectoryName(task.Request, packPath);
            var gamePath = System.IO.Path.Combine(downloadDir, gameDirName);
            if (System.IO.Directory.Exists(gamePath))
                System.IO.Directory.Delete(gamePath, true);
            System.IO.Directory.CreateDirectory(gamePath);
            await UnpackService.UnpackAsync(task.Request, packPath, gamePath,
                (finished, total) => task.Message = $"解压中 {finished}/{total}");

            // 5. 入库 + 刮削
            task.Stage = DownloadTaskStage.Importing;
            task.Message = "入库刮削中...";
            await library.AddInstallationAsync(task.Request, gamePath);

            // 6. 清理压缩包
            System.IO.File.Delete(packPath);

            task.Stage = DownloadTaskStage.Completed;
            task.Current = task.Total;
            task.Message = "完成";
            _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                "PotatoDownload", $"完成: {task.Title}");
        }
        catch (Exception e)
        {
            task.Stage = DownloadTaskStage.Failed;
            task.Message = e.Message;
            _hostApi.Event(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "PotatoDownload",
                e, $"处理推送失败: {task.Title}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F1} {units[unit]}";
    }
}
