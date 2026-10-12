#!/usr/bin/env python3
"""Test modern controller health CSV guards and the monitor-only build identity check.

The C# history/state-machine tests are in RtcWarmupHistoryTests.cs; this portable
check exercises the actual Python consumers and verifies their UI integration.
"""
from pathlib import Path
from dataclasses import replace
import csv
import importlib.util
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from qualify_rtc_batch import read_start_rows, build_pair_row, controller_warmup_proof_ok

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT.parent


def rows(second=False):
    return [dict(RunCommandId="0000000000000002" if second else "0000000000000001",
        TStarMasterUs=6_000_000 + int(second)*2_460_000_000, DeviceId=f"ESP{n:02}", Phase="START",
        StatusCaptured=1, CapturedMasterUs=1_100_000 + int(second)*2_460_000_000,
        RtcState="LOCKED", SyncEpochMasterUs=1_000_000 + int(second)*2_460_000_000,
        SyncEpochLocalUs=2_000_000 + int(second)*2_460_000_000, SyncEpochMasterMinusDisciplinedUs=50_000,
        RtcRatePpmVsRtc=-6, RtcFitPoints=129, RtcFitRmsUs=0.5, RtcFitOutliers=0,
        RtcAcceptedEdges=1000 + int(second)*2460, RtcInferredMissingEdges=0, RtcHoldoverEntries=0,
        RtcQueueDrops=0, RtcTemperatureValid=1, RtcTemperatureC=25.25, RtcSqwCore=1, HealthFlags=7,
        DiagnosticFlags="0x00", QualificationPhase="SECOND" if second else "FIRST",
        WarmupFirstCommandId="0000000000000001", WarmupHistorySeconds=608,
        WarmupRateChangePpm=0.25, WarmupGoodFitPolls=3, WarmupReady=1,
        FirmwareBuildId="production-build", ControllerSessionId="same-controller",
        WarmupMaxRateChangePpm=0.25, WarmupRequiredFitPoints=129) for n in range(1, 6)]


def write(path, data):
    with path.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(data[0]))
        writer.writeheader(); writer.writerows(data)


