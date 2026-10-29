using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public sealed record ProgramSegment(uint Index, uint FirstRepeat, uint RepeatCount, uint DurationSeconds, uint LogoSeconds = 0,
    uint RepeatStartSeconds = 0)
{
    public uint Duration => FactoryProtocol.SegmentDuration(DurationSeconds, RepeatCount, LogoSeconds, RepeatStartSeconds);
    public uint CountdownSpan => Duration - LogoSeconds;
    public uint LastRepeat => checked(FirstRepeat + RepeatCount - 1);
    public CommandPacket Command(ulong plan, uint total, ulong command, long target, ulong boot = 1, ulong previous = 0) =>
        new(CommandType.StartAt, command, DurationSeconds, 0, target,
            PlanId: plan, SegmentIndex: Index, RepeatCount: RepeatCount,
            FirstRepeat: FirstRepeat, TotalRepeats: total, ExpectedBootId: boot, ExpectedPreviousCommandId: previous,
            LogoSeconds: LogoSeconds, RepeatStartSeconds: RepeatStartSeconds);
}

public sealed record RepetitionProgram(ulong PlanId, uint DurationSeconds, uint TotalRepeats,
    uint SegmentCapSeconds, IReadOnlyList<string> Participants, IReadOnlyList<ProgramSegment> Segments,
    CountdownPosition? ResumePosition = null, uint LogoSeconds = 0,
    uint RepeatStartSeconds = 0, bool SyncEveryRepetition = false)
{
    public static RepetitionProgram Create(ulong id, uint duration, uint repeats,
        IEnumerable<string> participants, uint cap = 6000, uint logoSeconds = 0,
        uint repeatStartSeconds = 0, bool syncEveryRepetition = false) =>
        CreateCore(id, duration, repeats, participants, cap, null, logoSeconds, repeatStartSeconds, syncEveryRepetition);

    public static RepetitionProgram ContinueFrom(ulong id, RepetitionProgram original, CountdownPosition position,
        IEnumerable<string>? participants = null)
    {
        original.Validate();
        return CreateCore(id, original.DurationSeconds, original.TotalRepeats,
            participants ?? original.Participants, original.SegmentCapSeconds, position, original.LogoSeconds,
            original.RepeatStartSeconds, original.SyncEveryRepetition);
    }

    public RepetitionProgram WithParticipants(IEnumerable<string> participants)
    {
        Validate();
        return CreateCore(PlanId, DurationSeconds, TotalRepeats, participants, SegmentCapSeconds, ResumePosition,
            LogoSeconds, RepeatStartSeconds, SyncEveryRepetition);
    }

    private static RepetitionProgram CreateCore(ulong id, uint duration, uint repeats,
        IEnumerable<string> participants, uint cap, CountdownPosition? resume, uint logo, uint repeatStart, bool syncEvery)
    {
        string[] roster = participants.Order(StringComparer.Ordinal).ToArray();
        if (id == 0 || duration is < 1 or > 5999 || repeats is < 1 or > 1_000_000 ||
            cap < 1 || cap > 6000 || (!syncEvery && cap < duration) || roster.Length is < 1 or > 15 ||
            roster.Distinct(StringComparer.Ordinal).Count() != roster.Length ||
            roster.Any(x => string.IsNullOrWhiteSpace(x)) || (logo != 0 && logo != FactoryProtocol.RepetitionLogoSeconds) ||
            (repeatStart != 0 && (repeatStart != FactoryProtocol.RepetitionLogoSeconds || logo != FactoryProtocol.RepetitionLogoSeconds)))
            throw new ArgumentOutOfRangeException(nameof(duration), "Invalid repetition program.");
        if (resume is not null && (resume.CurrentRepeat < 1 || resume.CurrentRepeat > repeats ||
            resume.RemainingSeconds < 1 || resume.RemainingSeconds > duration))
            throw new ArgumentOutOfRangeException(nameof(resume), "No valid paused countdown remains.");
        uint capacity = syncEvery ? 1u : (cap + logo + repeatStart) / (duration + logo + repeatStart);
        uint first = resume?.CurrentRepeat ?? 1;
        bool partial = resume is not null && resume.RemainingSeconds < duration;
        uint fullRepeats = repeats - first + (partial ? 0u : 1u);
        // A continuation may prepend one shortened repeat to an otherwise
        // valid 10,000-segment plan; do not strand a saved maximum-size plan.
        if (((ulong)fullRepeats + capacity - 1) / capacity > 10_000)
            throw new ArgumentOutOfRangeException(nameof(repeats), "Programs may contain at most 10,000 segments.");
        var segments = new List<ProgramSegment>();
        if (partial)
        {
            segments.Add(new(1, first, 1, resume!.RemainingSeconds, logo, repeatStart));
            first++;
        }
        while (first <= repeats)
        {
            uint count = Math.Min(capacity, repeats - first + 1);
            segments.Add(new((uint)segments.Count + 1, first, count, duration, logo, repeatStart));
            first = checked(first + count);
        }
        return new(id, duration, repeats, cap, roster, segments, resume, logo, repeatStart, syncEvery);
    }

    public void Validate()
    {
        RepetitionProgram canonical = CreateCore(PlanId, DurationSeconds, TotalRepeats, Participants, SegmentCapSeconds,
            ResumePosition, LogoSeconds, RepeatStartSeconds, SyncEveryRepetition);
        if (!canonical.Segments.SequenceEqual(Segments))
            throw new InvalidDataException("Saved segment plan does not match its geometry.");
    }
}

