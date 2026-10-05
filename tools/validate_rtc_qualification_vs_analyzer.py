#!/usr/bin/env python3
"""Physically validate two-START RTC qualification against Analyzer_v8 COMMIT drift.

Recommended procedure:
  1. With the fleet warm, capture START #1 health CSV and start a 30:00 countdown.
  2. Keep Analyzer_v8 running for the full countdown.
  3. When the 30:00 run finishes, perform START #2 and save its health CSV.
  4. Run this tool with both health CSVs and the analyzer log.

For analyzer pair phase y = commit_B - commit_A, the expected phase slope is

    analyzer_ppm ~= rate_A - rate_B

where rate_X is the qualification tool's DisciplinedMinusMasterPpm.
"""
from __future__ import annotations

import argparse
import math
import re
import statistics
from pathlib import Path

from qualify_rtc_batch import build_pair_row, read_start_rows

EDGE_RE = re.compile(r"^ANZ\|EDGE\|[^|]*\|[^|]*\|[^|]*\|(?P<name>[^|]+)\|(?P<ts>\d+)\|")
COUNTS_RE = re.compile(r"^ANZ\|COUNTS\|.*\|dropped=(?P<dropped>\d+)\s*$")


def split_1hz_clusters(values: list[int]) -> list[list[int]]:
    if not values:
        return []
    clusters=[[values[0]]]
    for t in values[1:]:
        gap=t-clusters[-1][-1]
        if 500_000 <= gap <= 1_500_000:
            clusters[-1].append(t)
        else:
            clusters.append([t])
    return clusters


def longest_cluster(values: list[int]) -> list[int]:
    clusters=split_1hz_clusters(values)
    return max(clusters, key=len) if clusters else []


def linear_fit(x: list[float], y: list[float]) -> tuple[float,float,float]:
    if len(x) != len(y) or len(x) < 3:
        raise ValueError("need at least 3 paired analyzer boundaries")
    xm=statistics.fmean(x); ym=statistics.fmean(y)
    sxx=sum((v-xm)**2 for v in x)
    if sxx == 0:
        raise ValueError("degenerate analyzer time axis")
    slope=sum((a-xm)*(b-ym) for a,b in zip(x,y))/sxx
    intercept=ym-slope*xm
    residuals=[b-(intercept+slope*a) for a,b in zip(x,y)]
    rms=math.sqrt(statistics.fmean(r*r for r in residuals))
    return slope,intercept,rms


