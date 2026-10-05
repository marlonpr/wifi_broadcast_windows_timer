#!/usr/bin/env python3
from pathlib import Path
import importlib.util
import sys

ROOT=Path(__file__).resolve().parents[1]
Q=ROOT/'tools/qualify_rtc_batch.py'
spec=importlib.util.spec_from_file_location('qual_rate_guard', Q)
mod=importlib.util.module_from_spec(spec); sys.modules[spec.name]=mod; spec.loader.exec_module(mod)
StartHealth=mod.StartHealth

DT=1_800_000_000

def row(rate, *, q=100_000, master=1_000_000, local=2_000_000, captured=1_100_000, accepted=100):
    return StartHealth(
        path=Path('x'), device='ESP01', command_id='1', captured_master_us=captured,
        master_epoch_us=master, local_epoch_us=local, master_minus_disciplined_us=q,
        rtc_state='LOCKED', rtc_rate_ppm_vs_rtc=rate, rtc_fit_points=129,
        rtc_fit_rms_us=0.5, fit_outliers=0, accepted_edges=accepted,
        inferred_missing_edges=0, holdover_entries=0, queue_drops=0,
        temperature_valid=True, temperature_c=25.0, sqw_core=1, health_flags=1)

def pair(rate1, rate2):
    a=row(rate1)
    b=row(rate2, q=91_000, master=1_000_000+DT, local=2_000_000+DT+9000,
          captured=1_100_000+DT, accepted=1900)
    return mod.build_pair_row(
        a,b,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
        accepted_edge_tolerance=2,warm_confirmed=True,
        max_rate_change_ppm=0.25,fit_lag_seconds=64.0)

r=pair(4.00,4.25)
assert r['RateStabilityOK'] and r['QualificationValid']
assert abs(r['RtcRateDeltaPpm']-0.25) < 1e-12
assert abs(r['EstimatedFitLagPhaseBiasUs']-16.0) < 1e-12
assert abs(r['EstimatedFitLagSlopeBiasPpm']-(16.0/1800.0)) < 1e-12

r=pair(4.00,4.251)
assert not r['RateStabilityOK'] and not r['QualificationValid']

r=pair(4.00,5.00)
assert not r['QualificationValid']
assert abs(r['EstimatedFitLagSlopeBiasPpm']-(64.0/1800.0)) < 1e-12

# Manual warm confirmation remains necessary even when the measured rate is stable.
a=row(4.0)
b=row(4.1, q=91_000, master=1_000_000+DT, local=2_000_000+DT+9000,
      captured=1_100_000+DT, accepted=1900)
r=mod.build_pair_row(a,b,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
                     accepted_edge_tolerance=2,warm_confirmed=False,
                     max_rate_change_ppm=0.25,fit_lag_seconds=64.0)
assert r['RateStabilityOK'] and not r['QualificationValid']

print('PASS: <=0.25 ppm endpoint-rate change accepted')
print('PASS: >0.25 ppm endpoint-rate change rejected')
print('PASS: 64 s fit-lag bias estimate')
print('PASS: procedural --warm-confirmed remains independently required')
