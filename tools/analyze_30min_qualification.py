#!/usr/bin/env python3
"""Analyze Factory Timer per-second analyzer capture and optional controller TX trace.

Designed for the DS3231 30-minute qualification run:
- proximity-pairs ESP01/ESP02 edges (not index pairing),
- finds START as the first edge followed by a 1 Hz run,
- excludes START from rate fits,
- robustly fits pairwise gap slope (analyzer drift cancels),
- computes per-device centered running-median residuals (±30 boundaries),
- detects per-device and common-mode late commits,
- correlates late commits with controller sends in master time relative to T*,
- fits lateness vs send lead; controller-caused events should approach slope -1.
"""
from __future__ import annotations

import argparse
import csv
import math
import re
import statistics
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

EDGE_RE = re.compile(
    r"ANZ\|EDGE\|(?P<run>\d+)\|(?P<trial>\d+)\|(?P<channel>\d+)\|(?P<device>ESP\d+)\|(?P<ts>\d+)"
)
RX_NEAR_RE = re.compile(
    r"RX_NEAR_BOUNDARY device=(?P<device>ESP\d+) .*?boundary=(?P<boundary>\d+) "
    r"lead_us=(?P<lead>\d+) .*?packet=(?P<packet>[^ ]+)"
)


@dataclass(frozen=True)
class Edge:
    device: str
    ts_us: int


@dataclass(frozen=True)
class Pair:
    a_us: int
    b_us: int

    @property
    def mean_us(self) -> float:
        return (self.a_us + self.b_us) / 2.0

    @property
    def gap_us(self) -> float:
        return float(self.a_us - self.b_us)


@dataclass(frozen=True)
class Fit:
    intercept: float
    slope: float
    slope_stderr: float
    n: int
    rejected: int = 0


def percentile(values: Sequence[float], p: float) -> float:
    if not values:
        return math.nan
    xs = sorted(values)
    if len(xs) == 1:
        return xs[0]
    pos = (len(xs) - 1) * p
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return xs[lo]
    frac = pos - lo
    return xs[lo] * (1.0 - frac) + xs[hi] * frac


def ols(x: Sequence[float], y: Sequence[float]) -> Fit:
    if len(x) != len(y) or len(x) < 2:
        raise ValueError("need at least two x/y points")
    n = len(x)
    mx = sum(x) / n
    my = sum(y) / n
    sxx = sum((v - mx) ** 2 for v in x)
    if sxx == 0:
        raise ValueError("zero x variance")
    sxy = sum((vx - mx) * (vy - my) for vx, vy in zip(x, y))
    slope = sxy / sxx
    intercept = my - slope * mx
    residuals = [vy - (intercept + slope * vx) for vx, vy in zip(x, y)]
    if n > 2:
        sigma2 = sum(r * r for r in residuals) / (n - 2)
        stderr = math.sqrt(sigma2 / sxx)
    else:
        stderr = math.nan
    return Fit(intercept, slope, stderr, n)


def robust_ols(x: Sequence[float], y: Sequence[float], min_threshold_us: float = 25.0) -> Fit:
    indices = list(range(len(x)))
    rejected_total = 0
    for _ in range(4):
        fit = ols([x[i] for i in indices], [y[i] for i in indices])
        residuals = [y[i] - (fit.intercept + fit.slope * x[i]) for i in indices]
        med = statistics.median(residuals)
        mad = statistics.median(abs(r - med) for r in residuals)
        sigma = 1.4826 * mad
        threshold = max(min_threshold_us, 6.0 * sigma)
        keep = [i for i, r in zip(indices, residuals) if abs(r - med) <= threshold]
        if len(keep) == len(indices) or len(keep) < 3:
            return Fit(fit.intercept, fit.slope, fit.slope_stderr, fit.n, rejected_total)
        rejected_total += len(indices) - len(keep)
        indices = keep
    fit = ols([x[i] for i in indices], [y[i] for i in indices])
    return Fit(fit.intercept, fit.slope, fit.slope_stderr, fit.n, rejected_total)


