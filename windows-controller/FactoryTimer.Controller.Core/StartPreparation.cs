using System.Collections.Concurrent;
using System.Net;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public enum StartBlockReason
{
    Offline,
    StatusStale,
    ParticipantBusy,
    SyncFailed,
    SyncInvalidated,
    TimingBudgetExceeded,
    ArmAckMissing,
    AbortUnconfirmed,
    MissingExpectedDevice,
}

public sealed record StartBlock(
    StartBlockReason Reason,
    string DeviceId,
    string Remedy,
    long? ValueMicroseconds = null,
    long? LimitMicroseconds = null);

public sealed record StartParticipantGateInput(
    string DeviceId,
    bool InRoster,
    bool Online,
    DateTimeOffset? LastStatusAtUtc,
    bool SyncAccepted,
    long ResidualErrorMicroseconds,
    long EffectiveSyncEpochMasterMicroseconds,
    long SyncSessionGeneration,
    long CurrentSessionGeneration,
    IPAddress? ReportedIpAddress,
    TimerState? ReportedState,
    ulong? ReportedCommandId);

public sealed record StartGateResult(
    bool Accepted,
    IReadOnlyList<StartBlock> Blocks,
    long? WorstPairStartUncertaintyMicroseconds,
    IReadOnlyDictionary<string, long> StartUncertaintyMicrosecondsByDevice)
{
    public static StartGateResult Blocked(params StartBlock[] blocks) =>
        new(false, blocks, null, new Dictionary<string, long>());
}

public static class StartReadinessGate
{
    public const long FleetPairBudgetMicroseconds = 20_000;
    public const long FlatRateBoundPpm = 50;
    public static readonly TimeSpan MaximumStatusAge = TimeSpan.FromSeconds(5);

    public static StartGateResult Evaluate(
        IReadOnlyList<StartParticipantGateInput> participants,
        DateTimeOffset nowUtc,
        long targetMasterMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (targetMasterMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetMasterMicroseconds));

        if (participants.Count == 0)
        {
            return StartGateResult.Blocked(new StartBlock(
                StartBlockReason.MissingExpectedDevice,
                "(none)",
                "Select at least one participant."));
        }

