using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 接收 Shionlib 推送（potato-vn://install 深链）的核心服务。
/// 双通道轮询捕获协议激活：
/// 1) <see cref="AppInstance.GetActivatedEventArgs"/> —— 冷启动激活可靠（每次轮询取新对象，避免缓存旧COM对象）；
/// 2) <see cref="IPotatoVnApi.ActivationArgs"/> —— 宿主在激活回调里同步更新该属性，覆盖应用运行中的
///    协议重定向激活（此时 GetActivatedEventArgs 可能仍返回最初的 Launch 参数）。
/// 注意：不能订阅宿主进程静态事件（如 AppInstance.Activated），否则事件委托会锁定插件程序集，
/// 导致插件更新/卸载时 DLL 无法删除。
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
    private object? _lastHostArgs;
    private int _reportBudget = 30; // 单次插件会话的远程上报配额，避免异常循环刷屏
    private DateTime _lastReportTime = DateTime.MinValue;

    /// <summary>收到新推送请求时触发（已通过校验与去重）。</summary>
    public event Func<InstallRequest, Task>? RequestReceived;

    public PushService(IPotatoVnApi hostApi, Func<Exception?, string?, Task>? reportError = null)
    {
        _hostApi = hostApi;
        _reportError = reportError;
    }

    /// <summary>
    /// 启动轮询循环，捕获应用运行中的协议激活。
    /// 必须在插件 InitializeAsync 中调用。
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _hostApi.Log(InfoBarSeverity.Informational, "PotatoDownload: PushService started");
    }

    /// <summary>
    /// 停止轮询并等待后台任务完全结束。
    /// 必须等待任务完成，否则后台 Task 的委托仍持有插件程序集，插件更新/卸载时 DLL 无法删除。
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
        _hostApi.Log(InfoBarSeverity.Informational, "PotatoDownload: PushService stopped");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 通道1：每次轮询获取当前有效的激活参数（缓存旧引用访问会抛 COMException）
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (args is not null)
                    HandleActivation(args, "sdk");
            }
            catch (Exception e)
            {
                _hostApi.Log(InfoBarSeverity.Warning,
                    $"PotatoDownload: poll GetActivatedEventArgs failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
                ReportThrottled(e, "PotatoDownload: poll GetActivatedEventArgs failed");
            }
            try
            {
                // 通道2：宿主记录的最近激活参数。宿主在激活回调里同步替换该属性；
                // 运行中的协议重定向激活可能只能从这条通道观察到。
                // 注意：该对象可能在激活回调结束后失效（COM 代理），必须检测到变化后立即读取。
                var hostArgs = _hostApi.ActivationArgs;
                if (hostArgs is not null && !ReferenceEquals(hostArgs, _lastHostArgs))
                {
                    _lastHostArgs = hostArgs;
                    if (hostArgs is AppActivationArguments appArgs)
                        HandleActivation(appArgs, "host");
                }
            }
            catch (Exception e)
            {
                _hostApi.Log(InfoBarSeverity.Warning,
                    $"PotatoDownload: read host ActivationArgs failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
                ReportThrottled(e, "PotatoDownload: read host ActivationArgs failed");
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

    private void HandleActivation(AppActivationArguments args, string source)
    {
        try
        {
            // 注意：Log 的 Informational 级别会被宿主的开发者模式开关过滤；
            // 与 potato-vn 推送直接相关的观测一律用 Warning（始终写入 log.txt）。
            _hostApi.Log(InfoBarSeverity.Informational,
                $"PotatoDownload: activation kind={args.Kind} via {source}");

            Uri? uri = null;

            switch (args.Kind)
            {
                case ExtendedActivationKind.Protocol:
                    if (args.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
                        uri = protocolArgs.Uri;
                    break;
                case ExtendedActivationKind.Launch:
                    // MSI/侧载版通过命令行参数传递协议 URL（manifest: Parameters="/p &quot;%1&quot;"）
                    if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
                        uri = ExtractUriFromLaunchArguments(launchArgs.Arguments);
                    break;
            }

            if (uri is null)
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: activation has no potato-vn URI (via {source})");
                return;
            }
            if (!string.Equals(uri.Scheme, InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: activation scheme={uri.Scheme} (not potato-vn)");
                return;
            }

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
                $"PotatoDownload: failed to handle activation: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            _hostApi.DeveloperEvent(e: e, msg: "PotatoDownload: failed to handle activation");
            ReportThrottled(e, "PotatoDownload: failed to handle activation");
        }
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
