using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller;

internal sealed partial class MainViewModel
{
    private readonly IProgramStateStore programStore = new FileProgramStateStore(Path.Combine(
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
    private readonly HashSet<string> auditedRtcWaitTransitions = new(StringComparer.Ordinal);
    private AwakeLease? programAwakeLease;
    private double repetitionsInput = 1;
    private double segmentCapSecondsInput = 6000;
    private bool signalEveryRepetitionStart;
    private bool syncEveryRepetition;
    private string productionProgramText = "No production program. Choose a duration and repetitions.";
    private readonly Dictionary<string, ulong> expectedExecutionReplies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)> matchedExecutionReplies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ulong Request, IPAddress Address)> matchedExecutionAddresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ExecutionStatusPacket Packet, DateTimeOffset At)> latestProgramExecution = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (StatusPacket Packet, DateTimeOffset At)> latestProgramHealth = new(StringComparer.Ordinal);
    private readonly SegmentDriftMonitor programDrift = new();
    private readonly List<string> programSyncSamples = [];
    private bool UseExecutionEvidence => productionSegmentContext is not null || recoveryProbeActive;

    public double RepetitionsInput { get => repetitionsInput; set => SetProperty(ref repetitionsInput, value); }
    public double SegmentCapSecondsInput { get => segmentCapSecondsInput; set => SetProperty(ref segmentCapSecondsInput, value); }
    public bool SignalEveryRepetitionStart { get => signalEveryRepetitionStart; set => SetProperty(ref signalEveryRepetitionStart, value); }
    public bool SyncEveryRepetition
    {
        get => syncEveryRepetition;
        set { if (SetProperty(ref syncEveryRepetition, value)) OnPropertyChanged(nameof(CanEditResyncLimit)); }
    }
    public bool CanEditRepetitionOptions => CanEditCountdownSettings && productionProgram?.State.StopCheckpoint is not { DiscardProgress: false };
    public bool CanEditResyncLimit => CanEditRepetitionOptions && !SyncEveryRepetition;
    public string ProductionProgramText { get => productionProgramText; private set => SetProperty(ref productionProgramText, value); }
    public bool CanManageProgram => !completionResetInProgress && !programManagementBusy && !isSending && !IsBenchmarkRunning && productionProgram is not null;

    private void LoadProductionProgram()
    {
        try
        {
            ProgramExecutionState? saved = programStore.Load();
            if (saved is null) return;
            productionProgram = new(programStore, saved);
            DurationMinutesInput = saved.Program.DurationSeconds / 60;
            DurationSecondsInput = saved.Program.DurationSeconds % 60;
            RepetitionsInput = saved.Program.TotalRepeats;
            SegmentCapSecondsInput = saved.Program.SegmentCapSeconds;
            SignalEveryRepetitionStart = saved.Program.RepeatStartSeconds != 0;
            SyncEveryRepetition = saved.Program.SyncEveryRepetition;
            foreach (DeviceViewModel device in Devices) device.IsSelected = saved.Program.Participants.Contains(device.DeviceId);
            if (saved.StopCheckpoint is { DiscardProgress: false } checkpoint)
                clock.Pause(checkpoint.RemainingSeconds, checkpoint.CurrentRepeat, saved.Program.TotalRepeats);
            else if (saved.Stage == ProgramStage.Stopped)
                clock.Reset(saved.Program.DurationSeconds);
            if (saved.Active)
                ProductionProgramText = $"Saved plan {saved.Program.PlanId:X16}: {saved.Stage}. Use RECONCILE before another START.";
            UpdateProductionProgramText();
            RefreshClockProperties();
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
        {
            matchedExecutionReplies[device.DeviceId] = (packet, now);
            matchedExecutionAddresses[device.DeviceId] = (packet.ResponseId, address);
        }
        device.ApplyExecution(packet);
        if (!operatorStopInProgress && productionProgram?.State.StopCheckpoint is null &&
            (productionProgram is null || productionProgram.State.Program.Participants.Contains(device.DeviceId)) &&
            packet.PlanId != 0 && (packet.State is TimerState.Armed or TimerState.Running) && !productionSilentRunOwnsExactWindow)
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

    private async Task<Dictionary<string, ProgramDeviceObservation>> ProbeProgramAsync(CancellationToken token,
        IReadOnlyList<DeviceViewModel>? targets = null)
    {
        recoveryProbeActive = true;
        try
        {
            DeviceViewModel[] participants = targets?.ToArray() ?? ProgramParticipants();
            Dictionary<string, ProgramDeviceObservation> observations = [];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                DateTimeOffset after = DateTimeOffset.UtcNow;
                await RequestFreshStatusesAsync(participants, token);
                await WaitForFreshStatusesAsync(participants, after, token);
                observations = CurrentProgramObservations(after).Where(pair => participants.Any(d => d.DeviceId == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
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
        catch (Exception exception) { StatusMessage = "Countdown operation held: " + exception.Message; }
        finally { programManagementBusy = false; UpdateCanSend(); }
    }

    public Task ReconcileProductionProgramAsync() => ManageProgramAsync(ReconcileProductionProgramCoreAsync);
    public Task ResumeProductionProgramAsync() => ManageProgramAsync(ResumeProductionProgramCoreAsync);
    public Task StopRepetitionProgramAsync() => StopCountdownAsync();

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
            SegmentCapSecondsInput < 1 || SegmentCapSecondsInput > 6000 || (!SyncEveryRepetition && SegmentCapSecondsInput < duration))
        {
            StatusMessage = "Use a whole repetition count 1–1,000,000 and a segment cap from the countdown duration to 6000 s.";
            return;
        }
        DateTimeOffset admissionTime = DateTimeOffset.UtcNow;
        DeviceViewModel[] chosen = Devices.Where(d => d.IsSelected).ToArray();
        IReadOnlyList<StartBlock> admissionBlocks = InitialRosterAdmission.Evaluate(chosen.Select(d =>
            new InitialRosterParticipant(d.DeviceId, d.TryGetIpAddress(out _), d.IsOnlineAt(admissionTime))));
        if (admissionBlocks.Count != 0)
        {
            ReportStartBlocks(admissionBlocks);
            StatusMessage += " Correct the selection and press START again; no countdown was created.";
            return;
        }
        try
        {
            RepetitionProgram plan = RepetitionProgram.Create(CreateCommandId(), duration, (uint)RepetitionsInput,
                chosen.Select(d => d.DeviceId), (uint)SegmentCapSecondsInput, FactoryProtocol.RepetitionLogoSeconds,
                SignalEveryRepetitionStart ? FactoryProtocol.RepetitionLogoSeconds : 0u, SyncEveryRepetition);
            ProgramExecutionState state = ProgramExecutionState.New(plan, productionProgram?.State);
            programStore.Save(state);
            productionProgram = new(programStore, state);
            programDriftAlert = "";
            programDrift.Reset();
            auditedRtcWaitTransitions.Clear();
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
                ProgramExecutionState before = productionProgram.State;
                // T* is only comparable to this PC clock within the original
                // controller session; do not compare it after a PC restart.
                bool plannedSegmentEnded = before.ControllerSessionId == controllerSessionId &&
                    before.CurrentSegment is ProgramSegment scheduled &&
                    MasterClock.NowMicroseconds >= checked(before.TargetMasterMicroseconds +
                        (long)scheduled.Duration * 1_000_000L + 750_000L);
                ProgramReconciliation decision = productionProgram.Reconcile(
                    observations, plannedSegmentEnded: plannedSegmentEnded);
                await WriteAutomaticRecoveryAuditAsync(before, productionProgram.State, observations);
                StatusMessage = decision.Message;
                UpdateProductionProgramText();
                if (decision.Action != ReconcileAction.WaitForReadiness) return (observations, decision);
                await Task.Delay(ProductionRecoveryPolicy.PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            ProgramExecutionState before = productionProgram.State;
            productionProgram.Hold("Boundary recovery exceeded 10 s (missing device or RTC/temperature); fleet remains at 00:00.");
            await WriteAutomaticRecoveryAuditAsync(before, productionProgram.State, observations);
            StatusMessage = productionProgram.State.Message;
            UpdateProductionProgramText();
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
                    if (ProductionTimingBudget.StartAllowanceUs(upcoming.CountdownSpan, state.MeasuredPairRatePpm) <= 0)
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
                    token.ThrowIfCancellationRequested();
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
            await ResetCompletedProgramAsync();
            if (productionProgram?.State.Active != true) { programAwakeLease?.Dispose(); programAwakeLease = null; }
            UpdateProductionProgramText();
            UpdateCanSend();
        }
    }

    private async Task PauseForRecoveredSegmentAsync(uint remaining, CancellationToken token)
    {
        // Do not extend a same-session segment beyond our authoritative T*.
        // Stale RUNNING reports after 00:00 must be re-probed, not trusted.
        remaining = CapRecoveredRunningPauseToPlannedEnd(remaining);
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
                    segment.FirstRepeat, productionProgram.State.Program.TotalRepeats, segment.LogoSeconds, segment.RepeatStartSeconds);
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
                if (decision.Action == ReconcileAction.Complete)
                {
                    await WriteProgramSegmentAuditAsync(observations);
                    await ResetCompletedProgramAsync();
                    programAwakeLease?.Dispose(); programAwakeLease = null;
                }
            }
        }
        catch (Exception exception) { StatusMessage = "Reconciliation held: " + exception.Message; }
        UpdateProductionProgramText();
        UpdateCanSend();
    }

    // Only comparable with this PC's master clock while the same controller
    // session remains alive. This also caps unsolicited STATUS/UI pauses.
    private uint CapRecoveredRunningPauseToPlannedEnd(uint reportedSeconds)
    {
        ProgramExecutionState? state = productionProgram?.State;
        if (state is null || state.ControllerSessionId != controllerSessionId ||
            state.CurrentSegment is not ProgramSegment segment ||
            state.TargetMasterMicroseconds <= 0 ||
            state.Stage is not (ProgramStage.Running or ProgramStage.WaitingForReadiness))
            return reportedSeconds;
        long deadline = checked(state.TargetMasterMicroseconds +
            (long)segment.Duration * 1_000_000L + 750_000L);
        long untilDeadlineUs = deadline - MasterClock.NowMicroseconds;
        uint seconds = untilDeadlineUs <= 0 ? 1u :
            checked((uint)Math.Ceiling(untilDeadlineUs / 1_000_000d));
        return Math.Min(reportedSeconds, seconds);
    }

    private async Task ResumeProductionProgramCoreAsync()
    {
        if (productionProgram?.State.Stage != ProgramStage.Held || isSending || !programTask.IsCompleted) return;
        try
        {
            var observations = await ProbeProgramAsync(CancellationToken.None);
            ProgramExecutionState before = productionProgram.State;
            // Preserve the exact observations even if the authorization fails.
            await WriteProgramResumeAuditAsync(before, observations);
            try
            {
                productionProgram.ResumeAfterOperatorReview(observations);
            }
            catch (Exception refusal)
            {
                try { await WriteProgramResumeOutcomeAuditAsync(before, "REFUSED", refusal.Message); }
                catch (Exception auditError) { StatusMessage = "RESUME refusal audit failed: " + auditError.Message; }
                throw;
            }
            // Once the coordinator has accepted RESUME, audit I/O must never
            // prevent continuation or silently leave an active plan stranded.
            string? auditWarning = null;
            try
            {
                await WriteProgramResumeOutcomeAuditAsync(before, "ACCEPTED", "Fleet qualified; RESUME authorized.");
                await AppendRecoveredSegmentExecutionAuditAsync(before, observations);
            }
            catch (Exception auditError) { auditWarning = "RESUME audit incomplete: " + auditError.Message; }
            programDrift.Reset();
            programAwakeLease ??= new();
            await StartProgramLoopAsync();
            if (auditWarning is not null) StatusMessage = auditWarning;
        }
        catch (Exception exception) { StatusMessage = "Fleet remains held: " + exception.Message; }
        UpdateProductionProgramText();
        UpdateCanSend();
    }

    private void UpdateProductionProgramText()
    {
        if (productionProgramLoadError is not null) { ProductionProgramText = "RECOVERY HELD: " + productionProgramLoadError; return; }
        if (productionProgram is null) return;
        ProgramExecutionState state = productionProgram.State;
        if (state.StopCheckpoint is { } checkpoint)
        {
            ProductionProgramText = checkpoint.DiscardProgress ? "RESET — START begins a new countdown." :
                $"PAUSED — {FormatMinutesSeconds(checkpoint.RemainingSeconds)} — repetition {checkpoint.CurrentRepeat}/{state.Program.TotalRepeats}. START continues; RESET clears progress.";
            if (!checkpoint.PositionKnown && !checkpoint.DiscardProgress)
                ProductionProgramText = "STOPPED — saved position unavailable. RESET, then START for a new countdown.";
            if (checkpoint.PendingDeviceIds.Count != 0)
                ProductionProgramText += "\nWaiting for stop confirmation: " + string.Join(", ", checkpoint.PendingDeviceIds);
            NotifyCountdownControls();
            return;
        }
        string plan = string.Join(" + ", state.Program.Segments.Take(12).Select(s => s.RepeatCount)) +
            (state.Program.Segments.Count > 12 ? " + …" : "");
        uint repeat = state.Stage == ProgramStage.Running ? clock.CurrentRepeat :
            state.Stage is ProgramStage.Arming or ProgramStage.Preparing ? state.CurrentSegment?.FirstRepeat ?? 0 :
            state.Stage == ProgramStage.WaitingForReadiness ? state.CurrentSegment?.LastRepeat ?? state.CompletedRepeatCount :
            state.CompletedRepeatCount;
        ProductionProgramText = $"Plan {state.Program.PlanId:X16} | {state.Stage} | repeat {repeat}/{state.Program.TotalRepeats} | " +
            $"segment {state.CurrentSegmentIndex}/{state.Program.Segments.Count} | plan {plan}\n" +
            $"Frozen fleet: {string.Join(", ", state.Program.Participants)}. {state.Message}" +
            (state.Program.RepeatStartSeconds == 0 ? "" : "\nStart logo/output: every repetition.") +
            (state.Program.SyncEveryRepetition ? "\nSynchronization: every repetition." : "") +
            (programDriftAlert.Length == 0 ? "" : "\n" + programDriftAlert);
        OnPropertyChanged(nameof(CanManageProgram));
        NotifyCountdownControls();
    }

    private void RecordProgramSyncSample(string device, ulong syncId, SyncReplyPacket reply, ClockSyncSample sample)
    {
        if (productionSegmentContext is null || productionProgram is null) return;
        programSyncSamples.Add(FormattableString.Invariant(
            $"{productionProgram.State.Program.PlanId:X16},{productionSegmentContext.Index},{controllerSessionId},{device},{syncId:X16},{reply.Bssid ?? "UNKNOWN"},{sample.MasterT1Microseconds},{sample.DeviceT2Microseconds},{sample.DeviceT3Microseconds},{sample.MasterT4Microseconds},{sample.MasterMinusLocalOffsetMicroseconds},{sample.NetworkRttMicroseconds}"));
    }

    private static string ProgramLogDirectory()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FactoryTimer.Controller", "program_logs");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string AuditCsvField(string? value) =>
        "\"" + (value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ResumeAuditFile(ProgramExecutionState state)
    {
        string directory = ProgramLogDirectory();
        string stem = $"program_{state.Program.PlanId:X16}_segment_{state.CurrentSegmentIndex}";
        string filename = Path.Combine(directory, stem + "_resume.csv");
        // Never append 27-column rc2 rows to an earlier 18-column audit.
        if (File.Exists(filename) &&
            File.ReadLines(filename).FirstOrDefault()?.Contains("ReadinessReason", StringComparison.Ordinal) != true)
            return Path.Combine(directory, stem + "_resume_rc2.csv");
        return filename;
    }

    private const string ResumeAuditHeader =
        "ObservedAtUtc,PlanId,SegmentId,ExpectedCommandId,Device,Present,BootId,ExpectedBootId,SameBoot,FirmwareBuildId,ExpectedBuildId,State,CommandId,PlanMatch,SegmentMatch,TargetMatch,CompletedUs,ExpectedDurationUs," +
        "RtcState,RtcFitRmsUs,RtcFitPoints,RtcTemperatureValid,OperationalReady,ReadinessReason,Event,Outcome,DecisionReason";

    private static async Task WriteProgramResumeAuditAsync(ProgramExecutionState state,
        IReadOnlyDictionary<string, ProgramDeviceObservation> observations)
    {
        string filename = ResumeAuditFile(state);
        var lines = new List<string>();
        if (!File.Exists(filename)) lines.Add(ResumeAuditHeader);
        ProgramSegment? segment = state.CurrentSegment;
        long expectedDurationUs = segment is null ? 0L : checked((long)segment.Duration * 1_000_000);
        string utc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string id in state.Program.Participants)
        {
            state.BootIds.TryGetValue(id, out ulong expectedBoot);
            state.FirmwareBuildIds.TryGetValue(id, out string? expectedBuild);
            if (!observations.TryGetValue(id, out ProgramDeviceObservation? observed))
            {
                lines.Add(FormattableString.Invariant(
                    $"{utc},{state.Program.PlanId:X16},{state.CurrentSegmentIndex},{state.CurrentCommandId:X16},{id},False,,{expectedBoot:X16},False,,{expectedBuild},MISSING,,,,,,{expectedDurationUs},,,,,{false},{AuditCsvField(ProductionRecoveryPolicy.OperationalReadinessReason(null))},OBSERVATION,PENDING,"));
                continue;
            }
            var e = observed.Execution;
            var h = observed.Health;
            bool sameBoot = expectedBoot != 0 && e.BootId == expectedBoot &&
                expectedBuild == h.FirmwareBuildId;
            bool planMatch = e.PlanId == state.Program.PlanId;
            bool segmentMatch = segment != null && e.SegmentIndex == segment.Index &&
                e.DurationSeconds == segment.DurationSeconds && e.RepeatCount == segment.RepeatCount &&
                e.LogoSeconds.GetValueOrDefault() == segment.LogoSeconds &&
                e.RepeatStartSeconds.GetValueOrDefault() == segment.RepeatStartSeconds &&
                e.FirstRepeat == segment.FirstRepeat && e.TotalRepeats == state.Program.TotalRepeats;
            bool targetMatch = e.TargetMasterMicroseconds == state.TargetMasterMicroseconds;
            string readyReason = ProductionRecoveryPolicy.OperationalReadinessReason(observed);
            lines.Add(FormattableString.Invariant(
                $"{utc},{state.Program.PlanId:X16},{state.CurrentSegmentIndex},{state.CurrentCommandId:X16},{id},True,{e.BootId:X16},{expectedBoot:X16},{sameBoot},{h.FirmwareBuildId},{expectedBuild},{e.State},{e.CommandId:X16},{planMatch},{segmentMatch},{targetMatch},{e.ElapsedSegmentMicroseconds},{expectedDurationUs},{h.RtcState},{h.RtcFitRmsMicroseconds},{h.RtcFitPoints},{h.RtcTemperatureValid},{readyReason == "READY"},{AuditCsvField(readyReason)},OBSERVATION,PENDING,"));
        }
        await File.AppendAllLinesAsync(filename, lines, Encoding.UTF8);
    }

    private static async Task WriteProgramResumeOutcomeAuditAsync(ProgramExecutionState state,
        string outcome, string reason)
    {
        string filename = ResumeAuditFile(state);
        string prefix = string.Join(",", new[] {
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            state.Program.PlanId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            state.CurrentSegmentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.CurrentCommandId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            "FLEET" }.Concat(Enumerable.Repeat("", 13)));
        var lines = new List<string>();
        if (!File.Exists(filename)) lines.Add(ResumeAuditHeader);
        lines.Add(prefix + ",,,,,,,DECISION," + outcome + "," + AuditCsvField(reason));
        await File.AppendAllLinesAsync(filename, lines, Encoding.UTF8);
    }

    private async Task WriteAutomaticRecoveryAuditAsync(ProgramExecutionState before,
        ProgramExecutionState after, IReadOnlyDictionary<string, ProgramDeviceObservation> observations)
    {
        // Called after the silent window. Capture transitions only, not every poll.
        string[] wasMissing = (before.PendingMissingParticipants ?? Array.Empty<string>()).ToArray();
        string[] nowMissing = (after.PendingMissingParticipants ?? Array.Empty<string>()).ToArray();
        var beforeCounts = before.AutomaticRecoveryCounts ?? new Dictionary<string, uint>();
        var afterCounts = after.AutomaticRecoveryCounts ?? new Dictionary<string, uint>();
        string[] newlyMissing = nowMissing.Except(wasMissing, StringComparer.Ordinal).ToArray();
        string[] recovered = afterCounts.Keys.Where(id =>
            afterCounts[id] > beforeCounts.GetValueOrDefault(id)).ToArray();
        bool held = before.Stage != ProgramStage.Held && after.Stage == ProgramStage.Held;
        bool timeout = held && after.Message.Contains("10 s", StringComparison.Ordinal);
        var entries = new List<(string Device, string Event)>();
        entries.AddRange(newlyMissing.Select(id => (id, "MISSING_WAIT")));
        entries.AddRange(recovered.Select(id => (id, "AUTO_RECOVERED")));
        if (after.Stage == ProgramStage.WaitingForReadiness)
            foreach (string id in after.Program.Participants)
                if (observations.TryGetValue(id, out ProgramDeviceObservation? obs) &&
                    (obs.Execution.State is TimerState.Finished or TimerState.Ready) &&
                    !SegmentedProgramCoordinator.OperationalReady(obs) &&
                    auditedRtcWaitTransitions.Add($"{after.Program.PlanId:X16}:{after.CurrentSegmentIndex}:{id}"))
                    entries.Add((id, "RTC_WAIT"));
        if (held)
        {
            foreach (string id in after.Program.Participants)
            {
                if (!observations.TryGetValue(id, out ProgramDeviceObservation? obs))
                    entries.Add((id, timeout ? "TIMEOUT_HOLD" : "RECOVERY_HOLD"));
                else if (!SegmentedProgramCoordinator.OperationalReady(obs))
                    entries.Add((id, timeout ?
                        (obs.Execution.State is TimerState.Armed or TimerState.Running ? "STALE_RUNNING_TIMEOUT_HOLD" : "RTC_TIMEOUT_HOLD")
                        : "READINESS_HOLD"));
            }
            if (!entries.Any(x => x.Event.EndsWith("HOLD", StringComparison.Ordinal)))
                entries.Add(("FLEET", timeout ? "TIMEOUT_HOLD" : "RECOVERY_HOLD"));
        }
        if (entries.Count == 0) return;
        string filename = Path.Combine(ProgramLogDirectory(), $"program_{after.Program.PlanId:X16}_recovery.csv");
        const string oldHeader = "Utc,PlanId,SegmentId,Event,Device,Count,BootId,CommandId,State";
        const string extraHeader = ",Reason,RtcState,RtcFitRmsUs,RtcFitPoints,RtcTemperatureValid";
        bool legacy = File.Exists(filename) &&
            File.ReadLines(filename).FirstOrDefault()?.Contains("Reason", StringComparison.Ordinal) != true;
        var lines = new List<string>();
        if (!File.Exists(filename)) lines.Add(oldHeader + extraHeader);
        foreach ((string id, string ev) in entries.Distinct())
        {
            observations.TryGetValue(id, out ProgramDeviceObservation? obs);
            afterCounts.TryGetValue(id, out uint count);
            string line = FormattableString.Invariant(
                $"{DateTimeOffset.UtcNow:O},{after.Program.PlanId:X16},{after.CurrentSegmentIndex},{ev},{id},{count},{obs?.Execution.BootId:X16},{obs?.Execution.CommandId:X16},{obs?.Execution.State}");
            if (!legacy)
            {
                string reason = id == "FLEET" ? after.Message : ProductionRecoveryPolicy.OperationalReadinessReason(obs);
                line += FormattableString.Invariant(
                    $",{AuditCsvField(reason)},{obs?.Health.RtcState},{obs?.Health.RtcFitRmsMicroseconds},{obs?.Health.RtcFitPoints},{obs?.Health.RtcTemperatureValid}");
            }
            lines.Add(line);
        }
        await File.AppendAllLinesAsync(filename, lines, Encoding.UTF8);
    }

    private static async Task AppendRecoveredSegmentExecutionAuditAsync(ProgramExecutionState state,
        IReadOnlyDictionary<string, ProgramDeviceObservation> observations)
    {
        // On HOLD the segment's execution audit contains only reachable boards.
        // After a successful RESUME, append exact same-boot completion evidence
        // for recovered originals without overwriting or duplicating prior rows.
        if (state.CurrentCommandId == 0 || state.CurrentSegment is not ProgramSegment segment) return;
        string filename = Path.Combine(ProgramLogDirectory(),
            $"program_{state.Program.PlanId:X16}_segment_{segment.Index}_execution.csv");
        if (!File.Exists(filename)) return;
        string[] existing = await File.ReadAllLinesAsync(filename, Encoding.UTF8);
        var recorded = existing.Skip(1).Select(line => line.Split(','))
            .Where(fields => fields.Length >= 4).Select(fields => fields[3])
            .ToHashSet(StringComparer.Ordinal);
        var additions = new List<string>();
        foreach (string id in state.Program.Participants)
        {
            if (recorded.Contains(id) || !observations.TryGetValue(id, out ProgramDeviceObservation? x)) continue;
            var e = x.Execution;
            var h = x.Health;
            if (!state.BootIds.TryGetValue(id, out ulong boot) || boot == 0 || boot != e.BootId ||
                !state.FirmwareBuildIds.TryGetValue(id, out string? build) || build != h.FirmwareBuildId ||
                e.PlanId != state.Program.PlanId || e.SegmentIndex != segment.Index ||
                e.CommandId != state.CurrentCommandId || h.CommandId != e.CommandId ||
                e.State != TimerState.Finished || h.State != TimerState.Finished ||
                e.DurationSeconds != segment.DurationSeconds || e.RepeatCount != segment.RepeatCount ||
                e.LogoSeconds.GetValueOrDefault() != segment.LogoSeconds ||
                e.RepeatStartSeconds.GetValueOrDefault() != segment.RepeatStartSeconds ||
                e.FirstRepeat != segment.FirstRepeat || e.TotalRepeats != state.Program.TotalRepeats ||
                e.TargetMasterMicroseconds != state.TargetMasterMicroseconds ||
                e.CurrentRepeat != segment.LastRepeat || e.ElapsedSegmentSeconds != segment.Duration ||
                e.ElapsedSegmentMicroseconds != checked((long)segment.Duration * 1_000_000L) ||
                h.RemainingSeconds != 0) continue;
            additions.Add(FormattableString.Invariant(
                $"{e.PlanId:X16},{e.SegmentIndex},{e.CommandId:X16},{e.DeviceId},{e.BootId:X16},{h.FirmwareBuildId},{e.State},{e.CurrentRepeat},{e.ElapsedSegmentSeconds},{e.ElapsedSegmentMicroseconds},{e.RtcRecoveredEdges},{h.RtcAcceptedEdges},{h.RtcInferredMissingEdges},{h.RtcHoldoverEntries},{h.RtcQueueDrops},{h.RtcTemperatureValid},{h.DiagnosticFlags:X},{h.WifiDisconnectCount},{h.WifiReconnectCount},{h.WifiLastReconnectUptimeMs}"));
        }
        if (additions.Count != 0)
            await File.AppendAllLinesAsync(filename, additions, Encoding.UTF8);
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
            "PlanId,SegmentId,CommandId,Device,BootId,FirmwareBuildId,State,Repeat,ElapsedSeconds,ElapsedUs,RecoveredEdges,AcceptedEdges,MissingEdges,Holdovers,QueueDrops,TemperatureValid,DiagnosticFlags,WifiDisconnectCount,WifiReconnectCount,WifiLastReconnectUptimeMs" };
        executionLines.AddRange(observations.Values.OrderBy(x => x.Execution.DeviceId).Select(x => FormattableString.Invariant(
            $"{x.Execution.PlanId:X16},{x.Execution.SegmentIndex},{x.Execution.CommandId:X16},{x.Execution.DeviceId},{x.Execution.BootId:X16},{x.Health.FirmwareBuildId},{x.Execution.State},{x.Execution.CurrentRepeat},{x.Execution.ElapsedSegmentSeconds},{x.Execution.ElapsedSegmentMicroseconds},{x.Execution.RtcRecoveredEdges},{x.Health.RtcAcceptedEdges},{x.Health.RtcInferredMissingEdges},{x.Health.RtcHoldoverEntries},{x.Health.RtcQueueDrops},{x.Health.RtcTemperatureValid},{x.Health.DiagnosticFlags:X},{x.Health.WifiDisconnectCount},{x.Health.WifiReconnectCount},{x.Health.WifiLastReconnectUptimeMs}")));
        await File.WriteAllLinesAsync(stem + "_execution.csv", executionLines, Encoding.UTF8);
        if (state.ControllerSessionId != controllerSessionId ||
            (state.CurrentSegmentIndex == 1 && state.Program.ResumePosition is { } resumed &&
             resumed.RemainingSeconds < state.Program.DurationSeconds))
        {
            // A restart changes the PC epoch. The shortened first repeat after
            // STOP is also excluded: its tiny sync interval can turn sync noise
            // into a false frequency/spread limit for the following long segment.
            programDrift.Reset();
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
