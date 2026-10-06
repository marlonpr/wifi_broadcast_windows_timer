# v6.22.11 — fleet rollout hardening

The controller protocol and 42-field STATUS mapping are unchanged from v6.22.10. Changes are in validation and fleet-analysis tooling.

## Real canary CSV validation

`tools/validate_cpu0_telemetry_canary_csv.py` now treats sampled `Cpu0MonitorWorstUs` as phase-dependent and accepts 100..600 us. It still requires:

- 42-field STATUS,
- monitor valid,
- exactly one monitor event,
- `Cpu0MonitorWorstTask=lat_canary`,
- exactly one late COMMIT,
- `Cpu0CommitWorstUs` in 300..600 us,
- `Cpu0CommitOverlap=1`,
- zero wrong-core callbacks and overflow,
- matching sampler/COMMIT interrupt levels.

## Fleet exposure and zero-event bound

Exposure is always `Cpu0MonitorSamples / 4000 / 3600` hours. `DurationSeconds` is only a sample-rate cross-check.

For zero natural events at or above 300 us, the one-sided 95% Poisson upper rate is:

`-ln(0.05) / monitored_hours = 2.995732 / monitored_hours`.

The fleet summary prints this bound and its reciprocal lower mean interval for every device and fleet-wide. If any run reports `Cpu0MonitorWorstUs >= 300`, the zero-event bound is reported as N/A.

## Predefined closure gate

The rollout item closes only when all are true:

1. at least 60.0 clean fleet monitored board-hours,
2. no natural >=300 us monitor event,
3. at least 4.0 clean monitored hours on ESP02,
4. no legacy STATUS row,
5. no sample-rate warning,
6. no `lat_canary` contamination,
7. no invalid monitor row, interrupt-level mismatch, wrong-core callback, or overflow.

At exactly 60 clean board-hours with zero >=300 us events, the fleet-wide 95% upper rate is 0.049929/hour, equivalent to a lower mean interval of 20.028 board-hours/event.
