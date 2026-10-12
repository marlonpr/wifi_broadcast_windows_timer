using System.Globalization;
using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

// STARTED's raw local-clock projection and STATUS's disciplined start metrics
// have different meanings. This display-only collector is never a START gate.
public sealed class StartTimingDiagnostics
{
    private ulong? reportedCommandId;
    private ulong? projectedCommandId;
    private long? startError;
    private long? schedulerLateness;
    private long? localClockPhase;

    public void Clear()
    {
        reportedCommandId = projectedCommandId = null;
        startError = schedulerLateness = localClockPhase = null;
    }

    public void Observe(StartedPacket started, long? verificationOffsetMicroseconds)
    {
        projectedCommandId = started.CommandId;
        long projectedMaster = verificationOffsetMicroseconds.HasValue
            ? started.LocalStartMicroseconds + verificationOffsetMicroseconds.Value
            : started.EstimatedMasterStartMicroseconds;
        localClockPhase = projectedMaster - started.TargetMasterStartMicroseconds;
    }

    public void Observe(StatusPacket status)
    {
        if (status.CommandId == 0 || status.State is not (TimerState.Running or TimerState.Finished)) return;
        reportedCommandId = status.CommandId;
        startError = status.StartErrorMicroseconds;
        schedulerLateness = status.SchedulerLatenessMicroseconds;
    }

    public long? StartErrorUs(ulong commandId) => reportedCommandId == commandId ? startError : null;
    public long? SchedulerLatenessUs(ulong commandId) => reportedCommandId == commandId ? schedulerLateness : null;
    public long? UncorrectedLocalClockPhaseAtTStarUs(ulong commandId) =>
        projectedCommandId == commandId ? localClockPhase : null;

    public string Display(ulong commandId)
    {
        static string Number(long? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "PENDING";
        return $"StartErrorUs={Number(StartErrorUs(commandId))}; SchedulerLatenessUs={Number(SchedulerLatenessUs(commandId))}";
    }
}
