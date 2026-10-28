using System.Net;
using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller;

internal sealed partial class MainViewModel
{
    private bool operatorStopInProgress;
    private HashSet<string>? operatorActionParticipants;
    private bool ControlsIdle => !completionResetInProgress && !programManagementBusy && !isSending && !IsBenchmarkRunning &&
        manualStartAtSession is null && !isPreparingManualStartAtTest;
    public bool CanEditParticipants => ControlsIdle;
    public bool CanEditCountdownSettings => ControlsIdle && productionProgram?.State.Active != true && programTask.IsCompleted;
    public bool CanStartCountdown => ControlsIdle && productionProgramLoadError is null &&
        SelectedNetworkInterface is not null && udpService.IsReady &&
        programTask.IsCompleted;
    public bool CanStopCountdown => !completionResetInProgress && !programManagementBusy && !IsBenchmarkRunning &&
        manualStartAtSession is null && productionProgram is not null &&
        (productionProgram.State.Active || productionProgram.State.StopCheckpoint is { DiscardProgress: false });
    public bool CanResetCountdown => !completionResetInProgress && !programManagementBusy && !IsBenchmarkRunning &&
        (!isSending || productionProgram?.State.Active == true) &&
        manualStartAtSession is null && (productionProgram is not null ||
            productionProgramLoadError is not null ||
            (!isSending && SelectedNetworkInterface is not null && udpService.IsReady));

    public Task StartCountdownAsync()
    {
        AppendCountdownLog("START pressed. " + CountdownStartAvailabilityText);
        return !CanStartCountdown ? Task.CompletedTask : ManageProgramAsync(StartOrResumeCountdownCoreAsync);
    }
    public Task StopCountdownAsync() => !CanStopCountdown ? Task.CompletedTask : ManageProgramAsync(StopCountdownCoreAsync);
    public Task ResetCountdownAsync() => !CanResetCountdown ? Task.CompletedTask : ManageProgramAsync(ResetSelectedCountdownAsync);

    private void NotifyCountdownControls()
    {
        OnPropertyChanged(nameof(CanStartCountdown));
        OnPropertyChanged(nameof(CanStopCountdown));
        OnPropertyChanged(nameof(CanResetCountdown));
        OnPropertyChanged(nameof(CanEditParticipants));
        OnPropertyChanged(nameof(CanEditCountdownSettings));
        OnPropertyChanged(nameof(CountdownStartAvailabilityText));
    }

