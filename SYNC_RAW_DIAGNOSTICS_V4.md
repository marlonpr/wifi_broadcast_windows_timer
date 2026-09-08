# Raw synchronization diagnostics v4

> Historical note: v5 extends this CSV with weighted-estimator identification and explicit partial-attempt/error rows. See `SYNC_PARTIAL_DIAGNOSTICS_V5.md`.

## Purpose

The current five-device benchmark occasionally sees synchronization-quality failures even with strong RSSI, five attempts, a timing-quiet window, and the best-3 low-RTT median estimator. The summary CSV only exposes the final consensus values, so it cannot show whether a failure came from one isolated packet, several correlated packets, or a persistent directional delay burst.

This revision adds raw per-sample diagnostics without changing the synchronization algorithm.

## Algorithm remains unchanged

- Calibration: 8 exchanges.
- Keep the 3 lowest-RTT valid samples.
- Apply the median offset of those 3.
- Verification: 8 delay-free exchanges.
- Keep the 3 lowest-RTT valid samples.
- Use the median offset of those 3.
- Quality threshold: ±3.000 ms from the expected experiment bias.
- Maximum synchronization attempts per device: 5.
- Quiet delay before a quality retry: 150 ms.
- Periodic STATUS discovery remains paused/drained during the timing-critical sequence.

## New output

Each automatic benchmark run writes:

```text
Documents\FactoryTimerBenchmarks\
    factory_timer_5_device_benchmark_YYYYMMDD_HHMMSS.csv
    factory_timer_5_device_sync_samples_YYYYMMDD_HHMMSS.csv
```

The timestamp suffix is identical, so the two files can be paired unambiguously.

## Raw CSV schema

Each row represents one completed `SYNC_REQUEST`/`SYNC_REPLY` exchange.

Core identity fields:

- `Trial`
- `TimestampUtc`
- `SyncMode`
- `Device`
- `Hardware`
- `IpAddress`
- `Attempt`
- `Phase` (`CALIBRATION` or `VERIFICATION`)
- `SampleIndex`
- `SyncId`

Path-injection fields:

- `PathForwardDelayMs`
- `PathReverseDelayMs`

Raw NTP-style timestamps:

- `MasterT1Us`
- `DeviceT2Us`
- `DeviceT3Us`
- `MasterT4Us`

Derived sample values:

- `RttUs`
- `OffsetUs`

Estimator-selection fields:

- `SelectedLowRtt`
- `ConsensusRepresentative`
- `ConsensusOffsetUs`
- `BestRttUs`

Attempt outcome fields:

- `ResidualErrorUs`
- `ExpectedBiasUs`
- `DeviationUs`
- `ThresholdUs`
- `AttemptAccepted`

The attempt outcome is repeated on all 16 rows for that attempt so filtering any selected sample still retains the final quality result.

## How to diagnose an outlier

For a failed `(Trial, Device, Attempt)` group:

1. Filter the raw file to that trial/device/attempt.
2. Separate `CALIBRATION` and `VERIFICATION`.
3. Plot or sort `RttUs` against `OffsetUs`.
4. Inspect the three rows where `SelectedLowRtt=1`.
5. Identify the row where `ConsensusRepresentative=1`.
6. Compare `ConsensusOffsetUs` between calibration and verification. Their difference is `ResidualErrorUs`.

Typical patterns:

- One extreme offset but `SelectedLowRtt=0`: estimator correctly rejected an isolated slow/outlier packet.
- One extreme offset with `SelectedLowRtt=1`, while the other two selected offsets agree: median should suppress it.
- All three selected offsets shifted together: the network condition persisted across several low-RTT packets; median-of-three cannot remove it.
- Calibration selected offsets clustered but verification selected offsets shifted: path conditions changed after `SYNC_SET`.
- Both phases show wide/bimodal offsets despite low RTT: strong evidence of short-term path asymmetry/queueing rather than local scheduler error.

## What to upload for analysis

After the next 30-trial NONE-mode benchmark, provide both generated files:

```text
factory_timer_5_device_benchmark_....csv
factory_timer_5_device_sync_samples_....csv
```

The second file is the important new diagnostic artifact.
