# Start-instant gate validation

This revision changes only the Windows controller/core readiness semantics. The v6.2 ESP32/ESP32-S3 firmware is unchanged.

## Static checks performed in this environment

- PASS — readiness gate API has no countdown-duration parameter.
- PASS — `DurationExceeded` is removed from the controller/core production START path.
- PASS — exact `20,000 us` start-pair uncertainty passes; only `>20,000 us` blocks.
- PASS — production and manual START_AT preparation call the readiness gate using participant state, current time, and `T*` only.
- PASS — UI range remains `00:01..99:59` and controller validation remains capped at 5,999 total seconds.
- PASS — host test source now includes exact-20-ms pass, >20-ms reject, and duration-independence cases.
- PASS — WinUI XAML parses successfully as XML.

## Build/test limitation

The .NET SDK is not installed in this execution environment, so MSTest and the WinUI build were not executed here. Run `dotnet test` and `dotnet build` on the normal Windows development PC before treating this as a release build.

## Hardware acceptance

- Set a duration longer than the previous reported Dmax, for example `10:00`.
- Press START with two or more synchronized participants.
- The controller must no longer show `DurationExceeded` or `Maximum countdown`.
- The readiness gate may still block as `TimingBudgetExceeded` if the fleet's predicted pair uncertainty at `T*` exceeds 20 ms.
- When the start gate passes, all selected devices should arm for the same `T*` and then run the requested long countdown locally.

## Confirmed-abort revision

Static source checks also cover:

- final STATUS starts at `T* - 3.5 s`;
- normal ARM preparation closes at `T* - 2.5 s`;
- successful-run session guard remains through `T* - 2.0 s`;
- cancellation confirmation is capped at 1.5 s and never extends past `T* - 1.0 s`;
- RESET ACK tracking is CommandId-, command-type-, and participant-scoped;
- cancellation STATUS must be fresh and `READY`, with the RESET CommandId (or `0` after reboot);
- unconfirmed cancellation reports `AbortUnconfirmed` instead of claiming that RESET succeeded.

The .NET SDK is still unavailable in this environment, so these tests must be executed on the Windows development PC.
