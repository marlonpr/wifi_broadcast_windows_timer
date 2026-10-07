#!/usr/bin/env python3
"""Check a single new-build 30-second firmware run using buffered serial records.

v6.23.17 review fixes:
  * ISR_PUBLISH records are parsed one record at a time. esp-idf-monitor can put a
    truncated copy of a long line and the full line on one physical line, and a
    noisy USB-serial path can overwrite bytes. Only complete, undamaged records
    count; a boundary seen only in damaged records is reported as
    FIRMWARE_30S_PREFLIGHT=CAPTURE_DAMAGED (re-capture), not as a firmware FAIL.
  * RTC_TEMP_POST_RUN must report periodic_reads_during_run=0 (suppression held
    from START_AT acceptance to the post-run read) and the read must follow the
    final scheduled COMMIT, not just the command task's finished detection.
"""
import argparse
from pathlib import Path
import re

REQUIRED_ISR_KEYS = ('boundary', 'disciplined_us', 'target_local_us',
                     'publish_marker_begin_us', 'published', 'frame_not_ready_count')
INTEGER = re.compile(r'-?\d+')

def fields(line):
    return dict(re.findall(r'(\w+)=([^\s|]+)', line))

def isr_records(line):
    """Yield (fields, complete) for each ISR_PUBLISH record in one physical line."""
    for part in line.split('ISR_PUBLISH boundary=')[1:]:
        segment = 'boundary=' + part
        f = fields(segment)
        damaged = '\ufffd' in segment or any(ord(ch) < 32 and ch != '\t' for ch in segment)
        complete = (not damaged and
                    all(k in f and INTEGER.fullmatch(f[k]) for k in REQUIRED_ISR_KEYS))
        yield f, complete

