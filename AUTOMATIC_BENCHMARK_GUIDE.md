# Automatic five-device benchmark

## Purpose

This mode measures synchronization as an independent random/network event on every trial. It is intended to establish a statistically meaningful 5-device baseline before scaling to 10 and 15 timers.

## Recommended first run

1. Power ESP01 through ESP05 and wait until every card is ONLINE.
2. Select **NONE — 0 / 0 ms**.
3. Leave timer duration at 20 seconds. The benchmark does not wait 20 seconds between trials; it resets shortly after STARTED telemetry is captured.
4. Set **Trials** to `10`.
5. Press **RUN BENCHMARK**.
6. Do not change Wi-Fi interface or SYNC path-delay mode while the benchmark is running.
7. When complete, note the summary and open the CSV path shown in the UI.

## One trial

Each trial executes:

```text
fresh SYNC of ESP01 (8 calibration + 4 verification)
fresh SYNC of ESP02 (8 calibration + 4 verification)
fresh SYNC of ESP03 (8 calibration + 4 verification)
fresh SYNC of ESP04 (8 calibration + 4 verification)
fresh SYNC of ESP05 (8 calibration + 4 verification)
        ↓
one common absolute START_AT target
        ↓
wait for STARTED from ESP01..ESP05
        ↓
record all five measurements
        ↓
RESET
        ↓
next trial
```

## CSV format

The file is long-form: five rows per trial, one row per device. Important columns:

- `BestSyncRttUs`
- `VerifyRttUs`
- `AppliedOffsetUs`
- `VerificationOffsetUs`
- `ClockErrorUs`
- `VerifiedStartMasterUs`
- `StartErrorUs`
- `SchedulerLatenessUs`
- `FleetSyncDurationUs`
- `WorstClockErrorUs`
- `FleetClockSpreadUs`
- `WorstStartErrorUs`
- `FleetStartSpreadUs`
- `Success` / `Failure`

`SchedulerLatenessUs` is reconstructed from the firmware STARTED packet as `EstimatedMasterStart - TargetMasterStart`. It measures the local START scheduler using the same applied offset that armed the deadline; `StartErrorUs` instead uses the independent delay-free verification offset and therefore measures the estimated physical synchronization error.

## Interpretation

For the 5-device NONE-mode baseline, the main fleet metric is `FleetStartSpreadUs`. Compare mean, P95, and maximum across independent trials. `FleetSyncDurationUs` is also important because sequential synchronization time grows with fleet size even though START_AT broadcast delivery does not.

A failed trial is retained in the CSV rather than discarded. This is intentional: packet loss, a missing device, or a timeout is part of the scaling behavior being measured.
