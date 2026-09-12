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
        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
           Id = "potato-download",
           Text = "下载",
           Placement = SidebarButtonPlacement.Menu, 
           FluentGlyph = "&#xE896;",
        }, () =>
        {
            ShowDownloadDialog();
            return Task.CompletedTask;
        });
        _uiInit = true;
    }

    /// <summary>弹出下载进度弹窗（模态）。</summary>
    private void ShowDownloadDialog()
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
                    Content = new DownloadProgressDialog(),
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Close,
                };
                _ = dialog.ShowAsync();
            }
            catch (System.Exception e)
            {
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