with tempfile.TemporaryDirectory() as td:
    td = Path(td)
    first, second = td / "first.csv", td / "second.csv"
    arows, brows = rows(), rows(True)
    write(first, arows); write(second, brows)
    a, b = read_start_rows(first), read_start_rows(second)
    assert len(a) == len(b) == 5
    assert all(controller_warmup_proof_ok(a[d], b[d]) for d in a)
    for d in a:
        result = build_pair_row(a[d], b[d], min_interval_seconds=2400, same_boot_tolerance_ppm=100,
            accepted_edge_tolerance=2, warm_confirmed=False)
        assert result["QualificationValid"] and result["ControllerWarmupProof"], result
    command = [sys.executable, str(ROOT / "tools/qualify_rtc_batch.py"), str(first), str(second),
        "--min-interval-seconds", "2400"]
    clean = subprocess.run(command, text=True, capture_output=True)
    assert clean.returncode == 0, clean.stdout + clean.stderr
    # The two boards without serial captures must still be audited and gate the batch.
    for device in ("ESP02", "ESP05"):
        for column, bad in (("WarmupHistorySeconds", 592), ("WarmupRateChangePpm", 0.250001),
            ("WarmupGoodFitPolls", 2), ("WarmupReady", 0), ("RtcFitRmsUs", 2.4),
            ("DiagnosticFlags", "0x04"), ("QualificationPhase", "PREFLIGHT"),
            ("WarmupFirstCommandId", "000000000000FFFF")):
            badrows = [dict(r) for r in arows]
            next(r for r in badrows if r["DeviceId"] == device)[column] = bad
            write(first, badrows)
            failure = subprocess.run(command + ["--warm-confirmed"], text=True, capture_output=True)
            assert failure.returncode != 0, (device, column, failure.stdout, failure.stderr)
        write(first, arows)
    missing = [dict(r) for r in arows]; missing[4]["StatusCaptured"] = 0
    write(first, missing)
    try:
        read_start_rows(first)
    except ValueError as exc:
        assert "ESP05" in str(exc) and "no captured START STATUS" in str(exc)
    else:
        raise AssertionError("missing frozen participant silently omitted")
    write(first, arows)
    assert not controller_warmup_proof_ok(a["ESP01"], replace(b["ESP01"], qualification_phase="FIRST"))
    assert not controller_warmup_proof_ok(a["ESP01"], replace(b["ESP01"], warmup_history_seconds=None))

    # Modern readiness metadata must also block the analyzer before capture I/O.
    badrows = [dict(r) for r in arows]; badrows[0]["WarmupReady"] = 0
    write(first, badrows)
    result = subprocess.run([sys.executable, str(ROOT / "tools/validate_rtc_qualification_vs_analyzer.py"),
        str(first), str(second), str(td / "absent.txt"), "--warm-confirmed"], text=True, capture_output=True)
    assert result.returncode != 0 and "ESP01" in result.stderr and "FileNotFoundError" not in result.stderr, result.stderr

    tool = BUNDLE / "firmware_v6_23_16/tools/monitor_board.py"
    spec = importlib.util.spec_from_file_location("monitor_board", tool)
    monitor = importlib.util.module_from_spec(spec); spec.loader.exec_module(monitor)
    build = td / "build-esp01"; (build / "config").mkdir(parents=True)
    (build / "config/sdkconfig.h").write_text('#define CONFIG_FACTORY_DEVICE_ID "ESP01"\n')
    (build / "project_description.json").write_text('{"app_elf":"factory.elf"}')
    (build / "factory.elf").write_bytes(b"fake ELF, identity fixture")
    assert monitor.verify_build(build, "ESP01") == build / "factory.elf"
    try:
        monitor.verify_build(build, "ESP03")
    except ValueError as exc:
        assert "build identity is ESP01" in str(exc)
    else:
        raise AssertionError("wrong board ELF accepted")
    result = subprocess.run([sys.executable, str(tool), "--build", str(build), "--board", "ESP01", "--check-only"],
        text=True, capture_output=True)
    assert result.returncode == 0 and "MONITOR_ONLY" in result.stderr

# Cross-file checks supplement, and do not replace, the pending .NET tests.
app = ROOT / "windows-controller/FactoryTimer.Controller"
view = (app / "MainViewModel.cs").read_text()
start = view[view.index("private async Task SendStartAtAsync("):view.index("private async Task<IReadOnlyList<StartBlock>> RefreshAndValidatePreflightAsync")]
assert start.index("ValidateQualificationWarmup") < start.index("SynchronizeParticipantsForwardOnlyRoundRobinAsync")
assert start.count("ValidateQualificationWarmup") == 2
assert "RequireWarmupHealth: qualificationMode is QualificationStartMode.First or QualificationStartMode.Second" in start
assert "QualificationWarmupProof.HealthyContinuation" in view
assert "e.MasterReceiveMicroseconds" in view and "Devices.Where(device => device.IsSelected)" in view
core = ROOT / "windows-controller/FactoryTimer.Controller.Core"
assert "FromSeconds(16)" in (core / "StatusDiscoveryService.cs").read_text()
assert "FromSeconds(48)" in (core / "DeviceStatusTracker.cs").read_text()
ET.parse(app / "MainWindow.xaml")
assert "{Binding WarmupText}" in (app / "MainWindow.xaml").read_text()
assert "{Binding QualificationGateText}" in (app / "MainWindow.xaml").read_text()
assert (ROOT / "tests/FactoryTimer.Controller.Core.Tests/RtcWarmupHistoryTests.cs").is_file()
print("V6_22_16_CONTROLLER_WARMUP=PASS: five-device modern CSV proof; inclusive thresholds; "
      "ESP02/05 cannot bypass with --warm-confirmed; analyzer guard; monitor identity; C# integration wiring")
