using FactoryTimer.Controller;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
void Reject(Action action, string name) {
    bool rejected = false;
    try { action(); } catch (Exception) { rejected = true; }
    Check(rejected, name);
}
RepetitionProgram Plan(uint d, uint n, uint cap = 6000) => RepetitionProgram.Create(0x123, d, n, ["ESP01", "ESP03", "ESP04"], cap);
foreach (var (duration, repeats, cap, counts) in new (uint,uint,uint,uint[])[] {
    (20,3,6000,[3]), (1800,5,6000,[3,2]), (3000,2,6000,[2]),
    (5999,2,6000,[1,1]), (180,10,600,[3,3,3,1]), (5,30,6000,[30]) })
    Check(Plan(duration,repeats,cap).Segments.Select(s => s.RepeatCount).SequenceEqual(counts), "canonical whole-repeat plan");
Reject(() => Plan(6000,1), "99:59 maximum");
Reject(() => Plan(5999,2,600), "cannot split a repetition");
RepetitionProgram plan = Plan(5999,2);
ProgramDeviceObservation Idle(string id, ulong boot = 0x456) => new(
    new(id,0x789,boot,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129),
    new(id,0,TimerState.Ready,20,RtcState:RtcDisciplineState.Locked,RtcFitPoints:128,
        RtcFitRmsMicroseconds:0.9,RtcQueueDrops:1,RtcTemperatureValid:true,RtcAcceptedEdges:1000,
        RtcInferredMissingEdges:0,RtcHoldoverEntries:0,FirmwareBuildId:"12345678",DiagnosticFlags:0));
