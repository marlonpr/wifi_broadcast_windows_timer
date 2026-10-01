# v6.22.2 START diagnostics UI revision

Firmware: unchanged v6.22.
Synchronization policy: unchanged v6.22.2 round-robin + med3 + 150 us floor-spread gate.

Controller UI changes only:
- Adds a persistent START diagnostics panel directly below START / RESET.
- Shows requirement-check progress, RTC qualification wait, round-robin SYNC progress/retries, and ARMING progress.
- Every blocked START shows the exact block reason, affected timer, and remedy.
- Adds explicit diagnostics for invalid duration, unavailable network interface, and controller-busy early exits.
- Existing lower Connection status remains and mirrors the most important START diagnostic.

No protocol or firmware changes are required.
