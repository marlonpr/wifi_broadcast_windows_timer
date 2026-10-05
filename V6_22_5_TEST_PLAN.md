# v6.22.5 / v6.23.3 fleet-health validation

## Smoke test with ESP01 + ESP02

1. Flash firmware v6.23.3 on both devices.
2. Run controller v6.22.5.
3. Wait for RTC qualification, then run one 30 s countdown.
4. Confirm the controller writes both `controller_tx_...csv` and `controller_health_...csv`.
5. The health file must contain `START` and `POST_RUN` rows for both devices with `StatusCaptured=1`.
6. At START, verify `RtcState=LOCKED`, `RtcSqwCore=1`, `RtcQueueDrops=0`, the sync/start health groups are valid, and scheduler/start errors remain normal. The display group may race the immediate START STATUS and is therefore not required in this row.
7. At POST_RUN, verify the display health group is valid and `StartPublishLatenessUs`, `WorstPublishLatenessUs`, and `FrameNotReady` contain the final presentation summary.
8. The TX CSV should show `HEALTH_STATUS_REQUEST` only after the countdown has ended when an explicit post-run health fetch was needed.

This test does not require Analyzer_v8. Reconnect the analyzer only if health telemetry or visual behavior identifies an anomaly.

## Batch qualification

After the smoke test, add devices progressively. Before the long 99:59 run, perform two warm qualification STARTs approximately 30 minutes apart and run `tools/qualify_rtc_batch.py` on the two health CSVs. Compare `DeltaFromBatchMedianPpm` across the batch; do not use raw `offset_master_minus_local_us` slope as a DS3231 criterion.
