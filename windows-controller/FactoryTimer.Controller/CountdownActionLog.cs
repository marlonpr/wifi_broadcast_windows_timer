using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller;

internal sealed partial class MainViewModel
{
    private const int CountdownLogCapacity = 60;
    private readonly Queue<string> countdownLogEntries = new();
    private string? lastCountdownLogMessage;
    private string countdownActionLog = "No countdown actions yet.";
    public string CountdownActionLog { get => countdownActionLog; private set => SetProperty(ref countdownActionLog, value); }

    public string CountdownStartAvailabilityText
    {
        get
        {
            if (productionProgramLoadError is not null) return "START blocked: saved recovery error. Press RESET.";
            if (completionResetInProgress) return "Program complete; waiting for automatic RESET confirmation.";
            if (programManagementBusy) return "Waiting for the current START / STOP / RESET operation.";
            if (IsBenchmarkRunning || manualStartAtSession is not null || isPreparingManualStartAtTest)
                return "START blocked: an engineering operation is active.";
            if (isSending) return "START blocked: controller command in progress.";
            if (SelectedNetworkInterface is null || !udpService.IsReady)
                return "START blocked: select a connected network interface.";
            string[] selected = Devices.Where(d => d.IsSelected).Select(d => d.DeviceId).ToArray();
            if (selected.Length == 0) return "START blocked: select at least one timer.";
            if (productionProgram?.State.StopCheckpoint is not null &&
                !productionProgram.State.Program.Participants.SequenceEqual(selected))
                return "START will apply the current selection and confirm only those timers.";
            if (productionProgram?.State.StopCheckpoint is { PendingDeviceIds.Count: > 0 } checkpoint)
                return "START is waiting for stop confirmation: " + CountdownDeviceNames(checkpoint.PendingDeviceIds) + ".";
            if (productionProgram?.State.Active == true && !programTask.IsCompleted) return "Countdown program is active.";
            return "START available; timer readiness and synchronization will be checked.";
        }
    }

    private string CountdownDeviceNames(IEnumerable<string> ids) => string.Join(", ",
        ids.Select(id => $"{OperatorDeviceName(id)} ({id})"));

    private void AppendCountdownLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == lastCountdownLogMessage) return;
        lastCountdownLogMessage = message;
        countdownLogEntries.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
        while (countdownLogEntries.Count > CountdownLogCapacity) countdownLogEntries.Dequeue();
        // Newest first so failures remain visible without automatic scrolling.
        CountdownActionLog = string.Join(Environment.NewLine + Environment.NewLine, countdownLogEntries.Reverse());
    }

    private void LogStopConfirmationFailures(StoppedCountdownCheckpoint checkpoint,
        IReadOnlyDictionary<string, ProgramDeviceObservation> observations,
        IReadOnlyDictionary<string, System.Net.IPAddress>? sentEndpoints = null)
    {
        foreach (string id in checkpoint.PendingDeviceIds)
        {
            string reason = StopResetConfirmation.Failure(id, checkpoint.ResetCommandId, observations.GetValueOrDefault(id))
                ?? "Stop confirmation is still pending.";
            if (sentEndpoints is not null && !sentEndpoints.ContainsKey(id))
                reason = "RESET was not sent: no freshly verified IP address. " + reason;
            AppendCountdownLog($"{CountdownDeviceNames([id])}: waiting for stop — {reason}");
        }
    }
}
