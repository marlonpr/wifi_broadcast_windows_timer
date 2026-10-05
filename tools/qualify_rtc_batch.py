#!/usr/bin/env python3
"""Qualify DS3231-disciplined countdown rate from two warm START health CSVs.

The production observable is

    Q = Master - Disciplined

at the selected synchronization-sample epoch.  For two observations:

    q_slope_ppm = dQ / dMaster * 1e6
    disciplined_minus_master_ppm ~= -q_slope_ppm

The raw Master-minus-esp_timer offset is intentionally NOT used as the
qualification rate because its slope measures the ESP32 crystal, not the RTC-
disciplined countdown timebase.

v6.22.8 validity guards reject a two-START interval when:
  * the raw local epoch is inconsistent with the master interval (probable reboot),
  * the RTC discipline entered HOLDOVER,
  * inferred-missing SQW edges increased,
  * accepted SQW edge count does not advance at approximately 1 Hz,
  * either endpoint is not a healthy LOCKED RTC state,
  * the fitted local-vs-RTC rate changes too much between the STARTs, or
  * the operator has not confirmed that the fleet was thermally warm/stable.

The rate-stability guard bounds fit-window lag.  The 128 s discipline fit is
approximately centered 64 s in the past, so a rate change dr across an interval
T can bias the two-START slope by roughly 64/T * dr ppm.
"""
from __future__ import annotations

import argparse
import csv
import math
import statistics
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class StartHealth:
    path: Path
    device: str
    command_id: str
    captured_master_us: int
    master_epoch_us: int
    local_epoch_us: int
    master_minus_disciplined_us: int
    rtc_state: str
    rtc_rate_ppm_vs_rtc: float | None
    rtc_fit_points: int | None
    rtc_fit_rms_us: float | None
    fit_outliers: int | None
    accepted_edges: int | None
    inferred_missing_edges: int | None
    holdover_entries: int | None
    queue_drops: int | None
    temperature_valid: bool | None
    temperature_c: float | None
    sqw_core: int | None
    health_flags: int | None


def _optional_int(text: str | None) -> int | None:
    if text is None or text.strip() == "":
        return None
    return int(text)


def _optional_float(text: str | None) -> float | None:
    if text is None or text.strip() == "":
        return None
    value = float(text)
    if not math.isfinite(value):
        raise ValueError(f"non-finite numeric value {text!r}")
    return value


def _optional_bool(text: str | None) -> bool | None:
    if text is None or text.strip() == "":
        return None
    if text == "1":
        return True
    if text == "0":
        return False
    raise ValueError(f"expected 0/1, got {text!r}")


def read_start_rows(path: Path) -> dict[str, StartHealth]:
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        raise ValueError(f"{path}: no rows")

    required = {
        "RunCommandId",
        "DeviceId",
        "Phase",
        "StatusCaptured",
        "CapturedMasterUs",
        "RtcState",
        "SyncEpochMasterUs",
        "SyncEpochLocalUs",
        "SyncEpochMasterMinusDisciplinedUs",
        "RtcAcceptedEdges",
        "RtcInferredMissingEdges",
        "RtcHoldoverEntries",
    }
    missing = required - set(rows[0])
    if missing:
        raise ValueError(
            f"{path}: missing v6.22.8 qualification column(s): "
            f"{', '.join(sorted(missing))}"
        )

    result: dict[str, StartHealth] = {}
    for row in rows:
        if row.get("Phase", "").upper() != "START":
            continue
        if row.get("StatusCaptured") != "1":
            continue
        device = row.get("DeviceId", "").strip()
        if not device:
            continue
        if device in result:
            raise ValueError(f"{path}: duplicate captured START row for {device}")

        captured_master = _optional_int(row.get("CapturedMasterUs"))
        master_epoch = _optional_int(row.get("SyncEpochMasterUs"))
        local_epoch = _optional_int(row.get("SyncEpochLocalUs"))
        q = _optional_int(row.get("SyncEpochMasterMinusDisciplinedUs"))
        if None in (captured_master, master_epoch, local_epoch, q):
            raise ValueError(
                f"{path}: {device} START has incomplete synchronized disciplined-epoch telemetry"
            )

        result[device] = StartHealth(
            path=path,
            device=device,
            command_id=row.get("RunCommandId", ""),
            captured_master_us=int(captured_master),
            master_epoch_us=int(master_epoch),
            local_epoch_us=int(local_epoch),
            master_minus_disciplined_us=int(q),
            rtc_state=row.get("RtcState", "").upper(),
            rtc_rate_ppm_vs_rtc=_optional_float(row.get("RtcRatePpmVsRtc")),
            rtc_fit_points=_optional_int(row.get("RtcFitPoints")),
            rtc_fit_rms_us=_optional_float(row.get("RtcFitRmsUs")),
            fit_outliers=_optional_int(row.get("RtcFitOutliers")),
            accepted_edges=_optional_int(row.get("RtcAcceptedEdges")),
            inferred_missing_edges=_optional_int(row.get("RtcInferredMissingEdges")),
            holdover_entries=_optional_int(row.get("RtcHoldoverEntries")),
            queue_drops=_optional_int(row.get("RtcQueueDrops")),
            temperature_valid=_optional_bool(row.get("RtcTemperatureValid")),
            temperature_c=_optional_float(row.get("RtcTemperatureC")),
            sqw_core=_optional_int(row.get("RtcSqwCore")),
            health_flags=_optional_int(row.get("HealthFlags")),
        )

    if not result:
        raise ValueError(f"{path}: no captured START health rows")
    return result


