using System.Reflection;
using System.Net;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using FactoryTimer.Controller;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;
using Microsoft.UI.Dispatching;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
void Set(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

var plan = RepetitionProgram.Create(99, 180, 30, ["ESP01", "ESP02"]);
var store = new MemoryStore();
using var vm = new MainViewModel(new DispatcherQueue(), store, new NoNetworkProvider());
foreach (var device in vm.Devices) device.IsSelected = plan.Participants.Contains(device.DeviceId);
var state = ProgramExecutionState.New(plan) with { Stage = ProgramStage.Running,
    CurrentSegmentIndex = 1, CurrentCommandId = 11,
    TargetMasterMicroseconds = MasterClock.NowMicroseconds - 370_250_000,
    ControllerSessionId = Field<string>(vm, "controllerSessionId"),
    BootIds = plan.Participants.ToDictionary(id => id, _ => 42ul),
    FirmwareBuildIds = plan.Participants.ToDictionary(id => id, _ => "12345678") };
var coordinator = new SegmentedProgramCoordinator(store, state);
Set(vm, "productionProgram", coordinator);
Check(vm.CanStopCountdown && vm.CanResetCountdown, "STOP/RESET remain available without a network");
await vm.SendStartAsync();
Check(store.Saves == 0, "START without network cannot alter an active run");
await vm.StopCountdownAsync();
var checkpoint = coordinator.State.StopCheckpoint!;
Check(checkpoint.CurrentRepeat == 3 && checkpoint.RemainingSeconds == 170, "actual STOP adapter saves click-time position");
Check(checkpoint.PendingDeviceIds.SequenceEqual(plan.Participants), "STOP requires the selected timers while offline");
ControllerClock clock = Field<ControllerClock>(vm, "clock");
Check(clock.IsPaused && clock.RemainingSeconds == 170 && vm.ControllerState == "PAUSED", "actual UI clock freezes");
ulong resetId = checkpoint.ResetCommandId;
await vm.StopCountdownAsync();
Check(coordinator.State.StopCheckpoint!.ResetCommandId == resetId && clock.RemainingSeconds == 170,
    "repeated STOP retains its transaction and time");
foreach (var device in vm.Devices) device.IsSelected = device.DeviceId is "ESP02" or "ESP03";
Check(vm.CanEditParticipants && coordinator.State.Program.Participants.SequenceEqual(new[] { "ESP01", "ESP02" }),
    "selection changes wait for an explicit START, STOP or RESET action");
vm.DurationMinutesInput = 2;vm.DurationSecondsInput = 15;
Check(clock.RemainingSeconds == 170, "draft duration does not change paused time");
using (var restarted = new MainViewModel(new DispatcherQueue(), store, new NoNetworkProvider()))
{
    Check(Field<ControllerClock>(restarted, "clock").IsPaused && restarted.ControllerRemaining == "02:50" &&
        restarted.ControllerState == "PAUSED", "paused state is reconstructed on restart");
    Check(!restarted.CanStartCountdown, "missing network cannot bypass resumed fleet confirmation");
}
await vm.SendResetAsync();
coordinator = Field<SegmentedProgramCoordinator>(vm, "productionProgram");
resetId = coordinator.State.StopCheckpoint!.ResetCommandId;
Check(coordinator.State.StopCheckpoint!.DiscardProgress && !coordinator.State.StopCheckpoint.CanContinue, "RESET discards continuation");
Check(coordinator.State.Program.LogoSeconds == 4, "operator RESET preloads the new four-second-logo program");
Check(!clock.IsPaused && clock.RemainingSeconds == 135 && vm.ControllerState == "READY", "RESET uses draft duration for new countdown");
Check(coordinator.State.StopCheckpoint.PendingDeviceIds.SequenceEqual(new[] { "ESP02", "ESP03" }), "RESET requires only the currently selected fleet");
ProgramDeviceObservation Ready(string id) => new(new(id, 17, 42, 0, 0, resetId, TimerState.Ready,
    180, 1, 1, 1, 1, 0, 0, 0, 129), new(id, resetId, TimerState.Ready, 180, FirmwareBuildId: "12345678"));
coordinator.ConfirmStoppedDevices(coordinator.State.Program.Participants.ToDictionary(id => id, Ready));
await vm.SendResetAsync();
coordinator = Field<SegmentedProgramCoordinator>(vm, "productionProgram");
Check(coordinator.State.StopCheckpoint!.ResetCommandId != resetId && coordinator.State.StopCheckpoint.PendingDeviceIds.Count == 2,
    "new RESET after confirmation rotates its command identity");

// A fresh reply from the new DHCP endpoint must not authorize destructive
// RESET to an address later overwritten by an out-of-order old STATUS.
DateTimeOffset addressChallenge = DateTimeOffset.UtcNow.AddSeconds(-1);
IPAddress freshIp = IPAddress.Parse("192.168.0.111"), staleIp = IPAddress.Parse("192.168.0.11");
vm.Esp01.Apply(new StatusPacket("ESP01", resetId, TimerState.Ready, 180), new IPEndPoint(freshIp, 5000), MasterClock.NowMicroseconds);
Field<Dictionary<string, ulong>>(vm, "expectedExecutionReplies")["ESP01"] = 123;
Field<Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)>>(vm, "matchedExecutionReplies")["ESP01"] =
    (Ready("ESP01").Execution with { ResponseId = 123 }, DateTimeOffset.UtcNow);
Field<Dictionary<string, (ulong Request, IPAddress Address)>>(vm, "matchedExecutionAddresses")["ESP01"] = (123, freshIp);
MethodInfo verifyAddress = typeof(MainViewModel).GetMethod("TryGetVerifiedResetAddress", BindingFlags.Instance | BindingFlags.NonPublic)!;
object?[] verifyArgs = [vm.Esp01, 123ul, addressChallenge, null];
Check((bool)verifyAddress.Invoke(vm, verifyArgs)! && freshIp.Equals(verifyArgs[3]), "current challenged DHCP endpoint is accepted");
vm.Esp01.Apply(new StatusPacket("ESP01", resetId, TimerState.Ready, 180), new IPEndPoint(staleIp, 5000), MasterClock.NowMicroseconds);
Check(!(bool)verifyAddress.Invoke(vm, [vm.Esp01, 123ul, addressChallenge, null])!, "out-of-order old IP cannot inherit new endpoint proof");

