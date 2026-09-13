using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;
using PotatoVN.App.PluginBase.Services;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 下载面板（对齐 Chrome 下载页）：上方「进行中」实时任务（进度条 + 速度 + 完成度），
/// 下方「历史记录」（持久化的完成/失败记录，含时间与结果）。纯 C# 构建。
/// </summary>
public sealed class DownloadProgressDialog : UserControl
{
    private const string GlyphFont = "Segoe MDL2 Assets";
    private const int HistoryCollapsedCount = 5; // 历史记录默认展示条数，超出需点击展开

    private readonly StackPanel _activePanel;
    private readonly StackPanel _historyPanel;
    private readonly FrameworkElement _activeHeader;
    private readonly FrameworkElement _historyHeader;
    private readonly TextBlock _emptyText;
    private bool _historyExpanded;

    /// <summary>
    /// 进度轮询计时器：PropertyChanged→InvokeOnMainThread 链路在宿主环境被实证不可靠（面板冻结、
    /// 只在打开瞬间显示一次快照），改为在 UI 线程上每 500ms 直接读任务模型刷新——
    /// 只要弹窗能打开，这条路一定走。
    /// </summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public DownloadProgressDialog()
    {
        _activeHeader = CreateHeader("进行中");
        _activePanel = new StackPanel { Spacing = 10 };
        _historyHeader = CreateHistoryHeader();
        _historyPanel = new StackPanel { Spacing = 10 };
        _emptyText = new TextBlock
        {
            Text = "当前没有下载任务",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24),
        };
        if (PluginTheme.GetBrush("TextFillColorSecondaryBrush") is { } secondary)
            _emptyText.Foreground = secondary;

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(_activeHeader);
        root.Children.Add(_activePanel);
        root.Children.Add(_historyHeader);
        root.Children.Add(_historyPanel);
        root.Children.Add(_emptyText);

        MinWidth = 480;
        Content = new ScrollViewer
        {
            Content = root,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Plugin.DownloadManager.Tasks.CollectionChanged += OnCollectionChanged;
        Plugin.HistoryCollection.CollectionChanged += OnCollectionChanged;
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            Plugin.DownloadManager.Tasks.CollectionChanged -= OnCollectionChanged;
            Plugin.HistoryCollection.CollectionChanged -= OnCollectionChanged;
            DetachRows(_activePanel);
        };
        Rebuild();
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    private static TextBlock CreateHeader(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 0),
        Opacity = 0.8,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement CreateHistoryHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(CreateHeader("历史记录"));
        var clearButton = new Button
        {
            Content = "清空历史",
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        clearButton.Click += (_, _) =>
        {
            Plugin.HistoryCollection.Clear();
            Plugin.SaveDataNow();
            Rebuild();
        };
        Grid.SetColumn(clearButton, 1);
        grid.Children.Add(clearButton);
        return grid;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Plugin.HostApi.InvokeOnMainThread(Rebuild);

    /// <summary>每 500ms 在 UI 线程上直接读任务模型刷新行；任务数变化时顺手重建（移出已完成行）。</summary>
    private void OnRefreshTick(object? sender, object e)
    {
        if (!IsLoaded)
        {
            // ContentDialog 关闭后内容未必触发 Unloaded：自查失活即停，避免计时器泄漏
            _refreshTimer.Stop();
            return;
        }
        var active = 0;
        foreach (var task in Plugin.DownloadManager.Tasks)
            if (task.IsListed) active++;
        if (active != _activePanel.Children.Count)
        {
            Rebuild();
            return;
        }
        foreach (var child in _activePanel.Children)
            if (child is TaskRow row) row.Refresh();
    }

    private static void DetachRows(Panel panel)
    {
        foreach (var child in panel.Children)
            if (child is TaskRow row) row.Detach();
    }

    private void Rebuild()
    {
        DetachRows(_activePanel);
        _activePanel.Children.Clear();
        var activeCount = 0;
        foreach (var task in Plugin.DownloadManager.Tasks)
        {
            if (!task.IsListed) continue;
            _activePanel.Children.Add(new TaskRow(task));
            activeCount++;
        }
        _activeHeader.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        _historyPanel.Children.Clear();
        var history = Plugin.HistoryCollection;
        var shown = 0;
        foreach (var record in history)
        {
            if (!_historyExpanded && shown >= HistoryCollapsedCount) break;
            _historyPanel.Children.Add(CreateHistoryRow(record));
            shown++;
        }
        _historyHeader.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (history.Count > HistoryCollapsedCount)
        {
            var expandButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = _historyExpanded
                    ? $"收起 {(char)0x25B2}"
                    : $"展开全部（共 {history.Count} 条）{(char)0x25BC}",
            };
            expandButton.Click += (_, _) =>
            {
                _historyExpanded = !_historyExpanded;
                Rebuild();
            };
            _historyPanel.Children.Add(expandButton);
        }

        _emptyText.Visibility = activeCount + history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string StageText(DownloadTaskStage stage) => stage switch
    {
        DownloadTaskStage.Pending => "等待中",
        DownloadTaskStage.Downloading => "下载中",
        DownloadTaskStage.Verifying => "校验中",
        DownloadTaskStage.Unpacking => "解压中",
        DownloadTaskStage.Importing => "入库中",
        DownloadTaskStage.Completed => "已完成",
        DownloadTaskStage.Failed => "失败",
        DownloadTaskStage.Cancelled => "已取消",
        DownloadTaskStage.Paused => "已暂停",
        _ => stage.ToString(),
    };

    private static TextBlock Glyph(string glyph, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(GlyphFont),
        FontSize = 16,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static Border Card(Grid content)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = content,
        };
        if (PluginTheme.GetBrush("LayerFillColorDefaultBrush") is { } background)
            border.Background = background;
        if (PluginTheme.GetBrush("CardStrokeColorDefaultBrush") is { } stroke)
            border.BorderBrush = stroke;
        return border;
    }

