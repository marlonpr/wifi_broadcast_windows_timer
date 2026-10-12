#!/usr/bin/env python3
"""Execute revised qualification/serial policies; C# wiring is checked, not run."""
from dataclasses import replace
from pathlib import Path
import contextlib
import csv
import importlib.util
import io
import subprocess
import sys
import tempfile
import types
from unittest.mock import patch

from qualify_rtc_batch import read_start_rows, build_pair_row
from rtc_warmup_gate import Sample, inspect_history
from monitor_test_fixtures import row as monitor_row, summarize

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT.parent / 'firmware_v6_23_16/tools/monitor_board.py'
spec = importlib.util.spec_from_file_location('monitor_policy', TOOL)
monitor = importlib.util.module_from_spec(spec); spec.loader.exec_module(monitor)

def rows(second=False):
    return [dict(RunCommandId=f'{1+int(second):016X}', TStarMasterUs=6_000_000+int(second)*2_460_000_000,
        DeviceId=f'ESP{n:02}', Phase='START', StatusCaptured=1,
        CapturedMasterUs=1_100_000+int(second)*2_460_000_000, RtcState='LOCKED',
        SyncEpochMasterUs=1_000_000+int(second)*2_460_000_000,
        # A -56 ppm raw local crystal is not the accepted-edge timing basis.
        SyncEpochLocalUs=2_000_000+int(second)*(2_460_000_000-137_760),
        SyncEpochMasterMinusDisciplinedUs=50_000, RtcRatePpmVsRtc=-56+int(second)*1.3,
        RtcFitPoints=129, RtcFitRmsUs=0.5, RtcFitOutliers=0, RtcAcceptedEdges=12000+int(second)*2460,
        RtcInferredMissingEdges=0, RtcHoldoverEntries=0, RtcQueueDrops=0,
        RtcTemperatureValid=1, RtcTemperatureC=25, RtcSqwCore=1, HealthFlags=7, DiagnosticFlags='0x00',
        FirmwareBuildId=f'production-{n}', ControllerSessionId='one-controller-process',
        QualificationPhase='SECOND' if second else 'FIRST', WarmupFirstCommandId='0000000000000001',
        WarmupHistorySeconds=608, WarmupRateChangePpm=0.25, WarmupGoodFitPolls=3, WarmupReady=1,
        WarmupMaxRateChangePpm=0.25, WarmupRequiredFitPoints=129) for n in range(1, 6)]

