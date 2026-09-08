using FactoryTimer.Controller.Core;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller;

internal sealed class ControllerClock
{
    private long scheduledStartMasterUs;
    private uint durationSeconds = 20;

    public TimerState State { get; private set; } = TimerState.Ready;
    public uint RemainingSeconds { get; private set; } = 20;

    public void ArmAt(uint duration, long startAtMasterMicroseconds)
    {
        durationSeconds = duration;
        RemainingSeconds = duration;
        scheduledStartMasterUs = startAtMasterMicroseconds;
        State = TimerState.Armed;
    }

    public void Reset(uint duration)
    {
        durationSeconds = duration;
        RemainingSeconds = duration;
        scheduledStartMasterUs = 0;
        State = TimerState.Ready;
    }

    public void Update()
    {
        if (State is not (TimerState.Armed or TimerState.Running)) return;

        long now = MasterClock.NowMicroseconds;
        if (now < scheduledStartMasterUs)
        {
            State = TimerState.Armed;
            RemainingSeconds = durationSeconds;
            return;
        }

        ulong elapsedSeconds = (ulong)((now - scheduledStartMasterUs) / 1_000_000L);
        if (elapsedSeconds >= durationSeconds)
        {
            RemainingSeconds = 0;
            State = TimerState.Finished;
        }
        else
        {
            RemainingSeconds = durationSeconds - (uint)elapsedSeconds;
            State = TimerState.Running;
        }
    }
}
