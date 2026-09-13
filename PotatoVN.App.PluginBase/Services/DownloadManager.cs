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
    [ObservableProperty] private double _speedBytesPerSec;

    public DownloadTask(InstallRequest request)
    {
        Request = request;
        Total = (long)request.Size;
    }

    /// <summary>进度百分比 0-100。</summary>
    public double ProgressPercent => Total <= 0 ? 0 : (double)Current / Total * 100;

    public bool IsActive => Stage is DownloadTaskStage.Pending or DownloadTaskStage.Downloading
        or DownloadTaskStage.Verifying or DownloadTaskStage.Unpacking or DownloadTaskStage.Importing;
}

/// <summary>
/// 下载任务管理器：维护任务队列、串行执行下载→解压→入库流程，记录历史并广播状态。
/// </summary>
public class DownloadManager
{
    private const int MaxHistoryCount = 50;
    private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPotatoVnApi _hostApi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _lastActiveCount;

    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>活动任务数变化时触发（用于侧边栏状态刷新），参数为当前活动任务数。</summary>
    public event Action<int>? ActiveTaskCountChanged;

    public DownloadManager(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>收到新推送时入队并开始处理。</summary>
    public async Task EnqueueAsync(InstallRequest request)
    {
        var task = new DownloadTask(request);
        Tasks.Add(task);
        BumpActiveCount();
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

    private void BumpActiveCount()
    {
        var count = 0;
        foreach (var task in Tasks)
            if (task.IsActive) count++;
        if (count == _lastActiveCount) return;
        _lastActiveCount = count;
        ActiveTaskCountChanged?.Invoke(count);
    }

    private async Task ProcessAsync(DownloadTask task)
    {
        try
        {
            // 1. 创建带外部 ID 的占位游戏（用于精确匹配；失败不阻断）
            task.Stage = DownloadTaskStage.Importing;
            task.Message = "创建游戏占位...";
            var library = new LibraryService(_hostApi);
            await library.EnsurePlaceholderAsync(task.Request);

            // 2. 下载目录：设置项，或系统盘 Galgame 文件夹（自动创建）
            var downloadDir = string.IsNullOrWhiteSpace(Plugin.DownloadPath)
                ? Plugin.DefaultDownloadPath
                : Plugin.DownloadPath;
            System.IO.Directory.CreateDirectory(downloadDir);
            var packPath = System.IO.Path.Combine(downloadDir, task.Request.FileName);

            // 3. 下载 + 校验（多线程分块 + 断点续传），速度做 EMA 平滑，UI 更新限流 500ms
            task.Stage = DownloadTaskStage.Downloading;
            task.Message = "下载中...";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long lastTickBytes = 0, lastTickMs = 0;
            var lastUiMs = -UiUpdateInterval.TotalMilliseconds;
            double smoothedSpeed = 0;
            var download = new DownloadService();
            await download.DownloadAsync(task.Request, packPath,
                (current, total) =>
                {
                    var nowMs = watch.ElapsedMilliseconds;
                    if (lastTickMs > 0 && nowMs > lastTickMs)
                    {
                        var instant = (current - lastTickBytes) * 1000.0 / (nowMs - lastTickMs);
                        smoothedSpeed = smoothedSpeed <= 0 ? instant : smoothedSpeed * 0.7 + instant * 0.3;
                    }
                    lastTickBytes = current;
                    lastTickMs = nowMs;
                    if (nowMs - lastUiMs >= UiUpdateInterval.TotalMilliseconds || current >= total)
                    {
                        lastUiMs = nowMs;
                        task.Current = current;
                        task.Total = total;
                        task.SpeedBytesPerSec = smoothedSpeed;
                        task.Message = $"下载中 {FormatBytes(current)} / {FormatBytes(total)} · {FormatBytes((long)smoothedSpeed)}/s";
                    }
                });

            task.SpeedBytesPerSec = 0;
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
            RecordHistory(task, DownloadRecord.OutcomeCompleted);
            _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                "PotatoDownload", $"完成: {task.Title}");
        }
        catch (Exception e)
        {
            task.Stage = DownloadTaskStage.Failed;
            task.Message = e.Message;
            task.SpeedBytesPerSec = 0;
            RecordHistory(task, DownloadRecord.OutcomeFailed);
            _hostApi.Event(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "PotatoDownload",
                e, $"处理推送失败: {task.Title}");
        }
        finally
        {
            BumpActiveCount();
        }
    }

    /// <summary>把完成/失败的任务写入持久化历史（最新在前，最多保留 MaxHistoryCount 条）。</summary>
    private void RecordHistory(DownloadTask task, string outcome)
    {
        try
        {
            var history = Plugin.HistoryCollection;
            _hostApi.InvokeOnMainThread(() =>
            {
                history.Insert(0, new DownloadRecord
                {
                    Title = task.Title,
                    Size = task.Total,
                    Outcome = outcome,
                    Message = task.Message,
                    FinishedAt = DateTimeOffset.Now,
                });
                while (history.Count > MaxHistoryCount)
                    history.RemoveAt(history.Count - 1);
            });
            Plugin.SaveDataNow();
        }
        catch
        {
            // 历史记录失败不影响主流程
        }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
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
