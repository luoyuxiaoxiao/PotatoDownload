using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;
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
/// 下载任务管理器：维护任务队列、串行执行下载→校验→解压→入库流程，记录历史并广播状态。
/// </summary>
public class DownloadManager
{
    private const int MaxHistoryCount = 50;
    private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPotatoVnApi _hostApi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _lastActiveCount;

    /// <summary>任务列表，绑定到下载面板：增删与遍历一律在主线程进行，避免与面板重建并发修改。</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>活动任务数变化时触发（用于侧边栏状态刷新），参数为当前活动任务数。</summary>
    public event Action<int>? ActiveTaskCountChanged;

    public DownloadManager(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>
    /// 插件停用/卸载时取消所有进行中的任务：否则后台下载会继续持有旧程序集与文件句柄，
    /// 更新或卸载时 DLL 删除失败。已下载的 .part 与水位保留，下次推送可续传。
    /// </summary>
    public void Shutdown() => _shutdown.Cancel();

    /// <summary>
    /// 收到新推送时入队并开始处理。同一资源已有活动任务时忽略；
    /// 任务结束后（含失败）再次推送即可重试。
    /// </summary>
    public async Task EnqueueAsync(InstallRequest request)
    {
        var task = await AddTaskAsync(request);
        if (task is null) return;
        try
        {
            await _gate.WaitAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            task.Stage = DownloadTaskStage.Failed;
            task.Message = "插件已停止";
            _hostApi.InvokeOnMainThread(BumpActiveCount);
            return;
        }
        try
        {
            await ProcessAsync(task);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<DownloadTask?> AddTaskAsync(InstallRequest request)
    {
        var added = new TaskCompletionSource<DownloadTask?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostApi.InvokeOnMainThread(() =>
        {
            try
            {
                foreach (var existing in Tasks)
                {
                    if (existing.IsActive && existing.Request.DeduplicationKey == request.DeduplicationKey)
                    {
                        _hostApi.Log(InfoBarSeverity.Warning,
                            $"PotatoDownload: duplicate push ignored, task already active ({request.Title})");
                        added.TrySetResult(null);
                        return;
                    }
                }
                var task = new DownloadTask(request);
                Tasks.Add(task);
                BumpActiveCount();
                added.TrySetResult(task);
            }
            catch (Exception e)
            {
                added.TrySetException(e);
            }
        });
        return added.Task;
    }

    /// <summary>遍历 Tasks，只能在主线程调用。</summary>
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
        var ct = _shutdown.Token;
        try
        {
            // 1. 下载目录：设置项，或系统盘 Galgame 文件夹（自动创建）。
            //    压缩包与 .part 放在专属子目录：下载目录里可能有用户自己的同名压缩包，绝不能覆盖或删除它
            var downloadDir = string.IsNullOrWhiteSpace(Plugin.DownloadPath)
                ? Plugin.DefaultDownloadPath
                : Plugin.DownloadPath;
            var stagingDir = Path.Combine(downloadDir, ".potatodownload");
            Directory.CreateDirectory(stagingDir);
            var packPath = Path.Combine(stagingDir, task.Request.FileName);

            // 2. 下载（多线程分块 + 断点续传），速度做 EMA 平滑，UI 更新限流 500ms
            task.Stage = DownloadTaskStage.Downloading;
            task.Message = "下载中...";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long lastTickBytes = 0, lastTickMs = 0;
            var lastUiMs = -UiUpdateInterval.TotalMilliseconds;
            double smoothedSpeed = 0;
            using (var download = new DownloadService())
            {
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
                    }, ct);
            }

            // 3. 校验：失败的文件必须删掉，否则下次会被当作已下载完成直接复用，永远校验失败
            task.SpeedBytesPerSec = 0;
            task.Stage = DownloadTaskStage.Verifying;
            task.Message = "校验中...";
            try
            {
                await DownloadService.VerifyChecksumAsync(task.Request, packPath, ct);
            }
            catch (DownloadException)
            {
                File.Delete(packPath);
                throw;
            }

            // 4. 解压：目录名来自压缩包内容，落在下载目录内；不删除任何不是本插件创建的目录
            task.Stage = DownloadTaskStage.Unpacking;
            task.Message = "解压中...";
            var gameDirName = UnpackService.ResolveGameDirectoryName(task.Request, packPath);
            var gamePath = UnpackService.PrepareGameDirectory(downloadDir, gameDirName);
            await UnpackService.UnpackAsync(task.Request, packPath, gamePath,
                (finished, total) => task.Message = total > 0 ? $"解压中 {finished}/{total}" : $"解压中 {finished} 个文件",
                ct);

            // 5. 入库 + 刮削：占位游戏到这里才创建，下载失败的任务不会在库里留下空条目
            task.Stage = DownloadTaskStage.Importing;
            task.Message = "入库刮削中...";
            var library = new LibraryService(_hostApi);
            await library.EnsurePlaceholderAsync(task.Request);
            await library.AddInstallationAsync(task.Request, gamePath);
            UnpackService.MarkComplete(gamePath);

            // 6. 清理压缩包
            File.Delete(packPath);

            task.Stage = DownloadTaskStage.Completed;
            task.Current = task.Total;
            task.Message = "完成";
            RecordHistory(task, DownloadRecord.OutcomeCompleted);
            _hostApi.Info(InfoBarSeverity.Success, "PotatoDownload", $"完成: {task.Title}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.Stage = DownloadTaskStage.Failed;
            task.Message = "插件已停止";
            task.SpeedBytesPerSec = 0;
        }
        catch (Exception e)
        {
            task.Stage = DownloadTaskStage.Failed;
            task.Message = e.Message;
            task.SpeedBytesPerSec = 0;
            RecordHistory(task, DownloadRecord.OutcomeFailed);
            _hostApi.Event(InfoBarSeverity.Error, "PotatoDownload", e, $"处理推送失败: {task.Title}");
        }
        finally
        {
            _hostApi.InvokeOnMainThread(BumpActiveCount);
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
                // 必须在插入之后、同一线程上保存：否则序列化可能早于插入执行，或与插入并发修改集合
                Plugin.SaveDataNow();
            });
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