using (var preparingVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    var preparing = new SegmentedProgramCoordinator(new MemoryStore(), ProgramExecutionState.New(plan));
    preparing.BeginPreparation();
    Set(preparingVm, "productionProgram", preparing);
    var cancellation = new CancellationTokenSource(); // The view model owns this source.
    Set(preparingVm, "programCancellation", cancellation);
    Set(preparingVm, "isSending", true);
    Task prepareTask = Task.Run(async () => {
        try { await Task.Delay(Timeout.Infinite, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally { Set(preparingVm, "isSending", false); }
    });
    Set(preparingVm, "programTask", prepareTask);
    Check(preparingVm.CanStopCountdown && preparingVm.CanResetCountdown, "STOP/RESET available during production preparation");
    await preparingVm.StopCountdownAsync();
    Check(cancellation.IsCancellationRequested && preparing.State.StopCheckpoint is { CurrentRepeat: 1, RemainingSeconds: 180 },
        "STOP cancels preparation and preserves full unstarted repetition");
}

var emptyStore = new MemoryStore();
using (var draftVm = new MainViewModel(new DispatcherQueue(), emptyStore, new NoNetworkProvider()))
{
    var create = typeof(MainViewModel).GetMethod("StartRepetitionProgramCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await (Task)create.Invoke(draftVm, null)!;
    Check(emptyStore.Saves == 0 && Field<SegmentedProgramCoordinator?>(draftVm, "productionProgram") is null,
        "missing selection is rejected before any durable program is created");
}
// Reproduce the screenshot with a real file journal: an invalid STOP/RESET
// checkpoint holds recovery at startup. RESET must repair both disk and UI.
string recoveryDir = Path.Combine(Path.GetTempPath(), "factory-reset-held-" + Guid.NewGuid());
try
{
    Directory.CreateDirectory(recoveryDir);
    string journal = Path.Combine(recoveryDir, "program.json");
    var badState = ProgramExecutionState.New(plan) with { Stage = ProgramStage.Held,
        StopCheckpoint = new(0xABC, 0, 999, ["ESP01"]) };
    string payload = JsonSerializer.Serialize(badState);
    File.WriteAllText(journal, JsonSerializer.Serialize(new { Version = 1, Payload = payload,
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) }));
    var recoveryStore = new FileProgramStateStore(journal);
    using (var heldVm = new MainViewModel(new DispatcherQueue(), recoveryStore, new NoNetworkProvider(), new NullSocketFactory()))
    {
        Check(heldVm.ProductionProgramText.Contains("recovery held", StringComparison.OrdinalIgnoreCase) && !heldVm.CanStartCountdown,
            "invalid checkpoint reproduces held recovery and disabled START");
        Check(heldVm.CanResetCountdown, "RESET can clear a loaded recovery error even offline");
        foreach (var device in heldVm.Devices) device.IsSelected = device.DeviceId == "ESP03";
        heldVm.DurationMinutesInput = 0;heldVm.DurationSecondsInput = 20;heldVm.RepetitionsInput = 2;
        await heldVm.SendResetAsync();
        var resetState = recoveryStore.Load()!;
        var recovered = Field<SegmentedProgramCoordinator>(heldVm, "productionProgram");
        Check(Field<string?>(heldVm, "productionProgramLoadError") is null &&
            !heldVm.ProductionProgramText.Contains("RECOVERY HELD"), "RESET clears the latched recovery error and visible message");
        Check(resetState.Stage == ProgramStage.Stopped && resetState.StopCheckpoint!.DiscardProgress &&
            resetState.CurrentCommandId == 0 && resetState.CurrentSegmentIndex == 0,
            "RESET durably discards invalid progress and old ARM identity");
        Check(resetState.StopCheckpoint!.PendingDeviceIds.SequenceEqual(new[] { "ESP03" }) && resetState.Program.TotalRepeats == 2,
            "recovery RESET uses the current selection and configured repeat count");
        Check(heldVm.ControllerState == "READY" && heldVm.ControllerRemaining == "00:20",
            "recovery RESET restores the configured countdown display");
        var network = new ControllerNetworkInterface("test", ControllerInterfaceType.Ethernet, "Test", IPAddress.Loopback, 24);
        Set(heldVm, "selectedNetworkInterface", network);
        var udp = Field<UdpControllerService>(heldVm, "udpService");
        udp.ChangeReceiveTimestampMode(UdpReceiveTimestampMode.AsyncAwait);
        udp.ChangeSelection(network); // In-memory socket; no live traffic.
        ulong resetCommand = resetState.StopCheckpoint.ResetCommandId;
        recovered.ConfirmStoppedDevices(resetState.Program.Participants.ToDictionary(id => id, id => new ProgramDeviceObservation(
            Ready(id).Execution with { CommandId = resetCommand }, Ready(id).Health with { CommandId = resetCommand })));
        Check(heldVm.CanStartCountdown && !recovered.State.Active,
            "START is available for a fresh countdown after recovery RESET confirmation");
    }
    using (var restarted = new MainViewModel(new DispatcherQueue(), recoveryStore, new NoNetworkProvider()))
        Check(Field<string?>(restarted, "productionProgramLoadError") is null &&
            !restarted.ProductionProgramText.Contains("RECOVERY HELD"), "cleared recovery does not return after restart");

    File.WriteAllText(journal, "{ truncated journal");
    using (var damagedVm = new MainViewModel(new DispatcherQueue(), recoveryStore, new NoNetworkProvider()))
    {
        foreach (var device in damagedVm.Devices) device.IsSelected = device.DeviceId == "ESP03";
        damagedVm.Esp04.Apply(new StatusPacket("ESP04", 123, TimerState.Running, 7),
            new IPEndPoint(IPAddress.Parse("192.168.0.104"), 5000), MasterClock.NowMicroseconds);
        await damagedVm.SendResetAsync();
        Check(Field<string?>(damagedVm, "productionProgramLoadError") is null &&
            recoveryStore.Load()!.StopCheckpoint!.DiscardProgress, "RESET also repairs a truncated journal");
        Check(recoveryStore.Load()!.Program.Participants.SequenceEqual(new[] { "ESP03" }) && damagedVm.Esp04.State == "RUNNING",
            "RESET explicitly uses selection even when an unselected timer reports RUNNING");
    }
    using (var blockedVm = new MainViewModel(new DispatcherQueue(), new FailingRecoveryStore(plan), new NoNetworkProvider()))
    {
        await blockedVm.SendResetAsync();
        Check(Field<string?>(blockedVm, "productionProgramLoadError") is not null && !blockedVm.CanStartCountdown,
            "failed durable reset cannot falsely clear recovery");
    }
}
finally { if (Directory.Exists(recoveryDir)) Directory.Delete(recoveryDir, true); }

// Exact reported case: the old program requires ESP01/04, but the next
// countdown selects only ESP02/03/05. Only an explicit RESET changes scope.
var fleet = new[] { "ESP01", "ESP02", "ESP03", "ESP04", "ESP05" };
var selectedFleet = new[] { "ESP02", "ESP03", "ESP05" };
var scopeStore = new MemoryStore();
var scopePlan = RepetitionProgram.Create(201, 20, 2, fleet);
var scope = new SegmentedProgramCoordinator(scopeStore, ProgramExecutionState.New(scopePlan));
scope.ResetProgress(202);
ProgramDeviceObservation Confirmed(string id, ulong cmd) => new(
    Ready(id).Execution with { CommandId = cmd }, Ready(id).Health with { CommandId = cmd });
scope.ConfirmStoppedDevices(selectedFleet.ToDictionary(id => id, id => Confirmed(id, 202)));
using (var scopeVm = new MainViewModel(new DispatcherQueue(), scopeStore, new NoNetworkProvider(), new NullSocketFactory()))
{
    foreach (var device in scopeVm.Devices) device.IsSelected = selectedFleet.Contains(device.DeviceId);
    Check(Field<SegmentedProgramCoordinator>(scopeVm, "productionProgram").State.StopCheckpoint!.PendingDeviceIds
        .SequenceEqual(new[] { "ESP01", "ESP04" }), "selection alone does not change an existing stop checkpoint");
    await scopeVm.SendResetAsync();
    var resetScope = Field<SegmentedProgramCoordinator>(scopeVm, "productionProgram");
    Check(resetScope.State.Program.Participants.SequenceEqual(selectedFleet) &&
        resetScope.State.StopCheckpoint!.PendingDeviceIds.SequenceEqual(selectedFleet),
        "RESET replaces the required fleet with selected ESP02/03/05");
    Check(!scopeVm.ProductionProgramText.Contains("ESP01") && !scopeVm.ProductionProgramText.Contains("ESP04"),
        "unselected ESP01/04 disappear from the current waiting list");
    Check(scopeVm.CountdownActionLog.Contains("Removed from required stop confirmation") &&
        scopeVm.CountdownActionLog.Contains("TIMER01 (ESP01)") && scopeVm.CountdownActionLog.Contains("TIMER04 (ESP04)"),
        "log explicitly records excluded timers without claiming they were stopped");
    Check(scopeVm.CountdownActionLog.Contains("No fresh matching STATUS / EXEC_STATUS"),
        "log explains missing stop evidence for the currently selected timers");
    ulong pendingId = resetScope.State.StopCheckpoint!.ResetCommandId;
    await scopeVm.SendResetAsync();
    resetScope = Field<SegmentedProgramCoordinator>(scopeVm, "productionProgram");
    Check(resetScope.State.StopCheckpoint!.ResetCommandId == pendingId,
        "retrying the same pending selected RESET retains its command identity");
    var network = new ControllerNetworkInterface("test", ControllerInterfaceType.Ethernet, "Test", IPAddress.Loopback, 24);
    Set(scopeVm, "selectedNetworkInterface", network);
    var udp = Field<UdpControllerService>(scopeVm, "udpService");
    udp.ChangeReceiveTimestampMode(UdpReceiveTimestampMode.AsyncAwait);udp.ChangeSelection(network);
    resetScope.ConfirmStoppedDevices(selectedFleet.ToDictionary(id => id, id => Confirmed(id, pendingId)));
    typeof(MainViewModel).GetMethod("UpdateProductionProgramText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scopeVm, null);
    Check(!scopeVm.ProductionProgramText.Contains("Waiting for stop confirmation") && scopeVm.CanStartCountdown,
        "selected timer confirmations remove waiting without ESP01/04");
    var preparingScope = new SegmentedProgramCoordinator(new MemoryStore(), ProgramExecutionState.New(resetScope.State.Program));
    Set(scopeVm, "productionProgram", preparingScope);
    typeof(MainViewModel).GetMethod("ObserveProductionRunningStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(scopeVm, [new StatusPacket("ESP01", 700, TimerState.Running, 20)]);
    Check(!Field<bool>(scopeVm, "productionSilentRunActive"), "unselected RUNNING STATUS cannot pause new fleet preparation");
    var excludedIp = IPAddress.Parse("192.168.0.101");
    scopeVm.Esp01.Apply(new StatusPacket("ESP01", 700, TimerState.Running, 20),
        new IPEndPoint(excludedIp, 5000), MasterClock.NowMicroseconds);
    typeof(MainViewModel).GetMethod("ObserveProgramExecution", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scopeVm,
        [scopeVm.Esp01, Ready("ESP01").Execution with { ResponseId = 0, PlanId = 700, State = TimerState.Running }, excludedIp]);
    Check(!Field<bool>(scopeVm, "productionSilentRunActive") && Field<Task>(scopeVm, "productionSilentRunTask").IsCompleted,
        "unselected EXEC_STATUS cannot establish a recovered silent window");
    Set(scopeVm, "productionProgram", resetScope);
    await scopeVm.SendStartAsync(); // Missing discovery is rejected before any START packet.
    Check(scopeVm.CountdownActionLog.Contains("START BLOCKED") && selectedFleet.All(id =>
        scopeVm.CountdownActionLog.Contains($"TIMER{id[3..]}")), "all selected START blockers are visible in the local log");
    Check(resetScope.State.Stage == ProgramStage.Stopped && !resetScope.State.Program.Participants.Contains("ESP01"),
        "a refused new START cannot resurrect the excluded original fleet");
    udp.ChangeSelection(null);Set(scopeVm, "selectedNetworkInterface", null!);
    scopeVm.Esp05.IsSelected = false;
    await scopeVm.SendResetAsync();
    Check(Field<SegmentedProgramCoordinator>(scopeVm, "productionProgram").State.StopCheckpoint!.ResetCommandId != pendingId,
        "changing selection uses a fresh RESET transaction");
}
using (var restarted = new MainViewModel(new DispatcherQueue(), scopeStore, new NoNetworkProvider()))
    Check(restarted.Devices.Where(d => d.IsSelected).Select(d => d.DeviceId).SequenceEqual(new[] { "ESP02", "ESP03" }) &&
        !restarted.ProductionProgramText.Contains("ESP01"), "selected RESET scope survives controller restart");

var untouchedStore = new MemoryStore { Last = ProgramExecutionState.New(plan) };
using (var noSelectionVm = new MainViewModel(new DispatcherQueue(), untouchedStore, new NoNetworkProvider()))
{
    foreach (var device in noSelectionVm.Devices) device.IsSelected = false;
    await noSelectionVm.SendResetAsync();
    Check(untouchedStore.Saves == 0 && noSelectionVm.CountdownActionLog.Contains("RESET blocked"),
        "empty RESET selection is explained and leaves the saved countdown intact");
    MethodInfo report = typeof(MainViewModel).GetMethod("ReportStartBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    report.Invoke(noSelectionVm, [new StartBlock[] {
        new(StartBlockReason.Offline, "ESP02", "Reconnect TIMER02."),
        new(StartBlockReason.RtcNotLocked, "ESP03", "Wait for RTC LOCKED.") }]);
    Check(noSelectionVm.CountdownActionLog.Contains("OFFLINE") && noSelectionVm.CountdownActionLog.Contains("RTC NOT LOCKED") &&
        noSelectionVm.CountdownActionLog.Contains("Reconnect TIMER02") && noSelectionVm.CountdownActionLog.Contains("Wait for RTC LOCKED"),
        "log retains every reason and remedy, not just the first START failure");
    var setter = typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusMessage))!.GetSetMethod(true)!;
    for (int i = 0; i < 80; i++) setter.Invoke(noSelectionVm, [$"bounded-entry-{i:D3}"]);
    Check(Field<Queue<string>>(noSelectionVm, "countdownLogEntries").Count == 60 &&
        noSelectionVm.CountdownActionLog.Contains("bounded-entry-079") && !noSelectionVm.CountdownActionLog.Contains("bounded-entry-000"),
        "visible action log is bounded and retains the most recent entries");
}

