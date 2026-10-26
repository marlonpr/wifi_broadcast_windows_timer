#!/usr/bin/env python3
"""Fault-injected capture tests of the actual production segment validator."""
from pathlib import Path
import subprocess
import sys
import tempfile

TOOL = Path(__file__).with_name('validate_production_segments.py')

def capture(lengths, offsets=(0,20000,-5000), remove=None, dropped=0, footer=True, swap=False):
    lines=[]; events=[]; base=1_000_000
    counts=[0,0,0]
    for train, length in enumerate(lengths,1):
        for boundary in range(length+1):
            for channel in range(1,4):
                if remove == (train,boundary,channel): continue
                name=['ESP01','ESP04','ESP03'][channel-1]
                if swap and channel!=1: name=['ESP01','ESP03','ESP04'][channel-1]
                events.append((base+boundary*1_000_000+offsets[channel-1],channel,name,boundary%2))
                counts[channel-1]+=1
        base += (length+10)*1_000_000
    for sequence,(timestamp,channel,name,level) in enumerate(sorted(events)):
        lines.append(f'ANZ|QUALIFIED|1|1|{channel}|{name}_COMMIT|q={sequence}|{timestamp}|{level}')
    if footer:
        lines += [f'ANZ|SUMMARY|1|1|0x07|3|trains={len(lengths)}',
            'ANZ|COUNTS|1|1|'+'|'.join(f'{["ESP01","ESP04","ESP03"][i]}_QUAL={n}' for i,n in enumerate(counts))+f'|dropped={dropped}',
            f'ANZ|TRIPWIRE|1|1|mode=START_ONLY|trains={len(lengths)}|incomplete_groups=0|start_trips=3|result=FAIL']
    return '\n'.join(lines)+'\n'

with tempfile.TemporaryDirectory() as directory:
    p=Path(directory)/'capture.txt'
    cases=[('clean repeats and resync',[540,540,540,180],{},0),
           ('exact6000s',[6000],{},0),
           ('two complete 40-minute trains',[2400,2400],{'offsets':(0,1830,10)},0),
           ('missing START',[15],{'remove':(1,0,2)},2),
           ('missing COMMIT',[15],{'remove':(1,5,1)},2),
           ('missing END',[15],{'footer':False},3),
           ('dropped events',[15],{'dropped':1},2),
           ('production skew exceeded',[15],{'offsets':(0,31000,0)},2),
           ('wrong channel map',[15],{'swap':True},2),
           ('beyond 40ms association',[15],{'offsets':(0,40001,0)},2)]
    for name,lengths,options,expected in cases:
        p.write_text(capture(lengths,**options))
        result=subprocess.run([sys.executable,str(TOOL),str(p),'--segment-seconds',','.join(map(str,lengths))],capture_output=True,text=True)
        assert result.returncode==expected, (name,result.stdout,result.stderr)
        print(f'PASS: {name}, exit={result.returncode}')
    for span, expected in ((5001,0),(29999,0),(30000,0),(30001,2),(39999,2),(40000,2)):
        p.write_text(capture([15],offsets=(0,span,0)))
        result=subprocess.run([sys.executable,str(TOOL),str(p),'--segment-seconds','15'],capture_output=True,text=True)
        assert result.returncode==expected, (span,result.stdout,result.stderr)
        assert 'CAPTURE_STRUCTURE=COMPLETE' in result.stdout and 'BOUNDARIES=16' in result.stdout, result.stdout
        assert 'malformed END counters' not in result.stdout, result.stdout
        print(f'PASS: complete {span}us skew, production exit={expected}')
    p.write_text(capture([15],offsets=(0,40001,0)))
    incomplete=subprocess.run([sys.executable,str(TOOL),str(p),'--segment-seconds','15'],capture_output=True,text=True)
    assert incomplete.returncode==2 and 'CAPTURE_STRUCTURE=INCOMPLETE' in incomplete.stdout, incomplete.stdout
    assert 'incomplete groups=' in incomplete.stdout and 'PRODUCTION_MARKER_GATE=PASS' not in incomplete.stdout, incomplete.stdout
    p.write_text(capture([15]))
    sync=Path(directory)/'sync.csv';sync.write_text('Device,Bssid\nESP01,02:00:00:00:00:01\nESP04,02:00:00:00:00:02\nESP03,02:00:00:00:00:03\n')
    command=[sys.executable,str(TOOL),str(p),'--segment-seconds','15','--sync-csv',str(sync)]
    good=subprocess.run(command,capture_output=True,text=True);assert good.returncode==0,good.stdout
    sync.write_text(sync.read_text().replace('02:00:00:00:00:03','02:00:00:00:00:02'))
    bad=subprocess.run(command,capture_output=True,text=True);assert bad.returncode==2,bad.stdout
    print('PASS: three-AP identity proof and duplicate-AP failure')
    config_tool = TOOL.parents[2]/'analyzer_v10_1/tools/verify_production_config.py'
    production_profile = TOOL.parents[2]/'analyzer_v10_1/sdkconfig.production_0143'
    generated = Path(directory)/'sdkconfig'
    generated.write_text('CONFIG_IDF_TARGET="esp32s3"\n'+production_profile.read_text())
    cfg_command = [sys.executable,str(config_tool),str(generated)]
    good=subprocess.run(cfg_command,capture_output=True,text=True);assert good.returncode==0,good.stdout
    original=generated.read_text()
    for before, after in (('ASSOCIATION_WINDOW_US=40000','ASSOCIATION_WINDOW_US=5000'),
                          ('CHANNEL2_NAME="ESP04"','CHANNEL2_NAME="ESP02"'),
                          ('IDF_TARGET="esp32s3"','IDF_TARGET="esp32"')):
        generated.write_text(original.replace(before,after))
        bad=subprocess.run(cfg_command,capture_output=True,text=True);assert bad.returncode==2,bad.stdout
    generated.write_text(original)
    edge_file=Path(directory)/'edge.txt'
    for name, expected in (('ESP04_COMMIT',0),('ESP02_COMMIT',2)):
        edge_file.write_text(f'ANZ|EDGE|1|1|2|{name}|1000000|0\n')
        result=subprocess.run(cfg_command+['--capture',str(edge_file)],capture_output=True,text=True)
        assert result.returncode==expected,result.stdout
    print('PASS: generated config checks reject wrong window, channel and target; first CH2 EDGE identity verified')
print('PRODUCTION_SEGMENT_CAPTURE_TESTS=PASS CASES=24')
