# v6.22 forward-only production synchronization

Normal START now uses the device-reported earliest UDP ingress timestamp rather than the reply leg to estimate phase.

For each timer the controller collects the existing 8+8 exchanges, then selects the three largest `T1 - ingress_local` offsets across all 16 and applies their arithmetic mean. Their mean ingress timestamp is sent as the offset epoch. This makes reverse-path delay, `sendto()` duration, uplink retries, and PC receive latency irrelevant to the applied production phase estimate.

The calibration 8 and verification 8 are also estimated independently. Their difference is used with the existing ±3 ms retry gate before the final `SYNC_SET` is applied.

The previous four-timestamp/RTT estimator remains available to benchmark and manual diagnostic workflows; v6.22 changes only the production START path.

The sync-order diagnostic selector remains available so the fixed build can be tested with alternating ESP01/ESP02 order.
