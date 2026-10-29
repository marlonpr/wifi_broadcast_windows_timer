namespace FactoryTimer.Controller.Core;

public sealed record CountdownPosition(uint CurrentRepeat, uint RemainingSeconds)
{
    // The display is whole-second based. Rounding elapsed down preserves the
    // visible remaining second; a pause can replay less than one second.
    public static CountdownPosition AtPhase(ProgramSegment segment, long elapsedMicroseconds)
    {
        ulong elapsed = (ulong)Math.Max(0L, elapsedMicroseconds) / 1_000_000;
        if (elapsed >= segment.Duration) return new(segment.LastRepeat, 0);
        uint index = segment.FirstRepeat + FactoryTimer.Protocol.FactoryProtocol.RepeatOffset(segment.DurationSeconds,
            segment.RepeatCount, elapsed, segment.LogoSeconds, segment.RepeatStartSeconds);
        uint remaining = FactoryTimer.Protocol.FactoryProtocol.RepeatRemaining(segment.DurationSeconds, segment.RepeatCount, elapsed, segment.LogoSeconds, segment.RepeatStartSeconds);
        if (FactoryTimer.Protocol.FactoryProtocol.IsRepeatStart(segment.DurationSeconds, segment.RepeatCount, elapsed,
                segment.LogoSeconds, segment.RepeatStartSeconds)) return new(index, segment.DurationSeconds);
        // Pausing in a logo interval preserves the next countdown, rather than
        // persisting an invalid zero-second intermediate repetition.
        if (remaining == 0 && index < segment.LastRepeat) return new(index + 1, segment.DurationSeconds);
        return new(index, remaining);
    }

    public static CountdownPosition Capture(ProgramExecutionState state, long now, string session)
    {
        ProgramSegment? segment = state.Stage is ProgramStage.Ready or ProgramStage.BetweenSegments ||
            state.CurrentCommandId == 0
            ? state.Program.Segments.FirstOrDefault(s => s.Index == state.NextSegmentIndex)
            : state.CurrentSegment;
        if (segment is null) return new(state.Program.TotalRepeats, 0);
        if (state.CurrentCommandId == 0 || state.Stage is ProgramStage.Ready or ProgramStage.BetweenSegments)
            return new(segment.FirstRepeat, segment.DurationSeconds);
        if (state.ControllerSessionId != session)
            throw new InvalidOperationException("The saved controller clock needs a fresh device snapshot.");
        return WithFollowingRepeats(state.Program, segment, now - state.TargetMasterMicroseconds);
    }

    public static CountdownPosition WithFollowingRepeats(RepetitionProgram program, ProgramSegment segment, long phase)
    {
        CountdownPosition position = AtPhase(segment, phase);
        return position.RemainingSeconds == 0 && segment.LastRepeat < program.TotalRepeats
            ? new(segment.LastRepeat + 1, program.DurationSeconds) : position;
    }
}

public sealed record StoppedCountdownCheckpoint(ulong ResetCommandId, uint CurrentRepeat, uint RemainingSeconds,
    IReadOnlyList<string> PendingDeviceIds, bool DiscardProgress = false, bool PositionKnown = true)
{
    public bool CanResumePosition => !DiscardProgress && PositionKnown && RemainingSeconds > 0;
    public bool CanContinue => CanResumePosition && PendingDeviceIds.Count == 0;
    public CountdownPosition Position => new(CurrentRepeat, RemainingSeconds);

    public void Validate(RepetitionProgram program, ProgramStage stage)
    {
        if (stage != ProgramStage.Stopped || ResetCommandId == 0 || CurrentRepeat < 1 ||
            CurrentRepeat > program.TotalRepeats || RemainingSeconds > program.DurationSeconds ||
            (RemainingSeconds == 0 && CurrentRepeat != program.TotalRepeats) ||
            PendingDeviceIds.Distinct(StringComparer.Ordinal).Count() != PendingDeviceIds.Count ||
            PendingDeviceIds.Any(id => !program.Participants.Contains(id)))
            throw new InvalidDataException("Invalid saved STOP/RESET checkpoint.");
    }
}
