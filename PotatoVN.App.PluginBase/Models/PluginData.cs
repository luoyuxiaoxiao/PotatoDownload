using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据类示例
///
/// 其中，[ObservableProperty] 特性是用来给UI绑定的，如果你的某个数据需要在UI上实时更新（即代码里修改变量会实时反馈到UI上，反之也成立）
/// 对于不需要反应到UI上的数据，可以直接使用普通的属性。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    /// <summary>下载目录（解压后的游戏存放位置）。为空时使用插件目录下的 downloads。</summary>
    [ObservableProperty] private string _downloadPath = string.Empty;

    /// <summary>收到推送后是否自动开始下载（关闭时仅通知，不自动处理）。</summary>
    [ObservableProperty] private bool _autoDownload = true;
}