        var blocks = new List<StartBlock>();
        foreach (StartParticipantGateInput participant in participants)
        {
            if (!participant.InRoster)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.MissingExpectedDevice,
                    participant.DeviceId,
                    "Restore the configured device to the roster or deselect it."));
                continue;
            }

            // Freshness is authoritative before interpreting the last reported state.
            // If a STATUS exists but is stale, report StatusStale even when the
            // availability tracker has also aged the device to OFFLINE.
            if (participant.LastStatusAtUtc.HasValue &&
                nowUtc - participant.LastStatusAtUtc.Value >= MaximumStatusAge)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.StatusStale,
                    participant.DeviceId,
                    "Wait for a fresh STATUS report and press START again."));
                continue;
            }

            if (!participant.Online || !participant.LastStatusAtUtc.HasValue)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.Offline,
                    participant.DeviceId,
                    "Reconnect the device or deselect it."));
                continue;
            }

            if (participant.ReportedState is TimerState.Armed or TimerState.Running)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.ParticipantBusy,
                    participant.DeviceId,
                    "Wait for completion or use the normal manual RESET action."));
                continue;
            }

            if (!participant.SyncAccepted)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.SyncFailed,
                    participant.DeviceId,
                    "Press START again to perform a fresh synchronization."));
                continue;
            }

            if (participant.SyncSessionGeneration != participant.CurrentSessionGeneration)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.SyncInvalidated,
                    participant.DeviceId,
                    "Press START again to synchronize the current device session."));
            }
        }

        if (blocks.Count != 0)
        {
            return new StartGateResult(
                false,
                blocks,
                null,
                new Dictionary<string, long>());
        }

        var startUncertaintyByDevice = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (StartParticipantGateInput participant in participants)
        {
            long ageUs = targetMasterMicroseconds - participant.EffectiveSyncEpochMasterMicroseconds;
            if (ageUs < 0)
            {
                blocks.Add(new StartBlock(
                    StartBlockReason.TimingBudgetExceeded,
                    participant.DeviceId,
                    "Press START again to obtain a valid synchronization epoch."));
                continue;
            }

            long residualMagnitude = participant.ResidualErrorMicroseconds == long.MinValue
                ? long.MaxValue
                : Math.Abs(participant.ResidualErrorMicroseconds);
            long startUncertaintyUs = checked(residualMagnitude + DriftBoundMicroseconds(ageUs));
            startUncertaintyByDevice.Add(participant.DeviceId, startUncertaintyUs);
        }

        if (blocks.Count != 0)
        {
            return new StartGateResult(false, blocks, null, startUncertaintyByDevice);
        }

        // N=1 has no pairwise fleet-spread claim. Individual freshness, synchronization,
        // session and busy-state gates above still apply.
        if (participants.Count == 1)
        {
            return new StartGateResult(true, [], null, startUncertaintyByDevice);
        }

        long[] worstTwo = startUncertaintyByDevice.Values
            .OrderByDescending(value => value)
            .Take(2)
            .ToArray();
        if (worstTwo.Length != 2)
        {
            throw new InvalidOperationException("Pairwise timing evaluation requires two participants.");
        }

        long startPairUs = checked(worstTwo[0] + worstTwo[1]);

        // The production guarantee is now explicitly a START-instant guarantee.
        // The 50 ppm fallback is used only to age each accepted residual from the
        // estimator's effective epoch up to T*. Countdown duration is deliberately
        // absent from this gate: once T* is reached, the devices run autonomously.
        // Therefore an exact 20 ms pair bound passes; only a value above 20 ms blocks.
        if (startPairUs > FleetPairBudgetMicroseconds)
        {
            return new StartGateResult(
                false,
                [new StartBlock(
                    StartBlockReason.TimingBudgetExceeded,
                    "fleet",
                    "Press START again to obtain fresher/lower-error synchronization before T*.",
                    startPairUs,
                    FleetPairBudgetMicroseconds)],
                startPairUs,
                startUncertaintyByDevice);
        }

        return new StartGateResult(
            true,
            [],
            startPairUs,
            startUncertaintyByDevice);
    }

    public static long DriftBoundMicroseconds(long intervalMicroseconds)
    {
        if (intervalMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(intervalMicroseconds));
        long numerator = checked(intervalMicroseconds * FlatRateBoundPpm);
        return CeilingDivide(numerator, 1_000_000L);
    }

    private static long CeilingDivide(long numerator, long denominator)
    {
        if (numerator < 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        if (numerator == 0) return 0;
        return checked(1 + (numerator - 1) / denominator);
    }
}

public static class StartArmSessionGuard
{
    public static StartBlock? Evaluate(
        string deviceId,
        StartParticipantGateInput frozenGate,
        ParticipantSessionSnapshot current,
        ulong preparedCommandId)
    {
        if (current.Generation != frozenGate.CurrentSessionGeneration)
        {
            return new StartBlock(
                StartBlockReason.SyncInvalidated,
                deviceId,
                "Device session changed during ARMING. Press START again.");
        }

        if (current.TimerState is TimerState.Armed or TimerState.Running &&
            current.CommandId.HasValue &&
            current.CommandId.Value != preparedCommandId)
        {
            return new StartBlock(
                StartBlockReason.ParticipantBusy,
                deviceId,
                "Device entered another run during ARMING; use normal RESET if recovery is required.");
        }

        return null;
    }
}

public static class StartArmWindow
{
    // Normal ARMING now reserves a dedicated cancellation-confirmation window.
    // Final STATUS begins at T* - 3.5 s, normal preparation must be complete by
    // T* - 2.5 s, the established session guard remains active until T* - 2 s,
    // and an abort may be positively confirmed only before T* - 1 s.
    public static readonly long FinalBarrierStartLeadMicroseconds = 3_500_000;
    public static readonly long NormalArmDeadlineLeadMicroseconds = 2_500_000;
    public static readonly long CutoffLeadMicroseconds = 2_000_000;
    public static readonly long AbortConfirmationDeadlineLeadMicroseconds = 1_000_000;
    public static readonly long AbortConfirmationMaximumWindowMicroseconds = 1_500_000;