public static class ProductionTimingBudget
{
    public const long VisibleFleetBudgetUs = 30_000;
    // Requires modules qualified within +/-1 ppm of the fleet median.
    // The 5 ms presentation reserve still needs the installation's physical test.
    public const long QualifiedPairRateBoundPpm = 2;
    public const long PresentationReserveUs = 5_000;
    public static long StartAllowanceUs(uint segmentSeconds, double? measuredPairRatePpm = null)
    {
        if (segmentSeconds is < 1 or > 6000) throw new ArgumentOutOfRangeException(nameof(segmentSeconds));
        if (measuredPairRatePpm.HasValue && (!double.IsFinite(measuredPairRatePpm.Value) || measuredPairRatePpm.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(measuredPairRatePpm));
        double rate = Math.Max(QualifiedPairRateBoundPpm, measuredPairRatePpm ?? QualifiedPairRateBoundPpm);
        if (!double.IsFinite(rate) || rate < 0) throw new ArgumentOutOfRangeException(nameof(measuredPairRatePpm));
        double drift = Math.Ceiling(segmentSeconds * rate);
        if (drift >= VisibleFleetBudgetUs - PresentationReserveUs) return 0;
        return VisibleFleetBudgetUs - PresentationReserveUs - (long)drift;
    }
}

public enum ProgramStage { Ready, Preparing, Arming, Running, BetweenSegments, Held, Completed, Stopped, WaitingForReadiness }

// Evidence survives new plans, but is activated only against fresh execution/health identities.
public sealed record FleetRateEvidence(double PairRatePpm,
    IReadOnlyDictionary<string, ulong> BootIds, IReadOnlyDictionary<string, string> BuildIds)
{
    public bool Matches(IReadOnlyList<string> roster, IReadOnlyList<ProgramDeviceObservation> observations) =>
        double.IsFinite(PairRatePpm) && PairRatePpm >= 0 && BootIds.Count == roster.Count &&
        BuildIds.Count == roster.Count && observations.Count == roster.Count &&
        observations.Select(x => x.Execution.DeviceId).Distinct(StringComparer.Ordinal).Count() == roster.Count &&
        observations.All(x => roster.Contains(x.Execution.DeviceId) && x.Health.DeviceId == x.Execution.DeviceId &&
            BootIds.TryGetValue(x.Execution.DeviceId, out ulong boot) && boot != 0 && boot == x.Execution.BootId &&
            BuildIds.TryGetValue(x.Execution.DeviceId, out string? build) && !string.IsNullOrWhiteSpace(build) &&
            build == x.Health.FirmwareBuildId);
}

public sealed record ProgramExecutionState(RepetitionProgram Program, ProgramStage Stage,
    uint NextSegmentIndex, uint CurrentSegmentIndex, ulong CurrentCommandId,
    long TargetMasterMicroseconds, string ControllerSessionId,
    IReadOnlyDictionary<string, ulong> BootIds, IReadOnlyDictionary<string, string> FirmwareBuildIds,
    string Message, bool RequiresOperator = false, int SegmentStartAttempts = 0,
    DateTimeOffset? ReadinessWaitStartedAtUtc = null, double? MeasuredPairRatePpm = null, FleetRateEvidence? FleetRate = null,
    IReadOnlyList<string>? PendingMissingParticipants = null, IReadOnlyDictionary<string, uint>? AutomaticRecoveryCounts = null,
    StoppedCountdownCheckpoint? StopCheckpoint = null)
{
    public uint CompletedRepeatCount => checked((Program.ResumePosition?.CurrentRepeat ?? 1) - 1 +
        (uint)Program.Segments.Where(s => s.Index < NextSegmentIndex).Aggregate(0UL, (sum, s) => sum + s.RepeatCount));
    public bool Active => Stage is not (ProgramStage.Completed or ProgramStage.Stopped);
    public ProgramSegment? CurrentSegment => CurrentSegmentIndex == 0 ? null :
        Program.Segments.Single(s => s.Index == CurrentSegmentIndex);
    public static ProgramExecutionState New(RepetitionProgram program, ProgramExecutionState? previous = null) =>
        new(program, ProgramStage.Ready, 1, 0, 0, 0, "",
            new Dictionary<string, ulong>(), new Dictionary<string, string>(), "Ready to synchronize.",
            FleetRate: previous?.FleetRate ?? (previous?.MeasuredPairRatePpm is double rate
                ? new FleetRateEvidence(rate, previous.BootIds, previous.FirmwareBuildIds) : null));
}

public interface IProgramStateStore
{
    ProgramExecutionState? Load();
    void Save(ProgramExecutionState state);
    // RESET discards execution progress. It may recover a valid frozen roster
    // even when the STOP checkpoint or ARM progress cannot be resumed.
    RepetitionProgram? LoadPlanForReset() => Load()?.Program;
}

public sealed class FileProgramStateStore(string path) : IProgramStateStore
{
    private sealed record Envelope(int Version, string Payload, string Sha256);
    private ProgramExecutionState? ReadJournal()
    {
        if (!File.Exists(path)) return null;
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty program journal.");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));
        if (envelope.Version != 1 || !string.Equals(digest, envelope.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Program journal is damaged; execution is held.");
        ProgramExecutionState state = JsonSerializer.Deserialize<ProgramExecutionState>(envelope.Payload)
            ?? throw new InvalidDataException("Invalid program journal.");
        return state;
    }

    public RepetitionProgram? LoadPlanForReset()
    {
        try
        {
            ProgramExecutionState? state = ReadJournal();
            state?.Program.Validate();
            return state?.Program;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or
            ArgumentException or OverflowException or NullReferenceException)
        {
            // No trustworthy plan remains. The operator's RESET will create a
            // new journal for the selected and observed active devices.
            return null;
        }
    }

    public ProgramExecutionState? Load()
    {
        ProgramExecutionState? state = ReadJournal();
        if (state is null) return null;
        state.Program.Validate();
        state.StopCheckpoint?.Validate(state.Program, state.Stage);
        if (!Enum.IsDefined(state.Stage) || state.NextSegmentIndex < 1 ||
            state.NextSegmentIndex > state.Program.Segments.Count + 1 ||
            state.CurrentSegmentIndex > state.Program.Segments.Count || state.SegmentStartAttempts < 0 ||
            (state.MeasuredPairRatePpm.HasValue && (!double.IsFinite(state.MeasuredPairRatePpm.Value) || state.MeasuredPairRatePpm.Value < 0)))
            throw new InvalidDataException("Invalid saved execution progress.");
        if ((state.Stage is ProgramStage.Arming or ProgramStage.Running or ProgramStage.WaitingForReadiness || state.CurrentCommandId != 0) &&
            (state.CurrentSegmentIndex == 0 || state.CurrentCommandId == 0 || state.TargetMasterMicroseconds <= 0 ||
             string.IsNullOrWhiteSpace(state.ControllerSessionId) ||
             state.Program.Participants.Any(id => !state.BootIds.TryGetValue(id, out ulong boot) || boot == 0 ||
                 !state.FirmwareBuildIds.TryGetValue(id, out string? build) || string.IsNullOrWhiteSpace(build))))
            throw new InvalidDataException("Saved ARM identity is incomplete; execution is held.");
        return state;
    }

    public void Save(ProgramExecutionState state)
    {
        state.Program.Validate();
        state.StopCheckpoint?.Validate(state.Program, state.Stage);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string payload = JsonSerializer.Serialize(state);
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Envelope(1, payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))));
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed record ProgramDeviceObservation(ExecutionStatusPacket Execution, StatusPacket Health);
public enum ReconcileAction { ObserveActiveSegment, SynchronizeNextSegment, Hold, Complete, WaitForReadiness }
public sealed record ProgramReconciliation(ReconcileAction Action, string Message, uint? RemainingSeconds = null);

// All wire effects live in the app adapter. This coordinator owns the durable
// state transitions and never treats controller clock expiry as device completion.
public sealed class SegmentedProgramCoordinator(IProgramStateStore store, ProgramExecutionState initial)
{
    public ProgramExecutionState State { get; private set; } = initial;
    private void Commit(ProgramExecutionState next)
    {
        store.Save(next); // Before returning permission to send any new segment.
        State = next;
    }

