# Five-device scaling test

## Device identities

The controller expects the exact IDs `ESP01`, `ESP02`, `ESP03`, `ESP04`, and `ESP05`. Numeric IDs such as `1` or `5` alone are not equivalent.

## Baseline configuration

For the baseline scaling run select **NONE — 0 / 0 ms**, **BLOCKING_THREAD_T4**, and **8 + 8 — baseline**. All five devices then use ordinary delay-free calibration and verification. The v8 experiment can then compare **4 + 4 — candidate** without changing the estimator, quality gate, scheduler, or START_AT path.

## Test sequence

1. Power all five ESP32 timers and wait until every card is ONLINE with a valid factory-LAN IPv4 address.
2. Select **NONE — 0 / 0 ms**, **BLOCKING_THREAD_T4**, and the requested sample-count profile.
3. Press **SYNC CLOCKS**. The controller synchronizes ESP01 through ESP05 sequentially using the selected calibration/verification counts; each phase uses a 1/RTT² weighted offset over its 3 lowest-RTT valid samples.
4. Record the total synchronization duration shown in the status message, each device's best sync RTT and verify RTT, the worst verified clock error, and the five-device clock spread.
5. Press **START_AT** for a 20-second run. One shared absolute target is broadcast three times; all five devices must use the same command ID and target Master timestamp.
6. Record every device's verification-corrected start error and the global five-device start spread.
7. Repeat at least 10 runs before changing architecture. This establishes the five-device baseline before any response-slotting, parallel synchronization, or other fleet optimizations.

## What to watch for

- All five `scheduler_lateness_us` values should remain near the validated two-device behavior.
- If STATUS discovery becomes intermittent, note it rather than immediately changing the 2-second broadcast; simultaneous status replies are one of the scaling effects we want to measure.
- If total synchronization time becomes too large, first evaluate the v8 4+4 sample-count candidate. Only after sample acquisition is optimized should controlled parallel/staggered synchronization be considered; do not change the absolute START_AT mechanism.
- At 10-15 devices, reply-slotting for STATUS/ACK traffic may become useful if simultaneous responses create contention.
