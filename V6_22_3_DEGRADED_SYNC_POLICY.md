# v6.22.3 controller-only degraded synchronization policy

Firmware remains **v6.22**.

## Production forward-sync retry rule

The 150 us threshold is a retry-quality threshold, not a terminal START safety threshold.
A device gets a fresh per-device 8+8 retry when either condition is true:

- final 16-sample med3 top-3 spread > 150 us, or
- |calibration med3 - verification med3| > 500 us.

The calibration and verification half-spreads are diagnostic only. They are not compared with the 150 us threshold.

## Retry exhaustion

Each usable attempt is ranked by measured synchronization uncertainty:

    uncertainty_us = final_top3_spread_us + abs(cal_ver_delta_us)

If all attempts remain retry-grade, the controller retains the usable attempt with the smallest uncertainty and applies that exact synchronization to the timer. It is marked `SYNCED DEGRADED`.

The measured uncertainty is passed to the existing StartReadinessGate. The gate then adds its normal drift-age bound and applies the existing 20 ms worst-pair fleet limit. The 150 us/500 us thresholds do not independently block START after the retry budget is exhausted.

## START ANYWAY (DEGRADED)

If a retained degraded synchronization passes the 20 ms readiness gate, normal START pauses before ARMING and exposes `START ANYWAY (DEGRADED)` in the START diagnostics card.

Clicking that button:

1. reuses the exact retained synchronization (no new SYNC traffic),
2. requests fresh STATUS / verifies the device sessions,
3. recomputes T* and the 20 ms readiness bound using the aged measured uncertainty,
4. proceeds to ARMING only if the 20 ms gate still passes.

A normal START instead discards the pending confirmation and performs a fresh synchronization.

## Controller TX CSV

The TX trace gains `SyncQualitySummary`, containing per-device:

- degraded=0/1
- calibration/verification delta
- final top-3 spread
- measured uncertainty

This lets analyzer b0 results be joined directly to the synchronization quality of that START command.

## Intended loaded-AP experiment

Generate contention from a **different station** than the controller PC when possible. Keep Analyzer v8 running. For accepted loaded starts, compare b0 against final spread, cal/ver delta and uncertainty. The purpose is to empirically locate where degraded evidence becomes a visible timing problem; the 20 ms fleet bound remains the terminal safety rule.
