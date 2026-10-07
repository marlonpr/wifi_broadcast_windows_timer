#!/usr/bin/env python3
from __future__ import annotations
from pathlib import Path
import csv, subprocess, sys, tempfile

ROOT=Path(__file__).resolve().parents[1]
HEADERS='RunCommandId,TStarMasterUs,DurationSeconds,DeviceId,Phase,StatusCaptured,CapturedMasterUs,TimerState,RtcState,RtcRatePpmVsRtc,RtcFitPoints,RtcFitRmsUs,RtcFitOutliers,RtcAcceptedEdges,RtcInferredMissingEdges,RtcHoldoverEntries,RtcQueueDrops,RtcTemperatureValid,RtcTemperatureC,RtcSqwCore,HealthFlags,SyncEpochMasterMinusLocalUs,SyncEpochLocalUs,SyncEpochDisciplinedUs,SyncEpochMasterUs,SyncEpochMasterMinusDisciplinedUs,StartErrorUs,SchedulerLatenessUs,StartPublishLatenessUs,WorstPublishLatenessUs,FrameNotReady,Cpu0MonitorValid,Cpu0MonitorSamples,Cpu0MonitorEventCount,Cpu0MonitorWorstUs,Cpu0MonitorWorstTask,Cpu0CommitLateCount,Cpu0CommitWorstUs,Cpu0CommitOverlap,Cpu0WrongCoreCallbacks,Cpu0MonitorOverflow,Cpu0InterruptLevelMatch,Cpu0MonitorMissedPeriods,Cpu0MonitorExpectedPeriods,FirmwareBuildId,StatusFieldCount,RunDiagnosticCaptured,RunDiagnosticValid,Cpu0MonitorPeriodUs,Cpu0MonitorThresholdUs,MonitorStartUs,TStarLocalUs,MonitorStartToTStarUs,MonitorEndUs,MonitorElapsedUs,FirstAlarmOffsetUs,Cpu0EventsGe50Us,RtcDisciplineEventsGe50Us,WifiEventsGe50Us,UdpEventsGe50Us,CommitLateEvents,CommitGe300Us,MonitorRearmFailures'.split(',')

def mk(dev,dur,fc='44',valid='1',samples=0,events=0,worst=0,task='NONE',late=0,cworst=0,overlap='0',wrong='0',overflow='0',level='1',missed=0,build='deadbeef'):
    d={h:'' for h in HEADERS}
    d.update(RunCommandId='1',TStarMasterUs='1',DurationSeconds=str(dur),DeviceId=dev,Phase='POST_RUN',StatusCaptured='1',TimerState='FINISHED',StatusFieldCount=str(fc),Cpu0MonitorValid=valid,Cpu0MonitorSamples=str(samples) if samples is not None else '',Cpu0MonitorEventCount=str(events) if events is not None else '',Cpu0MonitorWorstUs=str(worst) if worst is not None else '',Cpu0MonitorWorstTask=task,Cpu0CommitLateCount=str(late) if late is not None else '',Cpu0CommitWorstUs=str(cworst) if cworst is not None else '',Cpu0CommitOverlap=overlap,Cpu0WrongCoreCallbacks=wrong,Cpu0MonitorOverflow=overflow,Cpu0InterruptLevelMatch=level,Cpu0MonitorMissedPeriods=str(missed),Cpu0MonitorExpectedPeriods=str((samples or 0)+missed),FirmwareBuildId=build)
    # Fixtures provide the independent endpoint, not a reconstructed DurationSeconds window.
    elapsed=int(round(((samples or 0)+missed)*251))
    d.update(RunDiagnosticCaptured='1',RunDiagnosticValid=valid,Cpu0MonitorPeriodUs='251',
        Cpu0MonitorThresholdUs='50',MonitorStartUs='1000000',TStarLocalUs='6000000',
        MonitorStartToTStarUs='-5000000',MonitorEndUs=str(1000000+elapsed),MonitorElapsedUs=str(elapsed),
        FirstAlarmOffsetUs='251',Cpu0EventsGe50Us=str(events or 0),RtcDisciplineEventsGe50Us='0',
        WifiEventsGe50Us='0',UdpEventsGe50Us='0',CommitLateEvents=str(late or 0),
        CommitGe300Us=str(1 if (cworst or 0)>=300 else 0),MonitorRearmFailures='0')
    return d

def run(cmd): return subprocess.run(cmd,text=True,capture_output=True)

def write(path, rows):
    with path.open('w',newline='') as f:
        w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerows(rows)

