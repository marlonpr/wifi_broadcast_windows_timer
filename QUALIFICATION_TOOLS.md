# Qualification tools

## START #1 warm-up gate — v6.22.8

```powershell
python tools\rtc_warmup_gate.py ESP01.log ESP02.log
```

Default criterion over the latest 600 s monotonic-boot window:

- >=30 samples and >=95% window coverage;
- all samples `LOCKED`, 129 fit points, fit RMS <=3 us;
- zero fit outliers, inferred-missing edges, holdover entries, and queue drops;
- valid RTC temperature and SQW core 1;
- `|OLS rate trend| <= 0.005 ppm/min`;
- `|last rate - first rate| <= 0.05 ppm`;
- RTC package-temperature span <=0.25 C.

Do not take START #1 until:

```text
QUALIFICATION_START1_GATE=READY
```

## Two-START batch qualification

```powershell
python tools\qualify_rtc_batch.py health_start1.csv health_start2.csv `
  --warm-confirmed `
  --output rtc_batch.csv
```

Measurement validity and module acceptance are separate. A valid measurement must
still pass the same-boot, SQW continuity, endpoint-health, and <=0.25 ppm
START1->START2 rate-stability guards. Valid modules are then classified against the
valid-batch median:

```text
<= ±1.0 ppm         ACCEPT
> ±1.0 to ±2.0 ppm INSPECT
> ±2.0 ppm         REJECT
```

The acceptance thresholds can be changed with `--accept-delta-ppm` and
`--reject-delta-ppm`.

## Analyzer cross-check

```powershell
python tools\validate_rtc_qualification_vs_analyzer.py `
  health_start1.csv health_start2.csv Analyzer_v8.log `
  --device-a ESP01 --device-b ESP02 `
  --warm-confirmed --tolerance-ppm 0.05
```

The v6.22.8 output label `TOOL_MINUS_ANALYZER_ERROR_PPM` is now sign-consistent:
`tool_expected_pair_slope - analyzer_pair_slope`.

## 30-minute capture analysis

```powershell
python tools\analyze_30min_qualification.py analyzer.txt --controller-csv "C:\Users\...\Documents\FactoryTimer\TimingQualification\controller_tx_....csv" --events-csv qualification_events.csv
```

The analyzer uses proximity pairing (±100 ms), excludes START from the rate fit,
robustly fits the pairwise ESP01−ESP02 gap, uses a centered ±30-boundary running
median for per-device presentation residuals, reports common late boundaries, and
correlates late boundaries with application TX lead.

### Device-side control-port arrival correlation

Qualification firmware can emit deferred `RX_NEAR_BOUNDARY` records when a packet
accepted on UDP port 5000 arrives within 15 ms before a running countdown boundary.
Pass one or more serial logs with:

```powershell
python tools\analyze_30min_qualification.py analyzer.txt `
  --controller-csv controller_tx.csv `
  --device-log ESP01.log `
  --device-log ESP02.log
```

This trace is intentionally application-level: it does not claim visibility into
Wi-Fi management traffic, mDNS/SSDP on other UDP ports, or AP airtime. For the new
v6.23.5 diagnostic build, correlate rare late commits with `FLASH_GUARD_EVENT`
records as well.
