using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class ProductionRecoveryTimeoutTests
{
    private static readonly string[] Devices = ["ESP02", "ESP03", "ESP05"];
    private static ProgramDeviceObservation Idle(string id) => new(
        new(id, 1, 42, 0, 0, 0, TimerState.Ready, 20, 1, 1, 1, 1, 0, 0, 0, 129),
        new(id, 0, TimerState.Ready, 20, RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129,
            RtcFitRmsMicroseconds: 0.5, RtcQueueDrops: 0, RtcTemperatureValid: true,
            RtcAcceptedEdges: 1000, RtcInferredMissingEdges: 0, RtcHoldoverEntries: 0,
            FirmwareBuildId: "12345678", DiagnosticFlags: 0));

    private static (SegmentedProgramCoordinator Coordinator, Dictionary<string, ProgramDeviceObservation> Finished) Running()
    {
        var plan = RepetitionProgram.Create(1, 20, 3, Devices);
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(plan));
        coordinator.BeginPreparation();
        coordinator.RecordArm(99, 5_000_000, "session", Devices.Select(Idle).ToArray());
        coordinator.MarkRunning();
        var finished = Devices.ToDictionary(id => id, id => {
            var x = Idle(id);
            return x with { Execution = x.Execution with { PlanId = 1, SegmentIndex = 1, CommandId = 99,
                State = TimerState.Finished, RepeatCount = 3, TotalRepeats = 3, CurrentRepeat = 3,
                ElapsedSegmentSeconds = 60, ElapsedSegmentMicroseconds = 60_000_000,
                TargetMasterMicroseconds = 5_000_000, TStarLocalMicroseconds = 6_000_000 },
                Health = x.Health with { CommandId = 99, State = TimerState.Finished, RemainingSeconds = 0 } };
        });
        return (coordinator, finished);
    }

    [TestMethod]
    public void MissingRtcAndStaleRunningAllHoldAtTenSecondsWithoutAdvancing()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), ProductionRecoveryPolicy.ReadinessTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(1), ProductionRecoveryPolicy.PollInterval);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        foreach (string fault in new[] { "missing", "temperature", "stale-running" })
        {
            var (coordinator, observations) = Running();
            if (fault == "missing") observations.Remove("ESP03");
            else if (fault == "temperature") observations["ESP03"] = observations["ESP03"] with {
                Health = observations["ESP03"].Health with { RtcTemperatureValid = false } };
            else observations["ESP03"] = observations["ESP03"] with {
                Execution = observations["ESP03"].Execution with { State = TimerState.Running, ElapsedSegmentSeconds = 15,
                    ElapsedSegmentMicroseconds = 15_000_000, CurrentRepeat = 1 },
                Health = observations["ESP03"].Health with { State = TimerState.Running, RemainingSeconds = 5 } };
            Assert.AreEqual(ReconcileAction.WaitForReadiness, coordinator.Reconcile(observations, start, true).Action, fault);
            Assert.AreEqual(ReconcileAction.WaitForReadiness, coordinator.Reconcile(observations, start.AddMilliseconds(9999), true).Action, fault);
            Assert.AreEqual(ReconcileAction.Hold, coordinator.Reconcile(observations, start.AddSeconds(10), true).Action, fault);
            Assert.AreEqual(0u, coordinator.State.CompletedRepeatCount, fault);
            Assert.IsTrue(coordinator.State.Message.Contains("10 s", StringComparison.Ordinal), fault);
        }
    }

    [TestMethod]
    public void RecoveredFleetBeforeTimeoutCompletesAndResetCannotResumeFinishedProgress()
    {
        var (coordinator, finished) = Running();
        var missing = new Dictionary<string, ProgramDeviceObservation>(finished); missing.Remove("ESP03");
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Assert.AreEqual(ReconcileAction.WaitForReadiness, coordinator.Reconcile(missing, start, true).Action);
        Assert.AreEqual(ReconcileAction.Complete, coordinator.Reconcile(finished, start.AddSeconds(9), true).Action);
        Assert.AreEqual(3u, coordinator.State.CompletedRepeatCount);
        coordinator.ResetProgress(101);
        Assert.AreEqual(ProgramStage.Stopped, coordinator.State.Stage);
        Assert.IsTrue(coordinator.State.StopCheckpoint!.DiscardProgress);
        Assert.IsFalse(coordinator.State.StopCheckpoint.CanResumePosition);
        Assert.AreEqual(20u, coordinator.State.StopCheckpoint.RemainingSeconds);
        Assert.AreEqual(0ul, coordinator.State.CurrentCommandId);
        CollectionAssert.AreEqual(Devices, coordinator.State.StopCheckpoint.PendingDeviceIds.ToArray());
    }

    private sealed class Store : IProgramStateStore
    {
        public ProgramExecutionState? Load() => null;
        public void Save(ProgramExecutionState state) { }
    }
}
