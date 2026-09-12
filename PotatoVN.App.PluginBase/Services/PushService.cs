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
/// 通过轮询 <see cref="AppInstance.GetActivatedEventArgs"/> 捕获协议激活（AppLifecycle 标准用法，
/// 每次调用返回当前有效的激活参数，不会因宿主替换引用而失效）。
/// 注意：不能订阅宿主进程静态事件（如 AppInstance.Activated），否则事件委托会锁定插件程序集，
/// 导致插件更新/卸载时 DLL 无法删除。
/// </summary>
public class PushService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

    private readonly IPotatoVnApi _hostApi;
    private readonly ConcurrentDictionary<string, DateTime> _seenUris = new();
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>收到新推送请求时触发（已通过校验与去重）。</summary>
    public event Func<InstallRequest, Task>? RequestReceived;

    public PushService(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
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
                // 每次轮询获取当前有效的激活参数（宿主替换旧引用后，旧对象访问会抛 COMException）
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (args is not null)
                    HandleActivation(args);
            }
            catch (Exception e)
            {
                _hostApi.DeveloperEvent(e: e, msg: "PotatoDownload: poll activation failed");
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

    private void HandleActivation(AppActivationArguments args)
    {
        try
        {
            _hostApi.Log(InfoBarSeverity.Informational,
                $"PotatoDownload: activation kind={args.Kind}");

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
                    "PotatoDownload: activation has no potato-vn URI");
                return;
            }
            if (!string.Equals(uri.Scheme, InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                _hostApi.Log(InfoBarSeverity.Informational,
                    $"PotatoDownload: activation scheme={uri.Scheme} (not potato-vn)");
                return;
            }

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
            _hostApi.Log(InfoBarSeverity.Informational,
                $"PotatoDownload: push received ({request.Title})");
            _ = Task.Run(() => RequestReceived?.Invoke(request));
        }
        catch (InstallRequestException e)
        {
            _hostApi.Event(InfoBarSeverity.Error, "PotatoDownload",
                msg: $"无效的推送请求: {e.Message}");
        }
        catch (Exception e)
        {
            _hostApi.DeveloperEvent(e: e, msg: "PotatoDownload: failed to handle activation");
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