var selectedPauseStore = new MemoryStore();
using (var selectedVm = new MainViewModel(new DispatcherQueue(), selectedPauseStore, new NoNetworkProvider()))
{
    var active = ProgramExecutionState.New(plan) with { Stage = ProgramStage.Running, CurrentSegmentIndex = 1,
        CurrentCommandId = 500, TargetMasterMicroseconds = MasterClock.NowMicroseconds - 370_250_000,
        ControllerSessionId = Field<string>(selectedVm, "controllerSessionId") };
    var selectedCoordinator = new SegmentedProgramCoordinator(selectedPauseStore, active);
    Set(selectedVm, "productionProgram", selectedCoordinator);
    foreach (var device in selectedVm.Devices) device.IsSelected = device.DeviceId == "ESP02";
    Check(selectedVm.CanEditParticipants && !selectedVm.CanEditCountdownSettings,
        "running countdown permits timer selection while keeping program settings locked");
    await selectedVm.StopCountdownAsync();
    var paused = selectedCoordinator.State.StopCheckpoint!;
    Check(selectedCoordinator.State.Program.Participants.SequenceEqual(new[] { "ESP02" }) &&
        paused.PendingDeviceIds.SequenceEqual(new[] { "ESP02" }), "STOP targets only the current selection");
    Check(paused.Position == new CountdownPosition(3, 170), "selected STOP preserves the running time and global repeat");
    foreach (var device in selectedVm.Devices) device.IsSelected = device.DeviceId is "ESP04" or "ESP05";
    selectedVm.DurationMinutesInput = 1;selectedVm.DurationSecondsInput = 0;selectedVm.RepetitionsInput = 8;
    var resume = typeof(MainViewModel).GetMethod("StartOrResumeCountdownCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await (Task)resume.Invoke(selectedVm, null)!; // Offline: applies scope and reports only selected confirmations.
    var resuming = selectedCoordinator.State.StopCheckpoint!;
    Check(selectedCoordinator.State.Program.Participants.SequenceEqual(new[] { "ESP04", "ESP05" }) &&
        resuming.PendingDeviceIds.SequenceEqual(new[] { "ESP04", "ESP05" }),
        "START after pause uses the new selection, including timers absent from the paused run");
    Check(resuming.Position == new CountdownPosition(3, 170) && resuming.ResetCommandId != paused.ResetCommandId,
        "resume scope change retains the pause position and prepares a fresh selected RESET");
    Check(selectedCoordinator.State.Program.DurationSeconds == 180 && selectedCoordinator.State.Program.TotalRepeats == 30,
        "resume keeps original repeat geometry despite edits to the draft duration and count");
    Check(!selectedVm.ProductionProgramText.Contains("ESP01") && !selectedVm.ProductionProgramText.Contains("ESP02") &&
        selectedVm.CountdownActionLog.Contains("START applied the current selection"),
        "old pause participants do not remain in the waiting list and the scope change is logged");
    using (var restored = new MainViewModel(new DispatcherQueue(), selectedPauseStore, new NoNetworkProvider()))
        Check(restored.ControllerRemaining == "02:50" && restored.Devices.Where(d => d.IsSelected).Select(d => d.DeviceId)
            .SequenceEqual(new[] { "ESP04", "ESP05" }), "new resume selection and saved time survive restart");
    foreach (var device in selectedVm.Devices) device.IsSelected = device.DeviceId == "ESP05";
    await selectedVm.StopCountdownAsync();
    Check(selectedCoordinator.State.StopCheckpoint!.Position == new CountdownPosition(3, 170) &&
        selectedCoordinator.State.Program.Participants.SequenceEqual(new[] { "ESP05" }),
        "another STOP applies its current selection without advancing saved time");
    int saves = selectedPauseStore.Saves;
    foreach (var device in selectedVm.Devices) device.IsSelected = false;
    await (Task)resume.Invoke(selectedVm, null)!;
    await selectedVm.StopCountdownAsync();
    await selectedVm.SendResetAsync();
    Check(selectedPauseStore.Saves == saves && selectedCoordinator.State.StopCheckpoint.Position == new CountdownPosition(3, 170),
        "empty selection on START, STOP and RESET leaves saved progress unchanged");
    Check(selectedVm.CountdownActionLog.Contains("START blocked — select at least one") &&
        selectedVm.CountdownActionLog.Contains("STOP blocked — select at least one") &&
        selectedVm.CountdownActionLog.Contains("RESET blocked — select at least one"),
        "all empty-selection failures remain visible in the action log");
}

using (var heldSelectionVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    var heldState = ProgramExecutionState.New(plan) with { Stage = ProgramStage.Held, CurrentSegmentIndex = 1,
        CurrentCommandId = 600, TargetMasterMicroseconds = MasterClock.NowMicroseconds - 370_250_000,
        ControllerSessionId = Field<string>(heldSelectionVm, "controllerSessionId") };
    var heldCoordinator = new SegmentedProgramCoordinator(new MemoryStore(), heldState);
    Set(heldSelectionVm, "productionProgram", heldCoordinator);
    foreach (var device in heldSelectionVm.Devices) device.IsSelected = device.DeviceId == "ESP03";
    await (Task)typeof(MainViewModel).GetMethod("StartOrResumeCountdownCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(heldSelectionVm, null)!;
    Check(heldCoordinator.State.Program.Participants.SequenceEqual(new[] { "ESP03" }) &&
        heldCoordinator.State.StopCheckpoint!.Position == new CountdownPosition(3, 170),
        "START recovery of a held program also applies the current selection and saved phase");
    Check(heldCoordinator.State.StopCheckpoint!.PendingDeviceIds.SequenceEqual(new[] { "ESP03" }),
        "held recovery requires confirmation only from the currently selected timer");
}

using (var snapshotVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    var snapshotCoordinator = new SegmentedProgramCoordinator(new MemoryStore(), ProgramExecutionState.New(plan));
    snapshotCoordinator.BeginPreparation();Set(snapshotVm, "productionProgram", snapshotCoordinator);
    foreach (var device in snapshotVm.Devices) device.IsSelected = device.DeviceId == "ESP02";
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Set(snapshotVm, "programCancellation", new CancellationTokenSource());
    Set(snapshotVm, "isSending", true);
    Set(snapshotVm, "programTask", Task.Run(async () => { await release.Task;Set(snapshotVm, "isSending", false); }));
    Task stopping = snapshotVm.StopCountdownAsync();
    Check(!snapshotVm.CanEditParticipants, "selection is locked while the STOP action is being applied");
    foreach (var device in snapshotVm.Devices) device.IsSelected = device.DeviceId == "ESP04";
    release.SetResult();await stopping;
    Check(snapshotCoordinator.State.Program.Participants.SequenceEqual(new[] { "ESP02" }),
        "STOP uses the click-time selection across asynchronous cancellation waits");
}

using (var abortScopeVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    Type participantType = typeof(MainViewModel).GetNestedType("PreparedParticipant", BindingFlags.NonPublic)!;
    Type runType = typeof(MainViewModel).GetNestedType("PreparedStartRun", BindingFlags.NonPublic)!;
    var preparingDevices = new[] { abortScopeVm.Esp01, abortScopeVm.Esp02, abortScopeVm.Esp03 };
    Array participants = Array.CreateInstance(participantType, preparingDevices.Length);
    for (int i = 0; i < preparingDevices.Length; i++)
        participants.SetValue(Activator.CreateInstance(participantType, preparingDevices[i], IPAddress.Loopback,
            preparingDevices[i].BuildStartGateInput(DateTimeOffset.UtcNow)), i);
    object prepared = Activator.CreateInstance(runType, 900ul, 20u, 9_000_000L, participants,
        false, null, 901ul, 1u, 1u, 1u, 1u, 0u, 0u)!;
    var restrict = typeof(MainViewModel).GetMethod("RestrictPreparedAbortForOperator", BindingFlags.Instance | BindingFlags.NonPublic)!;
    int CountPrepared(object run) => ((System.Collections.IEnumerable)runType.GetProperty("Participants")!.GetValue(run)!).Cast<object>().Count();
    Check(CountPrepared(restrict.Invoke(abortScopeVm, [prepared])!) == 3,
        "automatic START abort still includes its complete prepared fleet");
    Set(abortScopeVm, "operatorStopInProgress", true);
    Set(abortScopeVm, "operatorActionParticipants", new HashSet<string> { "ESP02" });
    object selectedAbort = restrict.Invoke(abortScopeVm, [prepared])!;
    var selectedPrepared = ((System.Collections.IEnumerable)runType.GetProperty("Participants")!.GetValue(selectedAbort)!).Cast<object>().ToArray();
    Check(selectedPrepared.Length == 1 && ((DeviceViewModel)participantType.GetProperty("Device")!.GetValue(selectedPrepared[0])!).DeviceId == "ESP02",
        "STOP/RESET during ARM cannot roll back unselected prepared timers");
    Set(abortScopeVm, "operatorActionParticipants", new HashSet<string> { "ESP05" });
    Task abort = (Task)typeof(MainViewModel).GetMethod("ConfirmPreparedRunAbortAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(abortScopeVm, [prepared, CancellationToken.None])!;
    await abort;
    object outcome = abort.GetType().GetProperty("Result")!.GetValue(abort)!;
    Check((bool)outcome.GetType().GetProperty("Confirmed")!.GetValue(outcome)! &&
        (ulong)outcome.GetType().GetProperty("ResetCommandId")!.GetValue(outcome)! == 0,
        "operator cancellation with no selected prepared timers sends no old-fleet RESET");
    var abortCoordinator = new SegmentedProgramCoordinator(new MemoryStore(), ProgramExecutionState.New(plan));
    abortCoordinator.BeginPreparation();Set(abortScopeVm, "productionProgram", abortCoordinator);
    ProgramExecutionState beforeAbortReport = abortCoordinator.State;
    typeof(MainViewModel).GetMethod("ReportPreparedAbortOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(abortScopeVm, [new StartBlock(StartBlockReason.ArmAckMissing, "controller", "Operator cancelled."), outcome, true]);
    Check(ReferenceEquals(beforeAbortReport, abortCoordinator.State) && !Field<bool>(abortScopeVm, "programRetryAllowed"),
        "selected operator cancellation cannot authorize a whole-fleet automatic retry");
    Set(abortScopeVm, "operatorStopInProgress", false);Set(abortScopeVm, "operatorActionParticipants", null!);
}

// A failed active probe must update the card immediately, even while the
// normal 48-second/silent-window connectivity grace has not expired.
using (var presenceVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    var missing = presenceVm.Esp03;
    var running = new StatusPacket("ESP03", 77, TimerState.Running, 15);
    missing.Apply(running, new IPEndPoint(IPAddress.Parse("192.168.0.103"), 5000), MasterClock.NowMicroseconds);
    Set(missing, "synchronizationAccepted", true);
    missing.BeginIntentionalStatusPause();
    missing.RefreshOnlineStatus(DateTimeOffset.UtcNow.AddSeconds(1000));
    Check(missing.OnlineText == "ONLINE" && missing.State == "RUNNING", "planned network silence does not invent an offline timer");
    DateTimeOffset challenge = DateTimeOffset.UtcNow.AddTicks(1);
    var markMissing = typeof(MainViewModel).GetMethod("MarkUnresponsiveParticipants", BindingFlags.Instance | BindingFlags.NonPublic)!;
    markMissing.Invoke(presenceVm, [new[] { missing }, challenge]);
    Check(missing.OnlineText == "OFFLINE" && missing.State == "UNKNOWN" && missing.RemainingSeconds == "—",
        "failed fresh probe immediately replaces stale RUNNING with OFFLINE / UNKNOWN");
    missing.UpdateRunningRemainingEstimate(DateTimeOffset.UtcNow.AddSeconds(2));
    Check(missing.State == "UNKNOWN" && missing.RemainingSeconds == "—" && !missing.IsSynchronized,
        "countdown estimate and cached sync cannot revive a missing timer card");
    Check(presenceVm.CountdownActionLog.Contains("TIMER03 (ESP03): OFFLINE / UNKNOWN"), "card transition and missing reason are logged together");
    challenge = DateTimeOffset.UtcNow;
    missing.Apply(running with { State = TimerState.Ready, RemainingSeconds = 20 }, new IPEndPoint(freshIp, 5000), MasterClock.NowMicroseconds);
    markMissing.Invoke(presenceVm, [new[] { missing }, challenge]);
    Check(missing.OnlineText == "ONLINE" && missing.State == "READY", "fresh STATUS restores card and missing EXEC alone does not mark it offline");
    challenge = DateTimeOffset.UtcNow.AddTicks(1);
    Field<Dictionary<string, ulong>>(presenceVm, "expectedExecutionReplies")["ESP03"] = 888;
    Field<Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)>>(presenceVm, "matchedExecutionReplies")["ESP03"] =
        (new("ESP03", 888, 42, 0, 0, 77, TimerState.Ready, 20, 1, 1, 1, 1, 0, 0, 0, 129), challenge.AddTicks(1));
    markMissing.Invoke(presenceVm, [new[] { missing }, challenge]);
    Check(missing.OnlineText == "ONLINE", "fresh challenged execution evidence also proves the device is reachable");
    missing.RefreshOnlineStatus(DateTimeOffset.UtcNow.AddSeconds(49));
    Check(missing.OnlineText == "OFFLINE" && missing.State == "UNKNOWN", "normal idle timeout also clears the stale runtime state");
}

// Exercise the real automatic completion adapter and UDP sender. No physical
// socket is opened; the fake timers return challenged STATUS/EXEC through the
// actual inbound packet handler.
var completedStore = new MemoryStore();
var completionSocket = new RecordingSocketFactory();
using (var finishedVm = new MainViewModel(new DispatcherQueue(), completedStore, new NoNetworkProvider(), completionSocket))
{
    var iface = new ControllerNetworkInterface("completion-test", ControllerInterfaceType.Ethernet, "Host", IPAddress.Parse("192.168.0.10"), 24);
    Set(finishedVm, "selectedNetworkInterface", iface);
    Field<UdpControllerService>(finishedVm, "udpService").ChangeReceiveTimestampMode(UdpReceiveTimestampMode.AsyncAwait);
    Field<UdpControllerService>(finishedVm, "udpService").ChangeSelection(iface);
    string[] roster = ["ESP02", "ESP03", "ESP05"];
    var completedPlan = RepetitionProgram.Create(888, 20, 3, roster);
    var completed = ProgramExecutionState.New(completedPlan) with { Stage = ProgramStage.Completed, CurrentSegmentIndex = 1,
        NextSegmentIndex = 2, CurrentCommandId = 77, TargetMasterMicroseconds = 1_000_000, ControllerSessionId = "completed-session",
        BootIds = roster.ToDictionary(id => id, _ => 42ul), FirmwareBuildIds = roster.ToDictionary(id => id, _ => "12345678") };
    Set(finishedVm, "productionProgram", new SegmentedProgramCoordinator(completedStore, completed));
    Set(finishedVm, "programTask", new TaskCompletionSource().Task);
    Check(!finishedVm.CanStartCountdown && !finishedVm.CanEditCountdownSettings,
        "START and settings cannot race final diagnostics or automatic completion RESET");
    Set(finishedVm, "programTask", Task.CompletedTask);
    foreach (var d in finishedVm.Devices) d.IsSelected = d.DeviceId is "ESP01" or "ESP04";
    Dictionary<string, ulong> boardCommands = roster.ToDictionary(id => id, _ => 77ul);
    Dictionary<string, IPAddress> addresses = roster.ToDictionary(id => id, id => IPAddress.Parse("192.168.0.1" + id[3..]));
    MethodInfo inbound = typeof(MainViewModel).GetMethod("UdpService_PacketReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var resetTargets = new List<string>();
    bool persistedBeforeSend = true, controlsLocked = true;
    completionSocket.OnSend = (bytes, destination) => {
        if (!FactoryProtocol.TryParseCommand(Encoding.ASCII.GetString(bytes), out var command, out _) || command is null) return;
        string[] targets = destination.Address.Equals(iface.BroadcastAddress) ? roster :
            addresses.Where(x => x.Value.Equals(destination.Address)).Select(x => x.Key).ToArray();
        foreach (string id in targets)
        {
            if (command.CommandType == CommandType.Reset)
            {
                resetTargets.Add(id);
                persistedBeforeSend &= completedStore.Last?.StopCheckpoint?.ResetCommandId == command.CommandId;
                controlsLocked &= !finishedVm.CanStartCountdown && !finishedVm.CanStopCountdown && !finishedVm.CanResetCountdown && !finishedVm.CanEditParticipants;
                boardCommands[id] = command.CommandId;
                continue;
            }
            if (command.CommandType != CommandType.StatusRequest) continue;
            bool reset = boardCommands[id] != 77;
            TimerState timer = reset ? TimerState.Ready : TimerState.Finished;
            var status = new StatusPacket(id, boardCommands[id], timer, reset ? 20u : 0u,
                RtcState: RtcDisciplineState.Locked, RtcFitPoints: 129, RtcFitRmsMicroseconds: 0.5,
                RtcQueueDrops: 0, RtcTemperatureValid: true, RtcAcceptedEdges: 1000, RtcInferredMissingEdges: 0,
                RtcHoldoverEntries: 0, FirmwareBuildId: "12345678", DiagnosticFlags: 0);
            var execution = new ExecutionStatusPacket(id, command.CommandId, 42, reset ? 0ul : 888ul,
                reset ? 0u : 1u, boardCommands[id], timer, 20, reset ? 1u : 3u, 1, reset ? 1u : 3u,
                reset ? 1u : 3u, reset ? 0u : 60u, reset ? 0 : 1_000_000, reset ? 0 : 2_000_000, 129,
                reset ? 0 : 60_000_000);
            foreach (InboundPacket packet in new InboundPacket[] { status, execution })
                inbound.Invoke(finishedVm, [null, new PacketReceivedEventArgs(packet, new IPEndPoint(addresses[id], 5000), iface, MasterClock.NowMicroseconds)]);
        }
    };
    // The host dispatcher runs callbacks inline. Suppress the independent
    // discovery worker here so this fixture only answers explicit challenges.
    Set(finishedVm, "timingQuietPeriodActive", true);
    var autoReset = typeof(MainViewModel).GetMethod("ResetCompletedProgramAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    // The program loop's completion hook must trigger the adapter automatically,
    // while preserving FINISHED health/diagnostics before any RESET reaches wire.
    var traceSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Set(finishedVm, "controllerTxTraceCommandId", 77ul);
    Set(finishedVm, "controllerTxTraceCompletion", traceSaved.Task);
    Task completion = (Task)typeof(MainViewModel).GetMethod("RunProgramLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(finishedVm, [CancellationToken.None])!;
    Check(resetTargets.Count == 0 && !completion.IsCompleted && completedStore.Last?.StopCheckpoint is not null,
        "completion RESET is queued durably but waits for the run health and diagnostic CSV save");
    traceSaved.SetResult();
    await completion;
    await Field<StatusDiscoveryService>(finishedVm, "statusDiscovery").StopAndWaitAsync();
    Check(resetTargets.Count == 9 && roster.All(id => resetTargets.Count(x => x == id) == 3),
        "completion sends exactly one three-copy RESET transaction to every executed timer");
    Check(!resetTargets.Contains("ESP01") && !resetTargets.Contains("ESP04"),
        "completion RESET ignores edited checkbox selection and never resets unrelated timers");
    Check(persistedBeforeSend && controlsLocked, "automatic RESET intent is durable and controls are locked before its wire effects");
    Check(completedStore.Last!.StopCheckpoint is { DiscardProgress: true, PendingDeviceIds.Count: 0 } && completedStore.Last.Stage == ProgramStage.Stopped,
        "fresh READY proofs complete automatic RESET without leaving resumable finished progress");
    Check(finishedVm.ControllerRemaining == "00:20" && finishedVm.ControllerState == "READY" &&
        finishedVm.CountdownActionLog.Contains("Program complete. RESET confirmed"), "completion returns UI to a fresh countdown and logs the result");
    int count = resetTargets.Count;
    await (Task)autoReset.Invoke(finishedVm, null)!;
    Check(resetTargets.Count == count, "repeated completion handling cannot send a second RESET");
    foreach (ProgramStage stage in new[] { ProgramStage.Running, ProgramStage.BetweenSegments, ProgramStage.Held })
    {
        Set(finishedVm, "productionProgram", new SegmentedProgramCoordinator(completedStore, completed with { Stage = stage }));
        await (Task)autoReset.Invoke(finishedVm, null)!;
    }
    Check(resetTargets.Count == count, "no automatic RESET occurs at an internal repeat, segment boundary, or HOLD");
    var offlineComplete = new SegmentedProgramCoordinator(completedStore, completed);
    Set(finishedVm, "productionProgram", offlineComplete);
    Set(finishedVm, "selectedNetworkInterface", null!);
    await (Task)autoReset.Invoke(finishedVm, null)!;
    Check(completedStore.Last!.StopCheckpoint is { DiscardProgress: true, PendingDeviceIds.Count: 3 } &&
        finishedVm.StatusMessage.Contains("waiting for stop confirmation"), "network loss keeps automatic RESET pending and visible");
    using var restored = new MainViewModel(new DispatcherQueue(), completedStore, new NoNetworkProvider());
    Check(restored.ControllerRemaining == "00:20" && restored.ControllerState == "READY" &&
        restored.ProductionProgramText.Contains("Waiting for stop confirmation"), "pending completion RESET survives controller restart");
}

using (var logoVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    var segment = RepetitionProgram.Create(900,20,3,["ESP01"],logoSeconds:4).Segments.Single();
    var gate = typeof(MainViewModel).GetMethod("ValidateLogoFirmware",BindingFlags.Instance|BindingFlags.NonPublic)!;
    DateTimeOffset challenge = DateTimeOffset.UtcNow;
    StartBlock[] Evaluate() => (StartBlock[])gate.Invoke(logoVm,[segment,new[]{logoVm.Esp01},challenge])!;
    Check(Evaluate().Single().Reason == StartBlockReason.FirmwareIncompatible, "missing logo capability blocks before sync or ARM");
    var expected = Field<Dictionary<string,ulong>>(logoVm,"expectedExecutionReplies");
    var replies = Field<Dictionary<string,(ExecutionStatusPacket Packet,DateTimeOffset At)>>(logoVm,"matchedExecutionReplies");
    expected["ESP01"]=123;
    var idle = new ExecutionStatusPacket("ESP01",123,42,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129);
    replies["ESP01"]=(idle,DateTimeOffset.UtcNow);
    Check(Evaluate().Length == 1,"old firmware cannot receive a four-second logo command");
    replies["ESP01"]=(idle with { LogoSeconds=0 },challenge.AddTicks(-1));
    Check(Evaluate().Length == 1,"stale capability cannot authorize a logo segment");
    replies["ESP01"]=(idle with { LogoSeconds=0 },DateTimeOffset.UtcNow);
    Check(Evaluate().Length == 0,"fresh challenged feature support authorizes normal START preparation");
    expected["ESP01"]=124;Check(Evaluate().Length == 1,"old challenge ID cannot authorize logo START");
    var device = logoVm.Esp01;
    device.Apply(new StatusPacket("ESP01",77,TimerState.Running,1),new IPEndPoint(freshIp,5000),MasterClock.NowMicroseconds);
    device.ApplyExecution(idle with { PlanId=900,SegmentIndex=1,CommandId=77,State=TimerState.Running,
        RepeatCount=3,TotalRepeats=3,ElapsedSegmentSeconds=19,ElapsedSegmentMicroseconds=19_000_000,LogoSeconds=4 });
    Set(device,"executionObservedAt",challenge);
    device.UpdateRunningRemainingEstimate(challenge.AddSeconds(1));Check(device.RemainingSeconds=="LOGO","timer card shows logo during the gap");
    device.UpdateRunningRemainingEstimate(challenge.AddSeconds(5));Check(device.RemainingSeconds=="00:20","timer card begins the next full repetition after four seconds");
    var runType = typeof(MainViewModel).GetNestedType("PreparedStartRun",BindingFlags.NonPublic)!;
    var participantType = typeof(MainViewModel).GetNestedType("PreparedParticipant",BindingFlags.NonPublic)!;
    var participants = Array.CreateInstance(participantType,0);
    var prepared = Activator.CreateInstance(runType,900ul,20u,5_000_000L,participants,false,null,900ul,1u,3u,1u,3u,4u,0u)!;
    Check((uint)runType.GetProperty("SegmentDurationSeconds")!.GetValue(prepared)! == 72,
        "prepared ARM, trace and silent window include the final logo instead of ending at 60s");
}

using (var optionsVm = new MainViewModel(new DispatcherQueue(), new MemoryStore(), new NoNetworkProvider()))
{
    Check(!optionsVm.SignalEveryRepetitionStart && !optionsVm.SyncEveryRepetition,"new options default OFF");
    Check(optionsVm.CanEditResyncLimit,"normal mode uses the resync limit");
    optionsVm.SyncEveryRepetition=true;optionsVm.SignalEveryRepetitionStart=true;
    Check(!optionsVm.CanEditResyncLimit,"sync each repeat disables the unused limit field");
    optionsVm.SyncEveryRepetition=false;Check(optionsVm.CanEditResyncLimit,"time-limit mode can be selected again");
    var p=RepetitionProgram.Create(901,20,3,["ESP01"],logoSeconds:4,repeatStartSeconds:4);
    var segment=p.Segments.Single();
    var gate=typeof(MainViewModel).GetMethod("ValidateLogoFirmware",BindingFlags.Instance|BindingFlags.NonPublic)!;
    DateTimeOffset challenge=DateTimeOffset.UtcNow;
    var expected=Field<Dictionary<string,ulong>>(optionsVm,"expectedExecutionReplies");expected["ESP01"]=123;
    var replies=Field<Dictionary<string,(ExecutionStatusPacket Packet,DateTimeOffset At)>>(optionsVm,"matchedExecutionReplies");
    var idle=new ExecutionStatusPacket("ESP01",123,42,0,0,0,TimerState.Ready,20,1,1,1,1,0,0,0,129,LogoSeconds:0);
    StartBlock[] Evaluate()=>(StartBlock[])gate.Invoke(optionsVm,[segment,new[]{optionsVm.Esp01},challenge])!;
    replies["ESP01"]=(idle,DateTimeOffset.UtcNow);
    Check(Evaluate().Single().Reason==StartBlockReason.FirmwareIncompatible,"6.24.7 lacks the optional per-repeat start feature");
    replies["ESP01"]=(idle with{RepeatStartSeconds=0},challenge.AddTicks(-1));
    Check(Evaluate().Length==1,"stale new feature evidence cannot authorize START");
    replies["ESP01"]=(idle with{RepeatStartSeconds=0},DateTimeOffset.UtcNow);
    Check(Evaluate().Length==0,"fresh challenged 21-field support authorizes the start feature");
    expected["ESP01"]=124;Check(Evaluate().Length==1,"a previous challenge cannot authorize the start feature");
    var d=optionsVm.Esp01;
    d.Apply(new StatusPacket("ESP01",77,TimerState.Running,1),new IPEndPoint(freshIp,5000),MasterClock.NowMicroseconds);
    d.ApplyExecution(idle with{PlanId=901,SegmentIndex=1,CommandId=77,State=TimerState.Running,RepeatCount=3,TotalRepeats=3,
        CurrentRepeat=2,ElapsedSegmentSeconds=24,ElapsedSegmentMicroseconds=24_000_000,LogoSeconds=4,RepeatStartSeconds=4});
    Set(d,"executionObservedAt",challenge);
    d.UpdateRunningRemainingEstimate(challenge.AddSeconds(3));Check(d.RemainingSeconds=="LOGO","card shows LOGO throughout the steady start signal");
    d.UpdateRunningRemainingEstimate(challenge.AddSeconds(4));Check(d.RemainingSeconds=="00:20","next countdown starts at its full duration");
    var runType=typeof(MainViewModel).GetNestedType("PreparedStartRun",BindingFlags.NonPublic)!;
    var participantType=typeof(MainViewModel).GetNestedType("PreparedParticipant",BindingFlags.NonPublic)!;
    var participants=Array.CreateInstance(participantType,0);
    var prepared=Activator.CreateInstance(runType,901ul,20u,5_000_000L,participants,false,null,901ul,1u,3u,1u,3u,4u,4u)!;
    Check((uint)runType.GetProperty("SegmentDurationSeconds")!.GetValue(prepared)! == 80,"silent window and completion include both optional start gaps");
    var stopped=ProgramExecutionState.New(p) with{Stage=ProgramStage.Stopped,StopCheckpoint=new(99,2,20,[])};
    Set(optionsVm,"productionProgram",new SegmentedProgramCoordinator(new MemoryStore(),stopped));
    Check(!optionsVm.CanEditRepetitionOptions,"paused execution keeps its saved timing options until RESET");
}
var optionsStore=new MemoryStore();
optionsStore.Save(ProgramExecutionState.New(RepetitionProgram.Create(902,20,3,["ESP01"],logoSeconds:4,repeatStartSeconds:4,syncEveryRepetition:true)));
using(var restoredOptions=new MainViewModel(new DispatcherQueue(),optionsStore,new NoNetworkProvider()))
    Check(restoredOptions.SignalEveryRepetitionStart && restoredOptions.SyncEveryRepetition,"saved options are restored with the recovered plan");

XDocument xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XElement resync = xaml.Descendants(ns + "NumberBox").Single(x => (string?)x.Attribute("Header") == "Resync limit (seconds)");
XElement controls = resync.ElementsAfterSelf().First(x=>x.Name.LocalName=="Grid");
Check(controls.Name.LocalName == "Grid" && controls.Elements(ns + "Button").Select(x => (string?)x.Attribute("Content"))
    .SequenceEqual(new[] { "START", "STOP", "RESET" }), "exactly three controls below repetition options");
var optionBoxes=resync.ElementsAfterSelf().TakeWhile(x=>x!=controls).Where(x=>x.Name.LocalName=="CheckBox").ToArray();
Check(optionBoxes.Length==2 && optionBoxes.Select(x=>(string?)x.Attribute("IsChecked"))
    .SequenceEqual(new[]{"{Binding SignalEveryRepetitionStart, Mode=TwoWay}","{Binding SyncEveryRepetition, Mode=TwoWay}"}),
    "two checkboxes expose independent repeat-start signaling and sync policy");
Check((string?)resync.Attribute("IsEnabled")=="{Binding CanEditResyncLimit}","unused resync limit is disabled by the sync policy");
Check(xaml.Descendants(ns + "Button").Count(x => new[] { "START", "STOP", "RESET" }.Contains((string?)x.Attribute("Content"))) == 3,
    "no duplicate countdown action buttons");
Check(xaml.Descendants(ns + "Expander").Any(x => (string?)x.Attribute("Header") == "Engineering diagnostics" &&
    (string?)x.Attribute("IsExpanded") == "False"), "engineering actions remain collapsed");
XElement programText = xaml.Descendants(ns + "TextBlock").Single(x => (string?)x.Attribute("Text") == "{Binding ProductionProgramText}");
XElement actionLog = programText.ElementsAfterSelf().First();
Check(actionLog.Descendants(ns + "TextBlock").Any(x => (string?)x.Attribute("Text") == "{Binding CountdownActionLog}" &&
    (string?)x.Attribute("IsTextSelectionEnabled") == "True"), "selectable action log sits immediately below waiting text");
Check(actionLog.Descendants(ns + "ScrollViewer").Any(x => (string?)x.Attribute("MaxHeight") == "180"),
    "long diagnostic history scrolls inside a bounded log panel");
Check((string?)resync.Attribute("IsEnabled") == "{Binding CanEditResyncLimit}" &&
    xaml.Descendants(ns + "ItemsControl").Any(x => (string?)x.Attribute("ItemsSource") == "{Binding Devices}" &&
        (string?)x.Attribute("IsEnabled") == "{Binding CanEditParticipants}"),
    "timer selection is enabled independently of active program setting edits");
Console.WriteLine($"COUNTDOWN_ADAPTER_SMOKE=PASS CHECKS={checks} (actual view-model sources; dispatcher/brush host stand-ins)");

sealed class MemoryStore : IProgramStateStore
{
    public int Saves;
    public ProgramExecutionState? Last;
    public ProgramExecutionState? Load() => Last;
    public void Save(ProgramExecutionState state) { Last = state; Saves++; }
}

sealed class NoNetworkProvider : INetworkInterfaceProvider
{
    public event EventHandler? NetworkAddressChanged { add { } remove { } }
    public IReadOnlyList<ControllerNetworkInterface> GetUsableInterfaces() => [];
    public void Dispose() { }
}

sealed class FailingRecoveryStore(RepetitionProgram plan) : IProgramStateStore
{
    public ProgramExecutionState? Load() => throw new InvalidDataException("Invalid saved STOP/RESET checkpoint.");
    public RepetitionProgram? LoadPlanForReset() => plan;
    public void Save(ProgramExecutionState state) => throw new IOException("Journal is not writable.");
}

sealed class NullSocketFactory : IControllerUdpSocketFactory
{
    public IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast) => new NullSocket();
    private sealed class NullSocket : IControllerUdpSocket
    {
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken token) =>
            ValueTask.FromResult(datagram.Length);
        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Receive should end by cancellation.");
        }
        public void Dispose() { }
    }
}

sealed class RecordingSocketFactory : IControllerUdpSocketFactory
{
    public Action<byte[], IPEndPoint>? OnSend;
    public IControllerUdpSocket CreateBound(IPAddress localAddress, bool enableBroadcast) => new RecordingSocket(this);
    private sealed class RecordingSocket(RecordingSocketFactory owner) : IControllerUdpSocket
    {
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken token)
        {
            owner.OnSend?.Invoke(datagram.ToArray(), destination);
            return ValueTask.FromResult(datagram.Length);
        }
        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Receive should end by cancellation.");
        }
        public void Dispose() { }
    }
}
