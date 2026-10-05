#!/usr/bin/env python3
"""Objective START #1 readiness gate for DS3231 qualification.

The gate is intentionally stricter than the later two-START validity guard.  Its
job is to predict that the existing |START2 rate - START1 rate| <= 0.25 ppm guard
will still hold after a roughly 40 minute qualification cycle.

Default readiness requirements over the latest 10 minute monotonic-boot window:
  * RTC state LOCKED for every retained sample;
  * 129-point fit available throughout the retained window;
  * fit RMS <= 3 us, zero fit outliers, zero inferred-missing edges,
    zero holdover entries, zero SQW queue drops, valid temperature, SQW core 1;
  * |OLS rate trend| <= 0.005 ppm/min;
  * |last rate - first rate| <= 0.05 ppm;
  * DS3231 package-temperature range <= 0.25 C (one register LSB);
  * at least 95% of the requested window covered and at least 30 samples.

The input is one or more ESP-IDF serial-monitor text logs containing RTC_QUAL
records.  Multiple files may be supplied; the latest monotonic local-time segment
for each device is used so an older boot cannot make a restarted device look warm.
"""
from __future__ import annotations

import argparse
import math
import re
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

RTC_RE = re.compile(
    r"RTC_QUAL device=(?P<device>ESP\d+) .*?local_us=(?P<local>\d+) .*?"
    r"state=(?P<state>\w+) rate_ppm=(?P<rate>[+-]?\d+(?:\.\d+)?) "
    r"rms_us=(?P<rms>[+-]?\d+(?:\.\d+)?) points=(?P<points>\d+) "
    r"accepted=(?P<accepted>\d+) rejected=(?P<rejected>\d+) "
    r"fit_outliers=(?P<fit_outliers>\d+) .*?"
    r"inferred_missing=(?P<missing>\d+) holdover_entries=(?P<holdover>\d+) "
    r"queue_drops=(?P<drops>\d+) temp_valid=(?P<tempvalid>[01]) "
    r"temp_c=(?P<temp>[+-]?\d+(?:\.\d+)?) sqw_core=(?P<sqw_core>-?\d+)"
)


@dataclass(frozen=True)
class Sample:
    local_us: int
    rate_ppm: float
    state: str
    rms_us: float
    points: int
    accepted: int
    rejected: int
    fit_outliers: int
    inferred_missing: int
    holdover_entries: int
    queue_drops: int
    temp_valid: bool
    temp_c: float
    sqw_core: int


def fit_slope_ppm_per_min(points: list[tuple[int, float]]) -> float:
    if len(points) < 2:
        return math.nan
    x0 = points[0][0]
    x = [(t - x0) / 1_000_000.0 for t, _ in points]
    y = [v for _, v in points]
    mx = sum(x) / len(x)
    my = sum(y) / len(y)
    sxx = sum((v - mx) ** 2 for v in x)
    if sxx == 0:
        return math.nan
    slope_per_s = sum((vx - mx) * (vy - my) for vx, vy in zip(x, y)) / sxx
    return slope_per_s * 60.0


def latest_monotonic_segment(rows: list[Sample]) -> list[Sample]:
    """Keep only the newest boot/session when raw local time moved backwards."""
    if not rows:
        return []
    start = 0
    previous = rows[0].local_us
    for index, row in enumerate(rows[1:], start=1):
        if row.local_us < previous:
            start = index
        previous = row.local_us
    return rows[start:]


