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
    
    private void InitUi()
    {
        if (_uiInit) return;
        UpdateDownloadSidebarState(0);
        // 开发期测试入口：发布应用市场前必须移除
        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
           Id = "potato-download-test-push",
           Text = "推送测试",
           Placement = SidebarButtonPlacement.Menu,
           FluentGlyph = "&#xE71B;",
        }, () =>
        {
            ShowTestPushDialog();
            return Task.CompletedTask;
        });
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

    /// <summary>弹出推送测试面板（模态）。</summary>
    private void ShowTestPushDialog()
    {
        _hostApi.InvokeOnMainThread(() =>
        {
            try
            {
                var window = _hostApi.GetMainWindow();
                if (window is null) return;
                var dialog = new ContentDialog
                {
                    XamlRoot = window.Content.XamlRoot,
                    Title = "PotatoDownload 推送测试",
                    Content = new TestPushDialog(DevReportInfo),
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Close,
                };
                _ = dialog.ShowAsync();
            }
            catch (System.Exception e)
            {
                _ = DevReportInfo(e, "ShowTestPushDialog failed");
            }
        });
    }

    private bool _downloadDialogOpen;

    /// <summary>弹出下载进度弹窗（模态；已打开时不重复弹出）。</summary>
    private void ShowDownloadDialog()
    {
        _hostApi.InvokeOnMainThread(() =>
        {
            try
            {
                if (_downloadDialogOpen) return;
                var window = _hostApi.GetMainWindow();
                if (window is null) return;
                _downloadDialogOpen = true;
                var dialog = new ContentDialog
                {
                    XamlRoot = window.Content.XamlRoot,
                    Title = "下载",
                    Content = new DownloadProgressDialog(),
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Close,
                };
                dialog.Closed += (_, _) => _downloadDialogOpen = false;
                _ = dialog.ShowAsync();
            }
            catch (System.Exception e)
            {
                _downloadDialogOpen = false;
                _ = DevReportInfo(e, "ShowDownloadDialog failed");
            }
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