from pathlib import Path

root = Path(__file__).resolve().parents[1]
proto = (root / 'windows-controller/FactoryTimer.Protocol/FactoryProtocol.cs').read_text()
vm = (root / 'windows-controller/FactoryTimer.Controller/MainViewModel.cs').read_text()
tests = (root / 'tests/FactoryTimer.Protocol.Tests/FactoryProtocolTests.cs').read_text()

checks = {
    '42-field STATUS accepted': 'fields.Length == 42' in proto,
    '31-field STATUS still accepted': 'fields.Length == 31' in proto,
    'hex monitor sample parser': 'NumberStyles.HexNumber' in proto and 'Cpu0MonitorSamples' in proto,
    'monitor validity parsed': 'Cpu0MonitorValid' in proto,
    'monitor worst task parsed': 'Cpu0MonitorWorstTask' in proto,
    'commit overlap parsed': 'Cpu0CommitOverlap' in proto,
    'wrong core parsed': 'Cpu0WrongCoreCallbacks' in proto,
    'overflow parsed': 'Cpu0MonitorOverflow' in proto,
    'interrupt level match parsed': 'Cpu0InterruptLevelMatch' in proto,
    'CSV monitor columns': 'Cpu0MonitorEventCount,Cpu0MonitorWorstUs,Cpu0MonitorWorstTask' in vm,
    'CSV commit columns': 'Cpu0CommitLateCount,Cpu0CommitWorstUs,Cpu0CommitOverlap' in vm,
    'protocol regression test added': 'ParsesFleetCpu0MonitorStatus' in tests,
}
failed=[]
for name, ok in checks.items():
    print(f'{"PASS" if ok else "FAIL"}: {name}')
    if not ok: failed.append(name)
if failed:
    raise SystemExit(f'{len(failed)} controller telemetry checks failed: {failed}')
print(f'PASS: {len(checks)}/{len(checks)} v6.22.9 controller telemetry source checks')
