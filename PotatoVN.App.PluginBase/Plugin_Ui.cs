using System;
using System.Collections.Generic;
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
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = window.Content.XamlRoot,
                        Title = "下载",
                        Content = new DownloadProgressDialog(),
                        CloseButtonText = "关闭",
                        DefaultButton = ContentDialogButton.Close,
                    };
                    await dialog.ShowAsync();
                }
                finally
                {
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