def parse_logs(paths: list[Path]) -> dict[str, list[Sample]]:
    data: dict[str, list[Sample]] = defaultdict(list)
    for path in paths:
        text = path.read_text(encoding="utf-8", errors="ignore")
        for m in RTC_RE.finditer(text):
            data[m.group("device")].append(
                Sample(
                    local_us=int(m.group("local")),
                    rate_ppm=float(m.group("rate")),
                    state=m.group("state"),
                    rms_us=float(m.group("rms")),
                    points=int(m.group("points")),
                    accepted=int(m.group("accepted")),
                    rejected=int(m.group("rejected")),
                    fit_outliers=int(m.group("fit_outliers")),
                    inferred_missing=int(m.group("missing")),
                    holdover_entries=int(m.group("holdover")),
                    queue_drops=int(m.group("drops")),
                    temp_valid=m.group("tempvalid") == "1",
                    temp_c=float(m.group("temp")),
                    sqw_core=int(m.group("sqw_core")),
                )
            )
    return {device: latest_monotonic_segment(rows) for device, rows in data.items()}


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Gate DS3231 qualification START #1 on 10-minute rate and temperature stability."
    )
    ap.add_argument("logs", nargs="+", type=Path)
    ap.add_argument("--window-seconds", type=float, default=600.0)
    ap.add_argument("--max-rate-trend-ppm-per-min", type=float, default=0.005)
    ap.add_argument("--max-endpoint-rate-change-ppm", type=float, default=0.05)
    ap.add_argument("--max-temperature-span-c", type=float, default=0.25)
    ap.add_argument("--min-window-coverage", type=float, default=0.95)
    ap.add_argument("--min-samples", type=int, default=30)
    ap.add_argument("--required-fit-points", type=int, default=129)
    ap.add_argument("--max-fit-rms-us", type=float, default=3.0)
    args = ap.parse_args()

    if args.window_seconds <= 0:
        ap.error("--window-seconds must be > 0")
    if args.max_rate_trend_ppm_per_min <= 0:
        ap.error("--max-rate-trend-ppm-per-min must be > 0")
    if args.max_endpoint_rate_change_ppm <= 0:
        ap.error("--max-endpoint-rate-change-ppm must be > 0")
    if args.max_temperature_span_c < 0:
        ap.error("--max-temperature-span-c must be >= 0")
    if not (0 < args.min_window_coverage <= 1):
        ap.error("--min-window-coverage must be in (0, 1]")
    if args.min_samples < 2:
        ap.error("--min-samples must be >= 2")
    if args.required_fit_points < 1:
        ap.error("--required-fit-points must be >= 1")
    if args.max_fit_rms_us <= 0:
        ap.error("--max-fit-rms-us must be > 0")

    data = parse_logs(args.logs)
    if not data:
        raise SystemExit("No RTC_QUAL records found")

    all_ready = True
    for device in sorted(data):
        rows = data[device]
        if not rows:
            continue
        latest_us = rows[-1].local_us
        cutoff = latest_us - int(args.window_seconds * 1_000_000)
        window = [r for r in rows if r.local_us >= cutoff]

        points = [(r.local_us, r.rate_ppm) for r in window]
        slope = fit_slope_ppm_per_min(points)
        coverage_seconds = (
            (window[-1].local_us - window[0].local_us) / 1_000_000.0
            if len(window) >= 2
            else 0.0
        )
        required_coverage_seconds = args.window_seconds * args.min_window_coverage
        endpoint_rate_delta = (
            window[-1].rate_ppm - window[0].rate_ppm if len(window) >= 2 else math.nan
        )
        rate_span = (
            max(r.rate_ppm for r in window) - min(r.rate_ppm for r in window)
            if window
            else math.nan
        )
        temp_span = (
            max(r.temp_c for r in window) - min(r.temp_c for r in window)
            if window and all(r.temp_valid for r in window)
            else math.nan
        )

        enough_samples = len(window) >= args.min_samples
        enough_coverage = coverage_seconds >= required_coverage_seconds
        locked = bool(window) and all(r.state == "LOCKED" for r in window)
        fit_points_ok = bool(window) and all(r.points >= args.required_fit_points for r in window)
        fit_rms_ok = bool(window) and all(
            math.isfinite(r.rms_us) and r.rms_us <= args.max_fit_rms_us for r in window
        )
        fit_outliers_ok = bool(window) and all(r.fit_outliers == 0 for r in window)
        missing_ok = bool(window) and all(r.inferred_missing == 0 for r in window)
        holdover_ok = bool(window) and all(r.holdover_entries == 0 for r in window)
        zero_drops = bool(window) and all(r.queue_drops == 0 for r in window)
        temp_valid = bool(window) and all(r.temp_valid for r in window)
        sqw_core_ok = bool(window) and all(r.sqw_core == 1 for r in window)
        trend_ok = (
            math.isfinite(slope)
            and abs(slope) <= args.max_rate_trend_ppm_per_min
        )
        endpoint_rate_ok = (
            math.isfinite(endpoint_rate_delta)
            and abs(endpoint_rate_delta) <= args.max_endpoint_rate_change_ppm
        )
        temp_stable = (
            math.isfinite(temp_span)
            and temp_span <= args.max_temperature_span_c + 1e-12
        )

        ready = all(
            (
                enough_samples,
                enough_coverage,
                locked,
                fit_points_ok,
                fit_rms_ok,
                fit_outliers_ok,
                missing_ok,
                holdover_ok,
                zero_drops,
                temp_valid,
                sqw_core_ok,
                trend_ok,
                endpoint_rate_ok,
                temp_stable,
            )
        )
        all_ready &= ready

        failures: list[str] = []
        if not enough_samples:
            failures.append(f"samples {len(window)}/{args.min_samples}")
        if not enough_coverage:
            failures.append(
                f"coverage {coverage_seconds:.1f}/{required_coverage_seconds:.1f}s"
            )
        if not locked:
            failures.append("RTC not continuously LOCKED")
        if not fit_points_ok:
            failures.append(f"fit points < {args.required_fit_points}")
        if not fit_rms_ok:
            failures.append(f"fit RMS > {args.max_fit_rms_us:.1f} us")
        if not fit_outliers_ok:
            failures.append("fit_outliers != 0")
        if not missing_ok:
            failures.append("inferred_missing != 0")
        if not holdover_ok:
            failures.append("holdover_entries != 0")
        if not zero_drops:
            failures.append("queue_drops != 0")
        if not temp_valid:
            failures.append("temperature invalid")
        if not sqw_core_ok:
            failures.append("SQW core != 1")
        if not trend_ok:
            failures.append(
                f"rate trend {slope:+.6f} ppm/min exceeds ±{args.max_rate_trend_ppm_per_min:.6f}"
            )
        if not endpoint_rate_ok:
            failures.append(
                f"10-min dRate {endpoint_rate_delta:+.6f} ppm exceeds ±{args.max_endpoint_rate_change_ppm:.6f}"
            )
        if not temp_stable:
            failures.append(
                f"temperature span {temp_span:.2f} C exceeds {args.max_temperature_span_c:.2f} C"
            )

        print(
            f"{device}: {'READY' if ready else 'WAIT'} "
            f"samples={len(window)} coverage_s={coverage_seconds:.1f} "
            f"trend={slope:+.6f}ppm/min "
            f"dRate={endpoint_rate_delta:+.6f}ppm rate_span={rate_span:.6f}ppm "
            f"temp_span={temp_span:.2f}C temp_now={window[-1].temp_c if window else math.nan:.2f}C "
            f"points_min={min((r.points for r in window), default=0)} "
            f"rms_max={max((r.rms_us for r in window), default=math.nan):.3f}us"
        )
        if failures:
            print("  WAIT: " + "; ".join(failures))

    print("QUALIFICATION_START1_GATE=" + ("READY" if all_ready else "WAIT"))
    return 0 if all_ready else 2


if __name__ == "__main__":
    raise SystemExit(main())
