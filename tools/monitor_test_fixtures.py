"""Independent elapsed-window fixtures for portable monitor CSV tests."""
from pathlib import Path
import csv
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

def row(**overrides):
    # Actual 35 s monitor window for a 30 s countdown, including 5 s prestart.
    elapsed=35_000_000;period=251;offset=100
    expected=1+(elapsed-offset)//period
    d=dict(DeviceId='ESP01',Phase='POST_RUN',DurationSeconds=30,StatusFieldCount=45,DiagnosticFlags="0x00",
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

