#!/usr/bin/env python3
"""Validate the v6.23.15 1000 us same-level ISR canary through STATUS -> CSV."""
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
    task=(r.get('Cpu0MonitorWorstTask') or '').strip()
    build=(r.get('FirmwareBuildId') or '').strip()
    checks={
        'STATUS_FIELD_COUNT_44': iv(r,'StatusFieldCount')==44,
        'MONITOR_VALID': (r.get('Cpu0MonitorValid') or '').strip()=='1',
        'EVENT_COUNT_1': iv(r,'Cpu0MonitorEventCount')==1,
        'WORST_AT_LEAST_749_US': (iv(r,'Cpu0MonitorWorstUs') or 0)>=749,
        'WORST_BELOW_1250_US': (iv(r,'Cpu0MonitorWorstUs') or 999999)<=1250,
        'UNDERLYING_TASK_PRESENT': bool(task) and task not in ('NONE','lat_canary'),
        'MISSED_PERIODS_AT_LEAST_3': (iv(r,'Cpu0MonitorMissedPeriods') or 0)>=3,
         'NO_COMMIT_GE300': (iv(r,'Cpu0CommitWorstUs') or 0)<300 and (iv(r,'CommitGe300Us') or 0)==0,
        'WRONG_CORE_0': iv(r,'Cpu0WrongCoreCallbacks')==0,
        'OVERFLOW_0': iv(r,'Cpu0MonitorOverflow')==0,
        'LEVEL_MATCH': (r.get('Cpu0InterruptLevelMatch') or '').strip()=='1',
        'FIRMWARE_BUILD_ID_PRESENT': len(build)==8 and all(c in '0123456789abcdefABCDEF' for c in build),
    }
    for k,v in checks.items(): print(f'{k}={"PASS" if v else "FAIL"}')
    print(f'Cpu0MonitorWorstTask={task}')
    for k in ('Cpu0MonitorSamples','Cpu0MonitorMissedPeriods','Cpu0MonitorWorstUs','Cpu0CommitWorstUs'):
        print(f'{k}={iv(r,k)}')
    print(f"COMMIT_OVERLAP_TELEMETRY={r.get('Cpu0CommitOverlap', '')}")
    ok=all(checks.values())
    print(f'CPU0_SAMPLER_1000US_ISR_CANARY={"PASS" if ok else "FAIL"}')
    return 0 if ok else 1
if __name__=='__main__': raise SystemExit(main())