    public void BeginPreparation()
    {
        if (State.Stage is not (ProgramStage.Ready or ProgramStage.BetweenSegments) ||
            State.SegmentStartAttempts >= ProductionRecoveryPolicy.MaximumStartAttempts)
            throw new InvalidOperationException("Reconcile or resume the existing program first.");
        Commit(State with { Stage = ProgramStage.Preparing, CurrentSegmentIndex = State.NextSegmentIndex,
            CurrentCommandId = 0, TargetMasterMicroseconds = 0, SegmentStartAttempts = checked(State.SegmentStartAttempts + 1),
            ReadinessWaitStartedAtUtc = null, Message = "Synchronizing the frozen fleet." });
    }

    public void RecordArm(ulong command, long target, string controllerSession,
        IReadOnlyList<ProgramDeviceObservation> observations)
    {
        if (State.Stage != ProgramStage.Preparing || command == 0 || target <= 0 ||
            observations.Count != State.Program.Participants.Count ||
            observations.Select(x => x.Execution.DeviceId).Distinct(StringComparer.Ordinal).Count() != observations.Count ||
            observations.Any(x => !State.Program.Participants.Contains(x.Execution.DeviceId) || !OperationalReady(x) ||
                    x.Health.CommandId != x.Execution.CommandId || x.Health.State != x.Execution.State ||
                    (State.BootIds.TryGetValue(x.Execution.DeviceId, out ulong boot) && boot != x.Execution.BootId) ||
                    (State.FirmwareBuildIds.TryGetValue(x.Execution.DeviceId, out string? build) && build != x.Health.FirmwareBuildId)))
            throw new InvalidOperationException("Frozen participants are not ready for a segment.");
        Commit(State with { Stage = ProgramStage.Arming, CurrentCommandId = command,
            TargetMasterMicroseconds = target, ControllerSessionId = controllerSession,
            BootIds = observations.ToDictionary(x => x.Execution.DeviceId, x => x.Execution.BootId),
            FirmwareBuildIds = observations.ToDictionary(x => x.Execution.DeviceId, x => x.Health.FirmwareBuildId!),
            Message = "ARM intent persisted before START_AT transmission." });
    }