def check_boundaries(records, duration):
    by_boundary = {}
    for record in records:
        n = int(record['boundary']); t = int(record['disciplined_us'])
        if n in by_boundary and by_boundary[n] != t:
            raise ValueError(f'conflicting scheduled timestamps at boundary {n}')
        by_boundary[n] = t
    missing = set(range(duration + 1)) - set(by_boundary)
    if missing: raise ValueError(f'missing scheduled boundary records: {sorted(missing)}')
    for n in range(1, duration + 1):
        dt = by_boundary[n] - by_boundary[n - 1]
        if dt != 1_000_000:
            raise ValueError(f'boundary {n-1}->{n}: {dt} us, expected exactly 1000000')
    return by_boundary

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('firmware_log', type=Path)
    ap.add_argument('--command-id', required=True, help='16-digit run command ID from the controller CSV')
    ap.add_argument('--duration', type=int, default=30)
    ap.add_argument('--expected-prestart-us', type=int, default=5_000_000)
    ap.add_argument('--prestart-tolerance-us', type=int, default=1_000_000)
    args = ap.parse_args()
    if not 1 <= args.duration <= 31: ap.error('--duration must be 1..31 for the retained preflight trace')
    if not re.fullmatch(r'[0-9a-fA-F]{16}', args.command_id):
        ap.error('--command-id must contain exactly 16 hexadecimal digits')
    command = args.command_id.upper()
    records = []; damaged = {}; window = None; summary = None; temperature = None
    active = False; trace_seen = False; damaged_segments = 0
    for line in args.firmware_log.read_text(encoding='utf-8-sig', errors='replace').splitlines():
        f = fields(line)
        if 'ISR_PUBLISH_TRACE_BEGIN' in line:
            active = f.get('command', '').upper() == command
            trace_seen |= active
        elif 'ISR_PUBLISH_TRACE_END' in line:
            active = False
        elif active and 'ISR_PUBLISH boundary=' in line:
            for record, complete in isr_records(line):
                if complete:
                    records.append(record)
                else:
                    damaged_segments += 1
                    if INTEGER.fullmatch(record.get('boundary', '')):
                        damaged.setdefault(int(record['boundary']), 0)
                        damaged[int(record['boundary'])] += 1
        if f.get('command', '').upper() == command:
            if 'CPU0_MONITOR_WINDOW ' in line: window = f
            if 'CPU0_FLEET_SUMMARY ' in line: summary = f
            if 'RTC_TEMP_POST_RUN ' in line: temperature = f
    errors = []; capture_errors = []
    if not trace_seen:
        capture_errors.append('no ISR_PUBLISH_TRACE_BEGIN for this command (wrong command ID or damaged capture)')
    final_target = None
    try:
        check_boundaries(records, args.duration)
        for r in records:
            if int(r['published']) != 1 or int(r['frame_not_ready_count']) != 0:
                raise ValueError('frame publication missing/not ready')
            actual_delay = int(r['publish_marker_begin_us']) - int(r['target_local_us'])
            if actual_delay >= 300: raise ValueError(f'actual COMMIT delay {actual_delay} us at boundary {r["boundary"]}')
        final_target = max(int(r['target_local_us']) for r in records)
        print(f'ALL_{args.duration}_ADJACENT_SCHEDULED_INTERVALS=1000000_US_EXACTLY')
    except (KeyError, ValueError) as e:
        message = str(e)
        present = {int(r['boundary']) for r in records}
        lost = sorted(n for n in damaged if n not in present and 0 <= n <= args.duration)
        if message.startswith('missing scheduled boundary records') and lost:
            capture_errors.append(f'serial capture damaged at boundaries {lost}; re-capture (not a firmware result)')
            # Boundaries absent without any trace in the capture are still firmware errors.
            unexplained = sorted(set(range(args.duration + 1)) - present - set(lost))
            if unexplained: errors.append(f'missing scheduled boundary records: {unexplained}')
        else:
            errors.append(message)
        if records: final_target = max(int(r['target_local_us']) for r in records)
    if damaged_segments:
        print(f'DAMAGED_ISR_PUBLISH_SEGMENTS_IGNORED={damaged_segments}')
    if window is None:
        errors.append('missing CPU0_MONITOR_WINDOW for selected command')
    else:
      try:
        start = int(window['monitor_start_us']); end = int(window['monitor_end_us'])
        elapsed = int(window['monitor_elapsed_us']); offset = int(window['first_alarm_offset_us'])
        expected = 0 if elapsed < offset else 1 + (elapsed - offset) // 251
        if elapsed <= 0 or not 1 <= offset <= 251 or elapsed != end - start or int(window['expected_periods']) != expected:
            errors.append('monitor elapsed/grid endpoints are inconsistent')
        lead = int(window['monitor_start_to_tstar_us'])
        if lead != start - int(window['tstar_local_us']):
            errors.append('monitor start-to-T* timestamps are inconsistent')
        if abs(lead + args.expected_prestart_us) > args.prestart_tolerance_us:
            errors.append(f'monitor did not begin in expected prestart window: {lead} us')
        if final_target is not None and end < final_target:
            errors.append('monitor ended before the final scheduled COMMIT')
        if int(window['commit_ge_300us']) != 0: errors.append('>=300 us COMMIT stall')
        print(f'MONITOR_START_TO_TSTAR_US={lead}')
        print(f'MONITOR_ELAPSED_US={elapsed}')
        print(f'EXPECTED_PERIODS={expected}')
        if 'rearm_guard_skips' in window:
            print(f'REARM_GUARD_SKIPS={int(window["rearm_guard_skips"])}')
        if summary is None:
            errors.append('missing CPU0_FLEET_SUMMARY for selected command')
        else:
            if int(summary['valid']) != 1 or int(summary['period_us']) != 251 or int(summary['threshold_us']) != 50:
                errors.append('monitor is invalid or not configured for 251 us / 50 us')
            observed = int(summary['sample_callbacks']) + int(summary['missed_periods'])
            if abs(observed - expected) > 1:
                errors.append('sample callbacks + missed periods do not match the elapsed monitor window')
            if int(summary['wrong_core_callbacks']) != 0 or int(summary['rearm_failures']) != 0:
                errors.append('monitor core/rearm failure')
            if summary['sampler_intr_level'] != summary['commit_intr_level']:
                errors.append('sampler and COMMIT interrupt levels differ')
            if int(summary['worst_commit_us']) >= 300:
                errors.append('>=300 us worst actual COMMIT delay')
            # overlap and retained-detail overflow are explanatory telemetry.
            print(f'SAMPLE_RATE_VALIDATION={"PASS" if abs(observed-expected) <= 1 else "FAIL"}')
      except (KeyError, ValueError) as e:
        errors.append(f'malformed monitor record: {e}')
    if temperature is None:
        errors.append('missing refreshed RTC_TEMP_POST_RUN for selected command')
    else:
      try:
        finished = int(temperature['finished_local_us']); read = int(temperature['read_local_us'])
        if not (0 < read - finished < 500000): errors.append('post-run read missing/late (must be after finish and within 0.5 s)')
        if int(temperature['delay_us']) != read - finished: errors.append('temperature delay/timestamps inconsistent')
        if final_target is not None and read <= final_target:
            errors.append('post-run temperature read did not follow the final scheduled COMMIT')
        if 'periodic_reads_during_run' not in temperature:
            errors.append('RTC_TEMP_POST_RUN lacks suppression evidence (firmware older than v6.23.17)')
        else:
            periodic = int(temperature['periodic_reads_during_run'])
            skipped = int(temperature.get('suppressed_refreshes', '0'))
            if periodic != 0:
                errors.append(f'{periodic} periodic DS3231 temperature read(s) occurred between START_AT and the post-run read')
            print(f'PERIODIC_TEMPERATURE_READS_DURING_RUN={periodic}')
            print(f'SUPPRESSED_TEMPERATURE_REFRESHES={skipped}')
        print(f'POST_RUN_TEMPERATURE_DELAY_US={read-finished}')
      except (KeyError, ValueError) as e:
        errors.append(f'malformed temperature record: {e}')
    for e in errors + capture_errors: print(f'ERROR={e}')
    if errors:
        verdict, code = 'FAIL', 2
    elif capture_errors:
        verdict, code = 'CAPTURE_DAMAGED', 3
    else:
        verdict, code = 'PASS', 0
    print(f'FIRMWARE_30S_PREFLIGHT={verdict}')
    return code

if __name__ == '__main__': raise SystemExit(main())