def parse_edges(path: Path) -> dict[str, list[int]]:
    result: dict[str, list[int]] = {}
    text = path.read_text(encoding="utf-8", errors="ignore")
    for m in EDGE_RE.finditer(text):
        result.setdefault(m.group("device"), []).append(int(m.group("ts")))
    for values in result.values():
        values.sort()
    return result


def pair_by_proximity(a: Sequence[int], b: Sequence[int], tolerance_us: int) -> tuple[list[Pair], list[int], list[int]]:
    i = j = 0
    pairs: list[Pair] = []
    orphan_a: list[int] = []
    orphan_b: list[int] = []
    while i < len(a) and j < len(b):
        da = a[i]
        db = b[j]
        delta = da - db
        if abs(delta) <= tolerance_us:
            pairs.append(Pair(da, db))
            i += 1
            j += 1
        elif da < db:
            orphan_a.append(da)
            i += 1
        else:
            orphan_b.append(db)
            j += 1
    orphan_a.extend(a[i:])
    orphan_b.extend(b[j:])
    return pairs, orphan_a, orphan_b


def find_start_index(pairs: Sequence[Pair]) -> int:
    # A RESET can produce a matched diagnostic edge before START. Identify START
    # as the first pair followed by at least three ~1-second intervals.
    if len(pairs) < 2:
        return 0
    needed = min(3, len(pairs) - 1)
    for s in range(0, len(pairs) - needed):
        good = True
        for k in range(s + 1, s + needed + 1):
            dt = pairs[k].mean_us - pairs[k - 1].mean_us
            if not 800_000 <= dt <= 1_200_000:
                good = False
                break
        if good:
            return s
    return 0


def centered_median_residuals(timestamps: Sequence[int], radius: int = 30) -> dict[int, float]:
    # timestamps correspond to boundaries k=1..N. Remove the nominal 1-second
    # grid first, then median-filter the slowly moving analyzer/device phase.
    phase = [float(ts - (idx + 1) * 1_000_000) for idx, ts in enumerate(timestamps)]
    out: dict[int, float] = {}
    if len(phase) < 2 * radius + 1:
        return out
    for i in range(radius, len(phase) - radius):
        baseline = statistics.median(phase[i - radius : i + radius + 1])
        # key is physical boundary number, START=0, first post-START boundary=1
        out[i + 1] = phase[i] - baseline
    return out




def parse_device_rx_logs(paths: Sequence[Path]) -> dict[str, dict[int, list[tuple[int, str]]]]:
    result: dict[str, dict[int, list[tuple[int, str]]]] = {}
    for path in paths:
        text = path.read_text(encoding="utf-8", errors="ignore")
        for m in RX_NEAR_RE.finditer(text):
            device = m.group("device")
            boundary = int(m.group("boundary"))
            lead = int(m.group("lead"))
            packet = m.group("packet")
            result.setdefault(device, {}).setdefault(boundary, []).append((lead, packet))
    return result

