using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    Cancelled,
    Paused,
}

/// <summary>用户对任务的请求类型：取消（删文件）或暂停（保现场可继续）。</summary>
public enum CancelRequestKind
{
    None,
    Cancel,
    Pause,
}

/// <summary>
/// 单个下载任务的状态。下载面板用计时器轮询读取（不走 PropertyChanged 事件链路），
/// 因此这里只是线程安全可读的普通属性：Stage/Message 由处理线程写入，Received 由下载线程写入。
/// </summary>
public class DownloadTask
{
    public InstallRequest Request { get; }
    public string Title => Request.Title;

    /// <summary>用户取消/暂停令牌：入队时与插件级 Shutdown 令牌链接。
    /// 取消会删除 .part/水位与未完成目录；暂停保留现场可继续；插件停止（Shutdown）保留续传现场。</summary>
    public CancellationTokenSource Cts { get; private set; } = new();

    /// <summary>暂存压缩包与解压目录（取消时的清理依据）。</summary>
    internal string? PackPath { get; set; }
    internal string? GamePath { get; set; }

    public CancelRequestKind CancelRequest { get; private set; } = CancelRequestKind.None;

    public DownloadTaskStage Stage { get; internal set; } = DownloadTaskStage.Pending;
    public string Message { get; internal set; } = string.Empty;
    public long Total { get; }

    private long _received;

    /// <summary>已落盘字节数（下载线程每次读取后写入；分块并发时偶有微小回退）。Interlocked 保证 x86 上 64 位读写不撕裂。</summary>
    public long Received
    {
        get => Interlocked.Read(ref _received);
        internal set => Interlocked.Exchange(ref _received, value);
    }

    public DownloadTask(InstallRequest request)
    {
        Request = request;
        Total = (long)request.Size;
    }

    /// <summary>取消：无论此前是否请求过暂停，最终语义都是取消（删文件）。</summary>
    public void Cancel()
    {
        CancelRequest = CancelRequestKind.Cancel;
        Cts.Cancel();
    }

    /// <summary>暂停：已请求取消的任务不会被降级成暂停。</summary>
    public void Pause()
    {
        if (CancelRequest == CancelRequestKind.None) CancelRequest = CancelRequestKind.Pause;
        Cts.Cancel();
    }

    /// <summary>从暂停恢复：换新令牌（旧的已触发不可复用），清请求标记。</summary>
    internal void ResetForResume()
    {
        CancelRequest = CancelRequestKind.None;
        Cts = new CancellationTokenSource();
    }

    /// <summary>进度百分比 0-100。</summary>
    public double ProgressPercent => Total <= 0 ? 0 : Math.Min(100, (double)Received / Total * 100);

    public bool IsActive => Stage is DownloadTaskStage.Pending or DownloadTaskStage.Downloading
        or DownloadTaskStage.Verifying or DownloadTaskStage.Unpacking or DownloadTaskStage.Importing;

    /// <summary>是否应在下载面板列出（并参与同资源去重）：活动任务 + 已暂停（可继续/可取消）。</summary>
    public bool IsListed => IsActive || Stage == DownloadTaskStage.Paused;
}

/// <summary>
/// 下载任务管理器：维护任务队列、串行执行下载→校验→解压→入库流程，记录历史并广播状态。
/// </summary>
public class DownloadManager
{
    private const int MaxHistoryCount = 50;

    private readonly IPotatoVnApi _hostApi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _finishLock = new();
    private int _lastActiveCount;

    /// <summary>任务列表，绑定到下载面板：增删与遍历一律在主线程进行，避免与面板重建并发修改。</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>下载服务工厂（CoreChecks 注入不做 SSRF 检查的传输层，跑本地模拟服务器）。</summary>
    internal Func<DownloadService> ServiceFactory { get; init; } = () => new DownloadService();

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
    /// 收到新推送时入队并开始处理。同一资源已有活动/暂停任务时忽略；
    /// 任务结束后（含失败/取消）再次推送即可重试。
    /// 入口一律落到线程池：调用方可能在 UI 线程上（确认框回调），若放任后续 await 捕获
    /// UI 同步上下文，整个下载管线的续体都会被泵进 Dispatcher，UI 会被下载循环饿死（界面冻结）。
    /// </summary>
    public Task EnqueueAsync(InstallRequest request) => Task.Run(async () =>
    {
        var task = await AddTaskAsync(request);
        if (task is not null) await RunAsync(task);
    });

    /// <summary>从暂停继续（对标 Chrome 下载的继续）：复用同一任务对象，下载侧凭 .part+水位自动续传。只在主线程调用（按钮回调）。</summary>
    public void ResumeTask(DownloadTask task)
    {
        if (task.Stage != DownloadTaskStage.Paused) return;
        task.ResetForResume();
        task.Message = "等待中";
        task.Stage = DownloadTaskStage.Pending;
        BumpActiveCount();
        _ = Task.Run(() => RunAsync(task));
    }

    /// <summary>取消任务（进行中或已暂停）：进行中由管线在取消点收尾并清理；已暂停时没有管线在跑，这里直接收尾。只在主线程调用（按钮回调）。</summary>
    public void CancelTask(DownloadTask task)
    {
        task.Cancel();
        lock (_finishLock)
        {
            // 与管线的收尾互斥：暂停收尾若正在进行，等它把 Stage 写成 Paused 后再由这里接手清理
            if (task.Stage != DownloadTaskStage.Paused) return;
            FinishInterrupted(task);
        }
        BumpActiveCount();
    }

