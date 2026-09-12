using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls
{
    /// <summary>
    /// 设置页：下载目录 + 自动下载开关。
    /// 纯C#构建，不使用XAML（插件XAML依赖宿主v1.10.1+的XAML承载机制，旧宿主上直接XamlParseException）。
    /// </summary>
    public sealed class UserControl1 : UserControl
    {
        private readonly PluginData _data;

        public UserControl1(PluginData data)
        {
            _data = data;

            var root = new StdStackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = PluginTheme.GetThickness("SmallBottomMargin", new Thickness(0, 0, 0, 12)),
            };

            root.Children.Add(CreatePanel(new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(20),
                Children =
                {
                    new TextBlock { FontSize = 24, Text = "PotatoDownload" },
                    new TextBlock { Text = "从 shionlib 自动推送到 PotatoVN 进行下载、解压和刮削。" },
                },
            }));

            var pathBox = new TextBox
            {
                Text = _data.DownloadPath,
                PlaceholderText = @"例如 D:\Games",
                Width = 320,
            };
            pathBox.LostFocus += (_, _) => _data.DownloadPath = pathBox.Text;
            root.Children.Add(CreatePanel(new StdSetting("下载目录",
                "解压后的游戏存放位置，留空则使用插件目录下的 downloads", pathBox)));

            var autoSwitch = new ToggleSwitch { IsOn = _data.AutoDownload };
            autoSwitch.Toggled += (_, _) => _data.AutoDownload = autoSwitch.IsOn;
            root.Children.Add(CreatePanel(new StdSetting("自动下载",
                "开启：收到推送立即下载；关闭：弹出确认框，点击「下载」后才开始", autoSwitch)));

            //数据被其他入口修改时同步回UI（设置项也可能来自推送流程之外的修改）
            _data.PropertyChanged += (_, e) => Plugin.HostApi.InvokeOnMainThread(() =>
            {
                if (e.PropertyName == nameof(PluginData.DownloadPath) && pathBox.Text != _data.DownloadPath)
                    pathBox.Text = _data.DownloadPath;
                if (e.PropertyName == nameof(PluginData.AutoDownload) && autoSwitch.IsOn != _data.AutoDownload)
                    autoSwitch.IsOn = _data.AutoDownload;
            });

            Content = root;
        }

        private static Border CreatePanel(UIElement content)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 10, 50, 10),
                Child = content,
            };
            if (PluginTheme.GetBrush("LayerFillColorDefaultBrush") is { } background)
                border.Background = background;
            if (PluginTheme.GetBrush("CardStrokeColorDefaultBrush") is { } stroke)
                border.BorderBrush = stroke;
            return border;
        }
    }
}
