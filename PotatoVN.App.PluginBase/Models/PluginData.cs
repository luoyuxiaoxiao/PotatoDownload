using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
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
    [ObservableProperty] private bool _autoDownload = false;

    /// <summary>下载完成后是否自动解压并入库；关闭时只把压缩包保留在下载目录，由用户自己解压</summary>
    [ObservableProperty] private bool _autoUnpack = true;

    /// <summary>
    /// 下载历史（最新在前，最多保留 50 条）。
    /// [JsonInclude]+private set 是必须的：STJ 默认不填充 get-only 集合（静默丢弃整个 History），
    /// 有 setter 才会在反序列化时替换实例。
    /// </summary>
    [JsonInclude]
    public ObservableCollection<DownloadRecord> History { get; private set; } = [];
}
