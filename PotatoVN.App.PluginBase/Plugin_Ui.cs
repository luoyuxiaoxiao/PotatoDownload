using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageLeftPanel, IGalgamePageRightPanel
{
    private bool _uiInit;

    // ===== 对话框串行协调器 =====
    // ContentDialog 同一时刻只能显示一个；推送确认框/下载面板/测试面板可能同时被触发，
    // 全部经此队列串行弹出，避免 ShowAsync 抛异常导致请求被静默吞掉。
    private readonly Queue<Func<Task>> _dialogQueue = new();
    private bool _dialogShowing;

    private void EnqueueDialog(Func<Task> showAsync)
    {
        _dialogQueue.Enqueue(showAsync);
        _hostApi.InvokeOnMainThread(() => _ = PumpDialogQueueAsync());
    }

    private async Task PumpDialogQueueAsync()
    {
        if (_dialogShowing) return;
        _dialogShowing = true;
        try
        {
            while (_dialogQueue.Count > 0)
            {
                var showAsync = _dialogQueue.Dequeue();
                try
                {
                    await showAsync();
                }
                catch (Exception e)
                {
                    _ = DevReportInfo(e, "EnqueueDialog show failed");
                }
            }
        }
        finally
        {
            _dialogShowing = false;
        }
    }
    
    private void InitUi()
    {
        if (_uiInit) return;
        UpdateDownloadSidebarState(0);
        _uiInit = true;
    }

    /// <summary>
    /// 刷新侧边栏「下载」按钮的外观：无活动任务时显示静态下载图标，
    /// 有活动任务时切换为同步图标并显示任务数（对齐 Chrome 下载按钮的状态变化）。
    /// 通过 Unregister+Register 替换实现（宿主 API 没有原地更新按钮的接口）。
    /// </summary>
    internal void UpdateDownloadSidebarState(int activeCount)
    {
        _hostApi.InvokeOnMainThread(() =>
        {
            try
            {
                _hostApi.UnregisterSidebarButton("potato-download");
                _hostApi.RegisterSidebarButton(new SidebarButtonInfo
                {
                    Id = "potato-download",
                    Text = activeCount > 0 ? $"下载中 ({activeCount})" : "下载",
                    Placement = SidebarButtonPlacement.Menu,
                    FluentGlyph = activeCount > 0 ? "&#xE895;" : "&#xE896;",
                }, () =>
                {
                    ShowDownloadDialog();
                    return Task.CompletedTask;
                });
            }
            catch (System.Exception e)
            {
                _ = DevReportInfo(e, "UpdateDownloadSidebarState failed");
            }
        });
    }

    private bool _downloadDialogOpen;

    // ===== 完成后关机（会话级状态，重启自动复位）=====
    // DownloadManager 挂起（ShutdownArmed）后，全部活动任务结束 → BeginShutdownCountdown 进入 10 秒倒计时；
    // 倒计时显示在下载面板标题栏（固定位置），取消勾选「关机」即中止；期间新推送到达则中止并重新挂起。
    private const int ShutdownCountdownSeconds = 10;
    private static DispatcherTimer? _shutdownTimer;
    private static DateTimeOffset _shutdownDeadline;

    /// <summary>关机倒计时剩余时间；不在倒计时中为 null。下载面板按自己的轮询节奏读取。</summary>
    internal static TimeSpan? ShutdownCountdownRemaining =>
        _shutdownTimer is null ? null : _shutdownDeadline - DateTimeOffset.Now;

    /// <summary>触发本次倒计时的批次里新增的失败任务数（用于倒计时提示文案）。</summary>
    internal static int ShutdownCountdownFailedCount { get; private set; }

    /// <summary>全部活动任务结束后开始关机倒计时（主线程，由 DownloadManager.ShutdownCountdownRequested 触发）。</summary>
    private void BeginShutdownCountdown(int failedCount)
    {
        CancelShutdownCountdown();
        ShutdownCountdownFailedCount = failedCount;
        _shutdownDeadline = DateTimeOffset.Now.AddSeconds(ShutdownCountdownSeconds);
        _shutdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _shutdownTimer.Tick += (_, _) =>
        {
            if (DateTimeOffset.Now < _shutdownDeadline) return;
            CancelShutdownCountdown();
            ExecuteShutdown();
        };
        _shutdownTimer.Start();
        // Warning 级始终落日志（Success/Informational 会被 DevelopmentMode 过滤）：关机提示必须可见、可查
        _hostApi.Info(InfoBarSeverity.Warning, "PotatoDownload",
            failedCount > 0
                ? $"下载结束（{failedCount} 个任务失败），{ShutdownCountdownSeconds} 秒后关机，可在下载面板取消"
                : $"下载完成，{ShutdownCountdownSeconds} 秒后关机，可在下载面板取消",
            (ShutdownCountdownSeconds + 2) * 1000);
        ShowDownloadDialog(); // 倒计时与取消入口在面板标题栏；已打开时不重复弹出
    }

    /// <summary>中止关机倒计时（取消勾选「关机」/倒计时期间新任务到达/插件卸载）。</summary>
    internal static void CancelShutdownCountdown()
    {
        _shutdownTimer?.Stop();
        _shutdownTimer = null;
    }

    /// <summary>执行系统关机：先落盘插件数据，再走系统 shutdown.exe（无需特权，也留给宿主正常的退出流程）。</summary>
    private void ExecuteShutdown()
    {
        try
        {
            SaveDataNow();
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception e)
        {
            _ = DevReportInfo(e, "ExecuteShutdown failed");
            _hostApi.Event(InfoBarSeverity.Error, "PotatoDownload", e, "自动关机失败");
        }
    }

    /// <summary>弹出下载进度弹窗（模态，经串行协调器；已打开时不重复弹出）。</summary>
    private void ShowDownloadDialog()
    {
        _hostApi.InvokeOnMainThread(() =>
        {
            if (_downloadDialogOpen) return;
            var window = _hostApi.GetMainWindow();
            if (window is null) return;
            _downloadDialogOpen = true;
            EnqueueDialog(async () =>
            {
                var content = new DownloadProgressDialog();
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = window.Content.XamlRoot,
                        Title = content.DialogHeader,
                        Content = content,
                        CloseButtonText = "关闭",
                        DefaultButton = ContentDialogButton.Close,
                    };
                    await dialog.ShowAsync();
                }
                finally
                {
                    // 面板的刷新计时器与集合订阅在这里确定性释放：不依赖 Unloaded（关闭未必触发、打开时可能虚发）
                    content.Detach();
                    _downloadDialogOpen = false;
                }
            });
        });
    }

    public FrameworkElement CreateSettingUi()
    {
        try
        {
            return new UserControl1(_data);
        }
        catch (System.Exception e)
        {
            _ = DevReportInfo(e, "CreateSettingUi failed");
            return new TextBlock { Text = $"PotatoDownload 设置界面加载失败: {e.Message}" };
        }
    }
    
    public async Task<FrameworkElement> CreateLeftPanelUiAsync(Galgame game)
    {
        await Task.CompletedTask;
        return new TextBlock { Text = $"这是左侧面板，当前游戏：{game.Name.Value}" };
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        return Task.FromResult<FrameworkElement>(new TextBlock { Text = $"这是右侧面板，当前游戏：{game.Name.Value}" });
    }
}