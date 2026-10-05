# v6.22.5 — Fleet health telemetry and DS3231 qualification

This controller revision keeps the v6.22.4 pooled-forward synchronization policy unchanged and adds controller-visible health capture for firmware v6.23.3.

## New per-run health CSV

Every production START trace now produces the existing `controller_tx_...csv` plus a companion:

```text
controller_health_YYYYMMDD-HHMMSS_<CommandId>.csv
```

There are two rows per frozen participant:

- `START`: first `RUNNING` STATUS for the run;
- `POST_RUN`: latest `FINISHED` STATUS received before the trace closes.

After the nominal finish, the controller explicitly requests up to two post-run health STATUS rounds for participants whose `FINISHED` health has not yet been captured. This happens after the timing-critical countdown and does not change START or RUNNING network silence.

A row with `StatusCaptured=0` means the controller did not receive that phase's STATUS; it must not be treated as healthy telemetry.

The health CSV records RTC lock/fit health, SQW core, sync-epoch raw and disciplined clocks, scheduler timing, display publication timing, and `frame_not_ready`.

## Correct DS3231 qualification observable

Do **not** qualify a module from the slope of `offset_master_minus_local_us`. That offset is measured against raw `esp_timer`, so its slope primarily measures the ESP32 crystal.

Firmware v6.23.3 reports the disciplined quantity at the selected forward-sync sample epoch:

```text
Q = Master - Disciplined
  = sync_source_offset_us
    + sync_epoch_local_us
    - sync_epoch_disciplined_us
```

The health CSV calls it `SyncEpochMasterMinusDisciplinedUs`. It also records

```text
SyncEpochMasterUs = sync_source_offset_us + sync_epoch_local_us
```

For two warm qualification STARTs A and B:

```text
q_slope_ppm = (Q_B - Q_A) / (M_B - M_A) * 1e6
disciplined_minus_master_ppm ~= -q_slope_ppm
```

The absolute result includes the Windows master-clock rate. For fleet screening, compare `disciplined_minus_master_ppm` against the batch median. The common PC-rate term cancels from that median-relative comparison.

## Warm qualification procedure

1. Power the whole batch and let the RTC-discipline fits settle while the devices are in their normal operating environment.
2. Confirm all devices are `LOCKED`, have zero queue drops, valid temperature, SQW ISR core 1, and stable fit metrics.
3. Run qualification START A and save its `controller_health_...csv`.
4. Leave the devices powered and undisturbed for about 30 minutes.
5. Run qualification START B and save its health CSV.
6. Analyze:

```powershell
python tools\qualify_rtc_batch.py <health_A.csv> <health_B.csv> --output rtc_batch_qualification.csv
```

An optional batch-relative warning threshold can be supplied explicitly:

```powershell
python tools\qualify_rtc_batch.py <health_A.csv> <health_B.csv> --warn-delta-ppm 2.0 --output rtc_batch_qualification.csv
```

No production ppm rejection limit is hard-coded by default. Establish the fleet distribution first and choose a limit from the hardware requirement/data.

## Scope

This revision does not change v6.22.4 pooled med3, the 150 us pooled-spread retry trigger, round-robin retry grouping, the 20 ms readiness gate, or START_AT timing. It is instrumentation and qualification support only.
