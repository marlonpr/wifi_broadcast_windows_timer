// ResumeFaultScenarios - adapted from user-supplied v6.23.6 replay.
// Asserted regression tests for controller-only v6.23.8 candidate.
// A: bounded boundary WAIT + all same-boot FINISHED => advance once.
// B: ARM unconfirmed and no FINISHED => refuse unsafe replay.
// C: partial ARM, one FINISHED while others RESET => refuse unsafe credit.
// D: fully confirmed pre-T* RESET => same segment can be safely retried.

using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

string[] ids = ["ESP02", "ESP03", "ESP05"];
RepetitionProgram plan = RepetitionProgram.Create(0x6D503475DF7B7772, 180, 2, ids, 180);

ProgramDeviceObservation Idle(string id, ulong command = 0, TimerState st = TimerState.Ready) => new(
    new(id, 0x789, 0x456, 0, 0, command, st, 180, 1, 1, 1, 1, 0, 0, 0, 129),
    new(id, command, st, 180, RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129, RtcFitRmsMicroseconds: 0.5,
        RtcQueueDrops: 0, RtcTemperatureValid: true, RtcAcceptedEdges: 1900, RtcInferredMissingEdges: 0,
        RtcHoldoverEntries: 0, FirmwareBuildId: "a9a3c280", DiagnosticFlags: 0));
ProgramDeviceObservation At(string id, ProgramExecutionState s, TimerState st) {
    var i = Idle(id); var seg = s.CurrentSegment!;
    long phase = st == TimerState.Finished ? seg.DurationSeconds * 1_000_000L : 5_000_000;
    return new(i.Execution with { PlanId = plan.PlanId, SegmentIndex = seg.Index, CommandId = s.CurrentCommandId, State = st,
        DurationSeconds = seg.DurationSeconds, RepeatCount = seg.RepeatCount, FirstRepeat = seg.FirstRepeat, TotalRepeats = plan.TotalRepeats,
        CurrentRepeat = seg.FirstRepeat, ElapsedSegmentSeconds = (uint)(phase / 1_000_000), ElapsedSegmentMicroseconds = phase,
        TargetMasterMicroseconds = s.TargetMasterMicroseconds, TStarLocalMicroseconds = 6_000_000 },
        i.Health with { CommandId = s.CurrentCommandId, State = st, RemainingSeconds = st == TimerState.Finished ? 0u : 175u });
}
Dictionary<string, ProgramDeviceObservation> All(Func<string, ProgramDeviceObservation> f) => ids.ToDictionary(x => x, f);
int checks = 0;
void Check(bool yes, string description) {
    if (!yes) throw new Exception("FAIL: " + description);
    checks++; Console.WriteLine("  PASS: " + description);
}
void Reject(Action action, string description) {
    try { action(); }
    catch (InvalidOperationException) { Check(true, description); return; }
    throw new Exception("FAIL: unexpectedly accepted: " + description);
}

SegmentedProgramCoordinator SegmentOneDone() {
    var c = new SegmentedProgramCoordinator(new Mem(), ProgramExecutionState.New(plan));
    c.BeginPreparation(); c.RecordArm(0xE42F, 913_425_714, "s1", All(x => Idle(x)).Values.ToArray()); c.MarkRunning();
    c.Reconcile(All(x => At(x, c.State, TimerState.Finished)));
    return c;
}

