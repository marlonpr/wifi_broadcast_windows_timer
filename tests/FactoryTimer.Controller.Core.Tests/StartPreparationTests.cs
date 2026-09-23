using FactoryTimer.Controller.Core;
using System.Net;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class StartPreparationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GateUsesEffectiveSyncEpochRatherThanSyncCompletionTime()
    {
        // Effective epoch 10 s, sync call happened to return at 13 s, target 20 s.
        // The gate must use 10 s of age, not 7 s. At 50 ppm that is 500 us.
        StartGateResult result = StartReadinessGate.Evaluate(
        [
            Participant("ESP01", residualUs: 1_000, effectiveEpochUs: 10_000_000),
            Participant("ESP02", residualUs: 1_000, effectiveEpochUs: 10_000_000),
        ],
        Now,
        targetMasterMicroseconds: 20_000_000);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(1_500L, result.StartUncertaintyMicrosecondsByDevice["ESP01"]);
        Assert.AreEqual(1_500L, result.StartUncertaintyMicrosecondsByDevice["ESP02"]);
        Assert.AreEqual(3_000L, result.WorstPairStartUncertaintyMicroseconds);
    }

    [TestMethod]
    public void StaleStatusTakesPrecedenceOverLastKnownBusyState()
    {
        StartParticipantGateInput staleBusy = Participant(
            "ESP01",
            residualUs: 0,
            effectiveEpochUs: 10_000_000) with
        {
            LastStatusAtUtc = Now - TimeSpan.FromSeconds(5),
            ReportedState = TimerState.Running,
        };

        StartGateResult result = StartReadinessGate.Evaluate(
            [staleBusy],
            Now,
            20_000_000);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(StartBlockReason.StatusStale, result.Blocks.Single().Reason);
    }

    [TestMethod]
    public void FreshArmedOrRunningParticipantIsBusy()
    {
        foreach (TimerState state in new[] { TimerState.Armed, TimerState.Running })
        {
            StartParticipantGateInput busy = Participant(
                "ESP01",
                residualUs: 0,
                effectiveEpochUs: 10_000_000) with
            {
                ReportedState = state,
                ReportedCommandId = 0x1234,
            };

            StartGateResult result = StartReadinessGate.Evaluate(
                [busy],
                Now,
                20_000_000);

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(StartBlockReason.ParticipantBusy, result.Blocks.Single().Reason);
        }
    }

    [TestMethod]
    public void OneParticipantSkipsPairwiseTimingClaim()
    {
        StartGateResult result = StartReadinessGate.Evaluate(
            [Participant("ESP01", residualUs: 19_999, effectiveEpochUs: 10_000_000)],
            Now,
            20_000_000);

        Assert.IsTrue(result.Accepted);
        Assert.IsNull(result.WorstPairStartUncertaintyMicroseconds);
    }

    [TestMethod]
    public void ExactTwentyMillisecondStartBoundPasses()
    {
        StartGateResult result = StartReadinessGate.Evaluate(
        [
            Participant("ESP01", residualUs: 10_000, effectiveEpochUs: 20_000_000),
            Participant("ESP02", residualUs: 10_000, effectiveEpochUs: 20_000_000),
        ],
        Now,
        targetMasterMicroseconds: 20_000_000);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(20_000L, result.WorstPairStartUncertaintyMicroseconds);
    }

    [TestMethod]
    public void StartBoundAboveTwentyMillisecondsIsRejected()
    {
        StartGateResult result = StartReadinessGate.Evaluate(
        [
            Participant("ESP01", residualUs: 10_001, effectiveEpochUs: 20_000_000),
            Participant("ESP02", residualUs: 10_000, effectiveEpochUs: 20_000_000),
        ],
        Now,
        targetMasterMicroseconds: 20_000_000);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(StartBlockReason.TimingBudgetExceeded, result.Blocks.Single().Reason);
        Assert.AreEqual(20_001L, result.WorstPairStartUncertaintyMicroseconds);
    }

    [TestMethod]
    public void CountdownDurationIsNotPartOfReadinessGate()
    {
        // The readiness API has no duration input. A synchronization that is
        // within the 20 ms fleet bound at T* is accepted regardless of whether
        // the prepared run later carries 00:01 or 99:59. Duration validation
        // belongs to the UI/protocol layer, not this start-instant timing gate.
        StartGateResult result = StartReadinessGate.Evaluate(
        [
            Participant("ESP01", residualUs: 4_000, effectiveEpochUs: 20_000_000),
            Participant("ESP02", residualUs: 4_000, effectiveEpochUs: 20_000_000),
        ],
        Now,
        targetMasterMicroseconds: 20_000_000);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(8_000L, result.WorstPairStartUncertaintyMicroseconds);
    }

    [TestMethod]
    public void SessionGenerationMismatchInvalidatesSync()
    {
        StartParticipantGateInput input = Participant(
            "ESP01",
            residualUs: 0,
            effectiveEpochUs: 10_000_000) with
        {
            SyncSessionGeneration = 4,
            CurrentSessionGeneration = 5,
        };

        StartGateResult result = StartReadinessGate.Evaluate(
            [input],
            Now,
            20_000_000);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(StartBlockReason.SyncInvalidated, result.Blocks.Single().Reason);
    }

    [TestMethod]
    public void ArmSessionGuardRejectsObservedSessionChangeBeforeCutoff()
    {
        StartParticipantGateInput frozen = Participant(
            "ESP01", residualUs: 0, effectiveEpochUs: 10_000_000);
        var current = new ParticipantSessionSnapshot(
            Generation: frozen.CurrentSessionGeneration + 1,
            LastStatusAtUtc: Now,
            IpAddress: IPAddress.Parse("192.168.5.101"),
            TimerState: TimerState.Armed,
            CommandId: 0x1234,
            LastLocalTimestampMicroseconds: null,
            DisconnectObserved: false);

        StartBlock? block = StartArmSessionGuard.Evaluate(
            "ESP01", frozen, current, preparedCommandId: 0x1234);

        Assert.IsNotNull(block);
        Assert.AreEqual(StartBlockReason.SyncInvalidated, block.Reason);
    }

    [TestMethod]
    public void ArmSessionGuardAcceptsMatchingPreparedCommandAndRejectsConflictingRun()
    {
        const ulong preparedCommand = 0x1234;
        StartParticipantGateInput frozen = Participant(
            "ESP01", residualUs: 0, effectiveEpochUs: 10_000_000);
        ParticipantSessionSnapshot baseSession = new(
            Generation: frozen.CurrentSessionGeneration,
            LastStatusAtUtc: Now,
            IpAddress: IPAddress.Parse("192.168.5.101"),
            TimerState: TimerState.Armed,
            CommandId: preparedCommand,
            LastLocalTimestampMicroseconds: null,
            DisconnectObserved: false);

        Assert.IsNull(StartArmSessionGuard.Evaluate(
            "ESP01", frozen, baseSession, preparedCommand));

        StartBlock? conflict = StartArmSessionGuard.Evaluate(
            "ESP01",
            frozen,
            baseSession with { CommandId = 0x9999 },
            preparedCommand);
        Assert.IsNotNull(conflict);
        Assert.AreEqual(StartBlockReason.ParticipantBusy, conflict.Reason);
    }

    [TestMethod]
    public void ArmWindowClosesExactlyAtTwoSecondCutoff()
    {
        const long targetUs = 10_000_000;

        Assert.IsTrue(StartArmWindow.IsOpen(7_999_000, targetUs));
        Assert.IsFalse(StartArmWindow.IsOpen(8_000_000, targetUs));
        Assert.IsFalse(StartArmWindow.IsOpen(8_001_000, targetUs));
    }


    [TestMethod]
    public void FinalBarrierRequiresFreshArmedStatusForPreparedCommand()
    {
        const ulong preparedCommand = 0x1234;
        StartParticipantGateInput frozen = Participant(
            "ESP01", residualUs: 0, effectiveEpochUs: 10_000_000);
        DateTimeOffset barrierStarted = Now;

        ParticipantSessionSnapshot matching = new(
            Generation: frozen.CurrentSessionGeneration,
            LastStatusAtUtc: barrierStarted + TimeSpan.FromMilliseconds(1),
            IpAddress: IPAddress.Parse("192.168.5.101"),
            TimerState: TimerState.Armed,
            CommandId: preparedCommand,
            LastLocalTimestampMicroseconds: null,
            DisconnectObserved: false);

        Assert.IsNull(StartFinalBarrier.EvaluateParticipant(
            "ESP01", frozen, matching, preparedCommand, barrierStarted));

        StartBlock? stale = StartFinalBarrier.EvaluateParticipant(
            "ESP01",
            frozen,
            matching with { LastStatusAtUtc = barrierStarted - TimeSpan.FromMilliseconds(1) },
            preparedCommand,
            barrierStarted);
        Assert.IsNotNull(stale);
        Assert.AreEqual(StartBlockReason.StatusStale, stale.Reason);
    }

    [TestMethod]
    public void FinalBarrierDetectsSameIpRebootFromStatusResetToReadyZero()
    {
        const ulong preparedCommand = 0x1234;
        IPAddress ip = IPAddress.Parse("192.168.5.101");
        var tracker = new ParticipantSessionTracker();

        // Before ARMING, the session is current. After START_AT, STATUS reports the
        // prepared command as ARMED. A reboot then resets firmware RAM to READY/0.
        tracker.ObserveStatus(
            new StatusPacket("ESP01", preparedCommand, TimerState.Armed, 20),
            ip,
            Now);
        long frozenGeneration = tracker.Snapshot.Generation;
        StartParticipantGateInput frozen = Participant(
            "ESP01", residualUs: 0, effectiveEpochUs: 10_000_000) with
        {
            SyncSessionGeneration = frozenGeneration,
            CurrentSessionGeneration = frozenGeneration,
            ReportedIpAddress = ip,
        };

        tracker.ObserveStatus(
            new StatusPacket("ESP01", 0, TimerState.Ready, 20),
            ip,
            Now + TimeSpan.FromMilliseconds(100));

        StartBlock? block = StartFinalBarrier.EvaluateParticipant(
            "ESP01",
            frozen,
            tracker.Snapshot,
            preparedCommand,
            Now + TimeSpan.FromMilliseconds(50));

        Assert.IsNotNull(block);
        Assert.AreEqual(StartBlockReason.SyncInvalidated, block.Reason);
    }

    [TestMethod]
    public void FinalBarrierRejectsWrongCommandEvenWhenDeviceIsArmed()
    {
        const ulong preparedCommand = 0x1234;
        StartParticipantGateInput frozen = Participant(
            "ESP01", residualUs: 0, effectiveEpochUs: 10_000_000);
        ParticipantSessionSnapshot wrong = new(
            Generation: frozen.CurrentSessionGeneration,
            LastStatusAtUtc: Now + TimeSpan.FromMilliseconds(1),
            IpAddress: IPAddress.Parse("192.168.5.101"),
            TimerState: TimerState.Armed,
            CommandId: 0x9999,
            LastLocalTimestampMicroseconds: null,
            DisconnectObserved: false);

        StartBlock? block = StartFinalBarrier.EvaluateParticipant(
            "ESP01", frozen, wrong, preparedCommand, Now);

        Assert.IsNotNull(block);
        Assert.AreEqual(StartBlockReason.ParticipantBusy, block.Reason);
    }

    [TestMethod]
    public void ProductionArmAndAbortWindowsReserveCancellationTime()
    {
        Assert.AreEqual(3_500_000L, StartArmWindow.FinalBarrierStartLeadMicroseconds);
        Assert.AreEqual(2_500_000L, StartArmWindow.NormalArmDeadlineLeadMicroseconds);
        Assert.AreEqual(2_000_000L, StartArmWindow.CutoffLeadMicroseconds);
        Assert.AreEqual(1_000_000L, StartArmWindow.AbortConfirmationDeadlineLeadMicroseconds);
        Assert.AreEqual(1_500_000L, StartArmWindow.AbortConfirmationMaximumWindowMicroseconds);

        const long targetUs = 10_000_000;
        Assert.IsTrue(StartArmWindow.IsNormalArmOpen(7_499_999, targetUs));
        Assert.IsFalse(StartArmWindow.IsNormalArmOpen(7_500_000, targetUs));
        Assert.IsTrue(StartArmWindow.IsOpen(7_999_999, targetUs));
        Assert.IsFalse(StartArmWindow.IsOpen(8_000_000, targetUs));
        Assert.IsTrue(StartArmWindow.IsAbortConfirmationOpen(8_999_999, targetUs));
        Assert.IsFalse(StartArmWindow.IsAbortConfirmationOpen(9_000_000, targetUs));
    }

    [TestMethod]
    public void ReservationIsAtomicForOverlappingParticipantSets()
    {
        var manager = new ParticipantReservationManager();
        Assert.IsTrue(manager.TryReserveAll(["ESP01", "ESP02"], out var first));
        Assert.IsNotNull(first);

        Assert.IsFalse(manager.TryReserveAll(["ESP02", "ESP03"], out var second));
        Assert.IsNull(second);
        CollectionAssert.AreEqual(
            new[] { "ESP02" },
            manager.GetConflicts(["ESP02", "ESP03"]).ToArray());

        // Failure must not partially reserve ESP03.
        Assert.IsTrue(manager.TryReserveAll(["ESP03"], out var third));
        Assert.IsNotNull(third);

        third!.Dispose();
        first!.Dispose();
        Assert.IsTrue(manager.TryReserveAll(["ESP01", "ESP02", "ESP03"], out var afterRelease));
        afterRelease!.Dispose();
    }

    [TestMethod]
    public async Task ConcurrentOverlappingReservationsAllowExactlyOnePrepare()
    {
        var manager = new ParticipantReservationManager();
        using var start = new ManualResetEventSlim(false);

        async Task<ParticipantReservationManager.ParticipantReservation?> TryAsync(string[] ids)
        {
            await Task.Yield();
            start.Wait();
            manager.TryReserveAll(ids, out var reservation);
            return reservation;
        }

        Task<ParticipantReservationManager.ParticipantReservation?> a =
            TryAsync(["ESP01", "ESP02"]);
        Task<ParticipantReservationManager.ParticipantReservation?> b =
            TryAsync(["ESP02", "ESP03"]);
        start.Set();

        ParticipantReservationManager.ParticipantReservation?[] results =
            await Task.WhenAll(a, b);
        Assert.AreEqual(1, results.Count(result => result is not null));

        foreach (var reservation in results) reservation?.Dispose();
    }

    [TestMethod]
    public void SessionTrackerInvalidatesOnIpChangeReconnectAndLocalTimestampRollback()
    {
        var tracker = new ParticipantSessionTracker();
        var ready = new StatusPacket("ESP01", 0, TimerState.Ready, 0);
        tracker.ObserveStatus(ready, IPAddress.Parse("192.168.5.101"), Now);
        long initial = tracker.Snapshot.Generation;

        tracker.ObserveLocalTimestamp(1_000_000);
        tracker.ObserveLocalTimestamp(900_000);
        long afterRollback = tracker.Snapshot.Generation;
        Assert.AreEqual(initial + 1, afterRollback);

        tracker.ObserveStatus(ready, IPAddress.Parse("192.168.5.102"), Now + TimeSpan.FromSeconds(1));
        long afterIpChange = tracker.Snapshot.Generation;
        Assert.AreEqual(afterRollback + 1, afterIpChange);

        tracker.MarkDisconnectedObserved();
        tracker.ObserveStatus(ready, IPAddress.Parse("192.168.5.102"), Now + TimeSpan.FromSeconds(2));
        Assert.AreEqual(afterIpChange + 1, tracker.Snapshot.Generation);
    }

    [TestMethod]
    public void IntentionalStatusPauseDoesNotCreateFalseReconnect()
    {
        var tracker = new ParticipantSessionTracker();
        var ready = new StatusPacket("ESP01", 0, TimerState.Ready, 0);
        tracker.ObserveStatus(ready, IPAddress.Parse("192.168.5.101"), Now);
        long generation = tracker.Snapshot.Generation;

        tracker.BeginIntentionalStatusPause();
        tracker.ObserveConnectivity(
            Now + TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(6),
            monitoringEnabled: true);
        tracker.ObserveStatus(
            ready,
            IPAddress.Parse("192.168.5.101"),
            Now + TimeSpan.FromSeconds(31));

        Assert.AreEqual(generation, tracker.Snapshot.Generation);
    }

    [TestMethod]
    public void StatusCommandResetToZeroInvalidatesKnownPriorCommandSession()
    {
        var tracker = new ParticipantSessionTracker();
        IPAddress ip = IPAddress.Parse("192.168.5.101");
        tracker.ObserveStatus(
            new StatusPacket("ESP01", 0xABC, TimerState.Finished, 0),
            ip,
            Now);
        long generation = tracker.Snapshot.Generation;

        tracker.ObserveStatus(
            new StatusPacket("ESP01", 0, TimerState.Ready, 0),
            ip,
            Now + TimeSpan.FromSeconds(1));

        Assert.AreEqual(generation + 1, tracker.Snapshot.Generation);
    }

    [TestMethod]
    public void ArmAckTrackerCountsAcceptedAndDuplicateAsArmedAndSurfacesRejection()
    {
        const ulong commandId = 0x1234;
        var tracker = new ArmAcknowledgementTracker(commandId, ["ESP01", "ESP02", "ESP03"]);

        tracker.Observe(new AckPacket("ESP01", commandId, CommandType.StartAt, AckResult.Accepted));
        tracker.Observe(new AckPacket("ESP02", commandId, CommandType.StartAt, AckResult.Duplicate));
        tracker.Observe(new AckPacket("ESP03", commandId, CommandType.StartAt, AckResult.NotSynced));

        Assert.IsFalse(tracker.AllArmed);
        CollectionAssert.AreEquivalent(new[] { "ESP03" }, tracker.MissingParticipants.ToArray());
        Assert.AreEqual(AckResult.NotSynced, tracker.Rejections["ESP03"]);
    }

    [TestMethod]
    public void ResetAckTrackerRequiresMatchingResetCommandAndParticipant()
    {
        const ulong resetCommandId = 0xCAFE;
        var tracker = new ResetAcknowledgementTracker(resetCommandId, ["ESP01", "ESP02"]);

        tracker.Observe(new AckPacket("ESP01", resetCommandId, CommandType.StartAt, AckResult.Accepted));
        tracker.Observe(new AckPacket("ESP01", 0xBEEF, CommandType.Reset, AckResult.Accepted));
        Assert.IsFalse(tracker.AllAcknowledged);
        CollectionAssert.AreEquivalent(new[] { "ESP01", "ESP02" }, tracker.MissingParticipants.ToArray());

        tracker.Observe(new AckPacket("ESP01", resetCommandId, CommandType.Reset, AckResult.Accepted));
        tracker.Observe(new AckPacket("ESP02", resetCommandId, CommandType.Reset, AckResult.Duplicate));
        Assert.IsTrue(tracker.AllAcknowledged);
        Assert.AreEqual(0, tracker.MissingParticipants.Count);
    }

    [TestMethod]
    public void AbortConfirmationRequiresFreshReadyStatusWithResetIdOrRebootZero()
    {
        const ulong preparedCommandId = 0x1234;
        const ulong resetCommandId = 0x5678;
        DateTimeOffset started = Now;
        IPAddress ip = IPAddress.Parse("192.168.5.101");

        ParticipantSessionSnapshot resetReady = new(
            Generation: 7,
            LastStatusAtUtc: started + TimeSpan.FromMilliseconds(1),
            IpAddress: ip,
            TimerState: TimerState.Ready,
            CommandId: resetCommandId,
            LastLocalTimestampMicroseconds: null,
            DisconnectObserved: false);
        Assert.IsTrue(StartAbortConfirmation.IsParticipantCancellationConfirmed(
            resetReady, preparedCommandId, resetCommandId, started));

        ParticipantSessionSnapshot rebootReady = resetReady with { CommandId = 0, Generation = 8 };
        Assert.IsTrue(StartAbortConfirmation.IsParticipantCancellationConfirmed(
            rebootReady, preparedCommandId, resetCommandId, started));

        Assert.IsFalse(StartAbortConfirmation.IsParticipantCancellationConfirmed(
            resetReady with { TimerState = TimerState.Armed, CommandId = preparedCommandId },
            preparedCommandId,
            resetCommandId,
            started));
        Assert.IsFalse(StartAbortConfirmation.IsParticipantCancellationConfirmed(
            resetReady with { LastStatusAtUtc = started - TimeSpan.FromMilliseconds(1) },
            preparedCommandId,
            resetCommandId,
            started));
        Assert.IsFalse(StartAbortConfirmation.IsParticipantCancellationConfirmed(
            resetReady with { CommandId = 0x9999 },
            preparedCommandId,
            resetCommandId,
            started));
    }

    private static StartParticipantGateInput Participant(
        string id,
        long residualUs,
        long effectiveEpochUs) =>
        new(
            id,
            InRoster: true,
            Online: true,
            LastStatusAtUtc: Now - TimeSpan.FromSeconds(1),
            SyncAccepted: true,
            ResidualErrorMicroseconds: residualUs,
            EffectiveSyncEpochMasterMicroseconds: effectiveEpochUs,
            SyncSessionGeneration: 7,
            CurrentSessionGeneration: 7,
            ReportedIpAddress: IPAddress.Parse("192.168.5.101"),
            ReportedState: TimerState.Ready,
            ReportedCommandId: 0);
}
