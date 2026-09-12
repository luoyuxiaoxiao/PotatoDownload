using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Services;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 下载进度弹窗内容：展示任务列表与每个任务的进度条/状态。
/// </summary>
public sealed partial class DownloadProgressDialog : UserControl
{
    public DownloadProgressDialog()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
        TaskList.ItemsSource = Plugin.DownloadManager.Tasks;
        Plugin.DownloadManager.Tasks.CollectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hasTasks = Plugin.DownloadManager.Tasks.Count > 0;
        TaskList.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasTasks ? Visibility.Collapsed : Visibility.Visible;
    }
}
