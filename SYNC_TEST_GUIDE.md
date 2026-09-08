# Wireless synchronization test guide

## What this test proves

The controller establishes a per-device mapping between each ESP32 `esp_timer_get_time()` clock and the Windows Master monotonic clock. START then carries one future absolute Master timestamp rather than "wait N ms after reception".

The app performs 8 calibration exchanges per device, keeps the 3 lowest-RTT valid samples and applies their 1/RTT² weighted offset with `SYNC_SET`, then performs 8 delay-free verification exchanges and uses the same best-3 inverse-RTT² weighted estimator for verification. START_AT is sent as the normal repeated absolute-time broadcast.

The watchdog-safe firmware uses a blocking 50 ms UDP receive timeout and a dedicated deadline task for the final START deadline; it does not continuously poll from `udp_command`.

## Controlled path-delay modes

ESP01 is always the control at 0/0 ms. Select one ESP02 calibration mode in the UI:

- **NONE — 0 / 0 ms**: no artificial delay.
- **SYMMETRIC — 250 / 250 ms**: `t1` is captured, the controller waits 250 ms before sending; the ESP captures `t3`, waits 250 ms, then replies.
- **ASYMMETRIC — 250 / 0 ms**: `t1` is captured, the controller waits 250 ms before sending; the ESP replies without artificial reverse delay.

The 8 verification exchanges are always 0/0 ms. START_AT is also always sent with no artificial path delay. This separation is deliberate.

## Expected observations

For **NONE**, ESP01 through ESP05 should all show ordinary Wi-Fi RTT and a residual error in the normal few-millisecond range.

For **SYMMETRIC 250/250**, ESP02 `Best sync RTT` should increase by roughly 500 ms, while its delay-free `Clock error` should stay near the ordinary baseline because `(reverse-forward)/2 ≈ 0`. START behavior should remain near baseline.

For **ASYMMETRIC 250/0**, ESP02 calibration RTT should increase by roughly 250 ms and the applied Master-minus-local offset should be biased by roughly -125 ms. Delay-free verification should expose a clock error near -125 ms. Because the controller reconstructs STARTED using the verification offset, ESP02 should also appear roughly +125 ms late relative to the requested Master target, while ESP01 remains the control.

Exact numbers include Windows scheduling and Wi-Fi jitter, so compare direction and scale rather than expecting exactly 250.000/125.000 ms.

## Recommended sequence

1. Run NONE, press **SYNC CLOCKS**, record all five devices' Best sync RTT, Verify RTT, Clock error, then run one 20 s START_AT.
2. Run SYMMETRIC, repeat the same measurements.
3. Run ASYMMETRIC, repeat the same measurements.
4. Save the screenshots/logs for side-by-side comparison before changing sample counts or scaling beyond two devices.
