# Timing-quiet synchronization retry v2

This revision is intended for the five-device NONE-mode benchmark after the hardware replacements on ESP01 and ESP04.

## Controller changes

- Keep the synchronization quality threshold at `±3000 us`.
- Increase the maximum synchronization attempts per device from 3 to 5.
- Wait 150 ms after a rejected verification before recalibrating the same device.
- Stop periodic `STATUS_REQUEST` discovery before the timing-critical synchronization sequence.
- Wait until the discovery loop has fully exited, then allow a 100 ms drain interval before the first SYNC packet.
- During an automatic benchmark trial, keep discovery paused through SYNC, `START_AT`, `STARTED` telemetry collection, and RESET.
- Restart discovery between benchmark trials. `StartOrRestart` sends a fresh status request immediately, allowing normal status/RSSI refresh during the inter-trial pause.
- Freeze ONLINE/OFFLINE aging while the intentional timing quiet period is active so pausing discovery does not create false OFFLINE indications.
- Update CSV hardware mapping to the current fleet: ESP01/ESP02/ESP04 are `ESP32`; ESP03/ESP05 are `ESP32-S3`.

## Recommended validation

Use the same test conditions as the previous 30-trial run:

```text
Mode: NONE (0/0 ms)
Quality threshold: ±3 ms
Trials: 30
Same AP/BSSID/channel and physical device placement
```

Compare fleet success rate, failure device distribution, total retries, `BestSyncRttUs`, `VerifyRttUs`, accepted `ClockErrorUs`, `FleetStartSpreadUs`, and `SchedulerLatenessUs` against the previous CSV.

## Build note

The source was updated and statically checked in the artifact environment. The .NET SDK is not installed in that environment, so compile and run the Windows test suite on the Windows/.NET 10 development machine before the hardware benchmark.
