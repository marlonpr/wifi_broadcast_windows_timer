#!/usr/bin/env python3
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = (ROOT / "windows-controller/FactoryTimer.Controller/MainViewModel.cs").read_text(encoding="utf-8")

checks = []
def check(name, condition):
    checks.append((name, bool(condition)))

check("150 us pooled spread threshold retained",
      "ForwardTop3SpreadRetryLimitMicroseconds = 150" in SRC)
check("cal/ver threshold renamed diagnostic reference",
      "ForwardCalibrationVerificationDeltaReferenceMicroseconds = 500" in SRC and
      "ForwardCalibrationVerificationDeltaRetryLimitMicroseconds" not in SRC)

m = re.search(r"private static bool ForwardSyncNeedsRetry\(.*?\n\s*candidate\.FinalConsensus\.TopSampleSpreadMicroseconds >\n\s*ForwardTop3SpreadRetryLimitMicroseconds;", SRC, re.S)
check("retry predicate is spread-only", m is not None)

check("cumulative calibration pool",
      "calibrationPool" in SRC and "verificationPool" in SRC)
check("retry set sampled together",
      "DeviceViewModel[] retrySet = pending.ToArray();" in SRC and
      "RotateRoundRobinOrder(retrySet, roundSequence)" in SRC)
check("successful evidence appended rather than replaced",
      "destination[device.DeviceId].Add(measurement with" in SRC)
check("per-sample attempt provenance",
      "ForwardAttempt = attempt" in SRC and
      "ForwardPhase = phase" in SRC and
      "ForwardRound = thisRound" in SRC)
check("winning top3 provenance reported",
      "Top3Provenance" in SRC and "top3={candidate.FinalConsensus.Top3Provenance}" in SRC)
check("delta remains in uncertainty",
      "absoluteDelta + finalConsensus.TopSampleSpreadMicroseconds" in SRC)
check("delta explicitly diagnostic only",
      "diagnostic/uncertainty only" in SRC and "diagnostic only" in SRC)
check("serial per-device retry helper removed",
      "SynchronizeDeviceForwardOnlyAsync" not in SRC)
check("best-attempt replacement removed",
      "BetterForwardSyncAttempt" not in SRC and "bestCandidate" not in SRC)
check("degraded fallback uses cumulative latest candidate",
      "pooled retries exhausted; retaining cumulative DEGRADED sync" in SRC)
check("existing 20 ms gate still documented authoritative",
      "20 ms readiness gate remains authoritative" in SRC)

failed = [name for name, ok in checks if not ok]
for name, ok in checks:
    print(f"{'PASS' if ok else 'FAIL'}: {name}")
if failed:
    raise SystemExit(f"{len(failed)} check(s) failed")
print(f"PASS: {len(checks)} v6.22.4 pooled-sync source-contract checks")
