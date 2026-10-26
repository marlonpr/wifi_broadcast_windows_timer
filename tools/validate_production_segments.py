#!/usr/bin/env python3
"""Check complete V10 segment captures against the production 30 ms COMMIT gate.

This verifies three wired boards. Visible skew and the remaining boards require
installation measurements. The engineering 100 us START verdict is reported,
but is not the production gate. An END footer is mandatory for every trial.
"""
import argparse
import csv
from pathlib import Path
import re
import sys

ANALYZER_TOOLS = Path(__file__).resolve().parents[2] / 'analyzer_v10_1' / 'tools'
sys.path.insert(0, str(ANALYZER_TOOLS))
from analyzer_v10 import load_capture

ASSOCIATION_WINDOW_US = 40_000
COMMIT_LIMIT_US = 30_000

FOOTER = re.compile(r'ANZ\|(SUMMARY|COUNTS|TRIPWIRE)\|(\d+)\|(\d+)\|(.+)')

def evaluate(path, lengths, devices):
    replay = load_capture(path, prefer_qualified=True, train_min=970000,
                          train_max=1030000, associate_us=ASSOCIATION_WINDOW_US)
    errors = list(replay.errors)
    absent = []
    footers = {}
    for line in path.read_text(encoding='utf-8-sig', errors='replace').splitlines():
        match = FOOTER.search(line)
        if match:
            kind, run, trial, tail = match.groups()
            key = (int(run), int(trial))
            if kind in footers.setdefault(key, {}):
                errors.append(f'duplicate {kind} footer for trial {key}')
            footers[key][kind] = dict(p.split('=', 1) for p in tail.split('|') if '=' in p)
    trains = list(replay.trains().items())
    sessions = {(g.run, g.trial) for g in replay.groups}
    if not trains:
        errors.append('no complete segment trains')
    for key in sessions:
        footer = footers.get(key, {})
        for kind in ('SUMMARY', 'COUNTS', 'TRIPWIRE'):
            if kind not in footer:
                absent.append(f'{key}: missing END {kind}')
        if len(footer) < 3:
            continue
        try:
            count = footer['COUNTS']
            if int(count['dropped']) != 0:
                errors.append(f'{key}: dropped events={count["dropped"]}')
            qualified = [int(count[f'{device}_QUAL']) for device in devices]
            count_groups = sum(g.run == key[0] and g.trial == key[1] for g in replay.groups)
            if qualified != [count_groups] * 3:
                errors.append(f'{key}: qualified counts {qualified} != grouped count {count_groups}')
            trip = footer['TRIPWIRE']
            if int(trip['incomplete_groups']) != 0:
                errors.append(f'{key}: firmware incomplete groups={trip["incomplete_groups"]}')
            train_count = sum(k[:2] == key for k, _ in trains)
            if int(footer['SUMMARY']['trains']) != train_count or int(trip['trains']) != train_count:
                errors.append(f'{key}: train footer count differs from replay')
        except (KeyError, ValueError):
            absent.append(f'{key}: malformed END counters')
    if set(footers) != sessions:
        errors.append('END trial identities differ from recorded trains')
    if replay.missing:
        errors.append(f'incomplete groups={len(replay.missing)}')
    if replay.dropped:
        errors.append(f'dropped events={replay.dropped}')
    if len(trains) != len(lengths):
        errors.append(f'expected {len(lengths)} segments, found {len(trains)}')
    reports = []
    timing_errors = []
    for index, (key, groups) in enumerate(trains):
        expected = lengths[index] + 1 if index < len(lengths) else None
        if expected != len(groups) or [g.boundary for g in groups] != list(range(len(groups))):
            errors.append(f'segment {index+1}: expected {expected} consecutive boundaries, found {len(groups)}')
        names = [groups[0].names.get(channel, '').removesuffix('_COMMIT') for channel in range(3)]
        if names != devices:
            errors.append(f'segment {index+1}: channel identities {names} != {devices}')
        for g in groups:
            if [g.names.get(channel, '').removesuffix('_COMMIT') for channel in range(3)] != devices:
                errors.append(f'segment {index+1}: changing channel identity')
                break
        worst = max(g.span_us for g in groups)
        if worst > ASSOCIATION_WINDOW_US:
            errors.append(f'segment {index+1}: group span exceeds {ASSOCIATION_WINDOW_US} us association window')
        if worst > COMMIT_LIMIT_US:
            timing_errors.append(f'segment {index+1}: worst COMMIT skew {worst} us exceeds {COMMIT_LIMIT_US} us')
        reports.append((index+1, len(groups), worst, groups[0].span_us))
    structurally_complete = not errors and not absent
    return errors + timing_errors, absent, reports, structurally_complete

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('capture', type=Path)
    parser.add_argument('--segment-seconds', required=True, help='Planned whole segment durations, e.g. 540,540,540,180')
    parser.add_argument('--devices', nargs=3, default=['ESP01', 'ESP04', 'ESP03'], help='CH1 CH2 CH3 identities')
    parser.add_argument('--sync-csv', type=Path, help='Optional controller segment sync audit for three-AP evidence')
    args = parser.parse_args()
    try:
        lengths = [int(s) for s in args.segment_seconds.split(',')]
        if not lengths or any(s < 1 or s > 6000 for s in lengths) or len(set(args.devices)) != 3:
            raise ValueError('Invalid segment geometry or channel identities')
        errors, absent, reports, structurally_complete = evaluate(args.capture, lengths, args.devices)
        for index, count, worst, start in reports:
            print(f'SEGMENT={index} BOUNDARIES={count} START_RANGE_US={start} WORST_RANGE_US={worst}')
        print('CAPTURE_STRUCTURE=' + ('COMPLETE' if structurally_complete else 'INCOMPLETE'))
        if args.sync_csv:
            rows = list(csv.DictReader(args.sync_csv.open(encoding='utf-8-sig', newline='')))
            associations = {device: {r['Bssid'].upper() for r in rows if r['Device'] == device} for device in args.devices}
            known = all(len(values) == 1 and 'UNKNOWN' not in values for values in associations.values())
            different = known and len({next(iter(v)) for v in associations.values()}) == 3
            print('THREE_AP_EVIDENCE=' + ('PASS' if different else 'FAIL'))
            if not different:
                errors.append('three distinct stable BSSIDs not established in sync audit')
        else:
            print('THREE_AP_EVIDENCE=NOT_PROVIDED')
        for message in errors:
            print('ERROR=' + message)
        for message in absent:
            print('CAPTURE_INCOMPLETE=' + message)
        verdict = 'INCOMPLETE' if absent else 'FAIL' if errors else 'PASS'
        print('PRODUCTION_MARKER_GATE=' + verdict)
        print('VISIBLE_15_BOARD_VALIDATION=REQUIRED (three marker channels do not prove the full installation)')
        return 3 if absent else 2 if errors else 0
    except (OSError, ValueError, KeyError) as error:
        print('INVALID=' + str(error))
        return 3

if __name__ == '__main__':
    raise SystemExit(main())