    public static bool IsNormalArmOpen(long masterNowMicroseconds, long targetMasterMicroseconds) =>
        IsBeforeLead(masterNowMicroseconds, targetMasterMicroseconds, NormalArmDeadlineLeadMicroseconds);

    // Existing post-barrier session guard. At and after T* - 2 s a successful
    // prepared run owns execution and the controller no longer introduces a
    // new liveness abort.
    public static bool IsOpen(long masterNowMicroseconds, long targetMasterMicroseconds) =>
        IsBeforeLead(masterNowMicroseconds, targetMasterMicroseconds, CutoffLeadMicroseconds);

    public static bool IsAbortConfirmationOpen(long masterNowMicroseconds, long targetMasterMicroseconds) =>
        IsBeforeLead(masterNowMicroseconds, targetMasterMicroseconds, AbortConfirmationDeadlineLeadMicroseconds);

    private static bool IsBeforeLead(
        long masterNowMicroseconds,
        long targetMasterMicroseconds,
        long leadMicroseconds)
    {
        if (masterNowMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(masterNowMicroseconds));
        if (targetMasterMicroseconds <= leadMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMasterMicroseconds));
        }
        return masterNowMicroseconds < targetMasterMicroseconds - leadMicroseconds;
    }
}

public static class StartFinalBarrier
{
    // The final controller-only barrier deliberately reuses the existing STATUS
    // protocol. A fresh STATUS after START_AT must still describe the exact
    // prepared command as ARMED and must belong to the same observed session.
    // This detects a reboot that has come back before the barrier even when the
    // device reacquires the same IP, because firmware RAM resets to READY /
    // CommandId=0 and ParticipantSessionTracker advances the session generation.
    public static StartBlock? EvaluateParticipant(
        string deviceId,
        StartParticipantGateInput frozenGate,
        ParticipantSessionSnapshot current,
        ulong preparedCommandId,
        DateTimeOffset requireStatusAfterUtc)
    {
        if (!current.LastStatusAtUtc.HasValue ||
            current.LastStatusAtUtc.Value < requireStatusAfterUtc)
        {
            return new StartBlock(
                StartBlockReason.StatusStale,
                deviceId,
                "Final ARM verification did not receive a fresh STATUS before cutoff; press START again.");
        }

        if (current.Generation != frozenGate.CurrentSessionGeneration)
        {
            return new StartBlock(
                StartBlockReason.SyncInvalidated,
                deviceId,
                "Device session changed before final ARM verification; press START again.");
        }

        if (current.TimerState is TimerState.Armed &&
            current.CommandId == preparedCommandId)
        {
            return null;
        }

        if (current.TimerState is TimerState.Armed or TimerState.Running &&
            current.CommandId.HasValue &&
            current.CommandId.Value != preparedCommandId)
        {
            return new StartBlock(
                StartBlockReason.ParticipantBusy,
                deviceId,
                "Final STATUS belongs to another command; use the normal RESET action if recovery is required.");
        }

        return new StartBlock(
            StartBlockReason.SyncInvalidated,
            deviceId,
            "Final STATUS no longer reports this prepared command as ARMED; press START again.");
    }
}

public static class StartAbortConfirmation
{
    // A cancellation is positively confirmed only by a STATUS observed after
    // cancellation began which proves the old START_AT is no longer armed. A
    // normal RESET leaves the device READY with the RESET command ID. A reboot
    // is also safe for the abandoned in-RAM arm and reports READY/0.
    public static bool IsParticipantCancellationConfirmed(
        ParticipantSessionSnapshot current,
        ulong preparedCommandId,
        ulong resetCommandId,
        DateTimeOffset requireStatusAfterUtc)
    {
        if (!current.LastStatusAtUtc.HasValue ||
            current.LastStatusAtUtc.Value < requireStatusAfterUtc)
        {
            return false;
        }

        if (current.TimerState != TimerState.Ready || !current.CommandId.HasValue)
        {
            return false;
        }

        ulong statusCommandId = current.CommandId.Value;
        if (statusCommandId == preparedCommandId) return false;

        return statusCommandId == resetCommandId || statusCommandId == 0;
    }
}