    public void CancelPreparationForRetry()
    {
        if (State.Stage != ProgramStage.Preparing || State.CurrentCommandId != 0)
            throw new InvalidOperationException("An ARM intent requires confirmed cancellation.");
        Commit(State with { Stage = ProgramStage.BetweenSegments, CurrentSegmentIndex = 0,
            TargetMasterMicroseconds = 0, RequiresOperator = false, Message = "No ARM sent; retrying the same segment." });
    }

    public void ConfirmArmAbortForRetry(ulong resetCommandId, long confirmedMasterUs, IReadOnlyDictionary<string, ProgramDeviceObservation> fresh)
    {
        if (State.Stage is not (ProgramStage.Arming or ProgramStage.Running or ProgramStage.Held) || State.CurrentCommandId == 0 ||
            resetCommandId == 0 || confirmedMasterUs <= 0 || confirmedMasterUs >= State.TargetMasterMicroseconds || fresh.Count != State.Program.Participants.Count ||
            State.Program.Participants.Any(id => !fresh.TryGetValue(id, out ProgramDeviceObservation? x) ||
                x.Execution.DeviceId != id || x.Health.DeviceId != id || x.Execution.ResponseId == 0 ||
                x.Execution.BootId == 0 || string.IsNullOrWhiteSpace(x.Health.FirmwareBuildId) ||
                x.Execution.State != TimerState.Ready || x.Health.State != TimerState.Ready ||
                x.Execution.PlanId != 0 || x.Execution.SegmentIndex != 0 || x.Execution.ElapsedSegmentSeconds != 0 ||
                x.Execution.CommandId != resetCommandId || x.Health.CommandId != resetCommandId))
            throw new InvalidOperationException("Every participant needs fresh READY evidence for the confirmed RESET.");
        Commit(State with { Stage = ProgramStage.BetweenSegments, CurrentSegmentIndex = 0, CurrentCommandId = 0,
            TargetMasterMicroseconds = 0, ControllerSessionId = "", RequiresOperator = false, ReadinessWaitStartedAtUtc = null,
            BootIds = fresh.ToDictionary(x => x.Key, x => x.Value.Execution.BootId),
            FirmwareBuildIds = fresh.ToDictionary(x => x.Key, x => x.Value.Health.FirmwareBuildId!),
            Message = "ARM cancellation confirmed; repetition progress preserved. Retrying the same segment." });
    }

    public void RecordMeasuredPairRate(double rate)
    {
        if (!double.IsFinite(rate) || rate < 0) throw new ArgumentOutOfRangeException(nameof(rate));
        Commit(State with { MeasuredPairRatePpm = rate,
            FleetRate = new(rate, new Dictionary<string, ulong>(State.BootIds),
                new Dictionary<string, string>(State.FirmwareBuildIds)) });
    }

    public void ResolveFleetRateEvidence(IReadOnlyList<ProgramDeviceObservation> fresh)
    {
        if (State.FleetRate is not { } evidence) return;
        bool matches = evidence.Matches(State.Program.Participants, fresh);
        Commit(State with { MeasuredPairRatePpm = matches ? evidence.PairRatePpm : null,
            FleetRate = matches ? evidence : null });
    }

    public void MarkRunning()
    {
        if (State.Stage != ProgramStage.Arming) throw new InvalidOperationException("No armed segment.");
        Commit(State with { Stage = ProgramStage.Running, Message = "Segment active; network polling is silent." });
    }

    public void Hold(string message) => Commit(State with {
        // A late failure must not turn a persisted pause/reset into an invalid
        // Held+StopCheckpoint journal, or resurrect a completed program.
        Stage = State.Active ? ProgramStage.Held : State.Stage,
        RequiresOperator = State.Active, Message = message });
    public void Stop() => Commit(State with { Stage = ProgramStage.Stopped,
        RequiresOperator = false, Message = "Program stopped by the operator." });

    public void Stop(ulong resetCommandId, uint currentRepeat, uint remainingSeconds, bool positionKnown = true,
        IEnumerable<string>? participants = null)
    {
        RepetitionProgram plan = participants is null ? State.Program : State.Program.WithParticipants(participants);
        SaveStop(new(resetCommandId, currentRepeat, remainingSeconds, plan.Participants.ToArray(),
            PositionKnown: positionKnown), plan);
    }

    public void SelectStoppedParticipants(IEnumerable<string> participants, ulong resetCommandId)
    {
        if (State.Stage != ProgramStage.Stopped || State.StopCheckpoint is not { } checkpoint)
            throw new InvalidOperationException("No saved STOP/RESET checkpoint.");
        RepetitionProgram plan = State.Program.WithParticipants(participants);
        if (plan.Participants.SequenceEqual(State.Program.Participants)) return;
        SaveStop(checkpoint with { ResetCommandId = resetCommandId, PendingDeviceIds = plan.Participants.ToArray() }, plan);
    }

