#!/usr/bin/env python3
"""Inspect serial RTC warm-up history; controller STATUS gates the frozen fleet.

The serial check uses full 129-point fits, a >=600 s continuous history,
<=0.25 ppm endpoint movement and three latest RMS readings <=1 us. Trend and
package-temperature span are informational. RTC_QUAL has no DiagnosticFlags or
controller-session evidence, so this tool cannot authorize qualification START.
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
        if row.local_us < previous or row.accepted < rows[index - 1].accepted:
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


def inspect_history(rows, *, window_seconds=600.0, rate_limit=0.25, rms_limit=1.0,
                    fit_points=129, min_samples=3):
    # A lock/full-fit loss, SQW fault, reboot or >=48 s gap starts a new segment.
    segment = []
    previous = None
    for row in rows:
        if previous is not None and (
            row.local_us < previous.local_us or row.local_us - previous.local_us >= 48_000_000
            or row.accepted < previous.accepted
            or row.inferred_missing != previous.inferred_missing
            or row.holdover_entries != previous.holdover_entries):
            segment.clear()
        if row.state != 'LOCKED' or row.points != fit_points:
            segment.clear()
        else:
            segment.append(row)
        previous = row
    latest = rows[-1]
    cutoff = latest.local_us - int(window_seconds * 1_000_000)
    anchors = [r for r in segment if r.local_us <= cutoff]
    anchor = anchors[-1] if anchors else None
    window = [r for r in segment if anchor is None or r.local_us >= anchor.local_us]
    span = (latest.local_us - segment[0].local_us) / 1e6 if segment else 0.0
    delta = latest.rate_ppm - anchor.rate_ppm if anchor is not None else math.nan
    reasons = []
    if latest.state != 'LOCKED': reasons.append('NOT_LOCKED')
    if latest.points != fit_points: reasons.append('FIT_NOT_FULL')
    if anchor is None or len(window) < min_samples: reasons.append('HISTORY_PENDING')
    if not math.isfinite(delta) or abs(delta) > rate_limit + 1e-12:
        reasons.append(f'10-min dRate {delta:+.6f} ppm requires <= ±{rate_limit:.6f}')
    # Past RMS excursions keep the history; three consecutive good readings recover.
    if len(segment) < 3 or any(not math.isfinite(r.rms_us) or not 0 <= r.rms_us <= rms_limit
                                for r in segment[-3:]):
        reasons.append('FIT_UNSTABLE/FIT_POLLS_PENDING')
    if latest.queue_drops != 0: reasons.append('RTC_QUEUE_DROPS')
    if not latest.temp_valid: reasons.append('TEMPERATURE_UNAVAILABLE')
    if latest.sqw_core != 1: reasons.append('SQW_CORE')
    return dict(ready=not reasons, reasons=reasons, window=window, history_seconds=span,
                rate_delta=delta, trend=fit_slope_ppm_per_min([(r.local_us, r.rate_ppm) for r in window]))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('logs', nargs='+', type=Path)
    ap.add_argument('--window-seconds', type=float, default=600.0)
    ap.add_argument('--max-endpoint-rate-change-ppm', type=float, default=0.25)
    ap.add_argument('--required-fit-points', type=int, default=129)
    ap.add_argument('--max-fit-rms-us', type=float, default=1.0)
    ap.add_argument('--min-samples', type=int, default=3)
    # Compatibility options describe telemetry; neither overrides the controller gate.
    ap.add_argument('--max-rate-trend-ppm-per-min', type=float, default=0.005,
                    help='Informational trend band only; does not gate readiness')
    ap.add_argument('--max-temperature-span-c', type=float, default=0.25,
                    help='Informational temperature span only; does not gate readiness')
    ap.add_argument('--min-window-coverage', type=float, default=1.0,
                    help='Compatibility option: full requested window is always required')
    args = ap.parse_args()
    for key in ('window_seconds', 'max_endpoint_rate_change_ppm', 'max_fit_rms_us',
                'max_rate_trend_ppm_per_min'):
        if not math.isfinite(getattr(args, key)) or getattr(args, key) <= 0:
            ap.error(f'{key} must be finite and > 0')
    if not math.isfinite(args.max_temperature_span_c) or args.max_temperature_span_c < 0:
        ap.error('--max-temperature-span-c must be finite and >= 0')
    if not 0 < args.min_window_coverage <= 1: ap.error('--min-window-coverage must be in (0,1]')
    if args.required_fit_points < 1 or args.min_samples < 3:
        ap.error('--required-fit-points must be positive and --min-samples >=3')
    data = parse_logs(args.logs)
    if not data: raise SystemExit('No RTC_QUAL records found')
    all_ready = True
    for device, rows in sorted(data.items()):
        result = inspect_history(rows, window_seconds=args.window_seconds,
            rate_limit=args.max_endpoint_rate_change_ppm, rms_limit=args.max_fit_rms_us,
            fit_points=args.required_fit_points, min_samples=args.min_samples)
        window = result['window']
        temp_span = max((r.temp_c for r in window), default=math.nan) - min((r.temp_c for r in window), default=math.nan)
        ready = result['ready']; all_ready &= ready
        print(f"{device}: {'READY' if ready else 'WAIT'} history_s={result['history_seconds']:.1f} "
              f"samples={len(window)} dRate={result['rate_delta']:+.6f}ppm "
              f"RMS={rows[-1].rms_us:.3f}us points={rows[-1].points} "
              f"trend_info={result['trend']:+.6f}ppm/min temp_span_info={temp_span:.2f}C")
        if result['reasons']: print('  WAIT: ' + '; '.join(result['reasons']))
    print('SERIAL_WARMUP_CHECK=' + ('READY' if all_ready else 'WAIT'))
    print('QUALIFICATION_START1_GATE=USE_CONTROLLER_STATUS (all frozen devices, DiagnosticFlags and session evidence)')
    return 0 if all_ready else 2


if __name__ == '__main__':
    raise SystemExit(main())
