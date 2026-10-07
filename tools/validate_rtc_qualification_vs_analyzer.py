#!/usr/bin/env python3
"""Compare all three physical V10 pair slopes with the two-START RTC prediction.

For phase commit_B - commit_A, the predicted slope is RTC_rate_A - RTC_rate_B.
Each train gets its own intercept; separate START offsets are never fitted as
drift. Original edge timestamps from QUALIFIED or SKEW records are retained.

v6.22.14 (review fixes):
  * The two-START prediction inherits each board's sync error at both STARTs.
    For a pair, that contributes (d2 - d1) / T to ERROR_PPM, where d1 and d2 are
    the physical commit_B - commit_A offsets at the two STARTs and T is the
    interval between them. On 2026-10-07 consecutive STARTs moved one pair's
    offset by up to 64 us, i.e. 0.053 ppm at T = 1200 s, more than the whole
    0.05 ppm budget. The default minimum interval is therefore 2400 s.
  * When the capture also contains both START trains (located from the
    controller's T* spacing), the measured sync term and the sync-corrected
    error are reported for every pair. PASS/FAIL stays on the raw error unless
    --gate sync-corrected is chosen explicitly.
"""
from __future__ import annotations
import argparse
import csv
from itertools import combinations
import math
from pathlib import Path
import statistics
from analyzer_v10 import load_capture
from qualify_rtc_batch import build_pair_row, read_start_rows

DEFAULT_MIN_INTERVAL_SECONDS = 2400.0
SYNC_BUDGET_WARNING_FRACTION = 0.5

def linear_fit(x, y):
    if len(x)!=len(y) or len(x)<3: raise ValueError('need at least 3 paired boundaries')
    xm=statistics.fmean(x); ym=statistics.fmean(y)
    sxx=sum((v-xm)**2 for v in x)
    if sxx<=0: raise ValueError('degenerate analyzer time axis')
    slope=sum((a-xm)*(b-ym) for a,b in zip(x,y))/sxx
    intercept=ym-slope*xm
    rms=math.sqrt(statistics.fmean((b-intercept-slope*a)**2 for a,b in zip(x,y)))
    return slope,intercept,rms

def pair_series(groups, ca, cb):
    a0=groups[0].timestamps[ca]; b0=groups[0].timestamps[cb]
    x=[((g.timestamps[ca]-a0)+(g.timestamps[cb]-b0))/2_000_000.0 for g in groups]
    y=[float(g.timestamps[cb]-g.timestamps[ca]) for g in groups]
    return x,y

def compare_pairs(trains, rates, channel_devices, tolerance_ppm=0.05):
    reports=[]
    for key,groups in trains.items():
        for ca,cb in combinations(range(3),2):
            a,b=channel_devices[ca],channel_devices[cb]
            x,y=pair_series(groups,ca,cb)
            slope,intercept,rms=linear_fit(x,y)
            expected=rates[a]-rates[b]
            reports.append(dict(train=key,pair=f'{a}-{b}',phase=f'{b}_MINUS_{a}',
                boundaries=len(groups),start_us=y[0],slope_ppm=slope,prediction_ppm=expected,
                error_ppm=slope-expected,rms_us=rms,passed=abs(slope-expected)<=tolerance_ppm))
    return reports

def read_tstar(path, devices):
    """Return the single TStarMasterUs shared by the selected devices' START rows."""
    values=set()
    with path.open(newline='',encoding='utf-8-sig') as f:
        for row in csv.DictReader(f):
            if (row.get('Phase') or '').upper()=='START' and row.get('DeviceId') in devices:
                text=(row.get('TStarMasterUs') or '').strip()
                if text: values.add(int(text))
    return values.pop() if len(values)==1 else None

def channel_map(groups, override):
    labels=[groups[0].names[i].removesuffix('_COMMIT') for i in range(3)]
    return list(override) if override else labels

def start_offsets(groups, mapping):
    """Physical commit_B - commit_A at the train's boundary 0, keyed by device pair."""
    first=groups[0].timestamps
    index={dev:ch for ch,dev in enumerate(mapping)}
    return {(a,b):first[index[b]]-first[index[a]] for a,b in combinations(sorted(index,key=index.get),2)}