    /// <summary>等待串行闸门后执行处理流程；等待期间被取消/暂停/停止直接收尾。</summary>
    private async Task RunAsync(DownloadTask task)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, task.Cts.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            FinishInterrupted(task);
            _hostApi.InvokeOnMainThread(BumpActiveCount);
            return;
        }
        try
        {
            await ProcessAsync(task, linked.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同一资源是否已有活动或暂停的任务；遍历 Tasks，只能在主线程调用。</summary>
    public bool HasActiveTask(string deduplicationKey)
    {
        foreach (var task in Tasks)
            if (task.IsListed && task.Request.DeduplicationKey == deduplicationKey) return true;
        return false;
    }

    /// <summary>
    /// 任务被打断后的收尾：插件停止保留现场且不记历史；暂停保留现场等待继续；
    /// 用户取消 = 放弃这个文件：删暂存包/.part/水位与未完成目录，写入历史。
    /// 决策与 Stage 写入在 <see cref="_finishLock"/> 内，与 <see cref="CancelTask"/> 互斥；Stage 最后写，观察到终态即表示收尾完成。
    /// </summary>
    private void FinishInterrupted(DownloadTask task)
    {
        lock (_finishLock)
        {
            if (_shutdown.IsCancellationRequested)
            {
                task.Message = "插件已停止";
                task.Stage = DownloadTaskStage.Failed;
                return;
            }
            if (task.CancelRequest == CancelRequestKind.Pause)
            {
                task.Message = "已暂停";
                task.Stage = DownloadTaskStage.Paused;
                return;
            }
            CleanupCancelledArtifacts(task);
            task.Message = "已取消";
            task.Stage = DownloadTaskStage.Cancelled;
        }
        RecordHistory(task, DownloadRecord.OutcomeCancelled);
    }

    /// <summary>
    /// 用户主动取消后的清理：删除暂存压缩包/.part/水位，以及带未完成标记的自建游戏目录
    /// （没有标记的目录不是本插件创建/已完成的，绝不动）。尽力而为，残留无碍。
    /// </summary>
    private void CleanupCancelledArtifacts(DownloadTask task)
    {
        try
        {
            if (task.GamePath is { } gamePath && Directory.Exists(gamePath)
                && File.Exists(Path.Combine(gamePath, UnpackService.IncompleteMarker)))
            {
                Directory.Delete(gamePath, true);
            }
            if (task.PackPath is { } packPath)
            {
                TryDeleteFile(packPath);
                TryDeleteFile(packPath + ".part");
                TryDeleteFile(packPath + ".part.watermark");
            }
        }
        catch (Exception e)
        {
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: cleanup after cancel failed: {e.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 单个文件删不掉（占用等）不影响其余清理
        }
    }

    private Task<DownloadTask?> AddTaskAsync(InstallRequest request)
    {
        var added = new TaskCompletionSource<DownloadTask?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostApi.InvokeOnMainThread(() =>
        {
            try
            {
                if (HasActiveTask(request.DeduplicationKey))
                {
                    _hostApi.Log(InfoBarSeverity.Warning,
                        $"PotatoDownload: duplicate push ignored, task already active ({request.Title})");
                    added.TrySetResult(null);
                    return;
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

    private async Task ProcessAsync(DownloadTask task, CancellationToken ct)
    {
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
            task.PackPath = packPath;

            // 2. 下载（多线程分块 + 断点续传）。每次网络读取都更新 Received；速度由面板按采样周期计算。
            //    心跳日志（Warning 级始终落 log.txt）：排查「面板不动」时分辨下载侧停滞与显示侧冻结
            task.Message = "下载中...";
            task.Stage = DownloadTaskStage.Downloading;
            using var heartbeat = new Timer(_ => _hostApi.Log(InfoBarSeverity.Warning,
                    $"PotatoDownload: heartbeat ({task.Title}): {task.Stage} {FormatBytes(task.Received)}/{FormatBytes(task.Total)}"),
                null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            using (var download = ServiceFactory())
            {
                await download.DownloadAsync(task.Request, packPath,
                    (received, _) => task.Received = received, ct,
                    msg => _hostApi.Log(InfoBarSeverity.Warning, $"PotatoDownload: {msg}"));
            }

            // 3. 校验：失败的文件必须删掉，否则下次会被当作已下载完成直接复用，永远校验失败
            task.Message = "校验中...";
            task.Stage = DownloadTaskStage.Verifying;
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
            task.Message = "解压中...";
            task.Stage = DownloadTaskStage.Unpacking;
            var gameDirName = UnpackService.ResolveGameDirectoryName(task.Request, packPath);
            var gamePath = UnpackService.PrepareGameDirectory(downloadDir, gameDirName);
            task.GamePath = gamePath;
            await UnpackService.UnpackAsync(task.Request, packPath, gamePath,
                (finished, total) => task.Message = total > 0 ? $"解压中 {finished}/{total}" : $"解压中 {finished} 个文件",
                ct);

            // 5. 入库 + 刮削：占位游戏到这里才创建，下载失败的任务不会在库里留下空条目
            task.Message = "入库刮削中...";
            task.Stage = DownloadTaskStage.Importing;
            var library = new LibraryService(_hostApi);
            await library.EnsurePlaceholderAsync(task.Request);
            await library.AddInstallationAsync(task.Request, gamePath);
            UnpackService.MarkComplete(gamePath);

            // 6. 清理压缩包
            File.Delete(packPath);

            task.Message = "完成";
            task.Stage = DownloadTaskStage.Completed;
            RecordHistory(task, DownloadRecord.OutcomeCompleted);
            _hostApi.Info(InfoBarSeverity.Success, "PotatoDownload", $"完成: {task.Title}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FinishInterrupted(task);
        }
        catch (Exception e)
        {
            task.Message = e.Message;
            task.Stage = DownloadTaskStage.Failed;
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