def endpoint_health_ok(row: StartHealth) -> bool:
    return (
        row.rtc_state == "LOCKED"
        and row.rtc_fit_points is not None
        and row.rtc_fit_points >= 64
        and row.rtc_fit_rms_us is not None
        and row.rtc_fit_rms_us <= 3.0
        and row.queue_drops == 0
        and row.temperature_valid is True
        and row.sqw_core == 1
        and row.health_flags is not None
        and (row.health_flags & 0x01) != 0
        and row.accepted_edges is not None
        and row.inferred_missing_edges is not None
        and row.holdover_entries is not None
    )


def build_pair_row(
    a: StartHealth,
    b: StartHealth,
    *,
    min_interval_seconds: float,
    same_boot_tolerance_ppm: float,
    accepted_edge_tolerance: float,
    warm_confirmed: bool,
    max_rate_change_ppm: float = 0.25,
    fit_lag_seconds: float = 64.0,
) -> dict[str, object]:
    master_dt_us = b.master_epoch_us - a.master_epoch_us
    local_dt_us = b.local_epoch_us - a.local_epoch_us
    captured_dt_us = b.captured_master_us - a.captured_master_us
    dq_us = b.master_minus_disciplined_us - a.master_minus_disciplined_us

    interval_ok = master_dt_us >= int(min_interval_seconds * 1_000_000.0)
    monotonic_ok = master_dt_us > 0 and local_dt_us > 0 and captured_dt_us > 0

    if master_dt_us > 0:
        local_master_mismatch_ppm = (local_dt_us - master_dt_us) * 1_000_000.0 / master_dt_us
    else:
        local_master_mismatch_ppm = math.nan
    same_boot_ok = monotonic_ok and abs(local_master_mismatch_ppm) <= same_boot_tolerance_ppm

    counters_present = all(
        v is not None
        for v in (
            a.accepted_edges,
            b.accepted_edges,
            a.inferred_missing_edges,
            b.inferred_missing_edges,
            a.holdover_entries,
            b.holdover_entries,
        )
    )

    accepted_delta: int | None = None
    inferred_delta: int | None = None
    holdover_delta: int | None = None
    accepted_error_edges = math.nan
    accepted_count_ok = False
    no_inferred_missing_ok = False
    no_holdover_ok = False

    if counters_present:
        accepted_delta = int(b.accepted_edges) - int(a.accepted_edges)
        inferred_delta = int(b.inferred_missing_edges) - int(a.inferred_missing_edges)
        holdover_delta = int(b.holdover_entries) - int(a.holdover_entries)
        expected_edges = captured_dt_us / 1_000_000.0 if captured_dt_us > 0 else math.nan
        accepted_error_edges = accepted_delta - expected_edges if math.isfinite(expected_edges) else math.nan
        accepted_count_ok = (
            accepted_delta >= 0
            and math.isfinite(accepted_error_edges)
            and abs(accepted_error_edges) <= accepted_edge_tolerance
        )
        no_inferred_missing_ok = inferred_delta == 0
        no_holdover_ok = holdover_delta == 0

    first_health_ok = endpoint_health_ok(a)
    second_health_ok = endpoint_health_ok(b)
    continuity_ok = accepted_count_ok and no_inferred_missing_ok and no_holdover_ok

    rate_delta_ppm = math.nan
    rate_stability_ok = False
    estimated_fit_lag_phase_bias_us = math.nan
    estimated_fit_lag_slope_bias_ppm = math.nan
    if (
        a.rtc_rate_ppm_vs_rtc is not None
        and b.rtc_rate_ppm_vs_rtc is not None
        and math.isfinite(a.rtc_rate_ppm_vs_rtc)
        and math.isfinite(b.rtc_rate_ppm_vs_rtc)
    ):
        rate_delta_ppm = b.rtc_rate_ppm_vs_rtc - a.rtc_rate_ppm_vs_rtc
        rate_stability_ok = abs(rate_delta_ppm) <= max_rate_change_ppm
        # ppm * seconds = microseconds of phase.  Dividing that phase by the
        # qualification interval gives the approximate slope bias in ppm.
        estimated_fit_lag_phase_bias_us = fit_lag_seconds * rate_delta_ppm
        interval_seconds = master_dt_us / 1_000_000.0 if master_dt_us > 0 else math.nan
        if math.isfinite(interval_seconds) and interval_seconds > 0:
            estimated_fit_lag_slope_bias_ppm = (
                estimated_fit_lag_phase_bias_us / interval_seconds
            )

    rate_computable = master_dt_us > 0
    q_slope_ppm = dq_us * 1_000_000.0 / master_dt_us if rate_computable else math.nan
    device_vs_master_ppm = -q_slope_ppm if rate_computable else math.nan

    valid = (
        interval_ok
        and same_boot_ok
        and first_health_ok
        and second_health_ok
        and continuity_ok
        and rate_stability_ok
        and warm_confirmed
    )

    return {
        "DeviceId": a.device,
        "FirstRunCommandId": a.command_id,
        "SecondRunCommandId": b.command_id,
        "FirstSyncEpochMasterUs": a.master_epoch_us,
        "SecondSyncEpochMasterUs": b.master_epoch_us,
        "IntervalSeconds": master_dt_us / 1_000_000.0 if master_dt_us > 0 else math.nan,
        "FirstSyncEpochLocalUs": a.local_epoch_us,
        "SecondSyncEpochLocalUs": b.local_epoch_us,
        "LocalIntervalSeconds": local_dt_us / 1_000_000.0 if local_dt_us > 0 else math.nan,
        "LocalVsMasterIntervalMismatchPpm": local_master_mismatch_ppm,
        "SameBootOK": same_boot_ok,
        "FirstCapturedMasterUs": a.captured_master_us,
        "SecondCapturedMasterUs": b.captured_master_us,
        "CounterIntervalSeconds": captured_dt_us / 1_000_000.0 if captured_dt_us > 0 else math.nan,
        "FirstAcceptedEdges": a.accepted_edges,
        "SecondAcceptedEdges": b.accepted_edges,
        "AcceptedEdgesDelta": accepted_delta,
        "AcceptedEdgeError": accepted_error_edges,
        "AcceptedCountOK": accepted_count_ok,
        "FirstInferredMissingEdges": a.inferred_missing_edges,
        "SecondInferredMissingEdges": b.inferred_missing_edges,
        "InferredMissingDelta": inferred_delta,
        "NoInferredMissingOK": no_inferred_missing_ok,
        "FirstHoldoverEntries": a.holdover_entries,
        "SecondHoldoverEntries": b.holdover_entries,
        "HoldoverEntriesDelta": holdover_delta,
        "NoHoldoverOK": no_holdover_ok,
        "FirstMasterMinusDisciplinedUs": a.master_minus_disciplined_us,
        "SecondMasterMinusDisciplinedUs": b.master_minus_disciplined_us,
        "DeltaMasterMinusDisciplinedUs": dq_us,
        "MasterMinusDisciplinedSlopePpm": q_slope_ppm,
        "DisciplinedMinusMasterPpm": device_vs_master_ppm,
        "FirstHealthOK": first_health_ok,
        "SecondHealthOK": second_health_ok,
        "WarmConfirmed": warm_confirmed,
        "ContinuityOK": continuity_ok,
        "RateStabilityOK": rate_stability_ok,
        "RtcRateDeltaPpm": rate_delta_ppm,
        "MaxRateChangePpm": max_rate_change_ppm,
        "FitLagSeconds": fit_lag_seconds,
        "EstimatedFitLagPhaseBiasUs": estimated_fit_lag_phase_bias_us,
        "EstimatedFitLagSlopeBiasPpm": estimated_fit_lag_slope_bias_ppm,
        "IntervalOK": interval_ok,
        "QualificationValid": valid,
        "FirstRtcRatePpmVsRtc": a.rtc_rate_ppm_vs_rtc,
        "SecondRtcRatePpmVsRtc": b.rtc_rate_ppm_vs_rtc,
        "FirstRtcFitPoints": a.rtc_fit_points,
        "SecondRtcFitPoints": b.rtc_fit_points,
        "FirstRtcFitRmsUs": a.rtc_fit_rms_us,
        "SecondRtcFitRmsUs": b.rtc_fit_rms_us,
        "FirstRtcFitOutliers": a.fit_outliers,
        "SecondRtcFitOutliers": b.fit_outliers,
        "FirstRtcQueueDrops": a.queue_drops,
        "SecondRtcQueueDrops": b.queue_drops,
        "FirstRtcTemperatureC": a.temperature_c,
        "SecondRtcTemperatureC": b.temperature_c,
        "FirstRtcSqwCore": a.sqw_core,
        "SecondRtcSqwCore": b.sqw_core,
    }


