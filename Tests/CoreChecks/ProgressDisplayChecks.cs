using System;
using PotatoVN.App.PluginBase.Helper;

namespace CoreChecks;

internal static class ProgressDisplayChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(TransferProgressDisplay.Percent(995, 1000) == 99.5,
            "progress: 99.5 percent never rounds up to 100");
        check(TransferProgressDisplay.Percent(99999, 100000) == 99.9,
            "progress: unfinished last fraction is truncated to 99.9");
        check(TransferProgressDisplay.Percent(1000, 1000) == 99.9,
            "progress: final bytes do not imply the stage has finished");
        check(TransferProgressDisplay.Percent(1000, 1000, completed: true) == 100,
            "progress: only an explicitly completed stage displays 100");
        check(TransferProgressDisplay.Percent(-1, 100) == 0 &&
              TransferProgressDisplay.Percent(1, 0) == 0 &&
              TransferProgressDisplay.Percent(101, 100) == 99.9,
            "progress: invalid or stale byte counts stay in the display range");
        check(TransferProgressDisplay.Percent(long.MaxValue - 1, long.MaxValue) == 99.9,
            "progress: large byte counts cannot overflow or round to 100");

        const long mib = 1024 * 1024;
        var speed = new TransferSpeedSampler();
        speed.Update(10 * mib, TimeSpan.Zero);
        check(speed.BytesPerSecond is null, "speed: opening a panel does not count old bytes as new traffic");
        speed.Update(12 * mib, TimeSpan.FromSeconds(0.5));
        check(speed.BytesPerSecond == 4 * mib, "speed: first measurement uses elapsed time and new bytes");
        speed.Update(12 * mib, TimeSpan.FromSeconds(0.6));
        check(speed.BytesPerSecond == 4 * mib, "speed: a button refresh does not reset the sample interval");
        speed.Update(12 * mib, TimeSpan.FromSeconds(1));
        check(speed.BytesPerSecond == 2 * mib && !speed.IsWaiting,
            "speed: a short chunk transition retains a rolling measurement");
        speed.Update(16 * mib, TimeSpan.FromSeconds(2));
        check(speed.BytesPerSecond == 3 * mib, "speed: bursts are averaged across the sample window");
        speed.Update(16 * mib, TimeSpan.FromSeconds(3));
        speed.Update(16 * mib, TimeSpan.FromSeconds(4));
        speed.Update(16 * mib, TimeSpan.FromSeconds(5));
        check(speed.BytesPerSecond == 0 && speed.IsWaiting,
            "speed: a real three-second stall clears the old rate and reports waiting");
        speed.Update(20 * mib, TimeSpan.FromSeconds(6));
        check(speed.BytesPerSecond > 0 && !speed.IsWaiting, "speed: new data clears waiting immediately");

        // 顺序服务器忽略 Range 时会从零重下；这类有效回退不能变成负速度或虚高速度。
        speed.Update(2 * mib, TimeSpan.FromSeconds(7));
        check(speed.BytesPerSecond is null && !speed.IsWaiting,
            "speed: a restarted transfer establishes a new baseline");
        speed.Update(3 * mib, TimeSpan.FromSeconds(7.5));
        check(speed.BytesPerSecond == 2 * mib, "speed: sampling recovers after a restart");
        speed.Reset();
        speed.Update(100 * mib, TimeSpan.FromSeconds(8));
        check(speed.BytesPerSecond is null, "speed: a new pipeline stage cannot inherit the previous stage rate");
    }
}
