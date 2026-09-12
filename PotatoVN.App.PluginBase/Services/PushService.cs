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
/// 通过轮询 <see cref="IPotatoVnApi.ActivationArgs"/> 捕获协议激活（宿主每次激活都会更新该属性），
/// 解析并校验 InstallRequest、去重后交给下载流程。
/// 注意：不能订阅宿主进程静态事件（如 AppInstance.Activated），否则事件委托会锁定插件程序集，
/// 导致插件更新/卸载时 DLL 无法删除。
/// </summary>
public class PushService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IPotatoVnApi _hostApi;
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new();
    private CancellationTokenSource? _cts;
    private string? _lastProcessedUri;

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
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hostApi.ActivationArgs is AppActivationArguments args)
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
        if (args.Kind != ExtendedActivationKind.Protocol) return;
        if (args.Data is not Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
            return;

        var uri = protocolArgs.Uri;
        if (!string.Equals(uri.Scheme, InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase))
            return;

        // 同一 URI 只处理一次（宿主每次激活都会更新 ActivationArgs，轮询会重复读到）
        var uriString = uri.ToString();
        if (uriString == _lastProcessedUri) return;
        _lastProcessedUri = uriString;

        try
        {
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
}