def main() -> int:
    ap=argparse.ArgumentParser(description="Compare two-START disciplined-rate qualification with Analyzer_v8 physical COMMIT drift.")
    ap.add_argument("first_health_csv", type=Path)
    ap.add_argument("second_health_csv", type=Path)
    ap.add_argument("analyzer_log", type=Path)
    ap.add_argument("--device-a", default="ESP01")
    ap.add_argument("--device-b", default="ESP02")
    ap.add_argument("--tolerance-ppm", type=float, default=0.05)
    ap.add_argument("--warm-confirmed", action="store_true", help="Confirm both qualification STARTs were taken after thermal warm-up. Required.")
    ap.add_argument("--max-rate-change-ppm", type=float, default=0.25, help="Automatic endpoint rate-stability guard (default 0.25 ppm).")
    ap.add_argument("--fit-lag-seconds", type=float, default=64.0, help="Effective fit-window center lag used for bias reporting (default 64 s).")
    ap.add_argument("--min-boundaries", type=int, default=1200, help="Minimum physical boundaries required (default 1200; 1801 expected for 30:00).")
    args=ap.parse_args()
    if args.tolerance_ppm <= 0: ap.error("--tolerance-ppm must be > 0")
    if args.max_rate_change_ppm <= 0: ap.error("--max-rate-change-ppm must be > 0")
    if args.fit_lag_seconds < 0: ap.error("--fit-lag-seconds must be >= 0")
    if not args.warm_confirmed: ap.error("--warm-confirmed is required for physical qualification validation")
    if args.min_boundaries < 3: ap.error("--min-boundaries must be >= 3")

    first=read_start_rows(args.first_health_csv)
    second=read_start_rows(args.second_health_csv)
    for dev in (args.device_a,args.device_b):
        if dev not in first or dev not in second:
            raise SystemExit(f"Missing captured START health for {dev} in one of the two CSVs")

    rows={}
    for dev in (args.device_a,args.device_b):
        rows[dev]=build_pair_row(
            first[dev], second[dev],
            min_interval_seconds=1200.0,
            same_boot_tolerance_ppm=100.0,
            accepted_edge_tolerance=2.0,
            warm_confirmed=args.warm_confirmed,
            max_rate_change_ppm=args.max_rate_change_ppm,
            fit_lag_seconds=args.fit_lag_seconds,
        )
        if not rows[dev]["QualificationValid"]:
            raise SystemExit(f"{dev}: qualification validity guards failed; do not compare to analyzer")

    edges={args.device_a:[], args.device_b:[]}
    dropped=None
    for raw in args.analyzer_log.read_text(encoding="utf-8", errors="replace").splitlines():
        m=EDGE_RE.match(raw.strip())
        if m:
            name=m.group("name")
            for dev in edges:
                if name == f"{dev}_COMMIT":
                    edges[dev].append(int(m.group("ts")))
        c=COUNTS_RE.match(raw.strip())
        if c:
            dropped=int(c.group("dropped"))

    ca=longest_cluster(edges[args.device_a])
    cb=longest_cluster(edges[args.device_b])
    if dropped not in (None,0):
        raise SystemExit(f"Analyzer reports dropped={dropped}; physical validation is invalid")
    if min(len(ca),len(cb)) < args.min_boundaries:
        raise SystemExit(f"Analyzer longest 1-Hz clusters are too short: {args.device_a}={len(ca)}, {args.device_b}={len(cb)}")
    if len(ca) != len(cb):
        raise SystemExit(f"Analyzer COMMIT cluster length mismatch: {args.device_a}={len(ca)}, {args.device_b}={len(cb)}")

    # The two channels are already within milliseconds, so ordinal pairing is
    # unambiguous inside a contiguous 1-Hz cluster.
    x=[((a+b)/2 - (ca[0]+cb[0])/2)/1_000_000.0 for a,b in zip(ca,cb)]
    y=[float(b-a) for a,b in zip(ca,cb)]
    analyzer_slope_ppm, intercept_us, rms_us=linear_fit(x,y)

    rate_a=float(rows[args.device_a]["DisciplinedMinusMasterPpm"])
    rate_b=float(rows[args.device_b]["DisciplinedMinusMasterPpm"])
    expected_pair_ppm=rate_a-rate_b
    error_ppm=expected_pair_ppm-analyzer_slope_ppm
    passed=abs(error_ppm) <= args.tolerance_ppm

    print(f"ANALYZER_BOUNDARIES={len(x)}")
    print(f"ANALYZER_{args.device_b}_MINUS_{args.device_a}_START_US={y[0]:+.3f}")
    print(f"ANALYZER_PAIR_SLOPE_PPM={analyzer_slope_ppm:+.6f}")
    print(f"ANALYZER_DETRENDED_RMS_US={rms_us:.3f}")
    print(f"TOOL_{args.device_a}_DISCIPLINED_MINUS_MASTER_PPM={rate_a:+.6f}")
    print(f"TOOL_{args.device_a}_RATE_DELTA_PPM={float(rows[args.device_a]['RtcRateDeltaPpm']):+.6f}")
    print(f"TOOL_{args.device_a}_EST_FIT_LAG_BIAS_PPM={float(rows[args.device_a]['EstimatedFitLagSlopeBiasPpm']):+.6f}")
    print(f"TOOL_{args.device_b}_DISCIPLINED_MINUS_MASTER_PPM={rate_b:+.6f}")
    print(f"TOOL_{args.device_b}_RATE_DELTA_PPM={float(rows[args.device_b]['RtcRateDeltaPpm']):+.6f}")
    print(f"TOOL_{args.device_b}_EST_FIT_LAG_BIAS_PPM={float(rows[args.device_b]['EstimatedFitLagSlopeBiasPpm']):+.6f}")
    print(f"TOOL_EXPECTED_{args.device_b}_MINUS_{args.device_a}_PHASE_SLOPE_PPM={expected_pair_ppm:+.6f}")
    print(f"TOOL_MINUS_ANALYZER_ERROR_PPM={error_ppm:+.6f}")
    print(f"TOLERANCE_PPM={args.tolerance_ppm:.6f}")
    print(f"RESULT={'PASS' if passed else 'FAIL'}")
    return 0 if passed else 2


if __name__ == "__main__":
    raise SystemExit(main())
