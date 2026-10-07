#!/usr/bin/env python3
import csv
from pathlib import Path
import subprocess
import sys
import tempfile
from analyzer_v10 import Edge,load_capture,replay_edges
from validate_rtc_qualification_vs_analyzer import compare_pairs
from validate_firmware_preflight_log import check_boundaries

ROOT=Path(__file__).resolve().parents[1]
capture=load_capture(ROOT/'tools/fixtures/20261007_original_v10_capture.txt')
qualified_capture=load_capture(ROOT/'tools/fixtures/20261007_original_v10_capture.txt',prefer_qualified=True)
assert qualified_capture.source=='QUALIFIED' and qualified_capture.passes()
assert [g.timestamps for g in qualified_capture.groups]==[g.timestamps for g in capture.groups]
assert capture.passes() and not capture.missing
assert [v[0].span_us for v in capture.trains().values()]==[46,48,68]
assert [max(g.span_us for g in v[:3]) for v in capture.trains().values()]==[46,50,68]
assert [len(v) for v in capture.trains().values()]==[31,31,31]
assert not replay_edges([]).passes()
print('PASS: actual V10 replay, true boundary 0, no bootstrap missing groups, empty capture fails')

# The disciplined schedule is exact, independently of actual callback entry.
scheduled=[dict(boundary=str(n),disciplined_us=str(8_000_000+n*1_000_000)) for n in range(31)]
assert len(check_boundaries(scheduled,30))==31
shifted=[dict(r) for r in scheduled]
for r in shifted[1:]: r['disciplined_us']=str(int(r['disciplined_us'])+1)
try:
    check_boundaries(shifted,30)
except ValueError as e:
    assert 'boundary 0->1: 1000001 us' in str(e)
else:
    raise AssertionError('a one-microsecond epoch replacement was accepted')

def firmware_log(epoch_error=0,callbacks=139442,delay=110,periodic=0,evidence=True,finished=36000100,
                 damage=None):
    command='0123456789ABCDEF'
    lines=[f'ISR_PUBLISH_TRACE_BEGIN command={command} count=31']
    for n in range(31):
        disciplined=8_000_000+n*1_000_000+(epoch_error if n else 0)
        target=6_000_000+n*1_000_000
        record=(f'ISR_PUBLISH boundary={n} disciplined_us={disciplined} target_local_us={target} '
                f'publish_marker_begin_us={target+60} published=1 frame_not_ready_count=0 retention=FIRST')
        prefix=f'I (2131{n:02d}) factory_display: '
        if damage=='duplicate' and n==12:
            # esp-idf-monitor: truncated copy and full line on one physical line.
            record=record[:58]+prefix+record
        elif damage=='damaged' and n==17:
            # USB-serial corruption inside the scheduled timestamp, no clean copy.
            record=record.replace(f'disciplined_us={disciplined}','disciplined_us=250\ufffd\ufffd\ufffd')
        elif damage=='conflict' and n==20:
            record=record+' '+prefix+record.replace(f'disciplined_us={disciplined}',f'disciplined_us={disciplined+1}')
        lines.append(prefix+record)
    temperature_evidence=f' periodic_reads_during_run={periodic} suppressed_refreshes=0' if evidence else ''
    lines.extend(['ISR_PUBLISH_TRACE_END',
        f'CPU0_FLEET_SUMMARY command={command} valid=1 period_us=251 threshold_us=50 '
        f'sample_callbacks={callbacks} missed_periods=0 wrong_core_callbacks=0 rearm_failures=0 '
        'sampler_intr_level=3 commit_intr_level=3 worst_commit_us=60 overlap=1 event_overflow=1000',
        f'CPU0_MONITOR_WINDOW command={command} monitor_start_us=1000000 tstar_local_us=6000000 '
        'monitor_start_to_tstar_us=-5000000 monitor_end_us=36000100 monitor_elapsed_us=35000100 '
        'first_alarm_offset_us=100 expected_periods=139443 commit_ge_300us=0 rearm_guard_skips=0',
        f'RTC_TEMP_POST_RUN command={command} finished_local_us={finished} '
        f'read_local_us={finished+delay} temp_c=25.00 delay_us={delay}{temperature_evidence}'])
    return '\n'.join(lines)+'\n'

