using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class RtcWarmupHistoryTests
{
    private static StatusPacket Status(string id = "ESP01", double rate = -6, double rms = 0.5) => new(
        id, 1, TimerState.Ready, 0, RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129,
        RtcFitRmsMicroseconds: rms, RtcQueueDrops: 0, RtcTemperatureValid: true,
        RtcRatePpmVsRtc: rate, RtcTemperatureC: 25.25, RtcAcceptedEdges: 1608,
        RtcInferredMissingEdges: 0, RtcHoldoverEntries: 0, FirmwareBuildId: "production-build", DiagnosticFlags: 0);

    private static RtcWarmupHistory Warm(string id = "ESP01", double change = 0)
    {
        var history = new RtcWarmupHistory();
        for (int n = 0; n <= 38; n++)
        {
            long time = n * 16_000_000L;
            history.BeginPoll(n, time);
            history.Observe(Status(id, -6 + change * n / 38) with { RtcAcceptedEdges = (ulong)(1000 + n * 16) }, 1, time);
        }
        return history;
    }

    [TestMethod]
    public void ExactlyTenMinutesIsRequiredAndTheRateThresholdIsInclusive()
    {
        var history = new RtcWarmupHistory();
        for (int n = 0; n <= 37; n++)
        {
            history.BeginPoll(n, n * 16_000_000L);
            history.Observe(Status(), 1, n * 16_000_000L);
        }
        Assert.AreEqual(592d, history.Evaluate(592_000_000).HistorySeconds);
        Assert.IsFalse(history.Evaluate(592_000_000).Ready);
        history.BeginPoll(38, 600_000_000);
        history.Observe(Status(rate: -5.95), 1, 600_000_000);
        Assert.IsTrue(history.Evaluate(600_000_000).Ready);
        Assert.IsTrue(Warm(change: 0.25).Evaluate(608_000_000).Ready);
        Assert.IsFalse(Warm(change: 0.250001).Evaluate(608_000_000).Ready);
    }

    [TestMethod]
    public void ThreeGoodPollsAreRequiredAndDuplicatesAndManualRepliesCannotSupplyThem()
    {
        RtcWarmupHistory history = Warm();
        history.Observe(Status(rms: 2.4), 1, 608_000_001);
        Assert.AreEqual(0, history.Evaluate(608_000_001).GoodFitPolls);
        Assert.IsTrue(history.Evaluate(608_000_001).Reasons.Contains("FIT_UNSTABLE"));
        for (int n = 0; n < 10; n++) history.Observe(Status(), 1, 608_000_002 + n);
        Assert.IsFalse(history.Evaluate(608_000_020).Ready);
        for (int n = 39; n <= 41; n++)
        {
            history.BeginPoll(n, n * 16_000_000L);
            history.Observe(Status(), 1, n * 16_000_000L);
            history.Observe(Status(), 1, n * 16_000_000L + 1);
            Assert.AreEqual(n == 41, history.Evaluate(n * 16_000_000L + 1).Ready);
        }
    }

    [TestMethod]
    public void PollJitterDoesNotDiscardSamplesAndMissingPollBreaksTheFitStreak()
    {
        var history = new RtcWarmupHistory();
        for (int n = 0; n <= 38; n++)
        {
            long time = n * 16_000_000L;
            history.BeginPoll(n, time);
            history.Observe(Status(), 1, time + (n % 2 == 0 ? 20_000 : 100_000));
        }
        Assert.AreEqual(39, history.Samples.Count);
        Assert.IsTrue(history.Evaluate(608_100_000).Ready);
        history.BeginPoll(39, 624_000_000); // No reply.
        history.BeginPoll(40, 640_000_000);
        history.Observe(Status(), 1, 640_000_000);
        Assert.AreEqual(1, history.Evaluate(640_000_000).GoodFitPolls);
        Assert.IsFalse(history.Evaluate(640_000_000).Ready);
    }

    [TestMethod]
    public void Esp02BlocksTheFiveBoardFleetBeforeAndAfterTenMinutes()
    {
        RtcWarmupHistory[] fleet = Enumerable.Range(1, 5).Select(n => Warm($"ESP{n:00}")).ToArray();
        Assert.IsTrue(fleet.All(h => h.Evaluate(608_000_000).Ready));
        fleet[1].Observe(Status("ESP02", -6.07, 2.8), 1, 608_000_001);
        Assert.IsFalse(fleet.All(h => h.Evaluate(608_000_001).Ready));
        StringAssert.Contains(fleet[1].Evaluate(608_000_001).Display("ESP02"), "FIT_UNSTABLE");
        var settling = new RtcWarmupHistory();
        settling.BeginPoll(0, 0);
        settling.Observe(Status("ESP02", -6.07, 2.4), 1, 0);
        Assert.IsTrue(settling.Evaluate(0).Reasons.Contains("FIT_UNSTABLE"));
        Assert.IsTrue(settling.Evaluate(0).Reasons.Contains("HISTORY_PENDING"));
        Assert.IsFalse(Warm("ESP02", 0.30).Evaluate(608_000_000).Ready);
    }

    [TestMethod]
    public void RebootBuildChangeLockLossDiagnosticFirmwareAndOutageResetHistory()
    {
        foreach (StatusPacket bad in new[] {
            Status() with { FirmwareBuildId = "different-build" },
            Status() with { RtcState = RtcDisciplineState.Holdover },
            Status() with { DiagnosticFlags = 4 }, Status() with { DiagnosticFlags = null },
            Status() with { RtcAcceptedEdges = 1 }, Status() with { RtcInferredMissingEdges = 1 },
            Status() with { RtcHoldoverEntries = 1 }, Status() with { RtcRatePpmVsRtc = double.NaN } })
        {
            RtcWarmupHistory history = Warm();
            history.Observe(bad, 1, 608_000_001);
            Assert.IsFalse(history.Evaluate(608_000_001).Ready);
            Assert.AreEqual(0, history.Samples.Count);
        }
        RtcWarmupHistory reboot = Warm();
        reboot.Observe(Status(), 2, 608_000_001);
        Assert.AreEqual(0, reboot.Samples.Count);
        Assert.IsFalse(Warm().Evaluate(632_000_000).Ready);
        RtcWarmupHistory outage = Warm();
        outage.Observe(Status(), 1, 656_000_000);
        Assert.AreEqual(0, outage.Samples.Count);
    }

    [TestMethod]
    public void SecondStartReusesProofAfterQuietRunButRejectsChangedFleetOrUnhealthyStatus()
    {
        RtcWarmupHistory[] fleet = Enumerable.Range(1, 5).Select(n => Warm($"ESP{n:00}")).ToArray();
        var proof = new QualificationWarmupProof(0x1234, 610_000_000, 2400,
            fleet.Select(h => new QualificationWarmupDeviceProof(h.Latest!.Status.DeviceId, h.Evaluate(608_000_000), h.Samples)).ToArray());
        RtcWarmupSample[] post = fleet.Select(h => new RtcWarmupSample(3_010_500_000, 1,
            h.Latest!.Status with { CommandId = 0x1234, State = TimerState.Finished, RtcAcceptedEdges = 4010 })).ToArray();
        Assert.AreEqual(0, proof.ValidateSecondStart(post, 3_010_500_000).Count);
        Assert.IsTrue(proof.ValidateSecondStart(post[..3], 3_010_500_000).Count > 0);
        Assert.IsTrue(proof.ValidateSecondStart(post, 3_131_000_001).Count > 0);
        Assert.IsTrue((proof with { FirstDurationSeconds = 30 }).ValidateSecondStart(post, 3_010_500_000).Count > 0);
        foreach (RtcWarmupSample bad in new[] {
            post[1] with { SessionGeneration = 2 },
            post[1] with { Status = post[1].Status with { DiagnosticFlags = 4 } },
            post[1] with { Status = post[1].Status with { RtcFitRmsMicroseconds = 1.25 } },
            post[1] with { Status = post[1].Status with { CommandId = 0x4567 } },
            post[1] with { Status = post[1].Status with { RtcInferredMissingEdges = 1 } } })
        {
            RtcWarmupSample[] contaminated = (RtcWarmupSample[])post.Clone();
            contaminated[1] = bad;
            Assert.IsTrue(proof.ValidateSecondStart(contaminated, 3_010_500_000).Count > 0);
        }
        StringAssert.Contains(proof.ToCsv(), "ESP05,POLL");
        StringAssert.Contains(proof.ToCsv(), "ESP02,GATE");
        StringAssert.Contains(proof.ToCsv(), "RtcTemperatureC");
    }

    [TestMethod]
    public void PostSyncReplyRefreshesHistoryAfterSlowSyncBeforeDiscoveryCanReply()
    {
        foreach (long elapsed in new[] { 24_000_000L, 30_000_000L, 47_000_000L })
        {
            long now = 608_000_000 + elapsed;
            RtcWarmupHistory originalRace = Warm();
            originalRace.Observe(Status(), 1, now);
            Assert.IsTrue(originalRace.Evaluate(now).Reasons.Contains("HISTORY_PENDING"));
            RtcWarmupHistory fixedHistory = Warm();
            fixedHistory.BeginPostSyncPoll(now);
            fixedHistory.Observe(Status(), 1, now + 10);
            Assert.IsTrue(fixedHistory.Evaluate(now + 10).Ready);
            Assert.AreEqual(40, fixedHistory.Samples.Count);
            Assert.AreEqual(RtcWarmupSampleKind.PostSync, fixedHistory.Samples[^1].Kind);
            Assert.AreEqual(3, fixedHistory.Evaluate(now + 10).GoodFitPolls);
        }
    }

    [TestMethod]
    public void OverlappingPostSyncAndDiscoveryRepliesConsumeOneSampleAndDoNotChangeSequences()
    {
        foreach (bool discoveryFirst in new[] { false, true })
        {
            RtcWarmupHistory history = Warm();
            if (discoveryFirst) history.BeginPoll(39, 638_000_000);
            history.BeginPostSyncPoll(638_000_000);
            history.Observe(Status(), 1, 638_000_010);
            history.Observe(Status(), 1, 638_000_020); // Duplicate cannot add another sample.
            Assert.AreEqual(40, history.Samples.Count);
            Assert.IsTrue(history.Evaluate(638_000_020).Ready);
            // The next discovery sequence remains valid after the foreground refresh.
            history.BeginPoll(discoveryFirst ? 40 : 39, 654_000_000);
            history.Observe(Status(), 1, 654_000_010);
            Assert.AreEqual(41, history.Samples.Count);
            Assert.IsTrue(history.Evaluate(654_000_010).Ready);
        }
    }

    [TestMethod]
    public void ForegroundRefreshesCannotManufactureGoodFitPollsOrConcealContinuityFailures()
    {
        RtcWarmupHistory history = Warm();
        history.Observe(Status(rms: 2.4), 1, 608_000_001);
        for (int n = 0; n < 5; n++)
        {
            long now = 638_000_000 + n * 1_000_000L;
            history.BeginPostSyncPoll(now);
            history.Observe(Status(), 1, now + 10);
            Assert.AreEqual(0, history.Evaluate(now + 10).GoodFitPolls);
            Assert.IsFalse(history.Evaluate(now + 10).Ready);
        }
        RtcWarmupHistory late = Warm();
        late.BeginPostSyncPoll(638_000_000);
        late.Observe(Status(), 1, 640_000_001);
        Assert.AreEqual(39, late.Samples.Count);
        Assert.IsFalse(late.Evaluate(640_000_001).Ready);
        foreach (StatusPacket bad in new[] {
            Status() with { RtcState = RtcDisciplineState.Holdover },
            Status() with { RtcInferredMissingEdges = 1 },
            Status() with { DiagnosticFlags = 4 } })
        {
            RtcWarmupHistory broken = Warm();
            broken.BeginPostSyncPoll(638_000_000);
            broken.Observe(bad, 1, 638_000_010);
            Assert.IsFalse(broken.Evaluate(638_000_010).Ready);
            Assert.IsTrue(broken.Samples.Count <= 1); // The old ten-minute segment was discarded.
        }
        RtcWarmupHistory outage = Warm();
        outage.BeginPostSyncPoll(656_000_000);
        outage.Observe(Status(), 1, 656_000_010);
        Assert.IsFalse(outage.Evaluate(656_000_010).Ready); // >=48 s gap still restarts the history.
    }

    [TestMethod]
    public void QueueDropCounterMustRemainZeroSinceBoot()
    {
        RtcWarmupHistory history = Warm();
        history.BeginPostSyncPoll(638_000_000);
        history.Observe(Status() with { RtcQueueDrops = 1 }, 1, 638_000_010);
        Assert.IsTrue(history.Evaluate(638_000_010).Reasons.Contains("RTC_QUEUE_DROPS"));
        Assert.AreEqual(40, history.Samples.Count); // Good rate/RMS does not erase a boot counter.
        Assert.IsFalse(history.Evaluate(638_000_010).Ready);
    }

    [TestMethod]
    public void Start2RateChangeUsesActualStart1ReadingAndIsTelemetryOnly()
    {
        var change = new QualificationStartRateChange("ESP02", -6.945, -6.931);
        Assert.AreEqual(0.014, change.ChangePpm!.Value, 1e-12);
        StringAssert.Contains(change.Display(), "Δrate=+0.014000 ppm");
        Assert.IsNull(new QualificationStartRateChange("ESP02", null, -6.931).ChangePpm);
        Assert.IsNull(new QualificationStartRateChange("ESP02", -6.945, double.NaN).ChangePpm);
        var beyondValidator = new QualificationStartRateChange("ESP02", -6.945, -6.645);
        StringAssert.Contains(beyondValidator.Display(), "(informational only)");
        RtcWarmupHistory history = Warm("ESP02");
        var proof = new QualificationWarmupProof(0x1234, 610_000_000, 2400,
            [new("ESP02", history.Evaluate(608_000_000), history.Samples)]);
        var post = new RtcWarmupSample(3_010_500_000, 1, history.Latest!.Status with {
            CommandId = 0x1234, State = TimerState.Finished, RtcAcceptedEdges = 4010, RtcRatePpmVsRtc = -6.645 });
        Assert.AreEqual(0, proof.ValidateSecondStart([post], 3_010_500_000).Count);
        QualificationStartRateChange row = proof.RateChangesSinceFirstStart(
            new Dictionary<string, double> { ["ESP02"] = -6.945 }, [post]).Single();
        Assert.AreEqual(0.3, row.ChangePpm!.Value, 1e-12); // Local-crystal movement never gates START #2 or qualification.
        StringAssert.Contains(row.Display(), "informational only");
        Assert.IsFalse(row.Display().Contains("WARNING", StringComparison.Ordinal));
    }
    [TestMethod]
    public void PartialFitsCannotSupplyHistoryOrGoodFitPolls()
    {
        var history = new RtcWarmupHistory();
        for (int n = 0; n <= 38; n++)
        {
            long time = n * 16_000_000L;
            history.BeginPoll(n, time);
            history.Observe(Status() with { RtcFitPoints = 128 }, 1, time);
        }
        Assert.AreEqual(0, history.Samples.Count);
        Assert.IsTrue(history.Evaluate(608_000_000).Reasons.Contains("FIT_NOT_FULL"));
        for (int n = 39; n <= 77; n++)
        {
            long time = n * 16_000_000L;
            history.BeginPoll(n, time);
            history.Observe(Status(), 1, time);
            Assert.AreEqual(n == 77, history.Evaluate(time).Ready);
        }
        RtcWarmupHistory ready = Warm();
        ready.Observe(Status() with { RtcFitPoints = 128 }, 1, 608_000_001);
        Assert.IsFalse(ready.Evaluate(608_000_001).Ready);
        Assert.AreEqual(0, ready.Samples.Count);
        Assert.IsFalse(QualificationWarmupProof.HealthyContinuation(
            Warm().Latest!, new(3_010_500_000, 1, Status() with { RtcFitPoints = 128 }), 3_010_500_000));
    }

    [TestMethod]
    public void WarmupProofRecordsFrozenPolicyAndControllerSession()
    {
        RtcWarmupHistory history = Warm(change: 0.25);
        var proof = new QualificationWarmupProof(1, 610_000_000, 2400,
            [new("ESP01", history.Evaluate(608_000_000), history.Samples)]) { ControllerSessionId = "session-A" };
        StringAssert.Contains(proof.ToCsv(), "RtcFitPoints,WarmupMaxRateChangePpm,WarmupRequiredFitPoints,ControllerSessionId");
        StringAssert.Contains(proof.ToCsv(), ",129,0.25,129,session-A");
    }

}