    public void ResetProgress(ulong resetCommandId) => SaveStop(new(resetCommandId, 1,
        State.Program.DurationSeconds, State.Program.Participants.ToArray(), DiscardProgress: true));

    private void SaveStop(StoppedCountdownCheckpoint checkpoint, RepetitionProgram? plan = null)
    {
        plan ??= State.Program;
        checkpoint.Validate(plan, ProgramStage.Stopped);
        // A selected timer can be new to the paused run. Old ARM/boot identities
        // must not be carried into its stopped journal or the next fresh sync.
        Commit(ProgramExecutionState.New(plan, State) with { Stage = ProgramStage.Stopped, StopCheckpoint = checkpoint,
            RequiresOperator = false, Message = checkpoint.DiscardProgress
                ? "RESET: saved progress cleared; confirming the selected timers are stopped."
                : "STOP: countdown position saved; confirming the selected timers are stopped." });
    }

    public void ConfirmStoppedDevices(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh) =>
        ConfirmStoppedDevices(fresh, recheckAll: false);

    public void RecheckAllStoppedDevices(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh) =>
        ConfirmStoppedDevices(fresh, recheckAll: true);

    private void ConfirmStoppedDevices(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh, bool recheckAll)
    {
        if (State.Stage != ProgramStage.Stopped || State.StopCheckpoint is not { } checkpoint)
            throw new InvalidOperationException("No STOP/RESET confirmation is pending.");
        IEnumerable<string> required = recheckAll ? State.Program.Participants : checkpoint.PendingDeviceIds;
        string[] pending = required.Where(id => !HasResetEvidence(id, checkpoint.ResetCommandId, fresh)).ToArray();
        Commit(State with { StopCheckpoint = checkpoint with { PendingDeviceIds = pending },
            Message = pending.Length != 0 ? "Stop not confirmed: " + string.Join(", ", pending) + ". Restore connectivity and retry."
                : checkpoint.DiscardProgress ? "RESET confirmed. START will begin a new countdown."
                : checkpoint.CanResumePosition ? "PAUSED. START will continue the saved countdown."
                : "STOP confirmed. RESET to prepare a new countdown." });
    }

    private static bool HasResetEvidence(string id, ulong resetId,
        IReadOnlyDictionary<string, ProgramDeviceObservation> fresh) =>
        fresh.TryGetValue(id, out ProgramDeviceObservation? x) && StopResetConfirmation.Failure(id, resetId, x) is null;

    private bool Matches(ProgramDeviceObservation x)
    {
        ProgramSegment? segment = State.CurrentSegment;
        ExecutionStatusPacket e = x.Execution;
        return segment is not null && e.PlanId == State.Program.PlanId &&
            e.SegmentIndex == segment.Index && e.CommandId == State.CurrentCommandId &&
            e.DurationSeconds == segment.DurationSeconds && e.RepeatCount == segment.RepeatCount &&
            e.LogoSeconds.GetValueOrDefault() == segment.LogoSeconds &&
            e.RepeatStartSeconds.GetValueOrDefault() == segment.RepeatStartSeconds &&
            e.FirstRepeat == segment.FirstRepeat && e.TotalRepeats == State.Program.TotalRepeats &&
            e.TargetMasterMicroseconds == State.TargetMasterMicroseconds &&
            x.Health.CommandId == e.CommandId && x.Health.State == e.State;
    }

    private bool SameBoot(ProgramDeviceObservation x) =>
        State.BootIds.TryGetValue(x.Execution.DeviceId, out ulong boot) && boot == x.Execution.BootId &&
        State.FirmwareBuildIds.TryGetValue(x.Execution.DeviceId, out string? build) && build == x.Health.FirmwareBuildId;

    // FINISHED is meaningful only when the last scheduled repeat and exact
    // segment end are both reflected by the command-scoped EXEC payload.
    private bool ConfirmedCurrentCompletion(ProgramDeviceObservation x)
    {
        ProgramSegment? segment = State.CurrentSegment;
        return segment is not null && SameBoot(x) && Matches(x) &&
            x.Execution.State == TimerState.Finished &&
            x.Execution.CurrentRepeat == segment.LastRepeat &&
            x.Execution.ElapsedSegmentSeconds == segment.Duration &&
            x.Execution.ElapsedSegmentMicroseconds == checked((long)segment.Duration * 1_000_000) &&
            x.Health.RemainingSeconds == 0;
    }

    private void CompleteCurrent()
    {
        uint next = checked(State.CurrentSegmentIndex + 1);
        bool done = next > State.Program.Segments.Count;
        Commit(State with { NextSegmentIndex = next, Stage = done ? ProgramStage.Completed : ProgramStage.BetweenSegments,
            Message = done ? "Program complete." : "At 00:00; synchronizing the next segment.", RequiresOperator = false,
            SegmentStartAttempts = 0, ReadinessWaitStartedAtUtc = null, PendingMissingParticipants = null });
    }