Dictionary<string,ProgramDeviceObservation> IdleFleet() => plan.Participants.ToDictionary(x=>x,x=>Idle(x));
ProgramDeviceObservation At(string id, ProgramExecutionState state, TimerState timer, long phase) {
    var idle = Idle(id);var seg = state.CurrentSegment!;
    return new(idle.Execution with { PlanId=plan.PlanId,SegmentIndex=seg.Index,CommandId=state.CurrentCommandId,
        State=timer,DurationSeconds=seg.DurationSeconds,RepeatCount=seg.RepeatCount,FirstRepeat=seg.FirstRepeat,
        TotalRepeats=plan.TotalRepeats,CurrentRepeat=seg.FirstRepeat+(uint)Math.Min(Math.Max(0,phase)/1_000_000/seg.DurationSeconds,seg.RepeatCount-1),
        ElapsedSegmentSeconds=(uint)(Math.Max(0,phase)/1_000_000),ElapsedSegmentMicroseconds=phase,
        TargetMasterMicroseconds=state.TargetMasterMicroseconds,TStarLocalMicroseconds=6000000 },
        idle.Health with { CommandId=state.CurrentCommandId,State=timer,RemainingSeconds=timer==TimerState.Finished?0u:seg.DurationSeconds });
}
var store = new MemoryStore();
var coordinator = new SegmentedProgramCoordinator(store,ProgramExecutionState.New(plan));
coordinator.BeginPreparation();
coordinator.RecordArm(11,9000000,"session1",IdleFleet().Values.ToArray());
Check(store.Last!.Stage==ProgramStage.Arming && store.Last.CurrentCommandId==11,"ARM intent durable before send");
ProgramExecutionState armed = coordinator.State;
var active = plan.Participants.ToDictionary(x=>x,x=>At(x,armed,TimerState.Running,5000500));
var restarted = new SegmentedProgramCoordinator(store,store.Last!);
Check(restarted.Reconcile(active).Action==ReconcileAction.ObserveActiveSegment,"restart observes, never repeats START");
Check(restarted.State.CurrentCommandId==11,"recovery preserves command");
var missing = new Dictionary<string,ProgramDeviceObservation>(active);missing.Remove("ESP03");
Check(restarted.Reconcile(missing).Action==ReconcileAction.ObserveActiveSegment,"healthy continue with missing board");
var finished = plan.Participants.ToDictionary(x=>x,x=>At(x,armed,TimerState.Finished,5999L*1_000_000));
Check(restarted.Reconcile(finished).Action==ReconcileAction.SynchronizeNextSegment,"transient loss does not invent reboot");
Check(restarted.State.NextSegmentIndex==2,"completion advances once");
var rebooted = new SegmentedProgramCoordinator(store,armed);
active["ESP03"]=Idle("ESP03",0x457);
Check(rebooted.Reconcile(active).Action==ReconcileAction.ObserveActiveSegment,"healthy continue after reboot");
finished["ESP03"]=Idle("ESP03",0x457);
Check(rebooted.Reconcile(finished).Action==ReconcileAction.Hold,"reboot holds next segment");
rebooted.ResumeAfterOperatorReview(finished);
Check(rebooted.State.NextSegmentIndex==2 && rebooted.State.BootIds["ESP03"]==0x457,"explicit review skips failed board repeats, no replay");
var lost = new SegmentedProgramCoordinator(store,armed);
finished.Remove("ESP03");
Check(lost.Reconcile(finished).Action==ReconcileAction.Hold,"missing required participant holds fleet");
Reject(()=>lost.ResumeAfterOperatorReview(finished),"missing participant cannot resume");
var foreign = new SegmentedProgramCoordinator(store,armed);
active["ESP03"]=At("ESP03",armed,TimerState.Running,1) with { Execution=At("ESP03",armed,TimerState.Running,1).Execution with { CommandId=99 } };
Check(foreign.Reconcile(active).Action==ReconcileAction.Hold,"foreign active command never duplicated");
var tempFault = new SegmentedProgramCoordinator(store,armed);
finished = plan.Participants.ToDictionary(x=>x,x=>At(x,armed,TimerState.Finished,5999L*1_000_000));
finished["ESP03"] = finished["ESP03"] with { Health=finished["ESP03"].Health with { RtcTemperatureValid=false } };
var waitStarted=DateTimeOffset.UtcNow;
Check(tempFault.Reconcile(finished,waitStarted).Action==ReconcileAction.WaitForReadiness && tempFault.State.NextSegmentIndex==1,"post-run temperature failure waits before advancing");
Check(tempFault.Reconcile(finished,waitStarted.AddSeconds(179)).Action==ReconcileAction.WaitForReadiness,"recoverable fault waits for full180s");
Check(tempFault.Reconcile(finished,waitStarted.AddSeconds(180)).Action==ReconcileAction.Hold,"180s boundary wait is bounded");
var recoveredTemp = new SegmentedProgramCoordinator(store,armed);
Check(recoveredTemp.Reconcile(finished,waitStarted).Action==ReconcileAction.WaitForReadiness,"temperature wait enters recovery");
finished["ESP03"]=finished["ESP03"] with { Health=finished["ESP03"].Health with { RtcTemperatureValid=true } };
Check(recoveredTemp.Reconcile(finished,waitStarted.AddSeconds(10)).Action==ReconcileAction.SynchronizeNextSegment,"temperature recovery resumes automatically");
var recoveredRtc = new SegmentedProgramCoordinator(store,armed);
var notRecovered=finished.ToDictionary(x=>x.Key,x=>x.Value with { Execution=x.Value.Execution with { RtcRecoveredEdges=128 } });
Check(recoveredRtc.Reconcile(notRecovered,waitStarted).Action==ReconcileAction.WaitForReadiness,"SQW recovery waits at segment end");
Check(recoveredRtc.Reconcile(finished,waitStarted.AddSeconds(129)).Action==ReconcileAction.SynchronizeNextSegment,"full recovery window advances without operator resume");
var cancelled = new SegmentedProgramCoordinator(store,ProgramExecutionState.New(plan));
Dictionary<string,ProgramDeviceObservation> ResetProof(ulong reset) => IdleFleet().ToDictionary(x=>x.Key,x=>new ProgramDeviceObservation(
    x.Value.Execution with { CommandId=reset }, x.Value.Health with { CommandId=reset }));
