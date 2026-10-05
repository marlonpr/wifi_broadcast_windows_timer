#!/usr/bin/env python3
from pathlib import Path
import importlib.util
import math
import sys

ROOT=Path(__file__).resolve().parents[1]
Q=ROOT/'tools/qualify_rtc_batch.py'
spec=importlib.util.spec_from_file_location('qual_acceptance', Q)
mod=importlib.util.module_from_spec(spec); sys.modules[spec.name]=mod; spec.loader.exec_module(mod)

assert mod.classify_module_acceptance(+1.0,1.0,2.0)=='ACCEPT'
assert mod.classify_module_acceptance(-1.0,1.0,2.0)=='ACCEPT'
assert mod.classify_module_acceptance(+1.000001,1.0,2.0)=='INSPECT'
assert mod.classify_module_acceptance(-2.0,1.0,2.0)=='INSPECT'
assert mod.classify_module_acceptance(+2.000001,1.0,2.0)=='REJECT'
assert mod.classify_module_acceptance(math.nan,1.0,2.0)=='INVALID'

# 99:59 engineering budget: accepted extremes differ by at most 2 ppm.
assert abs(2.0 * 5999 - 11998.0) < 1e-12  # ppm*s -> us
print('PASS: ±1 ppm ACCEPT, >1..±2 ppm INSPECT, >±2 ppm REJECT')
print('PASS: 2 ppm * 5999 s = 11998 us pair drift budget')
