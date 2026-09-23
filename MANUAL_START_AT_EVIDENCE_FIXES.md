# Manual START_AT evidence pass 2

This revision tightens the diagnostic evidence and fixes production START telemetry scoping.

## Manual proof verdict

The manual proof no longer uses verification-corrected start error or the 20 ms fleet timing budget as PASS criteria.

After all three frozen participants report STARTED for the frozen CommandId, the verdict is:

- `max |scheduler lateness| <= 1 us`
- minimum adjacent gap between the three recorded manual START_AT sends is at least `1.000 s`

The send records are sorted by their actual controller master send timestamps, so the operator may press the three device buttons in any order.

The result still displays:

- each device's sync residual
- each device's verification-corrected start error
- each device's scheduler lateness
- fleet start-error spread
- total manual send span
- minimum adjacent manual send gap

Start error and fleet start-error spread are explicitly labeled as sync-level information and are not used to determine PASS.

## Production START telemetry scoping

`StartSynchronizationResult` is no longer built from every device that happens to contain a `LastStartErrorMicroseconds` value.

A successful production START now freezes a telemetry context containing:

- production CommandId
- exactly the selected/frozen production participants

STARTED aggregation requires both:

- `LastStartedCommandId == frozen CommandId`
- membership in the frozen participant set

Therefore a three-device production run reports `0/3 ... 3/3`, not `3/5`, and stale STARTED values left on unselected devices cannot be mixed into a new run's spread.

The benchmark path also calls the same command-scoped aggregator with its explicit five-device CommandId.

A normal RESET clears the production telemetry context.

## Close behavior

The X / Alt+F4 close block remains diagnostic-only protection. During PREPARE (or a manual send while `isSending` is true), CANCEL is not available, so the close message now explicitly tells the operator to wait for the operation to finish. Once a live manual session is idle, it tells the operator to use `CANCEL / RESET` before closing.

This cannot protect against process kill, crash, power loss, or OS termination; that limitation is inherent to a controller-only diagnostic.

## Firmware

No firmware or protocol changes are required.
