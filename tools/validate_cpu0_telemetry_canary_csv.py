#!/usr/bin/env python3
"""Validate one real ESP01 task-canary run through STATUS -> controller CSV."""
from __future__ import annotations
import argparse, csv
from pathlib import Path

def i(row, key):
    v=(row.get(key) or '').strip(); return int(v) if v else None

def main():
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
    checks={
        'STATUS_FIELD_COUNT_44': i(r,'StatusFieldCount')==44,
        'MONITOR_VALID': (r.get('Cpu0MonitorValid') or '').strip()=='1',
        'EVENT_COUNT_1': i(r,'Cpu0MonitorEventCount')==1,
        'WORST_350_TO_700_US': i(r,'Cpu0MonitorWorstUs') is not None and 350 <= i(r,'Cpu0MonitorWorstUs') <= 700,
        'WORST_TASK_LAT_CANARY': (r.get('Cpu0MonitorWorstTask') or '').strip()=='lat_canary',
        'COMMIT_LATE_COUNT_1': i(r,'Cpu0CommitLateCount')==1,
        'COMMIT_WORST_300_TO_600_US': i(r,'Cpu0CommitWorstUs') is not None and 300 <= i(r,'Cpu0CommitWorstUs') <= 600,
        'WRONG_CORE_0': i(r,'Cpu0WrongCoreCallbacks')==0,
        'OVERFLOW_0': i(r,'Cpu0MonitorOverflow')==0,
        'LEVEL_MATCH': (r.get('Cpu0InterruptLevelMatch') or '').strip()=='1',
        'MISSED_PERIODS_PRESENT': (i(r,'Cpu0MonitorMissedPeriods') or 0)>=1,
        'FIRMWARE_BUILD_ID_PRESENT': len((r.get('FirmwareBuildId') or '').strip())==8 and all(c in '0123456789abcdefABCDEF' for c in (r.get('FirmwareBuildId') or '').strip()),
        'SAMPLES_PRESENT': (i(r,'Cpu0MonitorSamples') or 0)>0,
    }
    for k,v in checks.items(): print(f'{k}={"PASS" if v else "FAIL"}')
    print(f'Cpu0MonitorSamples={i(r,"Cpu0MonitorSamples")}')
    print(f'Cpu0MonitorWorstUs={i(r,"Cpu0MonitorWorstUs")}')
    print(f'Cpu0CommitWorstUs={i(r,"Cpu0CommitWorstUs")}')
    print(f'CPU0_TELEMETRY_CANARY_PATH={"PASS" if all(checks.values()) else "FAIL"}')
    return 0 if all(checks.values()) else 1

if __name__=='__main__': raise SystemExit(main())
