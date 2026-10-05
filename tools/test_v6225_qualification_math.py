#!/usr/bin/env python3
"""Deterministic smoke test for the two-START disciplined-rate formula."""
from __future__ import annotations

# 30 minutes = 1.8e9 us.  A disciplined clock +5 ppm versus the master makes
# Q = Master - Disciplined decrease by 9000 us over the interval.
dt_us = 1_800_000_000
dq_us = -9_000
q_slope_ppm = dq_us * 1_000_000.0 / dt_us
disciplined_minus_master_ppm = -q_slope_ppm

assert abs(q_slope_ppm - (-5.0)) < 1e-12
assert abs(disciplined_minus_master_ppm - 5.0) < 1e-12

# Common PC/master-rate error cancels when comparing devices to the batch
# median.  Example absolute device-vs-master rates 3, 5, 7 ppm -> deltas -2,0,+2.
rates = [3.0, 5.0, 7.0]
median = sorted(rates)[1]
deltas = [x - median for x in rates]
assert deltas == [-2.0, 0.0, 2.0]

print("PASS: disciplined-rate sign and 30-minute ppm conversion")
print("PASS: batch-median common-mode cancellation")
