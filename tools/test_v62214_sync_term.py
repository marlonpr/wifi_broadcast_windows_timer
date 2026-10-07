#!/usr/bin/env python3
"""v6.22.14: two-START sync-noise term in the RTC-vs-Analyzer qualification validator.

Synthetic physics: three RTC rates, sync errors injected at the second START
(ESP03 starts 64 us late, ESP04 20 us early, as large as the 2026-10-07 data),
an Analyzer clock 20 ppm off the controller, a 2401-boundary long train at the
first START and a 31-boundary train at the second START.
"""
import csv
from pathlib import Path
import subprocess
import sys
import tempfile
from analyzer_v10 import Edge

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / 'tools/validate_rtc_qualification_vs_analyzer.py'
DEVICES = ['ESP01', 'ESP03', 'ESP04']
RATES = {'ESP01': 0.30, 'ESP03': -0.45, 'ESP04': 0.80}          # disciplined minus master, ppm
SYNC_ERR = [{'ESP01': 0, 'ESP03': 0, 'ESP04': 0},                  # e_X,k: error in master-disciplined
            {'ESP01': 0, 'ESP03': -64, 'ESP04': 20}]               # -> physical START offset is -e
ANALYZER_PPM = 20.0
T1 = 100_000_000

def write_health(folder, index, tstar, reported_rates):
    rows = []
    for dev in DEVICES:
        epoch = tstar - 5_000_000
        q = 50_000 - round(reported_rates[dev] * (epoch - T1 + 5_000_000) / 1e6) + SYNC_ERR[index][dev]
        rows.append(dict(RunCommandId=f'{index+1:016X}', TStarMasterUs=tstar, DeviceId=dev, Phase='START',
            StatusCaptured=1, CapturedMasterUs=tstar + 100_000, RtcState='LOCKED',
            SyncEpochMasterUs=epoch, SyncEpochLocalUs=epoch + 7_000_000,
            SyncEpochMasterMinusDisciplinedUs=q, RtcRatePpmVsRtc=0, RtcFitPoints=129, RtcFitRmsUs=0.5,
            RtcFitOutliers=0, RtcAcceptedEdges=1000 + (tstar + 100_000) // 1_000_000,
            RtcInferredMissingEdges=0, RtcHoldoverEntries=0, RtcQueueDrops=0, RtcTemperatureValid=1,
            RtcTemperatureC=25, RtcSqwCore=1, HealthFlags=1))
    path = folder / f'start{index}.csv'
    with path.open('w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0])); w.writeheader(); w.writerows(rows)
    return path

def write_capture(folder, t2):
    # The long train must end before the second START: a board runs one countdown at a time.
    long_count = min(2401, (t2 - T1) // 1_000_000 - 30)
    edges = []
    for k, (tstar, count) in enumerate([(T1, long_count), (t2, 31)]):
        for n in range(count):
            for ch, dev in enumerate(DEVICES):
                master = tstar - SYNC_ERR[k][dev] + n * (1_000_000 - RATES[dev])
                edges.append(Edge(1, 1, ch, dev + '_COMMIT', round(master * (1 + ANALYZER_PPM * 1e-6)), (n + 1) % 2))
    edges.sort(key=lambda e: e.timestamp)
    path = folder / 'analyzer.txt'
    path.write_text('\n'.join(f'ANZ|QUALIFIED|1|1|{e.channel+1}|{e.name}|q={i+1}|{e.timestamp}|{e.level}'
                              for i, e in enumerate(edges)) + '\n')
    return path

def run(folder, interval_s, reported=RATES, *extra):
    t2 = T1 + int(interval_s * 1_000_000)
    args = [sys.executable, str(TOOL), str(write_health(folder, 0, T1, reported)),
            str(write_health(folder, 1, t2, reported)), str(write_capture(folder, t2)), '--warm-confirmed', *extra]
    r = subprocess.run(args, text=True, capture_output=True)
    return r.returncode, r.stdout + r.stderr

def rows_for(out, pair):
    return [line.split(',') for line in out.splitlines() if f',{pair},' in line]

with tempfile.TemporaryDirectory() as td:
    folder = Path(td)
    # 1) The old 1200 s interval is now rejected by default.
    code, out = run(folder, 1200)
    assert code != 0 and 'interval shorter than --min-interval-seconds' in out, out
    # 2) Explicit 1200 s: raw error = 64/1200 = 0.0533 ppm -> FAIL; measured sync term explains it.
    code, out = run(folder, 1200, RATES, '--min-interval-seconds', '1200', '--min-boundaries', '1100')
    assert code == 2 and 'RESULT=FAIL' in out and 'SYNC_TERM_SOURCE=ANALYZER_STARTS' in out, out
    r = rows_for(out, 'ESP01-ESP03')[0]
    assert abs(float(r[9]) - 64 / 1200) < 0.002 and r[11] == 'FAIL', r
    assert (int(r[12]), int(r[13])) == (0, 64) and abs(float(r[14]) - 64 / 1200) < 1e-4, r
    assert abs(float(r[15])) < 0.002, r
    code, out = run(folder, 1200, RATES, '--min-interval-seconds', '1200', '--min-boundaries', '1100',
                    '--gate', 'sync-corrected')
    assert code == 0 and 'RESULT=PASS' in out and 'GATE=sync-corrected' in out, out
    # 3) Bracketing interval: raw passes, but the budget warning shows half the margin is sync noise.
    code, out = run(folder, 2460)
    assert code == 0 and 'RESULT=PASS' in out and 'SYNC_TERM_WARNING=' in out, out
    assert abs(float(rows_for(out, 'ESP01-ESP03')[0][14]) - 64 / 2460) < 1e-4
    assert abs(float(rows_for(out, 'ESP03-ESP04')[0][14]) - (-84) / 2460) < 1e-4
    # 4) A genuine 0.06 ppm RTC telemetry error fails under either gate.
    wrong = {**RATES, 'ESP04': RATES['ESP04'] + 0.06}
    for gate in ('raw', 'sync-corrected'):
        code, out = run(folder, 2460, wrong, '--gate', gate)
        assert code == 2 and 'RESULT=FAIL' in out, (gate, out)
        assert all(abs(float(x[15])) > 0.05 for p in ('ESP01-ESP04', 'ESP03-ESP04') for x in rows_for(out, p))
    # 5) Without the second START train in the capture, the term is reported unavailable.
    t2 = T1 + 2_460_000_000
    health = [write_health(folder, 0, T1, RATES), write_health(folder, 1, t2, RATES)]
    lines = [l for l in write_capture(folder, t2).read_text().splitlines() if int(l.split('|')[7]) < t2]
    (folder / 'long_only.txt').write_text('\n'.join(lines) + '\n')
    r = subprocess.run([sys.executable, str(TOOL), *map(str, health), str(folder / 'long_only.txt'), '--warm-confirmed'],
                       text=True, capture_output=True)
    assert r.returncode == 0 and 'SYNC_TERM_SOURCE=UNAVAILABLE' in r.stdout, r.stdout + r.stderr
print('PASS: 1200 s START interval rejected by default; measured START-offset term recovers the injected '
      '64/84 us sync errors (raw 0.053 ppm FAIL explained); 2460 s passes with a budget warning; a real '
      '0.06 ppm telemetry error fails under both gates; missing second START reported as unavailable')