with tempfile.TemporaryDirectory() as td:
    log=Path(td)/'preflight.log'
    for params,verdict in [({},'PASS'),({'epoch_error':1},'FAIL'),({'callbacks':119522},'FAIL'),
                           ({'delay':0},'FAIL'),({'delay':500000},'FAIL'),
                           ({'periodic':1},'FAIL'),({'evidence':False},'FAIL'),
                           ({'finished':35999000},'FAIL'),
                           ({'damage':'duplicate'},'PASS'),({'damage':'damaged'},'CAPTURE_DAMAGED'),
                           ({'damage':'conflict'},'FAIL')]:
        log.write_text(firmware_log(**params),encoding='utf-8')
        result=subprocess.run([sys.executable,str(ROOT/'tools/validate_firmware_preflight_log.py'),
                               str(log),'--command-id','0123456789ABCDEF'],text=True,capture_output=True)
        expected_code={'PASS':0,'FAIL':2,'CAPTURE_DAMAGED':3}[verdict]
        assert result.returncode==expected_code,(params,result.stdout+result.stderr)
        assert f'FIRMWARE_30S_PREFLIGHT={verdict}' in result.stdout,(params,result.stdout)
print('PASS: 31 exact scheduled boundaries; 1 us epoch change, short monitor coverage, stale/late temperature, '
      'a mid-run periodic temperature read, missing suppression evidence and a read before the final COMMIT fail; '
      'monitor line duplication passes; serial damage is CAPTURE_DAMAGED, not a firmware result')

devices=['ESP01','ESP03','ESP04'];rates=dict(zip(devices,[0.25,-0.5,0.75]))
edges=[]
for train in range(2):
    base=10_000_000+train*3_000_000_000
    # Different START offsets per train are intentionally unrelated to rates.
    offsets=[0,10+train*600,40+train*1200]
    for n in range(1801):
        for ch,dev in enumerate(devices):
            t=base+round(n*(1_000_000-rates[dev]))+offsets[ch]
            edges.append(Edge(1,1,ch,dev+'_COMMIT',t,(n+1)%2))
edges.sort(key=lambda e:e.timestamp)
long=replay_edges(edges)
assert len(long.trains())==2 and not long.missing
reports=compare_pairs(long.trains(),rates,devices)
assert len(reports)==6 and all(r['passed'] for r in reports)
assert {r['pair'] for r in reports}=={'ESP01-ESP03','ESP01-ESP04','ESP03-ESP04'}
bad_rates={**rates,'ESP04':rates['ESP04']+0.06}
bad=compare_pairs(long.trains(),bad_rates,devices)
assert len([r for r in bad if not r['passed']])==4
assert max(abs(r['error_ppm']) for r in reports)<0.001
print('PASS: all three independent pair slopes, two train intercepts, 0.06 ppm mismatch fails both affected pairs')

# Exercise the full validator CLI with both V10 record formats and two START
# health files, rather than only testing the least-squares helper in isolation.
with tempfile.TemporaryDirectory() as td:
    folder=Path(td)
    for index in range(2):
        health=[]
        for dev in devices:
            health.append(dict(RunCommandId=f'{index+1:016X}',DeviceId=dev,Phase='START',StatusCaptured=1,
                CapturedMasterUs=1_100_000+index*1_800_000_000,RtcState='LOCKED',
                SyncEpochMasterUs=1_000_000+index*1_800_000_000,
                SyncEpochLocalUs=2_000_000+index*1_800_000_000,
                SyncEpochMasterMinusDisciplinedUs=100_000-round(index*rates[dev]*1800),
                RtcRatePpmVsRtc=0,RtcFitPoints=129,RtcFitRmsUs=0.5,RtcFitOutliers=0,
                RtcAcceptedEdges=100+index*1800,RtcInferredMissingEdges=0,RtcHoldoverEntries=0,
                RtcQueueDrops=0,RtcTemperatureValid=1,RtcTemperatureC=25,RtcSqwCore=1,HealthFlags=1))
        with (folder/f'start{index}.csv').open('w',newline='') as f:
            w=csv.DictWriter(f,fieldnames=list(health[0]));w.writeheader();w.writerows(health)
    formats={
        'QUALIFIED':'\n'.join(f'ANZ|QUALIFIED|{e.run}|{e.trial}|{e.channel+1}|{e.name}|q={i+1}|{e.timestamp}|{e.level}'
                              for i,e in enumerate(edges)),
        'SKEW':'\n'.join(f'ANZ|SKEW|{g.run}|{g.trial}|train={g.train}|boundary={g.boundary}|'+
                        '|'.join(f'{g.names[c].removesuffix("_COMMIT")}={g.timestamps[c]}' for c in range(3))+
                        f'|range_us={g.span_us}' for g in long.groups)}
    for source,content in formats.items():
        log=folder/'analyzer.log';log.write_text(content+'\n')
        reconstructed=load_capture(log,prefer_qualified=True)
        assert reconstructed.source==source and len(reconstructed.trains())==2
        assert [g.timestamps for g in reconstructed.groups]==[g.timestamps for g in long.groups]
        result=subprocess.run([sys.executable,str(ROOT/'tools/validate_rtc_qualification_vs_analyzer.py'),
                               str(folder/'start0.csv'),str(folder/'start1.csv'),str(log),'--warm-confirmed',
                               '--min-interval-seconds','1800'],  # v6.22.14 default is 2400 s
                              text=True,capture_output=True)
        assert result.returncode==0,result.stdout+result.stderr
        assert 'ANALYZER_TRAINS_USED=2' in result.stdout and 'RESULT=PASS' in result.stdout
        assert all(pair in result.stdout for pair in ['ESP01-ESP03','ESP01-ESP04','ESP03-ESP04'])
