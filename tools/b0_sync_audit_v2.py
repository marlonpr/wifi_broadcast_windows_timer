#!/usr/bin/env python3
"""
b0_sync_audit_v2.py - START (boundary 0) synchronization audit for the factory timer.

For each START found in the device UART logs and one Analyzer_v8 capture, compares the
analyzer's b0 COMMIT offsets (corrected for start-marker lateness) with what each device's
own SYNC trace predicts, and shows what a forward-only offset estimator with disciplined
sync->START propagation would have left at b0. No firmware change is needed to run it.

Per device (times in us, rates in ppm, ages in s):
  O_est      offset_master_minus_local_us the firmware used (four-timestamp estimator)
  O_fwd(Ls)  forward-only estimate: the largest (t1_master_us - ip_input_us) values, each
             moved to the sync epoch Ls with the disciplined rate, combined by --est
             = true offset - forward floor
  floor      O_est - O_fwd(Ls)     apparent forward floor under the firmware's own offset
  shift      (T* - L*) - O_fwd(L*) how much later the fixed build would have started, where
             O_fwd is carried to START with the disciplined rate
  prop       shift - floor         what the firmware's own sync->START propagation got wrong
             (rate * sync_age for raw esp_timer; ~0 once propagation is disciplined, so the
             script also works unchanged on the fixed build)
  age        L* - ip_input of the floor samples, in seconds

Device vs reference (the first device log given):
  predicted  shift_ref - shift_dev      b0 offset the current build should produce
  observed   analyzer b0 COMMIT offset, corrected for start-marker lateness
  fixed      observed - predicted       what the fixed build would have shown at b0

Across runs the script fits   fixed = c + y * (age_dev - age_ref)
  c  per-station forward-floor difference [us]
  y  rate error of the PC master clock against the DS3231s [ppm], + = PC fast
Alternating the sync order makes the age difference change sign, which separates c from y.
The summary repeats the fit for several floor estimators, so the series also picks one.

Usage:
  python b0_sync_audit_v2.py Analyzer_v8.log ESP01.log ESP02.log [more device logs]
         [--est mean3] [--gap-s 5] [--csv audit.csv]
"""
import argparse
import csv
import re
import statistics
import sys

KV = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)=(\S+)')
LOG_PREFIX = re.compile(r'[IWEDV] \(\d+\) ')


def warn(msg):
    print(f"warning: {msg}", file=sys.stderr)


def best_record(line, tag):
    """Serial captures sometimes print a truncated record followed by the full one on the
    same line. Split at every occurrence of tag (ending each fragment at the next ESP-IDF
    log prefix) and keep the most complete record."""
    best = {}
    for m in re.finditer(re.escape(tag), line):
        end = LOG_PREFIX.search(line, m.end())
        d = dict(KV.findall(line[m.start():end.start() if end else len(line)]))
        if len(d) > len(best):
            best = d
    return best


def num(d, key, cast=int):
    try:
        return cast(d[key])
    except (KeyError, ValueError):
        return None


# ----------------------------------------------------------------------------- device logs

