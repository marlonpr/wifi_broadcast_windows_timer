using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class SegmentDriftMonitorTests
{
    private static RtcSegmentDriftPoint[] Points(long epoch, long phase, ulong boot = 1, string build = "build") =>
    [
        new("ESP01", boot, build, epoch, epoch + phase, "ap1"),
        new("ESP03", 1, "build", epoch, epoch, "ap2"),
        new("ESP04", 1, "build", epoch, epoch, "ap3"),
    ];

    [TestMethod]
    public void PositiveRampCountsDeviationDuringRepeatedInnovations() => CheckRamp(1);

    [TestMethod]
    public void NegativeRampCountsDeviationDuringRepeatedInnovations() => CheckRamp(-1);

    private static void CheckRamp(int direction)
    {
        var monitor = new SegmentDriftMonitor();
        monitor.Observe(Points(1, 0));
        monitor.Observe(Points(6_000_000_001, 0));
        long phase = 0;
        for (int step = 1; step <= 15; step++)
        {
            phase += direction * 600L * step;
            var estimate = monitor.Observe(Points(6_000_000_001L + step * 6_000_000_000L, phase))
                .Single(x => x.DeviceId == "ESP01");
            Assert.AreEqual(direction * step * 0.1, estimate.RelativeFleetPpm, 1e-9);
            Assert.IsTrue(estimate.ValidationRequired, $"Innovation at ramp step {step}");
            Assert.AreEqual(step >= 13, estimate.SustainedDeviation,
                "1.1, 1.2 and 1.3 ppm must count despite innovation alerts");
        }
    }

    [TestMethod]
    public void SingleCorruptedEpochDoesNotBecomeSustainedDeviation()
    {
        var monitor = new SegmentDriftMonitor();
        monitor.Observe(Points(1, 0));
        monitor.Observe(Points(540_000_001, 0));
        var first = monitor.Observe(Points(1_080_000_001, 10_000)).Single(x => x.DeviceId == "ESP01");
        var second = monitor.Observe(Points(1_620_000_001, 0)).Single(x => x.DeviceId == "ESP01");
        var recovered = monitor.Observe(Points(2_160_000_001, 0)).Single(x => x.DeviceId == "ESP01");
        Assert.IsTrue(first.ValidationRequired && second.ValidationRequired);
        Assert.IsFalse(first.SustainedDeviation || second.SustainedDeviation || recovered.SustainedDeviation);
        Assert.IsFalse(recovered.ValidationRequired);
    }

    [TestMethod]
    public void StepWarnsOnThirdHighEstimateIncludingInnovationIntervals()
    {
        var monitor = new SegmentDriftMonitor();
        monitor.Observe(Points(1, 0));
        monitor.Observe(Points(540_000_001, 0));
        for (int step = 1; step <= 4; step++)
        {
            var estimate = monitor.Observe(Points(540_000_001L + step * 540_000_000L, step * 810L))
                .Single(x => x.DeviceId == "ESP01");
            Assert.AreEqual(step <= 2, estimate.ValidationRequired);
            Assert.AreEqual(step >= 3, estimate.SustainedDeviation);
        }
    }

    [TestMethod]
    public void ReturningToLimitResetsStreak()
    {
        var monitor = new SegmentDriftMonitor();
        monitor.Observe(Points(1, 0));
        long epoch = 1, phase = 0;
        long[] phaseIncrements = [810, 810, 540, 810, 810, 810];
        for (int index = 0; index < phaseIncrements.Length; index++)
        {
            epoch += 540_000_000;
            phase += phaseIncrements[index];
            var estimate = monitor.Observe(Points(epoch, phase)).Single(x => x.DeviceId == "ESP01");
            Assert.AreEqual(index == 5, estimate.SustainedDeviation,
                "Exactly 1 ppm must reset the consecutive >1 ppm streak");
        }
    }

    [TestMethod]
    public void RebootBuildChangeAndResetDiscardStreak()
    {
        foreach (string change in new[] { "boot", "build", "reset" })
        {
            var monitor = new SegmentDriftMonitor();
            monitor.Observe(Points(1, 0));
            monitor.Observe(Points(540_000_001, 810));
            monitor.Observe(Points(1_080_000_001, 1620));
            ulong boot = change == "boot" ? 2UL : 1UL;
            string build = change == "build" ? "other" : "build";
            if (change == "reset") monitor.Reset();
            Assert.IsFalse(monitor.Observe(Points(1_620_000_001, 2430, boot, build))
                .Any(x => x.DeviceId == "ESP01"), change);
            Assert.IsFalse(monitor.Observe(Points(2_160_000_001, 3240, boot, build))
                .Single(x => x.DeviceId == "ESP01").SustainedDeviation, change);
        }
    }
}
