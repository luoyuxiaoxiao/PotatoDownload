using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 接收 Shionlib 推送（potato-vn://install 深链）的核心服务。
///
/// 激活捕获策略（2026-09 实测定稿）：
/// - 热激活（应用运行/托盘时点深链）：订阅 <see cref="AppInstance.Activated"/>。
///   激活参数的 COM 代理在激活回调结束后立即失效（读取成员即抛 0x800706BA 一类错误），
///   轮询永远来不及读——必须在事件回调里同步把 URI 值取出来，绝不把 args 存到字段。
/// - 冷启动激活：首次激活不会触发 Activated 事件（WinAppSDK 语义，事件只覆盖重定向激活），
///   由后台轮询 <see cref="AppInstance.GetActivatedEventArgs"/> 兜底（初始激活参数长期有效）。
///
/// 程序集卸载安全：静态事件订阅会强引用插件委托，因此 <see cref="StopAsync"/> 必须
/// 退订并强制 GC 释放 WinRT CCW，否则插件更新/卸载时 DLL 删除失败。
/// </summary>
public class PushService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportCooldown = TimeSpan.FromSeconds(30);

    private readonly IPotatoVnApi _hostApi;
    private readonly Func<Exception?, string?, Task>? _reportError;
    private readonly ConcurrentDictionary<string, DateTime> _seenUris = new();
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _eventSubscribed;
    private object? _lastPollArgs; // 上次轮询见到的激活参数，引用去重避免对失效对象反复读取
    private int _reportBudget = 30; // 单次插件会话的远程上报配额，避免异常循环刷屏
    private DateTime _lastReportTime = DateTime.MinValue;

    /// <summary>收到新推送请求时触发（已通过校验与去重）。</summary>
    public event Func<InstallRequest, Task>? RequestReceived;

    public PushService(IPotatoVnApi hostApi, Func<Exception?, string?, Task>? reportError = null)
    {
        _hostApi = hostApi;
        _reportError = reportError;
    }

    /// <summary>启动激活捕获（事件订阅 + 冷启动轮询兜底）。必须在插件 InitializeAsync 中调用。</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        try
        {
            AppInstance.GetCurrent().Activated += OnAppInstanceActivated;
            _eventSubscribed = true;
        }
        catch (Exception e)
        {
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: subscribe AppInstance.Activated failed: {e.Message}");
            ReportThrottled(e, "PotatoDownload: subscribe AppInstance.Activated failed");
        }
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _hostApi.Log(InfoBarSeverity.Informational, "PotatoDownload: PushService started");
    }

    /// <summary>
    /// 停止捕获并等待后台任务完全结束，退订静态事件并强制 GC 释放 WinRT 引用。
    /// 不做这些会导致插件程序集被锁定，更新/卸载时 DLL 删除失败。
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try
        {
            if (_pollTask is not null) await _pollTask;
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
        _cts = null;
        _pollTask = null;
        RequestReceived = null; // 清空事件委托，确保不残留对插件方法的引用

        if (_eventSubscribed)
        {
            _eventSubscribed = false;
            try
            {
                AppInstance.GetCurrent().Activated -= OnAppInstanceActivated;
            }
            catch
            {
                // ignore
            }
            // WinRT 事件退订后，本机侧可能仍缓存着委托的 CCW，强制 GC 将其释放
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true);
        }
        _hostApi.Log(InfoBarSeverity.Informational, "PotatoDownload: PushService stopped");
    }

    /// <summary>
    /// 热激活事件回调：回调期间激活参数有效，必须同步把 URI 值取出。
    /// 只允许把 Uri（托管不可变值）带出去，绝不保留 args 或其成员对象。
    /// </summary>
    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        try
        {
            var uri = ExtractUri(args);
            if (uri is null) return;
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: activation event captured: {uri}");
            HandleUri(uri, "event");
        }
        catch (Exception e)
        {
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: activation event failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            ReportThrottled(e, "PotatoDownload: activation event failed");
        }
    }

    /// <summary>冷启动兜底轮询：初始激活参数长期有效；同一对象只尝试读取一次。</summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (args is not null && !ReferenceEquals(args, _lastPollArgs))
                {
                    _lastPollArgs = args;
                    HandleActivation(args, "sdk");
                }
            }
            catch (Exception e)
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: poll GetActivatedEventArgs failed: {e.GetType().Name} 0x{e.HResult:X8}");
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>轮询通道的激活处理：失效 COM 代理是预期现象（热激活由事件通道负责）。</summary>
    private void HandleActivation(AppActivationArguments args, string source)
    {
        Uri? uri;
        try
        {
            uri = ExtractUri(args);
        }
        catch (COMException e)
        {
            // 热激活过后 GetActivatedEventArgs 返回的对象已失效，读取成员必抛——
            // 该次激活已由事件通道处理，此处静默跳过即可。
            _hostApi.Log(InfoBarSeverity.Informational,
                $"PotatoDownload: stale activation args via {source} (0x{e.HResult:X8}), expected for warm activations");
            return;
        }
        catch (Exception e)
        {
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: read activation via {source} failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            ReportThrottled(e, $"PotatoDownload: read activation via {source} failed");
            return;
        }
        if (uri is null)
        {
            _hostApi.Log(InfoBarSeverity.Informational,
                $"PotatoDownload: activation has no potato-vn URI (via {source})");
            return;
        }
        HandleUri(uri, source);
    }

    /// <summary>从激活参数中提取 URI（Protocol 直接取；Launch 解析 MSI/侧载的 /p 命令行参数）。</summary>
    private static Uri? ExtractUri(AppActivationArguments args)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.Protocol:
                if (args.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
                    return protocolArgs.Uri;
                return null;
            case ExtendedActivationKind.Launch:
                if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
                    return ExtractUriFromLaunchArguments(launchArgs.Arguments);
                return null;
            default:
                return null;
        }
    }

    /// <summary>URI 统一处理：scheme 过滤 → 去重 → 解析校验 → 触发 <see cref="RequestReceived"/>。</summary>
    private void HandleUri(Uri uri, string source)
    {
        try
        {
            if (!string.Equals(uri.Scheme, InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: activation scheme={uri.Scheme} (not potato-vn, via {source})");
                return;
            }

            // 注意：Log 的 Informational 级别会被宿主的开发者模式开关过滤；
            // 与 potato-vn 推送直接相关的观测一律用 Warning（始终写入 log.txt）。
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: potato-vn activation via {source}: {uri}");

            var uriString = uri.ToString();
            var now = DateTime.UtcNow;

            // 时间窗口去重：30 秒内同一 URI 只处理一次；窗口过后可再次触发（便于重复测试）
            if (_seenUris.TryGetValue(uriString, out var lastSeen) && now - lastSeen < DedupeWindow)
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: duplicate push ignored within window ({uriString[..Math.Min(80, uriString.Length)]}...)");
                return;
            }
            _seenUris[uriString] = now;

            var request = InstallRequest.Parse(uri);
            if (!_seenKeys.TryAdd(request.DeduplicationKey, 0))
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: duplicate push ignored ({request.Title})");
                return;
            }
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: push received ({request.Title})");
            _ = Task.Run(() => RequestReceived?.Invoke(request));
        }
        catch (InstallRequestException e)
        {
            _hostApi.Event(InfoBarSeverity.Error, "PotatoDownload",
                msg: $"无效的推送请求: {e.Message}");
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: invalid push rejected: {e.Message}");
            ReportThrottled(e, "PotatoDownload: invalid push rejected");
        }
        catch (Exception e)
        {
            _hostApi.Log(InfoBarSeverity.Warning,
                $"PotatoDownload: failed to handle push: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            _hostApi.DeveloperEvent(e: e, msg: "PotatoDownload: failed to handle push");
            ReportThrottled(e, "PotatoDownload: failed to handle push");
        }
    }

    /// <summary>限流远程上报：同一插件会话内最多上报若干条，且受冷却时间约束。</summary>
    private void ReportThrottled(Exception? e, string msg)
    {
        if (_reportError is null) return;
        if (Interlocked.Decrement(ref _reportBudget) < 0) return;
        var now = DateTime.UtcNow;
        if (now - _lastReportTime < ReportCooldown) return;
        _lastReportTime = now;
        _ = Task.Run(() => _reportError(e, msg));
    }

    /// <summary>从启动命令行参数中提取协议 URL（形如 /p "potato-vn://install?..."）。</summary>
    private static Uri? ExtractUriFromLaunchArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var index = arguments.IndexOf(InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = arguments.IndexOf('"', index);
        var end = start >= 0 ? arguments.IndexOf('"', start + 1) : -1;
        var url = end > start
            ? arguments.Substring(start + 1, end - start - 1)
            : arguments[index..].Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }
}
