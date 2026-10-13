using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller;

internal sealed partial class MainViewModel
{
    private readonly FileProgramStateStore programStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FactoryTimer.Controller", "production_program.json"));
    private SegmentedProgramCoordinator? productionProgram;
    private string? productionProgramLoadError;
    private CancellationTokenSource? programCancellation;
    private Task programTask = Task.CompletedTask;
    private ProgramSegment? productionSegmentContext;
    private bool programArmSucceeded;
    private bool programRetryAllowed;
    private string programDriftAlert = "";
    private bool recoveryProbeActive;
    private bool programManagementBusy;
    private readonly HashSet<ulong> auditedProgramCommands = [];
    private AwakeLease? programAwakeLease;
    private double repetitionsInput = 1;
    private double segmentCapSecondsInput = 6000;
    private string productionProgramText = "No production program. Choose a duration and repetitions.";
    private readonly Dictionary<string, ulong> expectedExecutionReplies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)> matchedExecutionReplies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)> latestProgramExecution = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (StatusPacket Packet, DateTimeOffset At)> latestProgramHealth = new(StringComparer.Ordinal);
    private readonly SegmentDriftMonitor programDrift = new();
    private readonly List<string> programSyncSamples = [];
    private bool UseExecutionEvidence => productionSegmentContext is not null || recoveryProbeActive;

    public double RepetitionsInput { get => repetitionsInput; set => SetProperty(ref repetitionsInput, value); }
    public double SegmentCapSecondsInput { get => segmentCapSecondsInput; set => SetProperty(ref segmentCapSecondsInput, value); }
    public string ProductionProgramText { get => productionProgramText; private set => SetProperty(ref productionProgramText, value); }
    public bool CanManageProgram => !programManagementBusy && !isSending && !IsBenchmarkRunning && productionProgram is not null;

    private void LoadProductionProgram()
    {
        try
        {
            ProgramExecutionState? saved = programStore.Load();
            if (saved is null) return;
            productionProgram = new(programStore, saved);
            if (saved.Active)
                ProductionProgramText = $"Saved plan {saved.Program.PlanId:X16}: {saved.Stage}. Use RECONCILE before another START.";
        }
        catch (Exception exception)
        {
            productionProgramLoadError = exception.Message;
            ProductionProgramText = "Program recovery held: " + exception.Message;
        }
    }

    private bool HasFreshExecutionReply(string deviceId, DateTimeOffset after) =>
        expectedExecutionReplies.TryGetValue(deviceId, out ulong request) &&
        matchedExecutionReplies.TryGetValue(deviceId, out var reply) &&
        reply.Packet.ResponseId == request && reply.At >= after;

    private void ObserveProgramExecution(DeviceViewModel device, ExecutionStatusPacket packet, IPAddress address)
    {
        if (!device.TryGetIpAddress(out IPAddress? expectedAddress) || expectedAddress is null || !expectedAddress.Equals(address)) return;
        if (packet.ResponseId != 0 && (!expectedExecutionReplies.TryGetValue(device.DeviceId, out ulong request) || request != packet.ResponseId))
        {
            // Unsolicited discovery replies are useful outside a bounded probe;
            // they never satisfy a reconciliation or final ARM challenge.
            if (UseExecutionEvidence) return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        latestProgramExecution[device.DeviceId] = (packet, now);
        if (packet.ResponseId != 0 && expectedExecutionReplies.GetValueOrDefault(device.DeviceId) == packet.ResponseId)
            matchedExecutionReplies[device.DeviceId] = (packet, now);
        device.ApplyExecution(packet);
        if (packet.PlanId != 0 && (packet.State is TimerState.Armed or TimerState.Running) && !productionSilentRunOwnsExactWindow)
            ScheduleRecoveredRunningStatusPause(checked((uint)Math.Ceiling(
                (packet.SegmentDurationSeconds * 1_000_000d - packet.ElapsedSegmentMicroseconds) / 1_000_000d)));
    }

    private Dictionary<string, ProgramDeviceObservation> CurrentProgramObservations(DateTimeOffset? after = null)
    {
        var result = new Dictionary<string, ProgramDeviceObservation>(StringComparer.Ordinal);
        if (productionProgram is null) return result;
        foreach (string id in productionProgram.State.Program.Participants)
        {
            if (!latestProgramHealth.TryGetValue(id, out var health)) continue;
            var execution = after.HasValue
                ? matchedExecutionReplies.GetValueOrDefault(id) : latestProgramExecution.GetValueOrDefault(id);
            if (execution.Packet is null || (after.HasValue &&
                (health.At < after || !HasFreshExecutionReply(id, after.Value))) ||
                health.Packet.CommandId != execution.Packet.CommandId || health.Packet.State != execution.Packet.State) continue;
            result[id] = new(execution.Packet, health.Packet);
        }
        return result;
    }

    private DeviceViewModel[] ProgramParticipants() => productionProgram!.State.Program.Participants
        .Select(id => Devices.Single(d => d.DeviceId == id)).ToArray();

    private async Task<Dictionary<string, ProgramDeviceObservation>> ProbeProgramAsync(CancellationToken token)
    {
        recoveryProbeActive = true;
        try
        {
            DeviceViewModel[] participants = ProgramParticipants();
            Dictionary<string, ProgramDeviceObservation> observations = [];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                DateTimeOffset after = DateTimeOffset.UtcNow;
                await RequestFreshStatusesAsync(participants, token);
                await WaitForFreshStatusesAsync(participants, after, token);
                observations = CurrentProgramObservations(after);
                if (observations.Count == participants.Length) break;
            }
            return observations;
        }
        finally { recoveryProbeActive = false; }
    }

    private async Task ManageProgramAsync(Func<Task> action)
    {
        if (programManagementBusy) return;
        programManagementBusy = true;
        UpdateCanSend();
        try { await action(); }
        finally { programManagementBusy = false; UpdateCanSend(); }
    }

    private Task StartRepetitionProgramAsync() => !CanSend ? Task.CompletedTask : ManageProgramAsync(StartRepetitionProgramCoreAsync);
    public Task ReconcileProductionProgramAsync() => ManageProgramAsync(ReconcileProductionProgramCoreAsync);
    public Task ResumeProductionProgramAsync() => ManageProgramAsync(ResumeProductionProgramCoreAsync);
    public Task StopRepetitionProgramAsync() => ManageProgramAsync(StopRepetitionProgramCoreAsync);

    private async Task StartRepetitionProgramCoreAsync()
    {
        if (productionProgramLoadError is not null || productionProgram?.State.Active == true)
        {
            StatusMessage = "Complete, reconcile, or stop the saved program before creating another.";
            return;
        }
        if (!TryDuration(out uint duration)) return;
        if (!double.IsFinite(RepetitionsInput) || RepetitionsInput != Math.Truncate(RepetitionsInput) ||
            RepetitionsInput is < 1 or > 1_000_000 || !double.IsFinite(SegmentCapSecondsInput) ||
            SegmentCapSecondsInput != Math.Truncate(SegmentCapSecondsInput) ||
            SegmentCapSecondsInput < duration || SegmentCapSecondsInput > 6000)
        {
            StatusMessage = "Use a whole repetition count 1–1,000,000 and a segment cap from the countdown duration to 6000 s.";
            return;
        }
        try
        {
            RepetitionProgram plan = RepetitionProgram.Create(CreateCommandId(), duration, (uint)RepetitionsInput,
                Devices.Where(d => d.IsSelected).Select(d => d.DeviceId), (uint)SegmentCapSecondsInput);
            ProgramExecutionState state = ProgramExecutionState.New(plan, productionProgram?.State);
            programStore.Save(state);
            productionProgram = new(programStore, state);
            programDriftAlert = "";
            programDrift.Reset();
            programAwakeLease ??= new();
            await StartProgramLoopAsync();
        }
        catch (Exception exception) { StatusMessage = "Production program held: " + exception.Message; }
        UpdateProductionProgramText();
        UpdateCanSend();
    }

    private void ApplyProductionAbortResult(AbortConfirmationResult abort)
    {
        programRetryAllowed = false;
        programArmSucceeded = false;
        if (productionProgram is null) return;
        if (!abort.Confirmed || abort.SafeExecution is null)
        {
            productionProgram.Hold("ARM abort unconfirmed; no automatic retry. Reconcile the frozen boards before resuming.");
            return;
        }
        productionProgram.ConfirmArmAbortForRetry(abort.ResetCommandId, abort.ConfirmedAtMasterUs, abort.SafeExecution);
        programRetryAllowed = true;
    }

    private async Task<(Dictionary<string, ProgramDeviceObservation> Observations, ProgramReconciliation Decision)>
        ProbeBoundaryReadinessAsync(CancellationToken token)
    {
        Dictionary<string, ProgramDeviceObservation> observations = [];
        DateTimeOffset? since = productionProgram!.State.ReadinessWaitStartedAtUtc;
        TimeSpan remaining = since.HasValue ? ProductionRecoveryPolicy.ReadinessTimeout - (DateTimeOffset.UtcNow - since.Value)
            : ProductionRecoveryPolicy.ReadinessTimeout;
        remaining = remaining > ProductionRecoveryPolicy.ReadinessTimeout ? ProductionRecoveryPolicy.ReadinessTimeout : remaining;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            while (true)
            {
                observations = await ProbeProgramAsync(deadline.Token);
                ProgramReconciliation decision = productionProgram.Reconcile(observations);
                StatusMessage = decision.Message;
                UpdateProductionProgramText();
                if (decision.Action != ReconcileAction.WaitForReadiness) return (observations, decision);
                await Task.Delay(ProductionRecoveryPolicy.PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            productionProgram.Hold("RTC/post-run temperature recovery exceeded 180 s; fleet remains at 00:00.");
            return (observations, new(ReconcileAction.Hold, productionProgram.State.Message));
        }
    }

    private Task StartProgramLoopAsync()
    {
        if (!programTask.IsCompleted) throw new InvalidOperationException("Program execution is already active.");
        programCancellation?.Dispose();
        programCancellation = new();
        programTask = RunProgramLoopAsync(programCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task RunProgramLoopAsync(CancellationToken token)
    {
        try
        {
            while (productionProgram!.State.Active)
            {
                token.ThrowIfCancellationRequested();
                ProgramExecutionState state = productionProgram.State;
                if (state.Stage is ProgramStage.Ready or ProgramStage.BetweenSegments)
                {
                    if (state.SegmentStartAttempts >= ProductionRecoveryPolicy.MaximumStartAttempts)
                    {
                        productionProgram.Hold("Three safe START attempts were used; resume explicitly to begin another bounded retry cycle.");
                        break;
                    }
                    ProgramSegment upcoming = state.Program.Segments.Single(s => s.Index == state.NextSegmentIndex);
                    if (ProductionTimingBudget.StartAllowanceUs(upcoming.Duration, state.MeasuredPairRatePpm) <= 0)
                    {
                        productionProgram.Hold("Measured worst pair rate exceeds the next segment's 30 ms timing budget, including presentation reserve.");
                        break;
                    }
                    productionProgram.BeginPreparation();
                    productionSegmentContext = state.Program.Segments.Single(s => s.Index == state.NextSegmentIndex);
                    programArmSucceeded = false;
                    programRetryAllowed = false;
                    programSyncSamples.Clear();
                    try { await SendStartAtAsync(QualificationStartMode.Ordinary, productionSegmentContext); }
                    finally { productionSegmentContext = null; }
                    if (!programArmSucceeded)
                    {
                        if (productionProgram.State.Stage == ProgramStage.Preparing && productionProgram.State.CurrentCommandId == 0)
                        {
                            productionProgram.CancelPreparationForRetry();
                            programRetryAllowed = true;
                        }
                        if (ProductionRecoveryPolicy.CanRetry(productionProgram.State.SegmentStartAttempts, programRetryAllowed))
                        {
                            StatusMessage = $"Segment START attempt {productionProgram.State.SegmentStartAttempts}/3 failed safely; retrying the same repetitions.";
                            UpdateProductionProgramText();
                            await Task.Delay(TimeSpan.FromSeconds(2), token);
                            continue;
                        }
                        productionProgram.Hold(programRetryAllowed ? "Three safe START attempts failed; repetition progress preserved. " + StartSynchronizationResult :
                            "Segment ARM cancellation is unconfirmed; no retry. " + StartSynchronizationResult);
                        break;
                    }
                    state = productionProgram.State;
                }
                if (state.Stage is not (ProgramStage.Running or ProgramStage.WaitingForReadiness)) break;
                if (state.ControllerSessionId == controllerSessionId)
                {
                    long end = checked(state.TargetMasterMicroseconds + (long)state.CurrentSegment!.Duration * 1_000_000L + 750_000);
                    await WaitUntilMasterTimeAsync(end, token);
                }
                else
                {
                    // Recovery has a new PC clock origin. Its one-shot device
                    // snapshot supplies remaining time; old T* is never reused.
                    uint remaining = CurrentProgramObservations().Values
                        .Where(x => x.Execution.State is TimerState.Armed or TimerState.Running)
                        .Select(x => checked((uint)Math.Ceiling((x.Execution.SegmentDurationSeconds * 1_000_000d -
                            x.Execution.ElapsedSegmentMicroseconds) / 1_000_000d))).DefaultIfEmpty(0u).Max();
                    await Task.Delay(TimeSpan.FromSeconds(remaining + 1), token);
                }
                await productionSilentRunTask.WaitAsync(token);
                var (observations, decision) = await ProbeBoundaryReadinessAsync(token);
                await WriteProgramSegmentAuditAsync(observations);
                UpdateProductionProgramText();
                if (decision.Action == ReconcileAction.ObserveActiveSegment)
                {
                    // A late/slewed device is still active. Keep the fleet intact.
                    await PauseForRecoveredSegmentAsync(decision.RemainingSeconds ?? 1, token);
                    continue;
                }
                if (decision.Action is ReconcileAction.Hold or ReconcileAction.Complete || productionProgram.State.Stage == ProgramStage.Held) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            try { productionProgram?.Hold("Program held: " + exception.Message); }
            catch { productionProgramLoadError = "Cannot persist program state: " + exception.Message; }
        }
        finally
        {
            productionSegmentContext = null;
            if (productionProgram?.State.Active != true) { programAwakeLease?.Dispose(); programAwakeLease = null; }
            UpdateProductionProgramText();
            UpdateCanSend();
        }
    }

    private async Task PauseForRecoveredSegmentAsync(uint remaining, CancellationToken token)
    {
        productionSilentRunActive = true;
        productionSilentRunOwnsExactWindow = false;
        foreach (DeviceViewModel device in ProgramParticipants()) device.BeginIntentionalStatusPause();
        await statusDiscovery.StopAndWaitAsync();
        productionSilentRunCancellation?.Cancel();
        ScheduleRecoveredRunningStatusPause(remaining);
        await Task.Delay(TimeSpan.FromSeconds(remaining + 1), token);
    }

    private async Task ReconcileProductionProgramCoreAsync()
    {
        if (productionProgram?.State.Active != true || isSending || IsBenchmarkRunning) return;
        try
        {
            programCancellation?.Cancel();
            await programTask;
            await statusDiscovery.StopAndWaitAsync();
            var (observations, decision) = await ProbeBoundaryReadinessAsync(CancellationToken.None);
            StatusMessage = decision.Message;
            programAwakeLease ??= new();
            if (decision.Action == ReconcileAction.ObserveActiveSegment)
            {
                // UI reconstruction is an estimate from whole-second progress.
                // The boards continue their original disciplined schedules.
                ProgramSegment segment = productionProgram.State.CurrentSegment!;
                uint remaining = decision.RemainingSeconds!.Value;
                long phase = observations.Values.Where(x => x.Execution.State is TimerState.Armed or TimerState.Running)
                    .Min(x => x.Execution.ElapsedSegmentMicroseconds);
                long target = MasterClock.NowMicroseconds - phase;
                clock.ArmSegmentAt(segment.DurationSeconds, segment.RepeatCount, target,
                    segment.FirstRepeat, productionProgram.State.Program.TotalRepeats);
                productionSilentRunActive = true;
                productionSilentRunOwnsExactWindow = false;
                foreach (DeviceViewModel device in ProgramParticipants()) device.BeginIntentionalStatusPause();
                ScheduleRecoveredRunningStatusPause(remaining);
                await StartProgramLoopAsync();
            }
            else if (decision.Action == ReconcileAction.SynchronizeNextSegment) await StartProgramLoopAsync();
            else {
                productionSilentRunCancellation?.Cancel();
                ResumeAutomaticStatusDiscoveryAfterRun();
                if (decision.Action == ReconcileAction.Complete) { programAwakeLease?.Dispose(); programAwakeLease = null; }
            }
        }
        catch (Exception exception) { StatusMessage = "Reconciliation held: " + exception.Message; }
        UpdateProductionProgramText();
        UpdateCanSend();
    }

    private async Task ResumeProductionProgramCoreAsync()
    {
        if (productionProgram?.State.Stage != ProgramStage.Held || isSending || !programTask.IsCompleted) return;
        try
        {
            var observations = await ProbeProgramAsync(CancellationToken.None);
            productionProgram.ResumeAfterOperatorReview(observations);
            programDrift.Reset();
            programAwakeLease ??= new();
            await StartProgramLoopAsync();
        }
        catch (Exception exception) { StatusMessage = "Fleet remains held: " + exception.Message; }
        UpdateProductionProgramText();
        UpdateCanSend();
    }

    private async Task StopRepetitionProgramCoreAsync()
    {
        if (productionProgram is null) return;
        programCancellation?.Cancel();
        await programTask; // A finite prepare/abort already in progress owns sendLock.
        try
        {
            productionProgram.Stop();
            productionSilentRunCancellation?.Cancel();
            ResumeAutomaticStatusDiscoveryAfterRun();
            await sendLock.WaitAsync();
            try
            {
                var reset = new CommandPacket(CommandType.Reset, CreateCommandId(), productionProgram.State.Program.DurationSeconds, 0);
                foreach (DeviceViewModel device in ProgramParticipants())
                    if (device.TryGetIpAddress(out IPAddress? address) && address is not null)
                        await udpService.SendCommandUnicastAsync(reset, address);
                clock.Reset(productionProgram.State.Program.DurationSeconds);
                StatusMessage = "Program stopped. RESET sent to its frozen participants; verify every board is idle.";
            }
            finally { sendLock.Release(); }
        }
        catch (Exception exception) { StatusMessage = "Program stopped locally; RESET delivery needs checking: " + exception.Message; }
        programAwakeLease?.Dispose(); programAwakeLease = null;
        UpdateProductionProgramText(); UpdateCanSend();
    }

    private void UpdateProductionProgramText()
    {
        if (productionProgramLoadError is not null) { ProductionProgramText = "RECOVERY HELD: " + productionProgramLoadError; return; }
        if (productionProgram is null) return;
        ProgramExecutionState state = productionProgram.State;
        string plan = string.Join(" + ", state.Program.Segments.Take(12).Select(s => s.RepeatCount)) +
            (state.Program.Segments.Count > 12 ? " + …" : "");
        uint repeat = state.Stage == ProgramStage.Running ? clock.CurrentRepeat :
            state.Stage is ProgramStage.Arming or ProgramStage.Preparing ? state.CurrentSegment?.FirstRepeat ?? 0 :
            state.Stage == ProgramStage.WaitingForReadiness ? state.CurrentSegment?.LastRepeat ?? state.CompletedRepeatCount :
            state.CompletedRepeatCount;
        ProductionProgramText = $"Plan {state.Program.PlanId:X16} | {state.Stage} | repeat {repeat}/{state.Program.TotalRepeats} | " +
            $"segment {state.CurrentSegmentIndex}/{state.Program.Segments.Count} | plan {plan}\n" +
            $"Frozen fleet: {string.Join(", ", state.Program.Participants)}. {state.Message}" +
            (programDriftAlert.Length == 0 ? "" : "\n" + programDriftAlert);
        OnPropertyChanged(nameof(CanManageProgram));
    }

    private void RecordProgramSyncSample(string device, ulong syncId, SyncReplyPacket reply, ClockSyncSample sample)
    {
        if (productionSegmentContext is null || productionProgram is null) return;
        programSyncSamples.Add(FormattableString.Invariant(
            $"{productionProgram.State.Program.PlanId:X16},{productionSegmentContext.Index},{controllerSessionId},{device},{syncId:X16},{reply.Bssid ?? "UNKNOWN"},{sample.MasterT1Microseconds},{sample.DeviceT2Microseconds},{sample.DeviceT3Microseconds},{sample.MasterT4Microseconds},{sample.MasterMinusLocalOffsetMicroseconds},{sample.NetworkRttMicroseconds}"));
    }

    private async Task WriteProgramSegmentAuditAsync(IReadOnlyDictionary<string, ProgramDeviceObservation> observations)
    {
        if (productionProgram is null) return;
        ProgramExecutionState state = productionProgram.State;
        if (state.CurrentCommandId == 0 || observations.Values.Any(x => x.Execution.State is TimerState.Armed or TimerState.Running) ||
            !auditedProgramCommands.Add(state.CurrentCommandId)) return;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryTimer.Controller", "program_logs");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, $"program_{state.Program.PlanId:X16}_segment_{state.CurrentSegmentIndex}");
        if (programSyncSamples.Count != 0)
            await File.WriteAllLinesAsync(stem + "_sync.csv", new[] {
                "PlanId,SegmentId,ControllerSessionId,Device,SyncId,Bssid,T1,T2,T3,T4,MasterMinusLocalUs,RttUs" }.Concat(programSyncSamples), Encoding.UTF8);
        var executionLines = new List<string> {
            "PlanId,SegmentId,CommandId,Device,BootId,FirmwareBuildId,State,Repeat,ElapsedSeconds,ElapsedUs,RecoveredEdges,AcceptedEdges,MissingEdges,Holdovers,QueueDrops,TemperatureValid,DiagnosticFlags" };
        executionLines.AddRange(observations.Values.OrderBy(x => x.Execution.DeviceId).Select(x => FormattableString.Invariant(
            $"{x.Execution.PlanId:X16},{x.Execution.SegmentIndex},{x.Execution.CommandId:X16},{x.Execution.DeviceId},{x.Execution.BootId:X16},{x.Health.FirmwareBuildId},{x.Execution.State},{x.Execution.CurrentRepeat},{x.Execution.ElapsedSegmentSeconds},{x.Execution.ElapsedSegmentMicroseconds},{x.Execution.RtcRecoveredEdges},{x.Health.RtcAcceptedEdges},{x.Health.RtcInferredMissingEdges},{x.Health.RtcHoldoverEntries},{x.Health.RtcQueueDrops},{x.Health.RtcTemperatureValid},{x.Health.DiagnosticFlags:X}")));
        await File.WriteAllLinesAsync(stem + "_execution.csv", executionLines, Encoding.UTF8);
        if (state.ControllerSessionId != controllerSessionId)
        {
            programDrift.Reset(); // A restarted controller has a different master-clock origin.
            return;
        }
        var points = observations.Values.Where(x => x.Health.SyncEpochLocalMicroseconds > 0 &&
            x.Health.SyncEpochDisciplinedMicroseconds > 0 && x.Health.SyncSourceOffsetMicroseconds.HasValue)
            .Select(x => new RtcSegmentDriftPoint(x.Execution.DeviceId, x.Execution.BootId, x.Health.FirmwareBuildId!,
                checked(x.Health.SyncEpochLocalMicroseconds!.Value + x.Health.SyncSourceOffsetMicroseconds!.Value),
                x.Health.SyncEpochDisciplinedMicroseconds!.Value, x.Health.Bssid ?? "UNKNOWN")).ToArray();
        IReadOnlyList<RtcSegmentDriftEstimate> estimates = programDrift.Observe(points);
        var lines = new List<string> { "Device,RtcVsMasterPpm,RelativeFleetPpm,InnovationUs,ValidationRequired,SustainedDeviation,ControllerSessionId" };
        lines.AddRange(estimates.Select(x => FormattableString.Invariant(
            $"{x.DeviceId},{x.RatePpm:R},{x.RelativeFleetPpm:R},{x.InnovationUs:R},{x.ValidationRequired},{x.SustainedDeviation},{controllerSessionId}")));
        await File.WriteAllLinesAsync(stem + "_drift.csv", lines, Encoding.UTF8);
        if (estimates.Count == state.Program.Participants.Count && estimates.Count >= 2)
            productionProgram.RecordMeasuredPairRate(estimates.Max(x => x.RatePpm) - estimates.Min(x => x.RatePpm));
        string[] flagged = estimates.Where(x => x.ValidationRequired || x.SustainedDeviation).Select(x => x.DeviceId).ToArray();
        if (flagged.Length != 0)
        {
            programDriftAlert = "RTC drift alert (informational): " + string.Join(", ", flagged);
            StatusMessage = programDriftAlert;
            // Phase innovations and median-relative warnings are alerts. Fresh
            // synchronization and the measured pair timing budget gate the next START.
        }
    }

    private sealed class AwakeLease : IDisposable
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        public AwakeLease() { if (OperatingSystem.IsWindows()) SetThreadExecutionState(0x80000001); }
        public void Dispose() { if (OperatingSystem.IsWindows()) SetThreadExecutionState(0x80000000); }
    }
}