    public ProgramReconciliation Reconcile(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh,
        DateTimeOffset? observedAtUtc = null, bool plannedSegmentEnded = false)
    {
        if (!State.Active) return new(ReconcileAction.Complete, State.Message);
        ProgramDeviceObservation[] present = State.Program.Participants.Where(fresh.ContainsKey).Select(id => fresh[id]).ToArray();
        bool missing = present.Length != State.Program.Participants.Count;
        ProgramDeviceObservation[] active = present.Where(x => x.Execution.State is TimerState.Armed or TimerState.Running).ToArray();
        if (active.Length != 0)
        {
            if (active.Any(x => !Matches(x) || !SameBoot(x)))
            {
                Hold("Unknown active command or changed boot identity; observe the boards and resolve before resuming.");
                return new(ReconcileAction.Hold, State.Message);
            }
            // A fresh reply can still contain a snapshot frozen by an older
            // firmware's disconnected CommandTask. Once our own same-session
            // deadline has passed, do not trust its RemainingSeconds as a new
            // countdown duration. Keep probing for exact FINISHED evidence,
            // with the same bounded 10 s window used for absent devices.
            if (plannedSegmentEnded && State.CurrentCommandId != 0 &&
                State.Stage is (ProgramStage.Running or ProgramStage.WaitingForReadiness))
            {
                DateTimeOffset now = observedAtUtc ?? DateTimeOffset.UtcNow;
                DateTimeOffset since = State.ReadinessWaitStartedAtUtc ?? now;
                if (now - since >= ProductionRecoveryPolicy.ReadinessTimeout)
                {
                    Hold("At 00:00, participants still reported RUNNING/ARMED after 10 s; fleet held for operator review.");
                    return new(ReconcileAction.Hold, State.Message);
                }
                string[] stale = active.Select(x => x.Execution.DeviceId).Order(StringComparer.Ordinal).ToArray();
                Commit(State with { Stage = ProgramStage.WaitingForReadiness,
                    ReadinessWaitStartedAtUtc = since,
                    Message = "At 00:00; awaiting exact FINISHED evidence (up to 10 s): " + string.Join(", ", stale) });
                return new(ReconcileAction.WaitForReadiness, State.Message);
            }
            bool fault = missing || present.Any(x => !Matches(x) || !SameBoot(x));
            Commit(State with { Stage = ProgramStage.Running, RequiresOperator = State.RequiresOperator || present.Any(x => !Matches(x) || !SameBoot(x)),
                Message = fault ? "A participant is missing or rebooted; healthy devices continue. Next segment will be held."
                                : "Recovered the active segment; no START command will be resent." });
            return new(ReconcileAction.ObserveActiveSegment, State.Message,
                active.Max(x => checked((uint)Math.Ceiling((x.Execution.SegmentDurationSeconds * 1_000_000d -
                    x.Execution.ElapsedSegmentMicroseconds) / 1_000_000d))));
        }
        if (missing)
        {
            string[] absent = State.Program.Participants.Where(id => !fresh.ContainsKey(id)).ToArray();
            // A missing device at an ordinary RUNNING-segment boundary may return
            // during a bounded wait. Never extend that automatic path to a held
            // or unconfirmed ARM abort: no completion evidence can prove cancellation.
            if (State.CurrentCommandId == 0 || State.RequiresOperator ||
                State.Stage is not (ProgramStage.Running or ProgramStage.WaitingForReadiness))
            {
                Hold("Required participants missing; operator review: " + string.Join(", ", absent));
                return new(ReconcileAction.Hold, State.Message);
            }
            DateTimeOffset now = observedAtUtc ?? DateTimeOffset.UtcNow;
            DateTimeOffset since = State.ReadinessWaitStartedAtUtc ?? now;
            if (now - since >= ProductionRecoveryPolicy.ReadinessTimeout)
            {
                Hold("Required participants remained missing for 10 s: " + string.Join(", ", absent));
                return new(ReconcileAction.Hold, State.Message);
            }
            string[] pending = (State.PendingMissingParticipants ?? Array.Empty<string>())
                .Concat(absent).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Commit(State with { Stage = ProgramStage.WaitingForReadiness, ReadinessWaitStartedAtUtc = since,
                PendingMissingParticipants = pending,
                Message = "At 00:00; waiting up to 10 s for missing participants: " + string.Join(", ", absent) });
            return new(ReconcileAction.WaitForReadiness, State.Message);
        }
        if (State.CurrentCommandId != 0)
        {
            if (!present.All(ConfirmedCurrentCompletion) || State.RequiresOperator)
            {
                Hold("Execution or boot continuity needs operator review before the next segment.");
                return new(ReconcileAction.Hold, State.Message);
            }
            if (present.Any(x => !OperationalReady(x)))
            {
                if (present.Any(x => x.Health.DiagnosticFlags != 0 || string.IsNullOrWhiteSpace(x.Health.FirmwareBuildId)))
                {
                    Hold("Diagnostic or incompatible firmware is active; review the frozen fleet.");
                    return new(ReconcileAction.Hold, State.Message);
                }
                DateTimeOffset now = observedAtUtc ?? DateTimeOffset.UtcNow;
                DateTimeOffset since = State.ReadinessWaitStartedAtUtc ?? now;
                if (now - since >= ProductionRecoveryPolicy.ReadinessTimeout)
                {
                    Hold("RTC/post-run temperature recovery exceeded 10 s; fleet remains at 00:00.");
                    return new(ReconcileAction.Hold, State.Message);
                }
                Commit(State with { Stage = ProgramStage.WaitingForReadiness, ReadinessWaitStartedAtUtc = since,
                    Message = "At 00:00; waiting up to 10 s for RTC recovery and fresh post-run temperature." });
                return new(ReconcileAction.WaitForReadiness, State.Message);
            }
            // Count automatic recoveries only after all original boards have
            // supplied exact same-boot, same-command FINISHED evidence.
            if (State.PendingMissingParticipants is { Count: > 0 } recovered)
            {
                var counts = (State.AutomaticRecoveryCounts ?? new Dictionary<string, uint>())
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                foreach (string id in recovered)
                    counts[id] = checked(counts.GetValueOrDefault(id) + 1);
                Commit(State with { AutomaticRecoveryCounts = counts });
            }
            CompleteCurrent();
        }
        if (State.Stage == ProgramStage.Completed) return new(ReconcileAction.Complete, State.Message);
        if (State.Stage == ProgramStage.Held || present.Any(x => !OperationalReady(x)))
        {
            Hold("Fleet held at 00:00; wait for recovery, then press START to continue.");
            return new(ReconcileAction.Hold, State.Message);
        }
        Commit(State with { Stage = ProgramStage.BetweenSegments });
        return new(ReconcileAction.SynchronizeNextSegment, State.Message);
    }

