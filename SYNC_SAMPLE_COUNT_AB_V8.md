# SYNC sample-count A/B experiment (v8)

This revision isolates the next synchronization bottleneck: the number of timestamp exchanges per device.

## Fixed production settings

Keep these unchanged for the entire experiment:

- Windows T4 path: `BLOCKING_THREAD_T4` (now the controller default).
- ESP02 path delay: `NONE — 0 / 0 ms`.
- Offset estimator: 1/RTT² weighted consensus over the 3 lowest-RTT valid samples.
- Verification quality gate: ±3.000 ms from the expected bias.
- Per-device retry policy: up to 5 total synchronization attempts with the existing 150 ms quiet interval.
- Firmware scheduler and `START_AT` mechanism: unchanged.

No ESP32 firmware change is required for this experiment.

## Profiles

The Windows controller now exposes **SYNC sample-count experiment**:

- `8 + 8 — baseline`: 8 calibration + 8 verification exchanges per device (16 exchanges/device).
- `4 + 4 — candidate`: 4 calibration + 4 verification exchanges per device (8 exchanges/device).

Both profiles still retain the best 3 RTT samples in each phase. Changing the profile invalidates the previous synchronization so `START_AT` cannot reuse an offset measured under another profile.

The benchmark CSV `SyncMode` value records the selected profile, for example:

`NONE (0/0 ms) | RX=BLOCKING_THREAD_T4 | SAMPLES=4+4_CANDIDATE`

## Recommended counterbalanced run

Use 10 trials per block and alternate order by pair:

1. Pair 1: `8+8`, then `4+4`
2. Pair 2: `4+4`, then `8+8`
3. Pair 3: `8+8`, then `4+4`
4. Pair 4: `4+4`, then `8+8`
5. Pair 5: `8+8`, then `4+4`
6. Pair 6: `4+4`, then `8+8`

This gives 60 trials per profile and 6 matched pairs while balancing time order.

Before each block verify:

- all five devices are ONLINE;
- ESP02 calibration path is `NONE`;
- T4 receive path is `BLOCKING_THREAD_T4`;
- the requested sample-count profile is selected.

Then press `RUN BENCHMARK`.

## Primary metrics

Compare profiles using matched blocks, not only pooled raw measurements:

1. fleet SYNC duration (median and P95);
2. all-five final synchronization success;
3. first-attempt acceptance and total retries;
4. median/P95/max absolute final `ClockErrorUs`;
5. START spread (median/P95/max);
6. worst absolute START timing error.

RTT remains a diagnostic metric rather than the optimization objective.

## Acceptance target for 4+4

Treat `4+4` as acceptable only if it materially reduces fleet-ready time while preserving the baseline synchronization distribution. A practical pre-declared gate is:

- final all-five synchronization success: no regression from the 8+8 baseline;
- P95 absolute `ClockErrorUs`: no more than 0.2 ms above baseline;
- P95 START spread: no more than 0.3 ms above baseline;
- retry rate: no material increase;
- fleet SYNC duration: clearly lower.

If `4+4` passes, use it as the fixed-count candidate before implementing adaptive acquisition. If it fails, the raw sample CSVs will show whether calibration or verification needs the additional evidence.

## Production scaling target

After the five-device A/B result is accepted, repeat on the 15-device fleet. The current production target is:

`P95 fleet-ready time < 15 s`

while preserving the accepted synchronization-quality envelope.