def write(path, data):
    with path.open('w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(data[0])); w.writeheader(); w.writerows(data)

def pair(a, b):
    return build_pair_row(a, b, min_interval_seconds=2400, same_boot_tolerance_ppm=100,
        accepted_edge_tolerance=2, warm_confirmed=False)

with tempfile.TemporaryDirectory() as td:
    td=Path(td); first=td/'first.csv'; second=td/'second.csv'
    write(first, rows()); write(second, rows(True))
    a=read_start_rows(first); b=read_start_rows(second)
    for device in a:
        result=pair(a[device], b[device])
        assert result['QualificationValid'] and result['ControllerSessionOK'] and result['BuildUnchangedOK'], result
        assert result['AcceptedEdgeError']==0 and result['CounterIntervalSeconds']==2460
        assert not result['RateChangeWithinInformationalBand'] and result['RateChangeInformationalOnly']
    command=[sys.executable,str(ROOT/'tools/qualify_rtc_batch.py'),str(first),str(second)]
    r=subprocess.run(command,text=True,capture_output=True)
    assert r.returncode==0 and 'informational only' in r.stdout, r.stdout+r.stderr
    # Both edge-count endpoint limits are inclusive, using master capture time.
    for error in (-3, -2, 0, 2, 3):
        result=pair(a['ESP01'],replace(b['ESP01'],accepted_edges=b['ESP01'].accepted_edges+error))
        assert result['QualificationValid']==(abs(error)<=2), (error,result)
    for kwargs in (dict(firmware_build_id='reflashed'),dict(firmware_build_id=None),
                   dict(controller_session_id='restarted-controller'),dict(controller_session_id=None),
                   dict(accepted_edges=16),dict(inferred_missing_edges=1),dict(holdover_entries=1),
                   dict(queue_drops=1),dict(diagnostic_flags=4),dict(rtc_fit_points=128)):
        assert not pair(a['ESP01'],replace(b['ESP01'],**kwargs))['QualificationValid'], kwargs
    # Modern proof cannot hide bad evidence on an unwired participant.
    for field,value in (('RtcFitPoints',128),('WarmupRateChangePpm',0.250001),
                        ('WarmupMaxRateChangePpm',0.05),('WarmupRequiredFitPoints',128),
                        ('FirmwareBuildId','changed'),('ControllerSessionId','restarted'),
                        ('RtcAcceptedEdges',16),('RtcQueueDrops',1)):
        data=rows(True);data[1][field]=value;write(second,data)
        r=subprocess.run(command+['--warm-confirmed'],text=True,capture_output=True)
        assert r.returncode!=0, (field,r.stdout,r.stderr)
        r=subprocess.run([sys.executable,str(ROOT/'tools/validate_rtc_qualification_vs_analyzer.py'),
            str(first),str(second),str(td/'absent.txt'),'--warm-confirmed'],text=True,capture_output=True)
        assert r.returncode!=0 and 'ESP02' in r.stderr and 'FileNotFoundError' not in r.stderr, (field,r.stderr)
    # ±1 monitor stop endpoint is allowed; an unaccounted second period is not.
    expected=monitor_row()['Cpu0MonitorExpectedPeriods']
    for delta in (-2,-1,0,1,2):
        output=summarize([monitor_row(Cpu0MonitorSamples=expected+delta)],td)
        assert f'FLEET_SAMPLE_RATE_WARN_ROWS={int(abs(delta)>1)}' in output, output

print('PASS: frozen 0.25 ppm/600 s proof; full fits; build/controller continuity; master-time ±2 SQW edges; all five endpoints guarded; START2 rate informational; monitor ±1')

full=[Sample(n*16_000_000,4+0.25*n/38,'LOCKED',0.5,129,12000+n*16,0,0,0,0,0,True,25,1) for n in range(39)]
assert inspect_history(full)['ready']
assert not inspect_history(full[:-1])['ready']
assert not inspect_history([replace(s,points=128) for s in full])['ready']
assert not inspect_history(full[:-1]+[replace(full[-1],points=128)])['ready']
assert not inspect_history(full[:-1]+[replace(full[-1],rate_ppm=4.250001)])['ready']
# One past RMS excursion must not discard 600 s; three later good polls recover.
excursion=list(full);excursion[-4]=replace(excursion[-4],rms_us=2.4)
assert inspect_history(excursion)['ready']
excursion[-2]=replace(excursion[-2],rms_us=2.4)
assert not inspect_history(excursion)['ready']
assert not inspect_history(full[:-1]+[replace(full[-1],accepted=16)])['ready']
# Trend and temperature span no longer impose extra gate limits.
assert inspect_history([replace(s,temp_c=25+n/38) for n,s in enumerate(full)])['ready']
print('PASS: serial inspection uses full fits and 600 s; RMS recovery; accepted-count reset; trend/temperature informational and controller gate authoritative')

def qual(count):
    return f'I (100) rtc_discipline: RTC_QUAL device=ESP01 accepted={count}\r\n'.encode()

class SerialMock:
    def __init__(self,chunks):
        self.chunks=iter(chunks);self.in_waiting=0;self.opened=False;self.closed=False
        self.dtr=self.rts=self.rtscts=self.dsrdtr=True
    def open(self):
        assert not self.dtr and not self.rts and not self.rtscts and not self.dsrdtr
        assert self.port=='COM14' and self.baudrate==115200 and self.timeout>0
        self.opened=True
    def read(self,_):
        try: return next(self.chunks)
        except StopIteration: raise KeyboardInterrupt
    def close(self): self.closed=True

for first_count in (16,11999,12000,12304):
    data=qual(first_count)
    for split in range(1,len(data)):
        writes=[]; observed=[]
        try:
            monitor.capture_verified(SerialMock([data[:split],data[split:]]),'ESP01',writes.append,
                clock=lambda:0,accepted_before_open=12000,on_continuity=lambda g:observed.append(g.first_accepted_edges))
        except KeyboardInterrupt:
            assert first_count>=12000 and b''.join(writes)==data and observed==[first_count]
        except monitor.BoardIdentityError as exc:
            assert first_count<12000 and 'PORT_OPEN_RESET_DETECTED' in str(exc) and writes==[]
writes=[]
try:
    monitor.capture_verified(SerialMock([qual(12000),qual(16)]),'ESP01',writes.append,
        clock=lambda:0,accepted_before_open=12000)
except monitor.BoardIdentityError as exc:
    assert 'RTC_ACCEPTED_EDGES_DECREASED' in str(exc) and writes==[qual(12000)]
else: raise AssertionError('mid-capture reboot ignored')

class BinaryStdout:
    def __init__(self):self.buffer=io.BytesIO()

with tempfile.TemporaryDirectory() as td:
    td=Path(td);build=td/'build-esp01';(build/'config').mkdir(parents=True)
    (build/'config/sdkconfig.h').write_text('#define CONFIG_FACTORY_DEVICE_ID "ESP01"\n')
    (build/'project_description.json').write_text('{"app_elf":"factory.elf"}')
    (build/'factory.elf').write_bytes(b'test ELF')
    for count in (16,12304):
        uart=SerialMock([qual(count)]);log=td/f'{count}.log';stderr=io.StringIO()
        args=[str(TOOL),'--board','ESP01','--build',str(build),'--port','COM14',
            '--accepted-before-open','12000','--log',str(log)]
        with patch.dict(sys.modules,{'serial':types.SimpleNamespace(Serial=lambda:uart)}),\
                patch.object(sys,'argv',args),patch.object(sys,'stdout',BinaryStdout()),contextlib.redirect_stderr(stderr):
            result=monitor.main()
        assert uart.opened and uart.closed
        assert result==(2 if count<12000 else 0)
        if count<12000: assert not log.exists() and 'PORT_OPEN_RESET_DETECTED' in stderr.getvalue()
        else: assert log.read_bytes()==qual(count) and 'PORT_OPEN_CONTINUITY=VERIFIED' in stderr.getvalue()
print('PASS: four serial settings applied before open; split accepted-count comparison; open/mid-capture reset abort; live logger CLI and cleanup')

# Source checks supplement the included .NET scenarios; no C# execution claimed.
app=ROOT/'windows-controller/FactoryTimer.Controller'
view=(app/'MainViewModel.cs').read_text();device=(app/'DeviceViewModel.cs').read_text()
summary=view[view.index('private void RefreshStartSynchronizationResult('):view.index('private void RefreshSynchronizationResolution()')]
assert 'StartDiagnostics.StartErrorUs(commandId)' in summary and 'StartDiagnostics.SchedulerLatenessUs(commandId)' in summary
assert 'LastStartErrorMicroseconds' not in summary and 'LastSchedulerLatenessMicroseconds' not in summary
assert 'SyncUncertaintyUs=' in summary and 'estimate' in summary and 'never gates START' in summary
status=view[view.index('case StatusPacket status:'):view.index('case StartedPacket started:')]
assert 'RefreshStartSynchronizationResult' in status
gate=device[device.index('public StartParticipantGateInput BuildStartGateInput'):device.index('public bool TryGetIpAddress')]
degraded=view[view.index('private bool CanReusePendingDegradedSync'):view.index('private void SendStartAtAsync') if 'private void SendStartAtAsync' in view else view.index('private async Task SendStartAtAsync(')]
for name in ('StartDiagnostics','LastStartError','UncorrectedLocalClockPhase'):
    assert name not in gate and name not in degraded
serial=(app/'AnalyzerSerialClient.cs').read_text();opening=serial[:serial.index('serial.Open();')]
assert 'DtrEnable = false' in opening and 'RtsEnable = false' in opening and 'Handshake = Handshake.None' in opening
assert 'WarmupMaxRateChangePpm,WarmupRequiredFitPoints,ControllerSessionId' in view
assert 'Guid.NewGuid().ToString("N")' in view
core=(ROOT/'windows-controller/FactoryTimer.Controller.Core/RtcWarmupHistory.cs').read_text()
assert 'MaximumRateChangePpm = 0.25' in core and 'RequiredFitPoints = 129' in core
assert 'status.RtcFitPoints == RequiredFitPoints' in core and 'current.Status.RtcFitPoints == RtcWarmupHistory.RequiredFitPoints' in core
assert 'validator limit' not in core
print('V6_22_18_QUALIFICATION_POLICY=PASS: Python execution and C# UI/gate/serial integration checked; C# tests included, require dotnet test')
