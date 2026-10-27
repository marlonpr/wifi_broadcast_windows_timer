using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller;

internal sealed partial class MainViewModel
{
    private bool completionResetInProgress;

    private async Task ResetCompletedProgramAsync()
    {
        if (completionResetInProgress || operatorStopInProgress ||
            productionProgram?.State.Stage != ProgramStage.Completed) return;

        completionResetInProgress = true;
        operatorStopInProgress = true;
        // Completion belongs to the executed roster, even if the checkboxes
        // were edited during its final segment. Never reset unrelated timers.
        operatorActionParticipants = productionProgram.State.Program.Participants.ToHashSet(StringComparer.Ordinal);
        UpdateCanSend();
        try
        {
            AppendCountdownLog("Program complete. Automatic RESET for " +
                CountdownDeviceNames(productionProgram.State.Program.Participants) + ".");
            // Save the RESET identity and pending roster before sending. A lost
            // reply or controller restart cannot resurrect finished progress.
            Task postRunTrace = controllerTxTraceCommandId == productionProgram.State.CurrentCommandId
                ? controllerTxTraceCompletion : Task.CompletedTask;
            productionProgram.ResetProgress(CreateCommandId());
            // FINISHED health, RUN_DIAG and CSV collection must finish before
            // RESET changes the devices' command identities and run counters.
            await postRunTrace;
            productionSilentRunCancellation?.Cancel();
            await productionSilentRunTask;
            ResumeAutomaticStatusDiscoveryAfterRun();
            productionStartTelemetryContext = null;
            ClearDegradedStartPending();
            clock.Reset(productionProgram.State.Program.DurationSeconds);
            RefreshClockProperties();
            UpdateProductionProgramText();
            await ConfirmOperatorStopAsync();
            StatusMessage = productionProgram.State.StopCheckpoint!.PendingDeviceIds.Count == 0
                ? "Program complete. RESET confirmed; START begins a new countdown."
                : "Program complete. Automatic RESET is waiting for stop confirmation: " +
                    CountdownDeviceNames(productionProgram.State.StopCheckpoint.PendingDeviceIds) + ".";
        }
        catch (Exception exception)
        {
            StatusMessage = "Program complete. Automatic RESET needs attention: " + exception.Message +
                ". Press RESET to retry.";
        }
        finally
        {
            operatorActionParticipants = null;
            operatorStopInProgress = false;
            completionResetInProgress = false;
            UpdateProductionProgramText();
            UpdateCanSend();
        }
    }
}