def find_start_trains(all_trains, tstar1, tstar2, tolerance_us=None):
    """Locate the trains whose STARTs are separated like the two health STARTs."""
    if tstar1 is None or tstar2 is None or tstar2<=tstar1:
        return None,'T* missing or not increasing in the health files'
    delta=tstar2-tstar1
    tol=tolerance_us if tolerance_us is not None else max(250_000,int(delta*100e-6))
    starts=sorted((min(g[0].timestamps.values()),key) for key,g in all_trains.items())
    hits=[(abs((t2-t1)-delta),k1,k2) for (t1,k1),(t2,k2) in combinations(starts,2)
          if abs((t2-t1)-delta)<=tol]
    if not hits: return None,f'no pair of trains is {delta/1e6:.3f} s apart (+/-{tol/1e6:.3f} s)'
    if len(hits)>1: return None,f'{len(hits)} train pairs match the START spacing; capture is ambiguous'
    _,k1,k2=hits[0]
    return (k1,k2),f'matched trains {k1} and {k2}'

def main():
    ap=argparse.ArgumentParser(description=__doc__,formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('first_health_csv',type=Path)
    ap.add_argument('second_health_csv',type=Path)
    ap.add_argument('analyzer_log',type=Path)
    ap.add_argument('--devices',nargs=3,default=['ESP01','ESP03','ESP04'],metavar=('A','B','C'))
    ap.add_argument('--channel-devices',nargs=3,metavar=('CH1','CH2','CH3'),
        help='Explicit physical mapping for older slot-labelled captures; otherwise labels must match --devices')
    ap.add_argument('--tolerance-ppm',type=float,default=0.05)
    ap.add_argument('--warm-confirmed',action='store_true')
    ap.add_argument('--max-rate-change-ppm',type=float,default=0.25)
    ap.add_argument('--fit-lag-seconds',type=float,default=64.0)
    ap.add_argument('--min-interval-seconds',type=float,default=DEFAULT_MIN_INTERVAL_SECONDS,
        help='Minimum master interval between the two STARTs (default 2400 s; bracket the long train)')
    ap.add_argument('--min-boundaries',type=int,default=1200)
    ap.add_argument('--train',type=int,help='Optional train ID inside a single BEGIN/END capture')
    ap.add_argument('--gate',choices=('raw','sync-corrected'),default='raw',
        help='raw (default): PASS/FAIL on ERROR_PPM. sync-corrected: on ERROR_PPM minus the measured '
             'START-offset term; requires both START trains in the capture')
    ap.add_argument('--start-match-tolerance-us',type=int,
        help='Override the T*-spacing tolerance used to locate the START trains')
    args=ap.parse_args()
    if not args.warm_confirmed: ap.error('--warm-confirmed is required')
    for key in ('tolerance_ppm','max_rate_change_ppm','min_interval_seconds'):
        if not math.isfinite(getattr(args,key)) or getattr(args,key)<=0: ap.error(f'{key} must be finite and > 0')
    if not math.isfinite(args.fit_lag_seconds) or args.fit_lag_seconds<0: ap.error('--fit-lag-seconds must be finite and >= 0')
    if args.min_boundaries<3: ap.error('--min-boundaries must be >= 3')
    if len(set(args.devices))!=3: ap.error('--devices must name three distinct timers')
    first=read_start_rows(args.first_health_csv);second=read_start_rows(args.second_health_csv)
    rows={}
    for dev in args.devices:
        if dev not in first or dev not in second: raise SystemExit(f'Missing START health for {dev}')
        rows[dev]=build_pair_row(first[dev],second[dev],min_interval_seconds=args.min_interval_seconds,
            same_boot_tolerance_ppm=100.0,accepted_edge_tolerance=2.0,warm_confirmed=True,
            max_rate_change_ppm=args.max_rate_change_ppm,fit_lag_seconds=args.fit_lag_seconds)
        if not rows[dev]['QualificationValid']:
            reason=' (interval shorter than --min-interval-seconds)' if not rows[dev]['IntervalOK'] else ''
            raise SystemExit(f'{dev}: two-START validity guards failed{reason}')
    capture=load_capture(args.analyzer_log,prefer_qualified=True)
    if capture.dropped or capture.missing or capture.errors:
        raise SystemExit(f'Invalid physical capture: dropped={capture.dropped} incomplete={len(capture.missing)} errors={capture.errors}')
    all_trains=capture.trains()
    trains={key:groups for key,groups in all_trains.items()
        if len(groups)>=args.min_boundaries and (args.train is None or key[2]==args.train)}
    if not trains: raise SystemExit('No V10 train has the required number of complete physical boundaries')
    rates={dev:float(rows[dev]['DisciplinedMinusMasterPpm']) for dev in args.devices}

    # Measured START-offset (sync) term for every pair, when both STARTs were captured.
    match,match_note=find_start_trains(all_trains,read_tstar(args.first_health_csv,args.devices),
                                       read_tstar(args.second_health_csv,args.devices),
                                       args.start_match_tolerance_us)
    sync={}
    if match:
        g1,g2=all_trains[match[0]],all_trains[match[1]]
        m1,m2=channel_map(g1,args.channel_devices),channel_map(g2,args.channel_devices)
        if set(m1)!=set(args.devices) or set(m2)!=set(args.devices):
            match,match_note=None,'START train labels do not identify the selected devices'
        else:
            d1,d2=start_offsets(g1,m1),start_offsets(g2,m2)
            for (a,b),off1 in d1.items():
                interval=(float(rows[a]['IntervalSeconds'])+float(rows[b]['IntervalSeconds']))/2.0
                sync[(a,b)]=dict(d1=off1,d2=d2[(a,b)],term=(d2[(a,b)]-off1)/interval)
    if args.gate=='sync-corrected' and not sync:
        raise SystemExit(f'--gate sync-corrected needs both START trains in the capture: {match_note}')

    reports=[]
    for key,groups in trains.items():
        mapping=channel_map(groups,args.channel_devices)
        if len(set(mapping))!=3 or set(mapping)!=set(args.devices):
            labels=[groups[0].names[i].removesuffix('_COMMIT') for i in range(3)]
            raise SystemExit(f'Capture labels {labels} do not identify {args.devices}; supply an explicit --channel-devices mapping')
        reports.extend(compare_pairs({key:groups},rates,mapping,args.tolerance_ppm))
    for r in reports:
        a,b=r['pair'].split('-')
        s=sync.get((a,b)) or ({'d1':-sync[(b,a)]['d1'],'d2':-sync[(b,a)]['d2'],'term':-sync[(b,a)]['term']}
                              if (b,a) in sync else None)
        r['sync']=s
        r['corrected_ppm']=r['error_ppm']-s['term'] if s else math.nan
        if args.gate=='sync-corrected':
            r['passed']=abs(r['corrected_ppm'])<=args.tolerance_ppm

    print(f'ANALYZER_SOURCE={capture.source}')
    print(f'ANALYZER_TRAINS_USED={len(trains)}')
    print(f'ANALYZER_TRAINS_SKIPPED={len(all_trains)-len(trains)}')
    print('RUN,TRIAL,TRAIN,PAIR,PHASE,BOUNDARIES,START_US,ANALYZER_SLOPE_PPM,RTC_PREDICTION_PPM,ERROR_PPM,RMS_US,RESULT,'
          'START1_OFFSET_US,START2_OFFSET_US,SYNC_TERM_PPM,SYNC_CORRECTED_ERROR_PPM')
    for r in reports:
        run,trial,train=r['train']; s=r['sync']
        tail=(f"{s['d1']:+d},{s['d2']:+d},{s['term']:+.6f},{r['corrected_ppm']:+.6f}" if s else 'NA,NA,NA,NA')
        print(f"{run},{trial},{train},{r['pair']},{r['phase']},{r['boundaries']},{r['start_us']:+.3f},"
              f"{r['slope_ppm']:+.6f},{r['prediction_ppm']:+.6f},{r['error_ppm']:+.6f},{r['rms_us']:.3f},"
              f"{'PASS' if r['passed'] else 'FAIL'},{tail}")
    for dev in args.devices:
        print(f"TOOL_{dev}_DISCIPLINED_MINUS_MASTER_PPM={rates[dev]:+.6f}")
        print(f"TOOL_{dev}_RATE_DELTA_PPM={float(rows[dev]['RtcRateDeltaPpm']):+.6f}")
        print(f"TOOL_{dev}_INTERVAL_SECONDS={float(rows[dev]['IntervalSeconds']):.3f}")
    if sync:
        worst=max(abs(v['term']) for v in sync.values())
        print(f'SYNC_TERM_SOURCE=ANALYZER_STARTS ({match_note})')
        print(f'SYNC_TERM_MAX_ABS_PPM={worst:.6f}')
        if worst>SYNC_BUDGET_WARNING_FRACTION*args.tolerance_ppm:
            print(f'SYNC_TERM_WARNING=START sync noise uses {100*worst/args.tolerance_ppm:.0f}% of the '
                  f'{args.tolerance_ppm} ppm budget; lengthen the START interval')
    else:
        print(f'SYNC_TERM_SOURCE=UNAVAILABLE ({match_note})')
    passed=all(r['passed'] for r in reports)
    print(f'TOLERANCE_PPM={args.tolerance_ppm:.6f}')
    print(f'GATE={args.gate}')
    print(f'RESULT={"PASS" if passed else "FAIL"}')
    return 0 if passed else 2

if __name__=='__main__': raise SystemExit(main())
