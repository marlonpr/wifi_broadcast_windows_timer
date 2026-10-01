# FactoryTimer Zeit v6.22.2

This is a controller-only update for the v6.22 firmware.

Production START now uses:
- forward-only med3 estimator;
- 150 us top-3 spread gate for calibration, verification and final consensus;
- rotating round-robin first-attempt synchronization across the selected fleet;
- targeted per-device retry when a thin/failed floor is detected;
- unchanged DS3231-disciplined propagation to T*.

See `V6_22_2_ROUND_ROBIN_FLOOR_GATE.md` for the exact policy and the loaded-AP stress test.
