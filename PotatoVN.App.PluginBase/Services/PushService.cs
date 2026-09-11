using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 接收 Shionlib 推送（potato-vn://install 深链）的核心服务。
/// 负责订阅应用激活事件、解析并校验 InstallRequest、去重后交给下载流程。
/// </summary>
public class PushService
{
    private readonly IPotatoVnApi _hostApi;
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new();
    private bool _subscribed;

    /// <summary>收到新推送请求时触发（已通过校验与去重）。</summary>
    public event Func<InstallRequest, Task>? RequestReceived;

    public PushService(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>
    /// 订阅应用激活事件并处理启动时已有的激活参数。
    /// 必须在插件 InitializeAsync 中调用。
    /// </summary>
    public void Start()
    {
        if (_subscribed) return;
        _subscribed = true;

        AppInstance.GetCurrent().Activated += OnActivated;

        // 处理应用启动时携带的激活参数（例如 PotatoVN 尚未运行时用户点击了推送链接）
        if (_hostApi.ActivationArgs is AppActivationArguments args)
            HandleActivation(args);
    }

    public void Stop()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AppInstance.GetCurrent().Activated -= OnActivated;
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        HandleActivation(args);
    }

    private void HandleActivation(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Protocol) return;
        if (args.Data is not Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
            return;

        var uri = protocolArgs.Uri;
        if (!string.Equals(uri.Scheme, InstallRequest.Scheme, StringComparison.OrdinalIgnoreCase))
            return;

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