def parse_controller_csv(path: Path) -> tuple[list[dict[str, str]], int | None, int | None]:
    with path.open("r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return [], None, None
    tstar = int(rows[0]["TStarMasterUs"])
    duration = int(rows[0]["DurationSeconds"])
    return rows, tstar, duration


def fmt(v: float, digits: int = 3) -> str:
    return "nan" if math.isnan(v) else f"{v:.{digits}f}"


def summarize_residuals(name: str, residuals: dict[int, float], threshold_us: float) -> list[tuple[int, float]]:
    vals = list(residuals.values())
    late = sorted((k, r) for k, r in residuals.items() if r > threshold_us)
    if not vals:
        print(f"{name}: insufficient boundaries for ±30 running-median residuals")
        return late
    absvals = [abs(v) for v in vals]
    print(
        f"{name}: residual n={len(vals)} "
        f"P95|r|={fmt(percentile(absvals, .95),1)} us "
        f"P99|r|={fmt(percentile(absvals, .99),1)} us "
        f"max_late={fmt(max(vals),1)} us "
        f">1ms={sum(v > 1000 for v in vals)} "
        f">2ms={sum(v > 2000 for v in vals)} "
        f">5ms={sum(v > 5000 for v in vals)}"
    )
    return late


def segment_counts(events: Sequence[tuple[int, float]], duration: int) -> tuple[int, int, int]:
    a_end = duration / 3.0
    b_end = 2.0 * duration / 3.0
    a = b = c = 0
    for k, _ in events:
        if k <= a_end:
            a += 1
        elif k <= b_end:
            b += 1
        else:
            c += 1
    return a, b, c


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("analyzer_log", type=Path)
    ap.add_argument("--controller-csv", type=Path)
    ap.add_argument("--device-log", type=Path, action="append", default=[],
                    help="ESP serial log containing RX_NEAR_BOUNDARY records; repeat per device")
    ap.add_argument("--device-a", default="ESP01")
    ap.add_argument("--device-b", default="ESP02")
    ap.add_argument("--pair-tolerance-us", type=int, default=100_000)
    ap.add_argument("--late-threshold-us", type=float, default=1000.0)
    ap.add_argument("--tx-correlation-window-us", type=int, default=50_000)
    ap.add_argument("--events-csv", type=Path)
    args = ap.parse_args()

    edges = parse_edges(args.analyzer_log)
    a = edges.get(args.device_a, [])
    b = edges.get(args.device_b, [])
    if not a or not b:
        raise SystemExit(f"missing edges: {args.device_a}={len(a)} {args.device_b}={len(b)}")

    pairs, orphan_a, orphan_b = pair_by_proximity(a, b, args.pair_tolerance_us)
    if len(pairs) < 3:
        raise SystemExit("not enough matched edge pairs")
    start_idx = find_start_index(pairs)
    run_pairs = pairs[start_idx:]
    if len(run_pairs) < 3:
        raise SystemExit("not enough run pairs after START detection")

    print(f"edges: {args.device_a}={len(a)} {args.device_b}={len(b)}")
    print(f"paired={len(pairs)} orphans: {args.device_a}={len(orphan_a)} {args.device_b}={len(orphan_b)}")
    print(f"detected START pair index={start_idx}; run edges={len(run_pairs)} (START + {len(run_pairs)-1} boundaries)")
    print(f"START gap {args.device_a}-{args.device_b} = {run_pairs[0].gap_us:+.1f} us")

    steady = run_pairs[1:]
    x = [(p.mean_us - steady[0].mean_us) / 1_000_000.0 for p in steady]
    y = [p.gap_us for p in steady]
    pair_fit = robust_ols(x, y)
    print(
        f"pairwise robust gap fit: slope={pair_fit.slope:+.6f} ppm "
        f"white-noise_SE={pair_fit.slope_stderr:.6f} ppm "
        f"n={pair_fit.n} rejected={pair_fit.rejected}"
    )

    ts_a = [p.a_us for p in steady]
    ts_b = [p.b_us for p in steady]
    residual_a = centered_median_residuals(ts_a, 30)
    residual_b = centered_median_residuals(ts_b, 30)
    late_a = summarize_residuals(args.device_a, residual_a, args.late_threshold_us)
    late_b = summarize_residuals(args.device_b, residual_b, args.late_threshold_us)

    late_a_map = dict(late_a)
    late_b_map = dict(late_b)
    common_keys = sorted(set(late_a_map) & set(late_b_map))
    print(f"common late boundaries >{args.late_threshold_us:.0f} us: {len(common_keys)}")
    for k in common_keys[:20]:
        print(f"  k={k}: {args.device_a}={late_a_map[k]:+.1f} us {args.device_b}={late_b_map[k]:+.1f} us")

    rx_near = parse_device_rx_logs(args.device_log) if args.device_log else {}
    if rx_near:
        total_rx = sum(len(events) for by_boundary in rx_near.values() for events in by_boundary.values())
        print(f"device-side control-port RX-near-boundary records={total_rx}")
        for device, late_events in ((args.device_a, late_a), (args.device_b, late_b)):
            by_boundary = rx_near.get(device, {})
            correlated = []
            for k, lateness in late_events:
                arrivals = by_boundary.get(k, [])
                if arrivals:
                    lead, packet = min(arrivals, key=lambda item: item[0])
                    correlated.append((k, lateness, lead, packet))
            print(f"{device}: late events with device-side control RX in preceding 15 ms: {len(correlated)}/{len(late_events)}")
            for k, lateness, lead, packet in correlated[:30]:
                print(f"  k={k}: late={lateness:+.1f} us RX_lead={lead} us packet={packet}")

    controller_rows: list[dict[str, str]] = []
    duration = len(steady)
    if args.controller_csv:
        controller_rows, _, csv_duration = parse_controller_csv(args.controller_csv)
        if csv_duration is not None:
            duration = csv_duration
        print(f"controller TX rows={len(controller_rows)} duration={duration}s")

        correlated: list[tuple[str, int, float, float, str]] = []
        all_late = [(args.device_a, k, r) for k, r in late_a] + [(args.device_b, k, r) for k, r in late_b]
        for device, k, lateness in all_late:
            boundary_delta_us = k * 1_000_000
            candidates: list[tuple[int, dict[str, str]]] = []
            for row in controller_rows:
                send_delta = int(row["DeltaFromTStarUs"])
                lead = boundary_delta_us - send_delta
                if 0 <= lead <= args.tx_correlation_window_us:
                    candidates.append((lead, row))
            if candidates:
                lead, row = min(candidates, key=lambda item: item[0])
                correlated.append((device, k, lateness, float(lead), row["PacketType"]))

        print(
            f"late events with app TX in preceding {args.tx_correlation_window_us/1000:.1f} ms: "
            f"{len(correlated)}/{len(late_a)+len(late_b)}"
        )
        for device, k, lateness, lead, packet_type in correlated[:30]:
            print(f"  {device} k={k}: late={lateness:+.1f} us lead={lead:.0f} us type={packet_type}")

        if len(correlated) >= 3:
            lead_values = [c[3] for c in correlated]
            if max(lead_values) > min(lead_values):
                fit = ols(lead_values, [c[2] for c in correlated])
                print(
                    f"lateness vs app-send lead: slope={fit.slope:+.3f} "
                    f"(controller-cause prediction -1), intercept={fit.intercept:.1f} us, n={fit.n}"
                )
            else:
                print(
                    "lateness vs app-send lead: insufficient lead variation for slope fit "
                    f"(all correlated sends at {lead_values[0]:.0f} us lead)"
                )

        a1 = segment_counts(late_a, duration)
        b1 = segment_counts(late_b, duration)
        common_events = [(k, min(late_a_map[k], late_b_map[k])) for k in common_keys]
        c1 = segment_counts(common_events, duration)
        print(f"A/B/A thirds late counts {args.device_a}: {a1[0]}/{a1[1]}/{a1[2]}")
        print(f"A/B/A thirds late counts {args.device_b}: {b1[0]}/{b1[1]}/{b1[2]}")
        print(f"A/B/A thirds common late counts: {c1[0]}/{c1[1]}/{c1[2]}")

    if args.events_csv:
        with args.events_csv.open("w", encoding="utf-8", newline="") as f:
            w = csv.writer(f)
            w.writerow(["Boundary", f"{args.device_a}_ResidualUs", f"{args.device_b}_ResidualUs", "CommonLate"])
            keys = sorted(set(residual_a) | set(residual_b))
            for k in keys:
                ra = residual_a.get(k)
                rb = residual_b.get(k)
                common = ra is not None and rb is not None and ra > args.late_threshold_us and rb > args.late_threshold_us
                w.writerow([k, "" if ra is None else f"{ra:.3f}", "" if rb is None else f"{rb:.3f}", int(common)])
        print(f"events CSV: {args.events_csv}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
