# Windows runtime brightness control

This controller revision adds a panel-brightness control to the main timer card.

## Operator workflow

1. Select one or more ESP participants with the existing participant checkboxes.
2. Set `Panel brightness` from `0` through `100` percent using the slider or number box.
3. Press **APPLY BRIGHTNESS TO SELECTED**.
4. The controller unicasts the brightness command to every selected device that is currently online and has a resolved IP address.

The device ACK uses the normal per-device ACK display. Offline or unresolved selected devices are reported as skipped.

## Semantics

- `0%` blanks LED output.
- `100%` requests maximum panel output brightness.
- Brightness does not alter countdown state, clock synchronization, START_AT target, CommandId used by the timer, or RESET behavior.
- Brightness is runtime-only. A reboot restores the build-time firmware default.
- The controller starts at `50%`, matching the current firmware default of `CONFIG_FACTORY_DISPLAY_BRIGHTNESS=128` approximately.

## Protocol

```text
FCT2|CMD|BRIGHTNESS|<CommandId>|<percent>|0
```

The firmware replies with the existing ACK packet form using command type `BRIGHTNESS`.
