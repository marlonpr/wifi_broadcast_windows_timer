using FactoryTimer.Controller;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;
namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class LogoRepetitionTests
{
    private static RepetitionProgram Plan(uint d=20,uint n=3,uint cap=6000) =>
        RepetitionProgram.Create(1,d,n,["ESP01","ESP03","ESP04"],cap,4);

    [TestMethod]
    public void CountdownAndFourSecondLogoArePackedFromTheSameEpoch()
    {
        var p=Plan();p.Validate();
        Assert.AreEqual(72u,p.Segments.Single().Duration);
        Assert.AreEqual(68u,p.Segments.Single().CountdownSpan);
        Assert.AreEqual(4u,p.Segments.Single().Command(1,3,99,5_000_000).LogoSeconds);
        var clock=new ControllerClock();clock.ArmSegmentAt(20,3,5_000_000,1,3,4);
        foreach (var (phase,repeat,remaining,logo,finished) in new (long,uint,uint,bool,bool)[] {
            (0,1,20,false,false),(19,1,1,false,false),(20,1,0,true,false),(23,1,0,true,false),
            (24,2,20,false,false),(44,2,0,true,false),(48,3,20,false,false),
            (68,3,0,true,false),(71,3,0,true,false),(72,3,0,false,true) })
        {
            clock.UpdateAt(5_000_000+phase*1_000_000);
            Assert.AreEqual(repeat,clock.CurrentRepeat);Assert.AreEqual(remaining,clock.RemainingSeconds);
            Assert.AreEqual(logo,clock.IsLogoPhase);Assert.AreEqual(finished,clock.State==TimerState.Finished);
        }
    }

    [TestMethod]
    public void CapIncludesInternalGapsAndAllowsTheFinalLogoAfterTheLastCountdown()
    {
        var p=Plan(180,10,600);
        CollectionAssert.AreEqual(new uint[]{552,552,552,184},p.Segments.Select(s=>s.Duration).ToArray());
        Assert.IsTrue(p.Segments.All(s=>s.CountdownSpan<=600));
        Assert.AreEqual(2,Plan(3000,2).Segments.Count);
        Assert.AreEqual(6003u,Plan(5999,1).Segments.Single().Duration);
        Assert.AreEqual(5999u,Plan(5999,1).Segments.Single().CountdownSpan);
    }

    [TestMethod]
    public void StopInLogoSavesTheNextCountdownAndFinalLogoHasNothingToResume()
    {
        var p=Plan();var s=p.Segments.Single();
        Assert.AreEqual(new CountdownPosition(2,20),CountdownPosition.WithFollowingRepeats(p,s,20_000_000));
        Assert.AreEqual(new CountdownPosition(2,20),CountdownPosition.WithFollowingRepeats(p,s,23_999_999));
        Assert.AreEqual(new CountdownPosition(3,0),CountdownPosition.WithFollowingRepeats(p,s,70_000_000));
        var resumed=RepetitionProgram.ContinueFrom(2,p,new(1,15));
        Assert.AreEqual(4u,resumed.LogoSeconds);
        Assert.AreEqual(19u,resumed.Segments[0].Duration);
        Assert.AreEqual(48u,resumed.Segments[1].Duration);
    }

    [TestMethod]
    public void LogoGapAndFinalCompletionMustMatchThePersistedCommand()
    {
        var p=Plan();var coordinator=new SegmentedProgramCoordinator(new Store(),ProgramExecutionState.New(p));
        ProgramDeviceObservation Idle(string id)=>new(new(id,1,42,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129,LogoSeconds:0),
            new(id,0,TimerState.Ready,20,RtcState:RtcDisciplineState.Locked,RtcFitPoints:129,RtcFitRmsMicroseconds:0.5,
                RtcQueueDrops:0,RtcTemperatureValid:true,RtcAcceptedEdges:1000,RtcInferredMissingEdges:0,
                RtcHoldoverEntries:0,FirmwareBuildId:"12345678",DiagnosticFlags:0));
        coordinator.BeginPreparation();coordinator.RecordArm(99,5_000_000,"session",p.Participants.Select(Idle).ToArray());
        coordinator.MarkRunning();
        Dictionary<string,ProgramDeviceObservation> At(uint seconds,TimerState timer)=>p.Participants.ToDictionary(id=>id,id=>{
            var x=Idle(id);return x with { Execution=x.Execution with { PlanId=1,SegmentIndex=1,CommandId=99,State=timer,
                RepeatCount=3,TotalRepeats=3,CurrentRepeat=3,LogoSeconds=4,ElapsedSegmentSeconds=seconds,
                ElapsedSegmentMicroseconds=seconds*1_000_000L,TargetMasterMicroseconds=5_000_000,TStarLocalMicroseconds=6_000_000 },
                Health=x.Health with { CommandId=99,State=timer,RemainingSeconds=0 } };
        });
        Assert.AreEqual(ReconcileAction.ObserveActiveSegment,coordinator.Reconcile(At(71,TimerState.Running)).Action);
        Assert.AreEqual(0u,coordinator.State.CompletedRepeatCount);
        var other=new SegmentedProgramCoordinator(new Store(),coordinator.State);
        var wrong=At(71,TimerState.Running);wrong["ESP03"]=wrong["ESP03"] with { Execution=wrong["ESP03"].Execution with { LogoSeconds=0 } };
        Assert.AreEqual(ReconcileAction.Hold,other.Reconcile(wrong).Action);
        Assert.AreEqual(ReconcileAction.Complete,coordinator.Reconcile(At(72,TimerState.Finished)).Action);
        Assert.AreEqual(3u,coordinator.State.CompletedRepeatCount);
    }

    [TestMethod]
    public void LogoScheduleSurvivesJournalReloadAndLegacyPlansStayUnchanged()
    {
        string dir=Path.Combine(Path.GetTempPath(),"factory-logo-"+Guid.NewGuid());
        try {var store=new FileProgramStateStore(Path.Combine(dir,"program.json"));
            store.Save(ProgramExecutionState.New(Plan()));
            var read=store.Load()!;read.Program.Validate();Assert.AreEqual(72u,read.Program.Segments.Single().Duration);
            Assert.AreEqual(4u,read.Program.LogoSeconds);
            Assert.AreEqual(60u,RepetitionProgram.Create(2,20,3,["ESP01"]).Segments.Single().Duration);
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    private sealed class Store:IProgramStateStore {public ProgramExecutionState? Load()=>null;public void Save(ProgramExecutionState state){} }
}
