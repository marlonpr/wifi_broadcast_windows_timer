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
           ('wrong channel map',[15],{'swap':True},2)]
    for name,lengths,options,expected in cases:
        p.write_text(capture(lengths,**options))
        result=subprocess.run([sys.executable,str(TOOL),str(p),'--segment-seconds',','.join(map(str,lengths))],capture_output=True,text=True)
        assert result.returncode==expected, (name,result.stdout,result.stderr)
        print(f'PASS: {name}, exit={result.returncode}')
    p.write_text(capture([15]))
    sync=Path(directory)/'sync.csv';sync.write_text('Device,Bssid\nESP01,02:00:00:00:00:01\nESP04,02:00:00:00:00:02\nESP03,02:00:00:00:00:03\n')
    command=[sys.executable,str(TOOL),str(p),'--segment-seconds','15','--sync-csv',str(sync)]
    good=subprocess.run(command,capture_output=True,text=True);assert good.returncode==0,good.stdout
    sync.write_text(sync.read_text().replace('02:00:00:00:00:03','02:00:00:00:00:02'))
    bad=subprocess.run(command,capture_output=True,text=True);assert bad.returncode==2,bad.stdout
    print('PASS: three-AP identity proof and duplicate-AP failure')
print('PRODUCTION_SEGMENT_CAPTURE_TESTS=PASS CASES=11')
