using System.Net;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class DiagnosticBuildGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const ulong CommandId = 0x1234;

    private static StartParticipantGateInput Participant(string id, byte? flags = 0) => new(
        id, InRoster: true, Online: true, LastStatusAtUtc: Now, SyncAccepted: true,
        ResidualErrorMicroseconds: 0, EffectiveSyncEpochMasterMicroseconds: 10_000_000,
        SyncSessionGeneration: 1, CurrentSessionGeneration: 1,
        ReportedIpAddress: IPAddress.Loopback, ReportedState: TimerState.Ready,
        ReportedCommandId: 0, ReportedDiagnosticFlags: flags);

    private static ParticipantSessionSnapshot Armed(byte? flags) => new(
        Generation: 1, LastStatusAtUtc: Now, IpAddress: IPAddress.Loopback,
        TimerState: TimerState.Armed, CommandId: CommandId,
        LastLocalTimestampMicroseconds: null, DisconnectObserved: false,
        DiagnosticFlags: flags);

    [TestMethod]
    public void ProductionFleetPassesAllBuildGuards()
    {
        StartParticipantGateInput[] fleet = [Participant("ESP01"), Participant("ESP03"), Participant("ESP04")];
        Assert.IsTrue(StartReadinessGate.Evaluate(fleet, Now, 15_000_000).Accepted);
        foreach (StartParticipantGateInput participant in fleet)
        {
            Assert.IsNull(DiagnosticBuildGuard.Evaluate(participant.DeviceId, 0));
            Assert.IsNull(StartArmSessionGuard.Evaluate(participant.DeviceId, participant, Armed(0), CommandId));
            Assert.IsNull(StartFinalBarrier.EvaluateParticipant(participant.DeviceId, participant, Armed(0), CommandId, Now));
        }
    }

    [TestMethod]
    public void EveryDiagnosticBitAndUnknownBitsBlockBeforeSynchronizationAndAtFinalBarrier()
    {
        foreach (byte flags in new byte[] { 1, 2, 4, 8, 16, 31, 128, 255 })
        {
            StartParticipantGateInput participant = Participant("ESP01", flags) with { SyncAccepted = false };
            StartGateResult gate = StartReadinessGate.Evaluate(
                [participant, Participant("ESP03"), Participant("ESP04")], Now, 15_000_000);
            Assert.IsFalse(gate.Accepted);
            StartBlock block = gate.Blocks.Single();
            Assert.AreEqual(StartBlockReason.DiagnosticFirmware, block.Reason);
            Assert.AreEqual("ESP01", block.DeviceId);
            Assert.AreEqual(StartBlockReason.DiagnosticFirmware,
                StartReadinessGate.Evaluate([participant with { ReportedState = TimerState.Running }],
                    Now, 15_000_000).Blocks.Single().Reason);
            StringAssert.Contains(block.Remedy, $"DiagnosticFlags=0x{flags:X2}");
            Assert.AreEqual(StartBlockReason.DiagnosticFirmware,
                StartArmSessionGuard.Evaluate("ESP01", Participant("ESP01"), Armed(flags), CommandId)!.Reason);
            Assert.AreEqual(StartBlockReason.DiagnosticFirmware,
                StartFinalBarrier.EvaluateParticipant("ESP01", Participant("ESP01"), Armed(flags), CommandId, Now)!.Reason);
        }
        StringAssert.Contains(DiagnosticBuildGuard.Evaluate("ESP01", 4)!.Remedy, "START_TASK_DELAY_TEST");
        StringAssert.Contains(DiagnosticBuildGuard.Describe(128), "UNKNOWN(0x80)");
    }

    [TestMethod]
    public void MissingFlagsCannotQualifyLegacyFirmware()
    {
        StartParticipantGateInput participant = Participant("ESP01", null);
        Assert.AreEqual(StartBlockReason.DiagnosticFlagsUnavailable,
            StartReadinessGate.Evaluate([participant], Now, 15_000_000).Blocks.Single().Reason);
        Assert.AreEqual(StartBlockReason.DiagnosticFlagsUnavailable,
            StartArmSessionGuard.Evaluate("ESP01", participant, Armed(null), CommandId)!.Reason);
        Assert.AreEqual(StartBlockReason.DiagnosticFlagsUnavailable,
            StartFinalBarrier.EvaluateParticipant("ESP01", participant, Armed(null), CommandId, Now)!.Reason);
    }

    [TestMethod]
    public void TrackerUsesTheLatestFlagsAndClearsThemForLegacyStatusOrResetSession()
    {
        var tracker = new ParticipantSessionTracker();
        var production = new StatusPacket("ESP01", CommandId, TimerState.Armed, 30,
            RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129,
            RtcFitRmsMicroseconds: 0.5, RtcQueueDrops: 0, RtcTemperatureValid: true,
            HealthFlags: 7, Cpu0MonitorWorstTask: "wifi", DiagnosticFlags: 0);
        tracker.ObserveStatus(production, IPAddress.Loopback, Now);
        Assert.AreEqual((byte)0, tracker.Snapshot.DiagnosticFlags);
        tracker.ObserveStatus(production with { DiagnosticFlags = 2 }, IPAddress.Loopback, Now);
        Assert.AreEqual((byte)2, tracker.Snapshot.DiagnosticFlags);
        StringAssert.Contains(DiagnosticBuildGuard.Evaluate("ESP01", tracker.Snapshot.DiagnosticFlags)!.Remedy, "ISR_CANARY_ENABLED");
        tracker.ObserveStatus(production with { DiagnosticFlags = null }, IPAddress.Loopback, Now);
        Assert.IsNull(tracker.Snapshot.DiagnosticFlags);
        tracker.ObserveStatus(production, IPAddress.Loopback, Now);
        tracker.InvalidateControllerSession();
        Assert.IsNull(tracker.Snapshot.DiagnosticFlags);
    }
}
