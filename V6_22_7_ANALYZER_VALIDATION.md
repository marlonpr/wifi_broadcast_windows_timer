# v6.22.7 — Analyzer-vs-qualification validation

Use warm ESP01/ESP02 with Analyzer_v8 attached. Start a 30:00 countdown for START #1, keep Analyzer_v8 running through all 1801 COMMIT boundaries, and take START #2 immediately after the run.

Run:

```powershell
python tools\qualify_rtc_batch.py health_start1.csv health_start2.csv --warm-confirmed --output rtc_pair.csv
python tools\validate_rtc_qualification_vs_analyzer.py health_start1.csv health_start2.csv Analyzer_v8.log --device-a ESP01 --device-b ESP02 --warm-confirmed --tolerance-ppm 0.05
```

The comparison is valid only if both devices pass the automatic endpoint-rate stability guard (`|DeltaRatePpm| <= 0.25 ppm`) in addition to the existing same-boot, SQW continuity, endpoint-health, and procedural warm guards.

Expected physical comparison:

```text
Analyzer slope: ESP02_COMMIT - ESP01_COMMIT
Tool slope:     DMM(ESP01) - DMM(ESP02)
|difference| <= 0.05 ppm
```
