#!/usr/bin/env python3
"""Summarize actual acceptance-to-completion CPU0 monitor windows.

RUN_DIAG provides the grid period, elapsed time and first-deadline phase.
For the free-running grid the exact endpoint convention is (start, end]:
expected = 0 if elapsed < first_offset else 1 + (elapsed-first_offset)//period.
This differs from floor(elapsed/251) by at most one, independent of countdown
duration. Missing window diagnostics cannot qualify as clean exposure.
"""
from __future__ import annotations

import argparse
import csv
import glob
import math
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

EXPECTED_SAMPLE_RATE_HZ = 1_000_000.0 / 251.0
CURRENT_STATUS_FIELD_COUNT = 44
SEVERE_EVENT_US = 300
ZERO_EVENT_95_COUNT = -math.log(0.05)

@dataclass
class DeviceSummary:
    runs: int = 0
    monitor_samples: int = 0
    clean_monitor_samples: int = 0
    missed_periods: int = 0
    missed_period_rows: int = 0
    monitor_events: int = 0
    worst_monitor_us: int = 0
    worst_monitor_task: str = "NONE"
    severe_ge300_observed: bool = False  # actual COMMIT delays, not sampler lateness
    commit_late_events: int = 0
    worst_commit_us: int = 0
    overlap_runs: int = 0
    wrong_core_callbacks: int = 0
    overflows: int = 0
    invalid_monitor_rows: int = 0
    level_mismatch_rows: int = 0
    legacy_status_rows: int = 0
    sample_rate_warn_rows: int = 0
    canary_contamination_rows: int = 0
    build_id_warn_rows: int = 0
    monitor_elapsed_us: int = 0
    clean_monitor_elapsed_us: int = 0
    expected_periods: int = 0
    commit_ge300_events: int = 0
    build_ids: set[str] = field(default_factory=set)


def as_int(row: dict[str, str], key: str) -> int:
    value = (row.get(key) or "").strip()
    return int(value) if value else 0


def as_bool(row: dict[str, str], key: str) -> bool | None:
    value = (row.get(key) or "").strip()
    if value == "1": return True
    if value == "0": return False
    return None


def valid_build_id(value: str) -> bool:
    value = value.strip()
    return len(value) == 8 and all(c in "0123456789abcdefABCDEF" for c in value)


def expand_inputs(patterns: list[str]) -> list[Path]:
    found: list[Path] = []
    seen: set[Path] = set()
    for pattern in patterns:
        matches = [Path(p) for p in glob.glob(pattern)]
        if not matches and Path(pattern).exists(): matches = [Path(pattern)]
        for path in matches:
            resolved = path.resolve()
            if resolved not in seen:
                seen.add(resolved); found.append(path)
    return sorted(found)


