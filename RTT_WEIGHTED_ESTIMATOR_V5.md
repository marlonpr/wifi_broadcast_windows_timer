# RTT-weighted synchronization estimator v5

## Purpose

Raw v4 benchmark data showed that the three retained low-RTT samples can still have materially different offsets. A hard median gives all three retained samples equal rank influence even when one RTT is much lower than the other two. v5 keeps the same sample counts and quality policy but changes how the selected offsets are combined.

## Estimator

Each calibration phase and each verification phase still collects 8 valid NTP-style exchanges and sorts them by network RTT. The controller retains the 3 lowest-RTT samples.

For each retained sample `i`:

```text
relative_weight_i = (minimum_RTT / RTT_i)^2
```

This is algebraically equivalent to inverse-square RTT weighting (`1 / RTT_i^2`) but is numerically better conditioned because the largest relative weight is 1.

The offset applied or verified is:

```text
weighted_offset = round(
    sum(relative_weight_i * offset_i)
    / sum(relative_weight_i)
)
```

The minimum-RTT sample remains the representative sample only so `SYNC_SET` carries a real `SyncId`. Its individual offset is not substituted for the weighted estimate.

If a zero-RTT sample were ever observed, v5 avoids division by zero by using the mean offset of the zero-RTT subset; in the inverse-square limit those samples dominate every positive-RTT sample.

## Unchanged policy

- 8 calibration exchanges.
- 8 delay-free verification exchanges.
- 3 retained low-RTT samples in each phase.
- Quality gate: ±3.000 ms from the expected experiment bias.
- Maximum 5 total attempts per device.
- 150 ms quiet interval before a quality retry.
- Periodic STATUS discovery paused/drained during timing-critical synchronization.
- 100 ms drain interval before the first timing sample.
- START_AT deadline scheduler and ESP32 firmware are unchanged.

## Artificial path-delay experiment

The 250/0 ms asymmetric experiment remains visible. A persistent 250 ms forward-path delay shifts all retained calibration offsets by approximately -125 ms. Inverse-square weighting does not remove a bias shared by the retained samples; delay-free verification should therefore still observe the expected residual near -125 ms.