def classify_module_acceptance(delta_ppm: float, accept_delta_ppm: float, reject_delta_ppm: float) -> str:
    if not math.isfinite(delta_ppm):
        return "INVALID"
    abs_delta = abs(delta_ppm)
    if abs_delta <= accept_delta_ppm:
        return "ACCEPT"
    if abs_delta <= reject_delta_ppm:
        return "INSPECT"
    return "REJECT"


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Measure DS3231-disciplined fleet rate from two warm controller health CSVs."
    )
    parser.add_argument("first_health_csv", type=Path)
    parser.add_argument("second_health_csv", type=Path)
    parser.add_argument(
        "--warm-confirmed",
        action="store_true",
        help="Confirm both STARTs were taken after the devices were thermally warm/stable. Required for a valid qualification result.",
    )
    parser.add_argument(
        "--min-interval-seconds",
        type=float,
        default=1200.0,
        help="Minimum master interval for qualification (default: 1200 s; 1800 s recommended).",
    )
    parser.add_argument(
        "--same-boot-tolerance-ppm",
        type=float,
        default=100.0,
        help="Maximum |raw-local interval - master interval| mismatch for same-boot validation (default: 100 ppm).",
    )
    parser.add_argument(
        "--accepted-edge-tolerance",
        type=float,
        default=2.0,
        help="Maximum accepted-edge count error versus the health-capture interval (default: 2 edges).",
    )
    parser.add_argument(
        "--max-rate-change-ppm",
        type=float,
        default=0.25,
        help="Maximum |RtcRatePpmVsRtc(second)-RtcRatePpmVsRtc(first)| for the automatic warm/rate-stability guard (default: 0.25 ppm).",
    )
    parser.add_argument(
        "--fit-lag-seconds",
        type=float,
        default=64.0,
        help="Effective center lag of the RTC discipline fit used only to report estimated qualification bias (default: 64 s for a 128 s fit).",
    )
    parser.add_argument(
        "--accept-delta-ppm",
        type=float,
        default=1.0,
        help="Nominal module acceptance band around the valid-batch median (default: ±1.0 ppm).",
    )
    parser.add_argument(
        "--reject-delta-ppm",
        type=float,
        default=2.0,
        help="Reject modules beyond this absolute delta from the valid-batch median; values between accept and reject are INSPECT (default: ±2.0 ppm).",
    )
    parser.add_argument(
        "--warn-delta-ppm",
        type=float,
        default=None,
        help="Legacy optional warning threshold retained for compatibility; ModuleAcceptance remains governed by --accept-delta-ppm/--reject-delta-ppm.",
    )
    parser.add_argument("--output", type=Path, help="Optional output CSV path.")
    args = parser.parse_args()

    if args.min_interval_seconds <= 0:
        parser.error("--min-interval-seconds must be > 0")
    if args.same_boot_tolerance_ppm <= 0:
        parser.error("--same-boot-tolerance-ppm must be > 0")
    if args.accepted_edge_tolerance < 0:
        parser.error("--accepted-edge-tolerance must be >= 0")
    if args.max_rate_change_ppm <= 0:
        parser.error("--max-rate-change-ppm must be > 0")
    if args.fit_lag_seconds < 0:
        parser.error("--fit-lag-seconds must be >= 0")
    if args.accept_delta_ppm <= 0:
        parser.error("--accept-delta-ppm must be > 0")
    if args.reject_delta_ppm <= args.accept_delta_ppm:
        parser.error("--reject-delta-ppm must be greater than --accept-delta-ppm")
    if args.warn_delta_ppm is not None and args.warn_delta_ppm <= 0:
        parser.error("--warn-delta-ppm must be > 0")

    first = read_start_rows(args.first_health_csv)
    second = read_start_rows(args.second_health_csv)
    first_devices = set(first)
    second_devices = set(second)
    common = sorted(first_devices & second_devices)
    if not common:
        raise SystemExit("No device has a captured START row in both files")
    if first_devices != second_devices:
        only_first = ", ".join(sorted(first_devices - second_devices)) or "none"
        only_second = ", ".join(sorted(second_devices - first_devices)) or "none"
        raise SystemExit(
            "Qualification files do not contain the same captured START device set: "
            f"only first=[{only_first}], only second=[{only_second}]"
        )

    calculations = [
        build_pair_row(
            first[device],
            second[device],
            min_interval_seconds=args.min_interval_seconds,
            same_boot_tolerance_ppm=args.same_boot_tolerance_ppm,
            accepted_edge_tolerance=args.accepted_edge_tolerance,
            warm_confirmed=args.warm_confirmed,
            max_rate_change_ppm=args.max_rate_change_ppm,
            fit_lag_seconds=args.fit_lag_seconds,
        )
        for device in common
    ]

    valid_rates = [
        float(row["DisciplinedMinusMasterPpm"])
        for row in calculations
        if bool(row["QualificationValid"])
    ]
    median_rate = statistics.median(valid_rates) if valid_rates else math.nan

    for row in calculations:
        if bool(row["QualificationValid"]) and math.isfinite(median_rate):
            delta = float(row["DisciplinedMinusMasterPpm"]) - median_rate
            row["BatchMedianDisciplinedMinusMasterPpm"] = median_rate
            row["DeltaFromBatchMedianPpm"] = delta
            abs_delta = abs(delta)
            row["ModuleAcceptance"] = classify_module_acceptance(
                delta, args.accept_delta_ppm, args.reject_delta_ppm
            )
            row["AcceptDeltaPpm"] = args.accept_delta_ppm
            row["RejectDeltaPpm"] = args.reject_delta_ppm
            row["RateWarning"] = (
                args.warn_delta_ppm is not None and abs_delta > args.warn_delta_ppm
            )
        else:
            row["BatchMedianDisciplinedMinusMasterPpm"] = ""
            row["DeltaFromBatchMedianPpm"] = ""
            row["ModuleAcceptance"] = "INVALID"
            row["AcceptDeltaPpm"] = args.accept_delta_ppm
            row["RejectDeltaPpm"] = args.reject_delta_ppm
            row["RateWarning"] = False

    print(
        "Device   interval_s   D-master_ppm   delta_median   dRate_ppm   lagBias_ppm   boot   SQW   rate   endpoints   warm   validity   acceptance"
    )
    for row in calculations:
        dmaster = float(row["DisciplinedMinusMasterPpm"])
        delta = row["DeltaFromBatchMedianPpm"]
        delta_text = f"{float(delta):+10.6f}" if delta != "" else "       n/a"
        endpoint_text = (
            "OK" if row["FirstHealthOK"] and row["SecondHealthOK"] else "BAD"
        )
        validity = "PASS" if row["QualificationValid"] else "INVALID"
        acceptance = str(row["ModuleAcceptance"])
        print(
            f"{row['DeviceId']:<8}{float(row['IntervalSeconds']):>11.1f}   "
            f"{dmaster:>+12.6f}   {delta_text}   "
            f"{float(row['RtcRateDeltaPpm']):>+9.4f}   "
            f"{float(row['EstimatedFitLagSlopeBiasPpm']):>+11.6f}   "
            f"{'OK' if row['SameBootOK'] else 'BAD':<4}   "
            f"{'OK' if row['ContinuityOK'] else 'BAD':<3}   "
            f"{'OK' if row['RateStabilityOK'] else 'BAD':<4}   "
            f"{endpoint_text:<9}   "
            f"{'YES' if row['WarmConfirmed'] else 'NO':<4}   {validity:<8}   {acceptance}"
        )
        if not row["QualificationValid"]:
            reasons=[]
            if not row["IntervalOK"]: reasons.append("interval too short")
            if not row["SameBootOK"]: reasons.append(
                f"same-boot mismatch {float(row['LocalVsMasterIntervalMismatchPpm']):+.1f} ppm"
            )
            if not row["AcceptedCountOK"]: reasons.append(
                f"accepted-edge error {float(row['AcceptedEdgeError']):+.2f}"
            )
            if not row["NoInferredMissingOK"]: reasons.append(
                f"inferred-missing delta {row['InferredMissingDelta']}"
            )
            if not row["NoHoldoverOK"]: reasons.append(
                f"holdover-entry delta {row['HoldoverEntriesDelta']}"
            )
            if not row["RateStabilityOK"]:
                if math.isfinite(float(row["RtcRateDeltaPpm"])):
                    reasons.append(
                        f"rate changed {float(row['RtcRateDeltaPpm']):+.3f} ppm "
                        f"(limit {float(row['MaxRateChangePpm']):.3f} ppm; "
                        f"estimated lag bias {float(row['EstimatedFitLagSlopeBiasPpm']):+.4f} ppm)"
                    )
                else:
                    reasons.append("RTC rate telemetry missing")
            if not row["FirstHealthOK"] or not row["SecondHealthOK"]:
                reasons.append("endpoint health/LOCKED guard failed")
            if not row["WarmConfirmed"]:
                reasons.append("warm state not confirmed")
            print("  INVALID: " + "; ".join(reasons))

    if math.isfinite(median_rate):
        print(f"BATCH_MEDIAN_DISCIPLINED_MINUS_MASTER_PPM={median_rate:+.6f}")
    else:
        print("BATCH_MEDIAN_DISCIPLINED_MINUS_MASTER_PPM=UNAVAILABLE")
    print(
        "NOTE: qualification validity and module acceptance are separate. "
        "Absolute D-master ppm includes controller/master clock rate; DeltaFromBatchMedianPpm removes that common-mode term, "
        "and only valid pairs contribute to the median. Default module disposition is ACCEPT within ±1 ppm, INSPECT from >1 to ±2 ppm, REJECT beyond ±2 ppm."
    )

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        fieldnames = list(calculations[0].keys())
        with args.output.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=fieldnames)
            writer.writeheader()
            writer.writerows(calculations)
        print(f"WROTE={args.output}")

    any_invalid = any(not bool(row["QualificationValid"]) for row in calculations)
    any_reject = any(row["ModuleAcceptance"] == "REJECT" for row in calculations)
    any_inspect = any(row["ModuleAcceptance"] == "INSPECT" for row in calculations)
    any_rate_warning = any(bool(row["RateWarning"]) for row in calculations)
    if any_invalid or any_reject:
        return 2
    if any_inspect or any_rate_warning:
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
