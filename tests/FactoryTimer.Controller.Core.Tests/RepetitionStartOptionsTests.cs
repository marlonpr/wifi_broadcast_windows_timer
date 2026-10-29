using FactoryTimer.Controller;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class RepetitionStartOptionsTests
{
    private static RepetitionProgram Plan(uint duration=20,uint count=3,uint cap=6000,bool sync=false,uint start=4) =>
        RepetitionProgram.Create(1,duration,count,["ESP01","ESP03","ESP04"],cap,4,start,sync);

    [TestMethod]
    public void BothCheckboxesOffPreserveTheWorkingSchedule()
    {
        var old=Plan(start:0);old.Validate();
        Assert.AreEqual(72u,old.Segments.Single().Duration);
        Assert.IsFalse(old.SyncEveryRepetition);
        Assert.AreEqual(0u,old.RepeatStartSeconds);
        Assert.AreEqual(60u,RepetitionProgram.Create(2,20,3,["ESP01"]).Segments.Single().Duration);
    }

    [TestMethod]
    public void LocalStartLogoDoesNotConsumeCountdownSeconds()
    {
        var p=Plan();p.Validate();var s=p.Segments.Single();
        Assert.AreEqual(80u,s.Duration);Assert.AreEqual(76u,s.CountdownSpan);
        Assert.AreEqual(4u,s.Command(1,3,99,5_000_000).RepeatStartSeconds);
        var clock=new ControllerClock();clock.ArmSegmentAt(20,3,5_000_000,1,3,4,4);
        foreach(var (phase,repeat,remaining,logo,finished) in new (long,uint,uint,bool,bool)[]{
            (0,1,20,false,false),(19,1,1,false,false),(20,1,0,true,false),(23,1,0,true,false),
            (24,2,0,true,false),(27,2,0,true,false),(28,2,20,false,false),(47,2,1,false,false),
            (48,2,0,true,false),(52,3,0,true,false),(56,3,20,false,false),(75,3,1,false,false),
            (76,3,0,true,false),(79,3,0,true,false),(80,3,0,false,true)})
        {
            clock.UpdateAt(5_000_000+phase*1_000_000);
            Assert.AreEqual(repeat,clock.CurrentRepeat,$"repeat at {phase}");
            Assert.AreEqual(remaining,clock.RemainingSeconds,$"remaining at {phase}");
            Assert.AreEqual(logo,clock.IsLogoPhase);Assert.AreEqual(finished,clock.State==TimerState.Finished);
        }
    }

    [TestMethod]
    public void PlannerCountsBothInternalLogosAndPreservesMaximumDuration()
    {
        CollectionAssert.AreEqual(new uint[]{560,560,560,184},Plan(180,10,600).Segments.Select(s=>s.Duration).ToArray());
        Assert.AreEqual(2u,Plan(2996,2).Segments.Single().RepeatCount);
        Assert.AreEqual(2,Plan(2997,2).Segments.Count);
        Assert.AreEqual(6003u,Plan(5999,1).Segments.Single().Duration);
        Assert.AreEqual(24u,Plan(count:1).Segments.Single().Duration); // first signal already happens before T*
        Assert.AreEqual(386u,Plan(5,30).Segments.Single().Duration);
    }

    [TestMethod]
    public void EveryRepeatSyncAlwaysUsesOneRepeatPerSegmentWithoutAnExtraStartGap()
    {
        foreach(uint start in new uint[]{0,4}){
            var p=Plan(sync:true,start:start);p.Validate();
            Assert.AreEqual(3,p.Segments.Count);
            CollectionAssert.AreEqual(new uint[]{1,2,3},p.Segments.Select(s=>s.FirstRepeat).ToArray());
            Assert.IsTrue(p.Segments.All(s=>s.RepeatCount==1 && s.Duration==24));
            Assert.AreEqual(start,p.Segments[0].RepeatStartSeconds);
        }
        var maximum=Plan(5999,2,10,true);maximum.Validate(); // disabled limit is ignored
        Assert.IsTrue(maximum.Segments.All(s=>s.Duration==6003));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>Plan(5999,2,10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>Plan(count:10001,sync:true));
    }

    [TestMethod]
    public void StopInEitherLogoPreservesTheSameNextRepeat()
    {
        var p=Plan();var s=p.Segments.Single();
        foreach(long phase in new[]{20_000_000L,23_999_999L,24_000_000L,27_999_999L})
            Assert.AreEqual(new CountdownPosition(2,20),CountdownPosition.WithFollowingRepeats(p,s,phase));
        Assert.AreEqual(new CountdownPosition(2,20),CountdownPosition.WithFollowingRepeats(p,s,28_000_000));
        Assert.AreEqual(new CountdownPosition(3,20),CountdownPosition.WithFollowingRepeats(p,s,55_999_999));
        Assert.AreEqual(new CountdownPosition(3,0),CountdownPosition.WithFollowingRepeats(p,s,76_000_000));
        var resumed=RepetitionProgram.ContinueFrom(2,p,new(1,15),["ESP02"]);resumed.Validate();
        Assert.AreEqual(4u,resumed.RepeatStartSeconds);Assert.AreEqual(19u,resumed.Segments[0].Duration);
        Assert.AreEqual(52u,resumed.Segments[1].Duration);
        Assert.AreEqual("ESP02",resumed.Participants.Single());
        var each=RepetitionProgram.ContinueFrom(3,Plan(sync:true),new(1,15));each.Validate();
        Assert.IsTrue(each.SyncEveryRepetition && each.Segments.All(x=>x.RepeatCount==1));
    }

    [TestMethod]
    public void OptionsSurviveJournalReloadSelectionChangeAndContinuation()
    {
        string dir=Path.Combine(Path.GetTempPath(),"factory-repeat-options-"+Guid.NewGuid());
        try{
            var store=new FileProgramStateStore(Path.Combine(dir,"program.json"));
            var p=Plan(sync:true);store.Save(ProgramExecutionState.New(p));
            var read=store.Load()!.Program;read.Validate();
            Assert.IsTrue(read.SyncEveryRepetition);Assert.AreEqual(4u,read.RepeatStartSeconds);
            var selected=read.WithParticipants(["ESP02"]);selected.Validate();
            Assert.IsTrue(selected.SyncEveryRepetition);Assert.AreEqual(4u,selected.RepeatStartSeconds);
            var invalid=read with { SyncEveryRepetition=false };
            Assert.ThrowsExactly<InvalidDataException>(()=>invalid.Validate());
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }

    [TestMethod]
    public void RecoveryRejectsAChangedStartSignalAndWaitsForTheFullFinalLogo()
    {
        var p=Plan();var coordinator=new SegmentedProgramCoordinator(new Store(),ProgramExecutionState.New(p));
        ProgramDeviceObservation Idle(string id)=>new(new(id,1,42,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129,LogoSeconds:0,RepeatStartSeconds:0),
            new(id,0,TimerState.Ready,20,RtcState:RtcDisciplineState.Locked,RtcFitPoints:129,RtcFitRmsMicroseconds:0.5,
                RtcQueueDrops:0,RtcTemperatureValid:true,RtcAcceptedEdges:1000,RtcInferredMissingEdges:0,
                RtcHoldoverEntries:0,FirmwareBuildId:"12345678",DiagnosticFlags:0));
        coordinator.BeginPreparation();coordinator.RecordArm(99,5_000_000,"session",p.Participants.Select(Idle).ToArray());coordinator.MarkRunning();
        Dictionary<string,ProgramDeviceObservation> At(uint seconds,TimerState state)=>p.Participants.ToDictionary(id=>id,id=>{
            var x=Idle(id);return x with {Execution=x.Execution with {PlanId=1,SegmentIndex=1,CommandId=99,State=state,RepeatCount=3,TotalRepeats=3,
                CurrentRepeat=3,LogoSeconds=4,RepeatStartSeconds=4,ElapsedSegmentSeconds=seconds,ElapsedSegmentMicroseconds=seconds*1_000_000L,
                TargetMasterMicroseconds=5_000_000,TStarLocalMicroseconds=6_000_000},Health=x.Health with {CommandId=99,State=state,RemainingSeconds=0}};
        });
        Assert.AreEqual(ReconcileAction.ObserveActiveSegment,coordinator.Reconcile(At(79,TimerState.Running)).Action);
        var altered=At(79,TimerState.Running);altered["ESP03"]=altered["ESP03"] with {Execution=altered["ESP03"].Execution with {RepeatStartSeconds=0}};
        var other=new SegmentedProgramCoordinator(new Store(),coordinator.State);
        Assert.AreEqual(ReconcileAction.Hold,other.Reconcile(altered).Action);
        Assert.AreEqual(ReconcileAction.Complete,coordinator.Reconcile(At(80,TimerState.Finished)).Action);
    }
    [TestMethod]
    public void EveryRepeatRequiresNewPreparationAndAConfirmedPriorCompletion()
    {
        var p=Plan(sync:true);var c=new SegmentedProgramCoordinator(new Store(),ProgramExecutionState.New(p));
        var observations=p.Participants.ToDictionary(id=>id,id=>new ProgramDeviceObservation(
            new(id,1,42,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129,LogoSeconds:0,RepeatStartSeconds:0),
            new(id,0,TimerState.Ready,20,RtcState:RtcDisciplineState.Locked,RtcFitPoints:129,RtcFitRmsMicroseconds:0.5,
                RtcQueueDrops:0,RtcTemperatureValid:true,RtcAcceptedEdges:1000,RtcInferredMissingEdges:0,
                RtcHoldoverEntries:0,FirmwareBuildId:"12345678",DiagnosticFlags:0)));
        foreach(var s in p.Segments){
            Assert.AreEqual(s.Index,c.State.NextSegmentIndex);
            c.BeginPreparation();Assert.AreEqual(ProgramStage.Preparing,c.State.Stage);
            ulong command=100+s.Index;long target=s.Index*30_000_000L;
            c.RecordArm(command,target,"session",observations.Values.ToArray());c.MarkRunning();
            Assert.ThrowsExactly<InvalidOperationException>(()=>c.BeginPreparation());
            observations=observations.ToDictionary(x=>x.Key,x=>x.Value with{
                Execution=x.Value.Execution with{PlanId=1,SegmentIndex=s.Index,CommandId=command,State=TimerState.Finished,
                    RepeatCount=1,FirstRepeat=s.FirstRepeat,TotalRepeats=3,CurrentRepeat=s.LastRepeat,LogoSeconds=4,RepeatStartSeconds=4,
                    ElapsedSegmentSeconds=24,ElapsedSegmentMicroseconds=24_000_000,TargetMasterMicroseconds=target,TStarLocalMicroseconds=target+1_000_000},
                Health=x.Value.Health with{CommandId=command,State=TimerState.Finished,RemainingSeconds=0}});
            Assert.AreEqual(s.Index==3?ReconcileAction.Complete:ReconcileAction.SynchronizeNextSegment,c.Reconcile(observations).Action);
            Assert.AreEqual(s.Index,c.State.CompletedRepeatCount);
        }
    }
    private sealed class Store:IProgramStateStore{public ProgramExecutionState? Load()=>null;public void Save(ProgramExecutionState state){}}
}
