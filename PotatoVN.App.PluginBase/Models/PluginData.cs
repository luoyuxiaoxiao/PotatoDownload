using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据（随宿主持久化）。
///
/// 注意：[ObservableProperty] 的属性是给UI绑定用的，任何属性的修改都会实时反应在UI上，
/// 对于不需要反应到UI上的数据，请直接使用普通属性。
/// History 集合内的增删不会触发 PropertyChanged，修改后需要手动调用 Plugin.SaveDataNow()。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    /// <summary>下载目录：解压后的游戏存放位置，为空时使用插件目录下的 downloads</summary>
    [ObservableProperty] private string _downloadPath = string.Empty;

    /// <summary>收到推送后是否自动开始下载；关闭时弹出确认框，用户点击「下载」后才开始</summary>
    [ObservableProperty] private bool _autoDownload = true;

    /// <summary>下载历史（最新在前，最多保留 50 条）</summary>
    public ObservableCollection<DownloadRecord> History { get; } = [];
}
