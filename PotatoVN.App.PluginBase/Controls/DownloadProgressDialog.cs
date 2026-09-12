using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Services;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 下载进度弹窗内容：展示任务列表与每个任务的进度条/状态。
/// 纯C#构建，不使用XAML（插件XAML依赖宿主v1.10.1+的XAML承载机制，旧宿主上直接XamlParseException）。
/// </summary>
public sealed class DownloadProgressDialog : UserControl
{
    private readonly StackPanel _listPanel;
    private readonly ScrollViewer _scrollViewer;
    private readonly TextBlock _emptyText;

    public DownloadProgressDialog()
    {
        _listPanel = new StackPanel { Spacing = 12 };
        _scrollViewer = new ScrollViewer
        {
            MaxHeight = 320,
            Content = _listPanel,
            Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _emptyText = new TextBlock
        {
            Text = "当前没有下载任务",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (PluginTheme.GetBrush("TextFillColorSecondaryBrush") is { } secondary)
            _emptyText.Foreground = secondary;

        var root = new StackPanel { Spacing = 12, MinHeight = 120 };
        root.Children.Add(_scrollViewer);
        root.Children.Add(_emptyText);
        MinWidth = 420;
        Content = root;

        Plugin.DownloadManager.Tasks.CollectionChanged += OnTasksChanged;
        Unloaded += (_, _) =>
        {
            Plugin.DownloadManager.Tasks.CollectionChanged -= OnTasksChanged;
            DetachRows();
        };
        RebuildTaskList();
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Plugin.HostApi.InvokeOnMainThread(RebuildTaskList);

    private void DetachRows()
    {
        foreach (var child in _listPanel.Children)
            if (child is TaskRow row) row.Detach();
    }

    private void RebuildTaskList()
    {
        DetachRows();
        _listPanel.Children.Clear();
        foreach (var task in Plugin.DownloadManager.Tasks)
            _listPanel.Children.Add(new TaskRow(task));

        var hasTasks = Plugin.DownloadManager.Tasks.Count > 0;
        _scrollViewer.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        _emptyText.Visibility = hasTasks ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>单个任务的行：标题 + 状态 + 进度条，跟随任务属性变化实时刷新。</summary>
    private sealed class TaskRow : StackPanel
    {
        private readonly DownloadTask _task;
        private readonly TextBlock _messageText;
        private readonly ProgressBar _progressBar;

        public TaskRow(DownloadTask task)
        {
            _task = task;
            Spacing = 6;
            Padding = new Thickness(0, 4, 0, 4);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = task.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
            });
            _messageText = new TextBlock();
            if (PluginTheme.GetBrush("TextFillColorSecondaryBrush") is { } secondary)
                _messageText.Foreground = secondary;
            Grid.SetColumn(_messageText, 1);
            header.Children.Add(_messageText);
            Children.Add(header);

            _progressBar = new ProgressBar { Height = 4, Maximum = 100 };
            Children.Add(_progressBar);

            Refresh();
            _task.PropertyChanged += OnTaskPropertyChanged;
        }

        public void Detach() => _task.PropertyChanged -= OnTaskPropertyChanged;

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
            Plugin.HostApi.InvokeOnMainThread(Refresh);

        private void Refresh()
        {
            _messageText.Text = _task.Message;
            _progressBar.Value = _task.ProgressPercent;
        }
    }
}
