# Partial synchronization diagnostics v5

## Purpose

v4 wrote detailed rows only after a complete calibration + SYNC_SET + verification attempt. If an exchange or SYNC_SET timed out mid-attempt, samples already collected during that attempt were lost from the diagnostic CSV. v5 preserves them and writes an explicit failure marker.

## Raw CSV additions

The `factory_timer_5_device_sync_samples_*.csv` schema now adds:

- `Estimator` — currently `LOW_RTT_INV_RTT2_BEST3`.
- `AttemptOutcome` — `ACCEPTED`, `REJECTED_QUALITY`, `ERROR`, or `CANCELLED`.
- `FailureKind` — for example `QUALITY_GATE`, `TIMEOUT`, `TRANSPORT_ERROR`, `PROTOCOL_ERROR`, `INSUFFICIENT_SAMPLES`, `INVALID_OPERATION`, or `CANCELLED`.
- `FailureMessage` — the exception or quality-gate detail.

For complete attempts, all normal sample rows contain the final residual/quality fields.

For an incomplete attempt:

1. Every successful calibration or verification exchange collected before the error is written.
2. Any consensus that had already been computed is retained on the corresponding rows.
3. Residual/deviation fields that could not be computed are left blank.
4. One additional marker row identifies the active failing phase and sample index. Its raw timestamp/RTT/offset fields are blank because no valid completed exchange exists for that operation.
5. When the controller already knows the attempted `SyncId`, the marker row records it.

Possible marker phases include `CALIBRATION`, `CALIBRATION_ESTIMATOR`, `SYNC_SET`, `VERIFICATION`, and `VERIFICATION_ESTIMATOR`.

This makes transport failures distinguishable from quality-gate failures without changing the retry or timing algorithm.
