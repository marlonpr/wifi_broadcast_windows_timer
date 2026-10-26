using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class ProgramRecoveryAuditReadinessTests
{
    private static ProgramDeviceObservation Ready(string deviceId = "ESP03") => new(
        new(deviceId, 0x789, 0x456, 0, 0, 0xABC, TimerState.Finished,
            40, 1, 1, 1, 1, 40, 40_000_000, 0, 129),
        new(deviceId, 0xABC, TimerState.Finished, 0,
            RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129,
            RtcFitRmsMicroseconds: 0.5, RtcQueueDrops: 0,
            RtcTemperatureValid: true, RtcAcceptedEdges: 300,
            FirmwareBuildId: "929292ee", DiagnosticFlags: 0));

    [TestMethod]
    public void OperationalReadinessDiagnosticMatchesStartSafetyPredicate()
    {
        var healthy = Ready();
        Assert.IsTrue(SegmentedProgramCoordinator.OperationalReady(healthy));
        Assert.AreEqual("READY", ProductionRecoveryPolicy.OperationalReadinessReason(healthy));
        Assert.AreEqual("STATUS/EXEC missing", ProductionRecoveryPolicy.OperationalReadinessReason(null));

        var cases = new (ProgramDeviceObservation Sample, string Fragment)[]
        {
            (healthy with { Health = healthy.Health with { RtcFitRmsMicroseconds = 3.001 } }, "3.001"),
            (healthy with { Health = healthy.Health with { RtcFitPoints = 127 } }, "fit points"),
            (healthy with { Health = healthy.Health with { RtcState = RtcDisciplineState.Unknown } }, "RTC state"),
            (healthy with { Health = healthy.Health with { RtcTemperatureValid = false } }, "temperature"),
            (healthy with { Health = healthy.Health with { DiagnosticFlags = 1 } }, "DiagnosticFlags"),
            (healthy with { Execution = healthy.Execution with { State = TimerState.Running },
                Health = healthy.Health with { State = TimerState.Running } }, "not idle"),
            (healthy with { Health = healthy.Health with { RtcAcceptedEdges = null } }, "accepted edges")
        };
        foreach ((ProgramDeviceObservation sample, string fragment) in cases)
        {
            Assert.IsFalse(SegmentedProgramCoordinator.OperationalReady(sample));
            string reason = ProductionRecoveryPolicy.OperationalReadinessReason(sample);
            Assert.AreNotEqual("READY", reason);
            StringAssert.Contains(reason, fragment);
        }
    }

    [TestMethod]
    public void RefusedResumeIdentifiesDeviceAndRmsWithoutChangingHeldProgress()
    {
        RepetitionProgram plan = RepetitionProgram.Create(0xABC123, 40, 2, ["ESP03"], 40);
        var store = new VolatileStore();
        var coordinator = new SegmentedProgramCoordinator(store, ProgramExecutionState.New(plan));
        coordinator.Hold("Test HOLD for RTC recovery");
        var observation = Ready();
        var bad = observation with { Health = observation.Health with { RtcFitRmsMicroseconds = 3.90 } };
        try
        {
            coordinator.ResumeAfterOperatorReview(new Dictionary<string, ProgramDeviceObservation>
            {
                ["ESP03"] = bad
            });
            Assert.Fail("Over-limit RTC RMS unexpectedly accepted");
        }
        catch (InvalidOperationException error)
        {
            StringAssert.Contains(error.Message, "ESP03");
            StringAssert.Contains(error.Message, "3.900");
        }
        Assert.AreEqual(ProgramStage.Held, coordinator.State.Stage);
        Assert.AreEqual(0u, coordinator.State.CompletedRepeatCount);
        Assert.AreEqual(1u, coordinator.State.NextSegmentIndex);
    }

    private sealed class VolatileStore : IProgramStateStore
    {
        public ProgramExecutionState? Load() => null;
        public void Save(ProgramExecutionState value) { }
    }
}
