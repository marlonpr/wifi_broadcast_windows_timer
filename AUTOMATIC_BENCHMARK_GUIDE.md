# Automatic five-device benchmark

## Purpose

This mode measures synchronization as an independent random/network event on every trial. It is intended to establish a statistically meaningful 5-device baseline before scaling to 10 and 15 timers.

## Recommended first run

1. Power ESP01 through ESP05 and wait until every card is ONLINE.
2. Select **NONE — 0 / 0 ms**.
3. Leave timer duration at 20 seconds. The benchmark does not wait 20 seconds between trials; it resets shortly after STARTED telemetry is captured.
4. Set **Trials** to `30` for the current five-device validation.
5. Press **RUN BENCHMARK**.
6. Do not change Wi-Fi interface or SYNC path-delay mode while the benchmark is running.
7. When complete, note the summary and both CSV paths shown in the UI.

## One trial

Each trial executes:

```text
pause periodic STATUS_REQUEST and drain 100 ms
        ↓
fresh SYNC of ESP01 (8 calibration + 8 verification, best-3 low-RTT 1/RTT² weighted offset, max 5 attempts)
fresh SYNC of ESP02 (8 calibration + 8 verification, best-3 low-RTT 1/RTT² weighted offset, max 5 attempts)
fresh SYNC of ESP03 (8 calibration + 8 verification, best-3 low-RTT 1/RTT² weighted offset, max 5 attempts)
fresh SYNC of ESP04 (8 calibration + 8 verification, best-3 low-RTT 1/RTT² weighted offset, max 5 attempts)
fresh SYNC of ESP05 (8 calibration + 8 verification, best-3 low-RTT 1/RTT² weighted offset, max 5 attempts)
        ↓
one common absolute START_AT target
        ↓
wait for STARTED from ESP01..ESP05
        ↓
record all five measurements
        ↓
RESET
        ↓
resume STATUS_REQUEST discovery
        ↓
next trial
```

## CSV outputs

Every benchmark run now writes two CSV files under `Documents\FactoryTimerBenchmarks` with the same timestamp.

The **summary CSV** is long-form: five rows per trial, one row per device. Important columns:

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


The **raw SYNC sample CSV** is named `factory_timer_5_device_sync_samples_YYYYMMDD_HHMMSS.csv`. It contains one row for each completed timestamp exchange. A normal first-attempt device produces 16 rows: 8 `CALIBRATION` rows and 8 `VERIFICATION` rows. Retries add another 16 rows per completed attempt.

Important raw columns:

- `Attempt`
- `Phase`
- `SampleIndex`
- `SyncId`
- `PathForwardDelayMs` / `PathReverseDelayMs`
- `MasterT1Us`, `DeviceT2Us`, `DeviceT3Us`, `MasterT4Us`
- `RttUs`
- `OffsetUs`
- `SelectedLowRtt`
- `ConsensusRepresentative`
- `Estimator`
- `ConsensusOffsetUs`
- `BestRttUs`
- `ResidualErrorUs`
- `ExpectedBiasUs`
- `DeviationUs`
- `ThresholdUs`
- `AttemptAccepted`
- `AttemptOutcome`
- `FailureKind`
- `FailureMessage`

`SelectedLowRtt=1` identifies the three samples retained by the estimator. `ConsensusRepresentative=1` identifies the minimum-RTT selected sample whose real `SyncId` is used as the representative reference. The applied/verification offset is the inverse-RTT² weighted value in `ConsensusOffsetUs`, not that representative sample's individual offset. Because the raw file records all four timestamps and the `SyncId`, a suspicious exchange can be matched to ESP serial logs and recomputed independently.

If an exchange, estimator step, or `SYNC_SET` fails before quality evaluation, v5 preserves all successful samples already collected for that attempt and appends a failure-marker row identifying the active phase/sample, failure classification, and exception message. Raw timestamp fields on the marker are blank because that operation did not produce a valid completed exchange.

## Interpretation

For the 5-device NONE-mode baseline, the main fleet metric is `FleetStartSpreadUs`. Compare mean, P95, and maximum across independent trials. `FleetSyncDurationUs` is also important because sequential synchronization time grows with fleet size even though START_AT broadcast delivery does not.

A failed trial is retained in the CSV rather than discarded. This is intentional: packet loss, a missing device, or a timeout is part of the scaling behavior being measured.

## Synchronization quality retry

This revision adds a verification gate before each device is considered synchronized. Calibration and verification each collect 8 exchanges, retain the 3 lowest-RTT valid samples, and use the 1/RTT² weighted offset of those 3. The controller accepts a device only when the verification residual is within ±3.000 ms of the expected path-delay experiment bias. A failing device is automatically recalibrated, up to 5 total attempts. Rejected attempts are separated by a 150 ms quiet interval.

The CSV now includes `SyncAttempts`, `SyncRetries`, `InitialClockErrorUs`, `ExpectedSyncBiasUs`, `SyncQualityDeviationUs`, `SyncQualityThresholdUs`, and `SyncQualityAccepted` so retries are visible rather than hidden.

## Timing-quiet discovery

The benchmark stops the 2-second periodic `STATUS_REQUEST` loop before each synchronization sequence and waits for it to exit. It then allows a 100 ms drain interval before the first calibration packet. Discovery is restarted after STARTED telemetry is captured and RESET has been sent. This keeps background status bursts out of the synchronization window while preserving normal device discovery between trials.

## Wi-Fi diagnostics

With the matching updated firmware, every STATUS packet also reports RSSI, Wi-Fi channel, and BSSID. The controller displays these values on each device card and exports `RssiDbm`, `WifiChannel`, and `Bssid` in the benchmark CSV. Legacy FCT1 STATUS remains accepted, but these three columns remain blank when legacy firmware is used.