    public void ResumeAfterOperatorReview(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh)
    {
        if (State.Stage != ProgramStage.Held)
            throw new InvalidOperationException("RESUME requires a HELD program.");
        string[] unavailable = State.Program.Participants.Select(id =>
        {
            fresh.TryGetValue(id, out ProgramDeviceObservation? x);
            string reason = ProductionRecoveryPolicy.OperationalReadinessReason(x);
            return reason == "READY" ? "" : id + ": " + reason;
        }).Where(x => x.Length != 0).ToArray();
        if (unavailable.Length != 0 || fresh.Count != State.Program.Participants.Count)
            throw new InvalidOperationException("RESUME refused; frozen participants unready: " +
                (unavailable.Length == 0 ? "unexpected participant inventory" : string.Join("; ", unavailable)));
        if (State.CurrentCommandId != 0)
        {
            // A matching FINISHED report from a single device never proves that
            // the frozen fleet ran this segment. In particular, an ARM abort may
            // have RESET some devices while another ran alone. An operator RESUME
            // must not silently credit the non-running devices.
            //
            // A changed BootId explicitly denotes a replacement/rebooted board:
            // the operator can accept that board's missed repetition on RESUME.
            // Every original, same-boot participant must independently prove
            // FINISHED for exactly the persisted plan/segment/command/T*.
            string[] notFinished = State.Program.Participants
                .Where(id => SameBoot(fresh[id]) && !ConfirmedCurrentCompletion(fresh[id]))
                .ToArray();
            if (notFinished.Length != 0)
                throw new InvalidOperationException(
                    "Cannot credit this segment: unchanged-boot participants lack matching FINISHED: " +
                    string.Join(", ", notFinished) + ". Stop the program and start a new plan for the remaining repetitions; do not replay or skip this segment.");
            if (!State.Program.Participants.Any(id => ConfirmedCurrentCompletion(fresh[id])))
                throw new InvalidOperationException(
                    "No original participant confirms completion. Unconfirmed ARM cannot be safely replayed; " +
                    "prove cancellation before T* or stop/reset after reconciling the fleet.");
            CompleteCurrent();
        }
        else Commit(State with { Stage = ProgramStage.BetweenSegments, RequiresOperator = false,
            SegmentStartAttempts = 0, ReadinessWaitStartedAtUtc = null, Message = "Operator resumed the held fleet." });
        Commit(State with {
            BootIds = fresh.ToDictionary(x => x.Key, x => x.Value.Execution.BootId),
            FirmwareBuildIds = fresh.ToDictionary(x => x.Key, x => x.Value.Health.FirmwareBuildId!) });
    }

