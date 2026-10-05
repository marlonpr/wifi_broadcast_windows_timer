#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROTO = (ROOT / "windows-controller/FactoryTimer.Protocol/FactoryProtocol.cs").read_text(encoding="utf-8")
VM = (ROOT / "windows-controller/FactoryTimer.Controller/MainViewModel.cs").read_text(encoding="utf-8")
DOC = (ROOT / "V6_22_5_FLEET_HEALTH.md").read_text(encoding="utf-8")
QUAL = (ROOT / "tools/qualify_rtc_batch.py").read_text(encoding="utf-8")

checks = []
def check(name, value):
    checks.append((name, bool(value)))

check("protocol packet length expanded", "MaximumPacketLength = 511" in PROTO)
check("legacy, 28-field, and 31-field STATUS accepted",
      "fields.Length == 9 || fields.Length == 10 || fields.Length == 14" in PROTO and
      "fields.Length == 28" in PROTO and "fields.Length == 31" in PROTO)
check("disciplined sync fields parsed",
      "SyncEpochDisciplinedMicroseconds" in PROTO and
      "SyncMasterMinusDisciplinedMicroseconds" in PROTO)
check("health flags gate optional groups", "(flags & 0x01) != 0" in PROTO and
      "(flags & 0x02) != 0" in PROTO and "(flags & 0x04) != 0" in PROTO)
check("health companion CSV written", "controller_health_" in VM and
      "WriteControllerHealthCsvAsync" in VM)
check("post-run health is actively fetched",
      "CollectPostRunHealthAsync" in VM and "HEALTH_STATUS_REQUEST" in VM)
check("START and POST_RUN rows emitted", 'deviceId, "START", start' in VM and
      'deviceId, "POST_RUN", post' in VM)
check("source offset + local forms master epoch",
      "status.SyncSourceOffsetMicroseconds.Value +\n                    status.SyncEpochLocalMicroseconds.Value" in VM)
check("qualification uses Master-minus-Disciplined", "SyncEpochMasterMinusDisciplinedUs" in QUAL)
check("qualification avoids raw offset slope", "do **not** qualify" in DOC.lower() and "offset_master_minus_local_us" in DOC)
check("v6.22.4 pooled policy test retained", (ROOT / "tools/test_v6224_pooled_sync_policy.py").exists())

failed = [name for name, ok in checks if not ok]
for name, ok in checks:
    print(f"{'PASS' if ok else 'FAIL'}: {name}")
if failed:
    raise SystemExit(f"{len(failed)} check(s) failed")
print(f"PASS: {len(checks)} v6.22.6 fleet-health source-contract checks")