def zero_event_bound(hours: float, zero_severe: bool) -> tuple[str, str]:
    if not zero_severe or hours <= 0.0: return "N/A", "N/A"
    return f"{ZERO_EVENT_95_COUNT / hours:.6f}", f"{hours / ZERO_EVENT_95_COUNT:.3f}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+", help="controller_health CSV files or glob patterns")
    ap.add_argument("--sample-rate-tolerance-pct", type=float, default=0.5)
    ap.add_argument("--closure-fleet-hours", type=float, default=60.0)
    ap.add_argument("--watch-device", default="ESP02")
    ap.add_argument("--watch-device-min-hours", type=float, default=4.0)
    args = ap.parse_args()

    paths = expand_inputs(args.files)
    if not paths: raise SystemExit("No matching controller health CSV files")

    required = {
        "DeviceId", "Phase", "DurationSeconds", "StatusFieldCount",
        "Cpu0MonitorValid", "Cpu0MonitorSamples", "Cpu0MonitorEventCount",
        "Cpu0MonitorWorstUs", "Cpu0MonitorWorstTask", "Cpu0CommitLateCount",
        "Cpu0CommitWorstUs", "Cpu0CommitOverlap", "Cpu0WrongCoreCallbacks",
        "Cpu0MonitorOverflow", "Cpu0InterruptLevelMatch",
    }
    by_device: dict[str, DeviceSummary] = defaultdict(DeviceSummary)
    total_rows = 0

    for path in paths:
        with path.open(newline="", encoding="utf-8-sig") as f:
            reader = csv.DictReader(f)
            missing = required.difference(reader.fieldnames or [])
            if missing:
                raise SystemExit(f"{path}: missing fleet-monitor column(s): {', '.join(sorted(missing))}")
            for row in reader:
                if (row.get("Phase") or "").strip().upper() != "POST_RUN": continue
                device = (row.get("DeviceId") or "").strip()
                if not device: continue
                total_rows += 1
                out = by_device[device]; out.runs += 1

                field_count = as_int(row, "StatusFieldCount")
                legacy = field_count < CURRENT_STATUS_FIELD_COUNT
                if legacy: out.legacy_status_rows += 1

                valid = as_bool(row, "Cpu0MonitorValid")
                if valid is not True:
                    out.invalid_monitor_rows += 1
                    continue

                samples = as_int(row, "Cpu0MonitorSamples")
                missed = as_int(row, "Cpu0MonitorMissedPeriods") if "Cpu0MonitorMissedPeriods" in row else 0
                out.monitor_samples += samples
                out.missed_periods += missed
                if missed > 0: out.missed_period_rows += 1
                period = as_int(row, "Cpu0MonitorPeriodUs")
                elapsed = as_int(row, "MonitorElapsedUs")
                start = as_int(row, "MonitorStartUs")
                end = as_int(row, "MonitorEndUs")
                offset = as_int(row, "FirstAlarmOffsetUs")
                expected = as_int(row, "Cpu0MonitorExpectedPeriods")
                grid = 0 if elapsed < offset or period <= 0 else 1 + (elapsed - offset) // period
                window_valid = (
                    as_bool(row, "RunDiagnosticCaptured") is True and
                    as_bool(row, "RunDiagnosticValid") is True and
                    period == 251 and as_int(row, "Cpu0MonitorThresholdUs") == 50 and
                    elapsed > 0 and end - start == elapsed and 1 <= offset <= period and
                    expected == grid and as_int(row, "MonitorRearmFailures") == 0 and
                    as_int(row, "Cpu0EventsGe50Us") == as_int(row, "Cpu0MonitorEventCount") and
                    as_int(row, "CommitLateEvents") == as_int(row, "Cpu0CommitLateCount")
                )
                # A stop can precede service of the last due hardware deadline.
                # One terminal deadline is allowed; swallowed interior periods
                # remain explicitly represented by MissedPeriods.
                rate_warn = not window_valid or abs(samples + missed - expected) > 1
                if window_valid:
                    out.monitor_elapsed_us += elapsed
                    out.expected_periods += expected
                if rate_warn: out.sample_rate_warn_rows += 1

                level_ok = as_bool(row, "Cpu0InterruptLevelMatch") is True
                if not level_ok: out.level_mismatch_rows += 1
                wrong = as_int(row, "Cpu0WrongCoreCallbacks"); out.wrong_core_callbacks += wrong
                overflow = as_int(row, "Cpu0MonitorOverflow"); out.overflows += overflow

                events = as_int(row, "Cpu0MonitorEventCount")
                worst = as_int(row, "Cpu0MonitorWorstUs")
                task = (row.get("Cpu0MonitorWorstTask") or "NONE").strip() or "NONE"
                canary = task == "lat_canary"
                if canary: out.canary_contamination_rows += 1
                else:
                    out.monitor_events += events

                if worst > out.worst_monitor_us:
                    out.worst_monitor_us = worst; out.worst_monitor_task = task
                out.commit_late_events += as_int(row, "Cpu0CommitLateCount")
                commit_worst = as_int(row, "Cpu0CommitWorstUs")
                out.worst_commit_us = max(out.worst_commit_us, commit_worst)
                commit_ge300 = as_int(row, "CommitGe300Us")
                out.commit_ge300_events += commit_ge300
                if not canary and (commit_ge300 > 0 or commit_worst >= SEVERE_EVENT_US):
                    out.severe_ge300_observed = True
                if as_bool(row, "Cpu0CommitOverlap") is True: out.overlap_runs += 1

                build_id = (row.get("FirmwareBuildId") or "").strip()
                build_ok = valid_build_id(build_id)
                if build_ok: out.build_ids.add(build_id.lower())
                else: out.build_id_warn_rows += 1

                row_clean = (
                    not legacy and not canary and not rate_warn and missed == 0 and level_ok and
                    wrong == 0 and build_ok
                )
                if row_clean:
                    out.clean_monitor_samples += samples
                    out.clean_monitor_elapsed_us += elapsed

    header = (
        "DEVICE,RUNS,CLEAN_MON_HOURS,OBS_RATE_HZ,GRID_RATE_HZ,MISSED_PERIODS,MON_EVENTS,"
        "WORST_MON_US,WORST_TASK,GE300_OBSERVED,ZERO_GE300_95_UPPER_PER_HOUR,"
        "ZERO_GE300_95_LOWER_MEAN_HOURS,COMMIT_LATE,WORST_COMMIT_US,OVERLAP_RUNS,"
        "WRONG_CORE,OVERFLOW,INVALID,LEVEL_MISMATCH,LEGACY_STATUS,SAMPLE_RATE_WARN,"
        "LAT_CANARY_ROWS,MISSED_PERIOD_ROWS,BUILD_ID_WARN,BUILD_IDS")
    print(header)

    totals = DeviceSummary()
    fleet_worst = 0; fleet_worst_device = "NONE"; fleet_severe = False
    for device in sorted(by_device):
        s = by_device[device]
        for name in (
            'runs','monitor_samples','clean_monitor_samples','missed_periods','missed_period_rows','monitor_events',
            'commit_late_events','overlap_runs','wrong_core_callbacks','overflows',
            'invalid_monitor_rows','level_mismatch_rows','legacy_status_rows',
            'sample_rate_warn_rows','canary_contamination_rows','build_id_warn_rows',
            'monitor_elapsed_us','clean_monitor_elapsed_us','expected_periods','commit_ge300_events'):
            setattr(totals, name, getattr(totals, name) + getattr(s, name))
        fleet_severe |= s.severe_ge300_observed
        if s.worst_monitor_us > fleet_worst:
            fleet_worst = s.worst_monitor_us; fleet_worst_device = device
        clean_hours = s.clean_monitor_elapsed_us / 3_600_000_000.0
        observed_rate = s.monitor_samples * 1_000_000.0 / s.monitor_elapsed_us if s.monitor_elapsed_us else 0.0
        expected_periods = s.expected_periods
        grid_rate = expected_periods * 1_000_000.0 / s.monitor_elapsed_us if s.monitor_elapsed_us else 0.0
        upper, mean_hours = zero_event_bound(clean_hours, not s.severe_ge300_observed)
        print(
            f"{device},{s.runs},{clean_hours:.6f},{observed_rate:.3f},{grid_rate:.3f},"
            f"{s.missed_periods},{s.monitor_events},{s.worst_monitor_us},{s.worst_monitor_task},"
            f"{1 if s.severe_ge300_observed else 0},{upper},{mean_hours},{s.commit_late_events},"
            f"{s.worst_commit_us},{s.overlap_runs},{s.wrong_core_callbacks},{s.overflows},"
            f"{s.invalid_monitor_rows},{s.level_mismatch_rows},{s.legacy_status_rows},"
            f"{s.sample_rate_warn_rows},{s.canary_contamination_rows},{s.missed_period_rows},{s.build_id_warn_rows},"
            f"{'|'.join(sorted(s.build_ids)) if s.build_ids else 'NONE'}")

    clean_hours = totals.clean_monitor_elapsed_us / 3_600_000_000.0
    fleet_upper, fleet_mean = zero_event_bound(clean_hours, not fleet_severe)
    telemetry_clean = (
        totals.legacy_status_rows == 0 and totals.sample_rate_warn_rows == 0 and
        totals.canary_contamination_rows == 0 and totals.missed_period_rows == 0 and totals.invalid_monitor_rows == 0 and
        totals.level_mismatch_rows == 0 and totals.wrong_core_callbacks == 0 and
        totals.build_id_warn_rows == 0
    )
    watch = by_device.get(args.watch_device)
    watch_hours = watch.clean_monitor_elapsed_us / 3_600_000_000.0 if watch else 0.0
    watch_clean = bool(watch) and not watch.severe_ge300_observed and watch.canary_contamination_rows == 0
    closure_pass = (
        telemetry_clean and not fleet_severe and clean_hours >= args.closure_fleet_hours and
        watch_clean and watch_hours >= args.watch_device_min_hours
    )

    print(f"FLEET_POST_RUN_ROWS={total_rows}")
    print(f"FLEET_MONITOR_SAMPLES={totals.monitor_samples}")
    print(f"FLEET_MONITOR_MISSED_PERIODS={totals.missed_periods}")
    print(f"FLEET_MONITOR_EXPECTED_PERIODS={totals.expected_periods}")
    print(f"FLEET_CLEAN_MONITOR_SAMPLES={totals.clean_monitor_samples}")
    print(f"FLEET_CLEAN_MONITORED_HOURS={clean_hours:.6f}")
    print(f"FLEET_NATURAL_MONITOR_EVENTS_GE50={totals.monitor_events}")
    print(f"FLEET_GE300_OBSERVED={1 if fleet_severe else 0}")
    print(f"FLEET_ZERO_GE300_95_UPPER_PER_HOUR={fleet_upper}")
    print(f"FLEET_ZERO_GE300_95_LOWER_MEAN_HOURS={fleet_mean}")
    print(f"FLEET_COMMIT_LATE_EVENTS={totals.commit_late_events}")
    print(f"FLEET_COMMIT_GE300_EVENTS={totals.commit_ge300_events}")
    print(f"FLEET_COMMIT_TIMING={'FAIL' if fleet_severe else 'PASS'}")
    print(f"FLEET_MONITOR_ELAPSED_US={totals.monitor_elapsed_us}")
    print(f"FLEET_WORST_MONITOR_US={fleet_worst}")
    print(f"FLEET_WORST_MONITOR_DEVICE={fleet_worst_device}")
    print(f"FLEET_LEGACY_STATUS_ROWS={totals.legacy_status_rows}")
    print(f"FLEET_SAMPLE_RATE_WARN_ROWS={totals.sample_rate_warn_rows}")
    print(f"FLEET_LAT_CANARY_ROWS={totals.canary_contamination_rows}")
    print(f"FLEET_MISSED_PERIOD_ROWS={totals.missed_period_rows}")
    print(f"FLEET_BUILD_ID_WARN_ROWS={totals.build_id_warn_rows}")
    print(f"FLEET_INVALID_MONITOR_ROWS={totals.invalid_monitor_rows}")
    print(f"FLEET_LEVEL_MISMATCH_ROWS={totals.level_mismatch_rows}")
    print(f"FLEET_WRONG_CORE_CALLBACKS={totals.wrong_core_callbacks}")
    print(f"FLEET_MONITOR_OVERFLOW={totals.overflows}")
    print(f"FLEET_TELEMETRY_HEALTH={'PASS' if telemetry_clean else 'WARN'}")
    print(f"CLOSURE_REQUIRED_FLEET_HOURS={args.closure_fleet_hours:.3f}")
    print(f"CLOSURE_WATCH_DEVICE={args.watch_device}")
    print(f"CLOSURE_WATCH_DEVICE_CLEAN_HOURS={watch_hours:.6f}")
    print(f"CLOSURE_REQUIRED_WATCH_DEVICE_HOURS={args.watch_device_min_hours:.3f}")
    print(f"ZERO_GE300_ROLLOUT_ITEM={'PASS' if closure_pass else 'OPEN'}")
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
