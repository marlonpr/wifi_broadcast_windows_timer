# v6.21 Sync-Series Diagnostic Controller

Controller-only diagnostic variant. Firmware remains v6.21 RTC-qualified START.

## Purpose

Collect the 10 x 60-second START series used by `b0_sync_audit_v2.py` before changing the synchronization estimator.

## Production sync-order selector

The Countdown panel now exposes:

- **Normal** - selected participants synchronize in ascending device order.
- **Reverse** - selected participants synchronize in descending device order.
- **Alternate each successful START** - run 1 Normal, run 2 Reverse, run 3 Normal, etc.

The Alternate counter advances only after START_AT has armed successfully and the production silent-run window has begun. A failed RTC qualification, failed SYNC, failed gate, or failed ARM does not consume a series number.

The actual order is also written to each controller TX CSV in the new `SyncOrder` column.

## Recommended series

1. Use unchanged v6.21 firmware on ESP01 and ESP02.
2. Select only ESP01 and ESP02.
3. Set duration to 01:00.
4. Set Production sync order to **Alternate each successful START**.
5. Start Analyzer v8 once and keep one continuous capture for all ten runs.
6. Run ten successful countdowns. Do not use REFRESH STATUS.
7. Save both UART logs and the continuous analyzer capture.
8. Run:

   `python b0_sync_audit_v2.py Analyzer_v8.log ESP01.log ESP02.log --csv series.csv`

No synchronization algorithm is changed in this controller variant.
