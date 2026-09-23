# Validation checklist - manual START_AT evidence v4

## Static checks performed in packaging environment

- Old manual PASS logic based on `|start error| <= 3 ms` was removed.
- Old manual PASS logic based on `fleet start spread <= 20 ms` was removed.
- Manual PASS now references only frozen-command STARTED completion, `max |scheduler lateness| <= 1 us`, and minimum manual send gap `>= 1 s`.
- Start error and fleet start-error spread remain displayed as informational sync-level quantities.
- Generic STARTED aggregation now requires a supplied CommandId and supplied participant list.
- Production START stores the successful frozen CommandId + frozen participant list and the packet handler refreshes results only for that context.
- Normal RESET clears that production telemetry context.
- Benchmark aggregation is also CommandId-scoped.
- No old `Waiting for STARTED telemetry from all five devices` / hard-coded `5-device start spread` result string remains in `MainViewModel.cs`.
- Close message distinguishes active PREPARE/send from an idle live manual session.

## Windows checks still required

This packaging environment does not have the .NET / Windows App SDK toolchain. On the Windows development PC run:

```powershell
dotnet test tests\FactoryTimer.Protocol.Tests\FactoryTimer.Protocol.Tests.csproj -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests\FactoryTimer.Controller.Core.Tests.csproj -c Release
dotnet build FactoryTimer.slnx -c Release
```

## Hardware/UI checks

### Manual proof

1. Select exactly ESP01/ESP02/ESP03.
2. PREPARE MANUAL TEST.
3. Send the three START_AT commands manually with at least 1 s between adjacent clicks.
4. Confirm all three have the same frozen CommandId and T*.
5. Confirm PASS only when all three STARTED messages are for that CommandId and worst absolute scheduler lateness is <= 1 us.
6. Confirm the display separately reports sync residual/start error/spread but does not use them as PASS criteria.
7. Repeat once with two sends less than 1 s apart; the proof should FAIL on send separation even if scheduler lateness is 0 us.

### Production aggregation regression

1. Select ESP01/ESP02/ESP03 and run normal production START to completion. Confirm `3/3`.
2. Without restarting the controller, deselect one device (for example ESP02) and run production START with ESP01/ESP03 only.
3. Confirm STARTED progress/final result for the second run is `0/2`, `1/2`, `2/2`.
4. Confirm the final spread uses only ESP01/ESP03 and the second run's CommandId.
5. ESP02's STARTED values from the first run must remain irrelevant even though they are stale and still present in its device view model.

### Close path

1. During PREPARE, attempt X / Alt+F4. The close should be blocked and the message should tell the operator to wait.
2. After PREPARE completes, send one START_AT, then attempt close while idle. The close should be blocked and the message should tell the operator to use CANCEL / RESET.
3. Press CANCEL / RESET, verify frozen participants receive RESET, then close normally.

## Evidence-v4 additions

- Each manual STARTED row must satisfy `start error = scheduler lateness - sync residual` exactly; the displayed identity delta must be `0 us` for all three rows.
- Interpret this only as an applied-offset consistency check: verification offset cancels; zero means the firmware start used the controller-recorded `SYNC_APPLIED` offset and does not validate verification accuracy.
- Manual proof PASS also requires that applied-offset consistency identity on all three rows.
- Send records are explicitly sorted by `SentMasterMicroseconds` before adjacent gaps are calculated; the result prints the chronological send order.
- Positive separation test should be out of DeviceId order.
- Negative separation control: ESP01 and ESP03 less than 1 s apart, ESP02 well apart. It must FAIL separation.
- Abort path: send to only two devices and let the cutoff pass; verify frozen-set RESET, no STARTED telemetry for the abandoned CommandId, and directly watch that the two armed countdown displays do not start at T*.
- Production regression with current hardware: run 3-device production START, then 2-device production START in the same controller session. The second run must report `2/2` and compute its spread from only the two current participants; the unselected third device intentionally supplies stale telemetry from the prior run.
