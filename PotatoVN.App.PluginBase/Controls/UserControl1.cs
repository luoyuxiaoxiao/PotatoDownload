using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls
{
    /// <summary>
    /// 设置页：下载目录（含浏览按钮）+ 自动解压开关 + 自动下载开关。
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

            var pathBox = new TextBox
            {
                Text = _data.DownloadPath,
                PlaceholderText = @"留空则使用 C:\Galgame",
                Width = 280,
            };
            pathBox.LostFocus += (_, _) => _data.DownloadPath = pathBox.Text;
            var browseButton = new Button
            {
                Content = new FontIcon { Glyph = ((char)0xE8B7).ToString(), FontSize = 13 }, // 0xE8B7 = 文件夹图标
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(browseButton,
                "Settings_BrowseFolder".GetLoc("浏览…"));
            browseButton.Click += (_, _) => BrowseForFolder(pathBox);
            var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
            pathRow.Children.Add(pathBox);
            pathRow.Children.Add(browseButton);
            root.Children.Add(CreatePanel(new StdSetting("下载目录",
                "解压后的游戏存放位置；留空则在系统盘创建 Galgame 文件夹使用", pathRow)));

            var unpackSwitch = new ToggleSwitch { IsOn = _data.AutoUnpack };
            unpackSwitch.Toggled += (_, _) => _data.AutoUnpack = unpackSwitch.IsOn;
            root.Children.Add(CreatePanel(new StdSetting(
                "Settings_AutoUnpack_Title".GetLoc("下载后自动解压"),
                "Settings_AutoUnpack_Description".GetLoc("开启：下载完成后自动解压并入库；关闭：只保留压缩包，由你手动解压"),
                unpackSwitch)));

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
                if (e.PropertyName == nameof(PluginData.AutoUnpack) && unpackSwitch.IsOn != _data.AutoUnpack)
                    unpackSwitch.IsOn = _data.AutoUnpack;
            });

            Content = root;
        }

        /// <summary>弹系统文件夹选择框改写下载目录。COM 对话框是模态阻塞调用，在 UI 线程（STA）直接调。</summary>
        private void BrowseForFolder(TextBox pathBox)
        {
            try
            {
                var window = Plugin.HostApi.GetMainWindow();
                if (window is null) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var current = pathBox.Text;
                var picked = FolderPickerDialog.PickFolder(hwnd,
                    "Settings_BrowseFolder".GetLoc("选择下载目录"),
                    Directory.Exists(current) ? current : null);
                if (picked is null) return;
                pathBox.Text = picked;
                _data.DownloadPath = picked; // 选中后 LostFocus 不一定触发，直接写数据（属性变更会自动保存）
            }
            catch (Exception e)
            {
                Plugin.HostApi.Log(InfoBarSeverity.Warning,
                    $"PotatoDownload: folder picker failed: {e.Message}");
            }
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
