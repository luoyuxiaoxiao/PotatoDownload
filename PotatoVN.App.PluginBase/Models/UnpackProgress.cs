namespace PotatoVN.App.PluginBase.Models;

/// <summary>解压线程发布的不可变快照；字节数只统计已写入输出流的数据，未知总量使用 null。</summary>
public sealed record UnpackProgress(long BytesExtracted, long? TotalBytes,
    int FilesExtracted, int? TotalFiles, string? CurrentEntry);
