# v6.22.12 — rollout telemetry hardening

This controller revision matches firmware v6.23.15.

## 44-field STATUS

The parser keeps all prior STATUS forms and accepts the v6.23.15 44-field form.
The two appended fields are:

- `Cpu0MonitorMissedPeriods`: skipped 250 us GPTimer deadlines.
- `FirmwareBuildId`: eight-character ELF SHA prefix.

The health CSV also derives `Cpu0MonitorExpectedPeriods = samples + missed`.

## Exposure accounting

Clean board-hours are derived from monitor samples. Rows do **not** contribute clean
exposure if they contain a canary, a missed period, legacy STATUS, invalid monitor,
wrong-core callback, overflow, interrupt-level mismatch, sample-rate warning or
missing build ID. `FLEET_MISSED_PERIOD_ROWS` makes sample-grid deficits visible even
if no retained >=50 us event is trusted.

## Positive-control tools

- `validate_cpu0_telemetry_canary_csv.py`: 600 us b10 task canary; requires corrected
  sampled lateness >=350 us, one late COMMIT and overlap.
- `validate_cpu0_sampler_task_canary_csv.py`: 1000 us mid-second task canary; requires
  sampled lateness >=750 us, >=3 missed periods and no late COMMIT.
- `validate_cpu0_isr_canary_csv.py`: 1000 us same-level ISR canary; same >=750 us /
  >=3 missed-period requirement, with an underlying task name rather than lat_canary.

## TX trace completeness

The trace session now remains active while explicit post-run `HEALTH_STATUS_REQUEST`
packets are sent. Round 1 always requests every frozen participant; round 2 retries
only missing POST_RUN snapshots. The TX CSV is written only after these packets have
been captured.

## Build identity

Every 44-field POST_RUN row records the firmware ELF SHA8. Device ID still keys the
controller data model; IP address and COM-port changes do not change device identity.
