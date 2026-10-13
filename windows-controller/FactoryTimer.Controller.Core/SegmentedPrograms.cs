using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public sealed record ProgramSegment(uint Index, uint FirstRepeat, uint RepeatCount, uint DurationSeconds)
{
    public uint Duration => checked(DurationSeconds * RepeatCount);
    public uint LastRepeat => checked(FirstRepeat + RepeatCount - 1);
    public CommandPacket Command(ulong plan, uint total, ulong command, long target, ulong boot = 1, ulong previous = 0) =>
        new(CommandType.StartAt, command, DurationSeconds, 0, target,
            PlanId: plan, SegmentIndex: Index, RepeatCount: RepeatCount,
            FirstRepeat: FirstRepeat, TotalRepeats: total, ExpectedBootId: boot, ExpectedPreviousCommandId: previous);
}

public sealed record RepetitionProgram(ulong PlanId, uint DurationSeconds, uint TotalRepeats,
    uint SegmentCapSeconds, IReadOnlyList<string> Participants, IReadOnlyList<ProgramSegment> Segments)
{
    public static RepetitionProgram Create(ulong id, uint duration, uint repeats,
        IEnumerable<string> participants, uint cap = 6000)
    {
        string[] roster = participants.Order(StringComparer.Ordinal).ToArray();
        if (id == 0 || duration is < 1 or > 5999 || repeats is < 1 or > 1_000_000 ||
            cap < duration || cap > 6000 || roster.Length is < 1 or > 15 ||
            roster.Distinct(StringComparer.Ordinal).Count() != roster.Length ||
            roster.Any(x => string.IsNullOrWhiteSpace(x)))
            throw new ArgumentOutOfRangeException(nameof(duration), "Invalid repetition program.");
        uint capacity = cap / duration;
        if (((ulong)repeats + capacity - 1) / capacity > 10_000)
            throw new ArgumentOutOfRangeException(nameof(repeats), "Programs may contain at most 10,000 segments.");
        var segments = new List<ProgramSegment>();
        for (uint first = 1; first <= repeats;)
        {
            uint count = Math.Min(capacity, repeats - first + 1);
            segments.Add(new((uint)segments.Count + 1, first, count, duration));
            first = checked(first + count);
        }
        return new(id, duration, repeats, cap, roster, segments);
    }

    public void Validate()
    {
        RepetitionProgram canonical = Create(PlanId, DurationSeconds, TotalRepeats, Participants, SegmentCapSeconds);
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
    DateTimeOffset? ReadinessWaitStartedAtUtc = null, double? MeasuredPairRatePpm = null, FleetRateEvidence? FleetRate = null)
{
    public uint CompletedRepeatCount => (uint)Program.Segments.Where(s => s.Index < NextSegmentIndex).Aggregate(0UL, (sum, s) => sum + s.RepeatCount);
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
}

public sealed class FileProgramStateStore(string path) : IProgramStateStore
{
    private sealed record Envelope(int Version, string Payload, string Sha256);
    public ProgramExecutionState? Load()
    {
        if (!File.Exists(path)) return null;
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty program journal.");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));
        if (envelope.Version != 1 || !string.Equals(digest, envelope.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Program journal is damaged; execution is held.");
        ProgramExecutionState state = JsonSerializer.Deserialize<ProgramExecutionState>(envelope.Payload)
            ?? throw new InvalidDataException("Invalid program journal.");
        state.Program.Validate();
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

    public void Hold(string message) => Commit(State with { Stage = ProgramStage.Held,
        RequiresOperator = true, Message = message });
    public void Stop() => Commit(State with { Stage = ProgramStage.Stopped,
        RequiresOperator = false, Message = "Program stopped by the operator." });

    private bool Matches(ProgramDeviceObservation x)
    {
        ProgramSegment? segment = State.CurrentSegment;
        ExecutionStatusPacket e = x.Execution;
        return segment is not null && e.PlanId == State.Program.PlanId &&
            e.SegmentIndex == segment.Index && e.CommandId == State.CurrentCommandId &&
            e.DurationSeconds == segment.DurationSeconds && e.RepeatCount == segment.RepeatCount &&
            e.FirstRepeat == segment.FirstRepeat && e.TotalRepeats == State.Program.TotalRepeats &&
            e.TargetMasterMicroseconds == State.TargetMasterMicroseconds &&
            x.Health.CommandId == e.CommandId && x.Health.State == e.State;
    }

    private bool SameBoot(ProgramDeviceObservation x) =>
        State.BootIds.TryGetValue(x.Execution.DeviceId, out ulong boot) && boot == x.Execution.BootId &&
        State.FirmwareBuildIds.TryGetValue(x.Execution.DeviceId, out string? build) && build == x.Health.FirmwareBuildId;

    private void CompleteCurrent()
    {
        uint next = checked(State.CurrentSegmentIndex + 1);
        bool done = next > State.Program.Segments.Count;
        Commit(State with { NextSegmentIndex = next, Stage = done ? ProgramStage.Completed : ProgramStage.BetweenSegments,
            Message = done ? "Program complete." : "At 00:00; synchronizing the next segment.", RequiresOperator = false,
            SegmentStartAttempts = 0, ReadinessWaitStartedAtUtc = null });
    }

    public ProgramReconciliation Reconcile(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh, DateTimeOffset? observedAtUtc = null)
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
            Hold("Required participants missing at the segment boundary: " + string.Join(", ", State.Program.Participants.Where(id => !fresh.ContainsKey(id))));
            return new(ReconcileAction.Hold, State.Message);
        }
        if (State.CurrentCommandId != 0)
        {
            if (!present.All(x => Matches(x) && SameBoot(x) && x.Execution.State == TimerState.Finished) || State.RequiresOperator)
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
                    Hold("RTC/post-run temperature recovery exceeded 180 s; fleet remains at 00:00.");
                    return new(ReconcileAction.Hold, State.Message);
                }
                Commit(State with { Stage = ProgramStage.WaitingForReadiness, ReadinessWaitStartedAtUtc = since,
                    Message = "At 00:00; waiting up to 180 s for RTC recovery and fresh post-run temperature." });
                return new(ReconcileAction.WaitForReadiness, State.Message);
            }
            CompleteCurrent();
        }
        if (State.Stage == ProgramStage.Completed) return new(ReconcileAction.Complete, State.Message);
        if (State.Stage == ProgramStage.Held || present.Any(x => !OperationalReady(x)))
        {
            Hold("Fleet held at 00:00; wait for recovery, then use RESUME HELD PROGRAM.");
            return new(ReconcileAction.Hold, State.Message);
        }
        Commit(State with { Stage = ProgramStage.BetweenSegments });
        return new(ReconcileAction.SynchronizeNextSegment, State.Message);
    }

    public void ResumeAfterOperatorReview(IReadOnlyDictionary<string, ProgramDeviceObservation> fresh)
    {
        if (State.Stage != ProgramStage.Held || fresh.Count != State.Program.Participants.Count ||
            State.Program.Participants.Any(id => !fresh.TryGetValue(id, out ProgramDeviceObservation? x) ||
                !OperationalReady(x) || x.Execution.State is TimerState.Armed or TimerState.Running))
            throw new InvalidOperationException("All frozen participants must be present, idle, and recovered.");
        if (State.CurrentCommandId != 0)
        {
            // Explicitly acknowledge a replacement board's missed repeats. Never
            // replay a completed segment for the healthy boards.
            if (!fresh.Values.Any(x => Matches(x) && SameBoot(x) && x.Execution.State == TimerState.Finished))
                throw new InvalidOperationException("No confirmed completion; stop/reset this program before creating a replacement.");
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
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
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
            // but allow subsequent sustained-rate alerts instead of latching forever.
            bool rebaseline = discontinuity && pendingRate.TryGetValue(item.Point.DeviceId, out double candidate) &&
                Math.Abs(relative - candidate) * item.Interval / 1e6 <= 500;
            if (discontinuity && !rebaseline) pendingRate[item.Point.DeviceId] = relative;
            else pendingRate.Remove(item.Point.DeviceId);
            int count = (!discontinuity || rebaseline) && Math.Abs(relative) > 1
                ? deviations.GetValueOrDefault(item.Point.DeviceId) + 1 : 0;
            deviations[item.Point.DeviceId] = count;
            if (!discontinuity || rebaseline) previousRelativeRate[item.Point.DeviceId] = relative;
            estimates.Add(new(item.Point.DeviceId, item.Rate, relative, innovation, discontinuity, count >= 3));
        }
        return estimates;
    }
}
