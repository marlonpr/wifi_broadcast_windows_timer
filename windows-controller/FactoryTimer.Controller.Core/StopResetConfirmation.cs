using FactoryTimer.Protocol;

namespace FactoryTimer.Controller.Core;

public static class StopResetConfirmation
{
    // The same predicate drives acceptance and its operator-visible reason.
    public static string? Failure(string id, ulong resetId, ProgramDeviceObservation? observation)
    {
        if (observation is not { } x) return "No fresh matching STATUS / EXEC_STATUS reply; check the timer connection.";
        if (x.Execution.DeviceId != id || x.Health.DeviceId != id) return "STATUS / EXEC_STATUS device identity does not match.";
        if (x.Execution.ResponseId == 0) return "EXEC_STATUS did not answer the current confirmation request.";
        if (x.Execution.BootId == 0) return "Timer boot identity is missing.";
        if (x.Execution.CommandId != resetId || x.Health.CommandId != resetId)
            return $"RESET command {resetId:X16} is not confirmed (EXEC={x.Execution.CommandId:X16}, STATUS={x.Health.CommandId:X16}).";
        if (x.Execution.State != TimerState.Ready || x.Health.State != TimerState.Ready)
            return $"Timer is not READY (EXEC={x.Execution.State.ToString().ToUpperInvariant()}, STATUS={x.Health.State.ToString().ToUpperInvariant()}).";
        if (x.Execution.PlanId != 0 || x.Execution.SegmentIndex != 0 ||
            x.Execution.ElapsedSegmentSeconds != 0 || x.Execution.ElapsedSegmentMicroseconds != 0)
            return "Timer still reports execution progress from a previous countdown.";
        if (string.IsNullOrWhiteSpace(x.Health.FirmwareBuildId)) return "STATUS is missing the firmware build identity.";
        return null;
    }
}
