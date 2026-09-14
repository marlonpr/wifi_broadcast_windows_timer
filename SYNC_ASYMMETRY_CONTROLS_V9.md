# V9 physical asymmetry controls

This revision prepares the four hardware controls that must run before the 600-trial natural-path measurement.

## Implementation changes

1. **Selectable injection target**
   - ESP01, ESP02, ESP03, ESP04, or ESP05.
   - Target and profile are independent UI selectors.
   - Changing either invalidates the previous synchronization.
   - Raw `SyncMode` identifies the target as `TARGET=ESPxx`.

2. **Precise reverse injection**
   - Firmware captures T3 before the artificial wait.
   - 1 ms and 4 ms waits use `esp_timer_get_time()` microsecond timing rather than FreeRTOS tick quantization.
   - Achieved delay is returned in the optional SYNC_REPLY field and written as `ActualReverseDelayUs`.
   - The Windows controller refuses a reverse control if a nonzero delay was requested but the firmware does not report a measured delay.

3. **Precise/measured forward injection**
   - The 1/4/40 ms metrology range uses QPC (`Stopwatch.GetTimestamp`) spin timing instead of `Task.Delay`.
   - Legacy 250 ms profiles may sleep most of the interval, then finish against QPC.
   - Achieved delay is written as `ActualForwardDelayUs`.

4. **Actual-delay quality correction**
   - For each calibration sample: `bias_j = (actual_reverse_j - actual_forward_j) / 2`.
   - The expected applied bias is calculated from the same best-3 samples and same inverse-RTT^2 relative weights used by the offset estimator.
   - The ±3 ms gate evaluates `Residual - ExpectedActualBias`, not nominal requested delay.

5. **Physical-analysis predictor fix**
   - `tools/analyze_logic_capture.py` uses `-ClockErrorUs` as the predicted START error.
   - STARTED telemetry loss no longer removes a physically complete trial from B_i analysis.
   - The output also includes `PhysicalVsOtherFourUs`, which is the correct unattenuated statistic for one-device synthetic controls.

## Frozen fixture

Keep the validated fixture unchanged:

- 1 kOhm series resistor directly at each timer START GPIO.
- Twisted signal + dedicated ground return per device.
- Leads routed away from PCB antennas.
- Timers at the separated (>30 cm) positions.
- Same AP placement, power arrangement, and analyzer wiring throughout controls and clean blocks.

## Control matrix

Use `BLOCKING_THREAD_T4`, fixed 8+8, best-3 inverse-RTT^2, and normal ±3 ms quality deviation gate.

| Control | Target | Calibration injection | Predicted physical movement |
|---|---|---|---|
| A | ESP02 / ESP32 | reverse +1 ms | ~0.5 ms EARLY |
| B | ESP02 / ESP32 | reverse +4 ms | ~2.0 ms EARLY |
| C | ESP03 / ESP32-S3 | reverse +4 ms | ~2.0 ms EARLY |
| D | ESP02 / ESP32 | forward +40 ms | ~20 ms LATE |

Run ~30 trials per control.

For control gain use the target relative to the other four, not fleet-centered target timing:

`C_i = t_i - mean(t_j, j != i)`

For A/B/C compare `Delta C_i` against a nearby no-injection baseline. This removes each device's natural static bias.

## Preregistered conclusions

The control stage passes only if:

- reverse delay produces an **early** physical START;
- forward delay produces a **late** physical START;
- physical displacement is approximately one half of measured one-way injected asymmetry;
- ESP02 and ESP03 reverse +4 ms have compatible sign/gain.

The later 600-trial natural-path replication is separately preregistered to test a **negative** association between contemporaneous fleet-centered P5 RTT floor and fleet-centered B_i. The loaded-channel run remains required for causal attribution because clean-path floor and silicon family are collinear in this five-device fleet.
