# Manual START_AT evidence v4

This revision keeps the production START path unchanged and tightens the diagnostic evidence.

## Applied-offset consistency identity

For each frozen participant the controller now checks the integer identity

`start_error_us = scheduler_lateness_us - sync_residual_us`

where:

- `start_error_us = local_start_us + verification_offset_us - T*`
- `scheduler_lateness_us = local_start_us + applied_offset_us - T*`
- `sync_residual_us = applied_offset_us - verification_offset_us`

The displayed `identity_delta_us` is

`start_error_us - (scheduler_lateness_us - sync_residual_us)`.

It must be exactly `0 us` for every participant. The verification offset appears in both `start_error_us` and `sync_residual_us`, so it cancels from `identity_delta_us`. A zero therefore certifies only that the firmware start used the same applied offset that the controller recorded at `SYNC_APPLIED`, with no intervening offset change visible to this relation. It does **not** certify that the verification estimate itself is correct.

A non-zero value means the controller-recorded applied offset and the offset embodied by the firmware start are inconsistent (or stale), so the manual proof fails. This is an internal applied-offset consistency check, not an additional physical timing measurement and not a verification-accuracy check.

## Manual proof PASS criteria

A manual proof passes only when:

1. all three frozen participants report `STARTED` for the frozen CommandId;
2. `max |scheduler_lateness_us| <= 1 us`;
3. after sorting recorded sends by `SentMasterMicroseconds`, every adjacent send gap is at least `1.000 s`;
4. every row has `identity_delta_us == 0`.

Start error and fleet start-error spread remain sync-level information and are not timing-budget PASS criteria.

## Send separation is chronological, not DeviceId ordered

The result sorts the three recorded sends by actual master send timestamp before calculating gaps and also prints the chronological order, for example:

`chronological send order=ESP01 -> ESP03 -> ESP02`

This makes the out-of-order negative control meaningful.

## High-value hardware/UI checks

### A. Out-of-order PASS

Use an order such as `ESP03 -> ESP01 -> ESP02`, with more than 1 s between each click. PASS must not depend on DeviceId order.

### B. Out-of-order separation negative control

Send ESP01 and ESP03 less than 1 s apart, then send ESP02 several seconds later. Expected result: separation FAIL. This distinguishes a true chronological calculation from a DeviceId-ordered calculation.

### C. Abort/no-partial-start path

Send START_AT to only two of the three prepared devices and let the `T* - 2 s` cutoff occur. The controller must unicast RESET to the full frozen set and abort. In the same run, watch the two armed countdown displays at `T*`: neither may start. Also require no `STARTED` telemetry for the abandoned CommandId and verify subsequent STATUS returns the devices to non-ARMED/non-RUNNING state.

### D. Production aggregation regression

The direct regression sequence needs only the three devices already available:

1. production START with ESP01/ESP02/ESP03 and allow STARTED telemetry to populate all three;
2. without restarting the controller, run production START with only two of them, for example ESP01/ESP03;
3. verify progress/final result is `2/2` and the spread is calculated from only those two devices on the new CommandId.

The unselected device (ESP02 in this example) deliberately retains stale STARTED telemetry from the prior run. That is sufficient to reproduce the precondition of the old generic aggregation bug; the old implementation could count the stale third value and report a mixed result such as `3/5`.

## Scheduler lateness provenance

The firmware implementation stores the actual local timestamp when the timer transitions to RUNNING and computes logged scheduler lateness as `actual_local_start_us - target_local_start_us`. The STARTED packet carries the same actual local start plus its master estimate. Therefore scheduler lateness is based on a post-wait timestamp, not copied from the requested deadline.

A firmware change that alters STARTED timestamp semantics should be treated as a reason to re-audit this diagnostic before using it as evidence.
