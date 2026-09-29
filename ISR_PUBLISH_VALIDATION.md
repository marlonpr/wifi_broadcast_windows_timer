# ISR publication validation controller

This bench-only build replaces the first STATUS sweep's 2 ms final-spin window
with a 20 ms QPC spin and targets authoritative device RX leads across roughly
`boundary - 6 ms` through `boundary + 2 ms`.

Target RX leads, in microseconds:

```text
6000, 5500, 5000, 4500, 4000, 3500, 3000, 2500, 2000, 1500,
1000, 500, 200, 0, -200, -500, -800, -1000, -1500, -2000
```

Positive means before the boundary; negative means after. The firmware's
`recv_us` remains authoritative. Existing per-device transit compensation is
kept (ESP01 2558 us, ESP02 2344 us).

Windows timing changes in this build:

- requests 1 ms process timer resolution with `timeBeginPeriod(1)` for the sweep,
- starts the QPC-backed final spin 20 ms before each send target,
- releases the 1 ms timer period when the sweep finishes.

The ESP32 `recv_us` timestamp remains the independent variable used for analysis.
