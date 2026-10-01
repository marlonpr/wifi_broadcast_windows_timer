# v6.22.4 controller-only pooled forward synchronization

Firmware baseline remains **v6.23.2**. The wire protocol is unchanged.

## Why this exists

The v6.22.3 controller retried a device by replacing an entire 8+8 attempt. Hardware audit of the five-run v6.23.2 analyzer series showed cases where an earlier rejected attempt had found a lower forward-delay floor than the later attempt that happened to pass. The cal/verification delta also caused retries even when the combined forward floor was tight.

v6.22.4 changes retry semantics from **replacement** to **accumulation**.

## Production estimator

For each device, calibration and verification ingress samples are retained for the entire START synchronization window.

- The final offset is still `med3`: the median of the three fastest forward-ingress offset samples.
- The retry criterion is now only the **cumulative pooled top-3 spread**.
- Retry when pooled top-3 spread is greater than **150 us**.
- `|calibration med3 - verification med3|` no longer triggers a retry.
- The cal/verification delta is retained in diagnostics and in the measured uncertainty:

      uncertainty_us = pooled_top3_spread_us + abs(pooled_cal_ver_delta_us)

- The existing 20 ms fleet readiness gate remains the terminal START safety rule.

## Retry behavior

All devices still requiring more evidence are retried together in rotating round-robin rounds:

1. 8 calibration rounds across the current retry set.
2. 25 ms phase gap.
3. 8 verification rounds across the same retry set.
4. Append successful samples to the existing per-device pools.
5. Recompute cumulative med3 / spread / cal-ver delta.
6. Remove devices whose pooled top-3 spread is <=150 us.
7. Repeat for the remaining retry set, up to 5 attempts.

No successful sample from an earlier attempt is discarded. Transport misses also leave prior evidence intact.

## Retry exhaustion

After attempt 5, if a device still has pooled top-3 spread >150 us, its **cumulative pooled candidate** is applied as `SYNCED DEGRADED` rather than selecting a single "best attempt". Its pooled measured uncertainty enters the existing 20 ms readiness gate and the existing `START ANYWAY (DEGRADED)` confirmation path.

## Sample provenance

Every retained production sample is tagged internally with:

- attempt number,
- calibration / verification phase,
- round number.

The three winning floor samples are emitted in START diagnostics as e.g. `a1Cr3,a2Vr19,a2Cr20`, making it possible to see whether the selected floor is supported across attempts or concentrated in one burst.

## Common-epoch note

The offline audit can move every forward sample to a common epoch using the firmware's `rate_ppm`. The current FCT2 STATUS/SYNC_REPLY wire protocol does **not** transmit that rate, so an exact audit-equivalent rate normalization cannot be added controller-only without changing firmware/protocol.

v6.22.4 therefore pools the native forward-ingress offsets and keeps the representative sample's physical ingress epoch, exactly as the prior controller did for one 16-sample block. The important bias control is that retrying devices are sampled together in rotating rounds, so retries no longer serialize whole devices and reintroduce a large host-clock age span.

A replay of the 2026-10-01 five-run capture showed that native-epoch pooled med3 gives essentially the same counterfactual result as the rate-corrected audit on the four fully auditable runs: about 13 us RMS residual versus about 13.5 us with audit rate correction. This is a validation of this capture, not a general proof that rate correction is unnecessary.

## Unchanged

- v6.23.2 DS3231 discipline and SQW core-1 implementation.
- med3 floor estimator itself.
- START_AT/T* and disciplined propagation.
- 20 ms readiness gate.
- degraded-start confirmation workflow.
- RUNNING network silence.
- refresh-boundary framebuffer adoption.
