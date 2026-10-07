#!/usr/bin/env python3
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
proto=(ROOT/'windows-controller/FactoryTimer.Protocol/FactoryProtocol.cs').read_text()
vm=(ROOT/'windows-controller/FactoryTimer.Controller/MainViewModel.cs').read_text()
sumtool=(ROOT/'tools/summarize_fleet_cpu0_monitor.py').read_text()
valtool=(ROOT/'tools/validate_cpu0_telemetry_canary_csv.py').read_text()
tests=(ROOT/'tests/FactoryTimer.Protocol.Tests/FactoryProtocolTests.cs').read_text()
checks={
 'wire remains 42-field compatible': 'fields.Length == 42' in proto,
 'legacy STATUS compatibility retained': all(f'fields.Length == {n}' in proto for n in (9,10,14,28,31)),
 'StatusFieldCount recorded by parser': 'StatusFieldCount = 0' in proto and 'fields.Length);' in proto,
 'StatusFieldCount CSV column': 'StatusFieldCount' in vm and 'FirmwareBuildId,StatusFieldCount' in vm,
 'board hours from elapsed monitor window': 'clean_monitor_elapsed_us / 3_600_000_000.0' in sumtool,
 '251us cadence': '1_000_000.0 / 251.0' in sumtool,
 'sample-rate warning': 'sample_rate_warn_rows' in sumtool and '--sample-rate-tolerance-pct' in sumtool,
 'legacy STATUS warning': 'legacy_status_rows' in sumtool and 'FLEET_LEGACY_STATUS_ROWS' in sumtool,
 'telemetry canary validator': 'CPU0_TELEMETRY_CANARY_PATH' in valtool,
 'overlap excluded from gate': "'COMMIT_OVERLAP':" not in valtool,
 'protocol test checks field count': 'Assert.AreEqual(42, status.StatusFieldCount);' in tests and 'Assert.AreEqual(44, status.StatusFieldCount);' in tests,
}
failed=[]
for name,ok in checks.items():
 print(('PASS' if ok else 'FAIL')+': '+name)
 if not ok: failed.append(name)
if failed:
 print(f'FAIL: {failed}')
 raise SystemExit(1)
print(f'PASS: {len(checks)}/{len(checks)} v6.22.10 controller checks')
