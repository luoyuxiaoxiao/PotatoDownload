using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
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
/// 所有者在弹窗关闭后必须调用 <see cref="Detach"/>。
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
    private readonly CheckBox _shutdownCheck;
    private readonly TextBlock _shutdownCountdownText;
    private bool _syncingShutdown;
    private bool _historyExpanded;

    /// <summary>
    /// 对话框标题栏（ContentDialog.Title）：左侧标题，右侧固定「关机」勾选框，倒计时文本在固定宽度的
    /// 中间列右对齐——文本长度变化不会推动勾选框，三者位置恒定。勾选=全部任务完成后关机，取消勾选=不关机
    /// （倒计时进行中取消勾选即取消关机）；没有活动任务时勾选框锁定。
    /// </summary>
    internal FrameworkElement DialogHeader { get; }

    /// <summary>
    /// 进度轮询计时器：PropertyChanged→InvokeOnMainThread 链路在宿主环境被实证不可靠（面板冻结、
    /// 只在打开瞬间显示一次快照），改为在 UI 线程上每 500ms 直接读任务模型刷新——
    /// 只要弹窗能打开，这条路一定走。任务模型不再发事件，面板是唯一的读取方。
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

        _shutdownCountdownText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _shutdownCheck = new CheckBox
        {
            Content = "关机",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _shutdownCheck.Checked += (_, _) =>
        {
            if (_syncingShutdown) return;
            Plugin.DownloadManager.SetShutdownArmed(true);
            SyncShutdownUi();
        };
        _shutdownCheck.Unchecked += (_, _) =>
        {
            if (_syncingShutdown) return;
            Plugin.DownloadManager.SetShutdownArmed(false);
            Plugin.CancelShutdownCountdown();
            SyncShutdownUi();
        };
        var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "下载",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_shutdownCountdownText, 1);
        header.Children.Add(_shutdownCountdownText);
        Grid.SetColumn(_shutdownCheck, 2);
        header.Children.Add(_shutdownCheck);
        DialogHeader = header;
        SyncShutdownUi();

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
        Rebuild();
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    /// <summary>
    /// 停表 + 退订集合事件。由弹窗所有者在 ShowAsync 返回后调用（Plugin_Ui），
    /// 不依赖 Loaded/Unloaded/IsLoaded：ContentDialog 内容关闭时未必触发 Unloaded，打开过程中又可能虚发一次——
    /// 之前按 Unloaded 拆订阅/停表，面板就只剩打开瞬间的一张快照。重复调用无害。
    /// </summary>
    internal void Detach()
    {
        _refreshTimer.Stop();
        Plugin.DownloadManager.Tasks.CollectionChanged -= OnCollectionChanged;
        Plugin.HistoryCollection.CollectionChanged -= OnCollectionChanged;
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

    /// <summary>两个集合都只在主线程改动，事件也在主线程到达；直接重建即可。</summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>每 500ms 在 UI 线程上直接读任务模型刷新行；列出的任务数变化时顺手重建（移出已完成行）。</summary>
    private void OnRefreshTick(object? sender, object e)
    {
        SyncShutdownUi();
        var listed = 0;
        foreach (var task in Plugin.DownloadManager.Tasks)
            if (task.IsListed) listed++;
        if (listed != _activePanel.Children.Count)
        {
            Rebuild();
            return;
        }
        foreach (var child in _activePanel.Children)
            if (child is TaskRow row) row.Refresh();
    }

    /// <summary>同步「关机」勾选框与倒计时显示（勾选事件与 500ms 轮询都走这里）。</summary>
    private void SyncShutdownUi()
    {
        var remaining = Plugin.ShutdownCountdownRemaining ?? TimeSpan.Zero;
        var countingDown = remaining > TimeSpan.Zero;
        var hasActive = false;
        foreach (var task in Plugin.DownloadManager.Tasks)
            if (task.IsActive) { hasActive = true; break; }
        _syncingShutdown = true;
        _shutdownCheck.IsChecked = countingDown || Plugin.DownloadManager.ShutdownArmed;
        _syncingShutdown = false;
        // 没有活动任务且不在倒计时：锁定勾选框——没有任务就不应进行关机动作
        _shutdownCheck.IsEnabled = hasActive || countingDown;
        if (countingDown)
        {
            var failed = Plugin.ShutdownCountdownFailedCount;
            _shutdownCountdownText.Text = (failed > 0 ? $"{failed} 个任务失败，" : string.Empty)
                + $"{(int)Math.Ceiling(remaining.TotalSeconds)} 秒后关机";
        }
        else
        {
            _shutdownCountdownText.Text = string.Empty;
        }
    }

    private void Rebuild()
    {
        // 历史更新或新推送不应把正在下载的行重新实例化，否则速率采样会被清空，显示突然归零。
        var existingRows = new Dictionary<DownloadTask, TaskRow>();
        foreach (var child in _activePanel.Children)
            if (child is TaskRow row) existingRows[row.Task] = row;
        _activePanel.Children.Clear();
        var activeCount = 0;
        foreach (var task in Plugin.DownloadManager.Tasks)
        {
            if (!task.IsListed) continue;
            var row = existingRows.TryGetValue(task, out var existing) ? existing : new TaskRow(task);
            _activePanel.Children.Add(row);
            row.Refresh();
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
        grid.Children.Add(Glyph(ok ? "\uE73E" : cancelled ? "\uE711" : "\uEA39", glyphBrush));

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

    /// <summary>活动任务行：按当前阶段展示真实字节进度；速度使用三秒滑窗，未知总量时使用不定进度条。</summary>
    private sealed class TaskRow : UserControl
    {
        private readonly DownloadTask _task;
        private readonly TextBlock _rightText;
        private readonly TextBlock _speedText;
        private readonly TextBlock _messageText;
        private readonly TextBlock _detailText;
        private readonly ProgressBar _progressBar;
        private readonly Button _pauseButton;
        private readonly Button _resumeButton;
        private readonly Button _cancelButton;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TransferSpeedSampler _speed = new();
        private DownloadTaskStage? _sampleStage;

        internal DownloadTask Task => _task;

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
            grid.Children.Add(Glyph("\uE896"));

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
            _detailText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };
            centerStack.Children.Add(_detailText);
            _progressBar = new ProgressBar { Height = 4, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
            centerStack.Children.Add(_progressBar);
            grid.Children.Add(centerStack);

            var rightStack = new StackPanel
            {
                Spacing = 6,
                MinWidth = 116,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _rightText = new TextBlock
            {
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
            };
            _speedText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.65,
                TextAlignment = TextAlignment.Right,
            };
            _pauseButton = IconButton(Symbol.Pause, "暂停");
            _resumeButton = IconButton(Symbol.Play, "继续");
            _cancelButton = IconButton(Symbol.Cancel, "取消");
            _pauseButton.Click += (_, _) =>
            {
                _task.Pause();
                Refresh();
            };
            _resumeButton.Click += (_, _) =>
            {
                Plugin.DownloadManager.ResumeTask(_task);
                Refresh();
            };
            _cancelButton.Click += (_, _) =>
            {
                Plugin.DownloadManager.CancelTask(_task);
                Refresh();
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
            rightStack.Children.Add(_speedText);
            rightStack.Children.Add(buttonStack);
            Grid.SetColumn(rightStack, 2);
            grid.Children.Add(rightStack);

            Content = Card(grid);
            Refresh();
        }

        internal void Refresh()
        {
            var stage = _task.Stage;
            var received = _task.Received;
            var unpack = _task.UnpackProgress;
            if (_sampleStage != stage)
            {
                _speed.Reset();
                _sampleStage = stage;
            }
            if (stage == DownloadTaskStage.Downloading)
                _speed.Update(received, _clock.Elapsed);
            else if (stage == DownloadTaskStage.Unpacking && unpack is not null)
                _speed.Update(unpack.BytesExtracted, _clock.Elapsed);

            var percent = 0.0;
            var indeterminate = false;
            var showProgress = stage != DownloadTaskStage.Pending;
            var message = _task.Message;
            var right = StageText(stage);
            var rate = string.Empty;
            var detail = string.Empty;
            switch (stage)
            {
                case DownloadTaskStage.Downloading:
                    percent = TransferProgressDisplay.Percent(received, _task.Total);
                    message = $"下载中 · {DownloadManager.FormatBytes(received)} / {DownloadManager.FormatBytes(_task.Total)}";
                    right = $"{percent:F1}%";
                    rate = received >= _task.Total ? "正在收尾…" : SpeedText("等待数据…");
                    break;
                case DownloadTaskStage.Unpacking:
                    // 不再把下载完成的 100% 进度条留到解压阶段；大文件内每次写入也会更新字节快照。
                    indeterminate = unpack?.TotalBytes is not > 0;
                    if (unpack is null) break;
                    if (unpack.TotalBytes is > 0)
                    {
                        percent = TransferProgressDisplay.Percent(unpack.BytesExtracted, unpack.TotalBytes.Value);
                        message = $"解压中 · {DownloadManager.FormatBytes(unpack.BytesExtracted)} / {DownloadManager.FormatBytes(unpack.TotalBytes.Value)}";
                        right = $"{percent:F1}%";
                    }
                    else
                    {
                        message = $"解压中 · 已解压 {DownloadManager.FormatBytes(unpack.BytesExtracted)} · {unpack.FilesExtracted} 个文件";
                    }
                    detail = unpack.CurrentEntry ?? string.Empty;
                    rate = unpack.TotalBytes is > 0 && unpack.BytesExtracted >= unpack.TotalBytes.Value
                        ? "正在收尾…" : SpeedText("处理中…");
                    break;
                case DownloadTaskStage.Verifying:
                case DownloadTaskStage.Importing:
                    // 校验和刮削没有可用总量，展示当前动作，不能用下载字节数冒充整体完成度。
                    indeterminate = true;
                    break;
                case DownloadTaskStage.Paused:
                    if (_task.PausedFromStage == DownloadTaskStage.Downloading)
                        percent = TransferProgressDisplay.Percent(received, _task.Total);
                    else if (_task.PausedFromStage == DownloadTaskStage.Unpacking && unpack?.TotalBytes is > 0)
                        percent = TransferProgressDisplay.Percent(unpack.BytesExtracted, unpack.TotalBytes.Value);
                    else
                        showProgress = false;
                    break;
                case DownloadTaskStage.Completed:
                    percent = 100;
                    break;
            }

            // 已请求但流程尚未收尾：按钮立即反馈，不等下一轮
            var interrupting = _task.CancelRequest != CancelRequestKind.None && _task.IsActive;
            _messageText.Text = interrupting
                ? (_task.CancelRequest == CancelRequestKind.Cancel ? "取消中…" : "暂停中…")
                : message;
            _progressBar.IsIndeterminate = indeterminate && !interrupting;
            _progressBar.Value = percent;
            _progressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            _rightText.Text = right;
            _speedText.Text = interrupting ? string.Empty : rate;
            _speedText.Visibility = _speedText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _detailText.Text = detail;
            _detailText.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(_detailText, detail);
            _pauseButton.Visibility = _task.IsActive && !interrupting ? Visibility.Visible : Visibility.Collapsed;
            _resumeButton.Visibility = stage == DownloadTaskStage.Paused ? Visibility.Visible : Visibility.Collapsed;
            _cancelButton.Visibility = _task.IsListed && _task.CancelRequest != CancelRequestKind.Cancel
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SpeedText(string waiting) => _speed.IsWaiting ? waiting
            : _speed.BytesPerSecond is { } speed && speed > 0
                ? $"{DownloadManager.FormatBytes((long)speed)}/s"
                : "计算速度中…";
    }
}