    private static Grid TwoColumnGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static FrameworkElement CreateHistoryRow(DownloadRecord record)
    {
        var ok = record.Outcome == DownloadRecord.OutcomeCompleted;
        var cancelled = record.Outcome == DownloadRecord.OutcomeCancelled;
        var glyphBrush = PluginTheme.GetBrush(ok ? "SystemFillColorSuccessBrush"
            : cancelled ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");

        var grid = TwoColumnGrid();
        grid.Children.Add(Glyph(ok ? "" : "", glyphBrush));

        var textStack = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 10, 0) };
        Grid.SetColumn(textStack, 1);
        textStack.Children.Add(new TextBlock
        {
            Text = record.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = ok
                ? $"已完成 · {DownloadManager.FormatBytes(record.Size)}"
                : cancelled ? "已取消" : $"失败：{record.Message}",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(textStack);

        var timeText = new TextBlock
        {
            Text = record.FinishedAt.ToLocalTime().ToString("MM-dd HH:mm"),
            FontSize = 12,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(timeText, 2);
        grid.Children.Add(timeText);

        return Card(grid);
    }

    /// <summary>活动任务行：图标 + 标题/状态 + 进度条 + 速度/完成度。</summary>
    private sealed class TaskRow : UserControl
    {
        private readonly DownloadTask _task;
        private readonly TextBlock _rightText;
        private readonly TextBlock _messageText;
        private readonly ProgressBar _progressBar;
        private readonly Button _pauseButton;
        private readonly Button _resumeButton;
        private readonly Button _cancelButton;

        /// <summary>Chrome 下载行风格的小图标按钮。</summary>
        private static Button IconButton(Symbol symbol, string tooltip)
        {
            var button = new Button
            {
                Content = new SymbolIcon(symbol),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        public TaskRow(DownloadTask task)
        {
            _task = task;

            var grid = TwoColumnGrid();
            grid.Children.Add(Glyph(""));

            var centerStack = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 10, 0) };
            Grid.SetColumn(centerStack, 1);
            centerStack.Children.Add(new TextBlock
            {
                Text = task.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
            });
            _messageText = new TextBlock { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
            centerStack.Children.Add(_messageText);
            _progressBar = new ProgressBar { Height = 4, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
            centerStack.Children.Add(_progressBar);
            grid.Children.Add(centerStack);

            var rightStack = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _rightText = new TextBlock
            {
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
            };
            _pauseButton = IconButton(Symbol.Pause, "暂停");
            _resumeButton = IconButton(Symbol.Play, "继续");
            _cancelButton = IconButton(Symbol.Cancel, "取消");
            _pauseButton.Click += (_, _) => _task.Pause();
            _resumeButton.Click += (_, _) => Plugin.DownloadManager.ResumeTask(_task);
            _cancelButton.Click += (_, _) =>
            {
                if (_task.Stage == DownloadTaskStage.Paused)
                    Plugin.DownloadManager.CancelPausedTask(_task);
                else
                    _task.Cancel();
            };
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttonStack.Children.Add(_pauseButton);
            buttonStack.Children.Add(_resumeButton);
            buttonStack.Children.Add(_cancelButton);
            rightStack.Children.Add(_rightText);
            rightStack.Children.Add(buttonStack);
            Grid.SetColumn(rightStack, 2);
            grid.Children.Add(rightStack);

            Content = Card(grid);

            Refresh();
            _task.PropertyChanged += OnTaskPropertyChanged;
        }

        public void Detach() => _task.PropertyChanged -= OnTaskPropertyChanged;

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
            Plugin.HostApi.InvokeOnMainThread(Refresh);

        internal void Refresh()
        {
            _messageText.Text = _task.Message;
            _progressBar.Value = _task.ProgressPercent;
            // 进度回调停滞超过 2 秒时速度归零显示：肉眼即可分辨「下载侧停滞」与「显示侧冻结」
            var stalled = _task.Stage == DownloadTaskStage.Downloading
                && (DateTimeOffset.UtcNow - _task.LastProgressUtc).TotalSeconds > 2;
            var speed = stalled ? 0 : (long)_task.SpeedBytesPerSec;
            _rightText.Text = _task.Stage == DownloadTaskStage.Downloading && _task.Total > 0
                ? $"{_task.ProgressPercent:F0}% · {DownloadManager.FormatBytes(speed)}/s"
                : StageText(_task.Stage);
            _pauseButton.Visibility = _task.IsActive ? Visibility.Visible : Visibility.Collapsed;
            _resumeButton.Visibility = _task.Stage == DownloadTaskStage.Paused ? Visibility.Visible : Visibility.Collapsed;
            _cancelButton.Visibility = _task.IsListed ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
