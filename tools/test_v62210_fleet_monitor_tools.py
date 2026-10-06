#!/usr/bin/env python3
from pathlib import Path
import csv, subprocess, sys, tempfile
ROOT=Path(__file__).resolve().parents[1]
HEADERS='RunCommandId,TStarMasterUs,DurationSeconds,DeviceId,Phase,StatusCaptured,CapturedMasterUs,TimerState,RtcState,RtcRatePpmVsRtc,RtcFitPoints,RtcFitRmsUs,RtcFitOutliers,RtcAcceptedEdges,RtcInferredMissingEdges,RtcHoldoverEntries,RtcQueueDrops,RtcTemperatureValid,RtcTemperatureC,RtcSqwCore,HealthFlags,SyncEpochMasterMinusLocalUs,SyncEpochLocalUs,SyncEpochDisciplinedUs,SyncEpochMasterUs,SyncEpochMasterMinusDisciplinedUs,StartErrorUs,SchedulerLatenessUs,StartPublishLatenessUs,WorstPublishLatenessUs,FrameNotReady,Cpu0MonitorValid,Cpu0MonitorSamples,Cpu0MonitorEventCount,Cpu0MonitorWorstUs,Cpu0MonitorWorstTask,Cpu0CommitLateCount,Cpu0CommitWorstUs,Cpu0CommitOverlap,Cpu0WrongCoreCallbacks,Cpu0MonitorOverflow,Cpu0InterruptLevelMatch,StatusFieldCount'.split(',')
def mk(dev,dur,fc,valid,samples,events,worst,task,late,cworst,overlap):
 d={h:'' for h in HEADERS}; d.update(RunCommandId='1',TStarMasterUs='1',DurationSeconds=str(dur),DeviceId=dev,Phase='POST_RUN',StatusCaptured='1',TimerState='FINISHED',StatusFieldCount=str(fc),Cpu0MonitorValid=valid,Cpu0MonitorSamples='' if samples is None else str(samples),Cpu0MonitorEventCount='' if events is None else str(events),Cpu0MonitorWorstUs='' if worst is None else str(worst),Cpu0MonitorWorstTask=task,Cpu0CommitLateCount='' if late is None else str(late),Cpu0CommitWorstUs='' if cworst is None else str(cworst),Cpu0CommitOverlap=overlap,Cpu0WrongCoreCallbacks='0',Cpu0MonitorOverflow='0',Cpu0InterruptLevelMatch='1'); return d
with tempfile.TemporaryDirectory() as td:
 p=Path(td)/'fleet.csv'
 with p.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerow(mk('ESP01',1800,42,'1',7199948,0,0,'NONE',0,0,'0')); w.writerow(mk('ESP02',1800,31,'',None,None,None,'',None,None,'')); w.writerow(mk('ESP03',60,42,'1',60000,0,0,'NONE',0,0,'0'))
 r=subprocess.run([sys.executable,str(ROOT/'tools/summarize_fleet_cpu0_monitor.py'),str(p)],text=True,capture_output=True)
 assert r.returncode==0, r.stderr
 assert 'FLEET_MONITORED_HOURS=0.504163' in r.stdout
 assert 'FLEET_LEGACY_STATUS_ROWS=1' in r.stdout
 assert 'FLEET_SAMPLE_RATE_WARN_ROWS=1' in r.stdout
 assert 'FLEET_TELEMETRY_HEALTH=WARN' in r.stdout
 c=Path(td)/'canary.csv'
 with c.open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=HEADERS); w.writeheader(); w.writerow(mk('ESP01',60,42,'1',239999,1,420,'lat_canary',1,470,'1'))
 r=subprocess.run([sys.executable,str(ROOT/'tools/validate_cpu0_telemetry_canary_csv.py'),str(c)],text=True,capture_output=True)
 assert r.returncode==0, r.stdout+r.stderr
 assert 'CPU0_TELEMETRY_CANARY_PATH=PASS' in r.stdout
print('PASS: v6.22.10 fleet sample accounting + legacy flag + canary CSV validation')
