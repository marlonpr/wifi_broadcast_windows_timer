# 5-device synchronization quality retry

This revision adds an automatic verification-quality gate before a device is accepted as synchronized.

## Policy

For each device, one synchronization attempt consists of:

1. Run 8 calibration exchanges.
2. Retain the 3 lowest-RTT valid calibration samples and use a 1/RTT² weighted Master-minus-local offset over those samples.
3. Apply that consensus offset with `SYNC_SET`; the reported `BestSyncRttUs` remains the minimum RTT among the retained samples.
4. Run 8 delay-free verification exchanges.
5. Retain the 3 lowest-RTT valid verification samples and use a 1/RTT² weighted offset over those samples.
6. Compare the applied calibration consensus offset against the verification consensus offset.

The normal NONE-mode quality condition is:

```text
abs(applied_offset - verification_offset) <= 3000 us
```

If it fails, only that device is recalibrated. There are at most 5 total attempts per device (4 retries). After a rejected verification, or after an eligible transient SYNC/SYNC_SET transport failure, the controller leaves a 150 ms quiet interval before recalibrating that device. A benchmark trial does not issue START_AT unless all five devices pass. Cancellation, protocol errors, explicit network-down conditions, and invalid configuration remain fail-fast.

The path-delay experiment remains usable. The gate compares the residual against the experiment's expected bias:

```text
NONE 0/0 ms:             expected bias =       0 us
SYMMETRIC 250/250 ms:    expected bias =       0 us
ASYMMETRIC 250/0 ms:     expected bias = -125000 us
```

So ASYMMETRIC mode is intentionally allowed to retain its ~-125 ms calibration bias for the experiment instead of being "corrected" by the retry system.

## Timing-quiet discovery behavior

Periodic `STATUS_REQUEST` discovery is intentionally suspended during the timing-critical sequence. The controller cancels the discovery loop, waits for the active loop iteration to finish, and then waits an additional 100 ms drain interval before the first SYNC sample. This prevents a five-device STATUS reply burst from overlapping calibration/verification traffic.

For an automatic benchmark trial, discovery remains paused through SYNC, `START_AT`, `STARTED` telemetry collection, and RESET. It is then restarted before the inter-trial pause, so fresh status/RSSI information can arrive without contaminating the next trial. Device ONLINE/OFFLINE aging is frozen while the deliberate quiet period is active.

## CSV additions

The automatic benchmark exports:

- `SyncAttempts`
- `SyncRetries`
- `InitialClockErrorUs`
- `ExpectedSyncBiasUs`
- `SyncQualityDeviationUs`
- `SyncQualityThresholdUs`
- `SyncQualityAccepted`
- `RssiDbm`
- `WifiChannel`
- `Bssid`

`ClockErrorUs` is the final verification residual from the accepted attempt. The initial value is retained separately so retry improvements remain visible.

## Firmware diagnostics

The matching firmware extends STATUS to:

```text
FCT2|STATUS|<device>|<command>|<state>|<remaining>|<rssi>|<channel>|<bssid>
```

The values are read using `esp_wifi_sta_get_ap_info()`. The same code path is used for classic ESP32 and ESP32-S3.

The Windows controller remains backward-compatible with legacy FCT1 STATUS, but RSSI/channel/BSSID are blank unless the new firmware is flashed.

## Recommended test

Use `NONE — 0 / 0 ms`, 30 trials, and run the automatic benchmark again. Compare against the previous baseline:

- fleet START spread distribution,
- worst absolute START error,
- total quality retries,
- any quality-failed trials,
- fleet synchronization duration,
- and whether poor runs correlate with RSSI, channel, or BSSID.