    private async Task StartOrResumeCountdownCoreAsync()
    {
        string[] selected = Devices.Where(d => d.IsSelected).Select(d => d.DeviceId).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "START blocked — select at least one timer. Saved progress is unchanged.";
            return;
        }
        AppendCountdownLog("START uses selected timers: " + CountdownDeviceNames(selected) + ".");
        if (productionProgram?.State.StopCheckpoint is { } checkpoint)
        {
            string[] previousSelection = productionProgram.State.Program.Participants.ToArray();
            productionProgram.SelectStoppedParticipants(selected, CreateCommandId());
            checkpoint = productionProgram.State.StopCheckpoint!;
            if (!previousSelection.SequenceEqual(selected))
            {
                AppendCountdownLog("START applied the current selection; preparing only " + CountdownDeviceNames(selected) +
                    (checkpoint.DiscardProgress ? " for a new countdown." :
                    $" at saved {FormatMinutesSeconds(checkpoint.RemainingSeconds)}, repetition {checkpoint.CurrentRepeat}."));
                UpdateProductionProgramText();
            }
            await ConfirmOperatorStopAsync();
            if (productionProgram.State.StopCheckpoint!.PendingDeviceIds.Count != 0)
            {
                StatusMessage = "START blocked — waiting for stop confirmation: " +
                    CountdownDeviceNames(productionProgram.State.StopCheckpoint.PendingDeviceIds) +
                    ". Reconnect them, or change the selection and press START again.";
                return;
            }
            if (!checkpoint.DiscardProgress)
            {
                if (!checkpoint.CanResumePosition)
                {
                    StatusMessage = "No saved countdown remains. Press RESET, then START for a new countdown.";
                    return;
                }
                // Every currently selected device is challenged at resume time.
                // Cached positive STOP replies cannot authorize a new START.
                var fresh = await ProbeProgramAsync(CancellationToken.None);
                productionProgram.RecheckAllStoppedDevices(fresh);
                checkpoint = productionProgram.State.StopCheckpoint!;
                if (!checkpoint.CanContinue)
                {
                    StatusMessage = productionProgram.State.Message;
                    return;
                }
                ProgramExecutionState previous = productionProgram.State;
                RepetitionProgram continuation = RepetitionProgram.ContinueFrom(CreateCommandId(), previous.Program, checkpoint.Position, selected);
                ProgramExecutionState next = ProgramExecutionState.New(continuation, previous);
                programStore.Save(next);
                productionProgram = new(programStore, next);
                programDrift.Reset();
                programDriftAlert = "";
                programAwakeLease ??= new();
                await StartProgramLoopAsync();
                return;
            }
        }
        if (productionProgram?.State.Active == true)
        {
            if (!productionProgram.State.Program.Participants.SequenceEqual(selected))
            {
                // Recovering a saved/held run with another selection first saves
                // its position and stops only the selected timers. The next
                // pass resumes that position through the normal fresh ARM path.
                await StopCountdownCoreAsync();
                if (productionProgram.State.StopCheckpoint is { PositionKnown: true })
                    await StartOrResumeCountdownCoreAsync();
                return;
            }
            if (productionProgram.State.Stage == ProgramStage.Held)
                await ResumeProductionProgramCoreAsync();
            else
                await RecoverSavedCountdownAsync();
            return;
        }
        await StartRepetitionProgramCoreAsync();
    }

    private StartBlock[] ValidateLogoFirmware(ProgramSegment? segment, DeviceViewModel[] participants, DateTimeOffset challenge)
    {
        if (segment is not { LogoSeconds: > 0 }) return [];
        return participants.Where(d => !HasFreshExecutionReply(d.DeviceId, challenge) ||
                !matchedExecutionReplies[d.DeviceId].Packet.SupportsLogoOutput)
            .Select(d => new StartBlock(StartBlockReason.FirmwareIncompatible, d.DeviceId,
                "Four-second logo/output requires timer firmware 6.24.6-rc1 or later. Reflash this timer before START."))
            .ToArray();
    }

    private async Task RecoverSavedCountdownAsync()
    {
        var observations = await ProbeProgramAsync(CancellationToken.None);
        ProgramExecutionState before = productionProgram!.State;
        bool expired = before.ControllerSessionId == controllerSessionId && before.CurrentSegment is { } ended &&
            MasterClock.NowMicroseconds >= checked(before.TargetMasterMicroseconds + (long)ended.Duration * 1_000_000 + 750_000);
        ProgramReconciliation decision = productionProgram.Reconcile(observations, plannedSegmentEnded: expired);
        if (decision.Action == ReconcileAction.ObserveActiveSegment)
        {
            ProgramSegment segment = productionProgram.State.CurrentSegment!;
            long phase = observations.Values.Where(x => x.Execution.State is TimerState.Armed or TimerState.Running)
                .Min(x => x.Execution.ElapsedSegmentMicroseconds);
            long target = before.ControllerSessionId == controllerSessionId ? before.TargetMasterMicroseconds :
                MasterClock.NowMicroseconds - phase;
            clock.ArmSegmentAt(segment.DurationSeconds, segment.RepeatCount, target, segment.FirstRepeat, before.Program.TotalRepeats, segment.LogoSeconds);
            await PauseForRecoveredSegmentSetupAsync(decision.RemainingSeconds ?? 1);
        }
        if (decision.Action is ReconcileAction.ObserveActiveSegment or ReconcileAction.SynchronizeNextSegment or ReconcileAction.WaitForReadiness)
        {
            programAwakeLease ??= new();
            await StartProgramLoopAsync();
        }
        if (decision.Action == ReconcileAction.Complete)
        {
            await WriteProgramSegmentAuditAsync(observations);
            await ResetCompletedProgramAsync();
        }
        else StatusMessage = productionProgram.State.Message;
        UpdateProductionProgramText();
    }

    private async Task PauseForRecoveredSegmentSetupAsync(uint seconds)
    {
        productionSilentRunActive = true;
        productionSilentRunOwnsExactWindow = false;
        foreach (DeviceViewModel device in ProgramParticipants()) device.BeginIntentionalStatusPause();
        await statusDiscovery.StopAndWaitAsync();
        ScheduleRecoveredRunningStatusPause(seconds);
    }

    private async Task StopCountdownCoreAsync()
    {
        DeviceViewModel[] selected = Devices.Where(d => d.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "STOP blocked — select at least one timer. The countdown is unchanged.";
            return;
        }
        if (productionProgram is null || (!productionProgram.State.Active && productionProgram.State.StopCheckpoint is null))
        {
            return;
        }
        string[] selectedIds = selected.Select(d => d.DeviceId).ToArray();
        AppendCountdownLog("STOP uses selected timers: " + CountdownDeviceNames(selectedIds) + ".");
        operatorActionParticipants = selectedIds.ToHashSet(StringComparer.Ordinal);
        operatorStopInProgress = true;
        try
        {
            ProgramExecutionState before = productionProgram.State;
            StoppedCountdownCheckpoint? saved = before.StopCheckpoint;
            CountdownPosition? position = null;
            if (saved is null)
            {
                try { position = CountdownPosition.Capture(before, MasterClock.NowMicroseconds, controllerSessionId); }
                catch (InvalidOperationException) { /* Fresh device phase is used after a controller restart. */ }
            }
            programCancellation?.Cancel();
            await programTask;
            productionSilentRunCancellation?.Cancel();
            await productionSilentRunTask;
            ResumeAutomaticStatusDiscoveryAfterRun();

            if (saved is null)
            {
                if (position is null && before.CurrentSegment is { } segment)
                {
                    try
                    {
                        await DiscoverResetTargetAddressesAsync(selected);
                        var observed = await ProbeProgramAsync(CancellationToken.None, selected);
                        long[] phases = observed.Values.Where(x => x.Execution.PlanId == before.Program.PlanId &&
                            x.Execution.CommandId == before.CurrentCommandId && x.Execution.SegmentIndex == segment.Index &&
                            (x.Execution.State is TimerState.Armed or TimerState.Running or TimerState.Finished))
                            .Select(x => x.Execution.ElapsedSegmentMicroseconds).ToArray();
                        if (phases.Length != 0)
                            position = CountdownPosition.WithFollowingRepeats(before.Program, segment, phases.Min());
                    }
                    catch (Exception) { /* STOP still persists and attempts RESET; unknown progress cannot resume. */ }
                }
                bool known = position is not null;
                position ??= new(before.CurrentSegment?.FirstRepeat ?? 1, before.CurrentSegment?.DurationSeconds ?? before.Program.DurationSeconds);
                productionProgram.Stop(CreateCommandId(), position.CurrentRepeat, position.RemainingSeconds, known, selectedIds);
                clock.Pause(position.RemainingSeconds, position.CurrentRepeat, before.Program.TotalRepeats);
            }
            else if (saved.DiscardProgress)
                return; // STOP can never resurrect progress discarded by RESET.
            else
            {
                productionProgram.SelectStoppedParticipants(selectedIds, CreateCommandId());
                clock.Pause(saved.RemainingSeconds, saved.CurrentRepeat, before.Program.TotalRepeats);
            }

            productionStartTelemetryContext = null;
            ClearDegradedStartPending();
            RefreshClockProperties();
            UpdateProductionProgramText();
            await ConfirmOperatorStopAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = "STOP needs confirmation: " + exception.Message +
                ". Restore connectivity or change the selection and retry.";
        }
        finally
        {
            operatorStopInProgress = false;
            operatorActionParticipants = null;
            programAwakeLease?.Dispose(); programAwakeLease = null;
            UpdateProductionProgramText();
            UpdateCanSend();
        }
    }

    private async Task ResetSelectedCountdownAsync()
    {
        // Each operator action snapshots the current selection before awaits.
        string[] selected = Devices.Where(d => d.IsSelected).Select(d => d.DeviceId).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "RESET blocked — select at least one timer. The saved countdown is unchanged.";
            return;
        }
        AppendCountdownLog("RESET pressed for selected timers: " + CountdownDeviceNames(selected) + ".");
        operatorActionParticipants = selected.ToHashSet(StringComparer.Ordinal);
        operatorStopInProgress = true;
        try
        {
            programCancellation?.Cancel();
            await programTask;
            productionSilentRunCancellation?.Cancel();
            await productionSilentRunTask;
            ResumeAutomaticStatusDiscoveryAfterRun();

            ProgramExecutionState? previous = productionProgram?.State;
            uint duration = TryDuration(out uint draft) ? draft : Math.Clamp(clock.RemainingSeconds, 1u, 5999u);
            uint repeats = double.IsFinite(RepetitionsInput) && RepetitionsInput == Math.Truncate(RepetitionsInput) &&
                RepetitionsInput is >= 1 and <= 1_000_000 ? (uint)RepetitionsInput : 1u;
            uint cap = double.IsFinite(SegmentCapSecondsInput) && SegmentCapSecondsInput == Math.Truncate(SegmentCapSecondsInput) &&
                SegmentCapSecondsInput >= duration && SegmentCapSecondsInput <= 6000 ? (uint)SegmentCapSecondsInput : 6000u;
            RepetitionProgram plan;
            try { plan = RepetitionProgram.Create(CreateCommandId(), duration, repeats, selected, cap, FactoryProtocol.RepetitionLogoSeconds); }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid repetition/segment geometry must not prevent RESET.
                // START will validate the draft before creating its next run.
                plan = RepetitionProgram.Create(CreateCommandId(), duration, 1, selected, logoSeconds: FactoryProtocol.RepetitionLogoSeconds);
            }

            StoppedCountdownCheckpoint? saved = previous?.StopCheckpoint;
            bool retrySameReset = saved is { DiscardProgress: true, PendingDeviceIds.Count: > 0 } &&
                previous!.Program.DurationSeconds == duration && previous.Program.Participants.SequenceEqual(selected);
            ulong resetId = retrySameReset ? saved!.ResetCommandId : CreateCommandId();
            var checkpoint = new StoppedCountdownCheckpoint(resetId, 1, plan.DurationSeconds,
                plan.Participants.ToArray(), DiscardProgress: true);
            ProgramExecutionState cleared = ProgramExecutionState.New(plan, previous) with {
                Stage = ProgramStage.Stopped, StopCheckpoint = checkpoint,
                Message = "RESET: progress cleared; confirming the selected timers are stopped." };
            // Persist the selected scope before clearing recovery or sending.
            programStore.Save(cleared);
            productionProgram = new(programStore, cleared);
            productionProgramLoadError = null;
            string[] excluded = previous?.Program.Participants.Except(selected).ToArray() ?? [];
            if (excluded.Length != 0)
                AppendCountdownLog("Removed from required stop confirmation by RESET (unselected): " + CountdownDeviceNames(excluded) + ".");
            programDrift.Reset();
            programDriftAlert = "";
            productionStartTelemetryContext = null;
            ClearDegradedStartPending();
            clock.Reset(duration);
            RefreshClockProperties();
            UpdateProductionProgramText();
            StatusMessage = cleared.Message;
            await ConfirmOperatorStopAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = "RESET could not complete: " + exception.Message + ". Press RESET to retry.";
        }
        finally
        {
            operatorStopInProgress = false;
            operatorActionParticipants = null;
            programAwakeLease?.Dispose(); programAwakeLease = null;
            UpdateProductionProgramText();
            UpdateCanSend();
        }
    }

    private PreparedStartRun RestrictPreparedAbortForOperator(PreparedStartRun prepared) =>
        operatorStopInProgress && operatorActionParticipants is { } selected
            ? prepared with { Participants = prepared.Participants.Where(p => selected.Contains(p.Device.DeviceId)).ToArray() }
            : prepared;

    private void ReportPreparedAbortOutcome(StartBlock originalFailure, AbortConfirmationResult abort, bool production)
    {
        if (operatorStopInProgress && operatorActionParticipants is { } selected)
        {
            AppendCountdownLog("Operator cancellation targets only the selected timers: " + CountdownDeviceNames(selected) +
                (abort.Confirmed ? "." : ". Their current stop state still needs confirmation."));
            // The operator action writes its own selected pause/reset checkpoint
            // after the old preparation finishes. It cannot authorize an auto-retry.
            return;
        }
        ReportPreparedRunAbort(originalFailure, abort);
        if (production) ApplyProductionAbortResult(abort);
    }

    private async Task ConfirmOperatorStopAsync()
    {
        if (productionProgram?.State.StopCheckpoint is not { } checkpoint) return;
        if (SelectedNetworkInterface is null || !udpService.IsReady)
        {
            StatusMessage = checkpoint.DiscardProgress
                ? "RESET saved for the selected timers. Progress is cleared; restore connectivity for stop confirmation."
                : "Network unavailable. Paused progress is retained; restore connectivity and retry.";
            LogStopConfirmationFailures(checkpoint, new Dictionary<string, ProgramDeviceObservation>());
            return;
        }
        for (int attempt = 0; attempt < 2 && checkpoint.PendingDeviceIds.Count != 0; attempt++)
        {
            DeviceViewModel[] missing = ProgramParticipants().Where(d => checkpoint.PendingDeviceIds.Contains(d.DeviceId)).ToArray();
            Dictionary<string, IPAddress> endpoints = await DiscoverResetTargetAddressesAsync(missing);
            var reset = new CommandPacket(CommandType.Reset, checkpoint.ResetCommandId, productionProgram.State.Program.DurationSeconds, 0);
            await sendLock.WaitAsync();
            try
            {
                foreach (DeviceViewModel device in missing)
                    if (endpoints.TryGetValue(device.DeviceId, out IPAddress? address))
                    {
                        await udpService.SendCommandUnicastAsync(reset, address);
                        AppendCountdownLog($"RESET sent to {CountdownDeviceNames([device.DeviceId])}; waiting for READY confirmation.");
                    }
            }
            finally { sendLock.Release(); }
            var observations = await ProbeProgramAsync(CancellationToken.None);
            productionProgram.ConfirmStoppedDevices(observations);
            checkpoint = productionProgram.State.StopCheckpoint!;
            LogStopConfirmationFailures(checkpoint, observations, endpoints);
        }
        StatusMessage = productionProgram.State.Message;
        UpdateProductionProgramText();
    }

    private async Task<Dictionary<string, IPAddress>> DiscoverResetTargetAddressesAsync(IReadOnlyList<DeviceViewModel> targets)
    {
        var result = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        if (targets.Count == 0 || SelectedNetworkInterface is null || !udpService.IsReady) return result;
        bool previousProbe = recoveryProbeActive;
        recoveryProbeActive = true;
        try
        {
            ulong request = CreateCommandId();
            DateTimeOffset after = DateTimeOffset.UtcNow;
            foreach (DeviceViewModel device in targets) expectedExecutionReplies[device.DeviceId] = request;
            var broadcast = BroadcastAddressResolver.Resolve(SelectedNetworkInterface, ManualBroadcastOverride);
            if (broadcast.IsValid) await udpService.SendStatusRequestAsync(request, broadcast.Address!);
            foreach (DeviceViewModel device in targets)
                if (device.TryGetIpAddress(out IPAddress? old) && old is not null)
                    await udpService.SendStatusRequestAsync(request, old);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(1);
            do
            {
                foreach (DeviceViewModel device in targets)
                    if (TryGetVerifiedResetAddress(device, request, after, out IPAddress? verified))
                        result[device.DeviceId] = verified!;
                if (result.Count == targets.Count) break;
                await Task.Delay(40);
            } while (DateTimeOffset.UtcNow < deadline);
        }
        finally { recoveryProbeActive = previousProbe; }
        return result;
    }

    private bool TryGetVerifiedResetAddress(DeviceViewModel device, ulong request, DateTimeOffset after, out IPAddress? verified)
    {
        verified = null;
        if (!StopResetAddressGate.HasFreshAddressEvidence(device.SessionSnapshot.LastStatusAtUtc, after) ||
            !HasFreshExecutionReply(device.DeviceId, after) ||
            !matchedExecutionAddresses.TryGetValue(device.DeviceId, out var endpoint) || endpoint.Request != request ||
            !device.TryGetIpAddress(out IPAddress? current) || current is null || !current.Equals(endpoint.Address)) return false;
        verified = endpoint.Address;
        return true;
    }
}
