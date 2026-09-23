# Manual START_AT proof — safety/result fixes

This update is based on `wifi_broadcast_timer_sync_on_start_v1_1_manual_startat.zip`.

## 1. Closing the app cannot silently arm a partial manual run

While manual START_AT preparation is running or a prepared manual session is live, the WinUI `AppWindow.Closing` event is cancelled.

If the session is already prepared, the operator is told to use **CANCEL / RESET** first. That path now uses the same confirmed-abort mechanism as production: one frozen RESET CommandId, RESET ACK tracking, and a fresh `READY` STATUS check for every frozen participant before cancellation is called confirmed. If proof is incomplete by `T* - 1 s`, the UI reports the cancellation as unconfirmed. Only after the manual activity is gone can the window close and dispose the UDP service.

This intentionally uses the "refuse to close while live" policy rather than trying to fire-and-forget RESET from `Dispose()` after the window has already closed.

## 2. Manual proof PASS is no longer unconditional

A completed manual proof reports PASS only when both established timing limits are satisfied:

- worst absolute reported START error <= 3.000 ms
- frozen-set reported START spread <= 20.000 ms

Otherwise the result is FAIL and prints the measured values.

The 3 ms threshold is the same synchronization-quality magnitude gate already used by the controller. The 20 ms threshold is `StartReadinessGate.FleetPairBudgetMicroseconds`.

## 3. Manual 3-device telemetry no longer leaks into the all-five main result

While the manual session is active, STARTED packets for that manual CommandId update the main synchronization result as `n/3` for the frozen participant set rather than invoking the legacy all-five `RefreshStartSynchronizationResult()` path.

At completion the main result reports the manual 3-device worst absolute error, 3-device spread, and PASS/FAIL. Late duplicate STARTED telemetry for the same manual CommandId is ignored by the legacy all-five summary so it cannot overwrite the final manual result with `3/5`.

## Firmware/protocol impact

None. This is Windows-controller-only diagnostic/safety work. The normal production START orchestration is not modified by these changes.