def load_device(path):
    dev = None
    starts, sync_rate, quals, visual = {}, {}, [], {}
    trace_blocks, current_block = [], None
    loose_samples = {}

    with open(path, encoding='utf-8', errors='replace') as f:
        for line_no, line in enumerate(f, 1):
            if 'SYNC_REPLY_TRACE_BEGIN' in line:
                # Every post-run dump numbers the successful synchronization samples
                # 1..N again. Preserve the block instead of pooling nearby samples;
                # this prevents a retried synchronization from being mixed with the
                # successful attempt merely because both occurred within +/-3 s.
                if current_block:
                    trace_blocks.append(current_block)
                current_block = {}
                continue

            if 'SYNC_REPLY_TRACE_END' in line:
                if current_block:
                    trace_blocks.append(current_block)
                current_block = None
                continue

            if 'SYNC_REPLY_TRACE sample=' in line:
                d = best_record(line, 'SYNC_REPLY_TRACE sample=')
                t1, ip = num(d, 't1_master_us'), num(d, 'ip_input_us')
                sid = d.get('sync')
                m = re.search(r'SYNC_REPLY_TRACE sample=(\d+)', line)
                sample_no = int(m.group(1)) if m else None
                if t1 is not None and ip and sid:
                    rec = dict(t1=t1, ip=ip, sync=sid,
                               selected=num(d, 'selected') or 0,
                               sample_no=sample_no, line_no=line_no)
                    loose_samples[sid] = rec
                    if current_block is not None:
                        # Garbled serial output can duplicate a sample line. Keying by
                        # sample number keeps the most complete/latest reconstruction.
                        key = sample_no if sample_no is not None else sid
                        current_block[key] = rec
                continue

            if 'RTC_START_QUAL ' in line:
                d = best_record(line, 'RTC_START_QUAL ')
                keys = ('sync_local_us', 'offset_master_minus_local_us', 'best_rtt_us',
                        'target_master_us', 'actual_local_us')
                rec = {k: num(d, k) for k in keys}
                if None not in rec.values() and 'command' in d:
                    rec['sync'] = d.get('sync')
                    starts[d['command']] = rec
                    dev = d.get('device', dev)
            if 'RTC_SYNC_QUAL ' in line:
                d = best_record(line, 'RTC_SYNC_QUAL ')
                r = num(d, 'rate_ppm', float)
                if r is not None and 'sync' in d:
                    sync_rate[d['sync']] = r
                dev = d.get('device', dev)
            if 'RTC_QUAL device=' in line:
                d = best_record(line, 'RTC_QUAL device=')
                loc, r = num(d, 'local_us'), num(d, 'rate_ppm', float)
                if loc is not None and r is not None:
                    quals.append((loc, r))
                dev = d.get('device', dev)
            if 'Visual countdown started:' in line:
                d = best_record(line, 'Visual countdown started:')
                t, lat, dur = (num(d, 'target_local_us'),
                               num(d, 'start_isr_publish_lateness_us'), num(d, 'duration'))
                if None not in (t, lat, dur):
                    visual[t] = (lat, dur)

    if current_block:
        trace_blocks.append(current_block)
    if dev is None:
        sys.exit(f"{path}: no device name found (no RTC_*QUAL records)")
    quals.sort()

    # De-duplicate repeated post-run dumps by their correlation-ID set.
    unique_blocks, seen = [], set()
    for block in trace_blocks:
        rows = sorted(block.values(), key=lambda x: (x['sample_no'] or 10**9, x['line_no']))
        signature = tuple(r['sync'] for r in rows)
        if not signature or signature in seen:
            continue
        seen.add(signature)
        unique_blocks.append(rows)

    runs = {}
    for cmd, s in starts.items():
        Ls, Lstar = s['sync_local_us'], s['actual_local_us']
        rate = sync_rate.get(s['sync'])
        if rate is None:
            prior = [r for loc, r in quals if loc <= Lstar]
            if not prior:
                warn(f"{dev} {cmd}: no rate_ppm before START, skipped")
                continue
            rate = prior[-1]

        matching = [b for b in unique_blocks if any(r['sync'] == s['sync'] for r in b)]
        if len(matching) == 1:
            block = matching[0]
            smp = sorted((r['t1'], r['ip']) for r in block)
            selected_rows = [r for r in block if r['sync'] == s['sync']]
            if selected_rows and not any(r['selected'] == 1 for r in selected_rows):
                warn(f"{dev} {cmd}: selected sync ID is present but not marked selected=1")
        elif len(matching) > 1:
            warn(f"{dev} {cmd}: selected sync ID appears in {len(matching)} distinct trace blocks; skipped")
            continue
        else:
            # Backward-compatible fallback for older/incomplete captures without
            # BEGIN/END framing. Still warn so a qualification series cannot silently
            # mix adjacent retries.
            near = [r for r in loose_samples.values() if abs(r['ip'] - Ls) < 3_000_000]
            smp = sorted((r['t1'], r['ip']) for r in near)
            warn(f"{dev} {cmd}: no framed SYNC block contains selected ID {s['sync']}; "
                 f"falling back to {len(smp)} samples within +/-3 s")

        if len(smp) < 3:
            warn(f"{dev} {cmd}: only {len(smp)} SYNC samples for the selected synchronization, skipped")
            continue
        if len(smp) != 16:
            warn(f"{dev} {cmd}: selected synchronization has {len(smp)} samples (expected 16)")
        if Lstar not in visual:
            warn(f"{dev} {cmd}: no 'Visual countdown started' for target {Lstar}, skipped")
            continue
        lat, dur = visual[Lstar]
        runs[cmd] = dict(Ls=Ls, Lstar=Lstar, O_est=s['offset_master_minus_local_us'],
                         rtt=s['best_rtt_us'], tstar=s['target_master_us'], rate=rate,
                         lat=lat, dur=dur, samples=smp, first_t1=smp[0][0])
    return dev, runs