public sealed class ParticipantReservationManager
{
    private readonly object gate = new();
    private readonly HashSet<string> reserved = new(StringComparer.Ordinal);

    public bool TryReserveAll(IEnumerable<string> participantIds, out ParticipantReservation? reservation)
    {
        ArgumentNullException.ThrowIfNull(participantIds);
        string[] ids = participantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            reservation = null;
            return false;
        }

        lock (gate)
        {
            if (ids.Any(reserved.Contains))
            {
                reservation = null;
                return false;
            }

            foreach (string id in ids) reserved.Add(id);
            reservation = new ParticipantReservation(this, ids);
            return true;
        }
    }

    public IReadOnlyList<string> GetConflicts(IEnumerable<string> participantIds)
    {
        ArgumentNullException.ThrowIfNull(participantIds);
        string[] ids = participantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        lock (gate)
        {
            return ids.Where(reserved.Contains).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }

    private void Release(IReadOnlyList<string> participantIds)
    {
        lock (gate)
        {
            foreach (string id in participantIds) reserved.Remove(id);
        }
    }

    public sealed class ParticipantReservation : IDisposable
    {
        private ParticipantReservationManager? owner;

        internal ParticipantReservation(ParticipantReservationManager owner, string[] participantIds)
        {
            this.owner = owner;
            ParticipantIds = participantIds;
        }

        public IReadOnlyList<string> ParticipantIds { get; }

        public void Dispose()
        {
            ParticipantReservationManager? current = Interlocked.Exchange(ref owner, null);
            current?.Release(ParticipantIds);
        }
    }
}

public sealed record ParticipantSessionSnapshot(
    long Generation,
    DateTimeOffset? LastStatusAtUtc,
    IPAddress? IpAddress,
    TimerState? TimerState,
    ulong? CommandId,
    long? LastLocalTimestampMicroseconds,
    bool DisconnectObserved);

public sealed class ParticipantSessionTracker
{
    private readonly object gate = new();
    private long generation;
    private DateTimeOffset? lastStatusAtUtc;
    private IPAddress? ipAddress;
    private TimerState? timerState;
    private ulong? commandId;
    private long? lastLocalTimestampMicroseconds;
    private bool disconnectObserved;
    private bool disconnectInferenceSuppressed;

    public ParticipantSessionSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return new ParticipantSessionSnapshot(
                    generation,
                    lastStatusAtUtc,
                    ipAddress,
                    timerState,
                    commandId,
                    lastLocalTimestampMicroseconds,
                    disconnectObserved);
            }
        }
    }

    public void ObserveStatus(StatusPacket status, IPAddress sourceAddress, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(sourceAddress);

        lock (gate)
        {
            bool invalidated = disconnectObserved ||
                (ipAddress is not null && !ipAddress.Equals(sourceAddress)) ||
                (commandId.HasValue && commandId.Value != 0 && status.CommandId == 0);
            if (invalidated) generation++;

            disconnectObserved = false;
            disconnectInferenceSuppressed = false;
            ipAddress = sourceAddress;
            lastStatusAtUtc = observedAtUtc;
            timerState = status.State;
            commandId = status.CommandId;
        }
    }

    public void ObserveLocalTimestamp(long localTimestampMicroseconds)
    {
        if (localTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localTimestampMicroseconds));
        }

        lock (gate)
        {
            if (lastLocalTimestampMicroseconds.HasValue &&
                localTimestampMicroseconds < lastLocalTimestampMicroseconds.Value)
            {
                generation++;
            }
            lastLocalTimestampMicroseconds = localTimestampMicroseconds;
        }
    }

    public void ObserveConnectivity(DateTimeOffset nowUtc, TimeSpan offlineTimeout, bool monitoringEnabled)
    {
        if (!monitoringEnabled) return;
        if (offlineTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offlineTimeout));

        lock (gate)
        {
            if (disconnectInferenceSuppressed) return;
            if (lastStatusAtUtc.HasValue && nowUtc - lastStatusAtUtc.Value >= offlineTimeout)
            {
                disconnectObserved = true;
            }
        }
    }


    public void BeginIntentionalStatusPause()
    {
        lock (gate) disconnectInferenceSuppressed = true;
    }

    public void MarkDisconnectedObserved()
    {
        lock (gate) disconnectObserved = true;
    }

    public void InvalidateControllerSession()
    {
        lock (gate)
        {
            generation++;
            disconnectObserved = false;
            disconnectInferenceSuppressed = false;
            lastStatusAtUtc = null;
            ipAddress = null;
            timerState = null;
            commandId = null;
            lastLocalTimestampMicroseconds = null;
        }
    }
}