Console.WriteLine("A. ESP05 temporarily absent at boundary; returns FINISHED with unchanged boot");
{
    var c = new SegmentedProgramCoordinator(new Mem(), ProgramExecutionState.New(plan));
    c.BeginPreparation(); c.RecordArm(0xE42F, 913_425_714, "s1", All(x => Idle(x)).Values.ToArray()); c.MarkRunning();
    var fin = All(x => At(x, c.State, TimerState.Finished));
    var missing = new Dictionary<string, ProgramDeviceObservation>(fin); missing.Remove("ESP05");
    Check(c.Reconcile(missing).Action == ReconcileAction.WaitForReadiness,
        "A: missing board starts bounded WAIT, not premature START");
    Check(c.Reconcile(fin).Action == ReconcileAction.SynchronizeNextSegment,
        "A: all three exact FINISHED allow automatic next-segment preparation");
    Check(c.State.NextSegmentIndex == 2 && c.State.CompletedRepeatCount == 1 &&
        c.State.AutomaticRecoveryCounts!.GetValueOrDefault("ESP05") == 1,
        "A: advance once and record ESP05 auto recovery");
}
Console.WriteLine("B. Segment-2 ARM abort unconfirmed; ESP05 never received START_AT; all boards idle afterwards");
{
    var c = SegmentOneDone(); var seg1 = c.State.CurrentCommandId;
    c.BeginPreparation(); c.RecordArm(0x21, 1_277_964_738, "s1", All(x => At2(x, seg1)).Values.ToArray());
    c.Hold("ARM abort unconfirmed; no automatic retry. Reconcile the frozen boards before resuming.");
    var fresh = new Dictionary<string, ProgramDeviceObservation> {
        ["ESP02"] = Idle("ESP02", 0x31), ["ESP03"] = Idle("ESP03", 0x31), ["ESP05"] = At2("ESP05", seg1) };
    Check(c.Reconcile(fresh).Action == ReconcileAction.Hold, "B: non-execution not inferred from old command");
    Reject(() => c.ResumeAfterOperatorReview(fresh), "B: unconfirmed ARM cannot be safely replayed");
    Check(c.State.NextSegmentIndex == 2 && c.State.CompletedRepeatCount == 1,
        "B: prior repeat preserved");
}
Console.WriteLine("C. Segment-2 ARM abort unconfirmed; ESP05 did receive START_AT and runs alone; ESP02/03 were RESET");
{
    var c = SegmentOneDone(); var seg1 = c.State.CurrentCommandId;
    c.BeginPreparation(); c.RecordArm(0x41, 1_277_964_738, "s1", All(x => At2(x, seg1)).Values.ToArray());
    c.Hold("ARM abort unconfirmed; no automatic retry. Reconcile the frozen boards before resuming.");
    var running = new Dictionary<string, ProgramDeviceObservation> {
        ["ESP02"] = Idle("ESP02", 0x51), ["ESP03"] = Idle("ESP03", 0x51), ["ESP05"] = At("ESP05", c.State, TimerState.Running) };
    Check(c.Reconcile(running).Action == ReconcileAction.ObserveActiveSegment,
        "C: running board observed without retransmitting START");
    running["ESP05"] = At("ESP05", c.State, TimerState.Finished);
    Check(c.Reconcile(running).Action == ReconcileAction.Hold,
        "C: partial completion remains HELD");
    Reject(() => c.ResumeAfterOperatorReview(running),
        "C: ESP05 FINISHED alone cannot credit ESP02/ESP03");
    Check(c.State.NextSegmentIndex == 2 && c.State.CompletedRepeatCount == 1,
        "C: false program completion prevented");
}

Console.WriteLine("D. Confirmed cancellation of segment 2 before T* permits safe retry");
{
    var c = SegmentOneDone(); var seg1 = c.State.CurrentCommandId;
    c.BeginPreparation(); c.RecordArm(0x61, 1_277_964_738, "s1", All(x => At2(x, seg1)).Values.ToArray());
    const ulong resetId = 0x62;
    var proofs = All(x => Idle(x, resetId));
    c.ConfirmArmAbortForRetry(resetId, 1_277_000_000, proofs);
    Check(c.State.Stage == ProgramStage.BetweenSegments && c.State.CurrentCommandId == 0 &&
        c.State.NextSegmentIndex == 2 && c.State.CompletedRepeatCount == 1,
        "D: safe pre-T* reset retains repetition 2 as next to run");
}
Console.WriteLine($"RESUME_FAULT_SCENARIOS=PASS CHECKS={checks}");

// Segment-1 FINISHED evidence for a board, used as the idle state before segment 2.
ProgramDeviceObservation At2(string id, ulong seg1Command) {
    var i = Idle(id, seg1Command, TimerState.Finished);
    return new(i.Execution with { PlanId = plan.PlanId, SegmentIndex = 1, DurationSeconds = 180, RepeatCount = 1, FirstRepeat = 1,
        TotalRepeats = 2, ElapsedSegmentSeconds = 180, ElapsedSegmentMicroseconds = 180_000_000, TargetMasterMicroseconds = 913_425_714 },
        i.Health with { RemainingSeconds = 0 });
}
sealed class Mem : IProgramStateStore { public ProgramExecutionState? Last; public ProgramExecutionState? Load() => Last; public void Save(ProgramExecutionState s) => Last = s; }