# ------------------------------------------------------------------- forward-only estimate

def est_mean(n):
    def f(v):
        s = v[:n]
        return sum(o for o, _ in s) / len(s), sum(ip for _, ip in s) / len(s)
    return f


def est_med3(v):
    return sorted(v[:3])[1]


ESTIMATORS = {'max': est_mean(1), 'mean2': est_mean(2), 'mean3': est_mean(3), 'med3': est_med3}


def forward(run, est):
    Ls, rate = run['Ls'], run['rate']
    # offset implied by each sample, moved to the sync epoch; largest = fastest delivery
    v = sorted((((t1 - ip) - rate * 1e-6 * (Ls - ip)), ip) for t1, ip in run['samples'])
    v.reverse()
    o_fwd, ip_ref = est(v)
    floor = run['O_est'] - o_fwd
    o_used = run['tstar'] - run['Lstar']                   # offset the firmware applied at START
    shift = o_used - (o_fwd - rate * 1e-6 * (run['Lstar'] - Ls))
    return dict(floor=floor, prop=shift - floor, shift=shift,
                age=(run['Lstar'] - ip_ref) / 1e6, spread=v[0][0] - v[2][0])


# ------------------------------------------------------------------------------- analyzer

def load_analyzer(path, devs, gap_s):
    commits, footer = {d: [] for d in devs}, None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.strip().split('|')
            if len(p) >= 8 and p[0] == 'ANZ' and p[1] == 'EDGE' and p[5].endswith('_COMMIT'):
                d = p[5][:-len('_COMMIT')]
                if d in commits:
                    commits[d].append(int(p[6]))
            elif len(p) >= 2 and p[0] == 'ANZ' and p[1] == 'COUNTS':
                footer = line.strip()
    segs = {}
    for d, ts in commits.items():
        ts.sort()
        out, cur = [], []
        for t in ts:
            if cur and t - cur[-1] > gap_s * 1e6:
                out.append(cur)
                cur = []
            cur.append(t)
        if cur:
            out.append(cur)
        segs[d] = [s for s in out if len(s) >= 10]      # drop stray pre-run markers
    ref, runs = devs[0], []
    for s in segs[ref]:
        run = {ref: s}
        for d in devs[1:]:
            cand = [x for x in segs[d] if abs(x[0] - s[0]) < 1_000_000]
            if len(cand) == 1:
                run[d] = cand[0]
        if len(run) == len(devs):
            runs.append(run)
    return runs, footer


def align(uruns, aruns, ref):
    # Never trust equal run counts by themselves. A stray analyzer segment can keep
    # the counts equal while shifting every pairing by one. Score possible shifts by
    # duration agreement and by inter-run timing agreement first, then by coverage.
    if not uruns or not aruns:
        return []

    tolerance_us = 500_000
    candidates = []
    for shift in range(-(len(uruns) - 1), len(aruns)):
        pairs = [(uruns[i], aruns[i + shift]) for i in range(len(uruns))
                 if 0 <= i + shift < len(aruns)]
        if not pairs:
            continue
        dur_bad = sum(1 for u, a in pairs if len(a[ref]) - 1 != u['dur'])
        gaps = [abs((a2[ref][0] - a1[ref][0]) - (u2['tstar'] - u1['tstar']))
                for (u1, a1), (u2, a2) in zip(pairs, pairs[1:])]
        gap_bad = sum(g > tolerance_us for g in gaps)
        mean_gap = round(statistics.mean(gaps)) if gaps else 0
        score = (dur_bad, gap_bad, mean_gap, -len(pairs), abs(shift))
        candidates.append((score, shift, pairs, gaps))

    candidates.sort(key=lambda x: x[0])
    best = candidates[0]
    ties = [x for x in candidates[1:] if x[0] == best[0]]
    if ties:
        warn("run alignment has an exact score tie; use one continuous analyzer capture and/or --command")
    if best[0][0] or best[0][1]:
        warn(f"best run alignment still has {best[0][0]} duration mismatch(es) and "
             f"{best[0][1]} inter-run gap mismatch(es) > {tolerance_us/1000:.0f} ms")
    if best[1] != 0 or len(best[2]) != min(len(uruns), len(aruns)):
        warn(f"run alignment selected shift {best[1]:+d}: paired {len(best[2])}/"
             f"{len(uruns)} UART STARTs with {len(aruns)} analyzer run(s)")
    elif len(uruns) == len(aruns):
        # Equal counts are accepted only after the checks above.
        if best[3] and max(best[3]) > tolerance_us:
            warn("equal run counts but inter-run timing disagrees; inspect alignment manually")
    return best[2]