public sealed class ArmAcknowledgementTracker
{
    private readonly ulong commandId;
    private readonly HashSet<string> participants;
    private readonly ConcurrentDictionary<string, AckResult> results = new(StringComparer.Ordinal);

    public ArmAcknowledgementTracker(ulong commandId, IEnumerable<string> participantIds)
    {
        if (commandId == 0) throw new ArgumentOutOfRangeException(nameof(commandId));
        ArgumentNullException.ThrowIfNull(participantIds);
        this.commandId = commandId;
        participants = participantIds.ToHashSet(StringComparer.Ordinal);
        if (participants.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(participantIds));
    }

    public void Observe(AckPacket ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (ack.CommandId != commandId || ack.CommandType != CommandType.StartAt) return;
        if (!participants.Contains(ack.DeviceId)) return;
        results.AddOrUpdate(ack.DeviceId, ack.Result, (_, previous) => Merge(previous, ack.Result));
    }

    public IReadOnlyList<string> MissingParticipants => participants
        .Where(id => !results.TryGetValue(id, out AckResult result) || !IsArmed(result))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyDictionary<string, AckResult> Rejections => results
        .Where(pair => pair.Value is AckResult.NotSynced or AckResult.Late)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public bool AllArmed => participants.All(id =>
        results.TryGetValue(id, out AckResult result) && IsArmed(result));

    private static bool IsArmed(AckResult result) =>
        result is AckResult.Accepted or AckResult.Duplicate;

    private static AckResult Merge(AckResult previous, AckResult current)
    {
        if (previous is AckResult.Accepted or AckResult.Duplicate) return previous;
        if (current is AckResult.Accepted or AckResult.Duplicate) return current;
        return current;
    }
}

public sealed class ResetAcknowledgementTracker
{
    private readonly ulong commandId;
    private readonly HashSet<string> participants;
    private readonly ConcurrentDictionary<string, AckResult> results = new(StringComparer.Ordinal);

    public ResetAcknowledgementTracker(ulong commandId, IEnumerable<string> participantIds)
    {
        if (commandId == 0) throw new ArgumentOutOfRangeException(nameof(commandId));
        ArgumentNullException.ThrowIfNull(participantIds);
        this.commandId = commandId;
        participants = participantIds.ToHashSet(StringComparer.Ordinal);
        if (participants.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(participantIds));
    }

    public void Observe(AckPacket ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (ack.CommandId != commandId || ack.CommandType != CommandType.Reset) return;
        if (!participants.Contains(ack.DeviceId)) return;
        results.AddOrUpdate(ack.DeviceId, ack.Result, (_, previous) => Merge(previous, ack.Result));
    }

    public IReadOnlyList<string> MissingParticipants => participants
        .Where(id => !results.TryGetValue(id, out AckResult result) || !IsAcknowledged(result))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();

    public bool AllAcknowledged => participants.All(id =>
        results.TryGetValue(id, out AckResult result) && IsAcknowledged(result));

    private static bool IsAcknowledged(AckResult result) =>
        result is AckResult.Accepted or AckResult.Duplicate;

    private static AckResult Merge(AckResult previous, AckResult current)
    {
        if (previous is AckResult.Accepted or AckResult.Duplicate) return previous;
        if (current is AckResult.Accepted or AckResult.Duplicate) return current;
        return current;
    }
}
