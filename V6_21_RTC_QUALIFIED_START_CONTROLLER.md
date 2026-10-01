# v6.21 RTC-qualified START controller

The production START button now owns RTC readiness. The operator no longer needs to watch `RTC_QUAL` serial logs.

## START sequence

1. Freeze/reserve the selected participant set.
2. Verify devices are discovered and not already ARMED/RUNNING.
3. Wait up to 90 seconds for every selected device to report:
   - `LOCKED`
   - at least 64 fit points
   - fit RMS <= 3.0 us
   - zero SQW ISR queue drops
   - valid DS3231 temperature
4. Continue automatically with the existing fixed 8+8 foreground synchronization.
5. Apply the existing +/-3 ms synchronization gate.
6. Freeze common T*, ARM all participants, and run the final fresh STATUS barrier.
7. Enter the validated production-silent RUNNING policy.

While waiting, the controller observes normal pre-run discovery updates rather than forcing a STATUS request every second. It performs an explicit refresh at entry and only forces another refresh if status becomes stale.

Conditions that can recover (ACQUIRING, too few fit points, high RMS, temperature not valid yet) are waited on. Missing v6.21 metrics or non-zero queue drops are surfaced immediately because waiting cannot correct them.

v6.21 firmware and v6.21 controller should be deployed together; the v6.20 controller accepts only the older STATUS field counts.
