using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class StartTimingDiagnosticsTests
{
    [TestMethod]
    public void RawClockPhaseDoesNotBecomeReportedStartErrorInEitherArrivalOrder()
    {
        foreach (bool statusFirst in new[] { false, true })
        {
            var timing = new StartTimingDiagnostics();
            var started = new StartedPacket("ESP03", 1, 4_999_715, 5_000_000, 5_000_000);
            var status = new StatusPacket("ESP03", 1, TimerState.Running, 2400,
                StartErrorMicroseconds: 0, SchedulerLatenessMicroseconds: 0);
            if (statusFirst) timing.Observe(status);
            timing.Observe(started, 0);
            if (!statusFirst) timing.Observe(status);
            Assert.AreEqual(-285L, timing.UncorrectedLocalClockPhaseAtTStarUs(1)!.Value);
            Assert.AreEqual(0L, timing.StartErrorUs(1)!.Value);
            Assert.AreEqual(0L, timing.SchedulerLatenessUs(1)!.Value);
            StringAssert.Contains(timing.Display(1), "StartErrorUs=0; SchedulerLatenessUs=0");
        }
    }

    [TestMethod]
    public void ScheduledTargetFromStartedCannotPretendTheTaskWokeOnTime()
    {
        var timing = new StartTimingDiagnostics();
        timing.Observe(new StartedPacket("ESP01", 1, 5_000_000, 5_000_000, 5_000_000), 0);
        Assert.IsNull(timing.StartErrorUs(1));
        Assert.IsNull(timing.SchedulerLatenessUs(1));
        timing.Observe(new StatusPacket("ESP01", 1, TimerState.Running, 30,
            StartErrorMicroseconds: 505, SchedulerLatenessMicroseconds: 505));
        Assert.AreEqual(505L, timing.StartErrorUs(1)!.Value);
        Assert.AreEqual(505L, timing.SchedulerLatenessUs(1)!.Value);
        Assert.AreEqual(0L, timing.UncorrectedLocalClockPhaseAtTStarUs(1)!.Value);
    }

    [TestMethod]
    public void OldCommandsReadyArmedAndMissingMetricsCannotSupplyCurrentStartProof()
    {
        var timing = new StartTimingDiagnostics();
        timing.Observe(new StatusPacket("ESP01", 1, TimerState.Finished, 0,
            StartErrorMicroseconds: 0, SchedulerLatenessMicroseconds: 0));
        Assert.IsNull(timing.StartErrorUs(2));
        foreach (TimerState state in new[] { TimerState.Ready, TimerState.Armed })
            timing.Observe(new StatusPacket("ESP01", 2, state, 30,
                StartErrorMicroseconds: 0, SchedulerLatenessMicroseconds: 0));
        Assert.IsNull(timing.StartErrorUs(2));
        timing.Observe(new StatusPacket("ESP01", 2, TimerState.Running, 30));
        Assert.IsNull(timing.SchedulerLatenessUs(2));
        timing.Clear();
        Assert.IsNull(timing.StartErrorUs(1));
        Assert.IsNull(timing.UncorrectedLocalClockPhaseAtTStarUs(1));
    }
}
