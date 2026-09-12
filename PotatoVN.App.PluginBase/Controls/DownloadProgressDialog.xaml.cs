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
    }
}
