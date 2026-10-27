// StaleRunningAfterOutage.cs - regression replay for stale RUNNING responses (base core 6.23.9).
// Run as Program.cs of a net10.0 console project referencing FactoryTimer.Controller.Core.csproj.
//
// Scenario: 99:59 segment. ESP05 loses Wi-Fi 10 min in and is offline at 00:00.
// Prior firmware 6.24.4 stopped updating the countdown while disconnected;
// STATUS_REQUEST could then return the frozen snapshot on reconnect.
// If the controller's probe reaches ESP05 in its first ~50 ms back online, the reply
// still says RUNNING with 10 min elapsed.
//
// Unsafe behavior from the original 6.23.9 core:
//   00:00, ESP05 missing      -> WaitForReadiness | WaitingForReadiness
//   +20 s, ESP05 stale RUNNING -> ObserveActiveSegment RemainingSeconds=5399 | Running
// ProductionPrograms then waits RemainingSeconds + 1 s (PauseForRecoveredSegmentAsync)
// before probing again, with the rest of the line idle at 00:00.
// Expected after the fix: a same-session board reporting RUNNING after the planned
// segment end is re-probed within seconds, inside the 10 s boundary window.

using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;
string[] ids = ["ESP02", "ESP03", "ESP05"];
RepetitionProgram plan = RepetitionProgram.Create(0x1111, 5999, 2, ids, 6000);
ProgramDeviceObservation Idle(string id) => new(
    new(id, 0x789, 0x456, 0, 0, 0, TimerState.Ready, 5999, 1, 1, 1, 1, 0, 0, 0, 129),
    new(id, 0, TimerState.Ready, 5999, RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129, RtcFitRmsMicroseconds: 0.5,
        RtcQueueDrops: 0, RtcTemperatureValid: true, RtcAcceptedEdges: 9000, RtcInferredMissingEdges: 0,
        RtcHoldoverEntries: 0, FirmwareBuildId: "4a75c500", DiagnosticFlags: 0));
ProgramDeviceObservation At(string id, ProgramExecutionState s, TimerState st, long elapsedUs) {
    var i = Idle(id); var seg = s.CurrentSegment!;
    return new(i.Execution with { PlanId = plan.PlanId, SegmentIndex = seg.Index, CommandId = s.CurrentCommandId, State = st,
        DurationSeconds = seg.DurationSeconds, RepeatCount = seg.RepeatCount, FirstRepeat = seg.FirstRepeat, TotalRepeats = plan.TotalRepeats,
        CurrentRepeat = seg.FirstRepeat, ElapsedSegmentSeconds = (uint)(elapsedUs / 1_000_000), ElapsedSegmentMicroseconds = elapsedUs,
        TargetMasterMicroseconds = s.TargetMasterMicroseconds, TStarLocalMicroseconds = 6_000_000 },
        i.Health with { CommandId = s.CurrentCommandId, State = st, RemainingSeconds = st == TimerState.Finished ? 0u : (uint)(5999 - elapsedUs / 1_000_000) });
}
int checks = 0;
void Check(bool valid, string why)
{
    if (!valid) throw new InvalidOperationException("FAIL: " + why);
    checks++;
    Console.WriteLine("PASS: " + why);
}
SegmentedProgramCoordinator NewCoordinator()
{
    var c = new SegmentedProgramCoordinator(new Mem(), ProgramExecutionState.New(plan));
    c.BeginPreparation();
    c.RecordArm(0xAB, 1_000_000_000, "s1", ids.Select(Idle).ToArray());
    c.MarkRunning();
    return c;
}

var t0 = new DateTimeOffset(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);
{
    var c = NewCoordinator();
    var obs = ids.ToDictionary(x => x, x => At(x, c.State, TimerState.Finished, 5999L * 1_000_000));
    obs.Remove("ESP05");
    Check(c.Reconcile(obs, t0, plannedSegmentEnded: true).Action == ReconcileAction.WaitForReadiness,
        "one missing participant waits at boundary");
    obs["ESP05"] = At("ESP05", c.State, TimerState.Running, 600L * 1_000_000);
    var stale = c.Reconcile(obs, t0.AddSeconds(2), plannedSegmentEnded: true);
    Check(stale.Action == ReconcileAction.WaitForReadiness && !stale.RemainingSeconds.HasValue,
        "stale RUNNING after deadline never schedules remaining 5399 seconds");
    Check(c.State.Stage == ProgramStage.WaitingForReadiness && c.State.CompletedRepeatCount == 0,
        "no false completion; wait remains bounded");
    obs["ESP05"] = At("ESP05", c.State, TimerState.Finished, 5999L * 1_000_000);
    Check(c.Reconcile(obs, t0.AddSeconds(5), plannedSegmentEnded: true).Action == ReconcileAction.SynchronizeNextSegment,
        "exact FINISHED is accepted after stale response");
    Check(c.State.CompletedRepeatCount == 1 && c.State.NextSegmentIndex == 2,
        "original repetition credited only once");
}
{
    var c = NewCoordinator();
    var obs = ids.ToDictionary(x => x, x => At(x, c.State, TimerState.Finished, 5999L * 1_000_000));
    obs["ESP05"] = At("ESP05", c.State, TimerState.Running, 600L * 1_000_000);
    Check(c.Reconcile(obs, t0, plannedSegmentEnded: true).Action == ReconcileAction.WaitForReadiness,
        "stale RUNNING begins 10-second bound");
    var held = c.Reconcile(obs, t0.AddSeconds(10), plannedSegmentEnded: true);
    Check(held.Action == ReconcileAction.Hold && c.State.Stage == ProgramStage.Held,
        "stale RUNNING that never clears enters HOLD after 10 seconds");
    Check(c.State.CompletedRepeatCount == 0,
        "no false repetition credited on timeout");
}
{
    var c = NewCoordinator();
    var obs = ids.ToDictionary(x => x, x => At(x, c.State, TimerState.Running, 600L * 1_000_000));
    var running = c.Reconcile(obs, t0, plannedSegmentEnded: false);
    Check(running.Action == ReconcileAction.ObserveActiveSegment && running.RemainingSeconds == 5399,
        "a genuinely active, pre-deadline segment still has silent-window remaining time");
}
Console.WriteLine($"STALE_RUNNING_REGRESSION=PASS CHECKS={checks}");
sealed class Mem : IProgramStateStore
{
    public ProgramExecutionState? Last;
    public ProgramExecutionState? Load() => Last;
    public void Save(ProgramExecutionState s) => Last = s;
}
