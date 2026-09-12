using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Helper;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 推送测试面板：一键发送各类预设深链用例（与 TestPlatform 页面一致）。
/// 通过 ShellExecute 触发 potato-vn:// 协议，与在浏览器中点击测试链接完全等效。
/// 注意：发布应用市场前必须移除本功能。
/// </summary>
public sealed class TestPushDialog : UserControl
{
    private readonly Func<Exception?, string?, Task>? _reportError;

    public TestPushDialog(Func<Exception?, string?, Task>? reportError = null)
    {
        _reportError = reportError;

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = "点击任意用例，将通过系统深链协议发送给 PotatoVN（等效于浏览器点击测试链接）。请先在插件设置中开启「自动下载」。",
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var scenario in TestPush.Scenarios)
            root.Children.Add(CreateScenarioCard(scenario));

        root.Children.Add(new TextBlock
        {
            Text = "边界/反例用例预期就是失败——观察错误提示与「预期」一致即算通过。",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        Content = new ScrollViewer
        {
            Content = root,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private FrameworkElement CreateScenarioCard(TestPush.Scenario scenario)
    {
        var button = new Button { Content = "发送", VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => Fire(scenario.Name, scenario.BuildUrl());

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock { Text = scenario.Name, FontWeight = FontWeights.SemiBold });
        textStack.Children.Add(new TextBlock
        {
            Text = scenario.Description, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = "预期：" + scenario.Expect, FontSize = 12, Opacity = 0.55, TextWrapping = TextWrapping.Wrap,
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(textStack);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = grid,
        };
        if (PluginTheme.GetBrush("LayerFillColorDefaultBrush") is { } background)
            border.Background = background;
        if (PluginTheme.GetBrush("CardStrokeColorDefaultBrush") is { } stroke)
            border.BorderBrush = stroke;
        return border;
    }

    private void Fire(string name, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                "PotatoDownload", $"已发送测试推送: {name}");
        }
        catch (Exception e)
        {
            Plugin.HostApi.Event(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                "PotatoDownload", e, $"发送测试推送失败: {name}");
            if (_reportError is not null) _ = _reportError(e, $"TestPushDialog.Fire failed: {name}");
        }
    }
}
