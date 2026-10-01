# v6.20 production-silent controller

This promotes the validated v6.19-C network-silent RUNNING behavior into normal controller operation.

## Normal production run

After the final START_AT ARM/status barrier succeeds, periodic STATUS discovery is stopped and drained. No automatic controller STATUS request is transmitted during the countdown. UDP reception stays active and RESET stays available. Automatic discovery resumes after the run window.

## Operator status request

`REFRESH STATUS (SELECTED)` sends exactly one unicast `STATUS_REQUEST` to each selected timer with a known IP. It does not restart periodic discovery. TX traces identify these packets as `MANUAL_STATUS_REQUEST`.

A manual request during RUNNING is intentionally allowed, but it reintroduces network activity for that instant and therefore should be used only when status is actually needed.

## Controller restart/reconnect during an existing run

On startup the normal discovery request is allowed to obtain a state snapshot. If a reply reports `RUNNING`, subsequent periodic discovery is stopped. The controller keeps devices intentionally ONLINE and locally decrements each timer card from its last reported remaining time. A fallback timer resumes ordinary discovery after the reported remaining duration plus a small grace period. Additional manual RUNNING status replies refresh that fallback.

## UI

Each timer card shows the time of the last real STATUS packet. The displayed remaining time continues to decrement locally while its last reported state is RUNNING; it is not presented as a fresh device measurement until the operator requests status or a transition packet arrives.
