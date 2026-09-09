# SYNC transport retry v6

This revision freezes the v5 inverse-RTT² estimator and changes only failure handling inside the existing five-attempt per-device synchronization loop.

## Retry policy

A complete synchronization attempt still uses:

- 8 calibration exchanges.
- The 3 lowest-RTT valid calibration samples.
- Inverse-RTT² weighted calibration offset.
- `SYNC_SET`.
- 8 delay-free verification exchanges.
- The 3 lowest-RTT valid verification samples.
- Inverse-RTT² weighted verification offset.
- ±3.000 ms quality gate relative to the expected experiment bias.

The maximum remains 5 total attempts per device and the quiet interval remains 150 ms.

A failed attempt is retried when the failure is considered transient transport behavior and the selected UDP interface is still usable. Retryable failures are:

- `TimeoutException` from `SYNC` or `SYNC_SET`.
- Selected transient `SocketException` conditions: timeout, would-block/try-again, no-buffer-space, connection reset, or host unreachable.
- `IOException` only when it wraps one of those transient socket errors.

The controller does **not** retry user cancellation, protocol validation failures, inconsistent `SYNC_APPLIED`, invalid timing samples, insufficient-sample estimator failures, explicit network-down socket errors, or a missing/unbound selected interface. Those conditions still abort immediately.

## Diagnostics

Partial raw sample diagnostics from a failed transport attempt are preserved exactly as in v5. A later retry receives the next attempt number, so a benchmark can show, for example, attempt 1 `ERROR,TIMEOUT` followed by attempt 2 `ACCEPTED`.

This specifically addresses the v5 30-trial result in which 29/30 fleet trials succeeded and the only failed trial stopped on ESP03 verification sample 3 because one `SYNC` datagram timed out. With v6, that timeout consumes one attempt instead of terminating the whole fleet synchronization immediately.
