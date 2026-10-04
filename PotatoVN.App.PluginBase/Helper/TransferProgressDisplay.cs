using System;
using System.Collections.Generic;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>进度显示统一保留一位小数；阶段真正结束前不显示 100%，避免四舍五入制造「假完成」。</summary>
internal static class TransferProgressDisplay
{
    internal static double Percent(long processed, long total, bool completed = false)
    {
        if (completed) return 100;
        if (total <= 0 || processed <= 0) return 0;
        // decimal 避免大文件在最后几个字节时浮点除法先舍入到 100；再向下截断到 0.1%。
        return (double)Math.Min(99.9m, Math.Floor((decimal)processed * 1000 / total) / 10);
    }
}

/// <summary>
/// 用最近三秒的真实字节增量显示速率，平滑分块切换和瞬时突发；停滞满一个窗口就明确显示等待。
/// 只在面板线程使用，时间由调用方传入单调时钟，便于测试且不受系统时间校准影响。
/// </summary>
internal sealed class TransferSpeedSampler
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(200);
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private long _lastBytes;
    private TimeSpan _lastSampleTime;
    private TimeSpan _lastChangeTime;

    internal double? BytesPerSecond { get; private set; }
    internal bool IsWaiting { get; private set; }

    internal void Reset()
    {
        _samples.Clear();
        BytesPerSecond = null;
        IsWaiting = false;
    }

    internal void Update(long bytes, TimeSpan now)
    {
        bytes = Math.Max(0, bytes);
        // 服务端忽略续传而从头重下、或切换阶段时，旧基线不能参与新速率计算。
        if (_samples.Count > 0 && (bytes < _lastBytes || now < _lastSampleTime)) Reset();
        if (_samples.Count == 0)
        {
            _samples.Enqueue((now, bytes));
            _lastBytes = bytes;
            _lastSampleTime = _lastChangeTime = now;
            return;
        }

        // 按钮回调也会刷新行；过密的刷新不消耗采样区间，避免下一次 timer tick 被算成零速度。
        if (now - _lastSampleTime < MinimumInterval) return;
        if (bytes > _lastBytes) _lastChangeTime = now;
        _lastBytes = bytes;
        _lastSampleTime = now;
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && now - _samples.Peek().Time > Window)
            _samples.Dequeue();

        var first = _samples.Peek();
        var seconds = (now - first.Time).TotalSeconds;
        BytesPerSecond = seconds > 0 ? (bytes - first.Bytes) / seconds : null;
        IsWaiting = now - _lastChangeTime >= Window;
    }
}