    public static bool OperationalReady(ProgramDeviceObservation x) =>
        x.Health.DeviceId == x.Execution.DeviceId && x.Health.CommandId == x.Execution.CommandId &&
        x.Health.State == x.Execution.State && x.Execution.State is TimerState.Ready or TimerState.Finished &&
        x.Health.RtcQueueDrops.HasValue && x.Health.RtcState == RtcDisciplineState.Locked && x.Health.DiagnosticFlags == 0 &&
        x.Health.RtcFitPoints >= 128 && x.Health.RtcAcceptedEdges >= 129 &&
        x.Health.RtcFitRmsMicroseconds is double rms && double.IsFinite(rms) && rms >= 0 && rms <= 3 &&
        x.Health.RtcTemperatureValid == true && !string.IsNullOrWhiteSpace(x.Health.FirmwareBuildId) &&
        x.Execution.RtcRecoveredEdges >= 129 && x.Execution.BootId != 0;
}

public static class ProductionRecoveryPolicy
{
    public const int MaximumStartAttempts = 3; // initial attempt plus two retries
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // A diagnostic view of the existing OperationalReady predicate. It changes
    // no authorization threshold and is shared by operator refusals and audits.
    public static string OperationalReadinessReason(ProgramDeviceObservation? x)
    {
        if (x is null) return "STATUS/EXEC missing";
        var h = x.Health;
        var e = x.Execution;
        if (h.DeviceId != e.DeviceId) return "STATUS/EXEC device mismatch";
        if (h.CommandId != e.CommandId) return "STATUS/EXEC command mismatch";
        if (h.State != e.State) return "STATUS/EXEC state mismatch";
        if (e.State is not (TimerState.Ready or TimerState.Finished))
            return "timer state " + e.State + " is not idle";
        if (!h.RtcQueueDrops.HasValue) return "RTC queue-drop telemetry missing";
        if (h.RtcState != RtcDisciplineState.Locked) return "RTC state " + h.RtcState;
        if (h.DiagnosticFlags != 0) return "DiagnosticFlags nonzero";
        if (h.RtcFitPoints is not ushort points || points < 128) return "RTC fit points below 128";
        if (h.RtcAcceptedEdges is not ulong accepted || accepted < 129) return "RTC accepted edges below 129";
        if (h.RtcFitRmsMicroseconds is not double rms || !double.IsFinite(rms) || rms < 0)
            return "RTC RMS invalid or unavailable";
        if (rms > 3) return "RTC RMS " + rms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " us exceeds 3.0 us";
        if (h.RtcTemperatureValid != true) return "RTC temperature unavailable";
        if (string.IsNullOrWhiteSpace(h.FirmwareBuildId)) return "firmware build identity missing";
        if (e.RtcRecoveredEdges < 129) return "RTC recovered edges below 129";
        if (e.BootId == 0) return "boot identity missing";
        return "READY";
    }

    public static bool CanRetry(int attempts, bool safelyCancelled) => safelyCancelled && attempts < MaximumStartAttempts;
}

public sealed record RtcSegmentDriftPoint(string DeviceId, ulong BootId, string BuildId,
    long MasterEpochUs, long DisciplinedEpochUs, string Bssid);
public sealed record RtcSegmentDriftEstimate(string DeviceId, double RatePpm, double RelativeFleetPpm,
    double? InnovationUs, bool ValidationRequired, bool SustainedDeviation);

public sealed class SegmentDriftMonitor
{
    private readonly Dictionary<string, RtcSegmentDriftPoint> previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> previousRelativeRate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> deviations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> pendingRate = new(StringComparer.Ordinal);
    public void Reset()
    {
        previous.Clear(); previousRelativeRate.Clear(); deviations.Clear(); pendingRate.Clear();
    }
    public IReadOnlyList<RtcSegmentDriftEstimate> Observe(IReadOnlyList<RtcSegmentDriftPoint> points)
    {
        var rates = new List<(RtcSegmentDriftPoint Point, double Rate, long Interval)>();
        foreach (RtcSegmentDriftPoint point in points)
        {
            if (previous.TryGetValue(point.DeviceId, out RtcSegmentDriftPoint? old) &&
                old.BootId == point.BootId && old.BuildId == point.BuildId &&
                point.MasterEpochUs > old.MasterEpochUs && point.DisciplinedEpochUs > old.DisciplinedEpochUs)
            {
                long dt = point.MasterEpochUs - old.MasterEpochUs;
                double rate = ((point.DisciplinedEpochUs - old.DisciplinedEpochUs) - (double)dt) / dt * 1e6;
                rates.Add((point, rate, dt));
            }
            else { previousRelativeRate.Remove(point.DeviceId); deviations.Remove(point.DeviceId); pendingRate.Remove(point.DeviceId); }
            previous[point.DeviceId] = point;
        }
        if (rates.Count < 2) return [];
        double[] sorted = rates.Select(x => x.Rate).Order().ToArray();
        double median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        var estimates = new List<RtcSegmentDriftEstimate>();
        foreach (var item in rates)
        {
            double relative = item.Rate - median;
            double? innovation = previousRelativeRate.TryGetValue(item.Point.DeviceId, out double oldRate)
                ? (relative - oldRate) * item.Interval / 1e6 : null;
            bool discontinuity = innovation.HasValue && Math.Abs(innovation.Value) > 500;
            // Two consecutive estimates agreeing within 500 us over this interval
            // establish a new reference. Keep this interval's innovation visible,
            // then use the new reference for subsequent innovation estimates.
            bool rebaseline = discontinuity && pendingRate.TryGetValue(item.Point.DeviceId, out double candidate) &&
                Math.Abs(relative - candidate) * item.Interval / 1e6 <= 500;
            if (discontinuity && !rebaseline) pendingRate[item.Point.DeviceId] = relative;
            else pendingRate.Remove(item.Point.DeviceId);
            // Sustained deviation is independent of phase innovation. One bad
            // sync epoch can corrupt two adjacent intervals; three estimates
            // beyond the limit also catch a steady ramp that never rebaselines.
            int count = Math.Abs(relative) > 1
                ? deviations.GetValueOrDefault(item.Point.DeviceId) + 1 : 0;
            deviations[item.Point.DeviceId] = count;
            if (!discontinuity || rebaseline) previousRelativeRate[item.Point.DeviceId] = relative;
            estimates.Add(new(item.Point.DeviceId, item.Rate, relative, innovation, discontinuity, count >= 3));
        }
        return estimates;
    }
}
