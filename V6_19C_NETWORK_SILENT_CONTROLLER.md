> Historical validation note: v6.19-C proved the timing benefit now used by v6.20 production-silent operation.

# v6.19-C network-silent controller

This source is derived from the ISR-validation controller used for the A/B qualification. The production START preparation, 8+8 foreground sync, readiness gate, ARM retries, final STATUS barrier, controller TX trace, RESET/abort behavior, and UI are unchanged.

The one C-specific change is after successful ARM verification:

1. Stop ordinary `STATUS_REQUEST` discovery and drain in-flight discovery traffic.
2. Do **not** start the 20-boundary STATUS lead sweep.
3. Keep the receive path/UI/local countdown running normally.
4. Send no controller-generated STATUS request during the measured RUNNING interval.
5. At `T* + duration + 0.5 s`, resume normal status discovery.

The controller TX CSV remains enabled. A valid 600 s C trace should contain ARM/pre-run traffic and post-run traffic, but no `STATUS_SWEEP_*` rows and no `STATUS_REQUEST` rows with `0 <= DeltaFromTStarUs <= 600000000`.
