using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller;

internal sealed class ControllerClock
{
    private long scheduledStartMasterUs;
    private uint durationSeconds = 20;
    private uint repeatCount = 1;
    private uint firstRepeat = 1;
    public uint CurrentRepeat { get; private set; } = 1;
    public uint TotalRepeats { get; private set; } = 1;
    public bool IsPaused { get; private set; }

    public TimerState State { get; private set; } = TimerState.Ready;
    public uint RemainingSeconds { get; private set; } = 20;

    public void ArmAt(uint duration, long startAtMasterMicroseconds)
    {
        ArmSegmentAt(duration, 1, startAtMasterMicroseconds, 1, 1);
    }

    public void ArmSegmentAt(uint duration, uint repeats, long target, uint first, uint total)
    {
        IsPaused = false;
        durationSeconds = duration;
        repeatCount = repeats;
        firstRepeat = first;
        CurrentRepeat = first;
        TotalRepeats = total;
        RemainingSeconds = duration;
        scheduledStartMasterUs = target;
        State = TimerState.Armed;
    }

    public void Reset(uint duration)
    {
        IsPaused = false;
        durationSeconds = duration;
        repeatCount = firstRepeat = CurrentRepeat = TotalRepeats = 1;
        RemainingSeconds = duration;
        scheduledStartMasterUs = 0;
        State = TimerState.Ready;
    }

    public void Pause(uint remaining, uint currentRepeat, uint totalRepeats)
    {
        IsPaused = true;
        RemainingSeconds = remaining;
        CurrentRepeat = currentRepeat;
        TotalRepeats = totalRepeats;
        scheduledStartMasterUs = 0;
        State = TimerState.Ready;
    }

    public void Update() => UpdateAt(MasterClock.NowMicroseconds);

    public void UpdateAt(long now)
    {
        if (State is not (TimerState.Armed or TimerState.Running)) return;

        if (now < scheduledStartMasterUs)
        {
            State = TimerState.Armed;
            RemainingSeconds = durationSeconds;
            return;
        }

        ulong elapsedSeconds = (ulong)((now - scheduledStartMasterUs) / 1_000_000L);
        CurrentRepeat = firstRepeat + (uint)Math.Min(elapsedSeconds / durationSeconds, repeatCount - 1);
        if (elapsedSeconds >= (ulong)durationSeconds * repeatCount)
        {
            RemainingSeconds = 0;
            State = TimerState.Finished;
        }
        else
        {
            RemainingSeconds = durationSeconds - (uint)(elapsedSeconds % durationSeconds);
            State = TimerState.Running;
        }
    }
}
