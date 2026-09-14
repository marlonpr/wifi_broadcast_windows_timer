# SYNC path-delay / physical asymmetry control guide (V9)

This build supports controlled calibration-path asymmetry on any one of ESP01...ESP05 while verification and START_AT remain delay-free.

## Fixed metrology configuration

Use:

- `BLOCKING_THREAD_T4`
- `8 + 8 — baseline`
- best-3 inverse-RTT^2 estimator
- normal ±3 ms `SyncQualityDeviationUs` gate
- frozen 1 kOhm analyzer fixture

The UI now exposes both an **Injection target device** and a **Calibration path profile**. Changing either invalidates the previous synchronization.

## Precision and measured intervals

Reverse-path artificial delay is carried in the SYNC packet in microseconds. Firmware captures T3 first, then waits using `esp_timer_get_time()` precision, then replies. The achieved interval is returned to Windows as `ActualReverseDelayUs`.

Forward-path artificial delay is timed against QPC (`Stopwatch.GetTimestamp`). The 1/4/40 ms metrology range does not rely on Windows `Task.Delay`; the achieved interval is recorded as `ActualForwardDelayUs`.

The quality gate uses **actual measured delay**, not the nominal profile. For every selected calibration sample:

`sample expected bias = (ActualReverseDelayUs - ActualForwardDelayUs) / 2`

The attempt's `ExpectedSyncBiasUs` is the same inverse-RTT^2 weighted combination of those biases over the same best-3 samples used by the offset estimator. Therefore timer/scheduler overshoot does not create a false ±3 ms quality rejection.

## Positive controls

Run about 30 trials each:

| Control | Target | Profile | Predicted physical START |
|---|---|---|---|
| A | ESP02 / ESP32 | reverse 0 / 1 ms | about 0.5 ms early |
| B | ESP02 / ESP32 | reverse 0 / 4 ms | about 2.0 ms early |
| C | ESP03 / ESP32-S3 | reverse 0 / 4 ms | about 2.0 ms early |
| D | ESP02 / ESP32 | forward 40 / 0 ms | about 20 ms late |

For a one-device injected control, do not use the target's fleet-centered value to estimate gain because centering attenuates the displacement by 4/5. Use:

`C_i = target analyzer timestamp - mean(other four analyzer timestamps)`

For the small controls compare injected `C_i` with a nearby no-delay baseline (`Delta C_i`).

## Preregistered sign

The codebase sign convention and prior delay decomposition imply:

- reverse-biased path -> applied offset positive -> physical START early;
- forward-biased path -> applied offset negative -> physical START late.

The controls must confirm this independently.

The subsequent 600-trial clean run is a replication test of the hypothesis-generating 30-trial observation: fleet-centered natural `B_i` should correlate **negatively** with contemporaneous fleet-centered attempt-1 P5 RTT floor.

Because silicon family and clean-path RTT floor are collinear in the current five-device fleet, the later loaded-channel within-device experiment remains required for causal attribution.
