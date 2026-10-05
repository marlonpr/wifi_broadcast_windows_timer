# v6.22.8 — START #1 warm-up gate and module acceptance

Controller synchronization/runtime behavior remains unchanged from v6.22.7. This
revision changes qualification tooling only.

## START #1 readiness gate

`tools/rtc_warmup_gate.py` now uses the latest 10-minute RTC_QUAL window and requires:

- continuous `LOCKED` state;
- 129 fit points;
- fit RMS <= 3 us;
- zero fit outliers, inferred-missing edges, holdover entries, and queue drops;
- valid DS3231 temperature and SQW ISR core 1;
- at least 30 samples and at least 95% of the 600 s window covered;
- |OLS rate trend| <= 0.005 ppm/min;
- |last rate - first rate| <= 0.05 ppm;
- DS3231 package-temperature range <= 0.25 C.

The latest monotonic raw-local segment is used, so a reboot cannot borrow warm-up
history from an older boot.

Example:

```powershell
python tools\rtc_warmup_gate.py ESP01.log ESP02.log
```

Do not take qualification START #1 until the final line is:

```text
QUALIFICATION_START1_GATE=READY
```

## Module acceptance is separate from measurement validity

`qualify_rtc_batch.py` still decides whether each two-START rate measurement is
valid using same-boot, SQW continuity, endpoint health, warm confirmation, and the
0.25 ppm START1->START2 rate-stability guard.

For valid measurements it now also reports `ModuleAcceptance` using the fleet median:

```text
|DeltaFromBatchMedianPpm| <= 1.0 ppm       ACCEPT
1.0 < |DeltaFromBatchMedianPpm| <= 2.0     INSPECT
|DeltaFromBatchMedianPpm| > 2.0 ppm        REJECT
```

The thresholds are configurable with `--accept-delta-ppm` and `--reject-delta-ppm`.
Temperature remains in the CSV beside each qualified rate; no temperature
compensation is applied.

At 99:59, two modules at opposite ±1 ppm limits differ by 2 ppm, corresponding to
11.998 ms of rate-derived separation over 5999 s before display refresh phase.