# ------------------------------------------------------------------------ least squares

def invert(m):
    n = len(m)
    a = [row[:] + [1.0 if i == j else 0.0 for j in range(n)] for i, row in enumerate(m)]
    scale = max(abs(x) for row in m for x in row) or 1.0
    for c in range(n):
        p = max(range(c, n), key=lambda r: abs(a[r][c]))
        if abs(a[p][c]) < 1e-12 * scale:
            return None
        a[c], a[p] = a[p], a[c]
        piv = a[c][c]
        a[c] = [x / piv for x in a[c]]
        for r in range(n):
            if r != c and a[r][c]:
                f = a[r][c]
                a[r] = [x - f * y for x, y in zip(a[r], a[c])]
    return [row[n:] for row in a]


def fit(points, others, with_rate=True):
    """points: (dev, age_diff, fixed). Model: fixed = c_dev + y*age_diff."""
    rows = [([1.0 if d == o else 0.0 for o in others] + ([dage] if with_rate else []), val)
            for d, dage, val in points]
    p = len(others) + (1 if with_rate else 0)
    if len(rows) < p:
        return None
    xtx = [[sum(r[0][i] * r[0][j] for r in rows) for j in range(p)] for i in range(p)]
    inv = invert(xtx)
    if inv is None:
        return fit(points, others, with_rate=False) if with_rate else None
    xty = [sum(r[0][i] * r[1] for r in rows) for i in range(p)]
    beta = [sum(inv[i][j] * xty[j] for j in range(p)) for i in range(p)]
    res = [r[1] - sum(b * x for b, x in zip(beta, r[0])) for r in rows]
    dof = len(rows) - p
    s2 = sum(e * e for e in res) / dof if dof > 0 else float('nan')
    se = [(s2 * inv[i][i]) ** 0.5 if dof > 0 else float('nan') for i in range(p)]
    rms = (sum(e * e for e in res) / len(res)) ** 0.5
    return dict(c=dict(zip(others, zip(beta, se))),
                y=(beta[-1], se[-1]) if with_rate else None, rms=rms, n=len(rows), dof=dof)


def pm(v, e, unit):
    return f"{v:+.1f} {unit}" if e != e else f"{v:+.1f} ± {e:.1f} {unit}"