print('PASS: complete validator CLI, two START files, QUALIFIED/grouped V10 timestamps and multiple trains')

def row(**overrides):
    # Actual 35 s monitor window for a 30 s countdown, including 5 s prestart.
    elapsed=35_000_000;period=251;offset=100
    expected=1+(elapsed-offset)//period
    d=dict(DeviceId='ESP01',Phase='POST_RUN',DurationSeconds=30,StatusFieldCount=44,
        Cpu0MonitorValid=1,Cpu0MonitorSamples=expected,Cpu0MonitorMissedPeriods=0,
        Cpu0MonitorEventCount=1,Cpu0MonitorWorstUs=60,Cpu0MonitorWorstTask='wifi',
        Cpu0CommitLateCount=1,Cpu0CommitWorstUs=60,Cpu0CommitOverlap=1,
        Cpu0WrongCoreCallbacks=0,Cpu0MonitorOverflow=0,Cpu0InterruptLevelMatch=1,FirmwareBuildId='deadbeef',
        RunDiagnosticCaptured=1,RunDiagnosticValid=1,Cpu0MonitorPeriodUs=period,Cpu0MonitorThresholdUs=50,
        MonitorStartUs=1_000_000,TStarLocalUs=6_000_000,MonitorStartToTStarUs=-5_000_000,
        MonitorEndUs=36_000_000,MonitorElapsedUs=elapsed,FirstAlarmOffsetUs=offset,
        Cpu0MonitorExpectedPeriods=expected,Cpu0EventsGe50Us=1,RtcDisciplineEventsGe50Us=0,
        WifiEventsGe50Us=1,UdpEventsGe50Us=0,CommitLateEvents=1,CommitGe300Us=0,MonitorRearmFailures=0)
    d.update(overrides);return d

def summarize(rows,td):
    p=Path(td)/'health.csv'
    with p.open('w',newline='') as f:
        w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
    cmd=[sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(p),
        '--closure-fleet-hours','0','--watch-device','ESP01','--watch-device-min-hours','0']
    r=subprocess.run(cmd,text=True,capture_output=True)
    assert r.returncode==0,r.stderr
    return r.stdout

with tempfile.TemporaryDirectory() as td:
    out=summarize([row()],td)
    assert 'FLEET_SAMPLE_RATE_WARN_ROWS=0' in out
    assert 'FLEET_TELEMETRY_HEALTH=PASS' in out
    assert 'ZERO_GE300_ROLLOUT_ITEM=PASS' in out # overlap=1 is explanatory
    assert 'FLEET_MONITOR_ELAPSED_US=35000000' in out
    out=summarize([row(Cpu0MonitorOverflow=1000)],td)
    assert 'ZERO_GE300_ROLLOUT_ITEM=PASS' in out # retained-detail capacity does not truncate cumulative counters
    out=summarize([row(DurationSeconds=9999)],td)
    assert 'FLEET_SAMPLE_RATE_WARN_ROWS=0' in out # duration is not an input to sample arithmetic
    out=summarize([row(Cpu0CommitWorstUs=300,CommitGe300Us=1)],td)
    assert 'FLEET_COMMIT_GE300_EVENTS=1' in out and 'ZERO_GE300_ROLLOUT_ITEM=OPEN' in out
    assert 'FLEET_COMMIT_TIMING=FAIL' in out
    out=summarize([row(Cpu0MonitorWorstUs=350,Cpu0MonitorSamples=139441,Cpu0MonitorMissedPeriods=1)],td)
    assert 'FLEET_SAMPLE_RATE_WARN_ROWS=0' in out and 'FLEET_COMMIT_TIMING=PASS' in out
    assert 'FLEET_GE300_OBSERVED=0' in out # a sampler stall alone is not a COMMIT timing defect
    out=summarize([row(RunDiagnosticCaptured=0)],td)
    assert 'FLEET_SAMPLE_RATE_WARN_ROWS=1' in out
    out=summarize([row(MonitorElapsedUs=30_000_000)],td)
    assert 'FLEET_SAMPLE_RATE_WARN_ROWS=1' in out
    out=summarize([row(Cpu0MonitorSamples=139439,Cpu0MonitorMissedPeriods=3)],td)
    assert 'FLEET_MISSED_PERIOD_ROWS=1' in out
print('PASS: actual elapsed window, duration independence, missing diagnostics fail, overlap telemetry, >=300 us COMMIT gate')
