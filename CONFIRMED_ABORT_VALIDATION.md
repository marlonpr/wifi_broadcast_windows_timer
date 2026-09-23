# Confirmed abort validation

## Implemented behavior

- Final STATUS barrier begins at `T* - 3.5 s`.
- Normal ARM preparation must complete by `T* - 2.5 s`.
- A successful run keeps the existing session guard through `T* - 2.0 s`.
- Any ARM/final-barrier failure enters confirmed cancellation.
- Confirmed cancellation uses one frozen RESET CommandId for the full frozen set.
- RESET is retried only for devices missing a matching RESET ACK.
- If RESET ACK is present but STATUS is missing, STATUS_REQUEST is retried independently.
- Safe cancellation requires both:
  - matching RESET `ACCEPTED`/`DUPLICATE` ACK; and
  - a STATUS observed after cancellation began that reports `READY` with either the RESET CommandId or boot-state CommandId `0`.
- Cancellation attempts last at most 1.5 s and never extend past `T* - 1.0 s`.
- Missing proof is reported as `AbortUnconfirmed`; the controller no longer reports a one-way RESET send as a safe abort.
- The same confirmed cancellation helper is used by manual START_AT CANCEL and the manual partial-send cutoff.

## Static validation performed here

- PASS — WinUI XAML parses as XML.
- PASS — C# delimiter balance for modified Core, ViewModel, and test source.
- PASS — `git diff --check` equivalent reports no whitespace errors.
- PASS — source contains CommandId/type/participant-scoped RESET ACK tracking.
- PASS — source contains fresh `READY` STATUS confirmation and rejects the abandoned START_AT CommandId.
- PASS — source contains explicit `AbortUnconfirmed` operator state.
- PASS — source no longer contains `BestEffortResetPreparedParticipantsAsync`.
- PASS — tests were added for the new timeline, RESET ACK tracker, and safe STATUS rules.

## Firmware validation

The ESP32/ESP32-S3 v6.2 firmware is unchanged. Its existing host suite was executed in this environment after this controller change:

- `factory_timer_core_debug_0`: PASS
- `factory_timer_core_debug_1`: PASS
- total: 2/2 passed

## Windows build limitation

The .NET SDK is not installed in this execution environment, so the modified MSTest suite and WinUI build were not executed here. On the Windows development PC, close any running `FactoryTimer.Controller.exe` first, then run:

```powershell
dotnet test tests\FactoryTimer.Protocol.Tests -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests -c Release
dotnet build FactoryTimer.slnx -c Release
```

## Hardware acceptance for the bug just observed

1. Select the normal production participants.
2. Cause one participant to miss the final STATUS while at least one other device has already ACKed START_AT.
3. Expected UI result is either:
   - `ABORT CONFIRMED` — all frozen participants produced RESET ACK + fresh `READY` STATUS, and no panel may start at the abandoned T*; or
   - `AbortUnconfirmed` — the controller explicitly warns that one or more devices may still start.
4. A plain `StatusStale` followed by an implied-success RESET is no longer an acceptable result.
