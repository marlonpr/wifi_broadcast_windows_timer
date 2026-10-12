#!/usr/bin/env python3
"""Exercise saved qualification data, diagnostic contamination and strict warm-up."""
from dataclasses import replace
from pathlib import Path
import csv
import subprocess
import sys
import tempfile

from diagnostic_flags import parse_flags, describe_flags
from qualify_rtc_batch import StartHealth, endpoint_health_ok, build_pair_row, read_start_rows
from monitor_test_fixtures import row, summarize

ROOT = Path(__file__).resolve().parents[1]
for value in range(256):
    assert parse_flags(f"0x{value:02X}") == value
    assert parse_flags(f"{value:02x}") == value
assert parse_flags(None) is None and parse_flags("") is None
for invalid in ("0", "000", "0x4", "0x000", "GG", "+4", "-1"):
    try:
        parse_flags(invalid)
    except ValueError:
        pass
    else:
        raise AssertionError(invalid)
assert "START_TASK_DELAY_TEST" in describe_flags(4)
assert "UNKNOWN(0x80)" in describe_flags(128)

first = StartHealth(path=Path("first.csv"), device="ESP01", command_id="1",
    captured_master_us=1_100_000, master_epoch_us=1_000_000, local_epoch_us=2_000_000,
    master_minus_disciplined_us=50_000, rtc_state="LOCKED", rtc_rate_ppm_vs_rtc=0,
    rtc_fit_points=129, rtc_fit_rms_us=0.5, fit_outliers=0, accepted_edges=1000,
    inferred_missing_edges=0, holdover_entries=0, queue_drops=0, temperature_valid=True,
    temperature_c=25, sqw_core=1, health_flags=7, diagnostic_flags=0, firmware_build_id="production-build")
second = replace(first, path=Path("second.csv"), command_id="2",
    captured_master_us=2_461_100_000, master_epoch_us=2_461_000_000,
    local_epoch_us=2_462_000_000, accepted_edges=3460)

def qualify(a, b):
    return build_pair_row(a, b, min_interval_seconds=2400, same_boot_tolerance_ppm=100,
        accepted_edge_tolerance=2, warm_confirmed=True)

assert endpoint_health_ok(first) and qualify(first, second)["QualificationValid"]
for flags in (None, *range(1, 256)):
    bad = replace(first, diagnostic_flags=flags)
    assert not endpoint_health_ok(bad)
    assert not qualify(bad, second)["QualificationValid"]
    assert not qualify(first, replace(second, diagnostic_flags=flags))["QualificationValid"]

with tempfile.TemporaryDirectory() as td:
    folder = Path(td)
    # Both task and ISR canaries must be identified even when Wi-Fi wins the
    # worst-event slot. Missing/invalid/unknown bits cannot supply clean hours.
    for flags in ("0x00", "0x01", "0x02", "0x04", "0x08", "0x10", "0x80", "", "GG"):
        output = summarize([row(DiagnosticFlags=flags, Cpu0MonitorWorstTask="wifi")], td)
        clean = flags == "0x00"
        assert f"FLEET_DIAGNOSTIC_BUILD_GUARD={'PASS' if clean else 'FAIL'}" in output, output
        assert f"ZERO_GE300_ROLLOUT_ITEM={'PASS' if clean else 'OPEN'}" in output, output
        if not clean:
            assert "FLEET_CLEAN_MONITORED_HOURS=0.000000" in output, output
        if flags in ("0x01", "0x02"):
            assert "FLEET_LAT_CANARY_ROWS=1" in output, output

    def write_health(path, index, flags):
        rows = []
        for device in ("ESP01", "ESP03", "ESP04"):
            rows.append(dict(RunCommandId=f"{index+1:016X}", TStarMasterUs=6_000_000+index*2_460_000_000,
                DeviceId=device, Phase="START", StatusCaptured=1, CapturedMasterUs=1_100_000+index*2_460_000_000,
                RtcState="LOCKED", SyncEpochMasterUs=1_000_000+index*2_460_000_000,
                SyncEpochLocalUs=2_000_000+index*2_460_000_000, SyncEpochMasterMinusDisciplinedUs=50_000,
                RtcRatePpmVsRtc=0, RtcFitPoints=129, RtcFitRmsUs=0.5, RtcFitOutliers=0,
                RtcAcceptedEdges=1000+index*2460, RtcInferredMissingEdges=0, RtcHoldoverEntries=0,
                RtcQueueDrops=0, RtcTemperatureValid=1, RtcTemperatureC=25, RtcSqwCore=1, HealthFlags=7,
                DiagnosticFlags=flags if device == "ESP01" else "0x00", FirmwareBuildId="production-build"))
        with path.open("w", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=list(rows[0]))
            writer.writeheader(); writer.writerows(rows)

    a, b = folder / "first.csv", folder / "second.csv"
    write_health(a, 0, "0x00"); write_health(b, 1, "0x00")
    assert read_start_rows(a)["ESP01"].diagnostic_flags == 0
    command = [sys.executable, str(ROOT / "tools/qualify_rtc_batch.py"), str(a), str(b),
        "--warm-confirmed", "--min-interval-seconds", "2400"]
    good = subprocess.run(command, text=True, capture_output=True)
    assert good.returncode == 0, good.stdout + good.stderr
    for index, flags in ((0, "0x04"), (1, "0x02"), (1, ""), (1, "0x80")):
        write_health(a, 0, flags if index == 0 else "0x00")
        write_health(b, 1, flags if index == 1 else "0x00")
        bad = subprocess.run(command, text=True, capture_output=True)
        assert bad.returncode != 0 and "diagnostic build guard failed" in bad.stdout, bad.stdout + bad.stderr
        # The three-pair validator must stop on the endpoint guard before even
        # opening an analyzer capture. The absent file makes that ordering observable.
        analyzer = subprocess.run([sys.executable, str(ROOT / "tools/validate_rtc_qualification_vs_analyzer.py"),
            str(a), str(b), str(folder / "absent_capture.txt"), "--warm-confirmed"], text=True, capture_output=True)
        assert analyzer.returncode != 0 and "ESP01: diagnostic build guard failed" in analyzer.stderr, analyzer.stdout + analyzer.stderr

    # The revised warm-up endpoint threshold is inclusive; above 0.25 ppm waits.
    for movement, expected_exit in ((0.2499, 0), (0.25, 0), (0.2501, 2)):
        log = folder / "warmup.log"
        log.write_text("\n".join(
            f"RTC_QUAL device=ESP01 command=0 local_us={n*20_000_000} state=LOCKED "
            f"rate_ppm={movement*n/30:.9f} rms_us=0.500 points=129 accepted={n*20} rejected=0 "
            f"fit_outliers=0 inferred_missing=0 holdover_entries=0 queue_drops=0 "
            f"temp_valid=1 temp_c=25.00 sqw_core=1" for n in range(31)))
        result = subprocess.run([sys.executable, str(ROOT / "tools/rtc_warmup_gate.py"), str(log)], text=True, capture_output=True)
        assert result.returncode == expected_exit, (movement, result.stdout + result.stderr)
        if expected_exit:
            assert "10-min dRate" in result.stdout, result.stdout

print("V6_22_15_DIAGNOSTIC_GUARDS=PASS: all nonzero bytes and missing flags reject; "
      "both endpoint CLIs reject before capture; hidden task/ISR canaries excluded; inclusive 0.25 ppm warm-up")