for(int attempt=1;attempt<=3;attempt++) {
    cancelled.BeginPreparation();long retryTarget=9000000+attempt*10000000L;
    cancelled.RecordArm((ulong)(20+attempt),retryTarget,"session1",IdleFleet().Values.ToArray());
    var proof=ResetProof((ulong)(30+attempt));
    var partialProof=new Dictionary<string,ProgramDeviceObservation>(proof);partialProof.Remove("ESP03");
    Reject(()=>cancelled.ConfirmArmAbortForRetry((ulong)(30+attempt),retryTarget-500000,partialProof),"WiFi loss without cancellation proof cannot retry");
    Reject(()=>cancelled.ConfirmArmAbortForRetry((ulong)(30+attempt),retryTarget,proof),"post-Tstar RESET cannot justify replay");
    cancelled.ConfirmArmAbortForRetry((ulong)(30+attempt),retryTarget-500000,proof);
    Check(cancelled.State.CurrentCommandId==0 && cancelled.State.NextSegmentIndex==1 && cancelled.State.Program.TotalRepeats==2,"confirmed ARM abort preserves whole repetition plan");
    Check(ProductionRecoveryPolicy.CanRetry(cancelled.State.SegmentStartAttempts,true)==(attempt<3),"initial plus two retries, then hold");
}
Reject(()=>cancelled.BeginPreparation(),"restarting controller does not grant a fourth automatic attempt");
cancelled.Hold("retry budget exhausted");cancelled.ResumeAfterOperatorReview(ResetProof(33));
Check(cancelled.State.SegmentStartAttempts==0 && cancelled.State.NextSegmentIndex==1,"explicit resume retains count and resets retry budget");
var noArm = new SegmentedProgramCoordinator(store,ProgramExecutionState.New(plan));
noArm.BeginPreparation();noArm.CancelPreparationForRetry();Check(noArm.State.CurrentCommandId==0&&noArm.State.NextSegmentIndex==1,"sync/preflight failures retry without advancing repeats");
Check(!ProductionRecoveryPolicy.CanRetry(1,false),"unconfirmed ARM abort always holds");
Check(SegmentedProgramCoordinator.OperationalReady(Idle("ESP01")),"recovered historical queue drop and fit128 allowed");
Check(!SegmentedProgramCoordinator.OperationalReady(Idle("ESP01") with { Execution=Idle("ESP01").Execution with { RtcRecoveredEdges=128 } }),"one short recovery window rejected");
Check(!SegmentedProgramCoordinator.OperationalReady(Idle("ESP01") with { Health=Idle("ESP01").Health with { DiagnosticFlags=4 } }),"diagnostic firmware rejected");
var failing = new MemoryStore();var unsafeArm = new SegmentedProgramCoordinator(failing,ProgramExecutionState.New(plan));
unsafeArm.BeginPreparation();failing.Fail=true;
Reject(()=>unsafeArm.RecordArm(11,9000000,"session1",IdleFleet().Values.ToArray()),"disk failure blocks ARM");
Check(unsafeArm.State.CurrentCommandId==0 && unsafeArm.State.Stage==ProgramStage.Preparing,"no in-memory ARM on failed persistence");
string directory=Path.Combine(Path.GetTempPath(),"factory_program_"+Guid.NewGuid());Directory.CreateDirectory(directory);
try {
    string journal=Path.Combine(directory,"program.json");var file=new FileProgramStateStore(journal);
    file.Save(armed);Check(file.Load()!.CurrentCommandId==11,"journal round trip");
    File.WriteAllText(journal,File.ReadAllText(journal).Replace("9000000","9000001"));
    Reject(()=>file.Load(),"journal tamper holds");
} finally { Directory.Delete(directory,true); }
Check(ProductionTimingBudget.StartAllowanceUs(6000)==13000,"30ms budget includes6000s drift and presentation reserve");
Check(ProductionTimingBudget.StartAllowanceUs(6000,0.0833)==13000,"small phase/rate warnings do not change the qualified timing reserve");
Check(ProductionTimingBudget.StartAllowanceUs(6000,3)==7000,"measured worst pair rate reduces start allowance");
Check(ProductionTimingBudget.StartAllowanceUs(6000,5)==0,"only timing budget exhaustion blocks for rate");
Reject(()=>ProductionTimingBudget.StartAllowanceUs(6000,double.NaN),"invalid measured rate rejected");
coordinator.RecordMeasuredPairRate(3);Check(coordinator.State.MeasuredPairRatePpm==3,"measured pair rate persists independently of median warnings");
string statusMaximum=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"status_v62319_max_width.txt")).Trim();
Check(statusMaximum.Length==511&&FactoryProtocol.TryParseInbound(statusMaximum,out _,out _),"native maximum-width STATUS remains compatible");
string wire="FCT2|CMD|START_AT|000000000000000B|5|9000000|0000000000000123|1|30|1|30|0000000000000456|0000000000000000";
Check(FactoryProtocol.TryParseCommand(wire,out var command,out _)&&FactoryProtocol.SerializeCommand(command!)==wire,"native firmware segment fixture roundtrip");
string exec="FCT2|EXEC_STATUS|ESP01|0000000000000789|0000000000000456|0000000000000123|1|000000000000000B|RUNNING|5|30|1|30|2|5|9000000|6000000|129|5000500";
Check(FactoryProtocol.TryParseInbound(exec,out var packet,out _)&&packet is ExecutionStatusPacket e&&e.ElapsedSegmentMicroseconds==5000500&&e.CurrentRepeat==2,"actual native EXEC fixture parses");
Check(!FactoryProtocol.TryParseInbound(exec.Replace("|129|5000500","|129|4000500"),out _,out _),"inconsistent phase rejected");
Check(!FactoryProtocol.TryParseCommand(wire+"|0",out _,out _),"partial/extra segment fields rejected");
Check(FactoryProtocol.TryParseInbound("FCT2|SYNC_REPLY|ESP01|0000000000000001|1|2|3|0|2|0|02:12:34:56:78:AB",out var sync,out _)&&sync is SyncReplyPacket sr&&sr.Bssid=="02:12:34:56:78:AB","sync BSSID propagates");
var clock=new ControllerClock();clock.ArmSegmentAt(5,30,9000000,1,30);
clock.UpdateAt(14000000);Check(clock.State==TimerState.Running&&clock.RemainingSeconds==5&&clock.CurrentRepeat==2,"controller seamless repeat");
clock.UpdateAt(159000000);Check(clock.State==TimerState.Finished&&clock.RemainingSeconds==0,"controller nD end");
var tracker=new DeviceStatusTracker();var now=DateTimeOffset.UtcNow;tracker.BeginSearching(now);tracker.RecordValidStatus(now);
tracker.BeginIntentionalStatusPause();Check(tracker.GetAvailability(now.AddSeconds(6000))==DeviceAvailability.Online,"silent6000s stays online");
tracker.EndIntentionalStatusPause(now.AddSeconds(6000));Check(tracker.GetAvailability(now.AddSeconds(6047))==DeviceAvailability.Online,"48s resume grace");
Check(tracker.GetAvailability(now.AddSeconds(6048))==DeviceAvailability.Offline,"grace expires without fabricating fresh STATUS");
var drift=new SegmentDriftMonitor();
RtcSegmentDriftPoint[] Points(long master,long extra) => [new("ESP01",1,"build",master,master+extra,"ap1"),new("ESP03",1,"build",master,master,"ap2"),new("ESP04",1,"build",master,master,"ap3")];
drift.Observe(Points(1,0));var rates=drift.Observe(Points(5_400_000_001,154));
Check(Math.Abs(rates.Single(x=>x.DeviceId=="ESP01").RelativeFleetPpm-154d/5400)<1e-8,"154us/90min drift");
Check(drift.Observe(Points(10_800_000_001,1000)).Single(x=>x.DeviceId=="ESP01").ValidationRequired,"500us discontinuity requires validation");
drift.Reset();Check(drift.Observe(Points(1,0)).Count==0,"new controller clock discards old drift basis");
// A mid-program step must stop latching innovation and reach sustained classification.
drift.Reset();drift.Observe(Points(1,0));drift.Observe(Points(540_000_001,0));
Check(drift.Observe(Points(1_080_000_001,810)).Single(x=>x.DeviceId=="ESP01").ValidationRequired,"first 1.5ppm step innovation");
Check(drift.Observe(Points(1_620_000_001,1620)).Single(x=>x.DeviceId=="ESP01").ValidationRequired,"second stable step remains visible and rebaselines");
var settledStep=drift.Observe(Points(2_160_000_001,2430)).Single(x=>x.DeviceId=="ESP01");
Check(!settledStep.ValidationRequired&&!settledStep.SustainedDeviation,"new reference clears innovation; sustained streak progresses");
Check(drift.Observe(Points(2_700_000_001,3240)).Single(x=>x.DeviceId=="ESP01").SustainedDeviation,"mid-program step reaches three confirmed sustained estimates");
drift.Reset();drift.Observe(Points(1,0));drift.Observe(Points(6_000_000_001,0));
drift.Observe(Points(12_000_000_001,600));drift.Observe(Points(18_000_000_001,1200));
var smallStep=drift.Observe(Points(24_000_000_001,1800)).Single(x=>x.DeviceId=="ESP01");
Check(!smallStep.ValidationRequired&&!smallStep.SustainedDeviation,"0.1ppm step at6000s rebaselines without sustained warning");
drift.Reset();drift.Observe(Points(1,0));drift.Observe(Points(540_000_001,0));
drift.Observe(Points(1_080_000_001,810));drift.Observe(Points(1_620_000_001,2430));
Check(drift.Observe(Points(2_160_000_001,3240)).Single(x=>x.DeviceId=="ESP01").ValidationRequired,"alternating 1.5/3ppm estimates do not confirm new reference");
var differentBoot=Points(2_700_000_001,4050);differentBoot[0]=differentBoot[0] with { BootId=2 };
Check(!drift.Observe(differentBoot).Any(x=>x.DeviceId=="ESP01"),"reboot clears pending candidate and drift reference");
// Last fleet measurement persists into a new plan, without reusing its ARM identities.
var priorMeasured=armed with { MeasuredPairRatePpm=3, Stage=ProgramStage.Completed };
var inherited=ProgramExecutionState.New(Plan(3000,2),priorMeasured);
Check(inherited.MeasuredPairRatePpm is null&&inherited.BootIds.Count==0&&inherited.FleetRate is not null,"new plan retains candidate but never old ARM identity");
var inheritedCore=new SegmentedProgramCoordinator(new MemoryStore(),inherited);
inheritedCore.ResolveFleetRateEvidence(IdleFleet().Values.Reverse().ToArray());
Check(inheritedCore.State.MeasuredPairRatePpm==3&&ProductionTimingBudget.StartAllowanceUs(6000,inheritedCore.State.MeasuredPairRatePpm)==7000,"same fresh fleet activates measured first-segment budget independent of order");
foreach(string change in new[]{"boot","build","roster","duplicate","missing"}) {
    var sample=IdleFleet().Values.ToList();
    if(change=="boot") sample[0]=sample[0] with { Execution=sample[0].Execution with { BootId=0x999 } };
    if(change=="build") sample[0]=sample[0] with { Health=sample[0].Health with { FirmwareBuildId="different" } };
    if(change=="roster") sample[0]=Idle("ESP05");
    if(change=="duplicate") sample[0]=sample[1];
    if(change=="missing") sample.RemoveAt(0);
    var invalidEvidence=new SegmentedProgramCoordinator(new MemoryStore(),inherited);
    invalidEvidence.ResolveFleetRateEvidence(sample);
    Check(invalidEvidence.State.MeasuredPairRatePpm is null&&invalidEvidence.State.FleetRate is null,"changed "+change+" discards inherited evidence");
}
string evidenceDirectory=Path.Combine(Path.GetTempPath(),"factory-rate-"+Guid.NewGuid());Directory.CreateDirectory(evidenceDirectory);
try {
    var evidenceStore=new FileProgramStateStore(Path.Combine(evidenceDirectory,"program.json"));
    evidenceStore.Save(inherited);var restoredEvidence=new SegmentedProgramCoordinator(evidenceStore,evidenceStore.Load()!);
    restoredEvidence.ResolveFleetRateEvidence(IdleFleet().Values.ToArray());
    Check(restoredEvidence.State.MeasuredPairRatePpm==3,"candidate survives controller restart before fresh pre-arm validation");
    Check(ProgramExecutionState.New(Plan(20,3),restoredEvidence.State).FleetRate!.PairRatePpm==3,"spread survives multiple consecutive programs");
} finally { Directory.Delete(evidenceDirectory,true); }
Console.WriteLine($"PRODUCTION_SMOKE=PASS ASSERTIONS={checks}");

sealed class MemoryStore : IProgramStateStore {
    public ProgramExecutionState? Last;public bool Fail;
    public ProgramExecutionState? Load()=>Last;
    public void Save(ProgramExecutionState state){if(Fail)throw new IOException("injected disk failure");Last=state;}
}
