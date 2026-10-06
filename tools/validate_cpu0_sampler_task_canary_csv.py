#!/usr/bin/env python3
"""Validate the v6.23.15 1000 us mid-second task canary through STATUS -> CSV."""
from __future__ import annotations
import argparse, csv
from pathlib import Path

def iv(row, key):
    v=(row.get(key) or '').strip(); return int(v) if v else None

def main() -> int:
    ap=argparse.ArgumentParser()
    ap.add_argument('csv_file')
    ap.add_argument('--device', default='ESP01')
    args=ap.parse_args()
    rows=[]
    with Path(args.csv_file).open(newline='',encoding='utf-8-sig') as f:
        for r in csv.DictReader(f):
            if (r.get('DeviceId') or '').strip()==args.device and (r.get('Phase') or '').strip().upper()=='POST_RUN': rows.append(r)
    if not rows: raise SystemExit(f'No POST_RUN row for {args.device}')
    r=rows[-1]
    build=(r.get('FirmwareBuildId') or '').strip()
    checks={
        'STATUS_FIELD_COUNT_44': iv(r,'StatusFieldCount')==44,
        'MONITOR_VALID': (r.get('Cpu0MonitorValid') or '').strip()=='1',
        'EVENT_COUNT_1': iv(r,'Cpu0MonitorEventCount')==1,
        'WORST_AT_LEAST_750_US': (iv(r,'Cpu0MonitorWorstUs') or 0)>=750,
        'WORST_BELOW_1250_US': (iv(r,'Cpu0MonitorWorstUs') or 999999)<=1250,
        'WORST_TASK_LAT_CANARY': (r.get('Cpu0MonitorWorstTask') or '').strip()=='lat_canary',
        'MISSED_PERIODS_AT_LEAST_3': (iv(r,'Cpu0MonitorMissedPeriods') or 0)>=3,
        'NO_LATE_COMMIT': iv(r,'Cpu0CommitLateCount')==0 and iv(r,'Cpu0CommitWorstUs')==0,
        'NO_COMMIT_OVERLAP': (r.get('Cpu0CommitOverlap') or '').strip()=='0',
        'WRONG_CORE_0': iv(r,'Cpu0WrongCoreCallbacks')==0,
        'OVERFLOW_0': iv(r,'Cpu0MonitorOverflow')==0,
        'LEVEL_MATCH': (r.get('Cpu0InterruptLevelMatch') or '').strip()=='1',
        'FIRMWARE_BUILD_ID_PRESENT': len(build)==8 and all(c in '0123456789abcdefABCDEF' for c in build),
    }
    for k,v in checks.items(): print(f'{k}={"PASS" if v else "FAIL"}')
    for k in ('Cpu0MonitorSamples','Cpu0MonitorMissedPeriods','Cpu0MonitorWorstUs','Cpu0CommitWorstUs'):
        print(f'{k}={iv(r,k)}')
    ok=all(checks.values())
    print(f'CPU0_SAMPLER_1000US_TASK_CANARY={"PASS" if ok else "FAIL"}')
    return 0 if ok else 1
if __name__=='__main__': raise SystemExit(main())
