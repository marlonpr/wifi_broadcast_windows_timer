#!/usr/bin/env python3
from pathlib import Path
import csv, subprocess, sys, tempfile
ROOT=Path(__file__).resolve().parents[1]
HEADERS='RunCommandId,TStarMasterUs,DurationSeconds,DeviceId,Phase,StatusCaptured,CapturedMasterUs,TimerState,RtcState,RtcRatePpmVsRtc,RtcFitPoints,RtcFitRmsUs,RtcFitOutliers,RtcAcceptedEdges,RtcInferredMissingEdges,RtcHoldoverEntries,RtcQueueDrops,RtcTemperatureValid,RtcTemperatureC,RtcSqwCore,HealthFlags,SyncEpochMasterMinusLocalUs,SyncEpochLocalUs,SyncEpochDisciplinedUs,SyncEpochMasterUs,SyncEpochMasterMinusDisciplinedUs,StartErrorUs,SchedulerLatenessUs,StartPublishLatenessUs,WorstPublishLatenessUs,FrameNotReady,Cpu0MonitorValid,Cpu0MonitorSamples,Cpu0MonitorEventCount,Cpu0MonitorWorstUs,Cpu0MonitorWorstTask,Cpu0CommitLateCount,Cpu0CommitWorstUs,Cpu0CommitOverlap,Cpu0WrongCoreCallbacks,Cpu0MonitorOverflow,Cpu0InterruptLevelMatch,StatusFieldCount'.split(',')

def mk(dev,dur,fc='42',valid='1',samples=0,events=0,worst=0,task='NONE',late=0,cworst=0,overlap='0',wrong='0',overflow='0',level='1'):
 d={h:'' for h in HEADERS}
 d.update(RunCommandId='1',TStarMasterUs='1',DurationSeconds=str(dur),DeviceId=dev,Phase='POST_RUN',StatusCaptured='1',TimerState='FINISHED',StatusFieldCount=str(fc),Cpu0MonitorValid=valid,Cpu0MonitorSamples=str(samples) if samples is not None else '',Cpu0MonitorEventCount=str(events) if events is not None else '',Cpu0MonitorWorstUs=str(worst) if worst is not None else '',Cpu0MonitorWorstTask=task,Cpu0CommitLateCount=str(late) if late is not None else '',Cpu0CommitWorstUs=str(cworst) if cworst is not None else '',Cpu0CommitOverlap=overlap,Cpu0WrongCoreCallbacks=wrong,Cpu0MonitorOverflow=overflow,Cpu0InterruptLevelMatch=level)
 return d

def run(cmd):
 return subprocess.run(cmd,text=True,capture_output=True)

with tempfile.TemporaryDirectory() as td:
 td=Path(td)
 # Canary sampled lateness of 120 us must pass; commit remains deterministic 300..600 us.
 c=td/'canary.csv'
 with c.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerow(mk('ESP01',60,samples=239999,events=1,worst=120,task='lat_canary',late=1,cworst=466,overlap='1'))
 r=run([sys.executable,str(ROOT/'tools/validate_cpu0_telemetry_canary_csv.py'),str(c)])
 assert r.returncode==0, r.stdout+r.stderr
 assert 'WORST_100_TO_600_US=PASS' in r.stdout
 assert 'COMMIT_WORST_300_TO_600_US=PASS' in r.stdout
 assert 'CPU0_TELEMETRY_CANARY_PATH=PASS' in r.stdout

 # A clean 60 fleet board-hour exposure including 4 h on ESP02 closes the rollout item.
 fleet=td/'fleet.csv'
 with fleet.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader()
  # 14 boards x4h = 56h plus ESP02 4h = 60h exactly.
  for i in range(1,16):
   w.writerow(mk(f'ESP{i:02d}',14400,samples=57_600_000,worst=80,task='IDLE0'))
 r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(fleet)])
 assert r.returncode==0, r.stdout+r.stderr
 assert 'FLEET_MONITORED_HOURS=60.000000' in r.stdout
 assert 'FLEET_GE300_OBSERVED=0' in r.stdout
 assert 'FLEET_ZERO_GE300_95_UPPER_PER_HOUR=0.049929' in r.stdout
 assert 'FLEET_ZERO_GE300_95_LOWER_MEAN_HOURS=20.028' in r.stdout
 assert 'CLOSURE_WATCH_DEVICE=ESP02' in r.stdout
 assert 'CLOSURE_WATCH_DEVICE_MONITORED_HOURS=4.000000' in r.stdout
 assert 'ZERO_GE300_ROLLOUT_ITEM=PASS' in r.stdout

 # A canary image in fleet data must be obvious and prevent closure.
 contam=td/'contam.csv'
 with contam.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerow(mk('ESP01',3600,samples=14_400_000,events=1,worst=420,task='lat_canary',late=1,cworst=470,overlap='1'))
 r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(contam)])
 assert r.returncode==0
 assert 'FLEET_LAT_CANARY_ROWS=1' in r.stdout
 assert 'FLEET_TELEMETRY_HEALTH=WARN' in r.stdout
 assert 'ZERO_GE300_ROLLOUT_ITEM=OPEN' in r.stdout

 # A severe natural event invalidates the zero-event upper bound.
 severe=td/'severe.csv'
 with severe.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerow(mk('ESP02',3600,samples=14_400_000,events=1,worst=358,task='IDLE0',late=1,cworst=380,overlap='1'))
 r=run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(severe)])
 assert 'FLEET_GE300_OBSERVED=1' in r.stdout
 assert 'FLEET_ZERO_GE300_95_UPPER_PER_HOUR=N/A' in r.stdout
 assert 'ZERO_GE300_ROLLOUT_ITEM=OPEN' in r.stdout

print('PASS: v6.22.11 canary floor + contamination flag + sample-derived board-hours + zero-event 95% bounds + closure gate')
