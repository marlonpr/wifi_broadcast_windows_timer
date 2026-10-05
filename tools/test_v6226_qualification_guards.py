#!/usr/bin/env python3
from pathlib import Path
import importlib.util
import sys

ROOT=Path(__file__).resolve().parents[1]
Q=ROOT/'tools/qualify_rtc_batch.py'
spec=importlib.util.spec_from_file_location('qual', Q)
mod=importlib.util.module_from_spec(spec); sys.modules[spec.name]=mod; spec.loader.exec_module(mod)
StartHealth=mod.StartHealth

def row(dev, master, local, q, captured, accepted, missing=0, hold=0, state='LOCKED'):
    return StartHealth(
        path=Path('x'), device=dev, command_id='1', captured_master_us=captured,
        master_epoch_us=master, local_epoch_us=local, master_minus_disciplined_us=q,
        rtc_state=state, rtc_rate_ppm_vs_rtc=0.0, rtc_fit_points=129,
        rtc_fit_rms_us=0.5, fit_outliers=0, accepted_edges=accepted,
        inferred_missing_edges=missing, holdover_entries=hold, queue_drops=0,
        temperature_valid=True, temperature_c=25.0, sqw_core=1, health_flags=1)

DT=1_800_000_000
# +5 ppm disciplined-vs-master => Q decreases 9000 us.
a=row('ESP01', 1_000_000, 2_000_000, 100_000, 1_100_000, 100)
b=row('ESP01', 1_000_000+DT, 2_000_000+DT+9000, 91_000,
      1_100_000+DT, 1900)
r=mod.build_pair_row(a,b,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
                     accepted_edge_tolerance=2,warm_confirmed=True)
assert abs(r['DisciplinedMinusMasterPpm']-5.0) < 1e-9
assert r['SameBootOK']
assert r['ContinuityOK']
assert r['QualificationValid']

# Reboot: local epoch no longer tracks master interval.
b_reboot=row('ESP01', a.master_epoch_us+DT, 50_000_000, 91_000,
             a.captured_master_us+DT, 1900)
r=mod.build_pair_row(a,b_reboot,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
                     accepted_edge_tolerance=2,warm_confirmed=True)
assert not r['SameBootOK'] and not r['QualificationValid']

# Holdover/missing SQW: reject even when endpoints are LOCKED again.
b_hold=row('ESP01', a.master_epoch_us+DT, a.local_epoch_us+DT, 91_000,
           a.captured_master_us+DT, 1899, missing=1, hold=1)
r=mod.build_pair_row(a,b_hold,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
                     accepted_edge_tolerance=2,warm_confirmed=True)
assert not r['NoInferredMissingOK']
assert not r['NoHoldoverOK']
assert not r['QualificationValid']

# Warm confirmation is explicit because two endpoint snapshots cannot prove thermal equilibrium.
r=mod.build_pair_row(a,b,min_interval_seconds=1200,same_boot_tolerance_ppm=100,
                     accepted_edge_tolerance=2,warm_confirmed=False)
assert not r['QualificationValid']

print('PASS: disciplined-rate sign')
print('PASS: same-boot guard')
print('PASS: SQW continuity/holdover guards')
print('PASS: explicit warm confirmation guard')
