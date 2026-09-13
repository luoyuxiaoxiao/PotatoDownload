using System;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>一条下载历史记录（随插件数据持久化）。</summary>
public class DownloadRecord
{
    public const string OutcomeCompleted = "Completed";
    public const string OutcomeFailed = "Failed";
    public const string OutcomeCancelled = "Cancelled";

    public string Title { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset FinishedAt { get; set; }
}
