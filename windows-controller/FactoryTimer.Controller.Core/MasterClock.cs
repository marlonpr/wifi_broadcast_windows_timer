using System.Diagnostics;

namespace FactoryTimer.Controller.Core;

public static class MasterClock
{
    private static readonly long OriginTicks = Stopwatch.GetTimestamp();

    public static long NowMicroseconds
    {
        get
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - OriginTicks;
            return (long)(elapsedTicks * 1_000_000d / Stopwatch.Frequency);
        }
    }
}
