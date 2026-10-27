using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryTimer.Controller;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class StoppedCountdownContinuationTests
{
    private sealed class Store : IProgramStateStore
    {
        public ProgramExecutionState? Value;
        public ProgramExecutionState? Load() => Value;
        public void Save(ProgramExecutionState state) => Value = state;
    }

    private static RepetitionProgram Plan(uint duration = 180, uint repeats = 30, uint cap = 6000) =>
        RepetitionProgram.Create(99, duration, repeats, ["ESP01", "ESP02"], cap);
    private static ProgramDeviceObservation ResetReady(string id, ulong command) => new(
        new(id, 17, 42, 0, 0, command, TimerState.Ready, 180, 1, 1, 1, 1, 0, 0, 0, 129),
        new(id, command, TimerState.Ready, 180, FirmwareBuildId: "12345678"));

    [TestMethod]
    public void PartialRepeatResumesThenReturnsToOriginalDurationAndNumbering()
    {
        var resumed = RepetitionProgram.ContinueFrom(100, Plan(), new(3, 170));
        resumed.Validate();
        Assert.AreEqual(180u, resumed.DurationSeconds);
        Assert.AreEqual(30u, resumed.TotalRepeats);
        Assert.AreEqual(new ProgramSegment(1, 3, 1, 170), resumed.Segments[0]);
        Assert.AreEqual(new ProgramSegment(2, 4, 27, 180), resumed.Segments[1]);
        Assert.AreEqual(5030u, (uint)resumed.Segments.Sum(x => x.Duration));
        Assert.AreEqual(2u, ProgramExecutionState.New(resumed).CompletedRepeatCount);
        Assert.IsTrue(resumed.Segments.All(x => x.Duration <= 6000));
    }

    [TestMethod]
    public void RepeatAndSegmentEdgesHaveNoAddedSecondOrSkippedRepeat()
    {
        var segment = new ProgramSegment(1, 1, 30, 5);
        Assert.AreEqual(new CountdownPosition(1, 1), CountdownPosition.AtPhase(segment, 4_999_999));
        Assert.AreEqual(new CountdownPosition(2, 5), CountdownPosition.AtPhase(segment, 5_000_000));
        Assert.AreEqual(new CountdownPosition(30, 0), CountdownPosition.AtPhase(segment, 150_000_000));
        Assert.AreEqual(new CountdownPosition(1, 5), CountdownPosition.AtPhase(segment, -500));
        var program = Plan(180, 10, 600);
        Assert.AreEqual(new CountdownPosition(4, 180), CountdownPosition.WithFollowingRepeats(program, program.Segments[0], 540_000_000));
        var state = ProgramExecutionState.New(program) with { Stage = ProgramStage.BetweenSegments, NextSegmentIndex = 2 };
        Assert.AreEqual(new CountdownPosition(4, 180), CountdownPosition.Capture(state, 1, "new-session"));
    }

    [TestMethod]
    public void RepeatedPausesInShortenedFirstRepeatPreserveOriginalProgram()
    {
        var first = RepetitionProgram.ContinueFrom(100, Plan(), new(3, 170));
        var position = CountdownPosition.WithFollowingRepeats(first, first.Segments[0], 17_400_000);
        var second = RepetitionProgram.ContinueFrom(101, first, position);
        Assert.AreEqual(new CountdownPosition(3, 153), second.ResumePosition);
        Assert.AreEqual(180u, second.Segments[1].DurationSeconds);
        Assert.AreEqual(30u, second.TotalRepeats);
        var atBoundary = CountdownPosition.WithFollowingRepeats(second, second.Segments[0], 153_000_000);
        Assert.AreEqual(new CountdownPosition(4, 180), atBoundary);
    }

    [TestMethod]
    public void FullRemainingRepeatCanBePackedWithoutAnUnnecessaryShortSegment()
    {
        var resumed = RepetitionProgram.ContinueFrom(100, Plan(5, 30), new(7, 5));
        Assert.AreEqual(1, resumed.Segments.Count);
        Assert.AreEqual(new ProgramSegment(1, 7, 24, 5), resumed.Segments[0]);
        Assert.AreEqual(6u, ProgramExecutionState.New(resumed).CompletedRepeatCount);
        var largest = Plan(2, 20_000, 4);
        var largestResume = RepetitionProgram.ContinueFrom(101, largest, new(1, 1));
        largestResume.Validate();
        Assert.AreEqual(10_001, largestResume.Segments.Count);
    }

    [TestMethod]
    public void MissingOrWrongResetProofBlocksResumeAndCannotEraseSavedPosition()
    {
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
        coordinator.Stop(0xABC, 3, 170);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP01"] = ResetReady("ESP01", 0xABC) });
        CollectionAssert.AreEqual(new[] { "ESP02" }, coordinator.State.StopCheckpoint!.PendingDeviceIds.ToArray());
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP02"] = ResetReady("ESP02", 0xDEF) });
        Assert.IsFalse(coordinator.State.StopCheckpoint!.CanContinue);
        Assert.AreEqual(170u, coordinator.State.StopCheckpoint.RemainingSeconds);
        Assert.AreEqual(0xABCul, coordinator.State.StopCheckpoint.ResetCommandId);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP02"] = ResetReady("ESP02", 0xABC) });
        Assert.IsTrue(coordinator.State.StopCheckpoint!.CanContinue);
        coordinator.RecheckAllStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP02"] = ResetReady("ESP02", 0xABC) });
        Assert.IsFalse(coordinator.State.StopCheckpoint!.CanContinue);
        CollectionAssert.AreEqual(new[] { "ESP01" }, coordinator.State.StopCheckpoint.PendingDeviceIds.ToArray());
    }

    [TestMethod]
    public void ResetDiscardsContinuationAndRequiresItsOwnCommandProof()
    {
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
        coordinator.Stop(0xABC, 3, 170);
        coordinator.ResetProgress(0xABD);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> {
            ["ESP01"] = ResetReady("ESP01", 0xABC), ["ESP02"] = ResetReady("ESP02", 0xABC) });
        Assert.IsTrue(coordinator.State.StopCheckpoint!.DiscardProgress);
        Assert.IsFalse(coordinator.State.StopCheckpoint.CanContinue);
        Assert.AreEqual(2, coordinator.State.StopCheckpoint.PendingDeviceIds.Count);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> {
            ["ESP01"] = ResetReady("ESP01", 0xABD), ["ESP02"] = ResetReady("ESP02", 0xABD) });
        Assert.AreEqual(0, coordinator.State.StopCheckpoint!.PendingDeviceIds.Count);
        Assert.IsFalse(coordinator.State.StopCheckpoint.CanContinue);
        Assert.IsNull(ProgramExecutionState.New(Plan(), coordinator.State).StopCheckpoint);
    }

    [TestMethod]
    public void ChallengedReadyEvidenceIsRequiredEvenWhenRtcIsNotReady()
    {
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
        coordinator.Stop(1, 1, 180);
        var invalid = ResetReady("ESP01", 1);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> {
            ["ESP01"] = invalid with { Execution = invalid.Execution with { ResponseId = 0 } } });
        Assert.AreEqual(2, coordinator.State.StopCheckpoint!.PendingDeviceIds.Count);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP01"] = invalid });
        Assert.AreEqual(1, coordinator.State.StopCheckpoint!.PendingDeviceIds.Count);
        Assert.IsFalse(SegmentedProgramCoordinator.OperationalReady(invalid));
    }

    [TestMethod]
    public void SavedControllerEpochCannotBeUsedAfterRestart()
    {
        var state = ProgramExecutionState.New(Plan()) with { Stage = ProgramStage.Running,
            CurrentSegmentIndex = 1, CurrentCommandId = 123, TargetMasterMicroseconds = 1_000_000,
            ControllerSessionId = "old" };
        Assert.ThrowsExactly<InvalidOperationException>(() => CountdownPosition.Capture(state, 5_000_000, "new"));
        Assert.AreEqual(new CountdownPosition(1, 176), CountdownPosition.Capture(state, 5_000_000, "old"));
    }

    [TestMethod]
    public void PausedJournalAndMixedDurationContinuationSurviveRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "factory-pause-" + Guid.NewGuid());
        try
        {
            var store = new FileProgramStateStore(Path.Combine(dir, "program.json"));
            var coordinator = new SegmentedProgramCoordinator(store, ProgramExecutionState.New(Plan()));
            coordinator.Stop(0xABC, 3, 170);
            ProgramExecutionState paused = store.Load()!;
            Assert.AreEqual(ProgramStage.Stopped, paused.Stage);
            Assert.AreEqual(170u, paused.StopCheckpoint!.RemainingSeconds);
            Assert.AreEqual(2, paused.StopCheckpoint.PendingDeviceIds.Count);
            var continued = RepetitionProgram.ContinueFrom(100, paused.Program, paused.StopCheckpoint.Position);
            store.Save(ProgramExecutionState.New(continued, paused));
            Assert.AreEqual(170u, store.Load()!.Program.Segments[0].DurationSeconds);
            Assert.AreEqual(180u, store.Load()!.Program.Segments[1].DurationSeconds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void LegacyJournalWithoutPauseFieldsStillLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "factory-old-journal-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonNode.Parse(JsonSerializer.Serialize(ProgramExecutionState.New(Plan())))!.AsObject();
            json.Remove("StopCheckpoint");json["Program"]!.AsObject().Remove("ResumePosition");
            string payload = json.ToJsonString();
            string file = Path.Combine(dir, "program.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { Version = 1, Payload = payload,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) }));
            Assert.IsNull(new FileProgramStateStore(file).Load()!.StopCheckpoint);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ControllerClockFreezesThenArmsOrResets()
    {
        var clock = new ControllerClock();
        clock.ArmSegmentAt(180, 30, 1_000_000, 1, 30);clock.UpdateAt(371_000_000);
        Assert.AreEqual(3u, clock.CurrentRepeat);Assert.AreEqual(170u, clock.RemainingSeconds);
        clock.Pause(170, 3, 30);clock.UpdateAt(9_000_000_000);
        Assert.IsTrue(clock.IsPaused);Assert.AreEqual(170u, clock.RemainingSeconds);
        clock.ArmSegmentAt(170, 1, 10_000_000_000, 3, 30);clock.UpdateAt(10_001_000_000);
        Assert.IsFalse(clock.IsPaused);Assert.AreEqual(169u, clock.RemainingSeconds);
        clock.Reset(180);Assert.IsFalse(clock.IsPaused);Assert.AreEqual(180u, clock.RemainingSeconds);
        Assert.AreEqual(1u, clock.CurrentRepeat);
    }

    [TestMethod]
    public void StopAddressGateRejectsCachedOrMissingAddresses()
    {
        DateTimeOffset challenge = DateTimeOffset.UtcNow;
        Assert.IsFalse(StopResetAddressGate.HasFreshAddressEvidence(null, challenge));
        Assert.IsFalse(StopResetAddressGate.HasFreshAddressEvidence(challenge.AddTicks(-1), challenge));
        Assert.IsTrue(StopResetAddressGate.HasFreshAddressEvidence(challenge, challenge));
    }

    [TestMethod]
    public void InitialRosterRejectsMissingAndOfflineBeforeCreatingJournal()
    {
        Assert.AreEqual(2, InitialRosterAdmission.Evaluate([new("ESP01", false, false), new("ESP02", true, false)]).Count);
        Assert.AreEqual(0, InitialRosterAdmission.Evaluate([new("ESP01", true, true)]).Count);
        Assert.AreEqual(1, InitialRosterAdmission.Evaluate([]).Count);
    }

    [TestMethod]
    public void LateHoldPreservesStoppedCheckpointAndReloads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "factory-held-stop-" + Guid.NewGuid());
        try
        {
            var store = new FileProgramStateStore(Path.Combine(dir, "program.json"));
            var coordinator = new SegmentedProgramCoordinator(store, ProgramExecutionState.New(Plan()));
            coordinator.Stop(0xABC, 3, 170);
            coordinator.Hold("A late recovery failure.");
            ProgramExecutionState stopped = store.Load()!;
            Assert.AreEqual(ProgramStage.Stopped, stopped.Stage);
            Assert.IsFalse(stopped.RequiresOperator);
            Assert.AreEqual(170u, stopped.StopCheckpoint!.RemainingSeconds);
            coordinator.ResetProgress(0xABD);
            coordinator.Hold("Another late failure after RESET.");
            Assert.AreEqual(ProgramStage.Stopped, store.Load()!.Stage);
            Assert.IsTrue(store.Load()!.StopCheckpoint!.DiscardProgress);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ResetCanRecoverPlanWithoutTrustingInvalidCheckpoint()
    {
        string dir = Path.Combine(Path.GetTempPath(), "factory-reset-plan-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "program.json");
            var store = new FileProgramStateStore(file);
            foreach (ProgramStage stage in new[] { ProgramStage.Held, ProgramStage.Stopped })
            {
                var invalid = ProgramExecutionState.New(Plan()) with { Stage = stage,
                    StopCheckpoint = new(0xABC, 0, 999, ["ESP01"]) };
                string payload = JsonSerializer.Serialize(invalid);
                File.WriteAllText(file, JsonSerializer.Serialize(new { Version = 1, Payload = payload,
                    Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) }));
                Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
                CollectionAssert.AreEqual(new[] { "ESP01", "ESP02" }, store.LoadPlanForReset()!.Participants.ToArray());
            }
            File.WriteAllText(file, "{ broken journal");
            Assert.IsNull(store.LoadPlanForReset());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void StopConfirmationReasonsAgreeWithAcceptance()
    {
        var ready = ResetReady("ESP01", 0xABC);
        Assert.IsNull(StopResetConfirmation.Failure("ESP01", 0xABC, ready));
        Assert.IsTrue(StopResetConfirmation.Failure("ESP01", 0xABC, null)!.Contains("No fresh"));
        Assert.IsTrue(StopResetConfirmation.Failure("ESP01", 0xABD, ready)!.Contains("not confirmed"));
        var running = ready with { Execution = ready.Execution with { State = TimerState.Running } };
        Assert.IsTrue(StopResetConfirmation.Failure("ESP01", 0xABC, running)!.Contains("RUNNING"));
        var progress = ready with { Execution = ready.Execution with { ElapsedSegmentMicroseconds = 1 } };
        Assert.IsTrue(StopResetConfirmation.Failure("ESP01", 0xABC, progress)!.Contains("execution progress"));
        var missingBuild = ready with { Health = ready.Health with { FirmwareBuildId = null } };
        Assert.IsTrue(StopResetConfirmation.Failure("ESP01", 0xABC, missingBuild)!.Contains("firmware build"));
        foreach (var observation in new[] { running, progress, missingBuild,
            ready with { Execution = ready.Execution with { ResponseId = 0 } },
            ready with { Execution = ready.Execution with { BootId = 0 } } })
        {
            var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
            coordinator.Stop(0xABC, 1, 180);
            coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP01"] = observation });
            Assert.IsNotNull(StopResetConfirmation.Failure("ESP01", 0xABC, observation));
            Assert.IsTrue(coordinator.State.StopCheckpoint!.PendingDeviceIds.Contains("ESP01"));
        }
    }

    [TestMethod]
    public void SelectedResumeKeepsSavedTimeAndOriginalRepeatGeometry()
    {
        var continued = RepetitionProgram.ContinueFrom(101, Plan(), new(3, 170), ["ESP03", "ESP05"]);
        continued.Validate();
        CollectionAssert.AreEqual(new[] { "ESP03", "ESP05" }, continued.Participants.ToArray());
        Assert.AreEqual(new ProgramSegment(1, 3, 1, 170), continued.Segments[0]);
        Assert.AreEqual(new ProgramSegment(2, 4, 27, 180), continued.Segments[1]);
        Assert.AreEqual(2u, ProgramExecutionState.New(continued).CompletedRepeatCount);
        Assert.AreEqual(30u, continued.TotalRepeats);
        var second = RepetitionProgram.ContinueFrom(102, continued, new(3, 153), ["ESP04"]);
        Assert.AreEqual(153u, second.Segments[0].DurationSeconds);
        Assert.AreEqual(180u, second.Segments[1].DurationSeconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RepetitionProgram.ContinueFrom(103, Plan(), new(3, 170), []));
    }

    [TestMethod]
    public void ChangedPauseSelectionRequiresOnlyNewScopeAndFreshResetProof()
    {
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
        coordinator.Stop(0xABC, 3, 170);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> { ["ESP02"] = ResetReady("ESP02", 0xABC) });
        coordinator.SelectStoppedParticipants(["ESP02", "ESP03"], 0xABD);
        Assert.AreEqual(new CountdownPosition(3, 170), coordinator.State.StopCheckpoint!.Position);
        CollectionAssert.AreEqual(new[] { "ESP02", "ESP03" }, coordinator.State.StopCheckpoint.PendingDeviceIds.ToArray());
        Assert.IsFalse(coordinator.State.Program.Participants.Contains("ESP01"));
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> {
            ["ESP02"] = ResetReady("ESP02", 0xABC), ["ESP03"] = ResetReady("ESP03", 0xABC) });
        Assert.AreEqual(2, coordinator.State.StopCheckpoint!.PendingDeviceIds.Count);
        coordinator.ConfirmStoppedDevices(new Dictionary<string, ProgramDeviceObservation> {
            ["ESP02"] = ResetReady("ESP02", 0xABD), ["ESP03"] = ResetReady("ESP03", 0xABD) });
        Assert.IsTrue(coordinator.State.StopCheckpoint!.CanContinue);
        coordinator.SelectStoppedParticipants(["ESP03", "ESP02"], 0xABE);
        Assert.AreEqual(0xABDul, coordinator.State.StopCheckpoint!.ResetCommandId);
        Assert.AreEqual(0, coordinator.State.StopCheckpoint.PendingDeviceIds.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => coordinator.SelectStoppedParticipants([], 0xABE));
        Assert.AreEqual(new CountdownPosition(3, 170), coordinator.State.StopCheckpoint.Position);
    }

    [TestMethod]
    public void SelectedStopWithNewTimerDropsOldArmIdentityAndLoadsFromDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "factory-selected-stop-" + Guid.NewGuid());
        try
        {
            var store = new FileProgramStateStore(Path.Combine(dir, "program.json"));
            var armed = ProgramExecutionState.New(Plan()) with { Stage = ProgramStage.Running,
                CurrentSegmentIndex = 1, CurrentCommandId = 77, TargetMasterMicroseconds = 1_000_000,
                ControllerSessionId = "old", BootIds = new Dictionary<string, ulong> { ["ESP01"] = 1, ["ESP02"] = 2 },
                FirmwareBuildIds = new Dictionary<string, string> { ["ESP01"] = "build", ["ESP02"] = "build" } };
            var coordinator = new SegmentedProgramCoordinator(store, armed);
            coordinator.Stop(0xABC, 3, 170, participants: ["ESP02", "ESP04"]);
            ProgramExecutionState loaded = store.Load()!;
            CollectionAssert.AreEqual(new[] { "ESP02", "ESP04" }, loaded.Program.Participants.ToArray());
            Assert.AreEqual(new CountdownPosition(3, 170), loaded.StopCheckpoint!.Position);
            Assert.AreEqual(0ul, loaded.CurrentCommandId);
            Assert.AreEqual(0u, loaded.CurrentSegmentIndex);
            Assert.AreEqual(0, loaded.BootIds.Count);
            loaded.StopCheckpoint.Validate(loaded.Program, loaded.Stage);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SelectingResetCheckpointCannotReviveDiscardedProgress()
    {
        var coordinator = new SegmentedProgramCoordinator(new Store(), ProgramExecutionState.New(Plan()));
        coordinator.ResetProgress(0xABC);
        coordinator.SelectStoppedParticipants(["ESP05"], 0xABD);
        Assert.IsTrue(coordinator.State.StopCheckpoint!.DiscardProgress);
        Assert.IsFalse(coordinator.State.StopCheckpoint.CanResumePosition);
        CollectionAssert.AreEqual(new[] { "ESP05" }, coordinator.State.Program.Participants.ToArray());
    }

    [TestMethod]
    public void InvalidOrCompletedContinuationIsRejected()
    {
        foreach (CountdownPosition position in new CountdownPosition[] { new(0, 1), new(31, 1), new(1, 181), new(30, 0) })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RepetitionProgram.ContinueFrom(100, Plan(), position));
        var malformed = Plan() with { Segments = [new ProgramSegment(1, 1, 1, 180)] };
        Assert.ThrowsExactly<InvalidDataException>(() => malformed.Validate());
    }
}