# ----------------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('analyzer', help='Analyzer_v8 capture covering the runs')
    ap.add_argument('logs', nargs='+', help='device UART logs; the first is the reference')
    ap.add_argument('--est', choices=list(ESTIMATORS), default='mean3',
                    help='floor estimator used in the per-run tables (default mean3)')
    ap.add_argument('--gap-s', type=float, default=5.0,
                    help='COMMIT gap that separates runs in the capture (default 5 s)')
    ap.add_argument('--command', action='append',
                    help='only audit this START command ID (repeatable); useful when the '
                         'analyzer capture covers fewer runs than the UART logs')
    ap.add_argument('--csv', help='write one row per run and device')
    a = ap.parse_args()
    if len(a.logs) < 2:
        ap.error('need at least two device logs')

    devlogs = [load_device(p) for p in a.logs]
    devs = [d for d, _ in devlogs]
    if len(set(devs)) != len(devs):
        sys.exit(f"duplicate device names in logs: {devs}")
    ref, others = devs[0], devs[1:]
    cmds = set.intersection(*(set(r) for _, r in devlogs))
    if a.command:
        cmds &= set(a.command)
    uruns = sorted(({'cmd': c, 'tstar': devlogs[0][1][c]['tstar'], 'dur': devlogs[0][1][c]['dur'],
                     'dev': {d: r[c] for d, r in devlogs}} for c in cmds),
                   key=lambda u: u['tstar'])
    aruns, footer = load_analyzer(a.analyzer, devs, a.gap_s)
    if not uruns or not aruns:
        sys.exit(f"nothing to audit: {len(uruns)} STARTs in all logs, {len(aruns)} analyzer runs")
    pairs = align(uruns, aruns, ref)
    if footer:
        print(footer)

    fitdata = {name: [] for name in ESTIMATORS}
    summary = {d: {'obs': [], 'fixed': []} for d in others}
    csv_rows = []
    for k, (u, ar) in enumerate(pairs, 1):
        order = sorted(devs, key=lambda d: u['dev'][d]['first_t1'])
        print(f"\nRun {k}  command {u['cmd']}  duration {u['dur']} s  "
              f"sync order {' -> '.join(order)}")
        if len(ar[ref]) - 1 != u['dur']:
            warn(f"run {k}: analyzer shows {len(ar[ref])} commits for a {u['dur']} s countdown")
        b0 = {d: ar[d][0] - u['dev'][d]['lat'] for d in devs}
        fw = {name: {d: forward(u['dev'][d], fn) for d in devs}
              for name, fn in ESTIMATORS.items()}
        f = fw[a.est]
        print(f"  {'device':7} {'best_rtt':>8} {'n':>3} {'top3 spread':>11} {'floor':>8} "
              f"{'prop':>7} {'shift':>8} {'age':>7} {'lateness':>8}")
        for d in devs:
            r, x = u['dev'][d], f[d]
            print(f"  {d:7} {r['rtt']:8d} {len(r['samples']):3d} {x['spread']:11.0f} "
                  f"{x['floor']:8.1f} {x['prop']:+7.1f} {x['shift']:8.1f} {x['age']:6.2f}s "
                  f"{r['lat']:8d}")
        for d in devs:
            row = dict(run=k, command=u['cmd'], duration_s=u['dur'], device=d, reference=ref,
                       sync_rank=order.index(d) + 1, best_rtt_us=u['dev'][d]['rtt'],
                       n_samples=len(u['dev'][d]['samples']), rate_ppm=u['dev'][d]['rate'],
                       o_est_us=u['dev'][d]['O_est'], lateness_us=u['dev'][d]['lat'],
                       b0_analyzer_us=ar[d][0],
                       **{f"{key}_{name}": round(fw[name][d][key], 2)
                          for name in ESTIMATORS for key in ('floor', 'shift', 'age')})
            if d != ref:
                obs = b0[d] - b0[ref]
                pred = f[ref]['shift'] - f[d]['shift']
                fixed = obs - pred
                dage = f[d]['age'] - f[ref]['age']
                print(f"  {d} - {ref} at b0: observed {obs:+.0f}  predicted {pred:+.0f}  "
                      f"fixed build {fixed:+.0f} us   (floor-age difference {dage:+.2f} s)")
                summary[d]['obs'].append(obs)
                summary[d]['fixed'].append(fixed)
                row.update(observed_vs_ref_us=obs, predicted_vs_ref_us=round(pred, 1),
                           fixed_vs_ref_us=round(fixed, 1), age_diff_s=round(dage, 3))
                for name in ESTIMATORS:
                    g = fw[name]
                    fitdata[name].append((d, g[d]['age'] - g[ref]['age'],
                                          obs - (g[ref]['shift'] - g[d]['shift'])))
            csv_rows.append(row)

    print(f"\nSummary over {len(pairs)} run(s), estimator {a.est}")
    for d in others:
        o, fx = summary[d]['obs'], summary[d]['fixed']
        rms = (sum(x * x for x in fx) / len(fx)) ** 0.5
        print(f"  {d} - {ref}: observed b0 {min(o):+.0f} .. {max(o):+.0f} us;  "
              f"fixed build mean {statistics.mean(fx):+.1f}, rms {rms:.1f}, "
              f"max |{max(abs(x) for x in fx):.0f}| us")

    print("\nFit  fixed = c + y * (age_dev - age_ref)   (c: floor difference, y: PC clock rate)")
    for name in ESTIMATORS:
        r = fit(fitdata[name], others)
        if r is None:
            print(f"  {name:6} not enough runs")
            continue
        cs = ',  '.join(f"c[{d}] {pm(v, e, 'us')}" for d, (v, e) in r['c'].items())
        ys = (f"y {pm(r['y'][0], r['y'][1], 'ppm')}" if r['y'] else
              "y not separable (floor-age difference never varied: alternate the sync order)")
        note = "" if r['dof'] > 0 else "  (exactly determined, no error estimate)"
        print(f"  {name:6} {cs},  {ys},  scatter rms {r['rms']:.1f} us{note}")

    if a.csv and csv_rows:
        keys = list(dict.fromkeys(k for r in csv_rows for k in r))
        with open(a.csv, 'w', newline='') as fh:
            w = csv.DictWriter(fh, fieldnames=keys)
            w.writeheader()
            w.writerows(csv_rows)
        print(f"\nwrote {len(csv_rows)} rows to {a.csv}")


if __name__ == '__main__':
    main()
