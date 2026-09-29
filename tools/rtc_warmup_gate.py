#!/usr/bin/env python3
"""Objectively gate DS3231 qualification on RTC_QUAL rate stability."""
from __future__ import annotations

import argparse
import math
import re
from collections import defaultdict
from pathlib import Path

RTC_RE = re.compile(
    r"RTC_QUAL device=(?P<device>ESP\d+) .*?local_us=(?P<local>\d+) .*?state=(?P<state>\w+) "
    r"rate_ppm=(?P<rate>[+-]?\d+(?:\.\d+)?) .*?queue_drops=(?P<drops>\d+) .*?"
    r"temp_valid=(?P<tempvalid>[01]) temp_c=(?P<temp>[+-]?\d+(?:\.\d+)?)"
)


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


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("logs", nargs="+", type=Path)
    ap.add_argument("--window-seconds", type=float, default=180.0)
    ap.add_argument("--threshold-ppm-per-min", type=float, default=0.01)
    ap.add_argument("--min-samples", type=int, default=8)
    args = ap.parse_args()

    data: dict[str, list[tuple[int, float, str, int, float]]] = defaultdict(list)
    for path in args.logs:
        text = path.read_text(encoding="utf-8", errors="ignore")
        for m in RTC_RE.finditer(text):
            data[m.group("device")].append((
                int(m.group("local")),
                float(m.group("rate")),
                m.group("state"),
                int(m.group("drops")),
                float(m.group("temp")),
            ))

    if not data:
        raise SystemExit("No RTC_QUAL records found")

    all_ready = True
    for device in sorted(data):
        rows = sorted(data[device])
        latest_us = rows[-1][0]
        cutoff = latest_us - int(args.window_seconds * 1_000_000)
        window = [r for r in rows if r[0] >= cutoff]
        points = [(r[0], r[1]) for r in window]
        slope = fit_slope_ppm_per_min(points)
        locked = all(r[2] == "LOCKED" for r in window)
        zero_drops = all(r[3] == 0 for r in window)
        enough = len(window) >= args.min_samples
        stable = enough and math.isfinite(slope) and abs(slope) < args.threshold_ppm_per_min
        ready = locked and zero_drops and stable
        all_ready &= ready
        rate_span = max((r[1] for r in window), default=math.nan) - min((r[1] for r in window), default=math.nan)
        print(
            f"{device}: {'READY' if ready else 'WAIT'} samples={len(window)} "
            f"slope={slope:+.5f} ppm/min threshold=±{args.threshold_ppm_per_min:.5f} "
            f"rate_span={rate_span:.5f} ppm state={'LOCKED' if locked else 'NOT_LOCKED'} "
            f"queue_drops={'0' if zero_drops else 'NONZERO'} temp={window[-1][4] if window else math.nan:.2f}C"
        )

    print("QUALIFICATION_GATE=" + ("READY" if all_ready else "WAIT"))
    return 0 if all_ready else 2


if __name__ == "__main__":
    raise SystemExit(main())