with tempfile.TemporaryDirectory() as td0:
    td=Path(td0)
    # Corrected 600 us boundary canary: sampler floor restored to >=350 us.
    p=td/'boundary.csv'; write(p,[mk('ESP01',60,samples=239998,events=1,worst=577,task='lat_canary',late=1,cworst=499,overlap='1',missed=2)])
    r=run([sys.executable,str(ROOT/'tools/validate_cpu0_telemetry_canary_csv.py'),str(p)])
    assert r.returncode==0, r.stdout+r.stderr
    assert 'WORST_350_TO_700_US=PASS' in r.stdout and 'STATUS_FIELD_COUNT_44=PASS' in r.stdout

    # 1000 us mid-second task canary proves raw lateness + missed-period accounting without COMMIT.
    p=td/'task1000.csv'; write(p,[mk('ESP01',60,samples=239996,events=1,worst=820,task='lat_canary',missed=3)])
    r=run([sys.executable,str(ROOT/'tools/validate_cpu0_sampler_task_canary_csv.py'),str(p)])
    assert r.returncode==0, r.stdout+r.stderr
    assert 'CPU0_SAMPLER_1000US_TASK_CANARY=PASS' in r.stdout

    # Same-level ISR canary exposes the underlying task rather than lat_canary.
    p=td/'isr1000.csv'; write(p,[mk('ESP01',60,samples=239996,events=1,worst=840,task='IDLE0',missed=3)])
    r=run([sys.executable,str(ROOT/'tools/validate_cpu0_isr_canary_csv.py'),str(p)])
    assert r.returncode==0, r.stdout+r.stderr
    assert 'CPU0_SAMPLER_1000US_ISR_CANARY=PASS' in r.stdout

    # 60 clean board-hours closes only when no grid misses/canaries/legacy/build warnings exist.
    p=td/'fleet.csv'; rows=[]
    for i in range(1,16):
        d=mk(f'ESP{i:02d}',14400,samples=14_400_000_000//251,worst=80,task='IDLE0')
        d.update(MonitorElapsedUs='14400000000',MonitorEndUs='14401000000')
        rows.append(d)
    write(p,rows)
    r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(p)])
    assert r.returncode==0, r.stdout+r.stderr
    assert 'FLEET_CLEAN_MONITORED_HOURS=60.000000' in r.stdout
    assert 'FLEET_MISSED_PERIOD_ROWS=0' in r.stdout
    assert 'FLEET_ZERO_GE300_95_UPPER_PER_HOUR=0.049929' in r.stdout
    assert 'ZERO_GE300_ROLLOUT_ITEM=PASS' in r.stdout

    # Any swallowed period is explicitly flagged and excluded from clean exposure.
    p=td/'missed.csv'; write(p,[mk('ESP02',3600,samples=14_399_999,events=1,worst=280,task='IDLE0',missed=1)])
    r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(p)])
    assert 'FLEET_MISSED_PERIOD_ROWS=1' in r.stdout
    assert 'FLEET_CLEAN_MONITORED_HOURS=0.000000' in r.stdout
    assert 'FLEET_TELEMETRY_HEALTH=WARN' in r.stdout
    assert 'ZERO_GE300_ROLLOUT_ITEM=OPEN' in r.stdout

    # Missing build identity also prevents clean exposure.
    p=td/'nobuild.csv'; write(p,[mk('ESP02',3600,samples=14_400_000,build='')])
    r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(p)])
    assert 'FLEET_BUILD_ID_WARN_ROWS=1' in r.stdout and 'FLEET_TELEMETRY_HEALTH=WARN' in r.stdout

# Static controller checks: parser compatibility and TX trace lifetime.
proto=(ROOT/'windows-controller/FactoryTimer.Protocol/FactoryProtocol.cs').read_text()
vm=(ROOT/'windows-controller/FactoryTimer.Controller/MainViewModel.cs').read_text()
checks={
    'parser accepts 42 and 44':'fields.Length == 42 || fields.Length == 44' in proto,
    'parser reads missed periods':'ProtocolParseError.Cpu0MonitorMissedPeriods' in proto,
    'parser reads build id':'ProtocolParseError.FirmwareBuildId' in proto,
    'CSV contains missed/expected/build':'Cpu0MonitorMissedPeriods,Cpu0MonitorExpectedPeriods,FirmwareBuildId,StatusFieldCount' in vm,
    'postrun requests tagged':'"HEALTH_STATUS_REQUEST"' in vm,
    'first postrun round hits every participant':'Always issue one explicit post-run health request to every frozen' in vm,
    'trace session ends after health collection':vm.find('await CollectPostRunHealthAsync(session') < vm.find('controllerTxTraceSession = null;'),
}
for k,v in checks.items(): print(f'{k}: {"PASS" if v else "FAIL"}')
assert all(checks.values())
print('V6_22_12_ROLLOUT_HARDENING=PASS')
