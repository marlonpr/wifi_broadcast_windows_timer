# Qualification tools

## Warm-up gate

```powershell
python tools\rtc_warmup_gate.py ESP01.log ESP02.log
```

Default criterion: over the latest 180 s, each device must have at least 8 RTC_QUAL points, all LOCKED, zero queue drops, and `|d(rate_ppm)/dt| < 0.01 ppm/min`.

## 30-minute capture analysis

```powershell
python tools\analyze_30min_qualification.py analyzer.txt --controller-csv "C:\Users\...\Documents\FactoryTimer\TimingQualification\controller_tx_....csv" --events-csv qualification_events.csv
```

The analyzer uses proximity pairing (±100 ms), excludes START from the rate fit, robustly fits the pairwise ESP01−ESP02 gap, uses a centered ±30-boundary running median for per-device presentation residuals, reports common late boundaries, and correlates late boundaries with application TX lead.

### Device-side control-port arrival correlation

Current qualification firmware can emit deferred `RX_NEAR_BOUNDARY` records when a packet accepted on UDP port 5000 arrives within 15 ms before a running countdown boundary. Pass one or more serial logs with:

```powershell
python tools\analyze_30min_qualification.py analyzer.txt `
  --controller-csv controller_tx.csv `
  --device-log ESP01.log `
  --device-log ESP02.log
```

The analyzer reports late commits that also had a device-side control-port arrival in the same boundary window. This trace is intentionally application-level: it does not claim visibility into Wi-Fi management traffic, mDNS/SSDP on other UDP ports, or AP airtime.
