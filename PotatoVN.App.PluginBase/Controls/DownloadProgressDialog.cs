using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
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
    private readonly CheckBox _shutdownCheck; // 勾选=全部任务完成后关机；倒计时中取消勾选即取消关机；无活动任务时锁定
    private readonly TextBlock _shutdownCountdownText;
    private bool _syncingShutdown;
    private bool _historyExpanded;

    /// <summary>
    /// 进度轮询计时器：PropertyChanged→InvokeOnMainThread 链路在宿主环境被实证不可靠（面板冻结、
    /// 只在打开瞬间显示一次快照），改为在 UI 线程上每 500ms 直接读任务模型刷新——
    /// 只要弹窗能打开，这条路一定走。任务模型不再发事件，面板是唯一的读取方。
    /// </summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public DownloadProgressDialog()
    {
        _activeHeader = CreateHeader("进行中");
        _activePanel = new StackPanel { Spacing = 6 };
        _historyHeader = CreateHistoryHeader();
        _historyPanel = new StackPanel { Spacing = 6 };
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
            Content = "完成后关机",
            FontSize = 12,
            // WinUI 的 CheckBox 默认 MinWidth=120、文字顶对齐（内边距按 14px 字号配的）：不清掉的话
            // 勾选框右侧会留一截空白贴不到右端，12px 的文字也会比方框高出几像素
            MinWidth = 0,
            Padding = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
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
        // 标题栏放在内容区顶部而不是 ContentDialog.Title：Title 容器左对齐、不拉伸自定义内容；放进内容区后
        // 标题栏与下方卡片同宽，勾选框右缘天然与卡片右缘对齐。倒计时文本在固定宽度的中间列右对齐，
        // 文本长度变化不会推动勾选框，三者位置恒定。
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "下载",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_shutdownCountdownText, 1);
        header.Children.Add(_shutdownCountdownText);
        Grid.SetColumn(_shutdownCheck, 2);
        header.Children.Add(_shutdownCheck);
        SyncShutdownUi();

        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(_activeHeader);
        list.Children.Add(_activePanel);
        list.Children.Add(_historyHeader);
        list.Children.Add(_historyPanel);
        list.Children.Add(_emptyText);

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        MinWidth = 480;
        Content = root;

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
        var clearButton = Subtle(new Button
        {
            Content = "清空历史",
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        });
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

    private static TextBlock Glyph(string glyph, Brush? brush = null, double top = 2) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(GlyphFont),
        FontSize = 16,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>去掉按钮的底色与边框（悬停/按下仍有模板自带的反馈），行内按钮不抢内容的视觉重心。</summary>
    private static Button Subtle(Button button)
    {
        button.Background = new SolidColorBrush(Colors.Transparent);
        button.BorderThickness = new Thickness(0);
        return button;
    }

    /// <summary>行内 28px 图标按钮（暂停/继续/取消/打开位置共用）。</summary>
    private static Button IconButton(Symbol symbol, string tooltip)
    {
        var button = Subtle(new Button
        {
            Content = new SymbolIcon(symbol),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

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

    /// <summary>
    /// 历史行四列：状态图标 | 标题+结果 | 完成时间 | 固定 28px 的操作列。「打开所在位置」恒在行的最右端；
    /// 没有目标位置的记录（失败/取消/旧版本留下的记录）该列留空，时间列因此在所有行上对齐。
    /// </summary>
    private static FrameworkElement CreateHistoryRow(DownloadRecord record)
    {
        var ok = record.Outcome == DownloadRecord.OutcomeCompleted;
        var cancelled = record.Outcome == DownloadRecord.OutcomeCancelled;
        var glyphBrush = PluginTheme.GetBrush(ok ? "SystemFillColorSuccessBrush"
            : cancelled ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.Children.Add(Glyph(ok ? "\uE73E" : cancelled ? "\uE711" : "\uEA39", glyphBrush));

        var textStack = new StackPanel { Spacing = 2 };
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
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(timeText, 2);
        grid.Children.Add(timeText);

        if (ok && !string.IsNullOrWhiteSpace(record.TargetPath))
        {
            var openButton = IconButton(Symbol.Folder, "打开所在位置");
            var targetPath = record.TargetPath;
            openButton.Click += (_, _) => OpenTargetLocation(targetPath);
            Grid.SetColumn(openButton, 3);
            grid.Children.Add(openButton);
        }

        return Card(grid);
    }

    /// <summary>打开历史记录的目标位置：目录直接打开，压缩包在资源管理器中选中；目标已不存在时提示而不是报错。</summary>
    private static void OpenTargetLocation(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Plugin.HostApi.Info(InfoBarSeverity.Warning, "PotatoDownload", "目标位置已不存在（可能已被移动或删除）");
        }
        catch (Exception e)
        {
            Plugin.HostApi.Event(InfoBarSeverity.Error, "PotatoDownload", e, "无法打开目标位置");
        }
    }

    /// <summary>
    /// 活动任务行，三行固定结构：标题+操作按钮 / 进度条 / 当前动作（左）与速度·百分比（右）。
    /// 按当前阶段展示真实字节进度；速度使用三秒滑窗，未知总量时使用不定进度条。
    /// </summary>
    private sealed class TaskRow : UserControl
    {
        private readonly DownloadTask _task;
        private readonly TextBlock _rightText;
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

        public TaskRow(DownloadTask task)
        {
            _task = task;

            // 暂停/继续共用同一格、取消恒在最右：按钮位置不随任务状态移动
            var titleRow = new Grid { ColumnSpacing = 4 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            titleRow.Children.Add(new TextBlock
            {
                Text = task.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
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
            Grid.SetColumn(_pauseButton, 1);
            Grid.SetColumn(_resumeButton, 1);
            Grid.SetColumn(_cancelButton, 2);
            titleRow.Children.Add(_pauseButton);
            titleRow.Children.Add(_resumeButton);
            titleRow.Children.Add(_cancelButton);

            // 进度条始终占位（排队中为空轨道），行高不随阶段跳动
            _progressBar = new ProgressBar { Height = 4, Maximum = 100 };

            var statusRow = new Grid { ColumnSpacing = 12 };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _messageText = new TextBlock { FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
            _rightText = new TextBlock { FontSize = 12, Opacity = 0.65, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(_rightText, 1);
            statusRow.Children.Add(_messageText);
            statusRow.Children.Add(_rightText);

            _detailText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };

            var stack = new StackPanel { Spacing = 4 };
            Grid.SetColumn(stack, 1);
            stack.Children.Add(titleRow);
            stack.Children.Add(_progressBar);
            stack.Children.Add(statusRow);
            stack.Children.Add(_detailText);

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Glyph("\uE896", top: 6)); // 与 28px 高的标题行垂直居中
            grid.Children.Add(stack);

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
            var hasPercent = false;
            var indeterminate = false;
            var message = _task.Message;
            var rate = string.Empty;
            var detail = string.Empty;
            switch (stage)
            {
                case DownloadTaskStage.Downloading:
                    percent = TransferProgressDisplay.Percent(received, _task.Total);
                    hasPercent = true;
                    message = $"下载中 · {DownloadManager.FormatBytes(received)} / {DownloadManager.FormatBytes(_task.Total)}";
                    rate = received >= _task.Total ? "正在收尾…" : SpeedText("等待数据…");
                    break;
                case DownloadTaskStage.Unpacking:
                    // 不再把下载完成的 100% 进度条留到解压阶段；大文件内每次写入也会更新字节快照。
                    indeterminate = unpack?.TotalBytes is not > 0;
                    if (unpack is null) break;
                    if (unpack.TotalBytes is > 0)
                    {
                        percent = TransferProgressDisplay.Percent(unpack.BytesExtracted, unpack.TotalBytes.Value);
                        hasPercent = true;
                        message = $"解压中 · {DownloadManager.FormatBytes(unpack.BytesExtracted)} / {DownloadManager.FormatBytes(unpack.TotalBytes.Value)}";
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
                    // 暂停保留原阶段的进度；原阶段没有可用总量时留空轨道，不拿下载的满进度冒充
                    if (_task.PausedFromStage == DownloadTaskStage.Downloading)
                    {
                        percent = TransferProgressDisplay.Percent(received, _task.Total);
                        hasPercent = true;
                    }
                    else if (_task.PausedFromStage == DownloadTaskStage.Unpacking && unpack?.TotalBytes is > 0)
                    {
                        percent = TransferProgressDisplay.Percent(unpack.BytesExtracted, unpack.TotalBytes.Value);
                        hasPercent = true;
                    }
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
            var percentText = hasPercent ? $"{percent:F1}%" : string.Empty;
            _rightText.Text = interrupting ? string.Empty
                : rate.Length > 0 && percentText.Length > 0 ? $"{rate} · {percentText}" : rate + percentText;
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
