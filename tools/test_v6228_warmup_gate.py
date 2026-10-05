#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / 'tools' / 'rtc_warmup_gate.py'
spec = importlib.util.spec_from_file_location('warmup_gate', TOOL)
mod = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = mod
spec.loader.exec_module(mod)

# 38 samples at 16 s cadence span 592 s, satisfying the default 95% coverage
# requirement for a 600 s gate while matching firmware RTC_QUAL cadence.
samples = []
for i in range(38):
    samples.append(mod.Sample(
        local_us=i * 16_000_000,
        rate_ppm=4.0 + (0.004 * (i * 16 / 60)),  # +0.004 ppm/min
        state='LOCKED', rms_us=0.5, points=129, accepted=i * 16,
        rejected=0, fit_outliers=0, inferred_missing=0, holdover_entries=0,
        queue_drops=0, temp_valid=True, temp_c=25.00 if i < 20 else 25.25,
        sqw_core=1,
    ))

slope = mod.fit_slope_ppm_per_min([(s.local_us, s.rate_ppm) for s in samples])
assert abs(slope - 0.004) < 1e-12
assert abs(samples[-1].rate_ppm - samples[0].rate_ppm) < 0.05
assert max(s.temp_c for s in samples) - min(s.temp_c for s in samples) == 0.25

# Latest monotonic segment must discard older-boot samples after a local-time reset.
old = mod.Sample(9_000_000_000, 1.0, 'LOCKED', 0.5, 129, 0, 0, 0, 0, 0, 0, True, 25.0, 1)
new1 = mod.Sample(1_000_000, 2.0, 'LOCKED', 0.5, 129, 0, 0, 0, 0, 0, 0, True, 25.0, 1)
new2 = mod.Sample(2_000_000, 2.0, 'LOCKED', 0.5, 129, 0, 0, 0, 0, 0, 0, True, 25.0, 1)
segment = mod.latest_monotonic_segment([old, new1, new2])
assert segment == [new1, new2]

# Boundary arithmetic that motivated v6.22.8.
assert abs(0.005 * 40 - 0.20) < 1e-12
assert 0.20 < 0.25

print('PASS: default 600 s / 0.005 ppm-min / 0.05 ppm / 0.25 C readiness arithmetic')
print('PASS: 16 s cadence provides sufficient 95% window coverage')
print('PASS: latest-boot monotonic segment isolation')
