# v6.22.4 pooled-sync hardware validation

Use firmware **v6.23.2 unchanged** on ESP01/ESP02 and controller **v6.22.4**.

## Analyzer

Analyzer_v8 is sufficient. Connect only:

- ESP01 GPIO33 COMMIT
- ESP01 GPIO16 REFRESH
- ESP02 GPIO33 COMMIT
- ESP02 GPIO16 REFRESH
- common GND

SQW analyzer taps are not required.

## Test A — normal network

Run five 30 s STARTs. Keep one Analyzer_v8 capture and both UART logs.

Acceptance:

- `start_error_us=0` and `scheduler_lateness_us=0` on both devices.
- `frame_not_ready=0`.
- RTC remains `LOCKED`, `sqw_core=1`, `queue_drops=0`.
- LED panel remains visually clean.
- Controller diagnostics say `pooled forward-only sync PASS`.

## Test B — exercise at least one retry

If Test A produces no retry, run the independent ESP32 AP-load generator at 10 Mbps during synchronization until at least one device reports `pooled RETRY`.

Verify:

1. A retry is caused only by `pooled top3 spread >150 us`.
2. A large cal/verify delta may appear in diagnostics but does not by itself cause a retry.
3. The cumulative pool size grows from 16 to 32, 48, ... rather than resetting to 16.
4. The final `top3=` provenance may contain samples from earlier attempts; earlier evidence is not discarded.
5. If several devices need retry, they are sampled together in rotating retry-set rounds.
6. If the pooled spread falls to <=150 us, the device is accepted immediately.
7. If five attempts are exhausted, the cumulative pooled candidate—not a single best attempt—is marked DEGRADED and sent to the existing 20 ms readiness gate.

## Timing acceptance

For each countdown calculate:

- GPIO33 pair b0 (and UART start-lateness correction when available),
- GPIO33 max/P95 pair separation,
- GPIO16 max/P95 pair separation,
- each device COMMIT->REFRESH maximum,
- analyzer dropped count.

The purpose of this test is to confirm that retry pooling returns physical b0 to the tens-of-microseconds regime while preserving the already validated v6.23.2 display-boundary behavior.
